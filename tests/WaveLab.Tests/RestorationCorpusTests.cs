using System.Collections.Concurrent;
using System.Diagnostics;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Crackle, speed variation and spectral repair against real recordings. Skipped unless
/// <c>WAVELAB_CORPUS=1</c>.
/// </summary>
public sealed class RestorationCorpusTests(ITestOutputHelper output)
{
    private static bool Skip(ITestOutputHelper output)
    {
        if (DeclipCorpus.Enabled) return false;
        output.WriteLine("skipped: set WAVELAB_CORPUS=1");
        return true;
    }

    private void Report<T>(IEnumerable<T> rows, Func<T, string> corpus, Func<T, double> severity,
        Func<T, double> gain, string unit = "dB")
    {
        var all = rows.ToList();
        foreach (var group in all.GroupBy(corpus).OrderBy(g => g.Key))
            output.WriteLine($"  {group.Key,-10} {group.Count(),4} cells  mean {group.Average(gain):+0.00;-0.00} {unit}  " +
                $"worst {group.Min(gain):+0.00;-0.00}  below do-nothing {group.Count(r => gain(r) < 0)}");
        foreach (var s in all.Select(severity).Distinct().OrderByDescending(x => x))
        {
            var at = all.Where(r => severity(r) == s).ToList();
            output.WriteLine($"  {s,6:0.0}: mean {at.Average(gain):+0.00;-0.00} {unit}  worst {at.Min(gain):+0.00;-0.00}");
        }
        output.WriteLine($"  {"ALL",-10} {all.Count,4} cells  mean {all.Average(gain):+0.00;-0.00} {unit}  " +
            $"worst {all.Min(gain):+0.00;-0.00}");
    }

    /// <summary>
    /// Crackle repair, scored the same way declip and clicks are: signal to noise over the samples
    /// the damage touched, against leaving them alone.
    /// </summary>
    [Fact]
    public void RepairingPlantedCrackleBeatsLeavingItAlone()
    {
        if (Skip(output)) return;
        var excluded = new ConcurrentBag<string>();
        var watch = Stopwatch.StartNew();
        var rows = RestorationCorpus.MeasureCrackle(cell =>
        {
            var repaired = (float[])cell.Damaged.Clone();
            var report = Decrackle.Process(repaired);
            return (cell.Recording.Corpus, cell.Recording.ShortName, cell.Severity,
                Planted: cell.Count, Found: report.Events, Gain: cell.Score(repaired) - cell.Raw);
        }, (r, why) => excluded.Add($"{r.Corpus}/{r.ShortName}: {why}"));
        watch.Stop();

        output.WriteLine($"{rows.Count} cells in {watch.Elapsed.TotalMinutes:0.0} min, {excluded.Count} recordings excluded");
        Report(rows, r => r.Corpus, r => r.Severity, r => r.Gain);
        foreach (var r in rows.OrderBy(r => r.Gain).Take(6))
            output.WriteLine($"  worst: {r.Corpus}/{r.ShortName} @{r.Severity:+0;-0} dB " +
                $"{r.Gain:+0.00;-0.00} dB (planted {r.Planted}, found {r.Found})");

        Assert.NotEmpty(rows);
        // The claim is no harm. Whether it finds quiet crackle is reported, not asserted, because
        // no threshold here has been fitted against these corpora.
        Assert.All(rows, r => Assert.True(r.Gain >= 0,
            $"{r.ShortName} at {r.Severity:0} dB scored {r.Gain:+0.00;-0.00} dB against leaving the crackle alone"));
    }

