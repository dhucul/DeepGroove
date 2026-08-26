using System.Windows;
using System.Windows.Controls;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// What the line under the track list says an Analyze pass did.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "it gives the same message with −30 — what is this supposed to mean and what am I
/// supposed to do with this information", and the report is right. The line said "Found 3 probable
/// tracks at −45 dB", which is the count and the setting that produced it — and the setting is
/// printed beside the slider that set it, so the only new fact was the count, which had not moved.
/// </para>
/// <para>
/// Measured on three real transfers butted into one 504 s side: −45 dB and −30 dB both propose
/// three tracks, and the boundaries between them sit <b>7.6 s apart</b> — at −30 the fade-out
/// counts as quiet sooner, so the gap starts earlier and its midpoint lands inside the music. The
/// old wording was true of both and described neither.
/// </para>
/// </remarks>
public sealed class CdProposalReadoutTests
{
    [Fact]
    public void AFirstPassSaysWhatToJudgeTheCountAgainst()
    {
        string line = CdTransfer.DescribeProposal(3, previous: 0, double.NaN, -45);
        Assert.Contains("3 tracks", line, StringComparison.Ordinal);
        Assert.Contains("-45 dB", line, StringComparison.Ordinal);
        Assert.Contains("if the side holds a different number", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The count is the interesting number only when it moves, so when it does, say where from.
    /// </summary>
    [Theory]
    [InlineData(3, 1, "up from 1")]
    [InlineData(2, 5, "down from 5")]
    public void AChangedCountSaysWhereItCameFrom(int proposed, int previous, string expected)
    {
        string line = CdTransfer.DescribeProposal(proposed, previous, double.NaN, -35);
        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the report came from. Three tracks before and three after is not "found 3 tracks"
    /// again — it is either nothing at all, or every boundary quietly moving into the fades, and
    /// those are the two things worth telling apart.
    /// </summary>
    [Fact]
    public void TheSameCountReportsWhetherTheBoundariesActuallyMoved()
    {
        string still = CdTransfer.DescribeProposal(3, previous: 3, 0, -45);
        Assert.Contains("the same 3 tracks in the same places", still, StringComparison.Ordinal);
        Assert.Contains("nothing moved", still, StringComparison.Ordinal);

        // 7.55 s is the real figure measured between -45 dB and -30 dB on the three-track side.
        string moved = CdTransfer.DescribeProposal(3, previous: 3, 7.55, -30);
        Assert.Contains("Still 3 tracks", moved, StringComparison.Ordinal);
        Assert.Contains("7.6 s", moved, StringComparison.Ordinal);
        Assert.Contains("SOURCE IN and OUT", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing moved", moved, StringComparison.Ordinal);
    }

    /// <summary>A move past a minute reads as a timecode, not as "185.0 s".</summary>
    [Fact]
    public void ALongMoveIsWrittenAsATimecode()
    {
        Assert.Contains("3:05", CdTransfer.DescribeProposal(3, 3, 185, -30), StringComparison.Ordinal);
        Assert.Contains("59.5 s", CdTransfer.DescribeProposal(3, 3, 59.5, -30), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one case where the user is stuck — nothing to rename, nothing to reorder — is the one
    /// worth spending the line on saying which way the slider goes. Everywhere else that lives on
    /// the slider's own tool tip, where it can be a whole sentence.
    /// </summary>
    [Fact]
    public void OneTrackIsTheCaseThatSaysWhichWayToMoveTheSlider()
    {
        string line = CdTransfer.DescribeProposal(1, previous: 0, double.NaN, -45);
        Assert.Contains("one track", line, StringComparison.Ordinal);
        Assert.Contains("toward -25 dB", line, StringComparison.Ordinal);

        // -25 dB is the laxer test and so the direction that finds more gaps. Pointing at -70 there
        // would send the user the way that finds fewer, which is the opposite of what they want.
        Assert.DoesNotContain("toward -70", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyProposalDoesNotClaimToHaveFoundTracks()
    {
        string line = CdTransfer.DescribeProposal(0, previous: 3, double.NaN, -45);
        Assert.Contains("Nothing was proposed", line, StringComparison.Ordinal);
    }
}

/// <summary>
/// Measures the widest of those wordings in the built dialog, at the width the window is fixed to.
/// </summary>
/// <remarks>
/// <b>Render before claiming a layout works</b>, and measure the right thing while doing it: the
/// first version of this read <c>DesiredSize</c> off the live <c>TextBlock</c>, which is capped at
/// the room its parent gave it — four different wordings all reported 752 px against 756, which
/// reads as "fits" and means "trimmed to the ellipsis". A detached copy carrying the live element's
/// own typeface, measured against infinity, is what answers the question.
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class CdStatusRenderProbe(ITestOutputHelper output) : IDisposable
{
    private readonly string _original = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppSettings.AppDataDir = _original;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    [Fact]
    public void EveryAnalysisWordingFitsTheStatusLine()
    {
        AppSettings.AppDataDir = _sandbox;
        string[] wordings =
        [
            CdTransfer.DescribeProposal(3, 0, double.NaN, -45),
            CdTransfer.DescribeProposal(3, 1, double.NaN, -35),
            CdTransfer.DescribeProposal(12, 3, double.NaN, -25),
            CdTransfer.DescribeProposal(3, 3, 0, -45),
            CdTransfer.DescribeProposal(3, 3, 7.55, -30),
            CdTransfer.DescribeProposal(99, 98, 185, -30),
            CdTransfer.DescribeProposal(1, 0, double.NaN, -45),
        ];

        var report = new List<string>();
        double room = 0, widest = 0;

        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            main.AddDocument(new AudioDocument([new float[44_100], new float[44_100]], 44_100, 16)
            { Title = "Side A.wav" });
            DocumentViewModel document = main.ActiveDocument!;
            document.Regions.Add(new NamedRegion { Name = "A", Start = 0, End = 44_100, CdTrackOrder = 1 });

            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                Wpf.Pump();
                var status = (TextBlock)window.FindName("statusText")!;
                room = status.ActualWidth;

                foreach (string text in wordings)
                {
                    var probe = new TextBlock
                    {
                        Text = text,
                        FontFamily = status.FontFamily,
                        FontSize = status.FontSize,
                        FontStyle = status.FontStyle,
                        FontWeight = status.FontWeight,
                        FontStretch = status.FontStretch,
                    };
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double wanted = probe.DesiredSize.Width;
                    widest = Math.Max(widest, wanted);
                    report.Add($"{wanted,6:F0} / {room:F0} px  \"{text}\"");
                }
            });
        });

        foreach (string line in report) output.WriteLine(line);
        output.WriteLine($"widest {widest:F0} px in {room:F0} px");

        Assert.True(room > 0, "the status line was not laid out");
        Assert.True(widest <= room,
            $"the widest analysis wording wants {widest:F0} px and the status line has {room:F0} px, " +
            "so it would be cut to an ellipsis");
    }
}
