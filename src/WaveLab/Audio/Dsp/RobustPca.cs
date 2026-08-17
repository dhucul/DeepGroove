namespace WaveLab.Audio.Dsp;

/// <summary>Settings for <see cref="RobustPca"/>.</summary>
/// <param name="MaximumIterations">Upper bound on the ALM loop. It usually converges well inside this.</param>
/// <param name="Tolerance">Relative residual at which the split is called done.</param>
/// <param name="SparsityWeight">
/// How much the sparse layer costs. Zero takes the theoretical value 1/√max(m,n), which is what
/// makes the decomposition parameter-free; raising it keeps less in the sparse layer.
/// </param>
/// <param name="MaximumRank">
/// Ceiling on the low-rank layer. Bounds the cost, since every iteration decomposes to this rank at
/// worst, and bounds what "low rank" is allowed to mean.
/// </param>
public readonly record struct RobustPcaOptions(
    int MaximumIterations = 60,
    double Tolerance = 1e-6,
    double SparsityWeight = 0,
    int MaximumRank = 40)
{
    /// <remarks>Spelled out rather than <c>new()</c>, which zero-initialises a record struct.</remarks>
    public static RobustPcaOptions Default { get; } = new(
        MaximumIterations: 60,
        Tolerance: 1e-6,
        SparsityWeight: 0,
        MaximumRank: 40);

    internal RobustPcaOptions OrDefault() => MaximumIterations <= 0 ? Default : this;
}

/// <summary>What a decomposition produced.</summary>
/// <param name="LowRank">The low-rank layer, row-major, same shape as the input.</param>
/// <param name="Sparse">The sparse layer, row-major, same shape as the input.</param>
/// <param name="Iterations">How many ALM steps it took.</param>
/// <param name="Rank">The rank the low-rank layer settled at.</param>
/// <param name="Residual">Relative size of what neither layer explained.</param>
public sealed record RobustPcaResult(
    double[] LowRank, double[] Sparse, int Iterations, int Rank, double Residual)
{
    /// <summary>Whether the two layers actually add back up to what went in.</summary>
    public bool Converged => Residual < 1e-4;
}

/// <summary>
/// Splits a matrix into a low-rank layer and a sparse one — principal component pursuit.
/// </summary>
/// <remarks>
/// <para>
/// Ordinary principal component analysis is destroyed by outliers: a single gross error drags the
/// principal directions towards itself, because the squared error it minimises rewards that. Candès,
/// Li, Ma and Wright's result is that if a matrix is exactly the sum of something low rank and
/// something sparse, the two can be separated <b>exactly</b>, by minimising the nuclear norm of one
/// plus the ℓ1 norm of the other — a convex problem, with no parameter to choose beyond a weight
/// whose theoretical value is <c>1/√max(m,n)</c>.
/// </para>
/// <para>
/// <b>On a spectrogram the two layers mean something.</b> Sustained material — a held note, a bowed
/// string, the noise floor of a room — repeats across frames, so it lands in a handful of singular
/// directions. Anything brief and broadband cannot be expressed that way at all and falls into the
/// sparse layer. That is the appeal for restoration: crackle, clicks and ticks are exactly what a
/// low-rank model cannot represent.
/// </para>
/// <para>
/// It is solved by <b>inexact augmented Lagrangian</b> iteration, which alternates a singular value
/// threshold against a soft threshold and tightens a penalty between them. Each step needs the top
/// singular values of one matrix, which comes from <see cref="RandomizedSvd"/> — and the rank asked
/// for <b>grows only when the previous step used everything it was given</b>, so a matrix that is
/// genuinely low rank never pays for a decomposition it did not need.
/// </para>
/// <para>
/// <b>What it will not do is tell a defect from a drum.</b> A struck note is brief and broadband too,
/// and lands in the sparse layer beside the crackle. The decomposition separates <em>sustained</em>
/// from <em>transient</em>, which is not the same question as clean from damaged, and anything built
/// on it has to supply that distinction itself.
/// </para>
/// </remarks>
public static class RobustPca
{
    /// <summary>Penalty growth per iteration. 1.5 is the value the original paper settles on.</summary>
    private const double PenaltyGrowth = 1.5;

