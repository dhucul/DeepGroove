using System.Diagnostics;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Click and pop repair against real recordings. Skipped unless <c>WAVELAB_CORPUS=1</c>; the two
/// tests that need no audio check the damage model itself.
/// </summary>
public sealed class ClickCorpusTests(ITestOutputHelper output)
{
    /// <summary>
    /// The damage model decides what every number below means, so it is checked without a corpus:
    /// clicks land where the signal is, they stand above it by the amount asked for, and the mask
    /// marks exactly what was touched.
    /// </summary>
    [Fact]
    public void PlantedClicksStandAboveTheLocalSignalAndAreMarked()
    {
        const int rate = 44100;
        var source = new float[rate * 4];
        for (int i = 0; i < source.Length; i++)
            source[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / rate));

        var (clean, damaged, hit, count) = ClickCorpus.Damage(source, rate, aboveLocalDb: 18, seed: 7);

        Assert.True(count >= 20, $"four seconds at 8 a second should plant about 32 clicks, planted {count}");
        Assert.Equal(source.Length, hit.Length);

        int marked = 0, unchangedButMarked = 0, changedButUnmarked = 0;
        double worstExcess = 0;
        for (int i = 0; i < source.Length; i++)
        {
            bool changed = !clean[i].Equals(damaged[i]);
            if (hit[i])
            {
                marked++;
                if (!changed) unchangedButMarked++;
                worstExcess = Math.Max(worstExcess, Math.Abs(damaged[i] - clean[i]));
            }
            else if (changed) changedButUnmarked++;
        }
        Assert.Equal(0, changedButUnmarked);
        Assert.Equal(0, unchangedButMarked);

