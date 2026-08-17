namespace WaveLab.Audio.Dsp;

/// <summary>
/// How a coefficient's fate is decided when the solver thresholds it.
/// </summary>
/// <remarks>
/// The difference between these is the whole point of "social" sparsity. Plain thresholding judges
/// every coefficient alone, so it keeps loud isolated cells and discards quiet ones that are part of
/// a partial — which is backwards for audio, where what makes a cell meaningful is that its
/// neighbours in time (a sustained tone) or in frequency (a transient) are meaningful too.
/// </remarks>
public enum SocialShrinkage
{
    /// <summary>
    /// Soft thresholding on each coefficient's own magnitude. The baseline, kept because it is what
    /// the structured operators have to beat for their extra cost to be worth paying.
    /// </summary>
    Lasso,

    /// <summary>
    /// Windowed group lasso: the threshold is applied to the centre coefficient but decided by the
    /// root-mean-square magnitude of its neighbourhood. A quiet cell inside a strong partial
    /// survives; a loud cell with nothing around it does not.
    /// </summary>
    WindowedGroupLasso,

    /// <summary>
    /// Persistent empirical Wiener: the same neighbourhood, shrunk by a Wiener-like law rather than
    /// a linear one, so what survives is attenuated less. Siedenburg and Kowalski report this as the
    /// best of the family on audio, and it measures best here too.
    /// </summary>
    PersistentEmpiricalWiener,
}

/// <summary>Settings for <see cref="SparseInpaint"/>.</summary>
/// <param name="Iterations">FISTA steps. Each costs one synthesis and one analysis of the block.</param>
/// <param name="Shrinkage">Which shrinkage operator plays the role of the proximal step.</param>
/// <param name="TimeRadius">Neighbourhood half-width in frames. Wider favours sustained partials.</param>
/// <param name="FrequencyRadius">Neighbourhood half-height in bins. Wider favours transients.</param>
/// <param name="LambdaFloorRatio">Where the threshold schedule ends, as a fraction of where it starts.</param>
/// <param name="LambdaQuantile">
/// Which quantile of the observed neighbourhood level the schedule starts from. Not the maximum: one
/// exceptional cell would otherwise set the threshold for the whole block.
/// </param>
/// <param name="EmptyBias">
/// How strongly a cell the caller handed over is presumed to want leaving alone. The caller passes
/// the estimate it already has as the data, so inside the region being solved that estimate is
/// whatever it gave up on — usually nothing — and this is the weight of the pull back towards it.
/// Zero lets consistency and sparsity decide the reconstruction outright.
/// </param>
/// <param name="PenalizeSelectionOnly">
/// Whether the penalty applies only inside the region being solved. It should: the coefficients
/// outside are known exactly, and shrinking them wastes the solver on rebuilding what it was told.
/// </param>
/// <param name="HardDataConstraint">
/// Whether the known coefficients are written back exactly after every step rather than being left
/// to the data term to restore.
/// </param>
public readonly record struct SparseInpaintOptions(
    int Iterations = 60,
    SocialShrinkage Shrinkage = SocialShrinkage.PersistentEmpiricalWiener,
    int TimeRadius = 3,
    int FrequencyRadius = 1,
    double LambdaFloorRatio = 0.30,
    double LambdaQuantile = 0.98,
    double EmptyBias = 0.05,
    bool PenalizeSelectionOnly = true,
    bool HardDataConstraint = true)
{
    /// <remarks>
    /// Spelled out field by field rather than written <c>new()</c>, for the reason recorded on
    /// <see cref="SpectralRepairOptions"/>: on a record struct the parameterless form zero-initialises
    /// instead of applying these defaults, which here would mean zero iterations.
    /// </remarks>
    public static SparseInpaintOptions Default { get; } = new(
        Iterations: 60,
        Shrinkage: SocialShrinkage.PersistentEmpiricalWiener,
        TimeRadius: 3,
        FrequencyRadius: 1,
        LambdaFloorRatio: 0.30,
        LambdaQuantile: 0.98,
        EmptyBias: 0.05,
        PenalizeSelectionOnly: true,
        HardDataConstraint: true);

    /// <summary>This, or <see cref="Default"/> when it was left zero-initialised.</summary>
    internal SparseInpaintOptions OrDefault() => Iterations <= 0 ? Default : this;
}

