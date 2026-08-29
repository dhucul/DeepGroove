using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The declip chain against real programme rather than a signal generator.
/// </summary>
/// <remarks>
/// <para>
/// Every earlier calibration of this chain was validated on synthetic material and every one was
/// wrong, most instructively the rule that scored 99.1 dB of shortfall on real audio after
/// cross-validating cleanly over thirty-one synthetic materials. The cause was a regime the
/// generator never produced: at light damage, synthetic materials have a median plateau of about
/// three samples and real programme has tens. So there is a test on a real recording, and it lives
/// in the suite rather than in a scratch probe.
/// </para>
/// <para>
/// <c>demo_track.wav</c> is the repository's own file, so this runs everywhere the rest of the
/// suite does. It is clipped synthetically because a repair can only be scored against a clean
/// reference, and rescaled so the plateau sits at the rail — a genuinely clipped recording ran out
/// of numbers, it is not merely quiet, and without the rescale
/// <see cref="ClippingAnalysisOptions.MinimumPeakLevel"/> skips the channel altogether.
/// </para>
/// </remarks>
public sealed class RealAudioDeclipTests(ITestOutputHelper output)
{
    private static string TrackPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "demo_track.wav"))) dir = dir.Parent;
        return dir == null ? "" : Path.Combine(dir.FullName, "demo_track.wav");
    }

    private static AudioDocument Track()
    {
        string path = TrackPath();
        Assert.True(File.Exists(path), "demo_track.wav is part of the repository and should be beside the solution.");
        return AudioImporter.Load(path);
    }

    private static double SnrDb(float[] clean, float[] candidate, bool[] mask)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            if (mask != null && !mask[i]) continue;
            double d = clean[i] - candidate[i];
            signal += (double)clean[i] * clean[i];
            error += d * d;
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    /// <summary>Clip one channel at a fraction of its own peak, with the plateau left at the rail.</summary>
    private static (float[] Clean, float[] Clipped, bool[] Mask) Damage(float[] source, double relative)
    {
        double peak = 0;
        foreach (float v in source) peak = Math.Max(peak, Math.Abs(v));
        double limit = peak * relative;
        double scale = 1.0 / limit;

        var clean = new float[source.Length];
        var clipped = new float[source.Length];
        var mask = new bool[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            clean[i] = (float)(source[i] * scale);
            float c = source[i];
            if (c > limit) { c = (float)limit; mask[i] = true; }
            else if (c < -limit) { c = (float)-limit; mask[i] = true; }
            clipped[i] = (float)(c * scale);
        }
        return (clean, clipped, mask);
    }

    /// <summary>
    /// The headline claim, and the one that has to hold on real audio however the chooser is
    /// calibrated: repairing must beat leaving the damage alone. Measured across the three external
    /// corpora named in <c>docs/validation-corpora.md</c> - 272 cells over 68 recordings - the chain
    /// gains a mean 9.60 dB and <b>no cell loses to leaving the damage alone</b>, the thinnest margin
    /// being +0.22 dB. The corpora are external, so this test carries the claim on the one recording
    /// that ships with the repository.
    /// </summary>
    [Theory]
    [InlineData(0.60)]
    [InlineData(0.40)]
    [InlineData(0.25)]
    [InlineData(0.15)]
    public void RepairingRealProgrammeBeatsLeavingItAlone(double relative)
    {
        var doc = Track();
        var (clean, clipped, mask) = Damage(doc.Channels[0], relative);

        // The workbench's own analysis options: nothing permissive.
        var analysis = Restoration.AnalyzeClipping([clipped], doc.SampleRate, new ClippingAnalysisOptions());
        Assert.NotEmpty(analysis.Events);

        var repaired = Restoration.RepairClipping([clipped], analysis.Events);
        double raw = SnrDb(clean, clipped, mask);
        double fixedUp = SnrDb(clean, repaired[0], mask);
        var choice = Assert.Single(Restoration.DescribeDeclipChoices([clipped], analysis.Events));
        output.WriteLine($"{relative:0.00} of peak: {choice.Method} · {choice.ClippedFraction * 100:0.#}% clipped · " +
            $"runs of {choice.MeanRunSamples:0} · raw {raw:0.0} dB -> {fixedUp:0.0} dB");

        Assert.True(fixedUp > raw,
            $"Repair scored {fixedUp:0.0} dB against {raw:0.0} dB for leaving the damage alone.");
    }

    /// <summary>
    /// Real programme has long plateaus even when barely clipped — a median of 57 samples at light
    /// damage against about three for the signal generator — and calibrating on the generator alone
    /// is what put the previous rule 99.1 dB out on real audio. This pins the property, so a future
    /// synthetic-only recalibration cannot quietly reintroduce the assumption.
    /// </summary>
    [Fact]
    public void RealProgrammeHasLongPlateausEvenWhenBarelyClipped()
    {
        var doc = Track();
        var (_, clipped, damaged) = Damage(doc.Channels[0], 0.60);
        var analysis = Restoration.AnalyzeClipping([clipped], doc.SampleRate, new ClippingAnalysisOptions());
        var choice = Assert.Single(Restoration.DescribeDeclipChoices([clipped], analysis.Events));

        output.WriteLine($"{choice.ClippedFraction * 100:0.##}% clipped, mean run {choice.MeanRunSamples:0.0} samples");
        output.WriteLine($"damage mask {damaged.Count(value => value) * 100.0 / damaged.Length:0.###}% · " +
                         string.Join(", ", analysis.Events.GroupBy(item => Math.Round(item.AbsoluteClipLevel, 4))
                             .Select(group => $"{group.Key:0.0000}:{group.Count()}")));
        Assert.True(choice.ClippedFraction < 0.015, "This should be light damage.");
        Assert.True(choice.MeanRunSamples > 10,
            $"Real programme clipped this lightly still ran {choice.MeanRunSamples:0} samples per plateau; " +
            "a rule calibrated where that number is three does not apply here.");
    }

    /// <summary>Undamaged real programme must not be reported as clipped.</summary>
    [Fact]
    public void UndamagedRealProgrammeFindsNoClipping()
    {
        var doc = Track();
        var analysis = Restoration.AnalyzeClipping(doc.Channels, doc.SampleRate, new ClippingAnalysisOptions());
        output.WriteLine($"{analysis.Events.Count} events in the untouched file");
        Assert.Empty(analysis.Events);
    }
}

