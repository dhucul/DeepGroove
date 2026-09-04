namespace WaveLab.Audio.Dsp;

/// <summary>A proposed, not yet applied, time/frequency repair for a short ringing disturbance.</summary>
public sealed record SpectralDefectCandidate(
    int StartSample, int EndSample, int PeakSample,
    double LowFrequency, double HighFrequency, double ContrastDb, double Score,
    int FftSize, int Hop)
{
    public SpectralMask CreateMask(int sampleRate) => SpectralMask.ForRegion(
        StartSample, EndSample, LowFrequency, HighFrequency, sampleRate, FftSize, Hop, feather: 1);
}

/// <summary>
/// Suggests a compact spectral selection inside an area the listener has marked. Unlike the
/// sample-curvature click detector, this looks for energy in a frequency band that briefly rises
/// above BOTH of its neighbouring passages, then subsides. It never changes audio; a short musical
/// sound can also satisfy these conditions, so the caller must present the selection before Heal.
/// </summary>
public static class SpectralDefectFinder
{
    public const double MaximumSearchSeconds = 10;
    private const double MinimumContrastDb = 9;
    private const double MinimumScore = 1.5;

    public static SpectralDefectCandidate? FindStrongest(
        IReadOnlyList<float[]> channels, int sampleRate, int selectionStart, int selectionCount,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate < 8_000 || sampleRate > 384_000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels.Count == 0 || channels[0] == null)
            throw new ArgumentException("Audio needs at least one channel.", nameof(channels));
        int length = channels[0].Length;
        if (channels.Any(c => c == null || c.Length != length))
            throw new ArgumentException("Audio channels must have equal lengths.", nameof(channels));
        if (selectionStart < 0 || selectionStart > length)
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (selectionCount <= 0 || selectionCount > length - selectionStart ||
            selectionCount / (double)sampleRate > MaximumSearchSeconds)
            throw new ArgumentOutOfRangeException(nameof(selectionCount));
        cancellationToken.ThrowIfCancellationRequested();

        // Establish each channel's own baseline before combining any evidence. A sustained tone
        // on the right must not replace the quieter left channel's baseline and hide its defect.
        var candidates = new List<SpectralDefectCandidate>();
        for (int channel = 0; channel < channels.Count; channel++)
        {
            var channelProgress = progress is null ? null : new ChannelProgress(progress, channel, channels.Count);
            var candidate = FindChannel(channels[channel], sampleRate, selectionStart, selectionCount,
                cancellationToken, channelProgress);
            if (candidate is not null) candidates.Add(candidate);
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1);
        if (candidates.Count == 0) return null;

