using WaveLab.Util;

namespace WaveLab.Audio.Dsp;

/// <summary>Settings for tracking and removing a drifting hum.</summary>
/// <param name="MinimumHz">Lowest fundamental considered.</param>
/// <param name="MaximumHz">Highest fundamental considered.</param>
/// <param name="Harmonics">How many partials of the fundamental to follow.</param>
/// <param name="BlockLength">Analysis block; sets the frequency resolution of each estimate.</param>
/// <param name="Smoothing">
/// How persistent a partial must be to be treated as hum. This is the whole discriminator between
/// the hum and the music sharing its frequency.
/// </param>
/// <param name="TrackingGain">How hard the fundamental is pulled toward the measured drift.</param>
/// <param name="MaximumDriftHz">Furthest the fundamental may move per block.</param>
public readonly record struct HumTrackOptions(
    double MinimumHz = 45,
    double MaximumHz = 65,
    int Harmonics = 8,
    int BlockLength = 16384,
    double Smoothing = 0.9,
    double TrackingGain = 0.25,
    double MaximumDriftHz = 0.05)
{
    /// <remarks>Spelled out rather than <c>new()</c>, which zero-initialises a record struct.</remarks>
    public static HumTrackOptions Default { get; } = new(
        MinimumHz: 45,
        MaximumHz: 65,
        Harmonics: 8,
        BlockLength: 16384,
        Smoothing: 0.9,
        TrackingGain: 0.25,
        MaximumDriftHz: 0.05);
}

/// <summary>What a hum measurement found.</summary>
/// <param name="StartHz">The fundamental at the beginning.</param>
/// <param name="MeanHz">Its average across the material.</param>
/// <param name="DriftHz">How far it wandered, peak to peak.</param>
/// <param name="LevelDb">Level of the hum against the programme.</param>
public readonly record struct HumReport(double StartHz, double MeanHz, double DriftHz, double LevelDb)
{
    public static HumReport None => new(0, 0, 0, double.NegativeInfinity);
    public bool Found => MeanHz > 0;
}

/// <summary>
/// Follows mains hum as its frequency wanders, and subtracts it rather than notching it out.
/// </summary>
/// <remarks>
/// <para>
/// Two things separate this from the notch bank in <c>HumRemovalEffect</c>. It <b>follows</b> the
/// fundamental instead of choosing 50 or 60 Hz once and holding it: mains frequency drifts by a
/// tenth of a hertz over minutes, and a hum picked up mechanically drifts with the turntable as
/// well. A fixed notch that is a tenth of a hertz off leaves the hum audible while still digging its
/// hole in the music.
/// </para>
/// <para>
/// And it <b>subtracts an estimate</b> rather than notching. A notch removes everything at its
/// frequency, hum and music alike, and a bank of eight of them puts eight holes through the bottom
/// two octaves. Estimating each partial's amplitude and phase and subtracting only that leaves the
/// music at those frequencies where it was.
/// </para>
/// <para>
/// What tells the hum from music sharing its frequency is <em>persistence</em>. Each partial's
/// complex amplitude is smoothed across blocks in a frame that rotates with the tracked frequency,
/// so a steady tone accumulates and a passing note averages away. That smoothing is the reason the
/// tracker can subtract at all rather than merely attenuate.
/// </para>
/// <para>
/// The fundamental is followed by a phase-locked loop rather than a search: if the assumed frequency
/// is a little wrong, each partial's measured phase rotates between blocks at a rate proportional to
/// the error, so the error is read off the rotation directly. That costs one projection per partial
/// per block, where a search costs one per candidate.
/// </para>
/// </remarks>
public static class HumTracker
{
    /// <summary>
    /// Measures every channel and returns the strongest supported hum family. A pickup fault may
    /// be almost entirely on one groove wall, so the first channel cannot decide whether a stereo
    /// transfer is eligible for repair.
    /// </summary>
    public static HumReport Measure(IReadOnlyList<float[]> channels, int sampleRate,
        HumTrackOptions options = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        cancellationToken.ThrowIfCancellationRequested();
        if (channels.Count == 0) return HumReport.None;

        progress?.Report(0);
        HumReport strongest = HumReport.None;
        for (int channel = 0; channel < channels.Count; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HumReport candidate = Measure(channels[channel], sampleRate, options, cancellationToken);
            if (candidate.Found && (!strongest.Found || candidate.LevelDb > strongest.LevelDb))
                strongest = candidate;
            progress?.Report((channel + 1.0) / channels.Count);
        }
        return strongest;
    }

