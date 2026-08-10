namespace WaveLab.Audio.Dsp;

/// <summary>Analysis-first, non-destructive restoration operations.</summary>
public static partial class Restoration
{
    private const int DefaultLegacySampleRate = 44100;

    /// <summary>
    /// Analyse deinterleaved audio for isolated clicks and short pops. Detection uses a
    /// robust local curvature floor and rejects candidates that behave like sustained
    /// musical attacks. No source samples are changed.
    /// </summary>
    public static ClickAnalysisResult AnalyzeClicks(IReadOnlyList<float[]> channels, int sampleRate,
        ClickAnalysisOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        int sampleCount = ValidateRestorationChannels(channels, sampleRate);
        options ??= new ClickAnalysisOptions();
        if (channels.Count == 0 || sampleCount < 7)
        {
            progress?.Report(new RestorationProgress(RestorationStage.AnalyzingClicks,
                1.0, Math.Max(0, channels.Count - 1), channels.Count));
            return new ClickAnalysisResult(Array.Empty<ClickEvent>(), sampleCount, channels.Count, sampleRate);
        }

        double sensitivity = Math.Clamp(options.Sensitivity, 1.0, 10.0);
        double sensitivityT = (sensitivity - 1.0) / 9.0;
        double sigmaMultiplier = 15.0 - 9.5 * sensitivityT;
        double relativeFloor = 0.008 - 0.0065 * sensitivityT;
        double minimumConfidence = Math.Clamp(options.MinimumConfidence, 0.0, 1.0);
        double maximumClickLengthMs = Math.Clamp(options.MaximumClickLengthMs, 0.05, 2.0);
        int maximumClickSamples = Math.Max(1,
            (int)Math.Round(sampleRate * maximumClickLengthMs / 1000.0));
        int maximumPopSamples = Math.Max(maximumClickSamples,
            (int)Math.Round(sampleRate * Math.Clamp(options.MaximumPopLengthMs,
                maximumClickLengthMs, 10.0) / 1000.0));
        // A short vinyl pop often has strong curvature only at its leading and
        // trailing discontinuities, with a comparatively flat or decaying interior.
        // Search the complete repairable-pop span for the matching edge; the
        // return-to-trend and attack checks below still reject sustained music.
        int maximumCandidateSpanSamples = maximumPopSamples;
        int postCandidateSkipSamples = Math.Clamp((int)Math.Round(sampleRate * 0.00018), 1,
            Math.Max(1, maximumPopSamples / 3));
        int blockSize = Math.Clamp((int)Math.Round(sampleRate * 0.025), 256, 4096);

        var events = new List<ClickEvent>();
        var curvature = new float[sampleCount];
        var scratch = new float[blockSize + blockSize / 2 + 8];
        const double minimumProgressFraction = 0.0025; // at most about 400 percentage reports
        const long maximumProgressSilenceMs = 250;    // but remain responsive if analysis is slow
        double lastReportedFraction = 0;
        long lastProgressTimestamp = Environment.TickCount64;
        progress?.Report(new RestorationProgress(RestorationStage.AnalyzingClicks,
            0.0, 0, channels.Count));

        for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = channels[channelIndex];
            Array.Clear(curvature);
            for (int i = 1; i < sampleCount - 1; i++)
            {
                // Half the second difference is the error from a one-sample linear predictor.
                curvature[i] = Math.Abs(samples[i] - 0.5f * (samples[i - 1] + samples[i + 1]));
            }

            int nextCandidateSample = 2;
            for (int blockStart = 2; blockStart < sampleCount - 2; blockStart += blockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int blockEnd = Math.Min(sampleCount - 2, blockStart + blockSize);
                int contextStart = Math.Max(1, blockStart - blockSize / 4);
                int contextEnd = Math.Min(sampleCount - 1, blockEnd + blockSize / 4);
                int contextLength = contextEnd - contextStart;

                EnsureScratchCapacity(ref scratch, contextLength);
                Array.Copy(curvature, contextStart, scratch, 0, contextLength);
                float median = MedianInPlace(scratch, contextLength);
                for (int scratchIndex = 0; scratchIndex < contextLength; scratchIndex++)
                    scratch[scratchIndex] = Math.Abs(curvature[contextStart + scratchIndex] - median);
                float mad = MedianInPlace(scratch, contextLength);

                double signalSquareSum = 0;
                for (int signalIndex = contextStart; signalIndex < contextEnd; signalIndex++)
                    signalSquareSum += samples[signalIndex] * (double)samples[signalIndex];
                double localRms = Math.Sqrt(signalSquareSum / Math.Max(1, contextLength));
                double robustScale = Math.Max(1.4826 * mad,
                    Math.Max(median * 0.25, Math.Max(2e-7, localRms * relativeFloor)));
                float threshold = (float)(median + sigmaMultiplier * robustScale);

                int i = Math.Max(blockStart, nextCandidateSample);
                while (i < blockEnd)
                {
                    if (curvature[i] <= threshold)
                    {
                        i++;
                        continue;
                    }

                    int start = i;
                    int lastStrong = i;
                    int peak = i;
                    float peakCurvature = curvature[i];
                    int scanLimit = Math.Min(sampleCount - 2, i + maximumCandidateSpanSamples);
                    for (int j = i + 1; j < scanLimit; j++)
                    {
                        if (curvature[j] <= threshold) continue;
                        lastStrong = j;
                        if (curvature[j] > peakCurvature)
                        {
                            peakCurvature = curvature[j];
                            peak = j;
                        }
                    }

                    int end = lastStrong + 1;
                    float expansionThreshold = threshold * 0.45f;
                    while (start > 2 && end - (start - 1) <= maximumPopSamples &&
                           curvature[start - 1] > expansionThreshold)
                        start--;
                    while (end < sampleCount - 2 && end + 1 - start <= maximumPopSamples &&
                           curvature[end] > expansionThreshold)
                        end++;

                    if (end - start <= maximumPopSamples &&
                        TryCreateClickEvent(samples, curvature, channelIndex, start, end, peak,
                            peakCurvature, threshold, robustScale, localRms, sampleRate,
                            maximumClickSamples, options.PreserveTransients, out var clickEvent) &&
                        clickEvent.Confidence >= minimumConfidence)
                    {
                        AddOrMergeClickEvent(events, clickEvent, maximumClickSamples);
                    }

                    nextCandidateSample = Math.Max(nextCandidateSample, end + postCandidateSkipSamples);
                    i = Math.Max(i + 1, nextCandidateSample);
                }

                double fraction = (channelIndex + (double)blockEnd / sampleCount) / channels.Count;
                long progressTimestamp = Environment.TickCount64;
                if (progress != null &&
                    (fraction - lastReportedFraction >= minimumProgressFraction ||
                     progressTimestamp - lastProgressTimestamp >= maximumProgressSilenceMs))
                {
                    lastReportedFraction = fraction;
                    lastProgressTimestamp = progressTimestamp;
                    progress.Report(new RestorationProgress(RestorationStage.AnalyzingClicks,
                        Math.Clamp(fraction, 0.0, 1.0), channelIndex, channels.Count,
                        events.Count, events.Count));
                }
            }
            nextCandidateSample = 2;
        }

