using System.Collections.Concurrent;
using System.Diagnostics;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The click detector measured against real defects rather than invented ones.
/// </summary>
/// <remarks>
/// Every click number so far rests on damped impulses of my own design, so it measures the detector
/// against a guess about what a stylus does. These plant shapes lifted off real shellac transfers
/// instead: the morphology is no longer a guess, while the positions stay known and the reference
/// stays clean. What it still cannot answer is whether the detector's rate on a real transfer is
/// right — that needs a recording with its clicks marked, and marking them needs ears.
/// </remarks>
public sealed class RealClickTests(ITestOutputHelper output)
{
    private static List<RealClick>? cached;
    private static readonly object Gate = new();

    private static List<RealClick> Library()
    {
        lock (Gate) return cached ??= RealClickLibrary.Build();
    }

    [Fact]
    public void TheLibraryIsBuiltFromRealTransfersAndIsVaried()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }

        var library = Library();
        if (library.Count == 0) { output.WriteLine("no shellac corpus present"); return; }

        var lengths = library.Select(c => c.Length).OrderBy(v => v).ToList();
        var sharpness = library.Select(c => c.Sharpness).OrderBy(v => v).ToList();
        output.WriteLine($"{library.Count} clicks from {library.Select(c => c.Source).Distinct().Count()} transfers");
        output.WriteLine($"  length   min {lengths[0]}  median {lengths[lengths.Count / 2]}  max {lengths[^1]} samples");
        output.WriteLine($"  sharpness min {sharpness[0]:0.00}  median {sharpness[sharpness.Count / 2]:0.00}  " +
            $"max {sharpness[^1]:0.00}");
        foreach (var group in library.GroupBy(c => c.Source).OrderByDescending(g => g.Count()).Take(4))
            output.WriteLine($"  {group.Count(),3} from {group.Key[..Math.Min(46, group.Key.Length)]}");

        Assert.True(library.Count >= 50, $"only {library.Count} clicks extracted");
        // A library of one shape would be a library of one click repeated.
        Assert.True(lengths[^1] > lengths[0], "every extracted click is the same length");
        Assert.All(library, c => Assert.True(c.Shape.Max(Math.Abs) > 0.99f,
            "shapes are normalised so the largest excursion is 1"));
    }

    /// <summary>
    /// The comparison that matters: does the detector do as well on real defects as on the invented
    /// ones every other click number here rests on?
    /// </summary>
    [Fact]
    public void RealClicksAreFoundAsWellAsInventedOnes()
    {
        if (!DeclipCorpus.Enabled) { output.WriteLine("skipped: set WAVELAB_CORPUS=1"); return; }
        var library = Library();
        if (library.Count == 0) { output.WriteLine("no shellac corpus present"); return; }

        var excluded = new ConcurrentBag<string>();
        var watch = Stopwatch.StartNew();
        var rows = DeclipCorpus.ForEachRecording<(string Corpus, string Name, double Severity,
            int Planted, int Found, double Gain, int SyntheticFound, double SyntheticGain)>(
            (recording, document) =>
            {
                if (recording.Corpus == "3")
                    return (null, "the clicks came from here; planting them back would be circular");
                var source = document.Channels[0];
                double seconds = source.Length / (double)document.SampleRate;
                var already = Restoration.AnalyzeClicks([source], document.SampleRate, new ClickAnalysisOptions());
                if (already.Events.Count / seconds > 1.0)
                    return (null, $"already clicky: {already.Events.Count / seconds:0.0}/s before any damage");

                var results = new List<(string, string, double, int, int, double, int, double)>();
                foreach (double severity in ClickCorpus.Severities)
                {
                    int seed = recording.Path.GetHashCode(StringComparison.Ordinal) ^ (int)(severity * 64);

                    var real = RealClickLibrary.Plant(source, document.SampleRate, library, severity, seed);
                    if (real.Count < 8) continue;
                    var synthetic = ClickCorpus.Damage(source, document.SampleRate, severity, seed);

                    (int Found, double Gain) Score(float[] clean, float[] damaged, bool[] hit)
                    {
                        var analysis = Restoration.AnalyzeClicks([damaged], document.SampleRate,
                            new ClickAnalysisOptions());
                        var repaired = Restoration.RepairClicks([damaged], analysis.Events);
                        double raw = DeclipCorpus.SnrDb(clean, damaged, hit);
                        return (analysis.Events.Count, DeclipCorpus.SnrDb(clean, repaired[0], hit) - raw);
                    }

                    var onReal = Score(real.Clean, real.Damaged, real.Hit);
                    var onSynthetic = Score(synthetic.Clean, synthetic.Damaged, synthetic.Hit);
                    results.Add((recording.Corpus, recording.ShortName, severity, real.Count,
                        onReal.Found, onReal.Gain, onSynthetic.Found, onSynthetic.Gain));
                }
                return (results, null);
            }, onExcluded: (r, why) => excluded.Add($"{r.Corpus}/{r.ShortName}: {why}"));
        watch.Stop();

        output.WriteLine($"{rows.Count} cells in {watch.Elapsed.TotalMinutes:0.0} min, " +
            $"{excluded.Count} recordings excluded, {library.Count} real shapes");
        if (rows.Count == 0) { output.WriteLine("nothing measurable"); return; }

        foreach (double severity in rows.Select(r => r.Severity).Distinct().OrderByDescending(s => s))
        {
            var at = rows.Where(r => r.Severity == severity).ToList();
            output.WriteLine($"  {severity,4:0} dB above local: " +
                $"real recall {at.Average(r => Math.Min(1.0, r.Found / (double)Math.Max(1, r.Planted))):P0} " +
                $"gain {at.Average(r => r.Gain):+0.00;-0.00} dB   |   " +
                $"invented recall {at.Average(r => Math.Min(1.0, r.SyntheticFound / (double)Math.Max(1, r.Planted))):P0} " +
                $"gain {at.Average(r => r.SyntheticGain):+0.00;-0.00} dB");
        }
        foreach (var group in rows.GroupBy(r => r.Corpus).OrderBy(g => g.Key))
            output.WriteLine($"  {group.Key,-10} {group.Count(),3} cells  real {group.Average(r => r.Gain):+0.00;-0.00} dB  " +
                $"invented {group.Average(r => r.SyntheticGain):+0.00;-0.00} dB");

        Assert.All(rows, r => Assert.True(r.Gain >= 0,
            $"{r.Name} at {r.Severity:0} dB scored {r.Gain:+0.00;-0.00} dB on real clicks"));
    }
}
