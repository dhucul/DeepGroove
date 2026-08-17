using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The partial singular value decomposition everything above it rests on.
/// </summary>
/// <remarks>
/// Tested against matrices whose answer is known by construction rather than against another
/// implementation, because there is no other implementation here to disagree with. A decomposition
/// that is quietly wrong does not fail; it returns a plausible basis and every result computed from
/// it is off by an amount nobody can attribute.
/// </remarks>
public sealed class RandomizedSvdTests(ITestOutputHelper output)
{
    /// <summary>
    /// A rows×cols matrix whose singular values are exactly <paramref name="scales"/>.
    /// </summary>
    /// <remarks>
    /// The factors are orthonormalised, and that is the whole point. Built from merely random
    /// vectors, the sum of scaled outer products has singular values that are <em>not</em> the
    /// scales — near them, but not them — so a test comparing the two measures how non-orthogonal
    /// the generator happened to be. Written that way first, it reported the decomposition returning
    /// 9.95 for a "10" that was never there, and a truncation error better than the theoretical
    /// optimum, which should have been the giveaway.
    /// </remarks>
    private static double[] LowRank(int rows, int cols, int rank, int seed, double[] scales)
    {
        var random = new Random(seed);
        var left = new List<double[]>();
        var right = new List<double[]>();

        for (int t = 0; t < rank; t++)
        {
            left.Add(OrthonormalTo(left, rows, random));
            right.Add(OrthonormalTo(right, cols, random));
        }

        var matrix = new double[rows * cols];
        for (int t = 0; t < rank; t++)
        {
            double scale = scales[t];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    matrix[r * cols + c] += scale * left[t][r] * right[t][c];
        }
        return matrix;

        static double[] OrthonormalTo(List<double[]> existing, int n, Random random)
        {
            var vector = new double[n];
            for (int i = 0; i < n; i++) vector[i] = random.NextDouble() * 2 - 1;

            // Twice, for the same reason the production code does it twice.
            for (int pass = 0; pass < 2; pass++)
                foreach (double[] previous in existing)
                {
                    double dot = 0;
                    for (int i = 0; i < n; i++) dot += vector[i] * previous[i];
                    for (int i = 0; i < n; i++) vector[i] -= dot * previous[i];
                }

            double length = Math.Sqrt(vector.Sum(v => v * v));
            for (int i = 0; i < n; i++) vector[i] /= length;
            return vector;
        }
    }

    private static double[] Reconstruct(double[] u, double[] s, double[] v, int rows, int cols, int k)
    {
        var result = new double[rows * cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double sum = 0;
                for (int t = 0; t < k; t++) sum += u[r * k + t] * s[t] * v[c * k + t];
                result[r * cols + c] = sum;
            }
        return result;
    }