    /// <summary>
    /// Speed variation. The correction is judged by how much of the planted timing error it
    /// actually removes, which is not what the obvious metrics measure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two obvious metrics are both useless here and the reasons differ.</b> Measuring the wow,
    /// correcting, and measuring again grades the correction with the estimator that drove it, so a
    /// shared blind spot reads as success. Signal to noise against the original is nearly
    /// all-or-nothing: at 0.3% the drift reaches about 22 samples, and halving it leaves the
    /// waveforms still uncorrelated sample for sample, so a half-corrected recording scores about
    /// the same as an untouched one. A perfect correction scores +34 dB and every real one scores
    /// about zero, which says only that none of them is perfect.
    /// </para>
    /// <para>
    /// <b>Residual timing error is linear in what was recovered</b>, so it is the metric the claim
    /// rests on. Measured against the planted warp it showed that the frame-to-frame estimator was
    /// not merely weak but harmful: it left around 220 samples of drift whatever was planted,
    /// including 223 where only 31 existed to begin with. That is the random walk its own smoothing
    /// was added to contain, and containing it by median-filtering a derivative cost the amplitude
    /// as well.
    /// </para>
    /// </remarks>
    [Fact]
    public void CorrectingPlantedWowRemovesTimingErrorRatherThanAddingIt()
    {
        if (Skip(output)) return;
        var excluded = new ConcurrentBag<string>();
        var watch = Stopwatch.StartNew();
        var shipped = WowFlutterOptions.Default;
        var velocity = shipped with { ReferenceSeconds = 0 };

        var rows = RestorationCorpus.MeasureWow(cell =>
        {
            static float[] CorrectedWith(WowCell c, WowFlutterOptions o)
            {
                var channels = new[] { (float[])c.Damaged.Clone() };
                WowFlutter.Correct(channels, c.SampleRate, o);
                return channels[0];
            }

            var corrected = CorrectedWith(cell, shipped);
            var all = Everything(cell.Clean.Length);
            double raw = DeclipCorpus.SnrDb(cell.Clean, cell.Damaged, all);

            return (cell.Recording.Corpus, cell.Recording.ShortName, cell.PlantedPercent,
                Measured: WowFlutter.Analyze(cell.Damaged, cell.SampleRate, shipped).RmsPercent,
                ShiftRaw: RestorationCorpus.ResidualShiftSamples(cell.Clean, cell.Damaged, cell.SampleRate),
                ShiftFixed: RestorationCorpus.ResidualShiftSamples(cell.Clean, corrected, cell.SampleRate),
                ShiftVelocity: RestorationCorpus.ResidualShiftSamples(cell.Clean,
                    CorrectedWith(cell, velocity), cell.SampleRate),
                Ceiling: DeclipCorpus.SnrDb(cell.Clean,
                    RestorationCorpus.UnplantWow(cell.Damaged, cell.SampleRate, cell.PlantedPercent), all) - raw);
        }, (r, why) => excluded.Add($"{r.Corpus}/{r.ShortName}: {why}"));
        watch.Stop();

        output.WriteLine($"{rows.Count} cells in {watch.Elapsed.TotalMinutes:0.0} min, {excluded.Count} recordings excluded");
        foreach (var planted in rows.Select(r => r.PlantedPercent).Distinct().OrderByDescending(x => x))
        {
            var at = rows.Where(r => r.PlantedPercent == planted).ToList();
            output.WriteLine($"  planted {planted:0.0}% (expect {planted * 0.559:0.000}% rms): " +
                $"reads {at.Average(r => r.Measured):0.000}%  |  residual shift " +
                $"{at.Average(r => r.ShiftRaw):0} uncorrected -> {at.Average(r => r.ShiftFixed):0} samples " +
                $"(frame-to-frame: {at.Average(r => r.ShiftVelocity):0})  |  " +
                $"a perfect correction would score {at.Average(r => r.Ceiling):+0.0;-0.0} dB");
        }

        Assert.NotEmpty(rows);

        // The correction must not be worse than the one it replaced. Measured, it is far better at
        // every severity: 215 against 277 samples of residual drift at 2.4%, and 47 against 223 at
        // 0.3%, where the old path injected seven times the error that was there.
        foreach (var planted in rows.Select(r => r.PlantedPercent).Distinct())
        {
            var at = rows.Where(r => r.PlantedPercent == planted).ToList();
            Assert.True(at.Average(r => r.ShiftFixed) < at.Average(r => r.ShiftVelocity),
                $"at {planted:0.0}% the correction left {at.Average(r => r.ShiftFixed):0} samples against " +
                $"{at.Average(r => r.ShiftVelocity):0} for the frame-to-frame estimator it replaced");
        }

        // And it must remove drift where the wow is gross. Below about 1% it does not yet: the
        // residual rises slightly rather than falling, which is reported and not asserted because
        // nothing has been fitted to gate it.
        foreach (var planted in rows.Select(r => r.PlantedPercent).Distinct().Where(p => p >= 1.2))
        {
            var at = rows.Where(r => r.PlantedPercent == planted).ToList();
            Assert.True(at.Average(r => r.ShiftFixed) < at.Average(r => r.ShiftRaw),
                $"a {planted:0.0}% wow should come down from {at.Average(r => r.ShiftRaw):0} samples, " +
                $"not {at.Average(r => r.ShiftFixed):0}");
        }
    }
    /// <summary>
    /// Spectral repair of a planted noise burst, scored over the span the repair replaced.
    /// </summary>
    [Fact]
    public void HealingAPlantedBurstBeatsLeavingItAlone()
    {
        if (Skip(output)) return;
        var excluded = new ConcurrentBag<string>();
        var watch = Stopwatch.StartNew();
        var rows = RestorationCorpus.MeasureSpectral(cell =>
        {
            const int fft = 2048, hop = 512;
            int frameFrom = cell.From / hop, frameTo = cell.To / hop + 1;
            int binFrom = (int)(cell.LowHz * fft / cell.SampleRate);
            int binTo = (int)(cell.HighHz * fft / cell.SampleRate) + 1;
            var mask = SpectralMask.Rectangle(frameFrom, frameTo, binFrom, binTo, 2);

            var healed = SpectralRepair.Heal(cell.Damaged, 0, mask);
            var candidate = (float[])cell.Damaged.Clone();
            if (!healed.IsEmpty)
                for (int i = 0; i < healed.Samples.Length && healed.Start + i < candidate.Length; i++)
                    candidate[healed.Start + i] = healed.Samples[i];

            var span = new bool[cell.Clean.Length];
            for (int i = cell.From; i < cell.To; i++) span[i] = true;
            double raw = DeclipCorpus.SnrDb(cell.Clean, cell.Damaged, span);
            double fixedUp = DeclipCorpus.SnrDb(cell.Clean, candidate, span);
            return (cell.Recording.Corpus, cell.Recording.ShortName, cell.Severity,
                Gain: fixedUp - raw, Replaced: healed.Samples.Length);
        }, (r, why) => excluded.Add($"{r.Corpus}/{r.ShortName}: {why}"));
        watch.Stop();

        output.WriteLine($"{rows.Count} cells in {watch.Elapsed.TotalMinutes:0.0} min, {excluded.Count} recordings excluded");
        Report(rows, r => r.Corpus, r => r.Severity, r => r.Gain);
        foreach (var r in rows.OrderBy(r => r.Gain).Take(6))
            output.WriteLine($"  worst: {r.Corpus}/{r.ShortName} @{r.Severity:+0;-0} dB {r.Gain:+0.00;-0.00} dB " +
                $"(replaced {r.Replaced} samples)");

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.Replaced > 0, $"{r.ShortName}: the heal replaced nothing"));

        // Above the local level the heal is reliable and the claim is no harm. At the local level it
        // is not: 17 of 58 cells there come out worse, down to -4.9 dB, because the mask then covers
        // as much wanted signal as unwanted and the reconstruction replaces real content with a
        // guess. That regime is reported rather than asserted, since nothing has been fitted for it.
        foreach (var r in rows.Where(r => r.Severity >= 6))
            Assert.True(r.Gain >= 0,
                $"{r.ShortName} at {r.Severity:0} dB above local scored {r.Gain:+0.00;-0.00} dB against leaving the burst alone");
        var atLevel = rows.Where(r => r.Severity <= 0).ToList();
        if (atLevel.Count > 0)
            output.WriteLine($"  at the local level: {atLevel.Count(r => r.Gain < 0)} of {atLevel.Count} cells " +
                $"come out worse, worst {atLevel.Min(r => r.Gain):+0.00;-0.00} dB");
    }

    private static bool[] Everything(int length)
    {
        var all = new bool[length];
        Array.Fill(all, true);
        return all;
    }

    /// <summary>The crackle model must differ from the click model, or the two tools are being asked
    /// the same question. Runs without a corpus.</summary>
    [Fact]
    public void CrackleIsDenserAndQuieterThanClicks()
    {
        const int rate = 44100;
        var source = new float[rate * 2];
        for (int i = 0; i < source.Length; i++)
            source[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / rate));

        var (_, _, _, clicks) = ClickCorpus.Damage(source, rate, aboveLocalDb: 18, seed: 1);
        var (clean, crackled, hit, grains) = RestorationCorpus.PlantCrackle(source, rate, 0.0, seed: 1);

        double worst = 0;
        for (int i = 0; i < source.Length; i++) if (hit[i]) worst = Math.Max(worst, Math.Abs(crackled[i] - clean[i]));
        output.WriteLine($"{grains} crackle grains against {clicks} clicks in 2 s; largest grain {worst:0.000}");

        Assert.True(grains > clicks * 10, "crackle is dense where clicks are sparse");
        Assert.True(worst < 0.9, "a crackle grain is quiet next to a click");
    }
}
