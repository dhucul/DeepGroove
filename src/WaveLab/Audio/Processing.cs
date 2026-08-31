using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>A short defect isolated inside a rough interpolation selection.</summary>
public readonly record struct InterpolateRepairTarget(
    int Start, int Count, float Confidence, float Severity);

/// <summary>Destructive processing operations. Each op runs on a copied range and commits via ReplaceRange (undoable).</summary>
public static class Processing
{
    /// <summary>
    /// A selection no longer than this is treated as sample-accurate. A longer selection is a rough
    /// search area from which <see cref="LocateInterpolateRepairTarget"/> isolates one short defect.
    /// </summary>
    public const double MaximumDirectInterpolationMilliseconds = 5.0;

    /// <summary>Longest rough area searched automatically before the UI zooms in for refinement.</summary>
    public const double MaximumAutomaticInterpolationSearchSeconds = 10.0;

    public static int MaximumDirectInterpolationSamples(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return Math.Max(1,
            (int)Math.Round(sampleRate * MaximumDirectInterpolationMilliseconds / 1000.0));
    }

    public static int MaximumAutomaticInterpolationSearchSamples(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return (int)Math.Min(int.MaxValue,
            Math.Round(sampleRate * MaximumAutomaticInterpolationSearchSeconds));
    }

    public static void Gain(AudioDocument doc, int start, int count, double db) =>
        ApplyGain(doc, start, count, db, $"Gain {db:+0.0;-0.0} dB");

    /// <summary>
    /// The name a matched-loudness edit commits under.
    /// </summary>
    /// <remarks>
    /// "Gain +2.3 dB" is true and cannot be read back a month later as a loudness decision, whereas
    /// the level matched to can. One place, so the dialog and the edit cannot describe the same
    /// change differently.
    /// </remarks>
    public static string MatchLoudnessName(double gainDb, double targetLufs) =>
        $"Match Loudness {targetLufs:0.0} LUFS ({gainDb:+0.0;-0.0} dB)";

    /// <summary>
    /// Scales a whole document by a decided gain, returning the result rather than committing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gain arrives already decided and already true-peak limited — see
    /// <see cref="Dsp.LoudnessMatch"/>, which is where the measuring and the arithmetic live.
    /// </para>
    /// <para>
    /// Pure and off-thread by design, and it allocates <b>once</b>: the scale is applied on the way
    /// into the new buffer rather than copying and then multiplying. Committed with
    /// <see cref="AudioDocument.ReplaceAllOwned"/>, which retains the outgoing arrays by reference,
    /// the whole edit costs one copy of the document instead of the three
    /// <see cref="Apply"/> would — the same correction the channel tools already had to make, for
    /// the same reason: a side of vinyl is a few hundred megabytes a copy, and this runs over every
    /// open tab at once.
    /// </para>
    /// </remarks>
    public static float[][] MatchLoudnessData(
        IReadOnlyList<float[]> channels, double gainDb, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        float g = (float)Math.Pow(10, gainDb / 20.0);
        var result = new float[channels.Count][];
        for (int c = 0; c < channels.Count; c++)
        {
            var source = channels[c];
            var scaled = new float[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                scaled[i] = source[i] * g;
            }
            result[c] = scaled;
        }
        return result;
    }

