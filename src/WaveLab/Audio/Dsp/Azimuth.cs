namespace WaveLab.Audio.Dsp;

/// <summary>What an azimuth measurement found.</summary>
/// <param name="DelaySamples">How far the right channel lags the left. Negative means it leads.</param>
/// <param name="Confidence">0 to 1: how consistently the windows agreed.</param>
/// <param name="Windows">How many windows carried enough signal to be measured.</param>
/// <param name="SpreadSamples">Median absolute deviation of the per-window estimates.</param>
public readonly record struct AzimuthEstimate(
    double DelaySamples, double Confidence, int Windows, double SpreadSamples)
{
    public static AzimuthEstimate None => new(0, 0, 0, 0);

    public double Microseconds(int sampleRate) =>
        sampleRate > 0 ? DelaySamples * 1e6 / sampleRate : 0;
}

/// <summary>Settings for an azimuth measurement.</summary>
/// <param name="WindowSize">Transform length per window; a power of two.</param>
/// <param name="MaximumDelayMs">How far apart the channels are allowed to be found.</param>
/// <param name="Windows">How many windows to spread across the material.</param>
/// <param name="MinimumLevel">Windows quieter than this contribute nothing.</param>
public readonly record struct AzimuthOptions(
    int WindowSize = 16384,
    double MaximumDelayMs = 0.5,
    int Windows = 64,
    double MinimumLevel = 1e-4)
{
    /// <remarks>Spelled out rather than <c>new()</c>, which zero-initialises a record struct.</remarks>
    public static AzimuthOptions Default { get; } = new(
        WindowSize: 16384,
        // A stylus geometry error is sub-millisecond. A wider search starts finding intentional
        // stereo-production delays and calling them cartridge alignment.
        MaximumDelayMs: 0.5,
        Windows: 64,
        MinimumLevel: 1e-4);
}

/// <summary>
/// Measures and corrects the timing difference between the two channels of a transfer.
/// </summary>
/// <remarks>
/// <para>
/// A stylus whose azimuth is off reads one wall of the groove slightly before the other, which puts
/// a constant sub-millisecond delay between the channels. It is heard as a smeared, phasey centre
/// image and it collapses the mono sum in the top octaves, where the delay is a significant part of
/// a cycle.
/// </para>
/// <para>
/// The measurement is <b>GCC-PHAT</b>: cross-correlate in the frequency domain, but divide out the
/// magnitude first so every bin contributes its phase alone. Plain cross-correlation is dominated by
/// whatever is loudest, so on music it measures the bass — the part with the least timing
/// information — and returns a broad peak that a sample-resolution search can barely locate.
/// Whitening makes every frequency count equally and turns the peak into a spike. The peak is then
/// refined by fitting a parabola to its three highest points, which is what gets below one sample
/// without transforming at a higher rate.
/// </para>
/// <para>
/// It is measured over many windows and reduced by the <b>median</b>, not the mean. A transient in
/// one channel, a passage panned hard, or a dropout will each produce a confident and completely
/// wrong answer in the window that contains them; the median ignores them, where a mean would be
/// dragged by them.
/// </para>
/// </remarks>
public static class Azimuth
{
    /// <summary>Measures how far the right channel lags the left across the given range.</summary>
    public static AzimuthEstimate Estimate(float[] left, float[] right, int sampleRate,
        AzimuthOptions options = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (options.WindowSize == 0) options = AzimuthOptions.Default;
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        int size = Fft.NextPowerOfTwo(Math.Max(256, options.WindowSize));
        int length = Math.Min(left.Length, right.Length);
        if (length < size) return AzimuthEstimate.None;

        int maximumLag = Math.Max(1, (int)(options.MaximumDelayMs * 1e-3 * sampleRate));
        if (maximumLag >= size / 2) maximumLag = size / 2 - 1;

        int windows = Math.Max(1, options.Windows);
        int stride = Math.Max(size, (length - size) / windows + 1);

        var estimates = new List<double>(windows);
        var window = Fft.HannWindow(size);
        var leftRe = new double[size];
        var leftIm = new double[size];
        var rightRe = new double[size];
        var rightIm = new double[size];
        var scratchRe = new double[size];
        var scratchIm = new double[size];

        int index = 0;
        for (int start = 0; start + size <= length; start += stride, index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(Math.Min(1, start / (double)Math.Max(1, length - size)));

            double energy = 0;
            for (int i = 0; i < size; i++)
            {
                double l = left[start + i] * window[i];
                double r = right[start + i] * window[i];
                leftRe[i] = l; leftIm[i] = 0;
                rightRe[i] = r; rightIm[i] = 0;
                energy += l * l + r * r;
            }
            // Both channels contributed to the sum, so both are divided out of it: without the two
            // the gate sits a factor of root two above the level it documents.
            if (Math.Sqrt(energy / (2.0 * size)) < options.MinimumLevel) continue;

            double? lag = PhaseTransformPeak(leftRe, leftIm, rightRe, rightIm,
                scratchRe, scratchIm, size, maximumLag);
            if (lag is { } value) estimates.Add(value);
        }

        progress?.Report(1);
        if (estimates.Count == 0) return AzimuthEstimate.None;

        estimates.Sort();
        double median = Median(estimates);
        double spread = MedianAbsoluteDeviation(estimates, median);

        // Confidence needs both agreement and enough independent evidence. With agreement alone a
        // single usable window has zero spread and therefore claims perfect confidence, even though
        // there is no second observation to agree with it. Eight windows is enough for the median
        // and its spread to be meaningful; longer sides usually contribute several times that.
        double agreement = Math.Clamp(1 - (spread - 0.1) / 4.0, 0, 1);
        double coverage = Math.Clamp(estimates.Count / 8.0, 0, 1);
        double confidence = agreement * coverage;
        return new AzimuthEstimate(median, confidence, estimates.Count, spread);
    }

