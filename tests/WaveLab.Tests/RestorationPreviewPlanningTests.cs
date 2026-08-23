using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The bounded lead-in a restoration preview runs before the audible range.
/// </summary>
/// <remarks>
/// This had no test at all until the subsonic high-pass needed a term in it, which is worth
/// stating: every stage added to the workbench either has state or has a grid, and the way that
/// shows up is a preview that disagrees with the render it is previewing — a thump at the boundary,
/// or a different answer about what is a defect. Neither reads as a lead-in fault.
/// </remarks>
public sealed class RestorationPreviewPlanningTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    /// <summary>Nothing stateful is on, so there is nothing to warm and no reason to copy more.</summary>
    [Fact]
    public void WithNoStatefulStageThereIsNoLeadIn()
    {
        var plan = RestorationPreviewPlanning.Create(Rate * 30, Rate,
            removeHum: false, humFrequency: 50, humQ: 35, reduceNoise: false);

        Assert.Equal(0, plan.WarmupSamples);
        Assert.Equal(Rate * 30, plan.StartSample);
    }

    /// <summary>
    /// The high-pass needs more lead-in than the flat fallback pad ever gave it, and that is the
    /// gap this term closes: with hum and noise both off, the preview used to fall back to
    /// <c>max(NrFftSize * 2, rate / 10)</c> — 4,410 samples at 44.1 kHz.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(30.0)]
    [InlineData(60.0)]
    public void TheHighPassAsksForMoreLeadInThanTheOldFlatPad(double cutoffHz)
    {
        var plan = RestorationPreviewPlanning.Create(Rate * 30, Rate,
            removeHum: false, humFrequency: 50, humQ: 35, reduceNoise: false,
            removeSubsonic: true, subsonicCutoff: cutoffHz);

        output.WriteLine($"{cutoffHz:0} Hz wants {plan.WarmupSamples} samples " +
                         $"({plan.WarmupSamples / (double)Rate:0.000} s); the old pad was 4410");
        Assert.True(plan.WarmupSamples > 4_410,
            $"{cutoffHz:0} Hz asked for only {plan.WarmupSamples} samples");
    }

    /// <summary>A lower corner is a slower pole, so it needs longer.</summary>
    [Fact]
    public void ALowerCutoffNeedsALongerLeadIn()
    {
        int low = RestorationPreviewPlanning.Create(Rate * 30, Rate, false, 50, 35, false,
            removeSubsonic: true, subsonicCutoff: 20).WarmupSamples;
        int high = RestorationPreviewPlanning.Create(Rate * 30, Rate, false, 50, 35, false,
            removeSubsonic: true, subsonicCutoff: 60).WarmupSamples;

        output.WriteLine($"20 Hz {low} samples, 60 Hz {high}");
        Assert.True(low > high);
    }

    /// <summary>
    /// The claim the term is made of, end to end: filtering from the planned start leaves the
    /// audible range indistinguishable from a whole-file pass, and the flat pad it replaced does
    /// not. Measured against the filter itself rather than against the pole arithmetic that chose
    /// the number, which would only be checking one formula against itself.
    /// </summary>
    [Fact]
    public void StartingAtThePlannedLeadInMatchesAWholeFilePass()
    {
        const int previewStart = Rate * 4;
        const int previewLength = Rate / 2;
        const double cutoff = 30;

        var whole = new float[previewStart + previewLength];
        var random = new Random(7);
        for (int i = 0; i < whole.Length; i++)
        {
            double t = i / (double)Rate;
            whole[i] = (float)(0.30 * Math.Sin(2 * Math.PI * 220 * t)
                             + 0.20 * Math.Sin(2 * Math.PI * 12 * t)
                             + (random.NextDouble() - 0.5) * 0.01);
        }

        var plan = RestorationPreviewPlanning.Create(previewStart, Rate, false, 50, 35, false,
            removeSubsonic: true, subsonicCutoff: cutoff);

        double planned = BoundaryErrorDb(whole, previewStart, previewLength, plan.StartSample, cutoff);
        double padded = BoundaryErrorDb(whole, previewStart, previewLength,
            previewStart - Math.Max(Restoration.NrFftSize * 2, Rate / 10), cutoff);

        output.WriteLine($"planned lead-in ({plan.WarmupSamples} samples): {planned:0.0} dB");
        output.WriteLine($"old flat pad (4410 samples):                {padded:0.0} dB");

        // The float samples cannot carry -180 dB, so the bound asserted is the float noise floor;
        // what the term buys over the pad is the comparison that says the number is doing work.
        Assert.True(planned < -120, $"the planned lead-in left {planned:0.0} dB of startup error");
        Assert.True(planned < padded - 20,
            $"the planned lead-in ({planned:0.0} dB) is no better than the pad ({padded:0.0} dB)");
    }

    private static double BoundaryErrorDb(float[] whole, int previewStart, int previewLength,
        int bufferStart, double cutoff)
    {
        bufferStart = Math.Max(0, bufferStart);
        var reference = new[] { (float[])whole.Clone() };
        Restoration.RemoveSubsonic(reference, Rate, cutoff);

        var window = new float[whole.Length - bufferStart];
        Array.Copy(whole, bufferStart, window, 0, window.Length);
        var partial = new[] { window };
        Restoration.RemoveSubsonic(partial, Rate, cutoff);

        double error = 0, signal = 0;
        for (int i = 0; i < previewLength; i++)
        {
            double a = reference[0][previewStart + i];
            double b = partial[0][previewStart - bufferStart + i];
            error += (a - b) * (a - b);
            signal += a * a;
        }
        return 10 * Math.Log10((error / Math.Max(1e-30, signal)) + 1e-30);
    }

    /// <summary>
    /// De-crackle carries no filter state at all, which is exactly why this is easy to miss: it
    /// fits one autoregressive model per block on a grid anchored at index zero, so a buffer that
    /// starts anywhere else fits its predictors to different audio than the render does.
    /// </summary>
    [Fact]
    public void TheBufferAlignsToTheDeCrackleBlockGrid()
    {
        int block = Decrackle.BlockLengthFor(DecrackleOptions.Default);
        var plan = RestorationPreviewPlanning.Create(Rate * 30 + 137, Rate, false, 50, 35, false,
            removeSubsonic: true, subsonicCutoff: 30, decrackleBlock: block);

        output.WriteLine($"block {block}, start {plan.StartSample}, {plan.StartSample % block} into it");
        Assert.Equal(0, plan.StartSample % block);
    }

    /// <summary>
    /// Both grids at once. The default block is a whole number of hops so a maximum would do here,
    /// but the block is <c>max(order * 8, BlockLength)</c> and neither is required to be one.
    /// </summary>
    [Fact]
    public void TwoGridsAtOnceSatisfyBoth()
    {
        const int hop = Restoration.NrFftSize / 4;
        int block = Decrackle.BlockLengthFor(DecrackleOptions.Default);
        var plan = RestorationPreviewPlanning.Create(Rate * 30 + 991, Rate, false, 50, 35,
            reduceNoise: true, removeSubsonic: false, subsonicCutoff: 30, decrackleBlock: block);

        Assert.Equal(0, plan.StartSample % hop);
        Assert.Equal(0, plan.StartSample % block);
    }

    /// <summary>An odd block size still has to satisfy the hop grid, which is what the LCM is for.</summary>
    [Fact]
    public void AnOddBlockSizeStillLandsOnTheHopGrid()
    {
        const int hop = Restoration.NrFftSize / 4;
        var plan = RestorationPreviewPlanning.Create(Rate * 30 + 991, Rate, false, 50, 35,
            reduceNoise: true, removeSubsonic: false, subsonicCutoff: 30, decrackleBlock: 1_000);

        output.WriteLine($"start {plan.StartSample}: {plan.StartSample % hop} into the hop, " +
                         $"{plan.StartSample % 1_000} into the block");
        Assert.Equal(0, plan.StartSample % hop);
        Assert.Equal(0, plan.StartSample % 1_000);
    }

    /// <summary>The lead-in can never reach behind the range it is previewing.</summary>
    [Fact]
    public void TheLeadInStopsAtTheRangeOrigin()
    {
        var plan = RestorationPreviewPlanning.Create(500, Rate, false, 50, 35, false,
            removeSubsonic: true, subsonicCutoff: 20);

        Assert.Equal(0, plan.StartSample);
        Assert.True(plan.StartsAtRangeOrigin);
        Assert.Equal(500, plan.WarmupSamples);
    }
}