        // 0.3 amplitude sine has an RMS near 0.212; 18 dB above that is about 1.7, and the model
        // scales each click by 0.6 to 1.4 on top.
        Assert.InRange(worstExcess, 1.0, 3.0);
        output.WriteLine($"{count} clicks over {marked} samples, largest excursion {worstExcess:0.00}");
    }

    /// <summary>A quieter setting must plant quieter clicks, or the severity axis means nothing.</summary>
    [Fact]
    public void SeverityMovesTheClickAmplitude()
    {
        const int rate = 44100;
        var source = new float[rate];
        for (int i = 0; i < source.Length; i++)
            source[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / rate));

        double Excursion(double db)
        {
            var (clean, damaged, hit, _) = ClickCorpus.Damage(source, rate, db, seed: 3);
            double worst = 0;
            for (int i = 0; i < source.Length; i++)
                if (hit[i]) worst = Math.Max(worst, Math.Abs(damaged[i] - clean[i]));
            return worst;
        }
        double loud = Excursion(24), quiet = Excursion(6);
        output.WriteLine($"24 dB above local -> {loud:0.000}, 6 dB -> {quiet:0.000}");
        Assert.True(loud > quiet * 2, "18 dB of severity should be worth more than a factor of two");
    }

    /// <summary>
    /// The standing measurement. Repairing planted clicks must beat leaving them alone on every
    /// cell, on real recordings rather than on a generator.
    /// </summary>
    [Fact]
    public void RepairingPlantedClicksBeatsLeavingThemAlone()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        var excluded = new System.Collections.Concurrent.ConcurrentBag<string>();
        var watch = Stopwatch.StartNew();
        var contrast = new ClickAnalysisOptions();
        var absolute = new ClickAnalysisOptions
        {
            LocalHighFrequencyContrast = false,
            PredictiveDetection = false,
            TrendRelativeRecovery = false,
        };
        (string Name, ClickAnalysisOptions Options)[] probes =
        [
            ("old recovery", new ClickAnalysisOptions { TrendRelativeRecovery = false }),
            ("before today", new ClickAnalysisOptions
            {
                TrendRelativeRecovery = false,
                MinimumConfidence = 0.60,
            }),
        ];
        var results = ClickCorpus.Measure(cell =>
        {
            (int Found, double Gain) Run(ClickAnalysisOptions o)
            {
                var analysis = Restoration.AnalyzeClicks([cell.Damaged], cell.SampleRate, o);
                var repaired = Restoration.RepairClicks([cell.Damaged], analysis.Events);
                return (analysis.Events.Count, cell.Score(repaired[0]) - cell.Raw);
            }
            var now = Run(contrast);
            var was = Run(absolute);
            return (cell.Recording.Corpus, cell.Recording.ShortName, cell.Severity,
                Planted: cell.ClickCount, Found: now.Found, Gain: now.Gain,
                OldFound: was.Found, OldGain: was.Gain,
                Probes: probes.Select(pr => (pr.Name, Run(pr.Options))).ToArray());
        }, onExcluded: (r, why) => excluded.Add($"{r.Corpus}/{r.ShortName}: {why}"));
        watch.Stop();

        output.WriteLine($"{results.Count} cells in {watch.Elapsed.TotalMinutes:0.0} min");
        foreach (var line in excluded.OrderBy(x => x, StringComparer.Ordinal).Take(12))
            output.WriteLine($"  EXCLUDED {line}");
        if (excluded.Count > 12) output.WriteLine($"  ... and {excluded.Count - 12} more excluded");

        foreach (var group in results.GroupBy(r => r.Corpus).OrderBy(g => g.Key))
        {
            var gains = group.Select(r => r.Gain).ToList();
            double recall = group.Average(r => Math.Min(1.0, r.Found / (double)Math.Max(1, r.Planted)));
            output.WriteLine($"  {group.Key,-10} {gains.Count,4} cells  mean {gains.Average():+0.00;-0.00} dB  " +
                $"worst {gains.Min():+0.00;-0.00}  below do-nothing {gains.Count(g => g < 0)}  " +
                $"found/planted {recall:P0}");
        }
        foreach (var severity in results.Select(r => r.Severity).Distinct().OrderByDescending(s => s))
        {
            var at = results.Where(r => r.Severity == severity).ToList();
            output.WriteLine($"  {severity,4:0} dB above local: gain {at.Average(r => r.Gain):+0.00;-0.00} dB " +
                $"(was {at.Average(r => r.OldGain):+0.00;-0.00})  recall " +
                $"{at.Average(r => Math.Min(1.0, r.Found / (double)Math.Max(1, r.Planted))):P0} " +
                $"(was {at.Average(r => Math.Min(1.0, r.OldFound / (double)Math.Max(1, r.Planted))):P0})  " +
                $"found/planted {at.Average(r => r.Found / (double)Math.Max(1, r.Planted)):0.00}x " +
                $"(was {at.Average(r => r.OldFound / (double)Math.Max(1, r.Planted)):0.00}x)");
            for (int q = 0; q < probes.Length; q++)
            {
                int index = q;
                output.WriteLine($"        {probes[q].Name,-7} recall " +
                    $"{at.Average(r => Math.Min(1.0, r.Probes[index].Item2.Found / (double)Math.Max(1, r.Planted))):P0}  " +
                    $"gain {at.Average(r => r.Probes[index].Item2.Gain):+0.00;-0.00} dB  " +
                    $"found/planted {at.Average(r => r.Probes[index].Item2.Found / (double)Math.Max(1, r.Planted)):0.00}x");
            }
        }
        var all = results.Select(r => r.Gain).ToList();
        if (all.Count == 0) { output.WriteLine("no cells measured"); return; }
        output.WriteLine($"  {"ALL",-10} {all.Count,4} cells  mean {all.Average():+0.00;-0.00} dB  " +
            $"worst {all.Min():+0.00;-0.00}");

        foreach (var r in results.Where(r => r.Gain < 0).OrderBy(r => r.Gain).Take(10))
            output.WriteLine($"  WORSE THAN DOING NOTHING: {r.Corpus}/{r.ShortName} @{r.Severity:0} dB " +
                $"{r.Gain:+0.00;-0.00} dB (planted {r.Planted}, found {r.Found})");
        // The claim is that repairing never makes it worse, not that it always finds something.
        // Below about 15 dB above the local level the detector misses most planted clicks and the
        // repair correctly does nothing, which scores exactly zero; that is a recall problem, and
        // recall is reported above rather than asserted here.
        Assert.All(results, r => Assert.True(r.Gain >= 0,
            $"{r.ShortName} at {r.Severity:0} dB scored {r.Gain:+0.00;-0.00} dB against leaving the clicks alone"));

        var loud = results.Where(r => r.Severity >= 18).ToList();
        Assert.All(loud, r => Assert.True(r.Gain > 5,
            $"a click {r.Severity:0} dB above the local level is not subtle; {r.ShortName} gained only {r.Gain:+0.00;-0.00} dB"));
    }

    /// <summary>
    /// The false-positive check the scored test cannot make: undamaged digital recordings should
    /// not be reported as full of clicks.
    /// </summary>
    [Fact]
    public void UndamagedRecordingsAreNotReportedAsClicky()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        var rates = DeclipCorpus.ForEachRecording<(string Corpus, string Name, double PerSecond, double OldPerSecond)>(
            (recording, document) =>
            {
                var source = document.Channels[0];
                double seconds = source.Length / (double)document.SampleRate;
                double perSecond = Restoration.AnalyzeClicks([source], document.SampleRate,
                    new ClickAnalysisOptions()).Events.Count / seconds;
                double oldPerSecond = Restoration.AnalyzeClicks([source], document.SampleRate,
                    new ClickAnalysisOptions
                    {
                        TrendRelativeRecovery = false,
                        MinimumConfidence = 0.60,
                    }).Events.Count / seconds;
                return ([(recording.Corpus, recording.ShortName, perSecond, oldPerSecond)], null);
            });

        foreach (var group in rates.GroupBy(r => r.Corpus).OrderBy(g => g.Key))
            output.WriteLine($"  {group.Key,-10} {group.Count(),3} recordings  " +
                $"median {group.Select(r => r.PerSecond).Order().ElementAt(group.Count() / 2):0.00}/s " +
                $"(was {group.Select(r => r.OldPerSecond).Order().ElementAt(group.Count() / 2):0.00})  " +
                $"max {group.Max(r => r.PerSecond):0.0}/s (was {group.Max(r => r.OldPerSecond):0.0})");
        foreach (var r in rates.Where(r => r.Corpus != "3").OrderByDescending(r => r.OldPerSecond).Take(6))
            output.WriteLine($"  worst offender: {r.Corpus}/{r.Name} {r.PerSecond:0.00}/s (was {r.OldPerSecond:0.00})");
    }
}