/// <summary>
/// The rule that a second corpus overturned, kept as a test so it cannot be re-derived by accident.
/// </summary>
/// <remarks>
/// A short-plateau exception was fitted on one real corpus, transferred to synthetic
/// material it was never fitted to, and cross-validated cleanly — then cost 668.7 dB on 152 cells
/// of a second real corpus. Refitting across all three datasets selects no exception in 87 of 88
/// folds. These assertions pin its absence, and the reasoning belongs with them: both real corpora
/// contain short plateaus at modest damage, the arch wins them in one and A-SPADE in the other, and
/// nothing measured separates the two.
/// </remarks>
public sealed class NoShortPlateauExceptionTests(ITestOutputHelper output)
{
    [Fact]
    public void ShortPlateausFollowTheCurveLikeEverythingElse()
    {
        // The cells the removed exception used to divert to the arch.
        Assert.True(DeclipMethodChooser.PrefersSparse(0.0188, meanRunSamples: 5.1));
        Assert.True(DeclipMethodChooser.PrefersSparse(0.00034, meanRunSamples: 6.5));
        Assert.True(DeclipMethodChooser.PrefersSparse(0.00945, meanRunSamples: 5.8));
        output.WriteLine("short plateaus follow the curve; the fitted exception did not survive a second corpus");
    }

    [Fact]
    public void TheCurveStillTurnsOverAtBothEnds()
    {
        double tiny = DeclipMethodChooser.ToleratedClippedFraction(2);
        double mid = DeclipMethodChooser.ToleratedClippedFraction(15);
        double huge = DeclipMethodChooser.ToleratedClippedFraction(150);
        output.WriteLine($"runs of 2 -> {tiny:0.00}, 15 -> {mid:0.00}, 150 -> {huge:0.00}");
        Assert.True(mid > tiny + 0.3);
        Assert.True(mid > huge + 0.3);
    }

    [Fact]
    public void LongPlateauProgrammeIsUnaffected()
    {
        Assert.True(DeclipMethodChooser.PrefersSparse(0.009, meanRunSamples: 65));
        Assert.False(DeclipMethodChooser.PrefersSparse(0.30, meanRunSamples: 145));
    }
}