/// <summary>
/// Sparse Gabor inpainting: rebuilds a region of the time-frequency plane as the sparsest signal
/// consistent with everything around it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes this work is that the Gabor transform is redundant.</b> Overlapping windows mean a
/// coefficient inside the hole is not free — it is tied to the observed coefficients outside by the
/// simple fact that both have to come from one signal. Writing that down gives a projection
/// <c>T = A∘S</c>, analyse-after-synthesise, which maps any grid of numbers to the nearest grid a
/// real signal could actually have produced. Iterating that projection against the observation is
/// Papoulis–Gerchberg, which this app already had. The step from there to here is replacing the hard
/// projection with a <b>proximal step</b>, so that on every iteration the estimate is not merely made
/// consistent but also made sparse — and sparse in the way audio is sparse.
/// </para>
/// <para>
/// The problem solved is
/// <c>min ½‖W ⊙ (Tα − d)‖² + λ Ω(α)</c>, with <c>d</c> the observation outside the selection,
/// <c>W</c> its reliability, and <c>Ω</c> a structured sparsity penalty. Two things about that are
/// load-bearing. The data term is on <c>Tα</c> and not on <c>α</c>: penalising the coefficients
/// directly would leave the ones inside the hole coupled to nothing, and the cheapest way to satisfy
/// a penalty with no data behind it is to set them to zero — a solver that confidently returns
/// silence. And because <c>T</c> is a projection its norm is one, so the gradient step size is one
/// and there is nothing to tune; the Lipschitz constant is still measured rather than assumed,
/// because the block's first and last frames are not covered by a full set of overlapping windows and
/// the frame is not quite tight there.
/// </para>
/// <para>
/// <b>FISTA</b> (Beck–Teboulle) supplies the acceleration: the same cost per iteration as plain
/// iterative shrinkage, converging at 1/k² instead of 1/k, which on this problem is the difference
/// between sixty iterations and several hundred.
/// </para>
/// <para>
/// <b>The threshold descends rather than sitting still.</b> Starting high and ending low is a
/// homotopy: the first iterations keep only the strongest structure, so the hole is filled by the
/// partials that certainly belong there, and later ones admit progressively finer detail on top. A
/// fixed threshold either never lets the detail in or lets the noise in from the start. The starting
/// point is read off the material's own neighbourhood levels, so nothing here depends on how loud the
/// file happens to be.
/// </para>
/// <para>
/// <b>The shrinkage operators are not proximal operators of convex penalties</b>, and that is
/// deliberate. Windowed group lasso and persistent empirical Wiener are the "social" shrinkages of
/// Kowalski and Siedenburg, which generalise the group lasso by letting a coefficient's neighbourhood
/// decide its threshold while shrinking only the coefficient itself. That generalisation is what
/// makes them work on audio and what costs them the convergence guarantee FISTA would otherwise
/// carry. In exchange the solver is bounded by its iteration count, never runs to a tolerance, and
/// its result is checked before it is accepted.
/// </para>
/// <para>
/// <b>Measured, it wins narrowly and loses badly.</b> Against the per-bin sinusoidal continuation it
/// was built to replace, on three kinds of synthetic material and at selection widths from 256 to
/// 8192 samples:
/// <list type="bullet">
/// <item>tonal programme, 512-sample selection — <b>15.7 dB to 19.0</b>, a clear win;</item>
/// <item>every selection of 4096 samples or wider — parity, because the guard hands back the
/// estimate it started from rather than a reconstruction it cannot support;</item>
/// <item>noisy programme at 512 samples — 11.3 dB to 2.4, and percussive 11.1 to −0.3.</item>
/// </list>
/// Two structural reasons, and neither is a tuning problem.
/// <b>Reach.</b> This reconstructs from local evidence, so it has something to work with only within
/// about a window of the selection's edge; a selection a user actually draws is 100 to 400 ms wide
/// and its middle is out of reach, where the estimate sags towards silence. Continuation extrapolates
/// a <i>model</i> — a sinusoid whose frequency was measured at the edge — and a model holds at any
/// width. <b>Predictability.</b> Where continuation refuses a cell it writes silence, and on noise and
/// on transients silence turns out to be the better estimate: that content genuinely cannot be
/// predicted from neighbouring frames, so a plausible fill uncorrelated with the truth scores worse
/// than a hole. Hence the default is the continuation, and this is offered rather than imposed.
/// </para>
/// <para>
/// The caveat on that verdict is the criterion. Signal-to-noise against a synthetic reference
/// punishes phase differences that may be inaudible and rewards silence, which is never what anyone
/// wants to hear. <b>Nobody has listened to either reconstruction.</b>
/// </para>
/// </remarks>
internal static class SparseInpaint
{
    /// <summary>Applies <c>T</c> in place: the projection onto coefficients a real signal could have.</summary>
    internal delegate void Projector(float[] re, float[] im);