    /// <summary>Measures the hum without changing anything.</summary>
    public static HumReport Measure(float[] samples, int sampleRate, HumTrackOptions options = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (options.BlockLength == 0) options = HumTrackOptions.Default;
        return Run(samples, sampleRate, options, null, cancellationToken, null);
    }

    /// <summary>Tracks the hum and subtracts it in place.</summary>
    public static HumReport Remove(float[] samples, int sampleRate, HumTrackOptions options = default,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (options.BlockLength == 0) options = HumTrackOptions.Default;

        var hum = new float[samples.Length];
        HumReport report = Run(samples, sampleRate, options, hum, cancellationToken, progress);
        if (!report.Found) return report;

        for (int i = 0; i < samples.Length; i++) samples[i] -= hum[i];
        return report;
    }

    /// <summary>
    /// Finds the fundamental, measures how it moves, and optionally builds the hum to subtract.
    /// </summary>
    /// <remarks>
    /// Two passes, not a tracking loop. A loop is causal, so it always lags whatever it is following:
    /// a first-order one settles to an error proportional to the rate of drift, and that residue is
    /// exactly what stops the subtraction cancelling — it followed a drifting hum well enough to
    /// report the drift while leaving most of it in the audio. Adding an integral term to remove the
    /// lag made the loop hunt on steady material instead, taking a 62 dB reduction down to 20.
    /// <para>
    /// Offline there is no reason to be causal. The first pass measures the frequency at every block
    /// against a fixed reference oscillator, which needs no feedback at all; the trajectory is then
    /// smoothed by <em>centred</em> filters, which have no lag by construction; and the second pass
    /// subtracts along the trajectory already known. A ramp is followed exactly, and there is no loop
    /// to be unstable.
    /// </para>
    /// </remarks>
    private static HumReport Run(float[] samples, int sampleRate, HumTrackOptions options,
        float[]? hum, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        int block = Fft.NextPowerOfTwo(Math.Max(4096, options.BlockLength));
        if (samples.Length < block * 2) return HumReport.None;

        (double reference, bool[] present) = Survey(samples, sampleRate, options, block);
        if (reference <= 0) return HumReport.None;

        int harmonics = Math.Clamp(options.Harmonics, 1, 32);
        int hop = block / 2;
        double[] window = Hann(block);
        double windowSum = 0;
        foreach (double value in window) windowSum += value;

        int blocks = 0;
        for (int start = 0; start + block <= samples.Length; start += hop) blocks++;
        if (blocks < 2) return HumReport.None;

        // ── pass one: where is the fundamental, block by block ──
        double[] trajectory = MeasureTrajectory(samples, sampleRate, reference, present, harmonics,
            block, hop, blocks, window, windowSum, cancellationToken,
            new SubProgress(progress, 0, hum == null ? 1 : 0.4));

        Smooth(trajectory);
        double lowest = Math.Min(options.MinimumHz, options.MaximumHz);
        double highest = Math.Max(options.MinimumHz, options.MaximumHz);
        for (int b = 0; b < blocks; b++) trajectory[b] = Math.Clamp(trajectory[b], lowest, highest);

        double sum = 0, minHz = double.MaxValue, maxHz = double.MinValue;
        foreach (double value in trajectory)
        {
            sum += value;
            minHz = Math.Min(minHz, value);
            maxHz = Math.Max(maxHz, value);
        }

        // ── pass two: subtract along it ──
        var phase = new double[harmonics + 1];
        var amplitudeRe = new double[harmonics + 1];
        var amplitudeIm = new double[harmonics + 1];
        var started = new bool[harmonics + 1];
        double alpha = Math.Clamp(options.Smoothing, 0, 0.999);
        double humEnergy = 0, totalEnergy = 0;

        for (int b = 0; b < blocks; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hum != null) progress?.Report(0.4 + 0.6 * b / blocks);

            int start = b * hop;
            double frequency = trajectory[b];

            for (int n = 1; n <= harmonics; n++)
            {
                if (!present[n]) continue;
                double partial = frequency * n;
                if (partial >= sampleRate * 0.47) break;

                double step = 2 * Math.PI * partial / sampleRate;
                double angle = phase[n];
                double re = 0, im = 0;
                for (int i = 0; i < block; i++)
                {
                    double weighted = samples[start + i] * window[i];
                    re += weighted * Math.Cos(angle);
                    im -= weighted * Math.Sin(angle);
                    angle += step;
                }
                re = 2 * re / windowSum;
                im = 2 * im / windowSum;

                // Smoothed in a frame that rotates with the tracked frequency, so a steady tone
                // accumulates and a passing note averages away. That is what tells hum from music
                // sharing its frequency, and it is why this can subtract rather than merely duck.
                if (!started[n]) { amplitudeRe[n] = re; amplitudeIm[n] = im; started[n] = true; }
                else
                {
                    amplitudeRe[n] = alpha * amplitudeRe[n] + (1 - alpha) * re;
                    amplitudeIm[n] = alpha * amplitudeIm[n] + (1 - alpha) * im;
                }

                if (hum != null)
                {
                    double synthesisAngle = phase[n];
                    double ar = amplitudeRe[n], ai = amplitudeIm[n];
                    for (int i = 0; i < block; i++)
                    {
                        hum[start + i] += (float)((ar * Math.Cos(synthesisAngle)
                                                 - ai * Math.Sin(synthesisAngle)) * window[i]);
                        synthesisAngle += step;
                    }
                }

                phase[n] += step * hop;
                phase[n] -= 2 * Math.PI * Math.Floor(phase[n] / (2 * Math.PI));

                // Mean square of a sinusoid of amplitude |A| is |A|²/2.
                humEnergy += 0.5 * (amplitudeRe[n] * amplitudeRe[n] + amplitudeIm[n] * amplitudeIm[n]);
            }

            for (int i = 0; i < block; i++) totalEnergy += (double)samples[start + i] * samples[start + i];
        }

