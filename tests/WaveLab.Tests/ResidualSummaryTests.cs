using WaveLab.Util;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The account the user gets of what a pass removed. Two surfaces report it — the workbench and
/// the Restore menu tools — so the wording lives in one pure function and is asserted here rather
/// than in a window.
/// </summary>
/// <remarks>
/// The character budgets come from the same measurement <see cref="DeclipReadoutTests"/> records:
/// about 4.7 px a character at 10.5 px Segoe UI. The status line runs the width of the window and
/// is trimmed with an ellipsis, so it is given the workbench card's caption width rather than the
/// readout's — 74 characters is the tight case and this is not it.
/// </remarks>
public sealed class ResidualSummaryTests(ITestOutputHelper output)
{
    private const int StatusBudget = 96;

    private void Fits(string line)
    {
        output.WriteLine($"{line.Length,3} chars: {line}");
        Assert.True(line.Length <= StatusBudget,
            $"{line.Length} characters is more status line than there is: {line}");
    }

    [Fact]
    public void TheStatusLineNamesTheFileThePeakAndTheLift()
    {
        string line = ResidualSummary.Describe("demo_track (removed).wav", 0.0123f,
            ResidualSummary.MonitorGainFor(0.0123f, 0.0031f));
        Assert.Equal("demo_track (removed).wav · peak -38.2 dBFS · monitoring at +26 dB.", line);
        Fits(line);
    }

    /// <summary>
    /// A residual loud enough to need no lift should not be told it is being monitored at +0 dB;
    /// that reads as a bug rather than as the good news it is.
    /// </summary>
    [Fact]
    public void AResidualLoudEnoughToHearSaysSo()
    {
        string line = ResidualSummary.Describe("side one (removed).wav", 0.8f, 1f);
        Assert.Equal("side one (removed).wav · peak -1.9 dBFS · no lift needed.", line);
        Fits(line);
    }

    [Fact]
    public void ALongTitleAtTheLargestLiftStillFits()
    {
        string line = ResidualSummary.Describe(
            "Beethoven - Symphony No 9 - Side Two (removed).wav", 1e-9f,
            ResidualSummary.MonitorGainFor(1e-5f, 1e-6f));
        Fits(line);
    }

    [Fact]
    public void ATitleIsRequiredBecauseAStatusLineWithoutOneSaysNothing()
    {
        Assert.Throws<ArgumentException>(() => ResidualSummary.Describe("  ", 0.1f, 2f));
    }

    [Fact]
    public void NothingRemovedIsReportedRatherThanPassedOverInSilence()
    {
        string line = ResidualSummary.DescribeNothingRemoved("Remove Clicks");
        Assert.Equal("Remove Clicks removed nothing audible · no separate file was made.", line);
        Fits(line);
    }

    [Fact]
    public void TheCostCaptionStatesWhatTheOptionWillTake()
    {
        // A 25-minute stereo side at 44.1 kHz.
        long samples = 25L * 60 * 44_100;
        string caption = ResidualSummary.DescribeCost(samples, 2);
        output.WriteLine(caption);
        Assert.Contains("second tab", caption);
        Assert.Contains("505 MB", caption);
    }

    [Fact]
    public void ALongSideIsQuotedInGigabytesRatherThanFourFigureMegabytes()
    {
        // Through the refusal, which is now the only way a figure that large can be reached:
        // anything measured in gigabytes is past the ceiling by definition.
        string line = ResidualSummary.DescribeTooLarge("Remove Clicks", 60L * 60 * 96_000, 2);
        output.WriteLine(line);
        Assert.Contains("2.6 GB", line);
        Assert.DoesNotContain("2662 MB", line);
    }

    /// <summary>
    /// Stating a cost is not the same as declining an impossible one. The clipboard already
    /// refuses a selection over the same 512 MB, and for the same reason: a residual is a whole
    /// extra copy of the range on top of everything else the edit is holding.
    /// </summary>
    [Fact]
    public void ARangeTooLongToKeepIsRefusedRatherThanQuoted()
    {
        // An hour of 96 kHz stereo: 2.6 GB.
        long samples = 60L * 60 * 96_000;
        Assert.True(ResidualSummary.ExceedsBudget(samples, 2));
        string caption = ResidualSummary.DescribeCost(samples, 2);
        output.WriteLine(caption);
        Assert.Contains("2.6 GB", caption);
        Assert.Contains("512 MB limit", caption);
        Assert.Contains("select less", caption);
        Assert.DoesNotContain("second tab", caption);
    }

    [Fact]
    public void TheCeilingSitsWhereTheClipboardsDoes()
    {
        const long Samples = 512L * 1024 * 1024 / sizeof(float) / 2;   // exactly 512 MB stereo
        Assert.False(ResidualSummary.ExceedsBudget(Samples, 2));
        Assert.True(ResidualSummary.ExceedsBudget(Samples + 1, 2));
        Assert.False(ResidualSummary.ExceedsBudget(0, 2));
        Assert.False(ResidualSummary.ExceedsBudget(Samples * 8, 0));
    }

    [Fact]
    public void ARangeThatTurnedOutTooLargeSaysTheEditStillStands()
    {
        string line = ResidualSummary.DescribeTooLarge("Remove Clicks", 60L * 60 * 96_000, 2);
        output.WriteLine(line);
        Assert.StartsWith("Remove Clicks applied", line);
        Assert.Contains("2.6 GB", line);
        Assert.Contains("was not kept", line);
    }

    [Fact]
    public void AnEmptyRangeQuotesNoFigureAtAll()
    {
        string caption = ResidualSummary.DescribeCost(0, 2);
        Assert.DoesNotContain("About", caption);
        Assert.Contains("second tab", caption);
    }

    [Fact]
    public void PeakTextReadsSilenceAsAWordRatherThanAsMinusInfinity()
    {
        Assert.Equal("silent", ResidualSummary.PeakText(0f));
        Assert.Equal("0.0 dBFS", ResidualSummary.PeakText(1f));
    }
}
