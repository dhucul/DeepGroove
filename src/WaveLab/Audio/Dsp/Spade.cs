namespace WaveLab.Audio.Dsp;

/// <summary>Tuning for <see cref="Spade"/>.</summary>
/// <param name="FrameSize">Transform length; must be a power of two.</param>
/// <param name="Hop">Frame advance; must divide <paramref name="FrameSize"/>.</param>
/// <param name="SparsityStep">How many coefficients the model is allowed to grow by.</param>
/// <param name="RelaxEvery">Iterations between each relaxation of the sparsity budget.</param>
/// <param name="MaxIterations">Cap on iterations per frame.</param>
/// <param name="Tolerance">Relative residual at which a frame is considered solved.</param>
public readonly record struct SpadeOptions(
    int FrameSize, int Hop, int SparsityStep, int RelaxEvery, int MaxIterations, double Tolerance)
{
    /// <remarks>
    /// The 1024-sample frame is measured, not inherited. The published results use 1024 at 16 kHz —
    /// 64 ms — and the obvious move at 44.1 kHz is to match that duration rather than the sample
    /// count. Measured across four clipping severities, that is wrong in every case: 1024 beats
    /// 2048, 4096 and 8192 monotonically, by 7.6 dB at the mildest setting. Longer frames are not
    /// worth revisiting without evidence.
    /// </remarks>
    public static SpadeOptions Default { get; } = new(
        FrameSize: 1024, Hop: 256, SparsityStep: 4, RelaxEvery: 1,
        MaxIterations: 120, Tolerance: 1e-3);

    /// <summary>
    /// Options for this material, with the sparsity budget matched to how many components it
    /// actually has. <b>Built, measured, and deliberately not the default</b> — see the remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The budget is the single biggest lever on this solver and the default starves dense
    /// material.</b> A step of four grows the model too slowly for a stack of two dozen partials
    /// over a noise bed: measured on such material at 20–50% clipped, the default scores 5.9 to
    /// 10.3 dB where a step of sixteen scores 15.0 to 18.2, and one cell moves 6.6 to 22.2. That
    /// band is the standing residual of <see cref="DeclipMethodChooser"/>, and it is not a limit of
    /// the method — it is the method being given a dozen coefficients to describe forty.
    /// </para>
    /// <para>
    /// The budget here is set from <see cref="EffectiveSparsity"/>, roughly an eighth of the bins
    /// holding 98% of a frame's energy, floored so simple material keeps the small step it wants. A
    /// flat larger step is not the answer: sixteen costs sparse synthetic material 4.7 dB a cell and
    /// 24.7 in the worst case. Over 105 cells this beats the flat default on synthetic (818.6
    /// against 743.5) and on real audio (1077.3 against 1072.1), and beats the best flat step held
    /// out across thirty-one groups.
    /// </para>
    /// <para>
    /// <b>And it still makes the shipped chain worse on real programme, which is why the chain does
    /// not use it.</b> Measured end to end over nineteen recordings, the default gives 76 of 76
    /// cells better than leaving the damage alone, mean +6.20 dB, worst +0.85. This gives 5.74.
    /// Re-fitting the chooser against the improved solver — which is required, since its thresholds
    /// were calibrated against the old one, and which recovers 226.5 to 158.9 dB held out on the
    /// method choice — still lands at 5.80 with one cell at −1.70, the only cell in the corpus where
    /// the chain would lose to doing nothing. The gain is concentrated where A-SPADE was losing to
    /// the arch anyway, so the better solver mostly improves cells the chooser does not send it,
    /// while the cells it does send it come out slightly worse.
    /// </para>
    /// <para>
    /// So this is kept the way <c>SparseInpaint</c> and <c>RobustPca</c> are kept: correct, tested,
    /// documented, and not wired in. It is the right starting point if the dense band is attacked
    /// again — the cause is known and the lever is large — but it needs a chooser fitted to the new
    /// solver on more real material than one corpus, not another synthetic recalibration. Take the
    /// warning on <c>JanssenOptions.For</c> as applying here too: refitting this on tones alone will
    /// produce a different and worse answer.
    /// </para>
    /// <param name="samples">The clipped audio the solver will be given.</param>
    /// <param name="clipLevel">Magnitude at or beyond which a sample is damaged, so the measurement
    /// can be taken from frames that survived. Pass zero to measure everything.</param>
    public static SpadeOptions For(float[] samples, double clipLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double bins = EffectiveSparsity(samples, clipLevel);
        int step = (int)Math.Round(BudgetIntercept + BudgetPerBin * bins);
        return Default with { SparsityStep = Math.Clamp(step, MinimumStep, MaximumStep) };
    }

    /// <summary>Floor of the sparsity budget, which is what simple material wants.</summary>
    public const int MinimumStep = 4;

    /// <summary>Ceiling of the sparsity budget.</summary>
    public const int MaximumStep = 16;

    /// <summary>Budget at zero measured components.</summary>
    public const double BudgetIntercept = 3.5;

    /// <summary>Extra budget per significant bin the material carries.</summary>
    public const double BudgetPerBin = 0.125;

    /// <summary>Transform length the sparsity measure is taken at, matching <see cref="Default"/>.</summary>
    private const int MeasureFrame = 1024;

    /// <summary>Share of a frame that may be clipped before it is too damaged to measure.</summary>
    private const double MaximumFrameDamage = 0.05;

    /// <summary>Fewest surviving frames worth trusting before measuring everything instead.</summary>
    private const int MinimumCleanFrames = 8;

    /// <summary>
    /// How many frequency bins hold 98% of a frame's energy, averaged over frames. Low is sparse.
    /// </summary>
    /// <remarks>
    /// Measured on frames that survived the clipping where there are enough of them, because a flat
    /// top is a corner and a corner is broadband: damage inflates this number, and the more damage
    /// the more inflation. <b>The fallback matters as much as the measurement</b> — returning a
    /// constant when too few clean frames remain makes the reading saturate on exactly the heavily
    /// damaged material the budget is for, and an earlier version of this fitted worse than a flat
    /// step because of it.
    /// </remarks>
    public static double EffectiveSparsity(float[] samples, double clipLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length < MeasureFrame) return 0;
        if (clipLevel > 0)
        {
            double clean = Measure(samples, clipLevel, MinimumCleanFrames);
            if (clean > 0) return clean;
        }
        return Measure(samples, 0, 1);
    }

    private static double Measure(float[] samples, double clipLevel, int minimumFrames)
    {
        int bins = MeasureFrame / 2 + 1;
        var frame = new float[MeasureFrame];
        var re = new float[bins];
        var im = new float[bins];
        var power = new double[bins];
        float[] window = WindowFunctions.Hann(MeasureFrame, periodic: true);

        // Hunting for clean frames needs a finer comb than measuring everything, or a busy side
        // offers too few of them to average.
        int stride = clipLevel > 0 ? MeasureFrame / 4 : MeasureFrame;
        double total = 0;
        int counted = 0;
        for (int start = 0; start + MeasureFrame <= samples.Length; start += stride)
        {
            if (clipLevel > 0)
            {
                int damaged = 0;
                for (int i = 0; i < MeasureFrame; i++)
                    if (Math.Abs(samples[start + i]) >= clipLevel - 1e-5) damaged++;
                if (damaged > MeasureFrame * MaximumFrameDamage) continue;
            }

            for (int i = 0; i < MeasureFrame; i++) frame[i] = samples[start + i] * window[i];
            Fft.RealForward(frame, re, im);
            double sum = 0;
            for (int b = 0; b < bins; b++)
            {
                power[b] = re[b] * (double)re[b] + im[b] * (double)im[b];
                sum += power[b];
            }
            if (sum <= 1e-18) continue;

            Array.Sort(power);
            double running = 0;
            int used = 0;
            for (int b = bins - 1; b >= 0; b--)
            {
                running += power[b];
                used++;
                if (running >= 0.98 * sum) break;
            }
            total += used;
            counted++;
        }
        return counted >= minimumFrames ? total / counted : 0;
    }
}

