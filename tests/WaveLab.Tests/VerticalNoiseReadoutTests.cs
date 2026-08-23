using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The wording of the two cards whose recommendation a user has to be able to judge, both pure
/// functions so neither needs a window.
/// </summary>
/// <remarks>
/// These two lines carry more weight than the readouts already here, because both cards can decide
/// to do something large. The side control can discard a whole channel of a stereo file, and
/// de-crackle turns itself on from evidence that is not a measurement of crackle. A control that
/// acts on a reason the user cannot see is one they cannot overrule.
/// </remarks>
public sealed class VerticalNoiseReadoutTests(ITestOutputHelper output)
{
    private static string Side(double sideToMidDb, double level, bool enabled = true,
        bool analysed = true, bool stereo = true) =>
        RestorationWorkbenchDialog.DescribeSideLevel(enabled, analysed, stereo, sideToMidDb, level);

    private static string Crackle(int impulses, double threshold = 3.5, bool enabled = true,
        bool analysed = true) =>
        RestorationWorkbenchDialog.DescribeCrackle(enabled, analysed, impulses, threshold);

    // ── the side control ─────────────────────────────────────────

    /// <summary>
    /// A mono pressing says so, gives the number, and says what that means for the side — the whole
    /// case for discarding a channel has to be on the card.
    /// </summary>
    [Fact]
    public void AMonoPressingSaysWhyTheSideCanGo()
    {
        string line = Side(-16.5, 0.0);
        output.WriteLine(line);

        Assert.Contains("16.5 dB under the mid", line);
        Assert.Contains("cut mono", line);
        Assert.Contains("Discarding the side", line);
    }

    /// <summary>
    /// A stereo pressing must say the cost out loud, because at that ratio the side is music and
    /// the control still moves.
    /// </summary>
    [Fact]
    public void AStereoPressingSaysWhatReducingTheSideCosts()
    {
        string line = Side(-6.0, 0.5);
        output.WriteLine(line);

        Assert.Contains("real stereo content", line);
        Assert.Contains("narrows the image", line);
        Assert.Contains("6.0 dB", line);
    }

    /// <summary>Between the anchors the honest answer is that some of what goes is music.</summary>
    [Fact]
    public void BetweenTheAnchorsTheLineSaysSoRatherThanPickingASide()
    {
        string line = Side(-11.0, 0.5);
        output.WriteLine(line);

        Assert.Contains("between a mono pressing and a stereo one", line);
        Assert.Contains("some of what goes is music", line);
    }

    /// <summary>
    /// The card's own switch comes first, the same rule the noise depth line follows: a line
    /// describing a stage that will not run is the disagreement these readouts exist to prevent.
    /// </summary>
    [Fact]
    public void TheSwitchIsReportedBeforeTheMeasurement()
    {
        string line = Side(-16.5, 0.0, enabled: false);
        output.WriteLine(line);

        Assert.Contains("switched off", line);
        Assert.DoesNotContain("Discarding", line);
    }

    [Fact]
    public void AMonoDocumentHasNoSideToTalkAbout()
    {
        string line = Side(0, 0, stereo: false);
        output.WriteLine(line);
        Assert.Contains("no side signal", line);
    }

    [Fact]
    public void BeforeAnalysisItClaimsNothing()
    {
        string line = Side(0, 1.0, analysed: false);
        output.WriteLine(line);
        Assert.Contains("Run analysis", line);
    }

    /// <summary>Full level is a stage that will not change the audio, and says so plainly.</summary>
    [Fact]
    public void FullSideLevelReadsAsLeavingItAlone()
    {
        string line = Side(-6.0, 1.0);
        output.WriteLine(line);
        Assert.Contains("Leaving the side at full", line);
    }

    /// <summary>The reduction is stated in dB, which is what a level in percent does not tell you.</summary>
    [Theory]
    [InlineData(0.5, "6.0 dB")]
    [InlineData(0.25, "12.0 dB")]
    public void APartialReductionIsStatedInDecibels(double level, string expected)
    {
        string line = Side(-11.0, level);
        output.WriteLine(line);
        Assert.Contains($"Reducing the side by {expected}", line);
    }

    // ── the crackle card ─────────────────────────────────────────

    /// <summary>
    /// The evidence is weaker here than on any other card and the line has to admit it: nothing
    /// measures crackle, which sits below the click detector's reach by definition.
    /// </summary>
    [Fact]
    public void TheCrackleLineNamesTheEvidenceAndItsLimit()
    {
        string line = Crackle(1_284);
        output.WriteLine(line);

        Assert.Contains("1,284 impulses", line);
        Assert.Contains("not counted", line);
    }

    [Fact]
    public void WithNoImpulsesItSaysThereIsNoEvidence()
    {
        string line = Crackle(0);
        output.WriteLine(line);
        Assert.Contains("No impulses were found", line);
    }

    /// <summary>
    /// Below three deviations the tool loses on both axes at once — measured, 2.5σ repaired twice
    /// as many samples and left more audible ticks than 3.5σ did — so a user who drags it there is
    /// told.
    /// </summary>
    [Fact]
    public void GoingBelowThreeDeviationsIsWarnedAbout()
    {
        string warned = Crackle(500, threshold: 2.5);
        string quiet = Crackle(500, threshold: 3.5);
        output.WriteLine(warned);

        Assert.Contains("2.5", warned);
        Assert.Contains("repairs twice as many samples", warned);
        Assert.DoesNotContain("repairs twice as many samples", quiet);
    }

    [Fact]
    public void TheCrackleSwitchIsAlsoReportedFirst()
    {
        string line = Crackle(500, enabled: false);
        output.WriteLine(line);
        Assert.Equal("This card is switched off.", line);
    }
}
