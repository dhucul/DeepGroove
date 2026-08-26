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
/// Reported twice. First as "Analyze does nothing"; then, once it plainly did something, as
/// <i>"the message at the bottom is too cryptic — I have no idea what the info means or what I'm
/// supposed to do with it"</i>. Both reports are about the same line, and the second one is the
/// one that decided its shape.
/// </para>
/// <para>
/// What it used to say was a count and the threshold that produced it — and the threshold is
/// printed beside the slider that set it, so the only new fact was a count that had not moved. The
/// versions after that were worse in a different way: they were accurate diffs written in the
/// vocabulary of the source, naming boundaries and decibels and column headers. A status line
/// arrives once, unbidden, and is read by somebody who has not seen any of that.
/// </para>
/// <para>
/// So every line here states what is on screen in ordinary words and names the next thing to do,
/// and no line carries a decibel figure or the word "boundary".
/// </para>
/// </remarks>
public sealed class CdProposalReadoutTests
{
    /// <summary>Nothing may quote a level; the slider prints its own, six inches away.</summary>
    private static void PlainEnough(string line)
    {
        Assert.DoesNotContain(" dB", line, StringComparison.Ordinal);
        Assert.DoesNotContain("boundar", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("threshold", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SOURCE", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AFirstPassNamesTheNextThingToDo()
    {
        string line = CdTransfer.DescribeProposal(3, previous: 0, double.NaN);
        Assert.Equal("3 tracks found. Select one and press Preview Track to hear where it starts.", line);
        PlainEnough(line);
    }

    [Theory]
    [InlineData(3, 1, "Now 3 tracks - there was 1.")]
    [InlineData(2, 5, "Now 2 tracks - there were 5.")]
    public void AChangedCountSaysWhereItCameFrom(int proposed, int previous, string expected)
    {
        string line = CdTransfer.DescribeProposal(proposed, previous, double.NaN);
        Assert.StartsWith(expected, line, StringComparison.Ordinal);
        Assert.Contains("Preview each one", line, StringComparison.Ordinal);
        PlainEnough(line);
    }

    [Fact]
    public void AnotherPassThatChangesNothingSaysSoAndStops()
    {
        string line = CdTransfer.DescribeProposal(3, previous: 3, 0);
        Assert.Equal("Same 3 tracks, in the same places.", line);
        PlainEnough(line);
    }

    /// <summary>
    /// The case the second report came from. Three tracks before and three after is not the same
    /// answer: measured on a real three-track side, −45 dB and −30 dB both give three tracks with
    /// the splits 7.6 s apart, which is far enough to cut into the end of a song. Reporting only
    /// the count there is reporting the one number that did not move.
    /// </summary>
    [Fact]
    public void TheSameCountReportsWhichWayTheSplitsWentAndWhatThatCosts()
    {
        string earlier = CdTransfer.DescribeProposal(3, previous: 3, -7.55);
        Assert.Equal(
            "Still 3 tracks, but the splits moved up to 7.6 s earlier and may now cut into the end " +
            "of a song. Preview them to check.", earlier);

        // Tightening the threshold moves them the other way, where they eat the song after the gap
        // rather than the one before it.
        string later = CdTransfer.DescribeProposal(3, previous: 3, 7.55);
        Assert.Contains("7.6 s later", later, StringComparison.Ordinal);
        Assert.Contains("start of a song", later, StringComparison.Ordinal);

        PlainEnough(earlier);
        PlainEnough(later);
    }

    /// <summary>A move past a minute reads as a timecode rather than as "185.0 s".</summary>
    [Fact]
    public void ALongMoveIsWrittenAsATimecode()
    {
        Assert.Contains("3:05 earlier", CdTransfer.DescribeProposal(3, 3, -185), StringComparison.Ordinal);
        Assert.Contains("59.5 s earlier", CdTransfer.DescribeProposal(3, 3, -59.5), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one outcome with nothing to rename, reorder or preview, so it is the one that has to say
    /// which way the slider goes. Right is the laxer test and therefore the direction that finds
    /// more gaps — which reads backwards, since right is the number closer to zero.
    /// </summary>
    [Fact]
    public void OneTrackIsTheCaseThatSaysWhichWayToMoveTheSlider()
    {
        const string expected =
            "No gaps found - this is all one track. Drag Quiet below to the right, then Analyze again.";
        Assert.Equal(expected, CdTransfer.DescribeProposal(1, previous: 0, double.NaN));

        // However many there were before. Guarding this on the previous count sent a side that
        // collapsed from three tracks to one away with "Preview each one to check where it starts",
        // which is advice about a list that no longer exists.
        Assert.Equal(expected, CdTransfer.DescribeProposal(1, previous: 3, double.NaN));

        PlainEnough(expected);
        Assert.DoesNotContain("left", expected, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyProposalDoesNotClaimToHaveFoundTracks()
    {
        Assert.Equal("No tracks were proposed.", CdTransfer.DescribeProposal(0, previous: 3, double.NaN));
    }
}

/// <summary>
/// The other three lines this window writes, brought into the same voice as the analysis one.
/// </summary>
/// <remarks>
/// They were the ones left behind by the readout rewrite, and all three named things by what they
/// are called in the source rather than by what the user is looking at: a track came "off the
/// unclaimed stretch", a split wanted the "boundary" fine-tuned, and Sync Regions "Synchronized 3
/// arranged track region(s)" — which describes the operation rather than what the user now has.
/// </remarks>
public sealed class CdListActionReadoutTests
{
    private static void PlainEnough(string line)
    {
        Assert.DoesNotContain("unclaimed", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("boundar", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("region(s)", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synchroniz", line, StringComparison.OrdinalIgnoreCase);
        // A count written "1 tracks" is the same fault by another route.
        Assert.DoesNotContain("1 tracks", line, StringComparison.Ordinal);
    }

    /// <remarks>
    /// One fact rather than a theory because <c>TrackOrigin</c> is internal to the window, and an
    /// internal type cannot be a parameter of a public test method.
    /// </remarks>
    [Fact]
    public void AddTrackSaysWhereTheTrackCameFromInWordsRatherThanInTheSourcesTerms()
    {
        (CdTransferDialog.TrackOrigin Origin, string Expected)[] cases =
        [
            (CdTransferDialog.TrackOrigin.Selection, "taken from what you had selected"),
            (CdTransferDialog.TrackOrigin.RestOfGap, "filling the stretch no track was using"),
            (CdTransferDialog.TrackOrigin.StartOfGap, "off the front of the stretch no track was using"),
        ];

        foreach ((CdTransferDialog.TrackOrigin origin, string expected) in cases)
        {
            string line = CdTransferDialog.DescribeAddedTrack(3, 0, 180, origin);
            Assert.StartsWith("Track 03 added, 0:00 to 3:00, ", line, StringComparison.Ordinal);
            Assert.Contains(expected, line, StringComparison.Ordinal);
            Assert.Contains("SOURCE IN and SOURCE OUT", line, StringComparison.Ordinal);
            PlainEnough(line);
        }
    }

    /// <summary>
    /// The In and Out cells carry milliseconds because a split point is exact. A sentence about one
    /// does not need them, and "Split at 00:00:21.249" is the readout talking to itself.
    /// </summary>
    [Fact]
    public void ASplitNamesBothHalvesAndDropsTheMilliseconds()
    {
        string line = CdTransferDialog.DescribeSplitTrack(2, 21.249, 3);
        Assert.StartsWith("Split at 0:21 - track 02 ends there and track 03 starts.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("21.2", line, StringComparison.Ordinal);
        Assert.Contains("SOURCE IN and SOURCE OUT", line, StringComparison.Ordinal);
        PlainEnough(line);
    }

    /// <summary>
    /// "Too short to divide into two valid CD tracks" is a refusal. Naming the number a disc
    /// actually requires is an explanation, and it is the difference between the two.
    /// </summary>
    [Fact]
    public void RefusingToDivideSaysWhatTheRuleIs()
    {
        string line = CdTransferDialog.DescribeTooShort(2, "divide");
        Assert.Contains("too short to divide", line, StringComparison.Ordinal);
        Assert.Contains("4 seconds a CD track has to run for", line, StringComparison.Ordinal);
        Assert.Contains("split", CdTransferDialog.DescribeTooShort(2, "split"), StringComparison.Ordinal);
        PlainEnough(line);
    }

    /// <summary>
    /// Add Track divides an existing row when the recording is already tiled, which is Split's
    /// operation at a fixed offset — but the user pressed Add Track, and being answered "Split at
    /// 0:31" describes the code rather than the press.
    /// </summary>
    [Fact]
    public void AddTrackSaysATrackWasAddedEvenWhenItGotThereByDividing()
    {
        string line = CdTransferDialog.DescribeAddedByDividing(2, 31.87, 3);
        Assert.StartsWith("Track 03 added by dividing track 02 at 0:31.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Split", line, StringComparison.Ordinal);
        PlainEnough(line);
    }

    [Fact]
    public void SyncRegionsSaysWhatTheUserNowHasRatherThanWhatItDid()
    {
        Assert.Equal("The regions on the waveform already match this track list.",
            CdTransferDialog.DescribeRegionSync(3, 0, changed: false));

        Assert.Equal("Marked 3 tracks on the waveform.",
            CdTransferDialog.DescribeRegionSync(3, 0, changed: true));

        // Singular and plural are written out, because "1 other region(s)" is the old voice.
        Assert.Equal("Marked 1 track on the waveform. One other region was left alone.",
            CdTransferDialog.DescribeRegionSync(1, 1, changed: true));

        Assert.Equal("Marked 3 tracks on the waveform. 2 other regions were left alone.",
            CdTransferDialog.DescribeRegionSync(3, 2, changed: true));

        PlainEnough(CdTransferDialog.DescribeRegionSync(3, 2, changed: true));
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
    public void EveryStatusWordingFitsTheStatusLine()
    {
        AppSettings.AppDataDir = _sandbox;
        string[] wordings =
        [
            CdTransfer.DescribeProposal(3, 0, double.NaN),
            CdTransfer.DescribeProposal(3, 1, double.NaN),
            CdTransfer.DescribeProposal(12, 3, double.NaN),
            CdTransfer.DescribeProposal(3, 3, 0),
            CdTransfer.DescribeProposal(3, 3, -7.55),
            CdTransfer.DescribeProposal(99, 99, 185),
            CdTransfer.DescribeProposal(1, 0, double.NaN),
            CdTransferDialog.DescribeAddedTrack(3, 0, 180, CdTransferDialog.TrackOrigin.StartOfGap),
            CdTransferDialog.DescribeAddedTrack(99, 3599, 3720, CdTransferDialog.TrackOrigin.Selection),
            CdTransferDialog.DescribeSplitTrack(98, 3599, 99),
            CdTransferDialog.DescribeAddedByDividing(98, 3599, 99),
            CdTransferDialog.DescribeTooShort(99, "divide"),
            CdTransferDialog.DescribeRegionSync(99, 98, changed: true),
            CdTransferDialog.DescribeRegionSync(1, 1, changed: true),
            CdTransferDialog.DescribeRegionSync(3, 0, changed: false),
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
