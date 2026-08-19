using System.Diagnostics;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The corpus harness itself, and the standing measurement of the declip chain over it.
/// </summary>
/// <remarks>
/// Everything here is skipped unless <c>WAVELAB_CORPUS=1</c>, because the corpora are external and
/// mostly not redistributable. The two tests that do run unconditionally are the ones that need no
/// audio: that the harness stays off by default, and that its damage model is self-consistent.
/// </remarks>
public sealed class DeclipCorpusTests(ITestOutputHelper output)
{
    /// <summary>
    /// The suite must not depend on audio that only exists on one machine, and a harness that runs
    /// by accident is how a ten-second suite became a five-minute one.
    /// </summary>
    [Fact]
    public void TheHarnessIsOffUnlessItIsAskedFor()
    {
        if (Environment.GetEnvironmentVariable("WAVELAB_CORPUS") is null)
            Assert.False(DeclipCorpus.Enabled);
        output.WriteLine($"corpus harness enabled: {DeclipCorpus.Enabled}");
    }

    /// <summary>
    /// The damage model is the part every measurement rests on, so it is checked without needing a
    /// corpus: clipping marks exactly the samples it flattened, and the plateau lands on the rail.
    /// </summary>
    [Fact]
    public void DamageMarksExactlyWhatItFlattened()
    {
        var source = new float[4096];
        for (int i = 0; i < source.Length; i++)
            source[i] = (float)(0.8 * Math.Sin(i * 0.01) + 0.2 * Math.Sin(i * 0.13));

        var (clean, clipped, damaged) = DeclipCorpus.Damage(source, 0.70, clickResistant: false);

        int marked = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (damaged[i])
            {
                marked++;
                Assert.True(Math.Abs(clipped[i]) <= 1.0 + 1e-5);
                Assert.True(Math.Abs(clean[i]) > Math.Abs(clipped[i]) - 1e-5);
            }
            else
            {
                Assert.Equal(clean[i], clipped[i], 5);
            }
        }
        Assert.True(marked > 0, "clipping at 0.70 of peak should flatten something");
        double peak = 0;
        foreach (float v in clipped) peak = Math.Max(peak, Math.Abs(v));
        Assert.Equal(1.0, peak, 4);
        output.WriteLine($"{marked} of {source.Length} samples flattened, plateau at {peak:0.0000}");
    }

    /// <summary>
    /// The standing measurement: over every corpus present, repairing must beat leaving the damage
    /// alone in every cell. Reports the table the commit messages quote.
    /// </summary>
    [Fact]
    public void TheChainBeatsLeavingTheDamageAlone()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        var watch = Stopwatch.StartNew();
        var results = DeclipCorpus.Measure(cell =>
        {
            var repaired = Restoration.RepairClipping([cell.Clipped], cell.Events);
            return (cell.Recording.Corpus, cell.Recording.ShortName, cell.Relative,
                Gain: cell.Score(repaired[0]) - cell.Raw);
        });
        watch.Stop();

        output.WriteLine($"{results.Count} cells in {watch.Elapsed.TotalMinutes:0.0} min");
        foreach (var group in results.GroupBy(r => r.Corpus).OrderBy(g => g.Key))
        {
            var gains = group.Select(r => r.Gain).ToList();
            output.WriteLine($"  {group.Key,-10} {gains.Count,4} cells  mean {gains.Average():+0.00;-0.00} dB  " +
                $"worst {gains.Min():+0.00;-0.00}  below do-nothing {gains.Count(g => g < 0)}");
        }
        var all = results.Select(r => r.Gain).ToList();
        output.WriteLine($"  {"ALL",-10} {all.Count,4} cells  mean {all.Average():+0.00;-0.00} dB  " +
            $"worst {all.Min():+0.00;-0.00}");

        foreach (var r in results.Where(r => r.Gain < 0))
            output.WriteLine($"  WORSE THAN DOING NOTHING: {r.ShortName} @{r.Relative:0.00} {r.Gain:+0.00;-0.00} dB");
        Assert.All(results, r => Assert.True(r.Gain > 0,
            $"{r.ShortName} at {r.Relative:0.00} scored {r.Gain:+0.00;-0.00} dB against leaving the damage alone"));
    }

    /// <summary>
    /// The delta cache must return exactly what the solver returned, or every sweep built on it is
    /// measuring the cache rather than the chain.
    /// </summary>
    [Fact]
    public void TheCachedSolverResultIsTheSolverResult()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        var checkedCells = 0;
        var results = DeclipCorpus.Measure(cell =>
        {
            var cached = DeclipCorpus.Solve(cell);          // first call may populate
            var again = DeclipCorpus.Solve(cell);           // second call must hit the cache
            var direct = (float[])cell.Clipped.Clone();
            Spade.Declip(direct, cell.ClipLevel, SpadeOptions.Default);

            int differences = 0;
            for (int i = 0; i < direct.Length; i++)
                if (!direct[i].Equals(again[i])) differences++;
            return (cell.Recording.ShortName, cell.Relative, Differences: differences,
                Changed: CountChanged(cell.Clipped, direct), cached.Length);
        }, maximumParallelism: 4);

        foreach (var r in results)
        {
            checkedCells++;
            Assert.True(r.Differences == 0,
                $"{r.ShortName} at {r.Relative:0.00}: cache differs from the solver on {r.Differences} samples");
        }
        double share = results.Average(r => (double)r.Changed / Math.Max(1, r.Length));
        output.WriteLine($"{checkedCells} cells verified; the solver moves {share:P3} of samples on average, " +
            "which is why the cache stores indices rather than the whole array");
    }


    /// <summary>
    /// A ceiling sweep, which is the shape most of these experiments take: solve once, then apply
    /// many policies to the same reconstruction. With the delta cache warm the solver never runs,
    /// so a sweep that took hours takes seconds and the cost stops scaling with the number of
    /// policies tried.
    /// </summary>
    [Fact]
    public void ACeilingSweepReusesOneSolve()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        double maximumGain = Math.Pow(10.0, 6.0 / 20.0);
        double[] multipliers = [1.00, 1.10, 1.25, 1.40, 1.60, 2.00];

        var watch = Stopwatch.StartNew();
        var results = DeclipCorpus.Measure(cell =>
        {
            var solved = DeclipCorpus.Solve(cell);
            bool bounded = Restoration.ShouldersBoundTheReconstruction(cell.Events);
            double clipLevel = cell.ClipLevel;
            double flat = clipLevel * maximumGain;

            var scores = new double[multipliers.Length];
            var candidate = new float[cell.Clipped.Length];
            for (int m = 0; m < multipliers.Length; m++)
            {
                for (int i = 0; i < candidate.Length; i++)
                    candidate[i] = (float)Math.Clamp(solved[i], -flat, flat);
                if (bounded)
                {
                    foreach (var e in cell.Events)
                    {
                        double rail = e.AbsoluteClipLevel;
                        if (!(rail > 0)) continue;
                        double overshoot = Math.Abs(e.EstimatedTruePeak) / rail - 1;
                        double allowed = Math.Min(rail * maximumGain,
                            Math.Max(rail, rail * (1 + overshoot * multipliers[m])));
                        int from = Math.Max(0, e.StartSample), to = Math.Min(candidate.Length, e.EndSample);
                        for (int i = from; i < to; i++)
                            if (candidate[i] > allowed) candidate[i] = (float)allowed;
                            else if (candidate[i] < -allowed) candidate[i] = (float)-allowed;
                    }
                }
                scores[m] = cell.Score(candidate) - cell.Raw;
            }
            return (cell.Recording.Corpus, Scores: scores);
        });
        watch.Stop();

        output.WriteLine($"{results.Count} cells x {multipliers.Length} ceilings in " +
            $"{watch.Elapsed.TotalSeconds:0.0} s");
        for (int m = 0; m < multipliers.Length; m++)
        {
            int index = m;
            output.WriteLine($"  x{multipliers[m]:0.00}  " + string.Join("  ",
                results.GroupBy(r => r.Corpus).OrderBy(g => g.Key).Select(g =>
                    $"{g.Key} {g.Average(r => r.Scores[index]):+0.000;-0.000}")) +
                $"   ALL {results.Average(r => r.Scores[index]):+0.000;-0.000}");
        }
        Assert.NotEmpty(results);
    }

    private static int CountChanged(float[] original, float[] solved)
    {
        int changed = 0;
        for (int i = 0; i < original.Length; i++) if (!original[i].Equals(solved[i])) changed++;
        return changed;
    }
}
