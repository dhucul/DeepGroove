namespace WaveLab.Audio.Dsp;

/// <summary>WSOLA time stretching and (via resampling) pitch shifting.</summary>
public static class TimeStretch
{
    /// <summary>Stretch duration by <paramref name="factor"/> (2.0 = twice as long) without changing pitch.</summary>
    /// <remarks>
    /// The correlation search makes this one of the slowest tools in the app on a whole side, which is
    /// why it reports progress and can be stopped: the output buffer is only handed back on success,
    /// so cancelling leaves the document untouched.
    /// </remarks>
    public static float[][] Stretch(IReadOnlyList<float[]> channels, int sampleRate, double factor,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!double.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        if (channels.Count == 0) return [];

        ArgumentNullException.ThrowIfNull(channels[0]);
        int n = channels[0].Length;
        for (int channel = 1; channel < channels.Count; channel++)
        {
            ArgumentNullException.ThrowIfNull(channels[channel]);
            if (channels[channel].Length != n)
                throw new ArgumentException("All channel buffers must have the same length.", nameof(channels));
        }

        int chCount = channels.Count;
        if (n == 0)
        {
            var empty = new float[chCount][];
            for (int channel = 0; channel < chCount; channel++) empty[channel] = [];
            return empty;
        }

        factor = Math.Clamp(factor, 0.25, 4.0);

        int segment = Math.Max(256, (int)(sampleRate * 0.050));   // 50 ms
        int overlap = segment / 2;
        int synHop = segment - overlap;
        double anaHop = synHop / factor;
        int search = Math.Max(16, (int)(sampleRate * 0.008));     // ±8 ms

        // Keep only the real mono guide; correlation treats indices beyond its end as zero.
        var guide = new float[n];
        for (int i = 0; i < n; i++)
        {
            float v = 0;
            for (int c = 0; c < chCount; c++) v += channels[c][i];
            guide[i] = v / chCount;
        }

        double roundedTargetLength = Math.Round(n * factor);
        if (roundedTargetLength > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(factor), "The stretched audio would exceed the maximum array length.");
        int targetLen = Math.Max(1, (int)roundedTargetLength);
        var output = new float[chCount][];
        for (int c = 0; c < chCount; c++) output[c] = new float[targetLen];
        var overlapGuide = new float[overlap];

        var fadeIn = new float[overlap];
        var fadeOut = new float[overlap];
        for (int i = 0; i < overlap; i++)
        {
            double t = (double)i / overlap;
            fadeIn[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * t));
            fadeOut[i] = 1 - fadeIn[i];
        }

        int outPos = 0;
        double srcPos = 0;
        bool first = true;
        int sinceReport = 0;
        progress?.Report(0);

        while (outPos < targetLen && (int)srcPos < n)
        {
            // Checked per synthesis frame rather than per sample: a frame is tens of milliseconds of
            // audio, so this is responsive without putting an interlocked read in the inner loop.
            if (++sinceReport >= 32)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)outPos / targetLen);
                sinceReport = 0;
            }

            int ideal = (int)srcPos;
            int best = ideal;

            if (!first)
            {
                double bestScore = double.NegativeInfinity;
                int lo = Math.Max(0, ideal - search);
                int hi = ideal + search;
                for (int cand = lo; cand <= hi; cand += 4)
                {
                    double score = 0;
                    for (int i = 0; i < overlap; i += 8)
                    {
                        int guideIndex = cand + i;
                        if (guideIndex < n) score += overlapGuide[i] * guide[guideIndex];
                    }
                    if (score > bestScore) { bestScore = score; best = cand; }
                }
            }

            for (int c = 0; c < chCount; c++)
            {
                var src = channels[c];
                var dst = output[c];
                for (int i = 0; i < segment; i++)
                {
                    int si = best + i;
                    int oi = outPos + i;
                    if (oi >= targetLen) break;
                    float s = si < n ? src[si] : 0f; // zero-padded tail
                    if (i < overlap && !first)
                        dst[oi] = dst[oi] * fadeOut[i] + s * fadeIn[i];
                    else
                        dst[oi] = s;
                }
            }
            for (int i = 0; i < overlap; i++)
            {
                int sourceIndex = best + overlap + i;
                overlapGuide[i] = sourceIndex < n ? guide[sourceIndex] : 0f;
            }

            first = false;
            outPos += synHop;
            srcPos += anaHop;
        }

        progress?.Report(1);
        return output;
    }

    /// <summary>Shift pitch by semitones (+cents) keeping duration: WSOLA stretch then windowed-sinc resample.</summary>
    public static float[][] PitchShift(IReadOnlyList<float[]> channels, int sampleRate, double semitones,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!double.IsFinite(semitones)) throw new ArgumentOutOfRangeException(nameof(semitones));
        if (semitones is < -24.0 or > 24.0)
            throw new ArgumentOutOfRangeException(
                nameof(semitones), semitones, "Pitch shift must be between -24 and +24 semitones.");

        int sampleCount = -1;
        for (int channel = 0; channel < channels.Count; channel++)
        {
            ArgumentNullException.ThrowIfNull(channels[channel]);
            if (sampleCount < 0)
                sampleCount = channels[channel].Length;
            else if (channels[channel].Length != sampleCount)
                throw new ArgumentException("All channel buffers must have the same length.", nameof(channels));
        }

        double requestedFactor = Math.Pow(2, semitones / 12.0);
        if (Math.Abs(requestedFactor - 1) < 1e-4)
        {
            var copy = new float[channels.Count][];
            for (int c = 0; c < channels.Count; c++) copy[c] = (float[])channels[c].Clone();
            return copy;
        }

        double exactVirtualRate = sampleRate * requestedFactor;
        if (!double.IsFinite(exactVirtualRate) || exactVirtualRate < 1 || exactVirtualRate > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(semitones), semitones,
                "The requested pitch shift cannot be represented at this sample rate.");
        }

        int virtualRate = checked((int)Math.Round(exactVirtualRate));
        if (virtualRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(semitones), semitones,
                "The requested pitch shift cannot be represented at this sample rate.");
        }

        // An integer sample rate can only approximate most musical intervals. Derive
        // the stretch from that representable rate so both stages use one exact ratio
        // and the resample restores the original duration instead of drifting.
        double pitchFactor = virtualRate / (double)sampleRate;

        // Two stages of very different cost share one bar. The stretch dominates, so it owns the
        // first three quarters and the resample the last.
        var stretched = Stretch(channels, sampleRate, pitchFactor, cancellationToken,
            progress is null ? null : new ScaledProgress(progress, 0, 0.75));
        // Play the stretched audio "faster" by the same factor.
        return Resampler.Resample(stretched, virtualRate, sampleRate, cancellationToken,
            progress is null ? null : new ScaledProgress(progress, 0.75, 1));
    }

    /// <summary>Maps a stage's own 0..1 onto a slice of an overall bar.</summary>
    private sealed class ScaledProgress(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) =>
            inner.Report(from + Math.Clamp(value, 0, 1) * (to - from));
    }
}