        events.Sort(static (a, b) =>
        {
            int byChannel = a.Channel.CompareTo(b.Channel);
            return byChannel != 0 ? byChannel : a.StartSample.CompareTo(b.StartSample);
        });
        progress?.Report(new RestorationProgress(RestorationStage.AnalyzingClicks,
            1.0, channels.Count - 1, channels.Count, events.Count, events.Count));
        return new ClickAnalysisResult(events.AsReadOnly(), sampleCount, channels.Count, sampleRate);
    }

    /// <summary>
    /// Analyse and repair clicks into new channel buffers. This is the preferred path for
    /// preview and undo/redo workflows because the source arrays remain unchanged.
    /// </summary>
    public static ClickRestorationResult RestoreClicks(IReadOnlyList<float[]> channels, int sampleRate,
        ClickAnalysisOptions? analysisOptions = null,
        ClickRepairOptions? repairOptions = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        var analysis = AnalyzeClicks(channels, sampleRate, analysisOptions, cancellationToken, progress);
        var audio = RepairClicks(channels, analysis.Events, repairOptions, cancellationToken, progress);
        return new ClickRestorationResult(audio, analysis);
    }

    /// <summary>Repair previously analysed click events into new channel buffers.</summary>
    public static float[][] RepairClicks(IReadOnlyList<float[]> channels,
        IReadOnlyList<ClickEvent> events,
        ClickRepairOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ValidateRestorationChannels(channels);
        var result = RestorationPreview.Clone(channels);
        RepairClicksInPlace(result, events, options, cancellationToken, progress);
        return result;
    }

    /// <summary>
    /// Repair analysed events in place. Prefer <see cref="RepairClicks"/> unless the caller
    /// already owns a disposable working copy.
    /// </summary>
    public static int RepairClicksInPlace(float[][] data, IReadOnlyList<ClickEvent> events,
        ClickRepairOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(events);
        ValidateRestorationChannels(data);
        options ??= new ClickRepairOptions();
        float strength = (float)Math.Clamp(options.Strength, 0.0, 1.0);
        double maximumOvershoot = Math.Clamp(options.MaximumOvershoot, 1.0, 4.0);
        if (strength <= 0f || events.Count == 0) return 0;

        // The same physical groove defect normally reaches both stereo channels, but
        // their different programme material can make the detector choose slightly
        // different leading/trailing edges or miss the quieter side entirely. Link
        // temporally coincident detections and use their union for every channel. Each
        // channel is still reconstructed from its own clean waveform context.
        int sampleCount = data.Length == 0 ? 0 : data[0].Length;
        var ordered = CreateClickRepairPlan(events, data.Length, sampleCount,
            options.LinkChannels);
        int repaired = 0;
        int previousChannel = -1;
        int previousEnd = -1;
        for (int eventIndex = 0; eventIndex < ordered.Length; eventIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var defect = ordered[eventIndex];
            if (defect.Channel < 0 || defect.Channel >= data.Length) continue;
            var samples = data[defect.Channel];
            if (defect.Channel != previousChannel)
            {
                previousChannel = defect.Channel;
                previousEnd = -1;
            }

            int start = Math.Max(defect.StartSample, previousEnd);
            int end = defect.EndSample;
            if (start < 1 || end >= samples.Length || start >= end) continue;

            InterpolateImpulse(samples, start, end, strength, maximumOvershoot);
            previousEnd = end;
            repaired++;
            if ((eventIndex & 31) == 0 || eventIndex == ordered.Length - 1)
            {
                progress?.Report(new RestorationProgress(RestorationStage.RepairingClicks,
                    (double)(eventIndex + 1) / ordered.Length, defect.Channel, data.Length,
                    eventIndex + 1, ordered.Length));
            }
        }
        return repaired;
    }

    private static ClickEvent[] CreateClickRepairPlan(
        IReadOnlyList<ClickEvent> events, int channelCount, int sampleCount,
        bool linkChannels)
    {
        const int alignmentToleranceSamples = 2;
        var chronological = events
            .Where(item => item.Channel >= 0 && item.Channel < channelCount &&
                           item.StartSample >= 1 && item.StartSample < item.EndSample &&
                           item.EndSample < sampleCount)
            .OrderBy(item => item.StartSample)
            .ThenBy(item => item.EndSample)
            .ToArray();
        if (chronological.Length == 0) return chronological;

        var linked = new List<ClickEvent>(chronological.Length);
        int clusterIndex = 0;
        while (clusterIndex < chronological.Length)
        {
            int clusterStart = chronological[clusterIndex].StartSample;
            int clusterEnd = chronological[clusterIndex].EndSample;
            int clusterLimit = clusterIndex + 1;
            while (clusterLimit < chronological.Length &&
                   chronological[clusterLimit].StartSample <=
                       clusterEnd + alignmentToleranceSamples)
            {
                clusterEnd = Math.Max(clusterEnd,
                    chronological[clusterLimit].EndSample);
                clusterLimit++;
            }

            // Include a small clean-context guard on both sides. Clicks are frequently
            // bipolar: the louder polarity establishes the detected event while a
            // quieter opposite-polarity lobe sits just beyond that boundary. Without a
            // guard, interpolation can remove only half of the visible/audible spike.
            int clusterLength = clusterEnd - clusterStart;
            int boundaryGuard = Math.Clamp(
                (int)Math.Ceiling(clusterLength * 0.25), 8, 32);
            clusterStart = Math.Max(1, clusterStart - boundaryGuard);
            clusterEnd = Math.Min(sampleCount - 1, clusterEnd + boundaryGuard);

            var strongestByChannel = new Dictionary<int, ClickEvent>();
            for (int index = clusterIndex; index < clusterLimit; index++)
            {
                var candidate = chronological[index];
                if (!strongestByChannel.TryGetValue(candidate.Channel, out var strongest) ||
                    candidate.Severity > strongest.Severity)
                {
                    strongestByChannel[candidate.Channel] = candidate;
                }
            }

            if (linkChannels)
            {
                ClickEvent source = strongestByChannel.Values
                    .MaxBy(item => item.Severity);
                for (int channel = 0; channel < channelCount; channel++)
                {
                    ClickEvent channelSource = strongestByChannel.TryGetValue(channel,
                        out var detected) ? detected : source;
                    linked.Add(channelSource with
                    {
                        Channel = channel,
                        StartSample = clusterStart,
                        EndSample = clusterEnd,
                    });
                }
            }
            else
            {
                foreach (var pair in strongestByChannel)
                {
                    var source = pair.Value;
                    linked.Add(source with
                    {
                        StartSample = clusterStart,
                        EndSample = clusterEnd,
                    });
                }
            }
            clusterIndex = clusterLimit;
        }

        var ordered = linked.OrderBy(item => item.Channel)
            .ThenBy(item => item.StartSample)
            .ToArray();
        if (ordered.Length < 2) return ordered;

        // Expanded guards from nearby impulses can overlap. Reconstruct their union in
        // one pass so two independently predicted windows cannot introduce a seam.
        var merged = new List<ClickEvent>(ordered.Length) { ordered[0] };
        for (int index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            var previous = merged[^1];
            if (next.Channel != previous.Channel ||
                next.StartSample > previous.EndSample)
            {
                merged.Add(next);
                continue;
            }

            bool useNext = next.Severity > previous.Severity;
            ClickEvent source = useNext ? next : previous;
            merged[^1] = source with
            {
                Channel = previous.Channel,
                StartSample = Math.Min(previous.StartSample, next.StartSample),
                EndSample = Math.Max(previous.EndSample, next.EndSample),
            };
        }
        return merged.ToArray();
    }

    /// <summary>
    /// In-place compatibility overload with explicit sample rate, strength, cancellation,
    /// and progress. Returns the number of repaired channel events.
    /// </summary>
    public static int RemoveClicks(float[][] data, int sampleRate, double sensitivity,
        double strength = 1.0,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        var analysis = AnalyzeClicks(data, sampleRate,
            new ClickAnalysisOptions { Sensitivity = sensitivity }, cancellationToken, progress);
        return RepairClicksInPlace(data, analysis.Events,
            new ClickRepairOptions { Strength = strength }, cancellationToken, progress);
    }

    /// <summary>
    /// Analyse hard clipping plateaus, or threshold crossings when a manual threshold is
    /// supplied. Automatic mode is conservative and requires flat-top evidence.
    /// </summary>
    public static ClippingAnalysisResult AnalyzeClipping(IReadOnlyList<float[]> channels,
        int sampleRate,
        ClippingAnalysisOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        int sampleCount = ValidateRestorationChannels(channels, sampleRate);
        options ??= new ClippingAnalysisOptions();
        bool automatic = !options.AbsoluteThreshold.HasValue;
        int minimumRun = Math.Clamp(options.MinimumConsecutiveSamples, 1, 256);
        int automaticMinimumRun = Math.Max(2, minimumRun);
        int maximumGap = Math.Clamp(options.MaximumGapSamples, 0, 32);
        double minimumConfidence = Math.Clamp(options.MinimumConfidence, 0.0, 1.0);
        float minimumPeak = (float)Math.Clamp(options.MinimumPeakLevel, 1e-4, 8.0);
        double relativeTolerance = Math.Clamp(options.PlateauTolerance, 1e-7, 0.02);
        var events = new List<ClippedPeakEvent>();

        for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = channels[channelIndex];
            if (samples.Length < minimumRun + 2) continue;

            if (automatic)
            {
                float positivePeak = 0f;
                float negativePeak = 0f;
                for (int i = 0; i < samples.Length; i++)
                {
                    if (samples[i] > positivePeak) positivePeak = samples[i];
                    float negativeMagnitude = -samples[i];
                    if (negativeMagnitude > negativePeak) negativePeak = negativeMagnitude;
                }

                if (positivePeak >= minimumPeak)
                {
                    float tolerance = Math.Max(2e-7f, positivePeak * (float)relativeTolerance);
                    ScanClippedRuns(samples, channelIndex, ClipPolarity.Positive,
                        positivePeak - tolerance, positivePeak, tolerance, true,
                        automaticMinimumRun, maximumGap, minimumConfidence, events,
                        cancellationToken);
                }
                if (negativePeak >= minimumPeak)
                {
                    float tolerance = Math.Max(2e-7f, negativePeak * (float)relativeTolerance);
                    ScanClippedRuns(samples, channelIndex, ClipPolarity.Negative,
                        negativePeak - tolerance, negativePeak, tolerance, true,
                        automaticMinimumRun, maximumGap, minimumConfidence, events,
                        cancellationToken);
                }
            }
            else
            {
                float threshold = (float)Math.Clamp(Math.Abs(options.AbsoluteThreshold!.Value),
                    1e-5, 8.0);
                float tolerance = Math.Max(2e-7f, threshold * (float)relativeTolerance);
                ScanClippedRuns(samples, channelIndex, ClipPolarity.Positive,
                    threshold, threshold, tolerance, false, minimumRun, maximumGap,
                    minimumConfidence, events, cancellationToken);
                ScanClippedRuns(samples, channelIndex, ClipPolarity.Negative,
                    threshold, threshold, tolerance, false, minimumRun, maximumGap,
                    minimumConfidence, events, cancellationToken);
            }

            progress?.Report(new RestorationProgress(RestorationStage.AnalyzingClipping,
                (double)(channelIndex + 1) / Math.Max(1, channels.Count), channelIndex,
                channels.Count, events.Count, events.Count));
        }

        events.Sort(static (a, b) =>
        {
            int byChannel = a.Channel.CompareTo(b.Channel);
            return byChannel != 0 ? byChannel : a.StartSample.CompareTo(b.StartSample);
        });
        return new ClippingAnalysisResult(events.AsReadOnly(), sampleCount, channels.Count,
            sampleRate, automatic);
    }

    /// <summary>
    /// Analyse and reconstruct clipping into new channel buffers, leaving the source
    /// untouched for preview, bypass, and undo/redo workflows.
    /// </summary>
    public static DeclippingResult Declip(IReadOnlyList<float[]> channels, int sampleRate,
        ClippingAnalysisOptions? analysisOptions = null,
        DeclippingOptions? repairOptions = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        var analysis = AnalyzeClipping(channels, sampleRate, analysisOptions,
            cancellationToken, progress);
        var audio = RepairClipping(channels, analysis.Events, repairOptions,
            cancellationToken, progress);
        return new DeclippingResult(audio, analysis);
    }

    /// <summary>Reconstruct previously analysed clipped peaks into new buffers.</summary>
    public static float[][] RepairClipping(IReadOnlyList<float[]> channels,
        IReadOnlyList<ClippedPeakEvent> events,
        DeclippingOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ValidateRestorationChannels(channels);
        var result = RestorationPreview.Clone(channels);
        RepairClippingInPlace(result, events, options, cancellationToken, progress);
        return result;
    }

    /// <summary>Reconstruct analysed clipped peaks in an owned working buffer.</summary>
    public static int RepairClippingInPlace(float[][] data,
        IReadOnlyList<ClippedPeakEvent> events,
        DeclippingOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(events);
        ValidateRestorationChannels(data);
        options ??= new DeclippingOptions();
        float strength = (float)Math.Clamp(options.Strength, 0.0, 1.0);
        int predictionSamples = Math.Clamp(options.PredictionSamples, 2, 64);
        double maximumGain = Math.Pow(10.0,
            Math.Clamp(options.MaximumReconstructionDb, 0.0, 18.0) / 20.0);
        if (strength <= 0f || events.Count == 0) return 0;

        var ordered = events.OrderBy(e => e.Channel).ThenBy(e => e.StartSample).ToArray();
        int repaired = 0;
        int previousChannel = -1;
        int previousEnd = -1;
        for (int eventIndex = 0; eventIndex < ordered.Length; eventIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clippedPeak = ordered[eventIndex];
            if (clippedPeak.Channel < 0 || clippedPeak.Channel >= data.Length) continue;
            var samples = data[clippedPeak.Channel];
            if (clippedPeak.Channel != previousChannel)
            {
                previousChannel = clippedPeak.Channel;
                previousEnd = -1;
            }

            int start = Math.Max(clippedPeak.StartSample, previousEnd);
            int end = clippedPeak.EndSample;
            if (start < 1 || end >= samples.Length || start >= end) continue;

            ReconstructClippedPeak(samples, clippedPeak, start, end, strength,
                predictionSamples, maximumGain);
            previousEnd = end;
            repaired++;
            if ((eventIndex & 31) == 0 || eventIndex == ordered.Length - 1)
            {
                progress?.Report(new RestorationProgress(RestorationStage.RepairingClipping,
                    (double)(eventIndex + 1) / ordered.Length, clippedPeak.Channel,
                    data.Length, eventIndex + 1, ordered.Length));
            }
        }
        return repaired;
    }

    private static bool TryCreateClickEvent(float[] samples, float[] curvature,
        int channel, int start, int end, int peak, float peakCurvature,
        float threshold, double robustScale, double localRms, int sampleRate,
        int maximumClickSamples, bool preserveTransients, out ClickEvent result)
    {
        result = default;
        if (start < 2 || end >= samples.Length - 1 || end <= start) return false;

        int context = Math.Clamp((int)Math.Round(sampleRate * 0.0008), 6, 64);
        int leftContext = Math.Max(1, start - context);
        int rightContext = Math.Min(samples.Length - 1, end + context);
        double neighborCurvatureSquare = 0;
        int neighborCount = 0;
        for (int i = leftContext; i < start; i++)
        {
            neighborCurvatureSquare += curvature[i] * (double)curvature[i];
            neighborCount++;
        }
        for (int i = end; i < rightContext; i++)
        {
            neighborCurvatureSquare += curvature[i] * (double)curvature[i];
            neighborCount++;
        }
        double neighborCurvatureRms = Math.Sqrt(neighborCurvatureSquare /
            Math.Max(1, neighborCount));

        int left = start - 1;
        int right = end;
        int span = right - left;
        double edgeJump = Math.Abs(samples[right] - samples[left]);
        double leftSlope = EstimateMedianSlopeBefore(samples, left, Math.Min(5, context));
        double rightSlope = EstimateMedianSlopeAfter(samples, right, Math.Min(5, context));
        double expectedEdgeChange = (Math.Abs(leftSlope) + Math.Abs(rightSlope)) * span * 0.75 +
                                    threshold * Math.Sqrt(span) + localRms * 0.015;
        double returnScore = 1.0 - Math.Clamp((edgeJump - expectedEdgeChange) /
            Math.Max(1e-7, localRms * 0.18 + expectedEdgeChange), 0.0, 1.0);

        double zScore = 1.0 - Math.Exp(-Math.Max(0.0, peakCurvature - threshold) /
                                      Math.Max(1e-9, robustScale * 4.0));
        double isolationRatio = peakCurvature /
                                Math.Max(1e-8, neighborCurvatureRms + threshold * 0.15);
        double isolationScore = Math.Clamp((isolationRatio - 1.25) / 5.0, 0.0, 1.0);

        double beforeSquare = 0, afterSquare = 0;
        int beforeCount = 0, afterCount = 0;
        for (int i = leftContext; i < start; i++)
        {
            beforeSquare += samples[i] * (double)samples[i];
            beforeCount++;
        }
        for (int i = end; i < rightContext; i++)
        {
            afterSquare += samples[i] * (double)samples[i];
            afterCount++;
        }
        double beforeRms = Math.Sqrt(beforeSquare / Math.Max(1, beforeCount));
        double afterRms = Math.Sqrt(afterSquare / Math.Max(1, afterCount));
        double attackRatio = afterRms / Math.Max(1e-6, beforeRms + localRms * 0.01);
        double attackPenalty = preserveTransients
            ? Math.Clamp((attackRatio - 2.25) / 5.0, 0.0, 0.55)
            : 0.0;

        double confidence = 0.48 * zScore + 0.27 * isolationScore + 0.25 * returnScore;
        confidence *= 1.0 - attackPenalty;
        if (preserveTransients && returnScore < 0.12) return false;

        double severityRatio = peakCurvature / Math.Max(1e-6, localRms);
        float severity = (float)Math.Clamp(1.0 - Math.Exp(-severityRatio), 0.0, 1.0);
        var kind = end - start <= maximumClickSamples
            ? ImpulseDefectKind.Click
            : ImpulseDefectKind.Pop;
        result = new ClickEvent(channel, start, end, peak, kind,
            (float)Math.Clamp(confidence, 0.0, 1.0), severity, samples[peak], threshold);
        return true;
    }

    private static void AddOrMergeClickEvent(List<ClickEvent> events, ClickEvent next,
        int maximumClickSamples)
    {
        if (events.Count == 0 || events[^1].Channel != next.Channel ||
            next.StartSample > events[^1].EndSample + 1)
        {
            events.Add(next);
            return;
        }

        var previous = events[^1];
        int start = Math.Min(previous.StartSample, next.StartSample);
        int end = Math.Max(previous.EndSample, next.EndSample);
        bool useNextPeak = next.Severity > previous.Severity;
        events[^1] = new ClickEvent(previous.Channel, start, end,
            useNextPeak ? next.PeakSample : previous.PeakSample,
            end - start <= maximumClickSamples ? ImpulseDefectKind.Click : ImpulseDefectKind.Pop,
            Math.Max(previous.Confidence, next.Confidence),
            Math.Max(previous.Severity, next.Severity),
            useNextPeak ? next.PeakAmplitude : previous.PeakAmplitude,
            Math.Min(previous.DetectionThreshold, next.DetectionThreshold));
    }

    private static void InterpolateImpulse(float[] samples, int start, int end,
        float strength, double maximumOvershoot)
    {
        int left = start - 1;
        int right = end;
        int span = right - left;
        float y0 = samples[left];
        float y1 = samples[right];

        float contextPeak = Math.Max(Math.Abs(y0), Math.Abs(y1));
        int gapLength = end - start;
        int contextLength = Math.Clamp(gapLength * 6, 96, 768);
        int contextStart = Math.Max(0, left - contextLength);
        int contextEnd = Math.Min(samples.Length, right + contextLength + 1);
        for (int i = contextStart; i < left; i++)
            contextPeak = Math.Max(contextPeak, Math.Abs(samples[i]));
        for (int i = right + 1; i < contextEnd; i++)
            contextPeak = Math.Max(contextPeak, Math.Abs(samples[i]));
        double limit = Math.Max(1e-6, contextPeak * maximumOvershoot);

        double[] reconstruction = gapLength >= 4 &&
                                  TryBidirectionalLinearPrediction(samples, start, end,
                                      contextLength, limit, out double[] predicted)
            ? predicted
            : CubicImpulseInterpolation(samples, start, end);

        for (int i = start; i < end; i++)
        {
            double interpolated = reconstruction[i - start];
            if (!double.IsFinite(interpolated))
            {
                double t = (double)(i - left) / span;
                interpolated = y0 + (y1 - y0) * t;
            }
            interpolated = Math.Clamp(interpolated, -limit, limit);
            samples[i] += ((float)interpolated - samples[i]) * strength;
        }
    }

    /// <summary>
    /// Reconstruct a missing impulse span from autoregressive models fitted to clean
    /// audio on both sides. Forward and backward predictions are equal-power blended,
    /// preserving periodic/complex waveform motion that cubic interpolation flattens.
    /// </summary>
    private static bool TryBidirectionalLinearPrediction(float[] samples, int start, int end,
        int requestedContext, double outputLimit, out double[] reconstruction)
    {
        int gapLength = end - start;
        reconstruction = new double[Math.Max(0, gapLength)];
        if (gapLength <= 0) return false;

        int leftCount = Math.Min(requestedContext, start);
        int rightCount = Math.Min(requestedContext, samples.Length - end);
        int available = Math.Min(leftCount, rightCount);
        int order = Math.Min(48, Math.Min(Math.Max(12, gapLength / 2), available / 3));
        if (order < 6) return false;

        if (!TryAutoregressivePrediction(samples, start - leftCount, leftCount,
                reverse: false, gapLength, order, outputLimit, out double[] forward) ||
            !TryAutoregressivePrediction(samples, end, rightCount,
                reverse: true, gapLength, order, outputLimit, out double[] backward))
            return false;

        for (int offset = 0; offset < gapLength; offset++)
        {
            double t = (offset + 1.0) / (gapLength + 1.0);
            double rightWeight = Math.Sin(t * Math.PI * 0.5);
            rightWeight *= rightWeight;
            double leftWeight = 1.0 - rightWeight;
            double value = forward[offset] * leftWeight +
                           backward[gapLength - 1 - offset] * rightWeight;
            if (!double.IsFinite(value) || Math.Abs(value) > outputLimit * 4)
                return false;
            reconstruction[offset] = value;
        }
        return true;
    }

    private static bool TryAutoregressivePrediction(float[] samples, int start, int count,
        bool reverse, int forecastCount, int order, double outputLimit, out double[] forecast)
    {
        forecast = new double[forecastCount];
        if (count <= order || start < 0 || start + count > samples.Length) return false;

        var sequence = new double[count];
        double mean = 0;
        for (int index = 0; index < count; index++)
        {
            int sourceIndex = reverse ? start + count - 1 - index : start + index;
            double value = samples[sourceIndex];
            if (!double.IsFinite(value)) return false;
            sequence[index] = value;
            mean += value;
        }
        mean /= count;
        for (int index = 0; index < count; index++) sequence[index] -= mean;

        var autocorrelation = new double[order + 1];
        for (int lag = 0; lag <= order; lag++)
        {
            double sum = 0;
            for (int index = lag; index < count; index++)
                sum += sequence[index] * sequence[index - lag];
            autocorrelation[lag] = sum / count;
        }
        if (!double.IsFinite(autocorrelation[0]) || autocorrelation[0] < 1e-12)
        {
            Array.Fill(forecast, mean);
            return true;
        }

        var coefficients = new double[order + 1];
        coefficients[0] = 1;
        double error = autocorrelation[0];
        int fittedOrder = 0;
        for (int currentOrder = 1; currentOrder <= order; currentOrder++)
        {
            double residual = autocorrelation[currentOrder];
            for (int index = 1; index < currentOrder; index++)
                residual += coefficients[index] * autocorrelation[currentOrder - index];
            double reflection = -residual / Math.Max(1e-18, error);
            if (!double.IsFinite(reflection)) break;
            reflection = Math.Clamp(reflection, -0.985, 0.985);

            var updated = (double[])coefficients.Clone();
            for (int index = 1; index < currentOrder; index++)
                updated[index] = coefficients[index] +
                                 reflection * coefficients[currentOrder - index];
            updated[currentOrder] = reflection;
            coefficients = updated;
            error *= Math.Max(1e-6, 1.0 - reflection * reflection);
            fittedOrder = currentOrder;
            if (!double.IsFinite(error) || error < autocorrelation[0] * 1e-10) break;
        }
        if (fittedOrder < 2) return false;

        var history = new double[count + forecastCount];
        Array.Copy(sequence, history, count);
        for (int step = 0; step < forecastCount; step++)
        {
            int outputIndex = count + step;
            double prediction = 0;
            for (int lag = 1; lag <= fittedOrder; lag++)
                prediction -= coefficients[lag] * history[outputIndex - lag];
            double value = prediction + mean;
            if (!double.IsFinite(value) || Math.Abs(value) > outputLimit * 4)
                return false;
            history[outputIndex] = prediction;
            forecast[step] = value;
        }
        return true;
    }

    private static double[] CubicImpulseInterpolation(float[] samples, int start, int end)
    {
        int left = start - 1;
        int right = end;
        int span = right - left;
        double y0 = samples[left];
        double y1 = samples[right];
        double slope0 = EstimateMedianSlopeBefore(samples, left, 5);
        double slope1 = EstimateMedianSlopeAfter(samples, right, 5);
        var reconstruction = new double[end - start];
        for (int i = start; i < end; i++)
        {
            double t = (double)(i - left) / span;
            double t2 = t * t;
            double t3 = t2 * t;
            reconstruction[i - start] =
                (2 * t3 - 3 * t2 + 1) * y0 +
                (t3 - 2 * t2 + t) * slope0 * span +
                (-2 * t3 + 3 * t2) * y1 +
                (t3 - t2) * slope1 * span;
        }
        return reconstruction;
    }

    private static void ScanClippedRuns(float[] samples, int channel,
        ClipPolarity polarity, float threshold, float plateauLevel, float tolerance,
        bool automatic, int minimumRun, int maximumGap, double minimumConfidence,
        List<ClippedPeakEvent> destination, CancellationToken cancellationToken)
    {
        float sign = polarity == ClipPolarity.Positive ? 1f : -1f;
        int i = 1;
        while (i < samples.Length - 1)
        {
            if ((i & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (samples[i] * sign < threshold)
            {
                i++;
                continue;
            }

            int start = i;
            int lastHigh = i;
            int highCount = 1;
            int peakSample = i;
            float observedPeak = samples[i] * sign;
            int j = i + 1;
            while (j < samples.Length - 1)
            {
                float signedSample = samples[j] * sign;
                if (signedSample >= threshold)
                {
                    lastHigh = j;
                    highCount++;
                    if (signedSample > observedPeak)
                    {
                        observedPeak = signedSample;
                        peakSample = j;
                    }
                }
                else if (j - lastHigh > maximumGap)
                {
                    break;
                }
                j++;
            }

            int end = lastHigh + 1;
            if (highCount >= minimumRun && end < samples.Length &&
                TryCreateClippedPeakEvent(samples, channel, start, end, peakSample,
                    polarity, threshold, Math.Max(plateauLevel, observedPeak), tolerance,
                    automatic, minimumRun, out var clippedEvent) &&
                clippedEvent.Confidence >= minimumConfidence)
            {
                destination.Add(clippedEvent);
            }
            i = Math.Max(i + 1, end);
        }
    }

    private static bool TryCreateClippedPeakEvent(float[] samples, int channel,
        int start, int end, int peakSample, ClipPolarity polarity, float threshold,
        float plateauLevel, float tolerance, bool automatic, int minimumRun,
        out ClippedPeakEvent result)
    {
        result = default;
        if (start < 1 || end >= samples.Length || end <= start) return false;
        float sign = polarity == ClipPolarity.Positive ? 1f : -1f;
        int length = end - start;

        double insideDifference = 0;
        double maximumInsideDifference = 0;
        int insidePairs = 0;
        int nearEqualPairs = 0;
        double equalityTolerance = Math.Max(1e-8, tolerance * 0.12);
        for (int i = start + 1; i < end; i++)
        {
            double difference = Math.Abs(samples[i] - samples[i - 1]);
            insideDifference += difference;
            maximumInsideDifference = Math.Max(maximumInsideDifference, difference);
            insidePairs++;
            if (difference <= equalityTolerance) nearEqualPairs++;
        }

        double shoulderDifference = 0;
        int shoulderPairs = 0;
        for (int i = Math.Max(1, start - 5); i <= start; i++)
        {
            shoulderDifference += Math.Abs(samples[i] - samples[i - 1]);
            shoulderPairs++;
        }
        for (int i = end; i < Math.Min(samples.Length - 1, end + 5); i++)
        {
            shoulderDifference += Math.Abs(samples[i + 1] - samples[i]);
            shoulderPairs++;
        }
        double averageInsideDifference = insideDifference / Math.Max(1, insidePairs);
        double averageShoulderDifference = shoulderDifference / Math.Max(1, shoulderPairs);
        double flatnessContrast = averageShoulderDifference /
                                  Math.Max(1e-9, averageInsideDifference);
        double equalityScore = insidePairs == 0 ? 0 : (double)nearEqualPairs / insidePairs;
        double contrastScore = Math.Clamp((flatnessContrast - 1.5) / 6.0, 0.0, 1.0);
        double lengthScore = Math.Clamp((double)(length - minimumRun + 1) / 6.0, 0.0, 1.0);

        double confidence;
        if (automatic)
        {
            // Auto mode requires actual flat-top evidence; a smooth natural maximum may
            // cross the near-peak threshold, but normally has no adjacent equal samples.
            if (nearEqualPairs == 0 || maximumInsideDifference > tolerance * 1.5)
                return false;
            // Very low-frequency PCM can quantize a perfectly clean rounded maximum into
            // a few identical samples. A short plateau is credible only when its shoulder
            // is steep enough to indicate an abrupt transition into the flat region.
            if (length < 6 && averageShoulderDifference < plateauLevel * 0.001)
                return false;
            confidence = 0.50 * equalityScore + 0.32 * contrastScore +
                         0.18 * Math.Max(lengthScore, 0.35);
        }
        else
        {
            double exceedance = Math.Max(0.0, plateauLevel - threshold) /
                                Math.Max(1e-6, threshold);
            confidence = 0.58 + 0.16 * lengthScore + 0.14 * contrastScore +
                         0.12 * Math.Clamp(exceedance * 8.0, 0.0, 1.0);
        }

        double leftSlope = EstimateWeightedBoundarySlopeBefore(samples, start - 1, 6) * sign;
        double rightSlope = -EstimateWeightedBoundarySlopeAfter(samples, end, 6) * sign;
        double inwardSlope = (Math.Max(0.0, leftSlope) + Math.Max(0.0, rightSlope)) * 0.5;
        // A conservative parabolic shoulder extrapolation. The 0.20 factor stays below
        // the idealised 0.25 tangent estimate because real clipped transfers commonly
        // include bandwidth limiting and pre-ringing at the plateau edge.
        double estimatedOvershoot = inwardSlope * (length + 2) * 0.20;
        double estimatedMagnitude = Math.Max(plateauLevel,
            plateauLevel + Math.Max(plateauLevel * 0.001, estimatedOvershoot));
        double severity = Math.Clamp(estimatedOvershoot /
                                     Math.Max(1e-6, plateauLevel * 0.12), 0.0, 1.0);
        int centerPeak = (start + end - 1) / 2;
        if (!automatic && peakSample >= start && peakSample < end) centerPeak = peakSample;

        result = new ClippedPeakEvent(channel, start, end, centerPeak, polarity,
            plateauLevel, (float)(estimatedMagnitude * sign),
            (float)Math.Clamp(confidence, 0.0, 1.0), (float)severity);
        return true;
    }

    private static void ReconstructClippedPeak(float[] samples, ClippedPeakEvent clippedPeak,
        int start, int end, float strength, int predictionSamples, double maximumGain)
    {
        int left = start - 1;
        int right = end;
        int span = right - left;
        float sign = clippedPeak.Polarity == ClipPolarity.Positive ? 1f : -1f;
        double y0 = samples[left];
        double y1 = samples[right];
        double slope0 = EstimateWeightedBoundarySlopeBefore(samples, left, predictionSamples);
        double slope1 = EstimateWeightedBoundarySlopeAfter(samples, right, predictionSamples);
        if (sign * slope0 < 0) slope0 = 0;
        if (sign * slope1 > 0) slope1 = 0;

        double observedLevel = Math.Max(Math.Abs(clippedPeak.ClipLevel),
            Math.Max(Math.Abs(y0), Math.Abs(y1)));
        double targetPeak = Math.Max(observedLevel, Math.Abs(clippedPeak.EstimatedTruePeak));
        double maximumPeak = Math.Max(observedLevel, observedLevel * maximumGain);
        targetPeak = Math.Min(targetPeak, maximumPeak);

        var reconstruction = new double[end - start];
        double reconstructedSignedPeak = double.NegativeInfinity;
        for (int i = start; i < end; i++)
        {
            double t = (double)(i - left) / span;
            double t2 = t * t;
            double t3 = t2 * t;
            double value = (2 * t3 - 3 * t2 + 1) * y0 +
                           (t3 - 2 * t2 + t) * slope0 * span +
                           (-2 * t3 + 3 * t2) * y1 +
                           (t3 - t2) * slope1 * span;
            if (!double.IsFinite(value)) value = y0 + (y1 - y0) * t;
            reconstruction[i - start] = value;
            reconstructedSignedPeak = Math.Max(reconstructedSignedPeak, value * sign);
        }

        // A shoulder tangent can overshoot on strongly curved low-frequency peaks. Scale
        // that arch about the clean boundary level instead of hard-clamping it, which
        // would merely create a new flat top.
        double boundaryFloor = Math.Min(y0 * sign, y1 * sign);
        if (reconstructedSignedPeak > targetPeak && reconstructedSignedPeak > boundaryFloor + 1e-9)
        {
            double scale = (targetPeak - boundaryFloor) /
                           (reconstructedSignedPeak - boundaryFloor);
            for (int i = 0; i < reconstruction.Length; i++)
            {
                double signedValue = reconstruction[i] * sign;
                if (signedValue > boundaryFloor)
                    reconstruction[i] = (boundaryFloor +
                        (signedValue - boundaryFloor) * scale) * sign;
            }
            reconstructedSignedPeak = targetPeak;
        }

        double missingPeak = Math.Max(0.0, targetPeak - reconstructedSignedPeak);
        for (int i = start; i < end; i++)
        {
            double t = (double)(i - left) / span;
            double bump = Math.Sin(Math.PI * t);
            double value = reconstruction[i - start] + sign * missingPeak * bump * bump;
            value = Math.Clamp(value, -maximumPeak, maximumPeak);
            samples[i] += ((float)value - samples[i]) * strength;
        }
    }

    private static double EstimateMedianSlopeBefore(float[] samples, int anchor, int count)
    {
        int start = Math.Max(1, anchor - Math.Max(1, count) + 1);
        int length = Math.Max(0, anchor - start + 1);
        if (length == 0) return anchor > 0 ? samples[anchor] - samples[anchor - 1] : 0.0;
        var slopes = new double[length];
        for (int i = 0; i < length; i++)
        {
            int sample = start + i;
            slopes[i] = samples[sample] - samples[sample - 1];
        }
        Array.Sort(slopes);
        return slopes[length / 2];
    }

    private static double EstimateMedianSlopeAfter(float[] samples, int anchor, int count)
    {
        int end = Math.Min(samples.Length - 2, anchor + Math.Max(1, count) - 1);
        int length = Math.Max(0, end - anchor + 1);
        if (length == 0)
            return anchor + 1 < samples.Length ? samples[anchor + 1] - samples[anchor] : 0.0;
        var slopes = new double[length];
        for (int i = 0; i < length; i++)
        {
            int sample = anchor + i;
            slopes[i] = samples[sample + 1] - samples[sample];
        }
        Array.Sort(slopes);
        return slopes[length / 2];
    }

    private static double EstimateWeightedBoundarySlopeBefore(float[] samples, int anchor, int count)
    {
        int start = Math.Max(1, anchor - Math.Max(1, count) + 1);
        double weightedSlope = 0;
        double weightSum = 0;
        for (int sample = anchor; sample >= start; sample--)
        {
            int distance = anchor - sample;
            double weight = 1.0 / ((distance + 1.0) * (distance + 1.0));
            weightedSlope += (samples[sample] - samples[sample - 1]) * weight;
            weightSum += weight;
        }
        return weightSum > 0 ? weightedSlope / weightSum : 0.0;
    }

    private static double EstimateWeightedBoundarySlopeAfter(float[] samples, int anchor, int count)
    {
        int end = Math.Min(samples.Length - 2, anchor + Math.Max(1, count) - 1);
        double weightedSlope = 0;
        double weightSum = 0;
        for (int sample = anchor; sample <= end; sample++)
        {
            int distance = sample - anchor;
            double weight = 1.0 / ((distance + 1.0) * (distance + 1.0));
            weightedSlope += (samples[sample + 1] - samples[sample]) * weight;
            weightSum += weight;
        }
        return weightSum > 0 ? weightedSlope / weightSum : 0.0;
    }

    private static float MedianInPlace(float[] values, int count)
    {
        if (count <= 0) return 0f;
        Array.Sort(values, 0, count);
        int middle = count / 2;
        return (count & 1) != 0
            ? values[middle]
            : 0.5f * (values[middle - 1] + values[middle]);
    }

    private static void EnsureScratchCapacity(ref float[] scratch, int required)
    {
        if (scratch.Length < required) scratch = new float[required];
    }

    private static int ValidateRestorationChannels(IReadOnlyList<float[]> channels,
        int? sampleRate = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate.HasValue && sampleRate.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels.Count == 0) return 0;
        if (channels[0] is null)
            throw new ArgumentException("Channel buffers cannot be null.", nameof(channels));

        int length = channels[0].Length;
        for (int c = 1; c < channels.Count; c++)
        {
            if (channels[c] is null)
                throw new ArgumentException("Channel buffers cannot be null.", nameof(channels));
            if (channels[c].Length != length)
                throw new ArgumentException("All channel buffers must have the same length.", nameof(channels));
        }
        return length;
    }
}
