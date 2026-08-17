using WaveLab.Util;

namespace WaveLab.Audio.Dsp;

/// <summary>Settings for crackle detection and repair.</summary>
/// <param name="Order">Order of the predictor the residual is taken from.</param>
/// <param name="BlockLength">How much audio each model is fitted to.</param>
/// <param name="Threshold">How many robust deviations a residual must exceed to count.</param>
/// <param name="MaximumRunLength">Longest run of samples treated as one crackle event.</param>
/// <param name="Guard">Samples either side of a detection that are replaced along with it.</param>
public readonly record struct DecrackleOptions(
    int Order = 32,
    int BlockLength = 4096,
    double Threshold = 3.5,
    int MaximumRunLength = 12,
    int Guard = 1)
{
    /// <remarks>Spelled out rather than <c>new()</c>, which zero-initialises a record struct.</remarks>
    public static DecrackleOptions Default { get; } = new(
        Order: 32,
        BlockLength: 4096,
        Threshold: 3.5,
        MaximumRunLength: 12,
        Guard: 1);
}

/// <summary>What a de-crackle pass did.</summary>
public readonly record struct DecrackleReport(int Events, long SamplesReplaced, int Fallbacks)
{
    public static DecrackleReport None => new(0, 0, 0);

    /// <summary>Events per second, which is how dense crackle is usually described.</summary>
    public double Density(int sampleRate, int length) =>
        length > 0 && sampleRate > 0 ? Events * sampleRate / (double)length : 0;
}

/// <summary>
/// Removes surface crackle: the dense, low-level population of tiny defects a record accumulates,
/// as distinct from the discrete clicks and pops that <see cref="Restoration"/> already finds.
/// </summary>
/// <remarks>
/// <para>
/// The difference is not one of degree. A click is loud enough to see, so a curvature test on the
/// waveform finds it; crackle sits at or below the level of the music and a curvature test cannot
/// tell it from a cymbal. What distinguishes crackle is that it is <em>unpredictable</em> — music is
/// highly self-similar over a few milliseconds, and a linear predictor fitted to it will forecast
/// the next sample closely, while a speck of dirt is not forecast at all. So the detector works on
/// the <b>prediction residual</b> of a high-order autoregressive model rather than on the waveform:
/// the music largely vanishes from the residual and the defects stand out of it.
/// </para>
/// <para>
/// The threshold is measured in robust deviations of the residual itself, per block, so it follows
/// the material: a quiet passage and a loud one get the same sensitivity in the terms that matter
/// rather than the same absolute level. Robust, because the outliers being hunted would otherwise
/// inflate the very deviation used to find them — the median absolute deviation ignores them, where
/// a standard deviation is dragged by them.
/// </para>
/// <para>
/// Repair is <see cref="Janssen"/> interpolation, the same estimator behind the de-click. The gaps
/// here are far shorter and far more numerous, which suits it: a two-sample gap surrounded by clean
/// audio is the case an autoregressive interpolator reconstructs almost exactly.
/// </para>
/// </remarks>
public static class Decrackle
{
    /// <summary>Finds and repairs crackle in place, returning what it did.</summary>
    public static DecrackleReport Process(float[] samples, DecrackleOptions options = default,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (options.Order == 0) options = DecrackleOptions.Default;

        List<(int Start, int End)> events = Detect(samples, options, cancellationToken,
            new SubProgress(progress, 0, 0.5));
        if (events.Count == 0)
        {
            progress?.Report(1);
            return DecrackleReport.None;
        }

        int repaired = 0, fallbacks = 0;
        long replaced = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if ((i & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.5 + 0.5 * i / events.Count);
            }

            var (start, end) = events[i];
            if (Repair(samples, start, end)) repaired++;
            else fallbacks++;
            replaced += end - start;
        }