    private static double RelativeError(double[] a, double[] b)
    {
        double error = 0, magnitude = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            error += d * d;
            magnitude += a[i] * a[i];
        }
        return magnitude > 0 ? Math.Sqrt(error / magnitude) : 0;
    }

    // ── it recovers what it was given ────────────────────────────

    /// <summary>
    /// A matrix built to have exactly three non-zero singular values must come back with those three
    /// values, to several digits. This is the test that would catch a transposed index, a wrong
    /// normalisation, or eigenvalues taken in the wrong order.
    /// </summary>
    [Theory]
    [InlineData(40, 90)]
    [InlineData(90, 40)]
    [InlineData(64, 64)]
    public void ItRecoversTheSingularValuesItWasBuiltFrom(int rows, int cols)
    {
        double[] scales = [10.0, 4.0, 1.5];
        double[] matrix = LowRank(rows, cols, 3, seed: 5, scales);

        (double[] u, double[] s, double[] v, int k) = RandomizedSvd.Compute(matrix, rows, cols, rank: 5);

        output.WriteLine($"{rows}x{cols}: " + string.Join(", ", s.Select(x => x.ToString("0.0000"))));

        Assert.Equal(5, k);
        for (int t = 0; t < 3; t++) Assert.Equal(scales[t], s[t], 1e-6);

        // Beyond the true rank there is nothing left to find.
        for (int t = 3; t < k; t++) Assert.True(s[t] < 1e-8, $"singular value {t} is {s[t]}, not zero");

        // And the factors multiply back to the matrix.
        Assert.True(RelativeError(matrix, Reconstruct(u, s, v, rows, cols, k)) < 1e-9);
    }

    /// <summary>Singular values come back in descending order, which callers threshold from the top.</summary>
    [Fact]
    public void TheSingularValuesAreOrdered()
    {
        double[] matrix = LowRank(50, 70, 6, seed: 11, [1, 9, 3, 7, 2, 5]);
        (_, double[] s, _, int k) = RandomizedSvd.Compute(matrix, 50, 70, rank: 6);

        output.WriteLine(string.Join(", ", s.Select(x => x.ToString("0.000"))));
        for (int t = 1; t < k; t++)
            Assert.True(s[t] <= s[t - 1] + 1e-12, $"value {t} ({s[t]}) is above value {t - 1} ({s[t - 1]})");
    }

    /// <summary>
    /// The left and right factors have to be orthonormal, which is what makes the decomposition a
    /// decomposition rather than merely a product that happens to fit.
    /// </summary>
    [Fact]
    public void TheFactorsAreOrthonormal()
    {
        const int rows = 60, cols = 80, k = 6;
        double[] matrix = LowRank(rows, cols, 6, seed: 3, [8, 6, 5, 3, 2, 1]);
        (double[] u, _, double[] v, _) = RandomizedSvd.Compute(matrix, rows, cols, k);

        double worst = 0;
        for (int i = 0; i < k; i++)
            for (int j = 0; j < k; j++)
            {
                double uDot = 0, vDot = 0;
                for (int r = 0; r < rows; r++) uDot += u[r * k + i] * u[r * k + j];
                for (int c = 0; c < cols; c++) vDot += v[c * k + i] * v[c * k + j];

                double want = i == j ? 1 : 0;
                worst = Math.Max(worst, Math.Max(Math.Abs(uDot - want), Math.Abs(vDot - want)));
            }

        output.WriteLine($"worst departure from orthonormal: {worst:0.000000000}");
        Assert.True(worst < 1e-8, $"the factors are out by {worst}");
    }

    /// <summary>
    /// On a matrix whose spectrum decays slowly — which is what a spectrogram is — the truncation has
    /// to be close to the best possible one. The best rank-k error is the tail of the true spectrum,
    /// so that is what it is measured against.
    /// </summary>
    [Fact]
    public void OnASlowlyDecayingSpectrumItIsNearlyOptimal()
    {
        const int rows = 70, cols = 120, rank = 30;
        double[] scales = [.. Enumerable.Range(0, rank).Select(i => Math.Pow(0.85, i))];
        double[] matrix = LowRank(rows, cols, rank, seed: 17, scales);

        const int k = 10;
        (double[] u, double[] s, double[] v, _) = RandomizedSvd.Compute(matrix, rows, cols, k);
        double achieved = RelativeError(matrix, Reconstruct(u, s, v, rows, cols, k));

        // The unavoidable error: everything past the tenth singular value.
        double tail = Math.Sqrt(scales.Skip(k).Sum(x => x * x));
        double total = Math.Sqrt(scales.Sum(x => x * x));
        double best = tail / total;

        output.WriteLine($"rank {k}: achieved {achieved:0.00000}, best possible {best:0.00000}, "
                         + $"ratio {achieved / best:0.000}");
        Assert.True(achieved < best * 1.35, $"truncation is {achieved / best:0.00}x worse than optimal");
    }

    /// <summary>The spectral norm is the first singular value, and the ALM loop scales its step by it.</summary>
    [Fact]
    public void TheSpectralNormIsTheLargestSingularValue()
    {
        double[] matrix = LowRank(55, 75, 4, seed: 23, [12.5, 6, 2, 0.5]);
        double norm = RandomizedSvd.SpectralNorm(matrix, 55, 75);

        output.WriteLine($"spectral norm {norm:0.0000}, expected 12.5");
        Assert.Equal(12.5, norm, 1e-4);
    }

    // ── degenerate shapes ────────────────────────────────────────

    [Fact]
    public void AZeroMatrixDecomposesToNothingRatherThanThrowing()
    {
        (double[] u, double[] s, double[] v, int k) = RandomizedSvd.Compute(new double[30 * 40], 30, 40, 4);

        Assert.Equal(4, k);
        Assert.All(s, value => Assert.Equal(0, value, 1e-12));
        Assert.All(u, value => Assert.True(double.IsFinite(value)));
        Assert.All(v, value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void AskingForMoreRankThanTheMatrixHasGivesWhatItHas()
    {
        (_, double[] s, _, int k) = RandomizedSvd.Compute(LowRank(8, 20, 3, 7, [3, 2, 1]), 8, 20, rank: 50);

        Assert.Equal(8, k);
        Assert.Equal(8, s.Length);
    }

    [Fact]
    public void AMisshapenMatrixIsRejected()
    {
        Assert.Throws<ArgumentException>(() => RandomizedSvd.Compute(new double[10], 5, 5, 2));
        Assert.Throws<ArgumentException>(() => RandomizedSvd.Compute(new double[10], 0, 5, 2));
    }

    /// <summary>
    /// The same matrix must decompose the same way twice. The method is randomized and the seed is
    /// fixed, so a result that moves between runs would make every measurement above meaningless.
    /// </summary>
    [Fact]
    public void ItIsReproducible()
    {
        double[] matrix = LowRank(45, 65, 5, seed: 31, [7, 5, 3, 2, 1]);

        (_, double[] first, _, _) = RandomizedSvd.Compute(matrix, 45, 65, 5);
        (_, double[] second, _, _) = RandomizedSvd.Compute(matrix, 45, 65, 5);

        Assert.Equal(first, second);
    }
}
