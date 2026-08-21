using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The wording of the noise card's depth readout, which is a pure function so it needs no window.
/// </summary>
/// <remarks>
/// <b>The line exists because the slider is a ceiling and the applied depth can be far below it, or
/// zero.</b> Measured over 108 corpus cells a fixed 10 dB reduction scores −0.85 dB segmental —
/// worse than leaving the audio alone — so scaling the depth is right, and it takes cells that come
/// out worse than doing nothing from 46 of 108 to 15. But a tool that is right to do nothing still
/// has to be seen doing it: without this line, dragging the slider on a clean transfer changes
/// nothing audible at any position, which is indistinguishable from a broken control.
/// </remarks>
public sealed class NoiseDepthReadoutTests(ITestOutputHelper output)
{
    private static string Line(bool analysed, bool hasProfile,
        double requested, double applied, double estimate) =>
        RestorationWorkbenchDialog.DescribeNoiseDepth(true, analysed, hasProfile, requested, applied, estimate)
            .ToString();

    [Fact]
    public void ThePartialDepthLineCarriesBothNumbers()
    {
        string line = Line(analysed: true, hasProfile: true, requested: 10, applied: 2.3, estimate: 7.7);
        output.WriteLine(line);

        Assert.Equal("Applying 2.3 dB · hiss sits 7.7 dB under the programme.", line);
    }

    /// <summary>
    /// The card's own switch is reported before anything about the audio.
    /// </summary>
    /// <remarks>
    /// The stage does not run when the card is switched off, so a line saying "Applying 2.3 dB"
    /// would be describing something that will not happen — the same disagreement between what is
    /// shown and what runs that this whole readout exists to prevent. Missed on the first pass:
    /// the readout consulted the audio and the slider and never the checkbox.
    /// </remarks>
    [Fact]
    public void ASwitchedOffCardSaysSoBeforeAnythingAboutTheAudio()
    {
        var readout = RestorationWorkbenchDialog.DescribeNoiseDepth(
            enabled: false, analysed: true, hasProfile: true,
            requestedDb: 10, appliedDb: 7.5, estimateDb: 2.0);
        output.WriteLine(readout.ToString());

        Assert.Equal("Not reducing \u00b7 this card is switched off.", readout.ToString());
        Assert.True(readout.Declining);
        Assert.DoesNotContain("7.5", readout.ToString());
    }

    /// <summary>The state that must never be silent.</summary>
    [Fact]
    public void DecliningSaysSoAndSaysWhy()
    {
        var readout = RestorationWorkbenchDialog.DescribeNoiseDepth(
            enabled: true, analysed: true, hasProfile: true, requestedDb: 10, appliedDb: 0, estimateDb: 12.4);
        output.WriteLine(readout.ToString());

        Assert.Equal("Not reducing · hiss already 12.4 dB under the programme.", readout.ToString());

        // The verb is its own half of the line so it can be coloured alone. Colouring the measured
        // number like a fault would teach the user to distrust the colour, which is the lesson
        // already on record from the VST3 scanner's "no host-visible parameters" note.
        Assert.Equal("Not reducing", readout.Lead);
        Assert.True(readout.Declining);
    }

    /// <summary>A zero on the slider is the user's decision, and is reported as a different one.</summary>
    [Fact]
    public void AZeroMaximumIsNotBlamedOnTheAudio()
    {
        var readout = RestorationWorkbenchDialog.DescribeNoiseDepth(
            enabled: true, analysed: true, hasProfile: true, requestedDb: 0, appliedDb: 0, estimateDb: 3.0);
        output.WriteLine(readout.ToString());

        Assert.Equal("Not reducing · the maximum is set to zero.", readout.ToString());
        Assert.True(readout.Declining);
        Assert.DoesNotContain("under the programme", readout.ToString());
    }

    [Theory]
    [InlineData(false, true, "Run analysis to see the depth.")]
    [InlineData(true, false, "Learn a noise profile to reduce hiss.")]
    public void BeforeThereIsAnythingToSayItSaysWhatIsMissing(
        bool analysed, bool hasProfile, string expected)
    {
        var readout = RestorationWorkbenchDialog.DescribeNoiseDepth(
            enabled: true, analysed, hasProfile, requestedDb: 10, appliedDb: 0, estimateDb: 0);

        Assert.Equal(expected, readout.ToString());
        Assert.False(readout.Declining);        // nothing has been decided yet, so nothing is amber
    }

    /// <summary>
    /// The longest wording the line can produce still fits the card, measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// Both numbers are bounded: the slider stops at 24 dB, and the estimate is only printed below
    /// the ceiling that switches reduction off, so neither can grow without limit. This asserts the
    /// character count as a proxy, because measuring glyphs needs a window
    /// — <see cref="NoiseDepthRenderProbe"/> does that against the built control and reports
    /// <b>370 px of room for a 286 px line</b>.
    /// </remarks>
    [Fact]
    public void TheLongestWordingIsBounded()
    {
        var longest = new List<string>();
        foreach (double requested in new[] { 0.0, 1.0, 10.0, 24.0 })
            foreach (double applied in new[] { 0.0, 2.3, 9.95, 24.0 })
                foreach (double estimate in new[] { 0.0, 7.7, 9.99, 12.4 })
                    longest.Add(Line(true, true, requested, applied, estimate));
        longest.Add(Line(false, true, 10, 0, 0));
        longest.Add(Line(true, false, 10, 0, 0));

        string worst = longest.MaxBy(l => l.Length)!;
        output.WriteLine($"{worst.Length} characters: {worst}");

        // 286 px measured in the built control for a 55-character line, against 370 px of card.
        Assert.True(worst.Length <= 60, $"the line grew to {worst.Length} characters: {worst}");
    }
}
