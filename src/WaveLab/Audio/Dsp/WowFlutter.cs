using WaveLab.Util;

namespace WaveLab.Audio.Dsp;

/// <summary>Settings for measuring and correcting speed variation.</summary>
/// <param name="BlockLength">Analysis block. The hop is a quarter of it.</param>
/// <param name="LowestHz">Bottom of the band the drift is measured over.</param>
/// <param name="HighestHz">Top of that band.</param>
/// <param name="Points">Resolution of the log-frequency axis, over the whole band.</param>
/// <param name="MaximumDeviation">Largest speed error corrected, as a fraction.</param>
/// <param name="BaselineSeconds">
/// Variation slower than this is left alone: it is a speed error, not wow, and correcting it would
/// change the pitch of the record.
/// </param>
/// <param name="MinimumCorrelation">How well two frames must agree for their shift to be believed.</param>
/// <param name="ReferenceSeconds">
/// Width of the local reference each block is matched against. <b>This is what the measurement
/// rests on.</b> Matching a block against its immediate predecessor measures the <i>velocity</i> of
/// the speed error, and recovering the speed itself then needs an integration — which turns
/// measurement noise into a random walk, and forces smoothing of the derivative to contain it.
/// Median-smoothing a derivative does not preserve its integral, so that costs amplitude as well.
/// Matching instead against the average of the blocks around it measures the <i>position</i>
/// directly: no integration, no walk, and the quantity being measured is ten times larger. Set to
/// zero for the old frame-to-frame behaviour.
/// </param>
/// <param name="SmoothingBlocks">
/// Centred smoothing applied to the per-block shifts before they are integrated. Integrating
/// measurement noise is a random walk, and a walk in the position map is a slow speed error put
/// there by the tool meant to remove one.
/// </param>
public readonly record struct WowFlutterOptions(
    int BlockLength = 4096,
    double LowestHz = 1000,
    double HighestHz = 8000,
    int Points = 1024,
    double MaximumDeviation = 0.06,
    double BaselineSeconds = 4,
    double MinimumCorrelation = 0.6,
    int SmoothingBlocks = 2,
    double ReferenceSeconds = 0.75)
{
    /// <remarks>Spelled out rather than <c>new()</c>, which zero-initialises a record struct.</remarks>
    /// <remarks>
    /// <para>
    /// The block length and the band are set together, and the choice is the uncertainty principle
    /// in plain sight. To see a frequency move by a fraction of a percent the transform must resolve
    /// that fraction, which needs roughly one-over-it cycles: a third of a percent needs three
    /// hundred cycles, which at 400 Hz is three quarters of a second. But wow is a variation of a few
    /// hertz, and a window of three quarters of a second averages across most of a cycle of it.
    /// </para>
    /// <para>
    /// Measured, that is not a small effect: with a 0.37 s window a planted variation came back at
    /// 0.55 of its depth at 0.8 Hz, 0.29 at 1.5 Hz and 0.14 at 2 Hz — a low-pass, not noise. The way
    /// out is to measure <em>high</em>: three hundred cycles at 4 kHz is 75 ms, short enough to
    /// follow wow well past 6 Hz. So the band starts at 1 kHz and the block is short.
    /// </para>
    /// </remarks>
    public static WowFlutterOptions Default { get; } = new(
        BlockLength: 4096,
        LowestHz: 1000,
        HighestHz: 8000,
        Points: 1024,
        MaximumDeviation: 0.06,
        BaselineSeconds: 4,
        MinimumCorrelation: 0.6,
        SmoothingBlocks: 2);
}

/// <summary>What a speed-variation measurement found.</summary>
/// <param name="PeakPercent">Largest deviation from the running average speed.</param>
/// <param name="RmsPercent">Root-mean-square deviation, which is how wow is usually quoted.</param>
/// <param name="Blocks">How many blocks carried enough signal to be measured.</param>
/// <param name="Confidence">Fraction of blocks whose shift was believed rather than interpolated.</param>
public readonly record struct WowFlutterReport(
    double PeakPercent, double RmsPercent, int Blocks, double Confidence)
{
    public static WowFlutterReport None => new(0, 0, 0, 0);
    public bool Found => Blocks > 0;
}