    /// <summary>Power-iteration steps used to measure the gradient step size.</summary>
    private const int LipschitzIterations = 3;

    /// <summary>Headroom on the measured Lipschitz constant, so a slight underestimate cannot diverge.</summary>
    private const double LipschitzSafety = 1.05;

    /// <summary>
    /// How far above the observed level the reconstruction may land before it is disbelieved. A
    /// solver that has diverged announces itself by filling the hole with something enormous.
    /// </summary>
    private const double DivergenceRatio = 8.0;

    /// <summary>
    /// Refines a starting estimate into the sparsest signal consistent with what surrounds the
    /// selection. Returns false, leaving the estimate untouched, when the result cannot be trusted.
    /// </summary>
    /// <param name="observedRe">Observed coefficients, row-major frames × bins.</param>
    /// <param name="observedIm">Observed coefficients, row-major frames × bins.</param>
    /// <param name="maskWeight">1 where the user selected, 0 outside, feathered between.</param>
    /// <param name="re">In: the starting estimate. Out: the solution, if one was accepted.</param>
    /// <param name="im">In: the starting estimate. Out: the solution, if one was accepted.</param>
    /// <param name="project">Applies <c>T</c>.</param>
    internal static bool Solve(
        float[] observedRe, float[] observedIm, float[] maskWeight,
        int frames, int bins, float[] re, float[] im,
        Projector project, SparseInpaintOptions options,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options = options.OrDefault();

        int n = frames * bins;
        if (n <= 0 || re.Length < n || im.Length < n) return false;

        // The weight the data term carries at each cell, and what it pulls towards. Outside the
        // selection that is the observation at full strength. Inside it is the starting estimate at
        // whatever weight the caller allows — zero leaves the reconstruction to consistency and
        // sparsity alone, which is right for a small hole and lets a long one sag in the middle,
        // because nothing in the interior of a long hole is within reach of the audio either side.
        var weight = new float[n];
        var targetRe = new float[n];
        var targetIm = new float[n];
        var emptyBias = (float)Math.Clamp(options.EmptyBias, 0, 1);

        double reliableCells = 0;
        for (int i = 0; i < n; i++)
        {
            float mask = Math.Clamp(maskWeight[i], 0f, 1f);
            float reliable = 1 - mask;
            weight[i] = reliable + emptyBias * mask;
            targetRe[i] = observedRe[i] * reliable + re[i] * mask;
            targetIm[i] = observedIm[i] * reliable + im[i] * mask;
            reliableCells += reliable;
        }

        // With nothing reliable anywhere there is no evidence to reconstruct from, and the honest
        // answer is to leave the starting estimate alone rather than to shrink it to silence.
        if (reliableCells < 1) return false;

        var energy = new float[n];
        var scratch = new float[n];

        double lambdaCeiling = LambdaCeiling(observedRe, observedIm, weight, frames, bins, options,
            energy, scratch);
        if (!(lambdaCeiling > 0)) return false;
        double lambdaFloor = lambdaCeiling * Math.Clamp(options.LambdaFloorRatio, 1e-9, 1);

        double lipschitz = EstimateLipschitz(project, weight, frames, bins, cancellationToken);

        // FISTA's three grids: where the last step landed, where this one lands, and the
        // extrapolation the gradient is taken at.
        var previousRe = (float[])re.Clone();
        var previousIm = (float[])im.Clone();
        var currentRe = new float[n];
        var currentIm = new float[n];
        var pointRe = (float[])re.Clone();
        var pointIm = (float[])im.Clone();
        var stepRe = new float[n];
        var stepIm = new float[n];

        int iterations = Math.Clamp(options.Iterations, 1, 4096);
        double momentum = 1;

        for (int k = 0; k < iterations; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ∇ = T( W ⊙ (T z − d) ). Two projections, which is what an iteration costs.
            pointRe.AsSpan(0, n).CopyTo(stepRe);
            pointIm.AsSpan(0, n).CopyTo(stepIm);
            project(stepRe, stepIm);
            for (int i = 0; i < n; i++)
            {
                float w = weight[i];
                stepRe[i] = w * (stepRe[i] - targetRe[i]);
                stepIm[i] = w * (stepIm[i] - targetIm[i]);
            }
            project(stepRe, stepIm);

            var inverseLipschitz = (float)(1.0 / lipschitz);
            for (int i = 0; i < n; i++)
            {
                currentRe[i] = pointRe[i] - stepRe[i] * inverseLipschitz;
                currentIm[i] = pointIm[i] - stepIm[i] * inverseLipschitz;
            }

            double lambda = iterations == 1
                ? lambdaFloor
                : lambdaCeiling * Math.Pow(lambdaFloor / lambdaCeiling, k / (double)(iterations - 1));
            Shrink(currentRe, currentIm, lambda / lipschitz, frames, bins, options, energy, scratch,
                options.PenalizeSelectionOnly ? maskWeight : null);

            if (options.HardDataConstraint)
            {
                for (int i = 0; i < n; i++)
                {
                    float mask = Math.Clamp(maskWeight[i], 0f, 1f);
                    currentRe[i] = observedRe[i] * (1 - mask) + currentRe[i] * mask;
                    currentIm[i] = observedIm[i] * (1 - mask) + currentIm[i] * mask;
                }
            }

            // Nesterov's extrapolation: the next gradient is taken not at the new point but a little
            // past it, along the direction the last two points travelled.
            double nextMomentum = (1 + Math.Sqrt(1 + 4 * momentum * momentum)) / 2;
            var beta = (float)((momentum - 1) / nextMomentum);
            for (int i = 0; i < n; i++)
            {
                pointRe[i] = currentRe[i] + beta * (currentRe[i] - previousRe[i]);
                pointIm[i] = currentIm[i] + beta * (currentIm[i] - previousIm[i]);
            }
            momentum = nextMomentum;

            (previousRe, currentRe) = (currentRe, previousRe);
            (previousIm, currentIm) = (currentIm, previousIm);

            progress?.Report((k + 1) / (double)iterations);
        }

        if (!IsBelievable(previousRe, previousIm, observedRe, observedIm, maskWeight, n)) return false;

        previousRe.AsSpan(0, n).CopyTo(re);
        previousIm.AsSpan(0, n).CopyTo(im);
        return true;
    }

