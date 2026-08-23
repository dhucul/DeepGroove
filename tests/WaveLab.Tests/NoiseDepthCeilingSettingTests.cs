using WaveLab.Audio.Dsp;
using WaveLab.Util;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The depth ceiling as a setting rather than a constant.
/// </summary>
/// <remarks>
/// <para>
/// The rule it feeds is measured and the default is its measured optimum, so what these tests
/// mostly protect is that <b>nothing moved for anyone who does not change it</b>. The one that
/// earns its keep on its own is <see cref="NothingToMeasureAsksForNothingAtEveryCeiling"/>: the
/// estimator answers "no reading" by returning the ceiling, so making the ceiling adjustable
/// without telling the estimator turns silence into a request for heavy reduction.
/// </para>
/// <para>
/// It exists because a record's surface crackle is impulsive and the estimate behind the rule is an
/// RMS ratio, so a plainly audible crackle can measure 24 dB under the programme and the reducer
/// declines on exactly the material a user is most sure needs help.
/// </para>
/// </remarks>
public sealed class NoiseDepthCeilingSettingTests(ITestOutputHelper output)
{
    /// <summary>The shipped default keeps its measurement: same answers, to the bit.</summary>
    [Theory]
    [InlineData(24.2, 12.0)]     // the record this was built for: declines
    [InlineData(15.9, 12.0)]     // hiss 30 dB down: declines
    [InlineData(9.5, 12.0)]
    [InlineData(2.7, 12.0)]
    [InlineData(0.0, 12.0)]
    public void TheDefaultCeilingIsTheSameRuleItAlwaysWas(double estimate, double requested)
    {
        double withDefault = Restoration.SuggestReductionDepthDb(estimate, requested);
        double explicitly = Restoration.SuggestReductionDepthDb(
            estimate, requested, Restoration.NoiseDepthCeilingDb);

        Assert.Equal(withDefault, explicitly);
    }