/// <summary>
/// Measures and corrects wow and flutter: the speed variation of the machine a record was cut or
/// played on.
/// </summary>
/// <remarks>
/// <para>
/// Speed variation is <b>multiplicative</b> — every frequency moves by the same ratio, so a note and
/// its tenth harmonic shift together in proportion, not in step. On a <em>logarithmic</em> frequency
/// axis a multiplication is a translation, so the whole spectrum simply slides sideways, and the
/// amount it slides between two frames is the speed ratio between them. That is what is measured
/// here: consecutive log-frequency spectra are correlated and the offset of the peak is the drift.
/// </para>
/// <para>
/// Tracking individual partials — the obvious approach, and the one the plan called for — needs
/// partials that are <em>sustained</em>, and music does not oblige: notes change every fraction of a
/// second and a tracker locked to one loses it. Sliding the whole spectrum needs no sustained
/// partial at all, only that the material sounds roughly like itself from one frame to the next,
/// which is true across a note change far more often than any individual partial survives it.
/// </para>
/// <para>
/// Frames whose spectra do not correlate — a note change, a splice, a silence — are not guessed at.
/// Their shift is marked unreliable and interpolated from the frames either side, because the
/// alternative is to let a chord change register as a lurch in turntable speed.
/// </para>
/// <para>
/// Only variation <em>faster</em> than <see cref="WowFlutterOptions.BaselineSeconds"/> is corrected.
/// A record running consistently half a percent fast is at the wrong pitch, which is a different
/// complaint with a different remedy; subtracting a slow baseline also stops the error accumulated
/// by integrating frame-to-frame measurements from wandering off over a whole side.
/// </para>
/// </remarks>
public static class WowFlutter
{
    /// <summary>
    /// The speed ratio at each block relative to the running average, and the report describing it.
    /// </summary>
    public static (double[] Ratio, int Hop, WowFlutterReport Report) Measure(float[] samples,
        int sampleRate, WowFlutterOptions options = default,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (options.BlockLength == 0) options = WowFlutterOptions.Default;
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        int block = Fft.NextPowerOfTwo(Math.Max(1024, options.BlockLength));
        int hop = block / 4;
        if (samples.Length < block * 4) return ([], hop, WowFlutterReport.None);

        int points = Math.Max(64, options.Points);
        double lowest = Math.Max(20, options.LowestHz);
        double highest = Math.Min(sampleRate * 0.45, Math.Max(lowest * 2, options.HighestHz));
        double perOctave = points / Math.Log2(highest / lowest);

        int blocks = (samples.Length - block) / hop + 1;
        var spectra = new double[blocks][];
        double[] window = Hann(block);
        int bins = block / 2 + 1;

        var frame = new float[block];
        var re = new float[bins];
        var im = new float[bins];

        for (int b = 0; b < blocks; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.5 * b / blocks);

            int start = b * hop;
            for (int i = 0; i < block; i++) frame[i] = (float)(samples[start + i] * window[i]);
            Fft.RealForward(frame, re, im);
            spectra[b] = LogSpectrum(re, im, bins, sampleRate, block, lowest, highest, points);
        }

        int reach = Math.Max(2, (int)Math.Ceiling(options.MaximumDeviation * perOctave / Math.Log(2) * 1.5));
        var shift = new double[blocks];
        var believed = new bool[blocks];
        int trusted = 0;
        int referenceRadius = options.ReferenceSeconds > 0
            ? Math.Max(1, (int)Math.Round(options.ReferenceSeconds * sampleRate / hop / 2))
            : 0;