    /// <summary>
    /// Splits <paramref name="matrix"/> into a low-rank and a sparse layer.
    /// </summary>
    public static RobustPcaResult Decompose(double[] matrix, int rows, int cols,
        RobustPcaOptions options = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (rows <= 0 || cols <= 0 || matrix.Length < (long)rows * cols)
            throw new ArgumentException("The matrix is smaller than its stated shape.", nameof(matrix));

        options = options.OrDefault();
        int n = rows * cols;

        double lambda = options.SparsityWeight > 0
            ? options.SparsityWeight
            : 1.0 / Math.Sqrt(Math.Max(rows, cols));

        double frobenius = 0, largest = 0;
        for (int i = 0; i < n; i++)
        {
            frobenius += matrix[i] * matrix[i];
            largest = Math.Max(largest, Math.Abs(matrix[i]));
        }
        frobenius = Math.Sqrt(frobenius);

        var low = new double[n];
        var sparse = new double[n];
        if (frobenius <= 0) return new RobustPcaResult(low, sparse, 0, 0, 0);

        // The dual variable starts scaled so that its own norm is one in the dual pair the problem
        // is stated in — nuclear against spectral, ℓ1 against ℓ∞. Starting it anywhere else costs
        // iterations recovering from the choice.
        double spectral = RandomizedSvd.SpectralNorm(matrix, rows, cols);
        if (!(spectral > 0)) return new RobustPcaResult(low, sparse, 0, 0, 0);

        double dual = Math.Max(spectral, largest / lambda);
        var y = new double[n];
        for (int i = 0; i < n; i++) y[i] = matrix[i] / dual;

        double penalty = 1.25 / spectral;
        double penaltyCeiling = penalty * 1e7;

        var work = new double[n];
        int requestedRank = Math.Clamp(1, 1, options.MaximumRank);
        int rank = 0, iteration = 0;
        double residual = 1;

        int maximumRank = Math.Clamp(options.MaximumRank, 1, Math.Min(rows, cols));

        for (iteration = 1; iteration <= options.MaximumIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((iteration - 1) / (double)options.MaximumIterations);

            // ── the low-rank layer: threshold the singular values ──
            double inversePenalty = 1 / penalty;
            for (int i = 0; i < n; i++) work[i] = matrix[i] - sparse[i] + y[i] * inversePenalty;

            int ask = Math.Clamp(requestedRank, 1, maximumRank);
            (double[] u, double[] s, double[] v, int k) =
                RandomizedSvd.Compute(work, rows, cols, ask);

            int kept = 0;
            for (int t = 0; t < k; t++)
            {
                s[t] -= inversePenalty;
                if (s[t] > 0) kept++;
                else s[t] = 0;
            }
            rank = kept;

            // Everything offered was used, so the truncation — not the threshold — decided the rank.
            // Ask for more next time; otherwise settle back to just past what survived.
            requestedRank = kept >= k
                ? Math.Min(maximumRank, k + Math.Max(1, k / 2))
                : Math.Min(maximumRank, kept + 1);

            Reconstruct(u, s, v, rows, cols, k, low);

            // ── the sparse layer: threshold the entries ──
            double cut = lambda * inversePenalty;
            for (int i = 0; i < n; i++)
            {
                double value = matrix[i] - low[i] + y[i] * inversePenalty;
                double magnitude = Math.Abs(value) - cut;
                sparse[i] = magnitude > 0 ? Math.Sign(value) * magnitude : 0;
            }

            // ── what neither layer explained ──
            double leftover = 0;
            for (int i = 0; i < n; i++)
            {
                double z = matrix[i] - low[i] - sparse[i];
                y[i] += penalty * z;
                leftover += z * z;
            }

            residual = Math.Sqrt(leftover) / frobenius;
            if (residual < options.Tolerance) break;

            penalty = Math.Min(penalty * PenaltyGrowth, penaltyCeiling);
        }

        progress?.Report(1);
        return new RobustPcaResult(low, sparse, Math.Min(iteration, options.MaximumIterations),
            rank, residual);
    }

    /// <summary>L = U·diag(S)·Vᵀ, written straight into the destination.</summary>
    private static void Reconstruct(double[] u, double[] s, double[] v, int rows, int cols, int k,
        double[] destination)
    {
        Array.Clear(destination, 0, rows * cols);
        Parallel.For(0, rows, r =>
        {
            int outRow = r * cols, uRow = r * k;
            for (int t = 0; t < k; t++)
            {
                double weight = s[t] * u[uRow + t];
                if (weight == 0) continue;
                for (int c = 0; c < cols; c++) destination[outRow + c] += weight * v[c * k + t];
            }
        });
    }
}
