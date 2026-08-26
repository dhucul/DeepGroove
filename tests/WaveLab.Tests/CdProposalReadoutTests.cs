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
/// The line above the footer, which reports whether the plan can be burned at all.
/// </summary>
/// <remarks>
/// The last readout in this window still in the old voice, and the one that made the fault visible
/// as a layout problem rather than only as a wording one: "Program length: 79:58 across 99
/// track(s), aligned to CD sectors. Lead-out at 79:58:00; 99 of 99 ISRC(s) set." wanted 536 px in a
/// column holding 475, so the tail a DDP user most needs — how many catalogue numbers are actually
/// set — was the part being cut off. Saying the same thing plainly is what makes it fit.
/// </remarks>
public sealed class CdValidationReadoutTests
{
    private static List<CdPlanIssue> Issues(int rate, params (double Start, double End)[] tracks)
    {
        int Frames(double seconds) => (int)Math.Round(seconds * rate);
        var plan = tracks
            .Select((t, i) => new CdTrackPlan(Frames(t.Start), Frames(t.End), $"Track {i + 1:00}"))
            .ToList();
        int length = plan.Count == 0 ? rate : plan.Max(p => p.SourceEnd);
        return CdTransfer.Validate(plan, rate, length);
    }

    private static string Say(List<CdPlanIssue> issues, CdPlanIssueSeverity severity) =>
        issues.First(i => i.Severity == severity).Message;

    private static void PlainEnough(string line)
    {
        Assert.DoesNotContain("track(s)", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ISRC(s)", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sector-aligned", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source range", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Program length", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The line a valid plan carries, which is on screen the whole time the window is usable — and
    /// the one the DDP tail is appended to, so its length is the constraint on both.
    /// </summary>
    [Fact]
    public void AValidPlanSaysWhatIsOnTheDiscInFiveWords()
    {
        string line = Say(Issues(44_100, (0, 60), (60, 130), (130, 200)), CdPlanIssueSeverity.Information);
        Assert.Equal("3 tracks, 3:20 on the disc.", line);
        PlainEnough(line);

        Assert.Equal("1 track, 1:00 on the disc.",
            Say(Issues(44_100, (0, 60)), CdPlanIssueSeverity.Information));
    }

    /// <summary>
    /// A refusal has to say what the rule is, the same way Add Track's does. These reach the Export
    /// message box as well as this line, so they are read outside the window too.
    /// </summary>
    [Fact]
    public void ARefusalNamesTheRuleItIsEnforcing()
    {
        string tooShort = Say(Issues(44_100, (0, 2), (2, 60)), CdPlanIssueSeverity.Error);
        Assert.Contains("comes out 2.0 s long on the disc", tooShort, StringComparison.Ordinal);
        Assert.Contains("at least 4 seconds", tooShort, StringComparison.Ordinal);
        PlainEnough(tooShort);

        string empty = Say(Issues(44_100, (5, 5)), CdPlanIssueSeverity.Error);
        Assert.Contains("covers no audio", empty, StringComparison.Ordinal);
        Assert.Contains("SOURCE IN and SOURCE OUT", empty, StringComparison.Ordinal);
        PlainEnough(empty);
    }

    /// <summary>
    /// Over 74 minutes is a question about the blank disc, not about the plan. Note the duration:
    /// <c>FormatDuration</c> goes to h:mm:ss past the hour, so a 75-minute programme reads 1:15:00
    /// rather than 75:00 — which is why the sentence beside it says "74 minutes" in words.
    /// </summary>
    [Fact]
    public void ALongProgrammeAsksAboutTheBlankDisc()
    {
        string line = Say(Issues(8_000, (0, 75 * 60)), CdPlanIssueSeverity.Warning);
        Assert.Equal("These tracks run 1:15:00 on the disc. Check your blank discs hold more than 74 minutes.", line);
        PlainEnough(line);
    }

    [Fact]
    public void PastEightyMinutesSaysWhatToDoAboutIt()
    {
        string line = Say(Issues(8_000, (0, 81 * 60)), CdPlanIssueSeverity.Error);
        Assert.Contains("These tracks run 1:21:00 on the disc", line, StringComparison.Ordinal);
        Assert.Contains("A CD holds at most 1:20:00", line, StringComparison.Ordinal);
        Assert.Contains("shorten one or take one out", line, StringComparison.Ordinal);
        PlainEnough(line);
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
    /// <summary>
    /// The two forms of the validation line at their widest — 99 tracks filling an 80-minute disc.
    /// Written out rather than produced through <c>Validate</c> so the probe measures a fixed worst
    /// case; <see cref="CdValidationReadoutTests"/> is what pins that these are the real wordings.
    /// </summary>
    private static class ValidationLine
    {
        public const string Plain = "99 tracks, 79:58 on the disc.";
        public const string Ddp = Plain + " Lead-out at 79:58:00. 99 of 99 ISRCs set.";
    }

    private readonly string _original = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppSettings.AppDataDir = _original;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    /// <summary>A sweep result built by hand, so the probe measures a fixed worst case.</summary>
    private static CdSplitSweep Swept(int tracks, double low, double high, int? runnerUpTracks)
    {
        var best = new CdSplitCandidate(tracks, low, high, Math.Round((low + high) / 2), 1.25, [0, 1]);
        List<CdSplitCandidate> all = [best];
        if (runnerUpTracks is int other)
            all.Add(new CdSplitCandidate(other, -34, -28, -31, 1.25, [0, 1]));
        return new CdSplitSweep(all, best, -70, -25, false);
    }

    private static string SweptLine(int tracks, double low, double high, int? runnerUp) =>
        CdTransfer.DescribeSweep(Swept(tracks, low, high, runnerUp), null);

    private static CdSplitSweep Unreachable() => new(
        [new CdSplitCandidate(5, -34, -28, -31, 1.25, [0, 1]),
         new CdSplitCandidate(3, -55, -40, -48, 1.25, [0, 1]),
         new CdSplitCandidate(1, -70, -56, -63, 1.25, [0, 1])],
        null, -70, -25, false);

    private static CdSplitSweep Relaxed()
    {
        var best = new CdSplitCandidate(4, -40, -36, -38, CdTransfer.RelaxedMinimumGapSeconds, [0, 1]);
        return new CdSplitSweep([best], best, -70, -25, true);
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
            SweptLine(3, -55, -40, null),
            SweptLine(3, -55, -40, 5),
            SweptLine(99, -33, -33, null),
            CdTransfer.DescribeSweep(Unreachable(), 6),
            CdTransfer.DescribeSweep(Relaxed(), null),
            CdTransfer.DescribeSweep(new CdSplitSweep([], null, -70, -25, false), null),
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

    /// <summary>
    /// The AUTO SPLIT row after Find Tracks and the track-count box were added to it. That row was
    /// four controls and is now seven, sharing one line at a window width that cannot change.
    /// </summary>
    [Fact]
    public void TheAutoSplitRowHoldsEverythingItGained()
    {
        var report = new List<string>();
        bool clipped = false;

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
                foreach (string name in
                    (string[])["thresholdSlider", "thresholdText", "trackCountBox", "findTracksBtn", "analyzeBtn"])
                {
                    var element = (FrameworkElement)window.FindName(name)!;
                    double given = element.ActualWidth;
                    double wanted = element.DesiredSize.Width - element.Margin.Left - element.Margin.Right;
                    report.Add($"{name,-16} {given,6:F0} px given, {wanted:F0} px wanted");
                    if (wanted > given + 0.5) clipped = true;
                }
            });
        });

        foreach (string line in report) output.WriteLine(line);
        Assert.False(clipped, "something in the AUTO SPLIT row is cut: " + string.Join(" | ", report));
    }