        if (referenceRadius > 0)
        {
            // Each block against the average of the blocks around it, which measures where the
            // spectrum sits rather than how fast it is moving. See ReferenceSeconds.
            var reference = new double[points];
            var running = new double[points];
            int windowFrom = 0, windowTo = 0;

            for (int b = 0; b < blocks; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.5 + 0.3 * b / blocks);

                int wantFrom = Math.Max(0, b - referenceRadius);
                int wantTo = Math.Min(blocks, b + referenceRadius + 1);
                while (windowTo < wantTo) { Add(running, spectra[windowTo], points, 1); windowTo++; }
                while (windowFrom < wantFrom) { Add(running, spectra[windowFrom], points, -1); windowFrom++; }

                int width = windowTo - windowFrom;
                if (width < 2) continue;
                for (int p = 0; p < points; p++) reference[p] = running[p] / width;

                (double offset, double quality) = BestShift(reference, spectra[b], points, reach);
                if (quality >= options.MinimumCorrelation)
                {
                    shift[b] = offset;
                    believed[b] = true;
                    trusted++;
                }
            }
        }
        else
        {
            // Shift between consecutive frames, in log-frequency points.
            for (int b = 1; b < blocks; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.5 + 0.3 * b / blocks);

                (double offset, double quality) = BestShift(spectra[b - 1], spectra[b], points, reach);
                if (quality >= options.MinimumCorrelation)
                {
                    shift[b] = offset;
                    believed[b] = true;
                    trusted++;
                }
            }
        }

        if (trusted < 2) return ([], hop, WowFlutterReport.None);
        Interpolate(shift, believed);

        // Integrate the frame-to-frame shifts into a running position on the log axis, which is the
        // cumulative speed error, then take off a slow baseline to leave only the variation.
        // Smoothed before integrating. Each block's shift carries measurement noise, and integrating
        // noise is a random walk: on material with no wow at all the position map wandered by a
        // dozen samples across ten seconds, which is a slow speed error introduced by the very tool
        // meant to remove one. Wow is a variation of a few hertz, so a hundred milliseconds of
        // centred smoothing costs nothing real and removes most of the walk.
        double[] cumulative;
        if (referenceRadius > 0)
        {
            // Already a position, so there is nothing to integrate and nothing to smooth away. A
            // mean of three blocks takes the edge off without touching wow: three blocks is 70 ms
            // against a 1.4 s cycle at 0.7 Hz.
            cumulative = shift;
            MeanSmooth(cumulative, 1);
        }
        else
        {
            SmoothTrajectory(shift, Math.Max(0, options.SmoothingBlocks));
            cumulative = new double[blocks];
            for (int b = 1; b < blocks; b++) cumulative[b] = cumulative[b - 1] + shift[b];
        }

        int baseline = Math.Max(1, (int)Math.Round(options.BaselineSeconds * sampleRate / hop));
        Detrend(cumulative, baseline);

        var ratio = new double[blocks];
        double peak = 0, sumSquares = 0;
        for (int b = 0; b < blocks; b++)
        {
            double octaves = cumulative[b] / perOctave;
            double value = Math.Pow(2, octaves);
            value = Math.Clamp(value, 1 - options.MaximumDeviation, 1 + options.MaximumDeviation);
            ratio[b] = value;

            double deviation = Math.Abs(value - 1);
            peak = Math.Max(peak, deviation);
            sumSquares += deviation * deviation;
        }

        progress?.Report(1);
        var report = new WowFlutterReport(peak * 100, Math.Sqrt(sumSquares / blocks) * 100, blocks,
            trusted / (double)Math.Max(1, blocks - 1));
        return (ratio, hop, report);
    }

    /// <summary>Measures without changing anything.</summary>
    public static WowFlutterReport Analyze(float[] samples, int sampleRate,
        WowFlutterOptions options = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null) =>
        Measure(samples, sampleRate, options, cancellationToken, progress).Report;

    /// <summary>
    /// Measures the drift on the first channel and resamples every channel along it.
    /// </summary>
    /// <remarks>
    /// One map for all channels, measured once. A map derived per channel would differ between them
    /// by the noise in each measurement, and resampling the two sides of a stereo pair along
    /// slightly different time bases is a wandering image and a smeared centre — a worse fault than
    /// the wow being corrected.
    /// </remarks>
    public static WowFlutterReport Correct(float[][] channels, int sampleRate,
        WowFlutterOptions options = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length == 0 || channels[0].Length == 0) return WowFlutterReport.None;

        var (ratio, hop, report) = Measure(channels[0], sampleRate, options, cancellationToken,
            new SubProgress(progress, 0, 0.4));
        if (!report.Found || ratio.Length == 0) return report;

        for (int c = 0; c < channels.Length; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            channels[c] = Resample(channels[c], ratio, hop, cancellationToken,
                new SubProgress(progress, 0.4 + 0.6 * c / channels.Length, 0.6 / channels.Length));
        }

        progress?.Report(1);
        return report;
    }

    /// <summary>
    /// Reads the signal along a position whose rate is the inverse of the measured speed.
    /// </summary>
    /// <remarks>
    /// If the machine ran fast by a factor r, it consumed the groove faster than it should have, so
    /// the recovered signal is read back at 1/r to put it where it belongs. The position is
    /// integrated as it goes rather than stored per sample: a side is tens of millions of samples and
    /// the ratio is smooth enough to interpolate from the block grid.
    /// </remarks>
    internal static float[] Resample(float[] signal, double[] ratio, int hop,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var result = new float[signal.Length];
        double position = 0;

        for (int j = 0; j < signal.Length; j++)
        {
            if ((j & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(j / (double)signal.Length);
            }

            result[j] = (float)Interpolation.At(signal, position);
            position += 1.0 / RatioAt(ratio, hop, position);
        }

        progress?.Report(1);
        return result;
    }

    /// <summary>The ratio at a sample position, interpolated between the blocks it was measured on.</summary>
    private static double RatioAt(double[] ratio, int hop, double position)
    {
        if (ratio.Length == 0) return 1;
        double exact = position / hop;
        int index = (int)Math.Floor(exact);
        if (index < 0) return ratio[0];
        if (index >= ratio.Length - 1) return ratio[^1];

        double fraction = exact - index;
        return ratio[index] * (1 - fraction) + ratio[index + 1] * fraction;
    }

    // ── the log-frequency spectrum ───────────────────────────────

    /// <summary>
    /// The magnitude spectrum resampled onto a logarithmic frequency axis, in decibels with its mean
    /// removed.
    /// </summary>
    /// <remarks>
    /// Decibels and mean-removed so that the correlation measures <em>shape</em>: a passage getting
    /// louder must not read as the spectrum sliding sideways.
    /// </remarks>
    private static double[] LogSpectrum(float[] re, float[] im, int bins, int sampleRate,
        int size, double lowest, double highest, int points)
    {
        var spectrum = new double[points];
        double step = Math.Log(highest / lowest) / points;
        double resolution = (double)sampleRate / size;

        for (int p = 0; p < points; p++)
        {
            double frequency = lowest * Math.Exp(step * (p + 0.5));
            double exact = frequency / resolution;
            int bin = (int)Math.Floor(exact);
            if (bin < 0 || bin >= bins - 1) continue;

            // Interpolated between bins, so a partial moving by a fraction of a bin moves the log
            // spectrum smoothly rather than in steps.
            double fraction = exact - bin;
            double low = Magnitude(re[bin], im[bin]);
            double high = Magnitude(re[bin + 1], im[bin + 1]);
            spectrum[p] = 20 * Math.Log10(Math.Max(low * (1 - fraction) + high * fraction, 1e-12));
        }

        double mean = 0;
        foreach (double value in spectrum) mean += value;
        mean /= points;
        for (int p = 0; p < points; p++) spectrum[p] -= mean;
        return spectrum;

        static double Magnitude(float real, float imaginary) =>
            Math.Sqrt((double)real * real + (double)imaginary * imaginary);
    }

    /// <summary>
    /// How far the second spectrum has slid against the first, and how well they agree there.
    /// </summary>
    private static (double Offset, double Quality) BestShift(double[] previous, double[] current,
        int points, int reach)
    {
        double best = double.NegativeInfinity;
        int bestLag = 0;
        Span<double> scores = stackalloc double[reach * 2 + 1];

        for (int lag = -reach; lag <= reach; lag++)
        {
            double score = Correlate(previous, current, points, lag);
            scores[lag + reach] = score;
            if (score > best) { best = score; bestLag = lag; }
        }

        if (best <= 0) return (0, 0);

        // Refined between lags by fitting the three around the peak, which is enough here: the
        // correlation of two similar spectra is smooth and broad on this axis.
        int index = bestLag + reach;
        double before = index > 0 ? scores[index - 1] : best;
        double after = index < scores.Length - 1 ? scores[index + 1] : best;
        return (bestLag + Azimuth.ParabolicOffset(before, best, after), best);
    }

    /// <summary>Normalised correlation of two log spectra at a given offset.</summary>
    private static double Correlate(double[] previous, double[] current, int points, int lag)
    {
        double dot = 0, left = 0, right = 0;
        int from = Math.Max(0, -lag);
        int to = Math.Min(points, points - lag);
        if (to - from < points / 2) return 0;

        for (int p = from; p < to; p++)
        {
            double a = previous[p];
            double b = current[p + lag];
            dot += a * b;
            left += a * a;
            right += b * b;
        }

        double norm = Math.Sqrt(left * right);
        return norm > 1e-12 ? dot / norm : 0;
    }

    // ── trajectory tidying ───────────────────────────────────────

    /// <summary>
    /// Fills in the blocks whose shift was not believed, from the nearest ones that were.
    /// </summary>
    private static void Interpolate(double[] shift, bool[] believed)
    {
        int first = Array.IndexOf(believed, true);
        if (first < 0) return;

        for (int b = 0; b < first; b++) shift[b] = shift[first];

        int previous = first;
        for (int b = first + 1; b < shift.Length; b++)
        {
            if (!believed[b]) continue;
            int gap = b - previous;
            for (int k = 1; k < gap; k++)
                shift[previous + k] = shift[previous] + (shift[b] - shift[previous]) * k / gap;
            previous = b;
        }
        for (int b = previous + 1; b < shift.Length; b++) shift[b] = shift[previous];
    }

    /// <summary>A centred median then a centred mean over the per-block shifts.</summary>
    private static void SmoothTrajectory(double[] values, int radius)
    {
        if (radius <= 0 || values.Length < radius * 2 + 1) return;

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
    /// Subtracts a slowly-varying baseline, leaving only variation faster than it.
    /// </summary>
    private static void Detrend(double[] values, int radius)
    {
        if (values.Length == 0) return;
        var baseline = new double[values.Length];

        // Running mean, computed once by prefix sums so the width costs nothing.
        var prefix = new double[values.Length + 1];
        for (int i = 0; i < values.Length; i++) prefix[i + 1] = prefix[i] + values[i];

        for (int i = 0; i < values.Length; i++)
        {
            int from = Math.Max(0, i - radius);
            int to = Math.Min(values.Length, i + radius + 1);
            baseline[i] = (prefix[to] - prefix[from]) / (to - from);
        }

        for (int i = 0; i < values.Length; i++) values[i] -= baseline[i];
    }

    private static void Add(double[] running, double[] spectrum, int points, int sign)
    {
        for (int p = 0; p < points; p++) running[p] += sign * spectrum[p];
    }

    /// <summary>Centred mean. Linear, so unlike a median it preserves what it smooths.</summary>
    private static void MeanSmooth(double[] values, int radius)
    {
        if (radius <= 0 || values.Length < radius * 2 + 1) return;
        var source = (double[])values.Clone();
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

    private static double[] Hann(int n)
    {
        var window = new double[n];
        for (int i = 0; i < n; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        return window;
    }
}