        progress?.Report(1);
        return new DecrackleReport(repaired + fallbacks, replaced, fallbacks);
    }

    // ── detection ────────────────────────────────────────────────

    /// <summary>
    /// Finds the spans where the signal departs from what a predictor fitted to it expects.
    /// </summary>
    internal static List<(int Start, int End)> Detect(float[] samples, DecrackleOptions options,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var events = new List<(int, int)>();
        int order = Math.Clamp(options.Order, 4, 256);
        int block = Math.Max(order * 8, options.BlockLength);
        if (samples.Length < block) return events;

        var residual = new double[block];
        var magnitude = new double[block];
        var coefficients = new double[order + 1];
        var autocorrelation = new double[order + 1];

        for (int start = 0; start + block <= samples.Length; start += block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(start / (double)samples.Length);

            if (!FitPredictor(samples, start, block, order, autocorrelation, coefficients)) continue;

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
            }

            double scale = RobustScale(magnitude);
            if (scale <= 1e-9) continue;
            double limit = scale * Math.Max(1, options.Threshold);

            CollectRuns(magnitude, start, limit, options, samples.Length, events);
        }

        progress?.Report(1);
        return events;
    }

    /// <summary>
    /// Turns a block's outliers into spans, merging neighbours and discarding anything too long to
    /// be crackle.
    /// </summary>
    /// <remarks>
    /// A run longer than <see cref="DecrackleOptions.MaximumRunLength"/> is left alone deliberately.
    /// A sustained departure from the model is not a speck of dirt; it is a transient the predictor
    /// could not follow — a rimshot, a plosive, the start of a note — and replacing it with what the
    /// model expected would take the attack off the music. That is the failure mode that makes
    /// aggressive de-crackling sound dull.
    /// </remarks>
    private static void CollectRuns(double[] magnitude, int offset, double limit,
        DecrackleOptions options, int length, List<(int, int)> events)
    {
        int guard = Math.Max(0, options.Guard);
        int longest = Math.Max(1, options.MaximumRunLength);
        int i = 0;

        while (i < magnitude.Length)
        {
            if (magnitude[i] <= limit) { i++; continue; }

            int runStart = i;
            while (i < magnitude.Length && magnitude[i] > limit) i++;
            int runEnd = i;

            if (runEnd - runStart > longest) continue;

            int start = Math.Max(1, offset + runStart - guard);
            int end = Math.Min(length - 1, offset + runEnd + guard);
            if (end <= start) continue;

            // Merge with the previous span when the guards overlap, so one defect is repaired once.
            if (events.Count > 0 && events[^1].Item2 >= start)
            {
                var (previousStart, previousEnd) = events[^1];
                if (end - previousStart <= longest + 2 * guard + 2)
                {
                    events[^1] = (previousStart, Math.Max(previousEnd, end));
                    continue;
                }
            }
            events.Add((start, end));
        }
    }

    /// <summary>
    /// Fits an autoregressive model to a block by Levinson-Durbin on its autocorrelation.
    /// </summary>
    private static bool FitPredictor(float[] samples, int start, int length, int order,
        double[] autocorrelation, double[] coefficients)
    {
        for (int lag = 0; lag <= order; lag++)
        {
            double sum = 0;
            for (int i = lag; i < length; i++) sum += (double)samples[start + i] * samples[start + i - lag];
            autocorrelation[lag] = sum;
        }

        // A touch of ridge on the zero lag, so a nearly-silent or perfectly periodic block cannot
        // produce a singular system.
        if (autocorrelation[0] <= 1e-14) return false;
        autocorrelation[0] *= 1.0 + 1e-7;

        Array.Clear(coefficients);
        double error = autocorrelation[0];
        var work = new double[order + 1];

        for (int m = 1; m <= order; m++)
        {
            double acc = autocorrelation[m];
            for (int k = 1; k < m; k++) acc -= coefficients[k] * autocorrelation[m - k];
            double reflection = acc / error;
            if (!double.IsFinite(reflection) || Math.Abs(reflection) >= 1) return false;

            work[m] = reflection;
            for (int k = 1; k < m; k++) work[k] = coefficients[k] - reflection * coefficients[m - k];
            for (int k = 1; k <= m; k++) coefficients[k] = work[k];

            error *= 1 - reflection * reflection;
            if (error <= 1e-18) return false;
        }
        return true;
    }

    /// <summary>
    /// A scale for the residual that the outliers themselves cannot inflate: the median absolute
    /// value, scaled to match a standard deviation on well-behaved material.
    /// </summary>
    internal static double RobustScale(double[] magnitude)
    {
        if (magnitude.Length == 0) return 0;
        var sorted = (double[])magnitude.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];

        // 1.4826 is what turns a median absolute value into a standard deviation for Gaussian data,
        // which is what makes a threshold expressed in deviations mean the usual thing.
        return median * 1.4826;
    }

    // ── repair ───────────────────────────────────────────────────

    private static bool Repair(float[] samples, int start, int end)
    {
        JanssenOptions options = JanssenOptions.For(end - start, outputLimit: 1.5);
        if (Janssen.TryInterpolate(samples, start, end, options, out double[] reconstruction))
        {
            for (int i = 0; i < reconstruction.Length; i++)
                samples[start + i] = (float)reconstruction[i];
            return true;
        }

        // Nothing to fit against: bridge it, which for a gap this short is barely different.
        double first = samples[start - 1];
        double last = samples[Math.Min(samples.Length - 1, end)];
        int span = end - start + 1;
        for (int i = 0; i < end - start; i++)
            samples[start + i] = (float)(first + (last - first) * (i + 1) / span);
        return false;
    }
}
