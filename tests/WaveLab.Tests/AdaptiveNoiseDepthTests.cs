using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The cheap half of the noise-reduction headroom: reduce less when there is less to remove.
/// </summary>
/// <remarks>
/// <para>
/// The oracle Wiener mask beats the shipped gate by <b>+9.63 dB over 108 cells</b>
/// (<see cref="NoiseReductionCeilingTests"/>), but <b>8.13 dB of the 13.21 dB gap at the quietest
/// severity is not an estimation problem at all</b> — it is the gate firing at full depth on hiss
/// already 30 dB under the programme, where a fixed 10 dB reduction costs more music than it saves
/// noise. Doing nothing scores 0 there and the gate scores −8.13. No model is needed to collect
/// that; a rule that declines to fire would do it.
/// </para>
/// <para>
/// This file is that experiment, and it is built in the order the repo's own history says it has to
/// be. <b>First the estimator</b>: the rule can only key off something measurable at run time, so
/// the question of whether the noise-to-programme ratio can be estimated from the audio alone is
/// settled before any rule is fitted to it. <b>Then the rule</b>, swept rather than reasoned about.
/// <b>Then held out</b> — by recording and by corpus, never by severity, which is the protocol five
/// failed declip calibrations produced.
/// </para>
/// </remarks>
public sealed class AdaptiveNoiseDepthTests(ITestOutputHelper output)
{
    /// <summary>
    /// How far the noise sits below the programme, estimated from the audio and nothing else.
    /// </summary>
    /// <remarks>
    /// The quietest two-second window is where a tool looks for a noise profile, and its level is
    /// the best available estimate of the floor; the whole signal's level is the programme. Neither
    /// needs a clean reference, which is the point — a rule that needed one could not ship.
    /// <b>Where a recording has no genuinely quiet passage the window contains music and the
    /// estimate reads the noise as louder than it is</b>, so the rule sees less headroom than there
    /// is and reduces more. That is the safe direction, and it is the situation a user is actually
    /// in.
    /// </remarks>
    private static double EstimateNoiseToProgrammeDb(float[] signal, int sampleRate) =>
        Restoration.EstimateNoiseToProgrammeDb([signal], sampleRate);

