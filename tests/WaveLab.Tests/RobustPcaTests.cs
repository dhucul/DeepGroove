using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Principal component pursuit: the claim that a matrix which <em>is</em> low rank plus sparse can be
/// split back into exactly those two things.
/// </summary>
/// <remarks>
/// That claim is testable in the strongest possible way — build the two layers, add them, and demand
/// both back — which is what nearly everything here does. A decomposition that merely produces two
/// matrices summing to the input has done nothing: the trivial split, everything in one layer and
/// nothing in the other, satisfies that and is worthless.
/// </remarks>
public sealed class RobustPcaTests(ITestOutputHelper output)
{
    private static double[] LowRankPart(int rows, int cols, int rank, int seed)
    {
        var random = new Random(seed);
        var left = new List<double[]>();
        var right = new List<double[]>();
        for (int t = 0; t < rank; t++)
        {
            left.Add(Orthonormal(left, rows, random));
            right.Add(Orthonormal(right, cols, random));
        }

        var matrix = new double[rows * cols];
        for (int t = 0; t < rank; t++)
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    matrix[r * cols + c] += left[t][r] * right[t][c];
        return matrix;

        static double[] Orthonormal(List<double[]> existing, int n, Random random)
        {
            var vector = new double[n];
            for (int i = 0; i < n; i++) vector[i] = random.NextDouble() * 2 - 1;
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

    /// <summary>A few large entries scattered at random, and nothing else.</summary>
    private static double[] SparsePart(int rows, int cols, int count, double size, int seed)
    {
        var random = new Random(seed);
        var matrix = new double[rows * cols];
        for (int i = 0; i < count; i++)
        {
            int at = random.Next(rows * cols);
            matrix[at] = size * (random.NextDouble() < 0.5 ? -1 : 1);
        }
        return matrix;
    }

    private static double Relative(double[] want, double[] got)
    {
        double error = 0, magnitude = 0;
        for (int i = 0; i < want.Length; i++)
        {
            double d = want[i] - got[i];
            error += d * d;
            magnitude += want[i] * want[i];
        }
        return magnitude > 0 ? Math.Sqrt(error / magnitude) : Math.Sqrt(error);
    }

    // ── the headline claim ───────────────────────────────────────

    /// <summary>
    /// Built as low rank plus sparse, it must come back as low rank plus sparse — both layers, not
    /// just a pair that adds up.
    /// </summary>
    [Theory]
    [InlineData(60, 80, 3, 120)]
    [InlineData(80, 60, 5, 200)]
    [InlineData(50, 200, 4, 300)]
    public void ItSeparatesWhatItWasBuiltFrom(int rows, int cols, int rank, int spikes)
    {
        double[] low = LowRankPart(rows, cols, rank, seed: 3);
        double[] sparse = SparsePart(rows, cols, spikes, size: 1.5, seed: 9);

        var mixed = new double[rows * cols];
        for (int i = 0; i < mixed.Length; i++) mixed[i] = low[i] + sparse[i];

        RobustPcaResult result = RobustPca.Decompose(mixed, rows, cols,
            RobustPcaOptions.Default with { MaximumIterations = 200, Tolerance = 1e-7 });

        double lowError = Relative(low, result.LowRank);
        double sparseError = Relative(sparse, result.Sparse);

        output.WriteLine($"{rows}x{cols} rank {rank}, {spikes} spikes: "
                         + $"{result.Iterations} iterations, rank {result.Rank}, "
                         + $"residual {result.Residual:0.0000000}, "
                         + $"low off by {lowError:0.00000}, sparse off by {sparseError:0.00000}");

        Assert.True(result.Converged, $"the layers do not add back up: residual {result.Residual}");
        Assert.Equal(rank, result.Rank);
        Assert.True(lowError < 0.02, $"the low-rank layer is off by {lowError:0.000}");
        Assert.True(sparseError < 0.02, $"the sparse layer is off by {sparseError:0.000}");
    }

    /// <summary>
    /// The two layers must always sum back to the input, whatever else is true of them. This is the
    /// weak claim, and it is here to be sure the strong one above is not passing by accident.
    /// </summary>
    [Fact]
    public void TheLayersAlwaysAddBackUp()
    {
        const int rows = 40, cols = 55;
        var random = new Random(21);
        var matrix = new double[rows * cols];
        for (int i = 0; i < matrix.Length; i++) matrix[i] = random.NextDouble() * 2 - 1;

        RobustPcaResult result = RobustPca.Decompose(matrix, rows, cols,
            RobustPcaOptions.Default with { MaximumIterations = 120 });

        double worst = 0;
        for (int i = 0; i < matrix.Length; i++)
            worst = Math.Max(worst, Math.Abs(matrix[i] - result.LowRank[i] - result.Sparse[i]));

        output.WriteLine($"noise: rank {result.Rank}, residual {result.Residual:0.0000000}, "
                         + $"worst entry off by {worst:0.0000000}");
        Assert.True(worst < 1e-3, $"an entry is off by {worst}");
    }

    /// <summary>
    /// Something with no sparse component must not have one invented for it. The trivial failure of
    /// a split like this is putting a little of everything in both layers.
    /// </summary>
    [Fact]
    public void SomethingPurelyLowRankGetsAnEmptySparseLayer()
    {
        const int rows = 50, cols = 70;
        double[] low = LowRankPart(rows, cols, 3, seed: 13);

        RobustPcaResult result = RobustPca.Decompose(low, rows, cols,
            RobustPcaOptions.Default with { MaximumIterations = 200, Tolerance = 1e-8 });

        double sparseEnergy = Math.Sqrt(result.Sparse.Sum(v => v * v));
        double totalEnergy = Math.Sqrt(low.Sum(v => v * v));

        output.WriteLine($"rank {result.Rank}, sparse layer holds "
                         + $"{sparseEnergy / totalEnergy:0.00000} of the energy");
        Assert.Equal(3, result.Rank);
        Assert.True(sparseEnergy / totalEnergy < 0.02,
            $"a sparse layer was invented holding {sparseEnergy / totalEnergy:0.000} of the energy");
    }

    /// <summary>
    /// The rank is discovered rather than dictated: it grows only when the previous step used
    /// everything it was given, so a matrix that is genuinely rank three never pays for rank forty.
    /// </summary>
    [Fact]
    public void TheRankIsFoundRatherThanAssumed()
    {
        const int rows = 60, cols = 90;
        foreach (int trueRank in new[] { 1, 2, 6 })
        {
            double[] low = LowRankPart(rows, cols, trueRank, seed: 100 + trueRank);
            double[] sparse = SparsePart(rows, cols, 150, 1.2, seed: 200 + trueRank);
            var mixed = new double[rows * cols];
            for (int i = 0; i < mixed.Length; i++) mixed[i] = low[i] + sparse[i];

            RobustPcaResult result = RobustPca.Decompose(mixed, rows, cols,
                RobustPcaOptions.Default with { MaximumIterations = 200, Tolerance = 1e-7 });

            output.WriteLine($"true rank {trueRank,2} -> found {result.Rank,2} "
                             + $"in {result.Iterations} iterations");
            Assert.Equal(trueRank, result.Rank);
        }
    }

    /// <summary>
    /// Where exact recovery stops, which is a property of the theorem and not of this code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candès and co. guarantee exact separation only while the rank is small against the smaller
    /// dimension and the sparse layer is thin — roughly rank ≲ min(m,n)/log²(m,n). Past that the
    /// split stops being unique enough to find, and the low-rank layer starts absorbing directions
    /// that belong to the spikes. On a 60×90 matrix that boundary is somewhere below rank twelve:
    /// rank 6 recovers exactly, rank 12 is reported as 28.
    /// </para>
    /// <para>
    /// Recorded rather than asserted away, because anything built on this has to know where it
    /// stops. What <em>is</em> asserted is that it degrades rather than breaks: the two layers still
    /// add back up to the input, so the result is a worse split and never a wrong one.
    /// </para>
    /// </remarks>
    [Fact]
    public void RecoveryDegradesGracefullyOnceTheRankIsTooHigh()
    {
        const int rows = 60, cols = 90;
        foreach (int trueRank in new[] { 6, 12, 20 })
        {
            double[] low = LowRankPart(rows, cols, trueRank, seed: 300 + trueRank);
            double[] sparse = SparsePart(rows, cols, 150, 1.2, seed: 400 + trueRank);
            var mixed = new double[rows * cols];
            for (int i = 0; i < mixed.Length; i++) mixed[i] = low[i] + sparse[i];

            RobustPcaResult result = RobustPca.Decompose(mixed, rows, cols,
                RobustPcaOptions.Default with { MaximumIterations = 200, Tolerance = 1e-7 });

            double worst = 0;
            for (int i = 0; i < mixed.Length; i++)
                worst = Math.Max(worst, Math.Abs(mixed[i] - result.LowRank[i] - result.Sparse[i]));

            output.WriteLine($"true rank {trueRank,2} -> found {result.Rank,2}, "
                             + $"low off by {Relative(low, result.LowRank):0.0000}, "
                             + $"worst entry off by {worst:0.0000000}");

            Assert.True(worst < 1e-3, $"at rank {trueRank} the layers stopped adding up: {worst}");
        }
    }

    // ── what it is worth on the job it was proposed for ──────────

    /// <summary>
    /// The plan proposed this as a refinement to crackle detection. This measures whether it is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The idea is sound on its face: sustained music repeats across spectrogram frames and lands in
    /// a few singular directions, while crackle is brief and broadband and cannot be written that
    /// way at all — so the sparse layer should be the crackle. What that argument leaves out is that
    /// <em>any</em> transient is brief and broadband, so the decomposition separates sustained from
    /// transient, which is a different question from clean from damaged.
    /// </para>
    /// <para>
    /// Measured against the autoregressive residual detector that ships, on tonal programme with
    /// known impulses added, and reported rather than asserted — the numbers are what decide whether
    /// this is worth wiring in, and they belong in the record either way.
    /// </para>
    /// </remarks>
    [Fact]
    public void MeasuredAsACrackleDetector()
    {
        const int rate = 44_100, length = rate * 2;
        const int fft = 256, hop = 64;

        // Sustained programme: what the low-rank layer is supposed to capture.
        var clean = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)rate;
            clean[i] = (float)(0.35 * Math.Sin(2 * Math.PI * 220 * t)
                             + 0.25 * Math.Sin(2 * Math.PI * 660 * t + 0.4)
                             + 0.18 * Math.Sin(2 * Math.PI * 1_540 * t - 0.7));
        }

        // Crackle: short, broadband, and at known places.
        var random = new Random(77);
        var damaged = (float[])clean.Clone();
        var truth = new bool[length];
        var spans = new List<(int Start, int End)>();
        const int events = 400;
        for (int e = 0; e < events; e++)
        {
            int at = random.Next(fft, length - fft);
            int run = 1 + random.Next(5);
            for (int i = 0; i < run; i++)
            {
                damaged[at + i] += (float)(0.30 * (random.NextDouble() * 2 - 1));
                truth[at + i] = true;
            }
            spans.Add((at, at + run));
        }

        // ── the detector that ships ──
        bool[] byResidual = Mark(length,
            Decrackle.Detect(damaged, DecrackleOptions.Default).Select(e => (e.Start, e.End)));

        // ── the same job, from the sparse layer ──
        int frames = (length - fft) / hop;
        var window = WindowFunctions.Hann(fft, periodic: true);
        var magnitude = new double[frames * (fft / 2 + 1)];
        var re = new float[fft / 2 + 1];
        var im = new float[fft / 2 + 1];
        var block = new float[fft];

        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < fft; i++) block[i] = damaged[f * hop + i] * window[i];
            Fft.RealForward(block, re, im);
            for (int b = 0; b <= fft / 2; b++)
                magnitude[f * (fft / 2 + 1) + b] = Math.Sqrt((double)re[b] * re[b] + (double)im[b] * im[b]);
        }

        RobustPcaResult split = RobustPca.Decompose(magnitude, frames, fft / 2 + 1,
            RobustPcaOptions.Default with { MaximumIterations = 40, MaximumRank = 20 });

        // Per-frame sparse energy, spread back over the samples each frame covers.
        var impulsive = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int b = 0; b <= fft / 2; b++)
            {
                double v = split.Sparse[f * (fft / 2 + 1) + b];
                sum += v * v;
            }
            impulsive[f] = Math.Sqrt(sum);
        }

        // A threshold at the median would be wrong here and worth saying why: with four hundred
        // events in two seconds most frames contain one, so the typical frame IS a damaged frame and
        // the median sits above the thing being looked for. The limit is set instead so the sparse
        // detector flags about as much of the timeline as the residual one does, which is what makes
        // the two comparable at all.
        double[] sorted = [.. impulsive.OrderByDescending(v => v)];
        double limit = sorted[Math.Min(sorted.Length - 1, (int)(frames * 0.35))];

        var bySparse = new bool[length];
        for (int f = 0; f < frames; f++)
        {
            if (impulsive[f] <= limit) continue;
            for (int i = 0; i < hop; i++)
            {
                int at = f * hop + fft / 2 + i - hop / 2;
                if ((uint)at < (uint)length) bySparse[at] = true;
            }
        }

        // Scored per event, not per sample. A 256-sample frame cannot resolve a three-sample tick,
        // so a frame-based detector's per-sample precision is capped by its own window length and
        // comparing the two that way measures the window rather than the method. The fair questions
        // are how many events were caught, and how much of the timeline had to be flagged to catch
        // them.
        (double residualCaught, double residualFlagged) = Score(spans, byResidual);
        (double sparseCaught, double sparseFlagged) = Score(spans, bySparse);

        output.WriteLine($"rank {split.Rank}, {split.Iterations} iterations on {frames}x{fft / 2 + 1}");
        output.WriteLine($"  AR residual   caught {residualCaught:0.000} of events, "
                         + $"flagged {residualFlagged:0.000} of the timeline");
        output.WriteLine($"  RPCA sparse   caught {sparseCaught:0.000} of events, "
                         + $"flagged {sparseFlagged:0.000} of the timeline");

        // Asserted only to the extent that both find something; which is better is the finding, and
        // it is written down above rather than encoded as a demand.
        Assert.True(residualCaught > 0.2, "the shipping detector found almost nothing to compare against");
        Assert.True(sparseFlagged > 0, "the sparse layer localised nothing at all");

        static bool[] Mark(int length, IEnumerable<(int Start, int End)> spans)
        {
            var marked = new bool[length];
            foreach ((int start, int end) in spans)
                for (int i = Math.Max(0, start); i < Math.Min(length, end); i++)
                    marked[i] = true;
            return marked;
        }

        static (double Caught, double Flagged) Score(List<(int Start, int End)> events, bool[] found)
        {
            int caught = 0;
            foreach ((int start, int end) in events)
            {
                for (int i = start; i < end; i++)
                {
                    if (!found[i]) continue;
                    caught++;
                    break;
                }
            }
            int flagged = found.Count(v => v);
            return (events.Count > 0 ? caught / (double)events.Count : 0,
                    flagged / (double)found.Length);
        }
    }

    // ── the shape of the problem ─────────────────────────────────

    /// <summary>Raising the weight keeps less in the sparse layer, which is what it is for.</summary>
    [Fact]
    public void TheWeightControlsHowMuchLandsInTheSparseLayer()
    {
        const int rows = 50, cols = 60;
        double[] low = LowRankPart(rows, cols, 4, seed: 41);
        double[] sparse = SparsePart(rows, cols, 200, 1.0, seed: 43);
        var mixed = new double[rows * cols];
        for (int i = 0; i < mixed.Length; i++) mixed[i] = low[i] + sparse[i];

        double Occupancy(double weight)
        {
            RobustPcaResult result = RobustPca.Decompose(mixed, rows, cols,
                RobustPcaOptions.Default with { SparsityWeight = weight, MaximumIterations = 120 });
            return result.Sparse.Count(v => v != 0) / (double)(rows * cols);
        }

        double loose = Occupancy(0.05), tight = Occupancy(0.5);
        output.WriteLine($"weight 0.05 fills {loose:0.000} of the sparse layer, 0.5 fills {tight:0.000}");
        Assert.True(tight < loose, "a heavier weight must keep less");
    }

    [Fact]
    public void AZeroMatrixDecomposesToNothing()
    {
        RobustPcaResult result = RobustPca.Decompose(new double[30 * 30], 30, 30);
        Assert.All(result.LowRank, v => Assert.Equal(0, v));
        Assert.All(result.Sparse, v => Assert.Equal(0, v));
    }

    [Fact]
    public void AMisshapenMatrixIsRejected()
    {
        Assert.Throws<ArgumentException>(() => RobustPca.Decompose(new double[10], 5, 5));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RobustPca.Decompose(LowRankPart(60, 60, 3, 1), 60, 60, RobustPcaOptions.Default,
                cancelled.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        var reports = new List<double>();
        RobustPca.Decompose(LowRankPart(40, 40, 2, 5), 40, 40,
            RobustPcaOptions.Default with { MaximumIterations = 30 },
            CancellationToken.None, new Sink(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
    }

    private sealed class Sink(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