        var strongest = candidates.MaxBy(c => c.Score)!;
        int start = strongest.StartSample, stop = strongest.EndSample;
        double low = strongest.LowFrequency, high = strongest.HighFrequency;
        foreach (var candidate in candidates)
        {
            // Only combine independently accepted evidence for the same event. Separate sounds
            // in separate channels are not one broad patch just because the search contains both.
            if (Math.Abs(candidate.PeakSample - strongest.PeakSample) > sampleRate * .010 ||
                candidate.EndSample <= strongest.StartSample || candidate.StartSample >= strongest.EndSample ||
                candidate.HighFrequency <= strongest.LowFrequency || candidate.LowFrequency >= strongest.HighFrequency)
                continue;
            start = Math.Min(start, candidate.StartSample);
            stop = Math.Max(stop, candidate.EndSample);
            low = Math.Min(low, candidate.LowFrequency);
            high = Math.Max(high, candidate.HighFrequency);
        }
        return strongest with { StartSample = start, EndSample = stop, LowFrequency = low, HighFrequency = high };
    }

    private sealed class ChannelProgress(IProgress<double> target, int channel, int count) : IProgress<double>
    {
        public void Report(double value) => target.Report((channel + value) / count);
    }

    private static SpectralDefectCandidate? FindChannel(float[] channel, int sampleRate,
        int selectionStart, int selectionCount, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int length = channel.Length;

        // Keep the time resolution near 12 ms at other sample rates, not 512 samples regardless
        // of the clock. The same grid travels with the result all the way through spectral Heal.
        int fft = 1 << (int)Math.Round(Math.Log2(sampleRate * (512.0 / 44_100)));
        fft = Math.Clamp(fft, 128, 4096);
        int hop = fft / 8, half = fft / 2, bins = half + 1;
        int inner = Math.Max(2, (int)Math.Round(.012 * sampleRate / hop));
        int outer = Math.Max(inner + 2, (int)Math.Round(.055 * sampleRate / hop));
        int end = selectionStart + selectionCount;
        int firstFrame = Math.Max((half + hop - 1) / hop, selectionStart / hop - outer - 2);
        int lastFrame = Math.Min((length - half) / hop, (end + hop - 1) / hop + outer + 2);
        int frames = lastFrame - firstFrame + 1;
        if (frames <= 2 * outer + 1) return null; // no independent context on both sides

        // Channels are processed sequentially; only this channel's spectrum plane is retained.
        var power = new float[checked(frames * bins)];
        var totals = new double[frames];
        var window = new double[fft];
        for (int i = 0; i < fft; i++) window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (fft - 1));
        var re = new double[fft];
        var im = new double[fft];
        var bandPower = new double[bins];
        double normalization = 4.0 / (fft * (double)fft);
        for (int frame = 0; frame < frames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int from = (firstFrame + frame) * hop - half;
            for (int i = 0; i < fft; i++)
            {
                float value = channel[from + i];
                if (!float.IsFinite(value))
                    throw new ArgumentException("Audio contains a non-finite sample.", nameof(channel));
                re[i] = value * window[i];
            }
            Array.Clear(im);
            Fft.Forward(re, im);
            for (int bin = 0; bin < bins; bin++)
                bandPower[bin] = (re[bin] * re[bin] + im[bin] * im[bin]) * normalization;
            for (int bin = 0; bin < bins; bin++)
            {
                // Smooth adjacent bins before comparison: a note drifting across a bin boundary
                // should not look like a new sound emerging from an empty bin.
                double value = (bandPower[bin] + (bin > 0 ? bandPower[bin - 1] : 0) +
                    (bin + 1 < bins ? bandPower[bin + 1] : 0)) / 3;
                power[frame * bins + bin] = (float)value;
                totals[frame] += value;
            }
            if ((frame & 63) == 0) progress?.Report(.6 * frame / frames);
        }

        var before = new double[bins];
        var after = new double[bins];
        var baseline = new double[bins];
        int contextFrames = outer - inner;
        for (int i = 0; i < contextFrames; i++) AddRow(before, i, 1);
        for (int i = outer + inner; i < outer * 2; i++) AddRow(after, i, 1);
        double binHz = sampleRate / (double)fft;
        int firstBin = Math.Max(1, (int)Math.Ceiling(500 / binHz));
        int lastBin = Math.Min(bins - 2, (int)Math.Floor(Math.Min(12_000, sampleRate * .45) / binHz));
        int maximumWidth = Math.Max(2, (int)Math.Floor(.025 * sampleRate / hop));
        SpectralDefectCandidate? best = null;

        for (int frame = outer; frame < frames - outer; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int peak = (firstFrame + frame) * hop;
            if (peak >= selectionStart && peak < end && totals[frame] > 1e-12)
            {
                int offset = frame * bins;
                for (int bin = 0; bin < bins; bin++)
                    baseline[bin] = Math.Max(Math.Max(before[bin], after[bin]) / contextFrames,
                        Math.Max(1e-18, totals[frame] * 1e-4));
                // Evaluate every seed through the same gates. The largest contrast ratio may
                // belong to a weak or invalid band and must not suppress a valid candidate.
                for (int seed = firstBin; seed <= lastBin; seed++)
                {
                    double ratio = power[offset + seed] / baseline[seed];
                    double contrastDb = 10 * Math.Log10(Math.Max(1e-18, ratio));
                    double excess = power[offset + seed] - baseline[seed];
                    double score = contrastDb * Math.Sqrt(Math.Max(0, excess / totals[frame]));
                    if (contrastDb >= MinimumContrastDb && score >= MinimumScore && score > (best?.Score ?? 0))
                    {
                        int left = frame, right = frame;
                        while (left > 0 && frame - left <= maximumWidth &&
                               power[(left - 1) * bins + seed] > baseline[seed] * 2) left--;
                        while (right + 1 < frames && right - left <= maximumWidth &&
                               power[(right + 1) * bins + seed] > baseline[seed] * 2) right++;
                        if (right - left + 1 <= maximumWidth)
                        {
                            int low = seed, high = seed;
                            while (low > firstBin && IsExcess(low - 1)) low--;
                            while (high < lastBin && IsExcess(high + 1)) high++;
                            // Leave room for both the spectral taper and the ringing tail. The bounds
                            // stay inside the user's search; the WOLA synthesis has its usual local skirt.
                            int start = Math.Max(selectionStart, (firstFrame + left - 2) * hop);
                            int stop = Math.Min(end, (firstFrame + right + 4) * hop);
                            double lowHz = Math.Max(0, (low - 2) * binHz);
                            double highHz = Math.Min(sampleRate / 2.0, (high + 4) * binHz);
                            // This is the narrow-band finder, not an instruction to select a whole
                            // drum attack. Broad simultaneous energy belongs to the ordinary tools.
                            double occupiedHz = (high - low + 1) * binHz;
                            bool concentrated = occupiedHz <= Math.Max(12 * binHz, seed * binHz * .85);
                            double bandExcess = 0, totalExcess = 0;
                            for (int bin = firstBin; bin <= lastBin; bin++)
                            {
                                double added = Math.Max(0, power[offset + bin] - baseline[bin]);
                                totalExcess += added;
                                if (bin >= low - 2 && bin <= high + 4) bandExcess += added;
                            }
                            // A random bump within a broadband attack can make a locally narrow peak.
                            // Most of the NEW energy must actually belong to the proposed band.
                            concentrated &= bandExcess >= .55 * totalExcess;
                            // A sustained note onset can have brief spectral side lobes even though
                            // its fundamental continues. Compare the whole proposed band at both
                            // anchors so those lobes cannot masquerade as a recovered ringing defect.
                            double earlier = 0, later = 0;
                            for (int bin = Math.Max(0, low - 2); bin <= Math.Min(bins - 1, high + 4); bin++)
                            {
                                earlier += before[bin];
                                later += after[bin];
                            }
                            bool stableSurroundings = Math.Max(earlier, later) <=
                                4 * Math.Min(earlier, later) + totals[frame] * contextFrames * .002;
                            if (stop > start && concentrated && stableSurroundings)
                                best = new(start, stop, peak, lowHz, highHz, contrastDb, score, fft, hop);

                            bool IsExcess(int bin) => power[offset + bin] >= baseline[bin] * 1.8 &&
                                power[offset + bin] - baseline[bin] >= excess * .015;
                        }
                    }
                }
            }
            // Sliding sums keep comparison linear in the search length. They are advanced even
            // when the current frame is outside the rough selection or too quiet to nominate.
            AddRow(before, frame - outer, -1);
            AddRow(before, frame - inner, 1);
            AddRow(after, frame + inner, -1);
            AddRow(after, frame + outer, 1);
            if ((frame & 63) == 0) progress?.Report(.6 + .4 * frame / frames);
        }
        progress?.Report(1);
        return best;

        void AddRow(double[] sum, int frame, int sign)
        {
            int offset = frame * bins;
            for (int bin = 0; bin < bins; bin++) sum[bin] += sign * (double)power[offset + bin];
        }
    }
}