    // ── the shrinkage operators ──────────────────────────────────

    /// <summary>Applies the chosen shrinkage in place at threshold <paramref name="threshold"/>.</summary>
    private static void Shrink(float[] re, float[] im, double threshold, int frames, int bins,
        SparseInpaintOptions options, float[] energy, float[] scratch, float[]? maskWeight)
    {
        int n = frames * bins;
        if (threshold <= 0) return;

        // The neighbourhood is read from the whole grid whichever cells are shrunk. What a cell
        // inside the selection is worth is decided largely by its neighbours outside it, and hiding
        // those would leave the reconstruction judging itself.
        if (options.Shrinkage != SocialShrinkage.Lasso)
        {
            NeighbourhoodMeanEnergy(re, im, frames, bins,
                Math.Max(0, options.TimeRadius), Math.Max(0, options.FrequencyRadius), energy, scratch);
        }

        double thresholdSquared = threshold * threshold;
        for (int i = 0; i < n; i++)
        {
            float factor;
            switch (options.Shrinkage)
            {
                case SocialShrinkage.Lasso:
                {
                    double magnitude = Math.Sqrt((double)re[i] * re[i] + (double)im[i] * im[i]);
                    factor = (float)(magnitude > 0 ? Math.Max(0, 1 - threshold / magnitude) : 0);
                    break;
                }
                case SocialShrinkage.WindowedGroupLasso:
                {
                    double rootMeanSquare = Math.Sqrt(energy[i]);
                    factor = (float)(rootMeanSquare > 0 ? Math.Max(0, 1 - threshold / rootMeanSquare) : 0);
                    break;
                }
                default:
                {
                    // Persistent empirical Wiener. The squared form is what makes it Wiener-like:
                    // well above the threshold it barely attenuates at all, where the linear law is
                    // still taking a fixed amount off everything it keeps.
                    double neighbourhood = energy[i];
                    factor = (float)(neighbourhood > 0
                        ? Math.Max(0, 1 - thresholdSquared / neighbourhood)
                        : 0);
                    break;
                }
            }

            // Faded in by the mask, so that a cell the user did not select is not shrunk at all and
            // a half-covered one is half shrunk — the same feather the rest of the edit respects.
            if (maskWeight is not null)
            {
                float mask = Math.Clamp(maskWeight[i], 0f, 1f);
                factor = 1 + mask * (factor - 1);
            }

            re[i] *= factor;
            im[i] *= factor;
        }
    }

