using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Attenuating a selection, both by a fixed amount and down to what surrounds it. The second is the
/// reason both actions exist, so most of this is about telling them apart.
/// </summary>
public sealed class SpectralAttenuateTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 132_300;      // 3 s
    private const int Fft = 2048, Hop = 512;

    private static SpectralRepairOptions Options => SpectralRepairOptions.Default;

    /// <summary>
    /// A steady 1 kHz tone at <paramref name="toneLevel"/>, with a burst added on top of it over the
    /// middle second. Attenuating should remove what the burst added and leave the tone.
    /// </summary>
    private static (float[] Damaged, float[] Clean) WithBurst(double toneLevel, double burstLevel)
    {
        var clean = new float[Length];
        var damaged = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)Rate;
            var tone = (float)(toneLevel * Math.Sin(2 * Math.PI * 1_000 * t));
            clean[i] = tone;
            damaged[i] = tone;
            if (i >= 44_100 && i < 88_200)
            {
                double envelope = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - 44_100) / 44_100.0);
                damaged[i] += (float)(burstLevel * envelope * Math.Sin(2 * Math.PI * 1_000 * t + 1.1));
            }
        }
        return (damaged, clean);
    }

    private static SpectralMask Mask() =>
        SpectralMask.ForRegion(44_100, 88_200, 850, 1_150, Rate, Fft, Hop);

    private static float[] Applied(float[] source, SpectralRepairResult result)
    {
        var merged = (float[])source.Clone();
        result.Samples.CopyTo(merged.AsSpan(result.Start));
        return merged;
    }

    /// <summary>RMS over the middle of the burst, where the selection is at full weight.</summary>
    private static double Level(float[] signal)
    {
        double total = 0;
        const int from = 60_000, to = 72_000;
        for (int i = from; i < to; i++) total += (double)signal[i] * signal[i];
        return Math.Sqrt(total / (to - from));
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-12));

    // ── matching the surroundings ────────────────────────────────

    /// <summary>
    /// The headline difference. A fixed reduction takes the music down with the defect by the same
    /// amount everywhere, so it can only be right at one programme level; reducing to what the bin
    /// carried either side of the selection removes what was added and leaves what was there.
    /// </summary>
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.20)]
    [InlineData(0.50)]
    public void MatchingTheSurroundingsLandsOnTheProgrammeLevelWhateverItIs(double toneLevel)
    {
        var (damaged, clean) = WithBurst(toneLevel, burstLevel: 0.35);

        SpectralRepairResult result = SpectralRepair.AttenuateToSurroundings(
            damaged, 0, Mask(), maximumReductionDb: 60, Options);
        float[] applied = Applied(damaged, result);

        double target = Level(clean), before = Level(damaged), after = Level(applied);
        output.WriteLine($"tone {Db(target):0.0} dB: damaged {Db(before):0.0} -> attenuated {Db(after):0.0}");

        Assert.True(after < before * 0.8, "the burst was not reduced");
        Assert.InRange(Db(after) - Db(target), -6, 4);
    }

    /// <summary>
    /// The same fixed reduction across two programme levels: it cannot be right at both, which is
    /// what the other mode exists to fix.
    /// </summary>
    [Fact]
    public void AFixedReductionCannotSuitTwoProgrammeLevels()
    {
        double[] errors = new double[2];
        double[] fixedErrors = new double[2];
        double[] levels = [0.05, 0.50];

        for (int i = 0; i < levels.Length; i++)
        {
            var (damaged, clean) = WithBurst(levels[i], burstLevel: 0.35);
            double target = Level(clean);

            SpectralRepairResult matched = SpectralRepair.AttenuateToSurroundings(
                damaged, 0, Mask(), 60, Options);
            SpectralRepairResult flat = SpectralRepair.Attenuate(damaged, 0, Mask(), -18, Options);

            errors[i] = Db(Level(Applied(damaged, matched))) - Db(target);
            fixedErrors[i] = Db(Level(Applied(damaged, flat))) - Db(target);
            output.WriteLine($"tone {Db(target):0.0} dB: matched {errors[i]:+0.0;−0.0}, " +
                             $"fixed −18 dB {fixedErrors[i]:+0.0;−0.0}");
        }

        double matchedSpread = Math.Abs(errors[0] - errors[1]);
        double fixedSpread = Math.Abs(fixedErrors[0] - fixedErrors[1]);
        output.WriteLine($"spread across levels: matched {matchedSpread:0.0} dB, fixed {fixedSpread:0.0} dB");
        Assert.True(matchedSpread < fixedSpread,
            "matching the surroundings should track the programme where a fixed reduction cannot");
    }

    [Fact]
    public void TheLimitStopsItGoingFurtherThanAsked()
    {
        var (damaged, _) = WithBurst(toneLevel: 0.02, burstLevel: 0.60);

        SpectralRepairResult limited = SpectralRepair.AttenuateToSurroundings(
            damaged, 0, Mask(), maximumReductionDb: 6, Options);
        SpectralRepairResult free = SpectralRepair.AttenuateToSurroundings(
            damaged, 0, Mask(), maximumReductionDb: 60, Options);

        double before = Level(damaged);
        double stopped = Level(Applied(damaged, limited));
        double full = Level(Applied(damaged, free));

        output.WriteLine($"limit 6 dB: {Db(stopped) - Db(before):0.0} dB, " +
                         $"limit 60 dB: {Db(full) - Db(before):0.0} dB");

        Assert.True(Db(before) - Db(stopped) <= 6.5, "the limit was exceeded");
        Assert.True(Db(before) - Db(full) > Db(before) - Db(stopped), "the limit did nothing");
    }

    /// <summary>It attenuates; it must never turn anything up.</summary>
    [Fact]
    public void MatchingTheSurroundingsNeverBoosts()
    {
        // A dip rather than a burst: the selected span is quieter than what surrounds it.
        var quiet = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)Rate;
            double level = i >= 44_100 && i < 88_200 ? 0.05 : 0.40;
            quiet[i] = (float)(level * Math.Sin(2 * Math.PI * 1_000 * t));
        }

        SpectralRepairResult result = SpectralRepair.AttenuateToSurroundings(quiet, 0, Mask(), 60, Options);

        double before = Level(quiet), after = Level(Applied(quiet, result));
        output.WriteLine($"{Db(before):0.0} dB -> {Db(after):0.0} dB");
        Assert.True(after <= before * 1.02, "a quiet selection was turned up to match its surroundings");
    }

    /// <summary>
    /// With nothing observed either side there is no surrounding level to reduce to, and inventing
    /// one would be a fixed reduction wearing a different name.
    /// </summary>
    [Fact]
    public void ASelectionCoveringEverythingIsLeftAlone()
    {
        var (damaged, _) = WithBurst(0.2, 0.35);
        SpectralMask whole = SpectralMask.ForRegion(0, Length, 20, 20_000, Rate, Fft, Hop);

        SpectralRepairResult result = SpectralRepair.AttenuateToSurroundings(damaged, 0, whole, 60, Options);

        double worst = 0;
        for (int i = 0; i < result.Samples.Length; i++)
            worst = Math.Max(worst, Math.Abs(result.Samples[i] - damaged[result.Start + i]));

        output.WriteLine($"worst change {worst:0.000e+0}");
        Assert.True(worst < 1e-4, $"audio with no surroundings to match was changed by {worst}");
    }

    // ── fixed gain ───────────────────────────────────────────────

    [Theory]
    [InlineData(-12)]
    [InlineData(-3)]
    [InlineData(3)]
    [InlineData(9)]
    public void AFixedGainMovesTheSelectionByTheAmountAsked(double gainDb)
    {
        var (damaged, _) = WithBurst(0.2, 0.35);

        SpectralRepairResult result = SpectralRepair.Attenuate(damaged, 0, Mask(), gainDb, Options);
        double measured = Db(Level(Applied(damaged, result))) - Db(Level(damaged));

        output.WriteLine($"asked {gainDb:+0;−0} dB, measured {measured:+0.0;−0.0} dB");
        Assert.InRange(measured, gainDb - 2, gainDb + 2);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var (damaged, _) = WithBurst(0.2, 0.35);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SpectralRepair.AttenuateToSurroundings(damaged, 0, Mask(), 24, Options, cancellation.Token));
    }

    [Fact]
    public void AnEmptyMaskIsANoOp()
    {
        var (damaged, _) = WithBurst(0.2, 0.35);
        Assert.True(SpectralRepair.AttenuateToSurroundings(
            damaged, 0, SpectralMask.Rectangle(10, 10, 20, 40), 24, Options).IsEmpty);
    }
}