/// <summary>Result of a declipping pass.</summary>
public sealed record SpadeResult(int FramesProcessed, int FramesRepaired, long ClippedSamples, double MeanIterations);

/// <summary>
/// A-SPADE: sparse audio declipping by alternating between a sparsity model and the constraints the
/// clipped signal imposes (Kitić, Bertin &amp; Gribonval, 2015).
/// </summary>
/// <remarks>
/// <para>
/// The reconstruction already in <c>Restoration.Advanced</c> rebuilds a flattened peak by fitting a
/// curve between the shoulders either side of it. That works well while the plateau is short and the
/// waveform through it is simple, and it has nothing to say about heavily crushed material, where
/// whole cycles are missing and there is no single arch to draw.
/// </para>
/// <para>
/// This takes the opposite view: the clipped samples are unknown, and everything known about them is
/// an inequality — a sample that railed high was <em>at least</em> the clipping level. Among all
/// signals that agree with the reliable samples and satisfy those inequalities, it looks for the one
/// with the fewest significant frequency components, which is the sense in which music is sparse and
/// clipping is not. Alternating between "make it sparse" (keep the largest coefficients) and "make it
/// feasible" (restore the reliable samples, push the clipped ones back past the rail) converges on a
/// signal that is both.
/// </para>
/// <para>
/// The sparsity budget starts small and is relaxed as the iteration proceeds, so the solver commits
/// to the strongest structure first — that is what stops it from simply reproducing the flat top,
/// which is itself perfectly consistent with the constraints.
/// </para>
/// </remarks>
public static class Spade
{
    /// <summary>
    /// Reconstructs samples at or beyond <paramref name="clipLevel"/> in place.
    /// </summary>
    /// <param name="samples">Audio to repair; modified in place.</param>
    /// <param name="clipLevel">Magnitude at or above which a sample is considered unreliable.</param>
    public static SpadeResult Declip(float[] samples, double clipLevel, SpadeOptions options = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (options.FrameSize == 0) options = SpadeOptions.Default;
        int n = options.FrameSize;
        int hop = options.Hop;
        if (n < 32 || (n & (n - 1)) != 0)
            throw new ArgumentException("Frame size must be a power of two of at least 32.", nameof(options));
        if (hop <= 0 || n % hop != 0)
            throw new ArgumentException("Hop must divide the frame size.", nameof(options));
        if (!(clipLevel > 0)) throw new ArgumentOutOfRangeException(nameof(clipLevel));

        int length = samples.Length;
        if (length == 0) return new SpadeResult(0, 0, 0, 0);

        float[] window = WindowFunctions.Sqrt(WindowFunctions.Hann(n, periodic: true));
        int bins = n / 2 + 1;

        var output = new double[length];
        var weight = new double[length];

        var frame = new float[n];
        var estimate = new float[n];
        var projected = new float[n];
        var binRe = new float[bins];
        var binIm = new float[bins];
        var dualRe = new double[bins];
        var dualIm = new double[bins];
        var sumRe = new double[bins];
        var sumIm = new double[bins];
        var magnitude = new double[bins];
        var order = new int[bins];

        int framesProcessed = 0, framesRepaired = 0;
        long clippedSamples = 0, totalIterations = 0;

        for (int start = -(n - hop); start < length; start += hop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            framesProcessed++;

            int clippedInFrame = 0;
            for (int i = 0; i < n; i++)
            {
                int index = start + i;
                float value = (uint)index < (uint)length ? samples[index] : 0f;
                frame[i] = value;
                // Windowed here so that both paths below hand Accumulate the same thing: a frame
                // that has already had the analysis window applied once.
                estimate[i] = value * window[i];
                if ((uint)index < (uint)length && IsRailed(value, clipLevel)) clippedInFrame++;
            }

            if (clippedInFrame > 0)
            {
                framesRepaired++;
                clippedSamples += clippedInFrame;
                totalIterations += Solve(frame, estimate, projected, window, clipLevel, n, bins, options,
                    binRe, binIm, dualRe, dualIm, sumRe, sumIm, magnitude, order);
            }

            // Frames with nothing to solve fall straight through, so the cost is proportional to the
            // damage rather than to the length of the file.
            Accumulate(output, weight, window, estimate, start, length, n);
        }

        for (int i = 0; i < length; i++)
        {
            if (weight[i] > 1e-6)
            {
                double value = output[i] / weight[i];
                samples[i] = float.IsFinite((float)value) ? (float)value : samples[i];
            }
        }

        return new SpadeResult(framesProcessed, framesRepaired, clippedSamples,
            framesRepaired == 0 ? 0 : (double)totalIterations / framesRepaired);
    }

