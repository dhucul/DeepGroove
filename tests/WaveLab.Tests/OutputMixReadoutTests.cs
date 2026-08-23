using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The wording of the output-mix ceiling line, which is a pure function so it needs no window.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mix is applied once, to the whole chain output</b>, so the share of the original it
/// returns is a floor under everything the chain removed — and therefore a ceiling on every stage
/// in it. At the shipped 90% default that ceiling is <b>20 dB</b>, which is well inside the range
/// the deeper stages work in: the subsonic high-pass measures 40 dB at 10 Hz on a real transfer and
/// lands at 19.7 through the dialog, and the notch bank's 42 dB of hum is capped at the same 20.
/// </para>
/// <para>
/// It was found by measuring a residual rather than by reading the code, which is the argument for
/// the line existing: nothing on screen said the mix was the binding constraint, so the honest
/// reading of a stage that underperforms its own numbers was that the stage was broken.
/// </para>
/// </remarks>
public sealed class OutputMixReadoutTests(ITestOutputHelper output)
{
    private static string Line(double restoredPercent, bool bypass = false) =>
        RestorationWorkbenchDialog.DescribeOutputMix(restoredPercent / 100.0, bypass).ToString();

    /// <summary>
    /// The default, and the number this whole line was added for: ten percent dry returning is
    /// twenty decibels, and twenty decibels is less than three of the stages achieve on their own.
    /// </summary>
    [Fact]
    public void TheShippedDefaultReportsATwentyDecibelCeiling()
    {
        string line = Line(90);
        output.WriteLine(line);

        Assert.Equal("Ceiling 20.0 dB · 10% dry returns over every stage.", line);
    }

    [Theory]
    [InlineData(50, "Ceiling 6.0 dB · 50% dry returns over every stage.")]
    [InlineData(90, "Ceiling 20.0 dB · 10% dry returns over every stage.")]
    [InlineData(99, "Ceiling 40.0 dB · 1% dry returns over every stage.")]
    public void TheCeilingIsTheDryShareInDecibels(double restored, string expected)
    {
        string line = Line(restored);
        output.WriteLine(line);

        Assert.Equal(expected, line);
    }

    /// <summary>At the top of the slider there is no ceiling, so the line must not invent one.</summary>
    /// <remarks>
    /// A line reading "Ceiling 320.0 dB" is arithmetically true and useless; what a user needs to
    /// know at 100% is that the mix has stopped constraining anything.
    /// </remarks>
    [Fact]
    public void AtFullWetThereIsNoCeilingToReport()
    {
        var readout = RestorationWorkbenchDialog.DescribeOutputMix(1.0, bypass: false);
        output.WriteLine(readout.ToString());

        Assert.Equal("No ceiling · every stage applies in full.", readout.ToString());
        Assert.False(readout.Inert);
    }

    /// <summary>
    /// Bypass is reported before anything about the mix, because the mix means nothing while the
    /// chain is off — the same rule the noise card's readout follows for its own switch.
    /// </summary>
    [Fact]
    public void BypassIsReportedBeforeTheMix()
    {
        var readout = RestorationWorkbenchDialog.DescribeOutputMix(0.9, bypass: true);
        output.WriteLine(readout.ToString());

        Assert.Equal("Bypassed", readout.Lead);
        Assert.True(readout.Inert);
        Assert.DoesNotContain("Ceiling", readout.ToString());
    }

    /// <summary>
    /// The two states where the chain's work does not reach the output are the only ones that earn
    /// amber. An ordinary ceiling is a fact about a setting, and colouring a fact like a fault is
    /// what the VST3 scanner note records as teaching users to distrust the colour.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(50, false)]
    [InlineData(90, false)]
    [InlineData(100, false)]
    public void OnlyAnInertChainEarnsTheColour(double restored, bool expectedInert)
    {
        var readout = RestorationWorkbenchDialog.DescribeOutputMix(restored / 100.0, bypass: false);
        output.WriteLine($"{restored:0}% -> {readout} (inert {readout.Inert})");

        Assert.Equal(expectedInert, readout.Inert);
    }

    /// <summary>
    /// The slider is continuous, so a mix a hair under full is reachable by dragging. Rounding the
    /// dry share there would print "0% dry returns" beside a ceiling that says otherwise — a line
    /// contradicting itself in its own two halves.
    /// </summary>
    [Fact]
    public void AMixJustUnderFullDoesNotClaimNoDryAtAll()
    {
        string line = Line(99.7);
        output.WriteLine(line);

        Assert.Contains("under 1% dry returns", line);
        Assert.DoesNotContain("0% dry", line);
    }

    /// <summary>
    /// Bounded, so no setting can push the sentence off the card. The widest is the fully dry line
    /// rather than any ceiling — the deepest ceilings come with the shortest detail, because a mix
    /// near full returns almost no dry to describe.
    /// </summary>
    /// <remarks>
    /// A character count is not a width and is not pretended to be one; it is the cheap invariant
    /// that catches a wording change growing without bound. <c>OutputMixRenderProbe</c> is what
    /// measures the line in the built control, for the reason the noise-depth probe records.
    /// </remarks>
    [Fact]
    public void NothingOnTheSliderMakesTheLineLongerThanTheWidestWording()
    {
        int longest = RestorationWorkbenchDialog.DescribeOutputMix(0.9, bypass: true).ToString().Length;
        string worst = RestorationWorkbenchDialog.DescribeOutputMix(0.9, bypass: true).ToString();
        for (double restored = 0; restored <= 100.0001; restored += 0.1)
        {
            string line = Line(Math.Min(restored, 100));
            if (line.Length > longest) { longest = line.Length; worst = line; }
        }
        output.WriteLine($"longest at {longest} characters: {worst}");

        Assert.True(longest <= 57, $"the line reached {longest} characters: {worst}");
    }
}
