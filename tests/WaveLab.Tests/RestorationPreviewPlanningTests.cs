using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The bounded lead-in a restoration preview runs before the audible range.
/// </summary>
/// <remarks>
/// Every stage added to the workbench either has state, a grid, or finite look-around, and the way that
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
    /// The zero-phase high-pass has finite look-around rather than IIR state. The declared context
    /// copied on both sides of a preview must therefore match a whole-file render.
    /// </summary>
    [Fact]
    public void OrdinaryPreviewPaddingContainsTheWholeHighPassResponse()
    {
        const int previewStart = Rate * 4;
        const int previewLength = Rate / 2;
        const double cutoff = 30;
        int padding = Math.Max(Restoration.NrFftSize * 2,
            Restoration.SubsonicLookaroundSamples(Rate));

        var whole = new float[previewStart + previewLength + Rate];
        var random = new Random(7);
        for (int i = 0; i < whole.Length; i++)
        {
            double t = i / (double)Rate;
            whole[i] = (float)(0.30 * Math.Sin(2 * Math.PI * 220 * t)
                             + 0.20 * Math.Sin(2 * Math.PI * 12 * t)
                             + (random.NextDouble() - 0.5) * 0.01);
        }

        double error = BoundaryErrorDb(whole, previewStart, previewLength,
            previewStart - padding, previewStart + previewLength + padding, cutoff);

        output.WriteLine($"declared two-sided padding: {error:0.0} dB");
        Assert.True(error < -120, $"ordinary preview padding left {error:0.0} dB of error");
    }

    private static double BoundaryErrorDb(float[] whole, int previewStart, int previewLength,
        int bufferStart, int bufferEnd, double cutoff)
    {
        bufferStart = Math.Max(0, bufferStart);
        bufferEnd = Math.Min(whole.Length, bufferEnd);
        var reference = new[] { (float[])whole.Clone() };
        Restoration.RemoveSubsonic(reference, Rate, cutoff);

        var window = new float[bufferEnd - bufferStart];
        Array.Copy(whole, bufferStart, window, 0, bufferEnd - bufferStart);
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
            decrackleBlock: block);

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
            reduceNoise: true, decrackleBlock: block);

        Assert.Equal(0, plan.StartSample % hop);
        Assert.Equal(0, plan.StartSample % block);
    }

    /// <summary>An odd block size still has to satisfy the hop grid, which is what the LCM is for.</summary>
    [Fact]
    public void AnOddBlockSizeStillLandsOnTheHopGrid()
    {
        const int hop = Restoration.NrFftSize / 4;
        var plan = RestorationPreviewPlanning.Create(Rate * 30 + 991, Rate, false, 50, 35,
            reduceNoise: true, decrackleBlock: 1_000);

        output.WriteLine($"start {plan.StartSample}: {plan.StartSample % hop} into the hop, " +
                         $"{plan.StartSample % 1_000} into the block");
        Assert.Equal(0, plan.StartSample % hop);
        Assert.Equal(0, plan.StartSample % 1_000);
    }

    /// <summary>The lead-in can never reach behind the range it is previewing.</summary>
    [Fact]
    public void TheLeadInStopsAtTheRangeOrigin()
    {
        var plan = RestorationPreviewPlanning.Create(500, Rate, true, 50, 35, false,
            decrackleBlock: 0);

        Assert.Equal(0, plan.StartSample);
        Assert.True(plan.StartsAtRangeOrigin);
        Assert.Equal(500, plan.WarmupSamples);
    }
}
