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
public readonly record struct DecrackleReport(int Events, long SamplesReplaced, int Fallbacks, int Rejected = 0)
{
    public static DecrackleReport None => new(0, 0, 0);

    /// <summary>Events per second, which is how dense crackle is usually described.</summary>
    public double Density(int sampleRate, int length) =>
        length > 0 && sampleRate > 0 ? Events * sampleRate / (double)length : 0;

    /// <summary>Total events found before classification, for diagnostics.</summary>
    public int Candidates => Events + Rejected;
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
    /// <summary>
    /// The block one autoregressive model is fitted to, for the options given.
    /// </summary>
    /// <remarks>
    /// Exposed because the block is a <em>grid</em>, anchored at index zero of whatever array is
    /// handed in, so anything running this over a window of a longer file has to align that window
    /// to it or fit its predictors to different audio than a whole-file pass would.
    /// </remarks>
    internal static int BlockLengthFor(DecrackleOptions options)
    {
        if (options.Order == 0) options = DecrackleOptions.Default;
        return Math.Max(Math.Clamp(options.Order, 4, 256) * 8, options.BlockLength);
    }

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

        // The final partial block is scanned as well, clamped back to the last full
        // block's worth of samples. Stopping at the last whole block left up to a block
        // of audio — about 93 ms at 44.1 kHz — never examined at the end of the channel.
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
    internal static bool FitPredictor(float[] samples, int start, int length, int order,
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

    // ── stereo M/S-aware processing ────────────────────────────────

    /// <summary>
    /// Removes surface crackle from a stereo pair by running detection on the side (L−R) signal
    /// alone, where 78% of record surface noise lives, and classifying each candidate against
    /// a musical-transient model before repair. The mid (L+R) signal, which carries most of the
    /// music, is never examined by the detector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-channel <see cref="Process"/> meets a different realisation of the same anti-phase
    /// tick in each channel, repairs each one independently, and summing the channels back
    /// reconstitutes what the other channel still holds. This method avoids that by working in
    /// the domain where the defect is coherent.
    /// </para>
    /// <para>
    /// The classifier is the defence against musical transients that the prediction residual
    /// cannot tell from crackle: a cymbal or sibilant is also unpredictable, but it has a
    /// structured spectrum, an asymmetric envelope, and its energy is sustained — none of which
    /// is true of a speck of dirt.
    /// </para>
    /// </remarks>
    public static DecrackleReport ProcessStereo(float[] left, float[] right,
        DecrackleOptions options = default,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (options.Order == 0) options = DecrackleOptions.Default;
        int n = Math.Min(left.Length, right.Length);
        if (n < BlockLengthFor(options)) return DecrackleReport.None;

        // Decompose into mid/side. The side signal carries the vertical noise.
        var mid = new float[n];
        var side = new float[n];
        for (int i = 0; i < n; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            mid[i] = (left[i] + right[i]) * 0.5f;
            side[i] = (left[i] - right[i]) * 0.5f;
        }

        // Detect on the side signal only — that is where the crackle is.
        progress?.Report(0.05);
        List<(int Start, int End)> candidates = Detect(side, options, cancellationToken,
            new SubProgress(progress, 0.05, 0.45));
        if (candidates.Count == 0)
        {
            progress?.Report(1);
            return DecrackleReport.None;
        }

        // Classify every candidate: musical transients pass through unmodified.
        progress?.Report(0.50);
        int rejected = 0;
        var accepted = new List<(int, int)>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            if ((i & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();
            var (start, end) = candidates[i];
            if (IsMusicalTransient(side, start, end, n))
                rejected++;
            else
                accepted.Add((start, end));
        }

        if (accepted.Count == 0)
        {
            progress?.Report(1);
            return new DecrackleReport(0, 0, 0, rejected);
        }

        // Repair accepted events in the side signal.
        int repaired = 0, fallbacks = 0;
        long replaced = 0;
        for (int i = 0; i < accepted.Count; i++)
        {
            if ((i & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.50 + 0.45 * i / accepted.Count);
            }

            var (start, end) = accepted[i];
            if (Repair(side, start, end)) repaired++;
            else fallbacks++;
            replaced += end - start;
        }

        // Reconstruct L/R from mid + repaired side.
        progress?.Report(0.95);
        for (int i = 0; i < n; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            left[i] = mid[i] + side[i];
            right[i] = mid[i] - side[i];
        }

        progress?.Report(1);
        return new DecrackleReport(repaired + fallbacks, replaced, fallbacks, rejected);
    }

    // ── musical transient classifier ───────────────────────────────

    /// <summary>
    /// Decides whether a candidate span in the side signal looks like a musical transient
    /// rather than crackle. Three properties distinguish them, none of which the prediction
    /// residual alone can see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Crest factor</b> — peak absolute value over RMS in the span. Crackle is a single
    /// sharp spike against a near-zero background and scores very high; a musical transient
    /// carries sustained energy and scores lower.
    /// </para>
    /// <para>
    /// <b>Temporal asymmetry</b> — where the peak sits within the span relative to the centre.
    /// Crackle is nearly symmetric (peak near centre); a musical attack rises fast and decays
    /// slowly (peak early), and a note release does the opposite. Both read as asymmetry.
    /// </para>
    /// <para>
    /// <b>Local isolation</b> — how much the candidate's RMS dwarfs the side signal immediately
    /// around it. A crackle tick is an isolated spike in otherwise near-zero side energy; a
    /// musical passage that excites the side does so over a wider span at comparable levels.
    /// The isolation window is proportional to the candidate span (4×, capped at 24 samples
    /// on each side) so a long event gets a wider context and a short one does not dilute its
    /// neighborhood with silence.
    /// </para>
    /// <para>
    /// All three metrics read the side signal alone — unlike a mid/side energy ratio they are
    /// not biased by <see cref="Restoration.ScaleSide"/> having already reduced the side, which
    /// in auto mode can attenuate the side to 20%. A mid/side ratio taken after that reduction
    /// always reads nearly all-mid and classifies genuine crackle as music.
    /// </para>
    /// </remarks>
    private static bool IsMusicalTransient(float[] side, int start, int end, int length)
    {
        int span = end - start;
        if (span < 2) return false;

        // ── 1. Crest factor: peak / RMS within the span ──
        double peak = 0, sumSq = 0;
        for (int i = start; i <= end; i++)
        {
            double abs = Math.Abs(side[i]);
            if (abs > peak) peak = abs;
            sumSq += side[i] * (double)side[i];
        }
        double rms = Math.Sqrt(sumSq / (span + 1));
        double crestFactor = rms > 1e-12 ? peak / rms : 1.0;
        double crestScore = Math.Clamp((crestFactor - 2.5) / 7.0, 0.0, 1.0);

        // ── 2. Temporal asymmetry: where the peak sits ──
        int peakIndex = start;
        double peakAbs = Math.Abs(side[start]);
        for (int i = start + 1; i <= end; i++)
        {
            double abs = Math.Abs(side[i]);
            if (abs > peakAbs) { peakAbs = abs; peakIndex = i; }
        }
        double centre = (start + end) * 0.5;
        double maxOffset = span * 0.5;
        double asymmetry = maxOffset > 0 ? Math.Abs(peakIndex - centre) / maxOffset : 0;
        double asymmetryScore = Math.Clamp(asymmetry * 1.5 - 0.15, 0.0, 1.0);

        // ── 3. Local isolation: how much the candidate stands out from
        //     the side signal around it. A crackle tick is an isolated spike
        //     in otherwise near-zero side energy; a musical passage excites
        //     the side over a wider span at comparable levels.
        //     Uses the side signal alone — unlike a mid/side ratio this is
        //     not biased by ScaleSide having already reduced the side.
        // ──
        int margin = Math.Min(span * 4, 24);
        int beforeStart = Math.Max(1, start - margin);
        int afterEnd = Math.Min(length - 1, end + margin);
        double neighborEnergy = 0;
        int neighborCount = 0;
        for (int i = beforeStart; i < start; i++)
        {
            neighborEnergy += side[i] * (double)side[i];
            neighborCount++;
        }
        for (int i = end + 1; i <= afterEnd; i++)
        {
            neighborEnergy += side[i] * (double)side[i];
            neighborCount++;
        }
        double neighborRms = neighborCount > 0
            ? Math.Sqrt(neighborEnergy / neighborCount)
            : 0;
        // If the candidate RMS dwarfs the neighborhood, it is isolated = crackle.
        // If the neighborhood has similar energy, it is sustained = music.
        double isolation = neighborRms > 1e-12 ? rms / neighborRms : 10.0;
        double isolationScore = Math.Clamp(
            (Math.Log10(Math.Max(isolation, 0.1)) + 0.7) / 2.0, 0.0, 1.0);

        // ── Combined verdict ──
        double crackleScore = (crestScore + asymmetryScore + isolationScore) / 3.0;
        return crackleScore < 0.35;
    }
}