    /// <summary>
    /// Whether a sample sits at or beyond the rail. The comparison carries a small tolerance because
    /// the recorded value is a float and the threshold a double: a file clipped at 0.35 stores
    /// 0.34999999, which a naive <c>&gt;=</c> against the double 0.35 rejects — and then nothing at
    /// all is detected as clipped.
    /// </summary>
    private static bool IsRailed(double value, double clipLevel) =>
        Math.Abs(value) >= clipLevel - RailTolerance;

    private const double RailTolerance = 1e-5;

    /// <summary>One frame of A-SPADE. Returns the iterations used.</summary>
    private static int Solve(float[] frame, float[] estimate, float[] projected, float[] window,
        double clipLevel, int n, int bins, SpadeOptions options,
        float[] binRe, float[] binIm, double[] dualRe, double[] dualIm,
        double[] sumRe, double[] sumIm, double[] magnitude, int[] order)
    {
        // `estimate` arrives already windowed; synthesis applies the window again and the running
        // weight divides out the square, so the pair reconstructs exactly where nothing is changed.
        Array.Clear(dualRe);
        Array.Clear(dualIm);

        int sparsity = Math.Max(1, options.SparsityStep);
        int iterations = 0;

        for (int iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            iterations++;
            Fft.RealForward(estimate, binRe, binIm);

            double residual = 0, energy = 0;
            for (int b = 0; b < bins; b++)
            {
                sumRe[b] = binRe[b] + dualRe[b];
                sumIm[b] = binIm[b] + dualIm[b];
                magnitude[b] = sumRe[b] * sumRe[b] + sumIm[b] * sumIm[b];
                order[b] = b;
                energy += magnitude[b];
            }

            // Keep the strongest `sparsity` bins; everything else is set aside as the part of the
            // signal the model does not yet explain.
            int keep = Math.Min(bins, sparsity);
            PartialSortDescending(order, magnitude, keep, bins);

            Array.Clear(sumRe, 0, bins);
            Array.Clear(sumIm, 0, bins);
            for (int i = 0; i < keep; i++)
            {
                int b = order[i];
                sumRe[b] = binRe[b] + dualRe[b];
                sumIm[b] = binIm[b] + dualIm[b];
            }

            for (int b = 0; b < bins; b++)
            {
                double dr = binRe[b] + dualRe[b] - sumRe[b];
                double di = binIm[b] + dualIm[b] - sumIm[b];
                residual += dr * dr + di * di;
            }
            if (energy <= 0 || residual / energy <= options.Tolerance * options.Tolerance) break;

            // x <- argmin ||Ax - (z - u)|| subject to the clipping constraints. The transform is
            // orthogonal up to a scale, so that minimiser is just the inverse transform projected
            // onto the feasible set — no inner solve required.
            for (int b = 0; b < bins; b++)
            {
                binRe[b] = (float)(sumRe[b] - dualRe[b]);
                binIm[b] = (float)(sumIm[b] - dualIm[b]);
            }
            Fft.RealInverse(binRe, binIm, projected);
            Project(projected, frame, window, clipLevel, n);
            Array.Copy(projected, estimate, n);

            Fft.RealForward(estimate, binRe, binIm);
            for (int b = 0; b < bins; b++)
            {
                dualRe[b] += binRe[b] - sumRe[b];
                dualIm[b] += binIm[b] - sumIm[b];
            }

            if ((iteration + 1) % Math.Max(1, options.RelaxEvery) == 0)
                sparsity += Math.Max(1, options.SparsityStep);
        }

        return iterations;
    }