    /// <summary>
    /// One window of GCC-PHAT, returning the sub-sample lag of the right channel behind the left.
    /// </summary>
    /// <remarks>
    /// The integer peak comes from the inverse transform and the fraction from evaluating the
    /// correlation directly at points between the samples. It is not refined by fitting a parabola
    /// to the three samples around the peak, which is the usual recipe and is <em>biased</em> here:
    /// whitening flattens the spectrum, so the correlation is a sinc, and a parabola is a poor fit to
    /// a sinc's crown. Measured, that recipe read a planted 0.25 samples as 0.135 and 0.40 as 0.268,
    /// while landing exactly on whole and half samples — the shape of an interpolation error, not of
    /// noise. Evaluating the true band-limited correlation has no such bias.
    /// </remarks>
    internal static double? PhaseTransformPeak(double[] leftRe, double[] leftIm,
        double[] rightRe, double[] rightIm, double[] scratchRe, double[] scratchIm,
        int size, int maximumLag)
    {
        Fft.Forward(leftRe, leftIm);
        Fft.Forward(rightRe, rightIm);

        for (int b = 0; b < size; b++)
        {
            // Cross-spectrum L · conj(R), then divided by its own magnitude: the phase transform.
            double re = leftRe[b] * rightRe[b] + leftIm[b] * rightIm[b];
            double im = leftIm[b] * rightRe[b] - leftRe[b] * rightIm[b];
            double magnitude = Math.Sqrt(re * re + im * im);
            bool usable = magnitude >= 1e-20;
            leftRe[b] = usable ? re / magnitude : 0;
            leftIm[b] = usable ? im / magnitude : 0;
            scratchRe[b] = leftRe[b];
            scratchIm[b] = leftIm[b];
        }

        Fft.Inverse(scratchRe, scratchIm);

        // Lag zero sits at index zero and negative lags wrap to the top of the buffer.
        int bestLag = 0;
        double bestValue = double.NegativeInfinity;
        for (int lag = -maximumLag; lag <= maximumLag; lag++)
        {
            double value = scratchRe[(lag + size) % size];
            if (value > bestValue) { bestValue = value; bestLag = lag; }
        }
        if (!double.IsFinite(bestValue) || bestValue <= 0) return null;

        double refined = Refine(leftRe, leftIm, size, bestLag);

        // The correlation of L against a right channel delayed by d peaks at -d, so the measured lag
        // is negated to report how far the right channel is *behind* the left.
        return -refined;
    }

