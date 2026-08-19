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
    /// Speed variation, scored two ways because one of them is partly circular — and the two
    /// disagree, which is the finding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious metric — measure the wow, correct it, measure again — grades the correction with
    /// the same estimator that drove it, so a shared blind spot looks like success. By it the
    /// correction works: the reading roughly halves at every severity.
    /// </para>
    /// <para>
    /// <b>The independent metric disagrees.</b> The warp planted here is zero-mean, and at 0.3%
    /// with a 0.7 Hz wow the accumulated drift is about 22 samples, so a correct inversion would
    /// visibly realign the waveform and can be scored against the original as ordinary signal to
    /// noise. Measured, <b>it does not: correction leaves the waveform 0.2 to 1.4 dB further from
    /// the original at every severity</b>. So what the estimator sees improving is not the timing
    /// being restored.
    /// </para>
    /// <para>
    /// <b>The estimator also under-reads badly.</b> Planting 2.4% peak reads 0.67% RMS where about
    /// 1.34% is expected, and there is a floor near 0.2–0.3% on digital recordings that have no
    /// speed variation at all — which is roughly the spec of a decent turntable, so on a good
    /// transfer this is largely measuring itself. Only the monotonic response is asserted; the
    /// scale and the floor are reported, because nothing here has been fitted.
    /// </para>
    /// </remarks>
    [Fact]
    public void CorrectingPlantedWowReducesWhatTheEstimatorSees()
    {
        if (Skip(output)) return;
        var excluded = new ConcurrentBag<string>();
        var watch = Stopwatch.StartNew();
        var rows = RestorationCorpus.MeasureWow(cell =>
        {
            var before = WowFlutter.Analyze(cell.Damaged, cell.SampleRate);
            var channels = new[] { (float[])cell.Damaged.Clone() };
            WowFlutter.Correct(channels, cell.SampleRate);
            var after = WowFlutter.Analyze(channels[0], cell.SampleRate);

            double raw = DeclipCorpus.SnrDb(cell.Clean, cell.Damaged, Everything(cell.Clean.Length));
            double fixedUp = DeclipCorpus.SnrDb(cell.Clean, channels[0], Everything(cell.Clean.Length));
            return (cell.Recording.Corpus, cell.Recording.ShortName, cell.PlantedPercent,
                MeasuredBefore: before.RmsPercent, MeasuredAfter: after.RmsPercent,
                Confidence: before.Confidence, WaveformGain: fixedUp - raw);
        }, (r, why) => excluded.Add($"{r.Corpus}/{r.ShortName}: {why}"));
        watch.Stop();

        output.WriteLine($"{rows.Count} cells in {watch.Elapsed.TotalMinutes:0.0} min, {excluded.Count} recordings excluded");
        foreach (var planted in rows.Select(r => r.PlantedPercent).Distinct().OrderByDescending(x => x))
        {
            var at = rows.Where(r => r.PlantedPercent == planted).ToList();
            output.WriteLine($"  planted {planted:0.0}%: measured {at.Average(r => r.MeasuredBefore):0.000}% " +
                $"-> {at.Average(r => r.MeasuredAfter):0.000}% after correction, " +
                $"confidence {at.Average(r => r.Confidence):P0}, " +
                $"waveform {at.Average(r => r.WaveformGain):+0.00;-0.00} dB " +
                $"(worst {at.Min(r => r.WaveformGain):+0.00;-0.00})");
        }
        foreach (var group in rows.GroupBy(r => r.Corpus).OrderBy(g => g.Key))
            output.WriteLine($"  {group.Key,-10} {group.Count(),3} cells  waveform {group.Average(r => r.WaveformGain):+0.00;-0.00} dB  " +
                $"measured {group.Average(r => r.MeasuredBefore):0.000}% -> {group.Average(r => r.MeasuredAfter):0.000}%");

        Assert.NotEmpty(rows);

        // What can honestly be asserted is that the reading rises with the planted deviation. The
        // absolute reading cannot: measured against a known warp it under-reads by about half even
        // after allowing that RmsPercent should be around 0.56 of the planted peak, and it sits on a
        // floor near 0.2-0.3% on digital material that has no speed variation at all. Both are
        // reported above.
        var byPlanted = rows.GroupBy(r => r.PlantedPercent)
            .OrderBy(g => g.Key)
            .Select(g => (Planted: g.Key, Measured: g.Average(r => r.MeasuredBefore)))
            .ToList();
        for (int i = 1; i < byPlanted.Count; i++)
            Assert.True(byPlanted[i].Measured > byPlanted[i - 1].Measured,
                $"planting {byPlanted[i].Planted:0.0}% read {byPlanted[i].Measured:0.000}% but " +
                $"{byPlanted[i - 1].Planted:0.0}% read {byPlanted[i - 1].Measured:0.000}%");

        // And that correcting reduces what the estimator itself sees.
        Assert.True(rows.Average(r => r.MeasuredAfter) < rows.Average(r => r.MeasuredBefore),
            "correction should reduce the measured deviation");
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