    /// <summary>
    /// Forces the frame back onto the set of signals the observation allows: reliable samples are
    /// what was recorded, and a sample that railed must stay at least as far out as the rail.
    /// </summary>
    private static void Project(float[] candidate, float[] original, float[] window,
        double clipLevel, int n)
    {
        for (int i = 0; i < n; i++)
        {
            double raw = original[i];
            double scale = window[i];
            double windowed = raw * scale;

            if (raw > 0 && IsRailed(raw, clipLevel))
            {
                // Clipped positive: anything at or above the rail is admissible, nothing below it.
                double floor = (clipLevel - RailTolerance) * scale;
                if (candidate[i] < floor) candidate[i] = (float)floor;
            }
            else if (raw < 0 && IsRailed(raw, clipLevel))
            {
                double ceiling = -(clipLevel - RailTolerance) * scale;
                if (candidate[i] > ceiling) candidate[i] = (float)ceiling;
            }
            else
            {
                candidate[i] = (float)windowed;
            }
        }
    }

    private static void Accumulate(double[] output, double[] weight, float[] window,
        float[] frame, int start, int length, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int index = start + i;
            if ((uint)index >= (uint)length) continue;
            output[index] += frame[i] * window[i];
            weight[index] += (double)window[i] * window[i];
        }
    }

    /// <summary>
    /// Places the indices of the <paramref name="keep"/> largest magnitudes at the front of
    /// <paramref name="order"/>. Quickselect rather than a full sort: the budget is typically a few
    /// dozen of several hundred bins, and this runs on every iteration of every damaged frame.
    /// </summary>
    private static void PartialSortDescending(int[] order, double[] magnitude, int keep, int count)
    {
        if (keep >= count) return;
        int low = 0, high = count - 1;
        while (low < high)
        {
            double pivot = magnitude[order[(low + high) >> 1]];
            int i = low, j = high;
            while (i <= j)
            {
                while (magnitude[order[i]] > pivot) i++;
                while (magnitude[order[j]] < pivot) j--;
                if (i <= j)
                {
                    (order[i], order[j]) = (order[j], order[i]);
                    i++;
                    j--;
                }
            }
            if (keep <= j) high = j;
            else if (keep >= i) low = i;
            else break;
        }
    }
}