    private static void ApplyGain(AudioDocument doc, int start, int count, double db, string name)
    {
        float g = (float)Math.Pow(10, db / 20.0);
        Apply(doc, start, count, name, data =>
        {
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++) ch[i] *= g;
        });
    }

    /// <summary>Scales the range so its loudest sample reaches <paramref name="targetDbfs"/>.</summary>
    /// <remarks>
    /// <b>The peak is measured in place, before anything is copied.</b> <see cref="Apply"/> commits
    /// whatever its delegate leaves behind, so a delegate that returned early on silence still cost
    /// an undo entry and a dirty document for an edit that changed nothing — the same defect the
    /// Reduce Noise path was already fixed for, where the transform returned the untouched buffer
    /// instead of null. Deciding before the copy also skips the copy.
    /// </remarks>
    /// <returns>
    /// False when the range was silent, in which case nothing was written and no undo entry exists.
    /// </returns>
    public static bool Normalize(AudioDocument doc, int start, int count, double targetDbfs)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (count <= 0) return false;

        float peak = PeakOfRange(doc, start, count);
        if (peak <= 0) return false;

        float g = (float)(Math.Pow(10, targetDbfs / 20.0) / peak);
        Apply(doc, start, count, $"Normalize {targetDbfs:0.0} dBFS", data =>
        {
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++) ch[i] *= g;
        });
        return true;
    }

    /// <summary>The loudest sample magnitude in a range, read from the document rather than a copy.</summary>
    /// <remarks>
    /// The bounds are clamped rather than trusted: this runs before <see cref="Apply"/>, which is
    /// where <see cref="AudioDocument.CopyRange"/> would otherwise have validated them.
    /// </remarks>
    private static float PeakOfRange(AudioDocument doc, int start, int count)
    {
        var channels = doc.Channels;
        if (channels.Count == 0) return 0;
        int from = Math.Max(0, start);
        int to = Math.Min(channels[0].Length, start + count);
        float peak = 0;
        foreach (var ch in channels)
            for (int i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(ch[i]));
        return peak;
    }

    public static void FadeIn(AudioDocument doc, int start, int count, int curveType = 0) =>
        Fade(doc, start, count, $"Fade In ({CurveName(curveType)})", from: 0f, to: 1f, curveType);

    public static void FadeOut(AudioDocument doc, int start, int count, int curveType = 0) =>
        Fade(doc, start, count, $"Fade Out ({CurveName(curveType)})", from: 1f, to: 0f, curveType);

    public static void Crossfade(AudioDocument doc, int position, int overlapSamples)
    {
        if (overlapSamples < 8) return;
        int start = Math.Max(0, position - overlapSamples / 2);
        int end = Math.Min(doc.Length, position + overlapSamples / 2);
        int actualOverlap = end - start;
        if (actualOverlap < 8) return;

        Apply(doc, start, actualOverlap, "Crossfade", data =>
        {
            foreach (var ch in data)
            {
                int n = ch.Length;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / (n - 1);
                    // Equal-power crossfade curve
                    double fadeOut = Math.Cos(t * Math.PI / 2);
                    double fadeIn = Math.Sin(t * Math.PI / 2);
                    // The pair sums to unity in *power*, and one buffer holds both
                    // sides of the join. Summing the amplitudes instead would put a
                    // +3.01 dB bulge at the centre of the window and clip hot material.
                    ch[i] *= (float)Math.Sqrt(fadeOut * fadeOut + fadeIn * fadeIn);
                }
            }
        });
    }

    private static string CurveName(int curveType) => curveType switch
    {
        1 => "Linear",
        2 => "Logarithmic",
        3 => "Exponential",
        4 => "S-Curve",
        _ => "Equal Power",
    };

    private static void Fade(AudioDocument doc, int start, int count, string name, float from, float to, int curveType = 0)
    {
        Apply(doc, start, count, name, data =>
        {
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++)
                {
                    double t = ch.Length <= 1 ? 1 : (double)i / (ch.Length - 1);
                    double g = ComputeFadeCurve(t, from, to, curveType);
                    ch[i] *= (float)g;
                }
        });
    }

    private static double ComputeFadeCurve(double t, double from, double to, int curveType)
    {
        double g;
        switch (curveType)
        {
            case 1: // Linear
                g = from + (to - from) * t;
                break;
            case 2: // Logarithmic
                g = from + (to - from) * Math.Log(1 + 9 * t) / Math.Log(10);
                break;
            case 3: // Exponential
                g = from + (to - from) * (Math.Exp(3 * t) - 1) / (Math.Exp(3) - 1);
                break;
            case 4: // S-Curve
            {
                // Normalize the logistic to its finite interval. The raw curve is still about
                // 0.25% open at t=0 and 0.25% shy of unity at t=1, which leaves a discontinuity
                // at either end when the neighbouring audio is digital silence.
                double low = 1.0 / (1.0 + Math.Exp(6));
                double high = 1.0 / (1.0 + Math.Exp(-6));
                double raw = 1.0 / (1.0 + Math.Exp(-12 * (t - 0.5)));
                double s = (raw - low) / (high - low);
                g = from + (to - from) * s;
                break;
            }
            default: // Equal-power (sine/cosine amplitude law)
            {
                double curve = from + (to - from) * t;
                // Squaring this sine made the midpoint -6.02 dB. Equal-power fades meet at
                // -3.01 dB, because their squared amplitudes — their powers — sum to one.
                g = Math.Sin(curve * Math.PI / 2);
                break;
            }
        }
        return Math.Clamp(g, 0.0, 1.0);
    }

    public static void Reverse(AudioDocument doc, int start, int count) =>
        Apply(doc, start, count, "Reverse", data => { foreach (var ch in data) Array.Reverse(ch); });

    public static void RemoveDcOffset(AudioDocument doc, int start, int count)
    {
        Apply(doc, start, count, "Remove DC Offset", data =>
        {
            foreach (var ch in data)
            {
                if (ch.Length == 0) continue;
                double mean = 0;
                for (int i = 0; i < ch.Length; i++) mean += ch[i];
                mean /= ch.Length;
                for (int i = 0; i < ch.Length; i++) ch[i] -= (float)mean;
            }
        });
    }

    /// <summary>
    /// Reconstructs a selected defect from the clean samples on either side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model-based interpolator used by automatic de-click is tried first, because it can carry
    /// an oscillating waveform through the gap instead of flattening it into a straight line. When
    /// the material or available context cannot support a model, a bounded straight bridge is the
    /// safe fallback. Each channel uses its own context over the same sample span.
    /// </para>
    /// <para>
    /// At a file edge only one clean anchor exists; extending that value is safer than extrapolating
    /// a slope backwards or forwards through an unknown-length defect. A whole-file range has no
    /// anchor at all and is declined without creating an undo entry.
    /// </para>
    /// </remarks>
    /// <returns>True when the repair was committed; false when no usable boundary exists.</returns>
    public static bool InterpolateRepair(
        AudioDocument doc, int start, int count, string opName = "Interpolate Repair")
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(opName);
        if (start < 0 || start > doc.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0 || count > doc.Length - start)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return false;

        int end = start + count;
        bool hasLeft = start > 0;
        bool hasRight = end < doc.Length;
        if (!hasLeft && !hasRight) return false;

        var replacement = new float[doc.ChannelCount][];
        for (int channel = 0; channel < doc.ChannelCount; channel++)
        {
            float[] source = doc.Channels[channel];
            var repaired = new float[count];
            if (hasLeft && hasRight)
            {
                float left = source[start - 1];
                float right = source[end];
                double contextPeak = Math.Max(Math.Abs(left), Math.Abs(right));
                int contextStart = Math.Max(0, start - 2_048);
                int contextEnd = Math.Min(source.Length, end + 2_048);
                for (int i = contextStart; i < start; i++)
                    contextPeak = Math.Max(contextPeak, Math.Abs(source[i]));
                for (int i = end; i < contextEnd; i++)
                    contextPeak = Math.Max(contextPeak, Math.Abs(source[i]));
                double limit = Math.Max(1e-6, contextPeak * 1.2);
                bool modelled = Janssen.TryInterpolate(source, start, end,
                    JanssenOptions.For(count, limit), out double[] reconstruction);
                double denominator = count + 1.0;

                for (int i = 0; i < count; i++)
                {
                    double t = (i + 1.0) / denominator;
                    double value = modelled ? reconstruction[i]
                        : (double)left + ((double)right - left) * t;
                    if (!double.IsFinite(value))
                        value = (double)left + ((double)right - left) * t;
                    repaired[i] = (float)Math.Clamp(value, -limit, limit);
                }
            }
            else
            {
                float anchor = hasLeft ? source[start - 1] : source[end];
                Array.Fill(repaired, anchor);
            }
            replacement[channel] = repaired;
        }

        doc.ReplaceRange(start, count, replacement, opName);
        return true;
    }

    /// <summary>
    /// Finds the strongest short impulsive defect inside a rough selection without changing audio.
    /// </summary>
    /// <remarks>
    /// Fifty milliseconds of clean context is included on both sides so the detector can distinguish
    /// a click that returns to the waveform from a musical attack. The returned range is always
    /// clamped to the selection: a rough selection grants permission to search, not to edit outside
    /// it. Coincident channel detections are joined into one shared repair span.
    /// </remarks>
    public static InterpolateRepairTarget? LocateInterpolateRepairTarget(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        int selectionStart,
        int selectionCount,
        CancellationToken cancellationToken = default,
        IProgress<RestorationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels.Count == 0)
            throw new ArgumentException("At least one audio channel is required.", nameof(channels));
        int length = channels[0].Length;
        if (channels.Any(channel => channel == null || channel.Length != length))
            throw new ArgumentException("All audio channels must have the same length.", nameof(channels));
        if (selectionStart < 0 || selectionStart > length)
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (selectionCount <= 0 || selectionCount > length - selectionStart)
            throw new ArgumentOutOfRangeException(nameof(selectionCount));
        if (selectionCount > MaximumAutomaticInterpolationSearchSamples(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(selectionCount),
                "The rough interpolation search area is too long.");

        int selectionEnd = selectionStart + selectionCount;
        int padding = Math.Max(2_048, (int)Math.Round(sampleRate * 0.050));
        int analysisStart = Math.Max(0, selectionStart - padding);
        int analysisEnd = Math.Min(length, selectionEnd + padding);
        int analysisLength = analysisEnd - analysisStart;
        var window = new float[channels.Count][];
        for (int channel = 0; channel < channels.Count; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window[channel] = channels[channel].AsSpan(analysisStart, analysisLength).ToArray();
        }

        ClickAnalysisResult analysis = Restoration.AnalyzeClicks(window, sampleRate,
            new ClickAnalysisOptions { Sensitivity = 10 }, cancellationToken, progress);

        // Anchor event choice to the strongest raw discontinuity in the area the user marked. The
        // detector can return several legitimate candidates around one pop (its two edges plus a
        // predictive-residual candidate); severity alone may choose the ringing after the defect
        // rather than the edge that began it. Proximity to the strongest curvature keeps the manual
        // command centred on what the user heard, while the detector still decides whether the
        // candidate behaves like a defect rather than a musical attack.
        int sharpestSample = selectionStart;
        double sharpestCurvature = -1;
        for (int channel = 0; channel < channels.Count; channel++)
        {
            float[] source = channels[channel];
            int from = Math.Max(1, selectionStart);
            int to = Math.Min(length - 1, selectionEnd);
            for (int i = from; i < to; i++)
            {
                if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                double curvature = Math.Abs(source[i] - 0.5 * (source[i - 1] + source[i + 1]));
                if (curvature <= sharpestCurvature) continue;
                sharpestCurvature = curvature;
                sharpestSample = i;
            }
        }

        ClickEvent? strongest = null;
        int strongestDistance = int.MaxValue;
        int directLimit = MaximumDirectInterpolationSamples(sampleRate);
        foreach (ClickEvent candidate in analysis.Events)
        {
            int peak = analysisStart + candidate.PeakSample;
            if (peak < selectionStart || peak >= selectionEnd) continue;
            int candidateStart = analysisStart + candidate.StartSample;
            int candidateEnd = analysisStart + candidate.EndSample;
            int distance = sharpestSample < candidateStart ? candidateStart - sharpestSample
                : sharpestSample >= candidateEnd ? sharpestSample - candidateEnd + 1
                : 0;
            bool nearSharpest = distance <= directLimit;
            bool currentNearSharpest = strongestDistance <= directLimit;
            if (strongest is not { } current
                || nearSharpest && !currentNearSharpest
                || nearSharpest == currentNearSharpest && distance < strongestDistance
                || nearSharpest == currentNearSharpest && distance == strongestDistance
                && (candidate.Severity > current.Severity
                    || candidate.Severity == current.Severity
                    && candidate.Confidence > current.Confidence
                    || candidate.Severity == current.Severity
                    && candidate.Confidence == current.Confidence
                    && Math.Abs(candidate.PeakAmplitude) > Math.Abs(current.PeakAmplitude)))
            {
                strongest = candidate;
                strongestDistance = distance;
            }
        }
        if (strongest is not { } chosen) return null;

        int repairStart = analysisStart + chosen.StartSample;
        int repairEnd = analysisStart + chosen.EndSample;
        int expandedStart = Math.Min(repairStart, sharpestSample);
        int expandedEnd = Math.Max(repairEnd, sharpestSample + 1);
        if (strongestDistance <= directLimit && expandedEnd - expandedStart <= directLimit)
        {
            repairStart = expandedStart;
            repairEnd = expandedEnd;
        }
        repairStart = Math.Max(selectionStart, repairStart);
        repairEnd = Math.Min(selectionEnd, repairEnd);
        int mergeDistance = Math.Max(1, (int)Math.Round(sampleRate * 0.00025));
        (repairStart, repairEnd) = MergeInterpolateRepairEvents(
            analysis.Events, analysisStart, selectionStart, selectionEnd,
            repairStart, repairEnd, directLimit, mergeDistance);

        repairStart = Math.Max(selectionStart, repairStart);
        repairEnd = Math.Min(selectionEnd, repairEnd);
        return repairEnd > repairStart
            ? new InterpolateRepairTarget(
                repairStart, repairEnd - repairStart, chosen.Confidence, chosen.Severity)
            : null;
    }

    /// <summary>
    /// Expands one detected span through every connected event, independent of event ordering.
    /// </summary>
    internal static (int Start, int End) MergeInterpolateRepairEvents(
        IReadOnlyList<ClickEvent> events,
        int analysisStart,
        int selectionStart,
        int selectionEnd,
        int repairStart,
        int repairEnd,
        int directLimit,
        int mergeDistance)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (selectionStart < 0 || selectionEnd < selectionStart)
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (repairStart < selectionStart || repairEnd < repairStart || repairEnd > selectionEnd)
            throw new ArgumentOutOfRangeException(nameof(repairStart));
        if (directLimit <= 0) throw new ArgumentOutOfRangeException(nameof(directLimit));
        if (mergeDistance < 0) throw new ArgumentOutOfRangeException(nameof(mergeDistance));

        bool expanded;
        do
        {
            expanded = false;
            foreach (ClickEvent candidate in events)
            {
                int peak = analysisStart + candidate.PeakSample;
                if (peak < selectionStart || peak >= selectionEnd) continue;
                int candidateStart = Math.Max(
                    selectionStart, analysisStart + candidate.StartSample);
                int candidateEnd = Math.Min(
                    selectionEnd, analysisStart + candidate.EndSample);
                if (candidateEnd <= candidateStart) continue;
                if ((long)candidateStart > (long)repairEnd + mergeDistance
                    || (long)candidateEnd < (long)repairStart - mergeDistance) continue;
                int mergedStart = Math.Min(repairStart, candidateStart);
                int mergedEnd = Math.Max(repairEnd, candidateEnd);
                if (mergedEnd - mergedStart > directLimit
                    || mergedStart == repairStart && mergedEnd == repairEnd) continue;
                repairStart = mergedStart;
                repairEnd = mergedEnd;
                expanded = true;
            }
        }
        while (expanded);
        return (repairStart, repairEnd);
    }

    public static void InsertSilence(AudioDocument doc, int at, double seconds)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds, "The silence duration must be a finite, non-negative number of seconds.");
        }

        // Rounded as a double and bounded before it is cast: a duration long enough to
        // overflow int reached `new float[n]` as a negative length.
        double exact = Math.Round(seconds * doc.SampleRate);
        long limit = Math.Min(Array.MaxLength, Array.MaxLength - (long)doc.Length);
        if (exact > limit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds, "The silence is longer than this file can grow to hold.");
        }

        var n = (int)exact;
        if (n == 0) return;
        int channels = doc.ChannelCount;
        var data = new float[channels][];
        for (int c = 0; c < channels; c++) data[c] = new float[n];
        doc.ReplaceRange(at, 0, data, "Insert Silence");
    }

    /// <summary>
    /// De-click an edit point: blend a short cubic bridge across the boundary so pasted/spliced
    /// joins have no discontinuity. Window is ±ms around the position.
    /// </summary>
    public static void SmoothEditPoint(AudioDocument doc, int position, double ms = 5)
    {
        int w = Math.Max(8, (int)(ms / 1000.0 * doc.SampleRate));
        int start = Math.Max(1, position - w);
        int end = Math.Min(doc.Length - 2, position + w);
        if (end - start < 8) return;

        Apply(doc, start, end - start, "Smooth Edit Point", data =>
        {
            foreach (var ch in data)
            {
                int n = ch.Length;
                float y0 = ch[0], y1 = ch[n - 1];
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / (n - 1);
                    float t2 = t * t, t3 = t2 * t;
                    // Smoothstep between the window's endpoints: monotone, and always
                    // bounded by [min(y0, y1), max(y0, y1)]. Scaling Hermite tangents
                    // by the window length turns a one-sample slope into a full-interval
                    // derivative and overshoots full scale by an order of magnitude.
                    float bridge = (2 * t3 - 3 * t2 + 1) * y0 + (-2 * t3 + 3 * t2) * y1;
                    // blend strongest at the centre (the actual edit point)
                    float weight = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * t);
                    ch[i] = ch[i] * (1 - weight) + bridge * weight;
                }
            }
        });
    }

    private static void Apply(AudioDocument doc, int start, int count, string name, Action<float[][]> op)
    {
        if (count <= 0) return;
        var data = doc.CopyRange(start, count);
        op(data);
        doc.ReplaceRange(start, count, data, name);
    }
}