    [Fact]
    public void NoiseToProgrammeMeasurementObservesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Restoration.EstimateNoiseToProgrammeDb(
                [new float[48_000]], 48_000,
                Restoration.NoiseDepthCeilingDb, cancelled.Token));
    }

    /// <summary>The reduction depths measured per cell, in dB. Zero means declining to fire at all.</summary>
    /// <remarks>
    /// Sweeping the <b>depth</b> rather than the rule's parameters is what makes this affordable and
    /// what makes it better evidence. The depth is the only thing a rule can change, so one pass
    /// measuring the whole depth response of every cell lets any rule be scored afterwards from the
    /// table, instantly, instead of re-running the corpus per candidate. It also yields the
    /// per-cell best depth, which bounds what <i>any</i> depth rule can achieve - the same ceiling
    /// argument as the oracle mask, one level down.
    /// </remarks>
    private static readonly double[] Depths = [0, 1, 2, 3, 4, 6, 8, 10];

    private sealed record Cell(string Corpus, string Name, double Planted, double Estimate, double[] Gain);

    private static List<Cell> Sweep()
    {
        const double SensitivityDb = 5.0;
        return RestorationCorpus.MeasureNoise(cell =>
        {
            double raw = RestorationCorpus.SegmentalSnrDb(cell.Clean, cell.Damaged, cell.SampleRate);
            var gain = new double[Depths.Length];
            for (int d = 0; d < Depths.Length; d++)
            {
                if (Depths[d] <= 0) { gain[d] = 0; continue; }   // declining to fire is exactly do-nothing
                float[][] work = [(float[])cell.Damaged.Clone()];
                Restoration.ReduceNoise(work, cell.Profile, Depths[d], SensitivityDb);
                gain[d] = RestorationCorpus.SegmentalSnrDb(cell.Clean, work[0], cell.SampleRate) - raw;
            }
            return new Cell(cell.Recording.Corpus, cell.Recording.ShortName, cell.SnrDb,
                EstimateNoiseToProgrammeDb(cell.Damaged, cell.SampleRate), gain);
        });
    }

    /// <summary>The depth a rule picks: full below <paramref name="full"/>, nothing above <paramref name="cutoff"/>.</summary>
    private static double RuleDepth(double estimate, double cutoff, double full, double requested)
    {
        if (estimate >= cutoff) return 0;
        if (estimate <= full) return requested;
        return requested * (cutoff - estimate) / (cutoff - full);
    }

    /// <summary>What a cell scores at a depth the ladder did not measure, read off the ladder.</summary>
    private static double GainAt(Cell cell, double depth)
    {
        if (depth <= Depths[0]) return cell.Gain[0];
        for (int i = 1; i < Depths.Length; i++)
        {
            if (depth > Depths[i]) continue;
            double t = (depth - Depths[i - 1]) / (Depths[i] - Depths[i - 1]);
            return cell.Gain[i - 1] + t * (cell.Gain[i] - cell.Gain[i - 1]);
        }
        return cell.Gain[^1];
    }

    private static double Score(IEnumerable<Cell> cells, double cutoff, double full, double requested) =>
        cells.Average(c => GainAt(c, RuleDepth(c.Estimate, cutoff, full, requested)));

    private static readonly double[] Cutoffs = [8, 10, 12, 14, 16, 18, 20, 24, 30];
    private static readonly double[] Fulls = [0, 2, 4, 6];

    private static (double Cutoff, double Full, double Score) BestRule(IReadOnlyList<Cell> cells, double requested)
    {
        (double Cutoff, double Full, double Score) best = (0, 0, double.NegativeInfinity);
        foreach (double cutoff in Cutoffs)
            foreach (double full in Fulls)
            {
                if (full >= cutoff) continue;
                double score = Score(cells, cutoff, full, requested);
                if (score > best.Score) best = (cutoff, full, score);
            }
        return best;
    }

    /// <summary>
    /// The whole experiment: the depth response, the ceiling a depth rule could reach, the rule
    /// that fits, and what it is worth held out.
    /// </summary>
    [Fact]
    public void ReducingLessWhereThereIsLessToRemove()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        const double Requested = 10.0;                  // the workbench default the gate ships at
        List<Cell> cells = Sweep();
        if (cells.Count == 0) { output.WriteLine("no corpus recordings found"); return; }

        // 1. The depth response, which is the thing being exploited.
        output.WriteLine($"segmental gain by planted severity and reduction depth, {cells.Count} cells");
        output.WriteLine("planted     " + string.Join("", Depths.Select(d => $"{d + " dB",8}")));
        foreach (var group in cells.GroupBy(c => c.Planted).OrderByDescending(g => g.Key))
            output.WriteLine($"{group.Key + " down",-12}" +
                string.Join("", Depths.Select((_, i) => $"{group.Average(c => c.Gain[i]),8:+0.00;-0.00}")));

        // 2. The ceiling any depth rule could reach: the best depth per cell, chosen in hindsight.
        double fixedTen = cells.Average(c => c.Gain[^1]);
        double oracleDepth = cells.Average(c => c.Gain.Max());
        output.WriteLine($"\nfixed {Requested:F0} dB (ships today): {fixedTen:+0.00;-0.00} dB");
        output.WriteLine($"best depth per cell, in hindsight:  {oracleDepth:+0.00;-0.00} dB " +
            $"- the ceiling for any depth rule");

        // 3. The rule that fits the whole set.
        var fit = BestRule(cells, Requested);
        output.WriteLine($"\nbest rule over all cells: full below {fit.Full:F0} dB, nothing above " +
            $"{fit.Cutoff:F0} dB -> {fit.Score:+0.00;-0.00} dB");

        // 3b. The score surface, because a fit is only worth having if it sits on a plateau. A peak
        //     means the parameters are carrying the corpus rather than the phenomenon, and the fold
        //     disagreement below is the same question asked a second way.
        output.WriteLine("");
        output.WriteLine("score surface (rows: nothing above, columns: full below)");
        output.WriteLine("        " + string.Join("", Fulls.Select(f => $"{f + " dB",9}")));
        foreach (double cutoff in Cutoffs)
            output.WriteLine($"{cutoff + " dB",-8}" + string.Join("", Fulls.Select(f =>
                f >= cutoff ? $"{"-",9}" : $"{Score(cells, cutoff, f, Requested),9:+0.00;-0.00}")));
        output.WriteLine("");

        // 4. Held out by recording, then by corpus. Never by severity: fitting a curve in severity
        //    and testing it on other severities of the same recording is the mistake five declip
        //    calibrations made, and it is what makes a rule that memorises materials look good.
        foreach (string what in new[] { "recording", "corpus" })
        {
            var groups = what == "recording"
                ? cells.GroupBy(c => c.Name).ToList()
                : cells.GroupBy(c => c.Corpus).ToList();

            double heldOut = 0, baseline = 0;
            var chosen = new List<(double Cutoff, double Full)>();
            foreach (var held in groups)
            {
                var rest = cells.Where(c => !held.Contains(c)).ToList();

                // Nothing to fit on. Reachable with a single corpus or a single recording present,
                // which is an ordinary way to run this - and Average() throws on an empty sequence,
                // so without this the run dies with "Sequence contains no elements" instead of
                // reporting what it does have.
                if (rest.Count == 0)
                {
                    output.WriteLine($"held out by {what,-10} skipped: only one {what} in the corpus");
                    heldOut = baseline = double.NaN;
                    break;
                }

                var rule = BestRule(rest, Requested);
                chosen.Add((rule.Cutoff, rule.Full));
                heldOut += Score(held, rule.Cutoff, rule.Full, Requested) * held.Count();
                baseline += held.Sum(c => c.Gain[^1]);
            }
            if (double.IsNaN(heldOut)) continue;
            heldOut /= cells.Count;
            baseline /= cells.Count;
            int agreed = chosen.Count(c => c == (fit.Cutoff, fit.Full));
            output.WriteLine($"held out by {what,-10} {heldOut:+0.00;-0.00} dB against {baseline:+0.00;-0.00} " +
                $"fixed, {agreed} of {groups.Count} folds picked the same rule");
        }

        // 5. What it does to the worst cases, which the mean hides and which is the part a user
        //    would actually notice. "Never much worse than leaving it alone" is a guarantee; half a
        //    decibel of mean is not.
        var underRule = cells.Select(c => GainAt(c, RuleDepth(c.Estimate, fit.Cutoff, fit.Full, Requested))).ToList();
        var underFixed = cells.Select(c => c.Gain[^1]).ToList();
        output.WriteLine($"worst cell:  fixed {underFixed.Min():+0.00;-0.00} dB, " +
            $"rule {underRule.Min():+0.00;-0.00} dB");
        output.WriteLine($"cells below do-nothing: fixed {underFixed.Count(g => g < 0)}, " +
            $"rule {underRule.Count(g => g < 0)}, of {cells.Count}");
        output.WriteLine($"cells worse than -3 dB: fixed {underFixed.Count(g => g < -3)}, " +
            $"rule {underRule.Count(g => g < -3)}");
        output.WriteLine($"cells the rule improves: {underRule.Where((g, i) => g > underFixed[i]).Count()}, " +
            $"worsens: {underRule.Where((g, i) => g < underFixed[i]).Count()}");

        // The rule must beat the fixed depth it replaces, or there is nothing here.
        Assert.True(fit.Score > fixedTen,
            $"the adaptive rule ({fit.Score:F2} dB) did not beat a fixed {Requested:F0} dB ({fixedTen:F2} dB)");
    }

    /// <summary>
    /// Does that estimate track the hiss actually planted? Nothing downstream is worth doing if not.
    /// </summary>
    [Fact]
    public void TheNoiseFloorEstimateTracksThePlantedSeverity()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        var rows = RestorationCorpus.MeasureNoise(cell =>
            (cell.Recording.Corpus, cell.Recording.ShortName, cell.SnrDb,
             Estimate: EstimateNoiseToProgrammeDb(cell.Damaged, cell.SampleRate)));

        if (rows.Count == 0) { output.WriteLine("no corpus recordings found"); return; }

        output.WriteLine($"noise floor estimate against what was planted: {rows.Count} cells");
        output.WriteLine($"{"planted",-12}{"estimate",10}{"spread",10}{"cells",8}");
        var means = new List<(double Planted, double Estimate)>();
        foreach (var group in rows.GroupBy(r => r.SnrDb).OrderByDescending(g => g.Key))
        {
            double mean = group.Average(r => r.Estimate);
            double spread = group.Max(r => r.Estimate) - group.Min(r => r.Estimate);
            output.WriteLine($"{group.Key + " dB down",-12}{mean,10:F1}{spread,10:F1}{group.Count(),8}");
            means.Add((group.Key, mean));
        }

        // Monotone is the whole requirement. The estimate need not equal the planted figure - the
        // recording carries its own floor, and the quietest window holds music as well as noise -
        // but if it does not rise as the planted hiss falls, it cannot drive anything.
        for (int i = 1; i < means.Count; i++)
            Assert.True(means[i].Estimate < means[i - 1].Estimate,
                $"the estimate did not fall with severity: {means[i - 1].Planted} dB down reads " +
                $"{means[i - 1].Estimate:F1}, {means[i].Planted} dB down reads {means[i].Estimate:F1}");
    }

    // ── the shipped rule, pinned without a corpus ────────────────

    /// <summary>
    /// Programme with a quiet passage in it, so the estimator has a floor to find - and with the
    /// noise running through that passage rather than the passage being silent, which is what a
    /// transfer actually looks like and what the estimator is for.
    /// </summary>
    private static float[][] Programme(int rate, double noiseAmplitude, int seed)
    {
        var random = new Random(seed);
        var channel = new float[rate * 8];
        for (int i = 0; i < channel.Length; i++)
        {
            // Six seconds of music, then two carrying only the noise floor.
            double music = i < rate * 6
                ? 0.30 * Math.Sin(2 * Math.PI * 440 * i / (double)rate)
                  + 0.15 * Math.Sin(2 * Math.PI * 1970 * i / (double)rate)
                : 0;
            channel[i] = (float)(music + (random.NextDouble() - 0.5) * 2 * noiseAmplitude);
        }
        return [channel];
    }

    /// <summary>
    /// A loud floor is reduced at the depth asked for; a quiet one is left alone entirely.
    /// </summary>
    /// <remarks>
    /// The two ends of the rule, which is all a non-corpus test can honestly pin. The threshold in
    /// between is a fitted number and belongs to the corpus measurement, not here - asserting it
    /// would be pinning a calibration in a place nobody would think to look when re-fitting it.
    /// </remarks>
    [Fact]
    public void TheDepthFollowsHowMuchNoiseThereIsToRemove()
    {
        const int rate = 44_100;

        // Noise loud enough to be most of what is there: the full request stands.
        float[][] noisy = Programme(rate, 0.40, seed: 1);
        double noisyEstimate = Restoration.EstimateNoiseToProgrammeDb(noisy, rate);
        double noisyDepth = Restoration.SuggestReductionDepthDb(noisy, rate, 10.0);

        // Noise far under the programme: nothing worth taking off.
        float[][] clean = Programme(rate, 0.0005, seed: 2);
        double cleanEstimate = Restoration.EstimateNoiseToProgrammeDb(clean, rate);
        double cleanDepth = Restoration.SuggestReductionDepthDb(clean, rate, 10.0);

        output.WriteLine($"loud floor:  {noisyEstimate:F1} dB above it -> {noisyDepth:F1} dB of reduction");
        output.WriteLine($"quiet floor: {cleanEstimate:F1} dB above it -> {cleanDepth:F1} dB of reduction");

        Assert.True(noisyEstimate < cleanEstimate,
            "the estimator did not separate a loud noise floor from a quiet one");
        Assert.True(noisyDepth > cleanDepth, "a noisier recording was not reduced harder");
        Assert.True(noisyDepth > 0, "a recording that is mostly hiss was not reduced at all");
        Assert.Equal(0, cleanDepth);
    }

    /// <summary>
    /// The rule and the measurement are separate calls, and they must not drift apart.
    /// </summary>
    /// <remarks>
    /// The overload taking an estimate exists because the measurement walks every sample — 388 ms on
    /// a 22-minute stereo side — while the rule is three comparisons, and anything drawing the depth
    /// on screen re-evaluates it on every movement of a slider. Two implementations of one rule is
    /// how they diverge, so there is one, and this pins that the convenience overload is really
    /// calling it.
    /// </remarks>
    [Fact]
    public void BothOverloadsOfTheRuleAgree()
    {
        const int rate = 44_100;
        foreach (double amplitude in new[] { 0.40, 0.05, 0.004, 0.0005 })
        {
            float[][] data = Programme(rate, amplitude, seed: 8);
            double estimate = Restoration.EstimateNoiseToProgrammeDb(data, rate);
            foreach (double requested in new[] { 0.0, 1.0, 10.0, 24.0 })
                Assert.Equal(
                    Restoration.SuggestReductionDepthDb(data, rate, requested),
                    Restoration.SuggestReductionDepthDb(estimate, requested), 9);
        }
    }

    /// <summary>The rule never asks for more than it was given, or for a negative depth.</summary>
    [Fact]
    public void TheDepthIsNeverMoreThanWasRequested()
    {
        const int rate = 44_100;
        foreach (double amplitude in new[] { 0.5, 0.1, 0.02, 0.004, 0.0002, 0.000001 })
        {
            float[][] data = Programme(rate, amplitude, seed: 3);
            foreach (double requested in new[] { 0.0, 1.0, 6.0, 12.0, 40.0 })
            {
                double depth = Restoration.SuggestReductionDepthDb(data, rate, requested);
                Assert.InRange(depth, 0, requested);
            }
        }
    }

    /// <summary>Degenerate input answers rather than throwing, the way every entry point here does.</summary>
    [Fact]
    public void TheEstimatorSurvivesAudioThatHasNothingInIt()
    {
        // Nothing to measure reads as nothing to remove. It must NOT read as zero, which is a
        // real measurement meaning "the programme is no louder than its own floor" and asks for
        // full reduction - handing that back for an empty buffer had digital silence requesting
        // the maximum.
        double ceiling = Restoration.NoiseDepthCeilingDb;
        Assert.Equal(ceiling, Restoration.EstimateNoiseToProgrammeDb([], 44_100));
        Assert.Equal(ceiling, Restoration.EstimateNoiseToProgrammeDb([new float[1000]], 44_100));
        Assert.Equal(ceiling, Restoration.EstimateNoiseToProgrammeDb([[]], 44_100));
        Assert.Equal(ceiling, Restoration.EstimateNoiseToProgrammeDb([new float[1000]], 0));

        Assert.Equal(0, Restoration.SuggestReductionDepthDb([new float[1000]], 44_100, 10));

        // A signal shorter than one analysis window still answers.
        var tiny = new float[64];
        for (int i = 0; i < tiny.Length; i++) tiny[i] = i % 2 == 0 ? 0.5f : -0.5f;
        double depth = Restoration.SuggestReductionDepthDb([tiny], 44_100, 10);
        Assert.InRange(depth, 0, 10);
    }

    /// <summary>
    /// A window across the edge of a spliced-in silence is not the noise floor, and the estimator
    /// is taken in by it. This records the limitation rather than asserting it away.
    /// </summary>
    /// <remarks>
    /// Digital silence itself is skipped, so an all-zero window cannot become the floor. What is
    /// not handled is the <b>boundary</b>: a window lying across the edge of one is almost all
    /// silence and a little programme, passes the silence test, and reads far below the real floor
    /// - so the estimate comes out high and the suppressor reduces less than it should. The fix
    /// that removes it, a low percentile of window energies instead of the minimum, was built and
    /// measured and is <b>worse where it counts</b>: it takes cells that come out worse than doing
    /// nothing from 46 of 108 to 29, where the minimum takes them to 15. A constructed case lost to
    /// a measured one, which is the right way round.
    /// </remarks>
    [Fact]
    public void ASplicedSilenceStillFoolsTheNoiseFloorEstimate()
    {
        const int rate = 44_100;
        float[][] hissy = Programme(rate, 0.40, seed: 5);
        double before = Restoration.SuggestReductionDepthDb(hissy, rate, 10.0);

        var withSilence = new float[hissy[0].Length + rate * 2];
        hissy[0].CopyTo(withSilence, rate * 2);
        double after = Restoration.SuggestReductionDepthDb([withSilence], rate, 10.0);

        output.WriteLine($"hissy programme asks for {before:F1} dB of reduction; with two seconds " +
            $"of digital silence spliced on it asks for {after:F1} dB");

        Assert.True(before > 0, "the hissy programme was not reduced at all");
        Assert.True(after < before, "the limitation this test records has gone - check the corpus " +
            "figures and delete it if the estimator genuinely improved");
    }
}