    /// <summary>
    /// The mean squared magnitude over each cell's neighbourhood, so that its square root is a level
    /// directly comparable with a single coefficient's magnitude — which is what lets one threshold
    /// serve both the plain and the structured operators.
    /// </summary>
    /// <remarks>
    /// Separable, and each pass is a running sum, so the cost is two additions per cell whatever the
    /// neighbourhood's size. The frequency pass runs first because a row is contiguous; the time pass
    /// strides, and doing it second means it strides over a smaller working set already in cache.
    /// Both are edge-aware — dividing by the taps that exist rather than the taps that were asked
    /// for, so the first and last frames are not quietly told their neighbourhood is half empty.
    /// </remarks>
    internal static void NeighbourhoodMeanEnergy(float[] re, float[] im, int frames, int bins,
        int timeRadius, int frequencyRadius, float[] energy, float[] scratch)
    {
        int n = frames * bins;
        for (int i = 0; i < n; i++)
            scratch[i] = (float)((double)re[i] * re[i] + (double)im[i] * im[i]);

        if (frequencyRadius > 0)
        {
            for (int f = 0; f < frames; f++)
            {
                int row = f * bins;
                double sum = 0;
                int low = 0, high = -1;
                for (int b = 0; b < bins; b++)
                {
                    int wantLow = Math.Max(0, b - frequencyRadius);
                    int wantHigh = Math.Min(bins - 1, b + frequencyRadius);
                    while (high < wantHigh) sum += scratch[row + ++high];
                    while (low < wantLow) sum -= scratch[row + low++];
                    energy[row + b] = (float)(sum / (wantHigh - wantLow + 1));
                }
            }
        }
        else
        {
            scratch.AsSpan(0, n).CopyTo(energy);
        }

        if (timeRadius <= 0) return;

        energy.AsSpan(0, n).CopyTo(scratch);
        for (int b = 0; b < bins; b++)
        {
            double sum = 0;
            int low = 0, high = -1;
            for (int f = 0; f < frames; f++)
            {
                int wantLow = Math.Max(0, f - timeRadius);
                int wantHigh = Math.Min(frames - 1, f + timeRadius);
                while (high < wantHigh) sum += scratch[++high * bins + b];
                while (low < wantLow) sum -= scratch[low++ * bins + b];
                energy[f * bins + b] = (float)(sum / (wantHigh - wantLow + 1));
            }
        }
    }

    // ── the two constants the solver will not guess at ───────────

    /// <summary>
    /// Where the threshold schedule starts: a high quantile of the observed neighbourhood level,
    /// measured only where the observation is worth measuring.
    /// </summary>
    /// <remarks>
    /// A quantile rather than the maximum, because one exceptional cell — a click, the very peak of a
    /// transient — would otherwise set a threshold the rest of the block never comes near, and the
    /// first two thirds of the schedule would be spent descending through nothing. Reading it off the
    /// material rather than fixing it is what makes the solver indifferent to how loud the file is.
    /// </remarks>
    private static double LambdaCeiling(float[] observedRe, float[] observedIm, float[] weight,
        int frames, int bins, SparseInpaintOptions options, float[] energy, float[] scratch)
    {
        NeighbourhoodMeanEnergy(observedRe, observedIm, frames, bins,
            Math.Max(0, options.TimeRadius), Math.Max(0, options.FrequencyRadius), energy, scratch);

        int n = frames * bins;
        var levels = new List<double>(n);
        for (int i = 0; i < n; i++)
            if (weight[i] > 0.5f && energy[i] > 0)
                levels.Add(Math.Sqrt(energy[i]));

        if (levels.Count == 0) return 0;
        levels.Sort();

        double quantile = Math.Clamp(options.LambdaQuantile, 0, 1);
        int index = Math.Clamp((int)Math.Round(quantile * (levels.Count - 1)), 0, levels.Count - 1);
        return levels[index];
    }