    /// <summary>
    /// Locates the correlation peak between samples by evaluating it directly either side of
    /// <paramref name="integerLag"/>, then fitting to that much finer grid.
    /// </summary>
    private static double Refine(double[] crossRe, double[] crossIm, int size, int integerLag)
    {
        const double Step = 0.05;
        double bestLag = integerLag, bestValue = double.NegativeInfinity;

        for (double offset = -1; offset <= 1.0000001; offset += Step)
        {
            double value = CorrelationAt(crossRe, crossIm, size, integerLag + offset);
            if (value > bestValue) { bestValue = value; bestLag = integerLag + offset; }
        }

        // The grid is fine enough that the crown really is locally parabolic here.
        double before = CorrelationAt(crossRe, crossIm, size, bestLag - Step);
        double after = CorrelationAt(crossRe, crossIm, size, bestLag + Step);
        return bestLag + ParabolicOffset(before, bestValue, after) * Step;
    }

    /// <summary>
    /// The whitened cross-correlation at any real lag, from its spectrum: the inverse transform
    /// evaluated at one point rather than on the sample grid.
    /// </summary>
    /// <remarks>
    /// The exponential is stepped by repeated multiplication rather than recomputed per bin — one
    /// complex multiply instead of two trigonometric calls, which is what makes evaluating this
    /// forty times per window affordable.
    /// </remarks>
    private static double CorrelationAt(double[] re, double[] im, int size, double lag)
    {
        double theta = 2 * Math.PI * lag / size;
        double stepCos = Math.Cos(theta), stepSin = Math.Sin(theta);
        double cos = 1, sin = 0;

        // The spectrum is conjugate-symmetric, so the upper half contributes the same as the lower.
        double sum = re[0];
        int half = size / 2;
        for (int b = 1; b < half; b++)
        {
            double nextCos = cos * stepCos - sin * stepSin;
            sin = cos * stepSin + sin * stepCos;
            cos = nextCos;
            sum += 2 * (re[b] * cos - im[b] * sin);
        }
        sum += re[half] * Math.Cos(theta * half);
        return sum;
    }

    /// <summary>
    /// Where the peak of a parabola through three equally spaced points actually lies, in samples
    /// either side of the middle one. This is what takes the estimate below a whole sample.
    /// </summary>
    internal static double ParabolicOffset(double before, double peak, double after)
    {
        double denominator = before - 2 * peak + after;
        if (Math.Abs(denominator) < 1e-20) return 0;
        double offset = 0.5 * (before - after) / denominator;
        return Math.Abs(offset) <= 1 ? offset : 0;
    }

    /// <summary>
    /// Shifts the right channel by <paramref name="delaySamples"/> to bring it back onto the left.
    /// </summary>
    /// <remarks>
    /// Half the correction is applied to each channel in opposite directions, so the programme stays
    /// where it was in time rather than sliding by the whole correction — which matters when a
    /// transfer has already been cut to length or has markers on it.
    /// </remarks>
    /// <remarks>
    /// <b>Stereo only.</b> A stylus reads two groove walls and there is no third one to be late, so
    /// beyond a pair the correction is undefined. Shifting the first two channels of a multichannel
    /// file would leave every other channel where it was and offset it from the pair by half the
    /// delay — introducing exactly the inter-channel misalignment this exists to remove, in the
    /// channels it never measured. Anything that is not a pair is left alone.
    /// </remarks>
    public static void Align(float[][] channels, double delaySamples,
        int halfTaps = Interpolation.DefaultHalfTaps)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length != 2 || Math.Abs(delaySamples) < 1e-9) return;

        // The right channel lags by delaySamples, so it moves earlier and the left moves later by
        // half each. Getting this the wrong way round doubles the error instead of removing it,
        // which is why CorrectingRemovesTheDelayItMeasured re-measures rather than trusting the sign.
        channels[0] = Interpolation.Shift(channels[0], delaySamples / 2, halfTaps);
        channels[1] = Interpolation.Shift(channels[1], -delaySamples / 2, halfTaps);
    }

    private static double Median(List<double> sorted) =>
        sorted.Count == 0 ? 0
        : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
        : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static double MedianAbsoluteDeviation(List<double> values, double centre)
    {
        var deviations = new List<double>(values.Count);
        foreach (double value in values) deviations.Add(Math.Abs(value - centre));
        deviations.Sort();
        return Median(deviations);
    }
}