    /// <summary>
    /// "Sync Regions" became "Save Track List", which is four characters longer in a row of six
    /// buttons that shares its width with the validation line beside them.
    /// </summary>
    /// <remarks>
    /// The button itself cannot be cut — it is in a <c>StackPanel</c>, which measures its children
    /// unbounded — so what a wider label actually costs is taken out of the <c>*</c> column holding
    /// the validation text, which trims. That is the measurement worth making, and it is not the
    /// one the button's own size answers.
    /// </remarks>
    [Fact]
    public void TheRenamedButtonFitsAndLeavesTheValidationLineItsRoom()
    {
        double button = 0, wanted = 0, validation = 0, plain = 0, longest = 0;
        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            main.AddDocument(new AudioDocument([new float[44_100 * 30], new float[44_100 * 30]], 44_100, 16)
            { Title = "Side A.wav" });
            DocumentViewModel document = main.ActiveDocument!;
            document.Regions.Add(new NamedRegion { Name = "A", Start = 0, End = 44_100 * 30, CdTrackOrder = 1 });

            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                Wpf.Pump();
                var save = (Button)window.FindName("saveRegionsBtn")!;
                var line = (TextBlock)window.FindName("validationText")!;
                button = save.ActualWidth;
                wanted = save.DesiredSize.Width;
                validation = line.ActualWidth;

                double Wants(string text)
                {
                    var probe = new TextBlock
                    {
                        Text = text,
                        FontFamily = line.FontFamily,
                        FontSize = line.FontSize,
                        FontStyle = line.FontStyle,
                        FontWeight = line.FontWeight,
                        FontStretch = line.FontStretch,
                    };
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    return probe.DesiredSize.Width;
                }

                // What a WAV+CUE package shows, which is the overwhelming majority of the time.
                plain = Wants(ValidationLine.Plain);
                // The DDP form appends the lead-out and the ISRC tally to it.
                longest = Wants(ValidationLine.Ddp);
            });
        });

        output.WriteLine($"Save Track List: {button:F0} px given, {wanted:F0} px wanted");
        output.WriteLine($"validation line: {validation:F0} px given; " +
            $"WAV+CUE wording wants {plain:F0} px, the DDP one {longest:F0} px");

        Assert.True(button > 0, "the button row was not laid out");
        // DesiredSize includes the 6 px left margin that ActualWidth does not - the trap this repo
        // already records for the monitor bar and the spectral scale switch.
        Assert.True(wanted - 6 <= button + 0.5,
            $"Save Track List wants {wanted:F0} px and was given {button:F0} px, so its label is cut");
        Assert.True(plain <= validation,
            $"the ordinary validation wording wants {plain:F0} px and the line has {validation:F0} px");
        // This one did not fit until the wording was rewritten: it wanted 536 px against 496 before
        // the button was renamed and 475 after, so it had been trimming all along.
        Assert.True(longest <= validation,
            $"the DDP validation wording wants {longest:F0} px and the line has {validation:F0} px");
    }
}