        progress?.Report(1);

        double signalMeanSquare = totalEnergy / ((double)blocks * block);
        double level = signalMeanSquare > 0
            ? 10 * Math.Log10(Math.Max(humEnergy / blocks, 1e-30) / signalMeanSquare)
            : double.NegativeInfinity;
        return new HumReport(trajectory[0], sum / blocks, maxHz - minHz, level);
    }

    /// <summary>
    /// The fundamental at each block, from how far the lowest partial's phase advances against a
    /// fixed reference oscillator.
    /// </summary>
    /// <remarks>
    /// Measured on the lowest present partial rather than the strongest. The phase difference can
    /// only be unwrapped while it stays under half a turn per block, and a partial n times up moves
    /// n times as fast — the sixth harmonic of a hum half a hertz off its reference is already at the
    /// limit, where the first has room to spare.
    /// </remarks>
    private static double[] MeasureTrajectory(float[] samples, int sampleRate, double reference,
        bool[] present, int harmonics, int block, int hop, int blocks, double[] window,
        double windowSum, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        int lowest = 1;
        while (lowest <= harmonics && !present[lowest]) lowest++;

        var trajectory = new double[blocks];
        if (lowest > harmonics)
        {
            Array.Fill(trajectory, reference);
            return trajectory;
        }

        double partial = reference * lowest;
        double step = 2 * Math.PI * partial / sampleRate;
        var measured = new double[blocks];

        for (int b = 0; b < blocks; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(b / (double)blocks);

            int start = b * hop;
            double angle = step * start;
            angle -= 2 * Math.PI * Math.Floor(angle / (2 * Math.PI));

            double re = 0, im = 0;
            for (int i = 0; i < block; i++)
            {
                double weighted = samples[start + i] * window[i];
                re += weighted * Math.Cos(angle);
                im -= weighted * Math.Sin(angle);
                angle += step;
            }
            measured[b] = Math.Atan2(im, re);
        }

        // Unwrapped, then differenced: the rate the measured phase runs away from the reference is
        // exactly how far the hum sits from it.
        double unwrapped = measured[0], previous = measured[0];
        var deviation = new double[blocks];
        deviation[0] = 0;
        for (int b = 1; b < blocks; b++)
        {
            unwrapped += Principal(measured[b] - previous);
            previous = measured[b];
            deviation[b] = unwrapped;
        }

        for (int b = 0; b < blocks; b++)
        {
            int from = Math.Max(0, b - 1), to = Math.Min(blocks - 1, b + 1);
            double slope = (deviation[to] - deviation[from]) / Math.Max(1, to - from);
            trajectory[b] = reference + slope * sampleRate / (2 * Math.PI * hop) / lowest;
        }

        progress?.Report(1);
        return trajectory;
    }

    /// <summary>
    /// A centred median then a centred mean. Centred because a lagging filter would put back exactly
    /// the tracking error the two-pass design exists to avoid.
    /// </summary>
    private static void Smooth(double[] values)
    {
        // Wide, because the trajectory comes from differencing a phase and differencing amplifies
        // whatever noise the phase carried. Real drift is slow — a tenth of a hertz over minutes, or
        // a hundredth per block here — so a couple of seconds of smoothing follows it faithfully
        // while removing jitter that would otherwise put the subtraction out of phase with the hum.
        const int radius = 7;
        if (values.Length < radius * 2 + 1) return;

        var source = (double[])values.Clone();
        var window = new double[radius * 2 + 1];
        for (int i = 0; i < values.Length; i++)
        {
            int used = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int index = i + k;
                if ((uint)index < (uint)values.Length) window[used++] = source[index];
            }
            Array.Sort(window, 0, used);
            values[i] = window[used / 2];
        }

        source = (double[])values.Clone();
        for (int i = 0; i < values.Length; i++)
        {
            double total = 0;
            int used = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int index = i + k;
                if ((uint)index < (uint)values.Length) { total += source[index]; used++; }
            }
            values[i] = total / used;
        }
    }

    /// <summary>
    /// A coarse fundamental, from the frequency whose partials carry the most energy over a long
    /// window. This only has to be close: the loop takes it from there.
    /// </summary>
    internal static double InitialFundamental(float[] samples, int sampleRate,
        HumTrackOptions options, int block) =>
        Survey(samples, sampleRate, options, block).Fundamental;

    /// <summary>
    /// Finds the fundamental and decides which of its harmonics are actually present.
    /// </summary>
    /// <remarks>
    /// Deciding the harmonic set matters as much as finding the fundamental. Tracking a partial that
    /// is not there means projecting whatever <em>is</em> at that frequency — a sustained note, most
    /// likely — and subtracting it from the music. The seventh harmonic of a 50 Hz hum is 350 Hz;
    /// with a note at 349 held through the piece, removing "harmonic seven" removed the note, which
    /// showed as a broadband residual barely improving while every real partial came down 60 dB.
    /// A partial counts only when there is a peak within a couple of bins of exactly n·f₀, and 349
    /// against 350 is six bins away at this resolution.
    /// </remarks>
    internal static (double Fundamental, bool[] Present) Survey(float[] samples, int sampleRate,
        HumTrackOptions options, int block)
    {
        int harmonicCount = Math.Clamp(options.Harmonics, 1, 32);
        var absent = new bool[harmonicCount + 1];
        double found = FindFundamental(samples, sampleRate, options, block, out double[] prominence,
            out double resolution, out int bins);
        if (found <= 0) return (0, absent);

        var present = new bool[harmonicCount + 1];
        for (int n = 1; n <= harmonicCount; n++)
        {
            double frequency = found * n;
            if (frequency >= sampleRate * 0.47) break;

            int bin = (int)Math.Round(frequency / resolution);
            if (bin <= 1 || bin >= bins - 2) break;

            // Half a hertz either side of exactly n·f₀ — wide enough that a hum drifting across the
            // survey window still registers, tight enough that a note a hertz away does not.
            int reach = Math.Max(1, (int)Math.Round(PresenceToleranceHz / resolution));
            double peak = 0;
            for (int k = -reach; k <= reach; k++) peak = Math.Max(peak, PeakNear(prominence, bin + k, bins));
            present[n] = peak >= MinimumProminence;
        }

        // Without a fundamental there is nothing to anchor the rest to.
        return present[1] ? (found, present) : (0, absent);
    }

    private static double FindFundamental(float[] samples, int sampleRate,
        HumTrackOptions options, int block, out double[] prominence, out double resolution,
        out int binCount)
    {
        prominence = [];
        resolution = 0;
        binCount = 0;
        // As long a transform as the material allows, up to a quarter of a million points. The
        // resolution here is what decides whether a partial of the hum can be told from a note a
        // hertz away from it: at 65536 points a bin is 0.67 Hz and 349 sits inside the window
        // around 350, which had the tracker adopt a sustained note as its seventh harmonic and
        // subtract it. At 262144 the same pair is six bins apart.
        int size = 1 << 18;
        while (size > samples.Length && size > 4096) size /= 2;
        if (size < 4096 || samples.Length < size) return 0;

        // Taken from the middle, where a side is most likely to be playing.
        int start = Math.Clamp((samples.Length - size) / 2, 0, Math.Max(0, samples.Length - size));
        var frame = new float[size];
        double[] window = Hann(size);
        for (int i = 0; i < size; i++) frame[i] = (float)(samples[start + i] * window[i]);

        int bins = size / 2 + 1;
        var re = new float[bins];
        var im = new float[bins];
        Fft.RealForward(frame, re, im);

        var magnitude = new double[bins];
        for (int b = 0; b < bins; b++) magnitude[b] = Math.Sqrt((double)re[b] * re[b] + (double)im[b] * im[b]);

        // How far each bin stands out of its own neighbourhood, rather than how loud it is. A raw
        // magnitude sum finds whatever is loudest, and the loudest thing near a low-frequency
        // candidate is usually the music: the first version of this scored a 58.23 Hz candidate
        // best on every signal it was given, because four times 58.23 is 233 Hz, which is where the
        // test programme's own fundamental sat. Prominence asks whether there is a *peak* there.
        prominence = Prominence(magnitude, bins);
        resolution = (double)sampleRate / size;
        binCount = bins;

        double lowest = Math.Min(options.MinimumHz, options.MaximumHz);
        double highest = Math.Max(options.MinimumHz, options.MaximumHz);
        int harmonics = Math.Clamp(options.Harmonics, 1, 32);

        double best = 0, bestScore = 0;
        for (double candidate = lowest; candidate <= highest; candidate += resolution / 4)
        {
            // Hum has consecutive low partials. Requiring the first two — not merely the first —
            // is what separates a real fundamental from a candidate that only explains other
            // people's partials as its upper harmonics: a note at 233 Hz makes 58.2 look like a
            // fundamental, but there is nothing whatever at 116.4.
            if (PeakNear(prominence, candidate / resolution, bins) < MinimumProminence) continue;
            if (PeakNear(prominence, candidate * 2 / resolution, bins) < MinimumProminence) continue;

            double score = 0;
            for (int n = 1; n <= harmonics; n++)
            {
                double frequency = candidate * n;
                if (frequency >= sampleRate * 0.47) break;

                // Weighted by 1/n, matching how a hum's partials actually fall away, and capped so
                // that landing one harmonic on a very loud note cannot outweigh a whole comb of
                // genuine ones.
                score += Math.Min(PeakNear(prominence, frequency / resolution, bins), ProminenceCap) / n;
            }
            if (score > bestScore) { bestScore = score; best = candidate; }
        }

        return bestScore > 0 ? RefineFromHarmonics(magnitude, prominence, bins, best, resolution,
            harmonics, sampleRate) : 0;
    }

    /// <summary>
    /// Sharpens the fundamental using the partials above it.
    /// </summary>
    /// <remarks>
    /// The coarse scan can only place the fundamental to about a transform bin, and a hum sitting a
    /// twentieth of a hertz away from where the subtraction thinks it is will not cancel. The
    /// partials fix that for free: the sixth harmonic of a 50 Hz hum sits at 300 Hz, so locating
    /// <em>it</em> to a fraction of a bin and dividing by six gives the fundamental to a sixth of
    /// that error. Each partial's own peak is interpolated between bins first, and they are combined
    /// weighted by how strong they are.
    /// </remarks>
    private static double RefineFromHarmonics(double[] magnitude, double[] prominence, int bins,
        double coarse, double resolution, int harmonics, int sampleRate)
    {
        var estimates = new List<double>(harmonics);
        for (int n = 1; n <= harmonics; n++)
        {
            double frequency = coarse * n;
            if (frequency >= sampleRate * 0.47) break;

            int bin = (int)Math.Round(frequency / resolution);
            if (bin <= 1 || bin >= bins - 2) break;

            // Searched over the same half-hertz the presence test allows, not one bin. The coarse
            // estimate can be a tenth of a hertz out, which at this resolution is four bins — a
            // one-bin search then finds noise instead of the partial, and the refinement confirms
            // the coarse error rather than correcting it.
            int reach = Math.Max(1, (int)Math.Round(PresenceToleranceHz / resolution));
            int peak = bin;
            for (int candidate = bin - reach; candidate <= bin + reach; candidate++)
            {
                if ((uint)candidate >= (uint)bins) continue;
                if (magnitude[candidate] > magnitude[peak]) peak = candidate;
            }
            if (peak <= 0 || peak >= bins - 1 || prominence[peak] < MinimumProminence) continue;

            double offset = Azimuth.ParabolicOffset(magnitude[peak - 1], magnitude[peak], magnitude[peak + 1]);
            double estimate = (peak + offset) * resolution / n;

            // Reject a partial that disagrees wildly; it is somebody else's.
            if (Math.Abs(estimate - coarse) <= PresenceToleranceHz) estimates.Add(estimate);
        }

        if (estimates.Count == 0) return coarse;

        // The median, not a weighted mean. Music lands on a hum's harmonics constantly — a partial
        // at 349 Hz is the seventh of 49.86, and a mean weighted by strength let that one loud
        // coincidence drag a 50 Hz estimate to 49.92 while leaving 60 Hz untouched. One colliding
        // harmonic out of six or eight cannot move a median.
        estimates.Sort();
        return estimates.Count % 2 == 1
            ? estimates[estimates.Count / 2]
            : (estimates[estimates.Count / 2 - 1] + estimates[estimates.Count / 2]) / 2;
    }

    /// <summary>A bin must stand this far out of its neighbourhood to count as a partial.</summary>
    private const double MinimumProminence = 3;

    /// <summary>How far from exactly n·f₀ a peak may sit and still be that partial.</summary>
    private const double PresenceToleranceHz = 0.5;

    /// <summary>
    /// Most a single harmonic may contribute to a candidate's score. Without it one very loud note
    /// coinciding with one harmonic outweighs a whole comb of real ones.
    /// </summary>
    private const double ProminenceCap = 40;

    /// <summary>
    /// The strongest prominence at a <em>local maximum</em> within a bin either side of a position.
    /// </summary>
    /// <remarks>
    /// Insisting on a local maximum is what tells a partial from the skirt of a loud neighbour. A
    /// note at 349 Hz leaks across 350, and its leakage clears any prominence threshold on quiet
    /// material — so the tracker declared a seventh harmonic that was not there, subtracted at
    /// 350 Hz, and took a piece of the note with it. A skirt falls away monotonically; only a real
    /// partial is a maximum.
    /// </remarks>
    private static double PeakNear(double[] prominence, double position, int bins)
    {
        int centre = (int)Math.Round(position);
        double best = 0;
        for (int bin = centre - 1; bin <= centre + 1; bin++)
        {
            if (bin <= 0 || bin >= bins - 1) continue;
            if (prominence[bin] < prominence[bin - 1] || prominence[bin] < prominence[bin + 1]) continue;
            best = Math.Max(best, prominence[bin]);
        }
        return best;
    }

    /// <summary>
    /// Each bin against the average of its neighbourhood, so what is measured is a peak rather than
    /// a level.
    /// </summary>
    /// <remarks>
    /// The background is a median of medians, not a moving average. An average is dragged upward by
    /// any strong peak inside its window, so a note at 233 Hz raised the background around 200 Hz
    /// far enough to hide a real hum partial sitting there — the fourth and fifth harmonics were
    /// declared absent and left in the audio. A median ignores the peak, which is the whole reason
    /// to use one.
    /// </remarks>
    private static double[] Prominence(double[] magnitude, int bins)
    {
        const int segment = 64;
        int segments = (bins + segment - 1) / segment;
        var medians = new double[segments];
        var buffer = new double[segment];

        for (int s = 0; s < segments; s++)
        {
            int from = s * segment;
            int count = Math.Min(segment, bins - from);
            Array.Copy(magnitude, from, buffer, 0, count);
            Array.Sort(buffer, 0, count);
            medians[s] = buffer[count / 2];
        }

        var window = new double[5];
        var background = new double[bins];
        for (int b = 0; b < bins; b++)
        {
            int s = b / segment;
            int used = 0;
            for (int k = -2; k <= 2; k++)
            {
                int index = s + k;
                if ((uint)index < (uint)segments) window[used++] = medians[index];
            }
            Array.Sort(window, 0, used);
            background[b] = window[used / 2];
        }

        // Floored by the noise level of the whole spectrum, not just the local one. Prominence is a
        // ratio, and in a quiet stretch the local median is tiny, so ordinary noise clears any
        // threshold and a bin with nothing in it reads as a peak. That is how a 58.21 Hz candidate
        // passed the "has a fundamental" gate on noise alone and then won the scan by explaining a
        // 233 Hz note as its fourth harmonic and a 349 Hz one as its sixth.
        var sorted = (double[])magnitude.Clone();
        Array.Sort(sorted);
        double globalFloor = sorted[sorted.Length / 2];

        var result = new double[bins];
        for (int b = 0; b < bins; b++)
            result[b] = magnitude[b] / Math.Max(background[b], Math.Max(globalFloor, 1e-15));
        return result;
    }

    private static double[] Hann(int n)
    {
        var window = new double[n];
        for (int i = 0; i < n; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        return window;
    }

    private static double Principal(double angle) =>
        angle - 2 * Math.PI * Math.Round(angle / (2 * Math.PI));
}