    /// <summary>
    /// The gradient step size, measured rather than assumed: the operator norm of
    /// <c>α ↦ T(W ⊙ Tα)</c> by power iteration.
    /// </summary>
    /// <remarks>
    /// In the interior of the block <c>T</c> is an orthogonal projection and this is exactly one. It
    /// is measured anyway because the first and last frames of a block are not covered by a full set
    /// of overlapping windows, so the frame is not tight there and the norm can sit a little above
    /// one — and a step size taken from a Lipschitz constant that is too small is the one way an
    /// otherwise correct FISTA diverges.
    /// </remarks>
    private static double EstimateLipschitz(Projector project, float[] weight, int frames, int bins,
        CancellationToken cancellationToken)
    {
        int n = frames * bins;
        var re = new float[n];
        var im = new float[n];

        // A fixed seed, so a run of the solver is reproducible and a test that measures its output
        // measures the same thing twice.
        var random = new Random(0x5EED);
        for (int i = 0; i < n; i++)
        {
            re[i] = (float)(random.NextDouble() * 2 - 1);
            im[i] = (float)(random.NextDouble() * 2 - 1);
        }

        double norm = Normalize(re, im, n);
        if (norm <= 0) return 1;

        double estimate = 1;
        for (int k = 0; k < LipschitzIterations; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            project(re, im);
            for (int i = 0; i < n; i++)
            {
                float w = weight[i];
                re[i] *= w;
                im[i] *= w;
            }
            project(re, im);

            double length = Normalize(re, im, n);
            if (length <= 0) return 1;
            estimate = length;
        }

        // Never below one: T is a projection composed with a weighting that is at most one, so an
        // estimate under one means the random start happened to miss the operator's leading
        // direction, not that the operator is small.
        return Math.Max(1, estimate * LipschitzSafety);
    }

    /// <summary>Scales a coefficient grid to unit length, returning the length it had.</summary>
    private static double Normalize(float[] re, float[] im, int n)
    {
        double sum = 0;
        for (int i = 0; i < n; i++) sum += (double)re[i] * re[i] + (double)im[i] * im[i];

        double length = Math.Sqrt(sum);
        if (length <= 0 || !double.IsFinite(length)) return 0;

        var scale = (float)(1 / length);
        for (int i = 0; i < n; i++) { re[i] *= scale; im[i] *= scale; }
        return length;
    }

    // ── the acceptance test ──────────────────────────────────────

    /// <summary>
    /// Whether the solution is worth returning, or whether the caller should keep what it had.
    /// </summary>
    /// <remarks>
    /// Not a judgement of quality — a solver whose result is merely mediocre should still be
    /// returned, because the alternative estimate is not obviously better. This catches the two ways
    /// the run can be worthless: a value that is not a number, and a reconstruction whose level
    /// inside the selection has run away from everything around it, which is what divergence looks
    /// like from the outside.
    /// </remarks>
    private static bool IsBelievable(float[] re, float[] im, float[] observedRe, float[] observedIm,
        float[] maskWeight, int n)
    {
        double inside = 0, insideCells = 0, outside = 0, outsideCells = 0;

        for (int i = 0; i < n; i++)
        {
            if (!float.IsFinite(re[i]) || !float.IsFinite(im[i])) return false;

            float mask = Math.Clamp(maskWeight[i], 0f, 1f);
            if (mask > 0.5f)
            {
                inside += (double)re[i] * re[i] + (double)im[i] * im[i];
                insideCells++;
            }
            else
            {
                outside += (double)observedRe[i] * observedRe[i] + (double)observedIm[i] * observedIm[i];
                outsideCells++;
            }
        }

        if (insideCells == 0 || outsideCells == 0) return true;

        double insideLevel = Math.Sqrt(inside / insideCells);
        double outsideLevel = Math.Sqrt(outside / outsideCells);
        return outsideLevel <= 0 || insideLevel <= outsideLevel * DivergenceRatio;
    }
}
