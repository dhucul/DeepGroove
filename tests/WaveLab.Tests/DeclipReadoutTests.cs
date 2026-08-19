using WaveLab.Audio.Dsp;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The line under the declip method switch, which is the only account the user gets of why one
/// channel is about to take minutes and another a second.
/// </summary>
/// <remarks>
/// The widths quoted here were measured by rendering the real control offscreen at the dialog's
/// 860 px minimum width, where the readout has 365 px. They are in the assertions as character
/// budgets rather than pixels, because a test that needs a window to run is a test that gets
/// skipped; the budget is set from the measurement and the measurement is repeatable.
/// </remarks>
public sealed class DeclipReadoutTests(ITestOutputHelper output)
{
    private static DeclipChannelChoice Choice(int channel, DeclipMethod method,
        double clippedFraction, double meanRun) =>
        new(channel, method, clippedFraction, meanRun);

    /// <summary>
    /// 365 px of readout at 10.5 px Segoe UI came to about 4.7 px a character across the strings
    /// measured, so 74 characters is the budget the line has to stay inside.
    /// </summary>
    private const int Budget = 74;

    private void Fits(string line)
    {
        output.WriteLine($"{line.Length,3} chars: {line}");
        Assert.True(line.Length <= Budget,
            $"{line.Length} characters will not fit the card at its minimum width: {line}");
    }

    [Fact]
    public void AgreeingChannelsReadAsOneSentenceWithTheNumbers()
    {
        string line = RestorationWorkbenchDialog.DescribeChoices(
        [
            Choice(0, DeclipMethod.Sparse, 0.042, 17),
            Choice(1, DeclipMethod.Sparse, 0.044, 18),
        ]);
        Assert.Equal("Chose sparse · 4.2% clipped, runs of 17.", line);
        Fits(line);
    }

    /// <summary>
    /// The case this was written for. The channels disagree <b>because</b> their numbers differ, so
    /// a single shared figure would be a lie and dropping the figures leaves the surprise
    /// unexplained; both sets stay on the line.
    /// </summary>
    [Fact]
    public void TwoDisagreeingChannelsEachKeepTheirOwnNumbers()
    {
        string line = RestorationWorkbenchDialog.DescribeChoices(
        [
            Choice(0, DeclipMethod.Sparse, 0.042, 17),
            Choice(1, DeclipMethod.PeakReconstruction, 0.31, 90),
        ]);
        Assert.Equal("Chose sparse on 1 (4.2%, runs 17), peaks on 2 (31%, runs 90).", line);
        Fits(line);
    }

    /// <summary>
    /// One badly damaged channel must not push the sentence off the card, so both figures are
    /// bounded. This is the widest the two-channel line can ever be.
    /// </summary>
    [Fact]
    public void TheWidestPossibleTwoChannelLineStillFits()
    {
        string line = RestorationWorkbenchDialog.DescribeChoices(
        [
            Choice(0, DeclipMethod.Sparse, 1.0, 44100),
            Choice(1, DeclipMethod.PeakReconstruction, 1.0, 96000),
        ]);
        Assert.Equal("Chose sparse on 1 (100%, runs 999+), peaks on 2 (100%, runs 999+).", line);
        Fits(line);
    }

    [Fact]
    public void AboveOneHundredPercentIsNotReportable()
    {
        string line = RestorationWorkbenchDialog.DescribeChoices(
        [
            Choice(0, DeclipMethod.Sparse, 1.4, 12),
            Choice(1, DeclipMethod.PeakReconstruction, 0.5, 12),
        ]);
        Assert.Contains("(100%, runs 12)", line);
        Fits(line);
    }

    /// <summary>Three channels of numbers want 507 px against 365, so they group by method.</summary>
    [Fact]
    public void MoreThanTwoChannelsGroupByMethodAndDropToTheToolTip()
    {
        string line = RestorationWorkbenchDialog.DescribeChoices(
        [
            Choice(0, DeclipMethod.Sparse, 0.04, 17),
            Choice(1, DeclipMethod.PeakReconstruction, 0.31, 90),
            Choice(2, DeclipMethod.Sparse, 0.05, 19),
            Choice(3, DeclipMethod.PeakReconstruction, 0.28, 80),
            Choice(4, DeclipMethod.Sparse, 0.04, 16),
            Choice(5, DeclipMethod.PeakReconstruction, 0.30, 88),
        ]);
        Assert.Equal("Chose sparse on 1, 3, 5 and peaks on 2, 4, 6.", line);
        Fits(line);
    }

    /// <summary>Sixteen channels listed want 371 px, so past eight they are counted instead.</summary>
    [Fact]
    public void ManyChannelsAreCountedRatherThanListed()
    {
        var choices = Enumerable.Range(0, 16)
            .Select(i => Choice(i, i % 2 == 0 ? DeclipMethod.Sparse : DeclipMethod.PeakReconstruction,
                0.04 + i * 0.01, 17 + i))
            .ToList();
        string line = RestorationWorkbenchDialog.DescribeChoices(choices);
        Assert.Equal("Chose sparse on 8 channels and peaks on 8.", line);
        Fits(line);
    }

    /// <summary>Channels with no damage are absent from the report, so the numbering can skip.</summary>
    [Fact]
    public void OnlyDamagedChannelsAreNamed()
    {
        string line = RestorationWorkbenchDialog.DescribeChoices(
        [
            Choice(0, DeclipMethod.Sparse, 0.042, 17),
            Choice(2, DeclipMethod.PeakReconstruction, 0.31, 90),
        ]);
        Assert.Equal("Chose sparse on 1 (4.2%, runs 17), peaks on 3 (31%, runs 90).", line);
        Fits(line);
    }

    [Fact]
    public void NoChoicesSaysSo()
    {
        Assert.Equal("No clipping detected.", RestorationWorkbenchDialog.DescribeChoices([]));
    }

    /// <summary>
    /// Every state the line can be in has to fit, and the ordinary agreeing case is the one that
    /// must never regress, since it is what a user sees on nearly every file.
    /// </summary>
    [Fact]
    public void EveryStateFitsTheCard()
    {
        Fits(RestorationWorkbenchDialog.DescribeChoices([Choice(0, DeclipMethod.Sparse, 0.494, 128)]));
        Fits(RestorationWorkbenchDialog.DescribeChoices([Choice(0, DeclipMethod.PeakReconstruction, 1.0, 44100)]));
    }
}