    /// <summary>
    /// Nothing to measure must ask for nothing whatever the ceiling is set to.
    /// </summary>
    /// <remarks>
    /// <b>This is the trap the setting introduces.</b> The estimator says "no reading" by returning
    /// the ceiling, so a fixed 10 handed to a rule running at 30 stops meaning "nothing to remove"
    /// and starts meaning two thirds of the requested depth — an empty buffer would then ask for
    /// 6.7 dB of reduction. That is the same defect the estimator's own remarks record being fixed
    /// once already, reintroduced by a different route.
    /// </remarks>
    [Theory]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(30.0)]
    [InlineData(40.0)]
    public void NothingToMeasureAsksForNothingAtEveryCeiling(double ceiling)
    {
        Assert.Equal(0, Restoration.SuggestReductionDepthDb([new float[1000]], 44_100, 12, ceiling));
        Assert.Equal(0, Restoration.SuggestReductionDepthDb([], 44_100, 12, ceiling));
        Assert.Equal(0, Restoration.SuggestReductionDepthDb([new float[1000]], 0, 12, ceiling));

        // And the estimator's sentinel is expressed in the ceiling actually in force.
        Assert.Equal(ceiling, Restoration.EstimateNoiseToProgrammeDb([new float[1000]], 44_100, ceiling));
    }

    /// <summary>
    /// Raising it unblocks a quiet-floored file, which is the whole reason it is adjustable.
    /// </summary>
    [Fact]
    public void RaisingTheCeilingUnblocksAQuietFlooredFile()
    {
        const double estimate = 24.2;      // measured on One More Chance
        const double requested = 12.0;

        double atDefault = Restoration.SuggestReductionDepthDb(estimate, requested);
        double at30 = Restoration.SuggestReductionDepthDb(estimate, requested, 30);
        double at40 = Restoration.SuggestReductionDepthDb(estimate, requested, 40);

        output.WriteLine($"estimate {estimate} dB, requested {requested} dB: " +
                         $"default {atDefault:0.00}, ceiling 30 {at30:0.00}, ceiling 40 {at40:0.00}");

        Assert.Equal(0, atDefault);
        Assert.True(at30 > 0);
        Assert.True(at40 > at30);
    }

    /// <summary>
    /// And the cost is stated rather than hidden: the same move reduces a file the default was
    /// protecting. This asserts the trade exists, so nobody can later describe the setting as free.
    /// </summary>
    [Fact]
    public void RaisingTheCeilingAlsoReducesFilesTheDefaultProtected()
    {
        // Hiss already 30 dB under the programme, where a fixed depth measured −8.13 dB segmental.
        const double protectedEstimate = 15.9;

        double atDefault = Restoration.SuggestReductionDepthDb(protectedEstimate, 12.0);
        double at30 = Restoration.SuggestReductionDepthDb(protectedEstimate, 12.0, 30);

        output.WriteLine($"a file at {protectedEstimate} dB: default {atDefault:0.00} dB, " +
                         $"ceiling 30 {at30:0.00} dB");

        Assert.Equal(0, atDefault);
        Assert.True(at30 > 5, "raising the ceiling should visibly reduce a file the default spared");
    }

    /// <summary>Both overloads must still agree, at a custom ceiling as at the default.</summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(24.0)]
    [InlineData(40.0)]
    public void BothOverloadsAgreeAtAnyCeiling(double ceiling)
    {
        const int rate = 44_100;
        var channel = new float[rate * 3];
        var random = new Random(17);
        for (int i = 0; i < channel.Length; i++)
        {
            double t = i / (double)rate;
            double envelope = i < rate ? 0.02 : 1.0;         // a quiet passage to find
            channel[i] = (float)(envelope * (0.25 * Math.Sin(2 * Math.PI * 220 * t)
                                             + (random.NextDouble() - 0.5) * 0.01));
        }
        float[][] data = [channel];

        double measured = Restoration.SuggestReductionDepthDb(data, rate, 12.0, ceiling);
        double fromEstimate = Restoration.SuggestReductionDepthDb(
            Restoration.EstimateNoiseToProgrammeDb(data, rate, ceiling), 12.0, ceiling);

        Assert.Equal(fromEstimate, measured, 12);
    }

    /// <summary>An out-of-range or hand-edited value is corrected rather than obeyed.</summary>
    [Theory]
    [InlineData(30.0, 30.0)]
    [InlineData(10.0, 10.0)]
    [InlineData(4.0, 10.0)]          // under the measured optimum: clamped back up
    [InlineData(120.0, 40.0)]        // past where scaling does anything: clamped down
    [InlineData(23.4, 23.0)]         // snapped to the slider's step
    [InlineData(double.NaN, 10.0)]
    [InlineData(double.PositiveInfinity, 10.0)]
    public void AHandEditedCeilingIsCorrected(double stored, double expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeNoiseDepthCeilingDb(stored));
    }

    /// <summary>
    /// A value outside the bounds cannot reach the rule even if it gets past the settings layer —
    /// the DSP clamps for itself rather than trusting its caller.
    /// </summary>
    [Fact]
    public void TheRuleClampsItsOwnCeiling()
    {
        double absurd = Restoration.SuggestReductionDepthDb(15.9, 12.0, 1_000_000);
        double bounded = Restoration.SuggestReductionDepthDb(
            15.9, 12.0, Restoration.MaximumNoiseDepthCeilingDb);
        Assert.Equal(bounded, absurd);

        double negative = Restoration.SuggestReductionDepthDb(15.9, 12.0, -50);
        double atMinimum = Restoration.SuggestReductionDepthDb(
            15.9, 12.0, Restoration.MinimumNoiseDepthCeilingDb);
        Assert.Equal(atMinimum, negative);

        Assert.Equal(atMinimum, Restoration.SuggestReductionDepthDb(15.9, 12.0, double.NaN));
    }

    /// <summary>The default the setting ships at is the constant the measurement was taken on.</summary>
    [Fact]
    public void TheSettingDefaultsToTheMeasuredOptimum()
    {
        Assert.Equal(Restoration.NoiseDepthCeilingDb, new AppSettings().NoiseDepthCeilingDb);
        Assert.Equal(Restoration.NoiseDepthCeilingDb, Restoration.MinimumNoiseDepthCeilingDb);
    }
}
