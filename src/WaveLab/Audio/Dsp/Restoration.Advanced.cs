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
        // The amplitude-outlier pass follows sensitivity too: the strict 8x envelope
        // gate at the bottom of the slider relaxes toward 3x at the top, so
        // conspicuous flat/decaying pops surface at working sensitivities.
        double outlierRatioThreshold = 8.0 - 5.0 * sensitivityT;

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
                            maximumClickSamples, options.PreserveTransients,
                            options.LocalHighFrequencyContrast, options.TrendRelativeRecovery,
                            options.MinimumRecovery, out var clickEvent) &&
                        clickEvent.Confidence >= minimumConfidence)
                    {
                        AddOrMergeClickEvent(events, clickEvent, maximumClickSamples);
                    }

                    nextCandidateSample = Math.Max(nextCandidateSample, end + postCandidateSkipSamples);
                    i = Math.Max(i + 1, nextCandidateSample);
                }

                // ── Amplitude-based detection pass ──────────────────
                // Curvature misses low-frequency thumps/pops that don't
                // produce sharp second differences. Scan for isolated
                // amplitude outliers in the same block.
                DetectAmplitudeOutliers(samples, curvature, channelIndex, blockStart, blockEnd,
                    sampleRate, localRms, maximumPopSamples, maximumClickSamples,
                    options.PreserveTransients, minimumConfidence, outlierRatioThreshold, events);


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

            if (options.PredictiveDetection)
            {
                AddPredictiveCandidates(samples, curvature, channelIndex, sampleRate, options,
                    sensitivityT, maximumClickSamples, maximumPopSamples, minimumConfidence,
                    events, cancellationToken);
            }
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
    /// already owns a disposable working copy. Channels are repaired concurrently, so
    /// <paramref name="progress"/> reports may arrive from worker threads and out of order —
    /// consumers should sample the latest value, not accumulate.
    /// </summary>
    public static int RepairClicksInPlace(float[][] data, IReadOnlyList<ClickEvent> events,
        ClickRepairOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
        => RepairClicksInPlace(data, events, options, cancellationToken, progress,
            maxDegreeOfParallelism: -1);

    /// <summary>
    /// <paramref name="maxDegreeOfParallelism"/> exists for the identity test alone: 1 runs the
    /// channels in turn, giving the parallel default a reference to be bit-identical against.
    /// </summary>
    internal static int RepairClicksInPlace(float[][] data, IReadOnlyList<ClickEvent> events,
        ClickRepairOptions? options,
        CancellationToken cancellationToken,
        IProgress<RestorationProgress>? progress,
        int maxDegreeOfParallelism)
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
        if (ordered.Length == 0) return 0;

        // The plan is channel-major with overlapping guards already merged per channel, so each
        // channel's events are one contiguous run over an array no other run touches. That is
        // what makes the channels safe to repair at once — the parallelism the de-crackle stage
        // already uses, worth about 2x on a stereo transfer — and Janssen at ~35x a linear
        // bridge is the wall clock of this whole tool.
        var runs = new List<(int First, int Limit)>(data.Length);
        for (int index = 0; index < ordered.Length;)
        {
            int channel = ordered[index].Channel;
            int limit = index + 1;
            while (limit < ordered.Length && ordered[limit].Channel == channel) limit++;
            runs.Add((index, limit));
            index = limit;
        }

        // The exclusivity argument for the parallel dispatch below is one run per channel, and it
        // rests on the plan being channel-major — a sort enforced in CreateClickRepairPlan, not
        // here. If that ever stops being true, two runs would race on one channel's array, and the
        // failure would be corrupted repair audio under scheduling rather than an exception. Fail
        // loud where the invariant is relied on.
        var ownedChannels = new HashSet<int>();
        foreach (var run in runs)
            if (!ownedChannels.Add(ordered[run.First].Channel))
                throw new InvalidOperationException(
                    "The click repair plan is no longer channel-major; parallel repair would race.");

        int repaired = 0;
        int processed = 0;
        void RepairRun((int First, int Limit) run)
        {
            int repairedInRun = 0;
            int previousEnd = -1;
            for (int eventIndex = run.First; eventIndex < run.Limit; eventIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var defect = ordered[eventIndex];
                var samples = data[defect.Channel];
                // The plan's merge pass keeps one channel's events disjoint, so the clamp is a
                // backstop rather than a decision.
                int start = Math.Max(defect.StartSample, previousEnd);
                int end = defect.EndSample;
                int done = Interlocked.Increment(ref processed);
                if (start >= 1 && end < samples.Length && start < end)
                {
                    InterpolateImpulse(samples, start, end, strength, maximumOvershoot);
                    previousEnd = end;
                    repairedInRun++;
                }
                if ((done & 31) == 0 || done == ordered.Length)
                {
                    progress?.Report(new RestorationProgress(RestorationStage.RepairingClicks,
                        (double)done / ordered.Length, defect.Channel, data.Length,
                        done, ordered.Length));
                }
            }
            Interlocked.Add(ref repaired, repairedInRun);
        }

        if (runs.Count == 1 || maxDegreeOfParallelism == 1)
        {
            foreach (var run in runs) RepairRun(run);
        }
        else
        {
            try
            {
                Parallel.ForEach(runs, new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                }, RepairRun);
            }
            catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
            {
                // Parallel.ForEach wraps a worker's fault, so a caller showing ex.Message would
                // report "One or more errors occurred" instead of the fault. Cancellation is not
                // affected — an OCE matching the options' token is already rethrown unwrapped.
                // Capture preserves the original stack, so serial and parallel report identically.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(aggregate.InnerException!).Throw();
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
                // A transfer can contain several clipping rails: gain changes, edits, or two
                // converters can put genuine flat tops at different amplitudes. Looking only at
                // the channel's absolute maximum hides every lower rail. Walk the signal once and
                // seed candidates from local near-equal pairs, then validate each with the same
                // shoulder/flatness tests used previously.
                ScanAutomaticClippedRuns(samples, channelIndex, ClipPolarity.Positive,
                    minimumPeak, relativeTolerance, automaticMinimumRun, maximumGap,
                    minimumConfidence, events, cancellationToken);
                ScanAutomaticClippedRuns(samples, channelIndex, ClipPolarity.Negative,
                    minimumPeak, relativeTolerance, automaticMinimumRun, maximumGap,
                    minimumConfidence, events, cancellationToken);
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

        // Channels the sparse solver takes are reconstructed whole, before the per-event loop, and
        // then skipped by it. The two methods must not both run over the same audio: A-SPADE decides
        // its own frame boundaries and drawing an arch through its output afterwards would replace
        // the waveform it reconstructed with the shape the other method assumed.
        var sparseChannels = SelectSparseChannels(data, ordered, options.Method, cancellationToken);
        int repaired = 0;
        foreach (int channel in sparseChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            repaired += RepairChannelSparse(data[channel], ordered, channel, strength, maximumGain,
                cancellationToken);
            progress?.Report(new RestorationProgress(RestorationStage.RepairingClipping,
                (double)(repaired) / ordered.Length, channel, data.Length, repaired, ordered.Length));
        }

        int previousChannel = -1;
        int previousEnd = -1;
        for (int eventIndex = 0; eventIndex < ordered.Length; eventIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clippedPeak = ordered[eventIndex];
            if (clippedPeak.Channel < 0 || clippedPeak.Channel >= data.Length) continue;
            if (sparseChannels.Contains(clippedPeak.Channel)) continue;
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

    /// <summary>
    /// Which channels A-SPADE should reconstruct. Empty for
    /// <see cref="DeclipMethod.PeakReconstruction"/>, every damaged channel for
    /// <see cref="DeclipMethod.Sparse"/>, and per-channel measurement for
    /// <see cref="DeclipMethod.Automatic"/>.
    /// </summary>
    private static HashSet<int> SelectSparseChannels(float[][] data,
        ClippedPeakEvent[] ordered, DeclipMethod method, CancellationToken cancellationToken)
    {
        var chosen = new HashSet<int>();
        foreach (var choice in DescribeDeclipChoices(data, ordered, method, cancellationToken))
            if (choice.Method == DeclipMethod.Sparse) chosen.Add(choice.Channel);
        return chosen;
    }

    /// <summary>
    /// What each damaged channel would be repaired with, and the two measurements behind it. The
    /// repair calls this, and so does the workbench readout — a report that were computed separately
    /// could disagree with what actually ran, which is the one thing it must never do.
    /// </summary>
    public static IReadOnlyList<DeclipChannelChoice> DescribeDeclipChoices(
        IReadOnlyList<float[]> channels,
        IReadOnlyList<ClippedPeakEvent> events,
        DeclipMethod method = DeclipMethod.Automatic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(events);

        var choices = new List<DeclipChannelChoice>();
        foreach (var group in events.GroupBy(e => e.Channel).OrderBy(g => g.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int channel = group.Key;
            if (channel < 0 || channel >= channels.Count) continue;
            var samples = channels[channel];
            if (samples == null || samples.Length == 0) continue;

            // The damage is counted from the events rather than from the raw sample values, so the
            // decision rests on the same evidence the repair does.
            long clipped = 0;
            int runs = 0;
            foreach (var e in group)
            {
                clipped += Math.Max(0, e.EndSample - e.StartSample);
                runs++;
            }
            double fraction = (double)clipped / samples.Length;
            double meanRun = runs == 0 ? 0 : (double)clipped / runs;

            if (method != DeclipMethod.Automatic)
            {
                choices.Add(new DeclipChannelChoice(channel, method, fraction, meanRun));
                continue;
            }

            choices.Add(new DeclipChannelChoice(channel,
                DeclipMethodChooser.PrefersSparse(fraction, meanRun)
                    ? DeclipMethod.Sparse
                    : DeclipMethod.PeakReconstruction,
                fraction, meanRun));
        }
        return choices;
    }

    /// <summary>
    /// The clipping threshold to treat a whole channel as having.
    /// </summary>
    /// <remarks>
    /// The median, not the minimum: one misdetected event at a low threshold would otherwise declare
    /// most of the programme unreliable, and hand the solver a signal with nothing left to fit a
    /// model to.
    /// </remarks>
    private static double MedianClipLevel(IEnumerable<ClippedPeakEvent> events)
    {
        var levels = events.Select(e => (double)e.AbsoluteClipLevel).Where(l => l > 0).ToList();
        if (levels.Count == 0) return 0;
        levels.Sort();
        return levels[levels.Count / 2];
    }

    /// <summary>
    /// Reconstructs one channel with A-SPADE, then blends and bounds it the way the per-event path
    /// does, so STRENGTH and the reconstruction ceiling mean the same thing whichever method ran.
    /// </summary>
    private static int RepairChannelSparse(float[] samples, ClippedPeakEvent[] ordered, int channel,
        float strength, double maximumGain, CancellationToken cancellationToken)
    {
        int count = ordered.Count(e => e.Channel == channel);
        double clipLevel = MedianClipLevel(ordered.Where(e => e.Channel == channel));
        if (count == 0 || !(clipLevel > 0)) return 0;

        var working = (float[])samples.Clone();
        // SpadeOptions.Default, not SpadeOptions.For: the adaptive budget is a large win on dense
        // synthetic material and a measured loss on real programme. See SpadeOptions.For.
        Spade.Declip(working, clipLevel, SpadeOptions.Default, cancellationToken);

        double ceiling = clipLevel * maximumGain;
        for (int i = 0; i < samples.Length; i++)
        {
            double value = Math.Clamp(working[i], -ceiling, ceiling);
            if (!double.IsFinite(value)) continue;
            samples[i] += (float)((value - samples[i]) * strength);
        }
        return count;
    }

    /// <summary>
    /// Whether the shoulder extrapolation should cap the sparse reconstruction on this channel,
    /// from the overshoot the shoulders themselves claim. <b>Shipped, then withdrawn by a fifth
    /// corpus. Nothing calls this; read the remarks before reinstating it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A-SPADE is told only that the clipped samples reached the rail, so nothing stops it
    /// inventing a peak far above one, and on lightly clipped material it does. Capping it below
    /// a mean claimed overshoot of 15% gained <b>+46.5 dB over the first 272 cells</b> and
    /// survived leave-one-corpus-out on three corpora with no fold negative, so it shipped.
    /// </para>
    /// <para>
    /// <b>It does not survive five corpora.</b> Spoken word cost it 5.2 dB and classical another
    /// 33.4: over 464 cells it is worth <b>+7.9 dB, which is 0.017 dB a cell</b>, and held out a
    /// corpus at a time it scores <b>−38.6</b>, two of the five folds selecting no cap at all.
    /// </para>
    /// <para>
    /// <b>The mechanism is the part worth keeping.</b> The shoulders claim little overshoot
    /// precisely when the material is <i>sparse</i>, because a sparse tonal signal approaches the
    /// rail smoothly — so the gate fires hardest on the material whose reconstruction least needs
    /// capping. The variable is not "the shoulders are trustworthy"; it is closer to "this
    /// material is sparse", and the correlation was read backwards. The underlying problem is
    /// real — A-SPADE does overshoot on light damage — so anything that attacks it again must
    /// <b>separate dense material from sparse</b>, which this does not, and must clear five
    /// corpora rather than three. Four other refinements to this cap were measured and declined
    /// before it, three of which also passed leave-one-recording-out first.
    /// </para>
    /// </remarks>
    internal static bool ShouldersBoundTheReconstruction(IEnumerable<ClippedPeakEvent> channelEvents)
    {
        double claimed = 0;
        int counted = 0;
        foreach (var clippedPeak in channelEvents)
        {
            double rail = clippedPeak.AbsoluteClipLevel;
            if (!(rail > 0)) continue;
            claimed += Math.Abs(clippedPeak.EstimatedTruePeak) / rail - 1;
            counted++;
        }
        return counted > 0 && claimed / counted < MaximumBoundedOvershoot;
    }

    /// <summary>The threshold the withdrawn cap used. See <see cref="ShouldersBoundTheReconstruction"/>.</summary>
    public const double MaximumBoundedOvershoot = 0.15;

    /// <summary>
    /// Nominates candidates from the prediction residual of a high-order model, for the acceptance
    /// tests to judge like any other. See <see cref="ClickAnalysisOptions.PredictiveDetection"/>.
    /// </summary>
    /// <remarks>
    /// The block, the order and the robust scale follow <see cref="Decrackle"/>, which solves the
    /// same problem for a denser and quieter defect, and shares its predictor fit rather than
    /// carrying a second one. What differs is what happens next: crackle is repaired wherever the
    /// residual is an outlier, while a candidate here still has to look like a click.
    /// </remarks>
    private static void AddPredictiveCandidates(float[] samples, float[] curvature, int channel,
        int sampleRate, ClickAnalysisOptions options, double sensitivityT,
        int maximumClickSamples, int maximumPopSamples, double minimumConfidence,
        List<ClickEvent> events, CancellationToken cancellationToken)
    {
        int order = Math.Clamp(options.PredictorOrder, 4, 64);
        int block = Math.Max(order * 8, 4096);
        if (samples.Length < block + 4) return;

        // Strict at the bottom of the slider, permissive at the top, like every other gate here.
        double residualSigma = Math.Max(2.0, options.PredictiveSigma) - 4.0 * sensitivityT;
        var residual = new double[block];
        var magnitude = new double[block];
        var coefficients = new double[order + 1];
        var autocorrelation = new double[order + 1];
        var found = new List<ClickEvent>();

        // The final partial block is scanned too, clamped back to the last full block's
        // worth of samples. Stopping at the last whole block dropped up to 4096 samples —
        // about 93 ms at 44.1 kHz — off the end of every channel, which on a record side
        // is the run-out.
        int lastStart = samples.Length - block;
        // The block starts, built up front and including a final one clamped back so the tail is
        // scanned too. Deliberately a list rather than an index advanced inside the loop: the body
        // has several `continue` paths, and with a clamped index every one of them is a chance to
        // re-clamp to the same block and spin. That is exactly what happened - a silent tail block
        // gives a zero residual scale, and the `continue` for it looped forever on any file whose
        // last block is constant.
        var starts = new List<int>();
        for (int start = 0; start + block <= samples.Length; start += block) starts.Add(start);
        if (starts.Count == 0 || starts[^1] != lastStart) starts.Add(lastStart);

        foreach (int start in starts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Decrackle.FitPredictor(samples, start, block, order, autocorrelation, coefficients))
                continue;

            double squareSum = 0;
            for (int i = 0; i < block; i++)
            {
                double predicted = 0;
                for (int k = 1; k <= order; k++)
                {
                    int index = start + i - k;
                    if (index >= 0) predicted += coefficients[k] * samples[index];
                }
                residual[i] = samples[start + i] - predicted;
                magnitude[i] = Math.Abs(residual[i]);
                squareSum += samples[start + i] * (double)samples[start + i];
            }

            double scale = Decrackle.RobustScale(magnitude);
            if (scale <= 1e-9) continue;
            double limit = scale * residualSigma;
            double localRms = Math.Sqrt(squareSum / block);

            for (int i = 1; i < block - 1; i++)
            {
                if (magnitude[i] <= limit) continue;

                int spanStart = start + i;
                int spanEnd = spanStart + 1;
                while (i + 1 < block - 1 && magnitude[i + 1] > scale * 1.5 &&
                       spanEnd - spanStart < maximumPopSamples)
                {
                    i++;
                    spanEnd++;
                }

                int peak = spanStart;
                for (int j = spanStart; j < spanEnd; j++)
                    if (curvature[j] > curvature[peak]) peak = j;

                if (TryCreateClickEvent(samples, curvature, channel, spanStart, spanEnd, peak,
                        curvature[peak], (float)limit, scale, localRms, sampleRate,
                        maximumClickSamples, options.PreserveTransients,
                        options.LocalHighFrequencyContrast, options.TrendRelativeRecovery,
                        options.MinimumRecovery, out var candidate) &&
                    candidate.Confidence >= minimumConfidence)
                {
                    found.Add(candidate);
                }
            }
        }

        // Merged rather than appended: the curvature pass has already found the loud ones, and the
        // same defect must not be reported twice.
        foreach (var candidate in found)
        {
            bool overlaps = false;
            foreach (var existing in events)
            {
                if (existing.Channel != channel) continue;
                if (candidate.StartSample < existing.EndSample && existing.StartSample < candidate.EndSample)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps) events.Add(candidate);
        }
    }

    private static bool TryCreateClickEvent(float[] samples, float[] curvature,
        int channel, int start, int end, int peak, float peakCurvature,
        float threshold, double robustScale, double localRms, int sampleRate,
        int maximumClickSamples, bool preserveTransients, bool localContrast,
        bool trendRelativeRecovery, double minimumRecovery, out ClickEvent result)
    {
        result = default;
        if (start < 2 || end >= samples.Length - 1 || end <= start) return false;

        int spanLength = end - start;
        int left = start - 1;
        int right = end;

        // ── 1. Duration gate ──────────────────────────────────────
        // Genuine clicks are extremely short. Anything beyond ~50 samples
        // (~1ms at 48kHz) is almost certainly musical, not a click.
        int maxClickSpan = Math.Max(maximumClickSamples, (int)(sampleRate * 0.001));
        if (spanLength > maxClickSpan * 3) return false;

        // ── 2. High-frequency energy ratio ────────────────────────
        // A vinyl click is a broadband impulse with strong HF content.
        // Musical transients have most energy in lower bands.
        // Apply a simple first-difference high-pass to isolate click energy.
        double hfEnergy = 0;
        double fullEnergy = 0;
        int energySamples = 0;

        int energyWindow = Math.Max(spanLength * 3, 8);
        int energyStart = Math.Max(1, start - energyWindow);
        int energyEnd = Math.Min(samples.Length - 1, end + energyWindow);

        for (int i = energyStart; i < energyEnd; i++)
        {
            double s = samples[i];
            fullEnergy += s * s;

            // First-difference = crude high-pass (emphasizes fast changes)
            if (i > energyStart)
            {
                double diff = samples[i] - samples[i - 1];
                hfEnergy += diff * diff;
            }
            energySamples++;
        }

        double hfRatio = energySamples > 0
            ? hfEnergy / Math.Max(1e-12, fullEnergy)
            : 0;

        double hfScore;
        if (localContrast)
        {
            // The same measure taken inside the candidate and in the audio either side of it, and
            // scored on the difference. Asking only "is there fast movement here" is a question
            // about the material: a chime, a cymbal or a sibilant answers yes wherever you ask.
            // Asking whether this event moves faster than its own surroundings is a question about
            // the event.
            double insideHf = 0, insideFull = 0;
            for (int i = start; i < end; i++)
            {
                double sample = samples[i];
                insideFull += sample * sample;
                double difference = samples[i] - samples[i - 1];
                insideHf += difference * difference;
            }
            double outsideHf = 0, outsideFull = 0;
            for (int i = energyStart + 1; i < energyEnd; i++)
            {
                if (i >= start && i < end) continue;
                double sample = samples[i];
                outsideFull += sample * sample;
                double difference = samples[i] - samples[i - 1];
                outsideHf += difference * difference;
            }

            double insideRatio = insideHf / Math.Max(1e-12, insideFull);
            double outsideRatio = outsideHf / Math.Max(1e-12, outsideFull);
            double contrast = insideRatio / Math.Max(1e-6, outsideRatio);
            hfScore = Math.Clamp((contrast - 1.5) / 4.0, 0.0, 1.0);
        }
        else
        {
            // Clicks have very high HF ratio (fast change dominates).
            // Musical transients have lower HF ratio (sustained energy).
            hfScore = Math.Clamp((hfRatio - 0.15) / 0.85, 0.0, 1.0);
        }

        // ── 3. Return-to-baseline check ───────────────────────────
        // After a click, the waveform returns to near the pre-click level.
        // Musical transients continue to evolve.
        int recoveryWindow = Math.Max(spanLength * 2, 4);
        int recoveryEnd = Math.Min(samples.Length - 1, end + recoveryWindow);
        double preLevel = samples[left];
        double maxDeviation = 0;
        for (int i = end; i < recoveryEnd; i++)
        {
            double dev = Math.Abs(samples[i] - preLevel);
            if (dev > maxDeviation) maxDeviation = dev;
        }

        double clickAmplitude = 0;
        for (int i = start; i < end; i++)
            clickAmplitude = Math.Max(clickAmplitude, Math.Abs(samples[i] - preLevel));

        double recoveryScore;
        if (trendRelativeRecovery)
        {
            // What the audio was already doing before the candidate, measured the same way, so the
            // comparison is like for like. A defect leaves the two alike; an attack raises the
            // second. Dividing by the candidate's amplitude, as this used to, asked instead whether
            // the event was louder than the music - which a quiet click is not, however cleanly it
            // recovered.
            int lookBack = Math.Max(4, recoveryWindow);
            double beforeDeviation = 0;
            for (int i = Math.Max(1, start - lookBack); i < start; i++)
                beforeDeviation = Math.Max(beforeDeviation, Math.Abs(samples[i] - preLevel));

            double scaleFor = Math.Max(clickAmplitude, beforeDeviation);
            recoveryScore = scaleFor > 1e-9
                ? 1.0 - Math.Clamp((maxDeviation - beforeDeviation) / scaleFor, 0.0, 1.0)
                : 1.0;
        }
        else
        {
            double recoveryRatio = clickAmplitude > 1e-9
                ? maxDeviation / clickAmplitude
                : 1.0;
            recoveryScore = 1.0 - Math.Clamp(recoveryRatio, 0.0, 1.0);
        }

        // ── 4. Bipolar check ──────────────────────────────────────
        // Real clicks are often bipolar (positive then negative spike).
        // Check if the span contains a sign change relative to the baseline.
        bool hasSignChange = false;
        double firstSign = Math.Sign(samples[start] - preLevel);
        for (int i = start + 1; i < end; i++)
        {
            if (Math.Sign(samples[i] - preLevel) != firstSign && Math.Abs(samples[i] - preLevel) > clickAmplitude * 0.3)
            {
                hasSignChange = true;
                break;
            }
        }
        double bipolarBonus = hasSignChange ? 0.15 : 0.0;

        // ── 5. Peak-to-RMS ratio in the span ──────────────────────
        // Clicks have very high peak-to-RMS ratio (single spike).
        // Musical content has lower ratio (more sustained energy).
        double spanRms = 0;
        for (int i = start; i < end; i++)
            spanRms += samples[i] * (double)samples[i];
        spanRms = Math.Sqrt(spanRms / Math.Max(1, spanLength));
        double peakToRms = spanRms > 1e-9 ? clickAmplitude / spanRms : 1.0;
        double peakScore = Math.Clamp((peakToRms - 1.5) / 6.0, 0.0, 1.0);

        // ── 6. Attack penalty (preserve musical transients) ───────
        double beforeRms = 0;
        int beforeCount = 0;
        int beforeWindow = Math.Min(energyWindow, start);
        for (int i = Math.Max(0, start - beforeWindow); i < start; i++)
        {
            beforeRms += samples[i] * (double)samples[i];
            beforeCount++;
        }
        beforeRms = Math.Sqrt(beforeRms / Math.Max(1, beforeCount));

        double afterRms = 0;
        int afterCount = 0;
        int afterWindow = Math.Min(energyWindow, samples.Length - end);
        for (int i = end; i < Math.Min(samples.Length, end + afterWindow); i++)
        {
            afterRms += samples[i] * (double)samples[i];
            afterCount++;
        }
        afterRms = Math.Sqrt(afterRms / Math.Max(1, afterCount));

        double attackRatio = afterRms / Math.Max(1e-6, beforeRms + localRms * 0.01);
        double attackPenalty = preserveTransients
            ? Math.Clamp((attackRatio - 2.0) / 4.0, 0.0, 0.5)
            : 0.0;

        // ── 7. Combined confidence ────────────────────────────────
        // Weighted toward HF ratio (best discriminator) and recovery (return to baseline).
        // 0.85 without the bipolar bonus, 1.00 with it - so a monopolar click cannot
        // score above 0.85 however cleanly it passes every other test. MinimumConfidence
        // defaults to 0.65, well under that, but a preset setting it higher would reject
        // every click that is not bipolar.
        double confidence = 0.35 * hfScore
                          + 0.30 * recoveryScore
                          + 0.20 * peakScore
                          + bipolarBonus;
        confidence *= 1.0 - attackPenalty;

        // Hard gates: must have reasonable HF content AND return to baseline
        if (hfScore < 0.15) return false;
        // The return-to-baseline test is the strongest thing separating a defect from an instrument,
        // and it was skipped for spans of three samples or fewer - which is exactly the shape a
        // sharp musical attack makes. Short candidates are now held to it too, a little more
        // loosely because there is less of them to measure.
        double recoveryFloor = trendRelativeRecovery
            ? minimumRecovery * (spanLength > 3 ? 1.0 : 0.6)
            : (spanLength > 3 ? 0.2 : 0.12);
        if (recoveryScore < recoveryFloor) return false;

        double severityRatio = clickAmplitude / Math.Max(1e-6, localRms);
        float severity = (float)Math.Clamp(1.0 - Math.Exp(-severityRatio * 0.5), 0.0, 1.0);
        var kind = spanLength <= maximumClickSamples
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

        // Three methods, best first. Janssen fits one model to the signal including the samples
        // being estimated and refines both together; the bidirectional predictor fits two models
        // that have never seen the gap and cross-fades their disagreement; the cubic bridge only
        // matches level and slope at the edges. Each falls through to the next when the geometry or
        // the audio will not support it, so a repair always happens.
        double[] reconstruction =
            Janssen.TryInterpolate(samples, start, end,
                JanssenOptions.For(gapLength, limit), out double[] janssen) ? janssen
            : gapLength >= 4 &&
              TryBidirectionalLinearPrediction(samples, start, end,
                  contextLength, limit, out double[] predicted) ? predicted
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
        // One scratch array for the whole recursion, swapped with the live set each
        // order step: cloning here allocated up to `order` arrays per call.
        var updated = new double[order + 1];
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

            Array.Copy(coefficients, updated, order + 1);
            for (int index = 1; index < currentOrder; index++)
                updated[index] = coefficients[index] +
                                 reflection * coefficients[currentOrder - index];
            updated[currentOrder] = reflection;
            (coefficients, updated) = (updated, coefficients);
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

    private static void ScanAutomaticClippedRuns(float[] samples, int channel,
        ClipPolarity polarity, float minimumPeak, double relativeTolerance,
        int minimumRun, int maximumGap, double minimumConfidence,
        List<ClippedPeakEvent> destination, CancellationToken cancellationToken)
    {
        float sign = polarity == ClipPolarity.Positive ? 1f : -1f;
        int i = 1;
        while (i < samples.Length - 2)
        {
            if ((i & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            float first = samples[i] * sign;
            float second = samples[i + 1] * sign;
            float seedLevel = Math.Max(first, second);
            float tolerance = Math.Max(2e-7f, seedLevel * (float)relativeTolerance);
            double equalityTolerance = Math.Max(1e-8, tolerance * 0.12);
            if (first < minimumPeak || second < minimumPeak ||
                Math.Abs(first - second) > equalityTolerance)
            {
                i++;
                continue;
            }

            int start = i;
            int lastHigh = i + 1;
            int highCount = 2;
            int peakSample = first >= second ? i : i + 1;
            float observedPeak = seedLevel;
            double levelSum = first + second;
            int j = i + 2;
            while (j < samples.Length - 1)
            {
                float signedSample = samples[j] * sign;
                float runningLevel = (float)(levelSum / highCount);
                if (signedSample >= minimumPeak &&
                    Math.Abs(signedSample - runningLevel) <= tolerance * 1.5f)
                {
                    lastHigh = j;
                    highCount++;
                    levelSum += signedSample;
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
            float plateauLevel = Math.Max(observedPeak, (float)(levelSum / highCount));
            tolerance = Math.Max(2e-7f, plateauLevel * (float)relativeTolerance);
            if (highCount >= minimumRun && end < samples.Length &&
                TryCreateClippedPeakEvent(samples, channel, start, end, peakSample,
                    polarity, plateauLevel - tolerance, plateauLevel, tolerance,
                    true, minimumRun, out var clippedEvent) &&
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

    /// <summary>
    /// How far the shoulders can be trusted to say where the peak went, from 0 (not at all) to 1.
    /// </summary>
    /// <remarks>
    /// The trend is the net rise across the shoulder window and the roughness is the mean absolute
    /// second difference over it — the part of the movement that is not going anywhere. A smooth
    /// approach to the rail is nearly all trend and scores near 1, leaving tonal material exactly as
    /// it was; a shoulder whose sample-to-sample movement is mostly high harmonics and noise scores
    /// low, and the arch stays near the rail it can prove.
    /// </remarks>
    private static double ShoulderReliability(float[] samples, int left, int right, int predictionSamples)
    {
        double trend = 0, roughness = 0;
        int counted = 0;

        for (int side = 0; side < 2; side++)
        {
            int from = side == 0 ? left - predictionSamples : right;
            int to = side == 0 ? left : right + predictionSamples;
            if (from < 1 || to >= samples.Length - 1) continue;

            trend += Math.Abs(samples[to] - samples[from]);
            for (int i = from + 1; i < to; i++)
            {
                roughness += Math.Abs(samples[i + 1] - 2.0 * samples[i] + samples[i - 1]);
                counted++;
            }
        }

        if (counted == 0) return 1;
        roughness /= counted;
        // Per-sample terms either side of the comparison: the trend is spread over the window, and
        // the second difference is already per sample.
        double perSampleTrend = trend / Math.Max(1, predictionSamples);
        double total = perSampleTrend + roughness;
        return total <= 1e-12 ? 1 : Math.Clamp(perSampleTrend / total, 0, 1);
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

        // Extrapolate only as far as the shoulder is smooth enough to extrapolate from. The height
        // of the arch comes from the boundary slope carried across the gap, and on dense material
        // that slope is mostly high harmonics and noise rather than the underlying arc — so the
        // estimate reads a rough shoulder as a steep climb and builds a peak nothing supports.
        // Measured by position inside the plateau, the reconstruction beat leaving the rail alone
        // over the outer fifths and lost by a factor of two across the middle, overshooting the
        // truth on 8,166 samples against 4,901 under. The rail is a proven lower bound and the
        // overshoot is a guess, so when the guess is unreliable the arch shrinks back towards the
        // bound rather than committing to a height it cannot justify.
        // Weighted by how far the arch actually has to reach. A two-sample plateau is barely an
        // extrapolation at all and its shoulders bracket it closely, so distrusting them costs
        // accuracy for nothing — measured, shrinking those took 1 to 2 dB off percussive and
        // sustained material at every severity, where a rough shoulder is a genuine attack rather
        // than noise. The doubt belongs to the long plateaus, which is where dense material sits at
        // the severities this was built for.
        double reach = Math.Min(1.0, span / (double)Math.Max(1, 2 * predictionSamples));
        double trust = 1 - reach * (1 - ShoulderReliability(samples, left, right, predictionSamples));
        targetPeak = observedLevel + (targetPeak - observedLevel) * trust;

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

            // Never come back under what was recorded. A railed sample was *at least* the rail —
            // that is the one thing clipping actually tells us — and the arch is drawn between two
            // shoulders that both sit below it, so away from the centre, where the restoring bump
            // has died away, it can dip back under. That is not merely inaccurate, it is
            // inconsistent with the observation, and it lands further from the truth than leaving
            // the sample alone would have. Measured on percussive material it was the whole of the
            // regression: reconstructions of 0.672 and 0.696 against a 0.700 plateau.
            double recorded = samples[i];
            value = sign > 0 ? Math.Max(value, recorded) : Math.Min(value, recorded);

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

    /// <summary>
    /// Detect isolated amplitude outliers that curvature-based detection misses.
    /// Low-frequency thumps/pops don't produce sharp second differences but are
    /// clearly visible as amplitude spikes relative to the local envelope.
    /// </summary>
    private static void DetectAmplitudeOutliers(float[] samples, float[] curvature,
        int channel, int blockStart, int blockEnd, int sampleRate, double localRms,
        int maximumPopSamples, int maximumClickSamples, bool preserveTransients,
        double minimumConfidence, double outlierRatioThreshold, List<ClickEvent> events)

    {
        // Compute a simple moving-average envelope to find amplitude outliers
        int envelopeWindow = Math.Max(16, sampleRate / 200); // ~5ms
        int n = samples.Length;

        // Robust per-block reference level: the median absolute amplitude. A moving
        // RMS window inflates itself when a pop is long relative to the window; the
        // median does not (breakdown point 50%), and for sine-like programme
        // material it tracks the RMS within a few percent (scaled 1.13x to match).
        double referenceEnvelope = 0;
        {
            int source = Math.Max(0, blockStart);
            int blockLength = Math.Max(0, Math.Min(blockEnd, n) - source);
            if (blockLength > 0)
            {
                var absBlock = new float[blockLength];
                for (int j = 0; j < blockLength; j++) absBlock[j] = Math.Abs(samples[source + j]);
                Array.Sort(absBlock);
                double median = (blockLength & 1) != 0
                    ? absBlock[blockLength / 2]
                    : 0.5 * (absBlock[blockLength / 2 - 1] + absBlock[blockLength / 2]);
                // Floored against the block's own RMS. The median of |sample| is a fine envelope for
                // continuous music, where it sits near 0.6 of the RMS - but on material with
                // silence between the notes it collapses towards zero, and then every note onset
                // reads as an enormous outlier. That is measurable: undamaged Windows alarms
                // reported up to 45.9 events a second and clean speech 3.7, against 0.05 for
                // continuous classical and 0.22 for record transfers. The floor cannot bind on
                // continuous material, where median x 1.13 already exceeds it.
                referenceEnvelope = Math.Max(median * 1.13, localRms * 0.5);
            }
        }


        for (int i = Math.Max(blockStart, envelopeWindow); i < Math.Min(blockEnd, n - envelopeWindow); i++)
        {
            // Look for samples that are extreme outliers vs the robust block reference
            double absSample = Math.Abs(samples[i]);
            double outlierRatio = absSample / Math.Max(1e-9, referenceEnvelope + localRms * 0.1);


            // Sensitivity-scaled gate: 8x the local envelope at the lowest
            // sensitivity, relaxing to 3x at the top of the slider.
            if (outlierRatio < outlierRatioThreshold) continue;


            // Find the extent of the outlier. Use a lower threshold (1.5× reference)
            // to catch the opposite-polarity lobe of bipolar clicks.
            // Bound both walks the way the curvature path does: on a quiet block the
            // 1.5x test can be true for the rest of the file, and a span longer than
            // maximumPopSamples is discarded below anyway.
            int start = i;
            int expansionFloor = Math.Max(1, i - maximumPopSamples);
            while (start > expansionFloor && Math.Abs(samples[start - 1]) > referenceEnvelope * 1.5)
                start--;

            int end = i + 1;
            int expansionCeiling = Math.Min(n - 1, start + maximumPopSamples);
            while (end < expansionCeiling && Math.Abs(samples[end]) > referenceEnvelope * 1.5)
                end++;


            int spanLength = end - start;
            if (spanLength < 1 || spanLength > maximumPopSamples) continue;

            // Quick validation: must return to near-baseline after the spike.
            // Skip a short guard region to allow the opposite-polarity lobe to pass.
            int recoverySkip = Math.Max(2, spanLength);
            int recoveryStart = end + recoverySkip;
            int recoveryEnd = Math.Min(n - 1, recoveryStart + spanLength * 3);

            // Near the end of the buffer this range collapses, and an empty range left
            // maxPostDeviation at zero — which passed the return-to-baseline test
            // unconditionally. That test is the strongest thing separating a defect from
            // an instrument, so a musical attack in the last few milliseconds of a side
            // was being accepted as a pop.
            if (recoveryEnd - recoveryStart < 2) continue;

            double preLevel = start > 0 ? samples[start - 1] : 0;
            double maxPostDeviation = 0;
            for (int j = recoveryStart; j < recoveryEnd; j++)
                maxPostDeviation = Math.Max(maxPostDeviation, Math.Abs(samples[j] - preLevel));

            double clickAmp = 0;
            for (int j = start; j < end; j++)
                clickAmp = Math.Max(clickAmp, Math.Abs(samples[j] - preLevel));

            double recoveryRatio = clickAmp > 1e-9 ? maxPostDeviation / clickAmp : 1.0;
            if (recoveryRatio > 0.5) continue; // doesn't return to baseline

            // Check for musical attack: if RMS after is much higher than before, skip
            double beforeRms = 0;
            int beforeCount = 0;
            for (int j = Math.Max(0, start - envelopeWindow); j < start; j++)
            {
                beforeRms += samples[j] * (double)samples[j];
                beforeCount++;
            }
            beforeRms = Math.Sqrt(beforeRms / Math.Max(1, beforeCount));

            double afterRms = 0;
            int afterCount = 0;
            for (int j = end; j < Math.Min(n, end + envelopeWindow); j++)
            {
                afterRms += samples[j] * (double)samples[j];
                afterCount++;
            }
            afterRms = Math.Sqrt(afterRms / Math.Max(1, afterCount));

            if (preserveTransients && afterRms > beforeRms * 2.5) continue;

            // High confidence — amplitude outliers are almost always real defects
            double confidence = Math.Clamp(0.65 + (outlierRatio - outlierRatioThreshold) / 20.0, 0.0, 1.0);

            if (confidence < minimumConfidence) continue;

            int peakSample = start;
            float peakAmp = Math.Abs(samples[start]);
            for (int j = start; j < end; j++)
            {
                if (Math.Abs(samples[j]) > peakAmp)
                {
                    peakAmp = Math.Abs(samples[j]);
                    peakSample = j;
                }
            }

            float severity = (float)Math.Clamp((outlierRatio - 5.0) / 15.0, 0.0, 1.0);
            var kind = spanLength <= maximumClickSamples
                ? ImpulseDefectKind.Click
                : ImpulseDefectKind.Pop;

            var clickEvent = new ClickEvent(channel, start, end, peakSample, kind,
                (float)confidence, severity, samples[peakSample], 0);

            AddOrMergeClickEvent(events, clickEvent, maximumClickSamples);

            // Skip past this event
            i = end + envelopeWindow;
        }
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

    // ── Advanced click repair: FFT-based spectral interpolation ──

    // ── Advanced declipping: cubic spline reconstruction ──

}
