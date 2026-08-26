using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Prepare Audio CD as a modeless window over a live document: adding a track by hand, and keeping
/// up with the file, the rack and the tab list while all three can still change underneath it.
/// </summary>
/// <remarks>
/// <para>
/// What these close is a window that could only ever be told about the outside world once. It was
/// modal, so the selection Add Track takes and the cursor Split cuts at were frozen at whatever they
/// were before it opened — pressing "Use Selection" twice added the same range twice, and there was
/// no other way to put a track in the list by hand at all.
/// </para>
/// <para>
/// In the shared UI thread and the app-settings collection for <see cref="MainViewModel"/>'s sake,
/// the same reasons <see cref="CloseAllTests"/> gives.
/// </para>
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class CdTransferDialogTests : IDisposable
{
    private const int Rate = 44_100;

    /// <summary>
    /// For the tests that need minutes rather than seconds of timeline. Add Track's block is defined
    /// in seconds, so what it does is a function of duration alone — and eight minutes here is 3.8 M
    /// samples instead of the 42 M a real side would be.
    /// </summary>
    private const int Slow = 8_000;

    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public CdTransferDialogTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private static int Seconds(double value) => (int)Math.Round(value * Rate);

    /// <summary>
    /// A twenty-second transfer with regions already on it. Seeding at least one region matters:
    /// the window analyses for track gaps on load only when it opens with an empty list, and that
    /// analysis is asynchronous, so a test that wants a known list must not provoke it.
    /// </summary>
    private static DocumentViewModel Open(MainViewModel main, params (double Start, double End)[] regions) =>
        Open(main, Rate, 20, regions);

    private static DocumentViewModel Open(
        MainViewModel main, int rate, double seconds, params (double Start, double End)[] regions)
    {
        int frames = (int)Math.Round(seconds * rate);
        main.AddDocument(new AudioDocument(
            [new float[frames], new float[frames]], rate, 16) { Title = "Side A.wav" });
        DocumentViewModel document = main.ActiveDocument!;
        foreach ((double start, double end) in regions)
        {
            document.Regions.Add(new NamedRegion
            {
                Name = $"Track {document.Regions.Count + 1:00}",
                Start = (int)Math.Round(start * rate),
                End = (int)Math.Round(end * rate),
                CdTrackOrder = document.Regions.Count + 1,
            });
        }
        return document;
    }

    private static void Click(Window window, string name) =>
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IReadOnlyList<CdTrackPlan> Plans(Window window)
    {
        var list = (ListBox)window.FindName("trackList");
        // The row type is private to the window — deliberately, it is a view model for one ListBox —
        // but the plan it carries is the public record the exporter is handed, so that is what is
        // read back here rather than the formatted text of the cells.
        return list.Items.Cast<object>()
            .Select(row => (CdTrackPlan)row.GetType().GetProperty("Plan")!.GetValue(row)!)
            .ToList();
    }

    /// <summary>
    /// The direct expression of "this part is a track". It is taken verbatim, and because the window
    /// is modeless the selection it reads is the one on the waveform now.
    /// </summary>
    [Fact]
    public void AddTrackTakesTheCurrentSelection()
    {
        IReadOnlyList<CdTrackPlan> plans = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, (0, 5));
            document.SetSelection(Seconds(12), Seconds(18));

            IReadOnlyList<CdTrackPlan> result = [];
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                Click(window, "addBtn");
                Wpf.Pump();
                result = Plans(window);
            });
            return result;
        });

        Assert.Equal(2, plans.Count);
        Assert.Equal((Seconds(12), Seconds(18)), (plans[1].SourceStart, plans[1].SourceEnd));
    }

    /// <summary>
    /// A stretch shorter than the block one press takes is claimed whole. Splitting it would leave a
    /// remainder under the CD minimum behind it, which is not a track and would need a second press
    /// to get rid of.
    /// </summary>
    [Fact]
    public void AddTrackClaimsAShortUnclaimedStretchWhole()
    {
        IReadOnlyList<CdTrackPlan> plans = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, (0, 5), (8, 12));
            document.ClearSelection();

            IReadOnlyList<CdTrackPlan> result = [];
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                // Selecting the first row also fixes where the new one lands: after it, not at the end.
                ((ListBox)window.FindName("trackList")).SelectedIndex = 0;
                Wpf.Pump();
                Click(window, "addBtn");
                Wpf.Pump();
                result = Plans(window);
            });
            return result;
        });

        // Gaps are 5–8 s and 12–20 s; the longer one wins.
        Assert.Equal(3, plans.Count);
        Assert.Equal((Seconds(12), Seconds(20)), (plans[1].SourceStart, plans[1].SourceEnd));
        Assert.Equal((Seconds(8), Seconds(12)), (plans[2].SourceStart, plans[2].SourceEnd));
    }

    /// <summary>
    /// The point of taking a block rather than the whole stretch: the button has to still do
    /// something on the second press, and on the third. A side is one unclaimed stretch, so claiming
    /// all of it would have made the first press the only one that worked — which is what the old
    /// Use Selection did, under a different name.
    /// </summary>
    [Fact]
    public void PressingAddTrackRepeatedlyBuildsTheListInSourceOrder()
    {
        IReadOnlyList<CdTrackPlan> plans = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            // Eight minutes at a low rate: long enough to hold several three-minute blocks without
            // allocating a real side's worth of samples.
            DocumentViewModel document = Open(main, Slow, 480, (0, 60));
            document.ClearSelection();

            IReadOnlyList<CdTrackPlan> result = [];
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                ((ListBox)window.FindName("trackList")).SelectedIndex = 0;
                Wpf.Pump();
                for (int press = 0; press < 3; press++)
                {
                    Click(window, "addBtn");
                    Wpf.Pump();
                }
                result = Plans(window);
            });
            return result;
        });

        Assert.Equal(4, plans.Count);
        Assert.Equal((0, Slow * 60), (plans[0].SourceStart, plans[0].SourceEnd));
        Assert.Equal((Slow * 60, Slow * 240), (plans[1].SourceStart, plans[1].SourceEnd));
        Assert.Equal((Slow * 240, Slow * 420), (plans[2].SourceStart, plans[2].SourceEnd));
        // 420–480 s is a minute, under the three-minute block, so the last press takes what is left
        // rather than leaving a scrap behind it.
        Assert.Equal((Slow * 420, Slow * 480), (plans[3].SourceStart, plans[3].SourceEnd));
    }

    /// <summary>
    /// The case the whole button turns on, and the one both earlier versions of it got wrong. The
    /// opening analysis proposes boundaries covering the entire recording, so in the ordinary flow
    /// there is never any unclaimed space — a search for some found nothing and reported that
    /// everything was claimed, which with a one-gap analysis is a list of two and no way to a third.
    /// Another track has to come out of an existing one.
    /// </summary>
    [Fact]
    public void AddTrackDividesTheSelectedTrackWhenTheRecordingIsAlreadyTiled()
    {
        IReadOnlyList<CdTrackPlan> plans = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            // One region over the whole eight minutes: what Analyze leaves behind on a side with no
            // gap it can find, and the state the report came from.
            DocumentViewModel document = Open(main, Slow, 480, (0, 480));
            document.ClearSelection();

            IReadOnlyList<CdTrackPlan> result = [];
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                ((ListBox)window.FindName("trackList")).SelectedIndex = 0;
                Wpf.Pump();
                for (int press = 0; press < 3; press++)
                {
                    Click(window, "addBtn");
                    Wpf.Pump();
                }
                result = Plans(window);
            });
            return result;
        });

        Assert.Equal(4, plans.Count);
        Assert.Equal((0, Slow * 180), (plans[0].SourceStart, plans[0].SourceEnd));
        Assert.Equal((Slow * 180, Slow * 360), (plans[1].SourceStart, plans[1].SourceEnd));
        // Two minutes left, which cannot give a three-minute block and a valid remainder, so the
        // third press falls back to the midpoint — the same clamp Split uses.
        Assert.Equal((Slow * 360, Slow * 420), (plans[2].SourceStart, plans[2].SourceEnd));
        Assert.Equal((Slow * 420, Slow * 480), (plans[3].SourceStart, plans[3].SourceEnd));
    }

    /// <summary>
    /// Two CD tracks need eight seconds between them. Under that the row cannot be divided, and the
    /// button says which row and why rather than doing nothing.
    /// </summary>
    [Fact]
    public void AddTrackSaysSoWhenThereIsNothingLeftLongEnoughToDivide()
    {
        (int tracks, string status) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, Rate, 6, (0, 6));
            document.ClearSelection();

            (int Tracks, string Status) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                Click(window, "addBtn");
                Wpf.Pump();
                result = (Plans(window).Count, ((TextBlock)window.FindName("statusText")).Text);
            });
            return result;
        });

        Assert.Equal(1, tracks);
        Assert.Contains("too short to divide", status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A track range is anchored to the timeline exactly as a marker or a region is. An edit made
    /// while this window is open therefore moves it, instead of leaving it pointing at a different
    /// piece of music than the one the user chose.
    /// </summary>
    [Fact]
    public void ATrackRangeMovesWithAnEditMadeWhileTheWindowIsOpen()
    {
        (CdTrackPlan before, CdTrackPlan after) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, (5, 10));

            (CdTrackPlan Before, CdTrackPlan After) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                result.Before = Plans(window)[0];

                // One second of silence inserted at the head of the file.
                document.Doc.ReplaceRange(0, 0,
                    [new float[Seconds(1)], new float[Seconds(1)]], "Insert Silence");
                Wpf.Pump();
                result.After = Plans(window)[0];
            });
            return result;
        });

        Assert.Equal((Seconds(5), Seconds(10)), (before.SourceStart, before.SourceEnd));
        Assert.Equal((Seconds(6), Seconds(11)), (after.SourceStart, after.SourceEnd));
    }

    /// <summary>
    /// The checkbox is the one master rack, not a private copy of it, so a bypass pressed in the
    /// main window while this is open reaches it — and export cannot promise a rack that is off.
    /// </summary>
    [Fact]
    public void TheRackCheckboxFollowsTheMainWindow()
    {
        (bool? enabled, bool? bypassed) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, (0, 5));

            (bool? Enabled, bool? Bypassed) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                var check = (CheckBox)window.FindName("renderRackCheck");
                main.Master.RackEnabled = true;
                Wpf.Pump();
                result.Enabled = check.IsChecked;

                main.Master.RackEnabled = false;
                Wpf.Pump();
                result.Bypassed = check.IsChecked;
            });
            return result;
        });

        Assert.True(enabled);
        Assert.False(bypassed);
    }

    /// <summary>
    /// Closing the tab while an operation is in flight goes through the ordinary cancel-then-close
    /// path rather than forcing the window down on top of it.
    /// </summary>
    /// <remarks>
    /// It did not. The handler cleared <c>_busy</c> so that <c>OnDialogClosing</c> would let the
    /// close through, which took the window down while an export was still unwinding — and a write
    /// that had already finished then tried to parent its "CD Package Ready" dialog to a dead owner,
    /// turning a package sitting correctly on disk into a "CD package failed" box. The window is
    /// modeless now, so reaching the tab strip during an export is a thing a user can do.
    /// </remarks>
    [Fact]
    public void ClosingTheDocumentMidOperationLetsTheOperationUnwindFirst()
    {
        (bool vetoedFirst, bool closed) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, (0, 5));

            (bool Vetoed, bool Closed) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                // Analyze is the operation available without a folder picker; it sets the same
                // _busy the export does, and OnDialogClosing does not distinguish them.
                ((Button)window.FindName("analyzeBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                main.CloseAllCommand.Execute(null);

                // Still up on the first pass: the close is deferred, not refused.
                result.Vetoed = window.IsVisible;
                long deadline = Environment.TickCount64 + 20_000;
                while (window.IsVisible && Environment.TickCount64 < deadline) Wpf.Pump();
                result.Closed = !window.IsVisible;
            });
            return result;
        });

        Assert.True(vetoedFirst, "the window went down before the operation had unwound");
        Assert.True(closed, "the deferred close was never re-issued");
    }

    /// <summary>Closing the file closes the window arranging it; there is nothing left to prepare.</summary>
    [Fact]
    public void ClosingTheDocumentClosesTheWindow()
    {
        bool visible = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, (0, 5));

            bool stillOpen = true;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                main.CloseAllCommand.Execute(null);
                Wpf.Pump();
                stillOpen = window.IsVisible;
            });
            return stillOpen;
        });

        Assert.False(visible);
    }

    /// <summary>
    /// Modeless windows can be asked for twice. The second Prepare Audio CD raises the list already
    /// being arranged rather than standing a rival one up beside it, which would have two windows
    /// writing regions over each other.
    /// </summary>
    [Fact]
    public void AskingTwiceRaisesTheWindowThatIsAlreadyOpen()
    {
        (bool same, bool closed) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main, (0, 5));

            CdTransferDialog first = CdTransferDialog.ShowFor(document, main, null);
            Wpf.Pump();
            bool isSame = ReferenceEquals(first, CdTransferDialog.ShowFor(document, main, null));

            // Closing is part of the test rather than cleanup after it. This is the one place here
            // that goes through the real ShowFor, so the window is shown and activated rather than
            // parked offscreen by Wpf.Show — and it holds subscriptions to the view model this block
            // is about to dispose. One left open takes those, and the focus, into the next test.
            first.Close();
            long deadline = Environment.TickCount64 + 15_000;
            while (first.IsVisible && Environment.TickCount64 < deadline) Wpf.Pump();
            return (isSame, !first.IsVisible);
        });

        Assert.True(same);
        Assert.True(closed, "the CD window outlived the test that opened it");
    }

    // ── Analyze ──────────────────────────────────────────────────

    /// <summary>
    /// A side with two sustained quiet gaps in it, carrying a real −60 dBFS floor in those gaps
    /// rather than digital silence, and a three-second fade into each one.
    /// </summary>
    /// <remarks>
    /// Both details are what make the threshold testable. A gap holding nothing at all is quieter
    /// than every setting of the slider, so they would all agree; and without a fade the edge of
    /// each gap is a step, so the boundary lands in the same place whatever counts as quiet. Real
    /// sides have both — measured on three transfers butted together, −45 dB and −30 dB propose the
    /// same three tracks with the boundaries 7.6 s apart, which is the fade being read as the gap.
    /// </remarks>
    private static DocumentViewModel OpenSideWithGaps(
        MainViewModel main, params (double Start, double End)[] regions)
    {
        const int seconds = 120;
        const double fade = 3;
        int frames = seconds * Rate;
        var left = new float[frames];
        var right = new float[frames];
        var noise = new Random(11);
        for (int i = 0; i < frames; i++)
        {
            double second = i / (double)Rate;
            bool music = second < 40 || (second >= 45 && second < 85) || second >= 90;
            double level = 0.001;
            if (music)
            {
                // Full level until the last three seconds before a gap, then down to the floor.
                double toGap = second < 40 ? 40 - second : second < 85 ? 85 - second : double.MaxValue;
                level = toGap >= fade ? 0.3 : 0.001 + (0.3 - 0.001) * (toGap / fade);
            }
            float value = (float)((noise.NextDouble() * 2 - 1) * level);
            left[i] = value;
            right[i] = value;
        }

        main.AddDocument(new AudioDocument([left, right], Rate, 16) { Title = "Side A.wav" });
        DocumentViewModel document = main.ActiveDocument!;
        foreach ((double start, double end) in regions)
        {
            document.Regions.Add(new NamedRegion
            {
                Name = $"Track {document.Regions.Count + 1:00}",
                Start = (int)Math.Round(start * Rate),
                End = (int)Math.Round(end * Rate),
                CdTrackOrder = document.Regions.Count + 1,
            });
        }
        return document;
    }

    /// <summary>Pump until the background gap analysis has landed, or give up loudly.</summary>
    private static void SettleAnalysis(Window window)
    {
        long deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            Wpf.Pump(DispatcherPriority.SystemIdle);
            if (((Button)window.FindName("analyzeBtn")).IsEnabled &&
                !((TextBlock)window.FindName("statusText")).Text
                    .StartsWith("Analyzing", StringComparison.Ordinal))
                return;
            Thread.Sleep(10);
        }

        Assert.Fail("the gap analysis did not finish.");
    }

    /// <summary>The five buttons whose enabled state comes from the list selection.</summary>
    private static bool[] RowButtonsEnabled(Window window) =>
    [
        ((Button)window.FindName("previewBtn")).IsEnabled,
        ((Button)window.FindName("removeBtn")).IsEnabled,
        ((Button)window.FindName("splitBtn")).IsEnabled,
        ((Button)window.FindName("upBtn")).IsEnabled,
        ((Button)window.FindName("downBtn")).IsEnabled,
    ];

    /// <summary>
    /// The row type is private to the window, so its editable title is reached the way the cell
    /// binding does. It is deliberately not <c>Plan.Title</c>: the plan carries the title the row
    /// was built with, and <c>ToPlan</c> is where the edited one is merged in.
    /// </summary>
    private static void SetRowTitle(object row, string title) =>
        row.GetType().GetProperty("Title")!.SetValue(row, title);

    private static string RowTitle(object row) =>
        (string)row.GetType().GetProperty("Title")!.GetValue(row)!;

    /// <summary>
    /// Reported as "Analyze does nothing — a quick flash of the box and nothing else", and the flash
    /// is the ListBox being rebuilt. Preview, Remove, Split, ▲ and ▼ all read their enabled state
    /// off the list selection, and rebuilding the collection clears it — so every press handed back
    /// a list with five of the buttons under it dead, and nothing said why.
    /// </summary>
    [Fact]
    public void AnalyzeLeavesARowSelectedSoTheButtonsBelowTheListStayLive()
    {
        (int tracks, bool[] before, bool[] after, int selected) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = OpenSideWithGaps(main, (0, 120));

            (int, bool[], bool[], int) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                var list = (ListBox)window.FindName("trackList");
                list.SelectedIndex = 0;
                Wpf.Pump();
                bool[] before = RowButtonsEnabled(window);

                Click(window, "analyzeBtn");
                SettleAnalysis(window);
                result = (list.Items.Count, before, RowButtonsEnabled(window), list.SelectedIndex);
            });
            return result;
        });

        Assert.Equal(3, tracks);
        Assert.All(before, Assert.True);
        Assert.All(after, Assert.True);
        Assert.Equal(0, selected);
    }

    /// <summary>
    /// The window analyses on load, so the ordinary second press proposes exactly the boundaries
    /// already listed. Rebuilding the rows for that threw away every title and ISRC typed since —
    /// for nothing, because the tracks are the same tracks. The same number of tracks now updates
    /// the ranges in place instead, so a nudge of the threshold keeps what has been typed even when
    /// the boundaries do move.
    /// </summary>
    [Fact]
    public void AnalyzeThatFindsTheSameBoundariesKeepsWhatWasTypedIntoTheRows()
    {
        (int tracks, string title, string status) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            // No regions, so the constructor's own analysis is what fills the list — the state a
            // user is in when they reach for the button.
            DocumentViewModel document = OpenSideWithGaps(main);

            (int, string, string) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                SettleAnalysis(window);
                var list = (ListBox)window.FindName("trackList");
                Assert.Equal(3, list.Items.Count);
                SetRowTitle(list.Items[1]!, "Sister Ray");

                Click(window, "analyzeBtn");
                SettleAnalysis(window);
                result = (list.Items.Count, RowTitle(list.Items[1]!),
                    ((TextBlock)window.FindName("statusText")).Text);
            });
            return result;
        });

        Assert.Equal(3, tracks);
        Assert.Equal("Sister Ray", title);
        Assert.Contains("in the same places", status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The threshold is a level, so moving it has to change what counts as a gap.</summary>
    [Fact]
    public void TheThresholdSliderDecidesWhatCountsAsAGap()
    {
        (int deep, int shallow) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = OpenSideWithGaps(main, (0, 120));

            (int Deep, int Shallow) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                var slider = (Slider)window.FindName("thresholdSlider");
                var list = (ListBox)window.FindName("trackList");

                // Below the −60 dBFS floor in the gaps: nothing there is quiet enough to be one.
                slider.Value = -70;
                Wpf.Pump();
                Click(window, "analyzeBtn");
                SettleAnalysis(window);
                int deep = list.Items.Count;

                slider.Value = -45;
                Wpf.Pump();
                Click(window, "analyzeBtn");
                SettleAnalysis(window);
                result = (deep, list.Items.Count);
            });
            return result;
        });

        Assert.Equal(1, deep);
        Assert.Equal(3, shallow);
    }

    /// <summary>
    /// The label beside the slider is a measurement, so the threshold analysed has to be the one
    /// printed. The slider's range is continuous, the label prints whole decibels, and the status
    /// line quotes the figure the analysis was actually handed — which is what ties the two.
    /// </summary>
    [Fact]
    public void TheThresholdAnalysedIsTheThresholdPrinted()
    {
        (string label, string status) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = OpenSideWithGaps(main, (0, 120));

            (string, string) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                var slider = (Slider)window.FindName("thresholdSlider");
                slider.Value = -45.4;
                Wpf.Pump();

                Click(window, "analyzeBtn");
                SettleAnalysis(window);
                result = (((TextBlock)window.FindName("thresholdText")).Text,
                    ((TextBlock)window.FindName("statusText")).Text);
            });
            return result;
        });

        Assert.Equal("-45 dB", label);
        Assert.Contains("-45 dB", status, StringComparison.Ordinal);
        Assert.DoesNotContain("-45.4", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// Analyze on a document with no audio returned in silence, which is indistinguishable from a
    /// button that was never wired up — the finding this repo already records for Reduce Noise.
    /// </summary>
    [Fact]
    public void AnalyzeSaysSoWhenThereIsNoAudioToAnalyze()
    {
        string status = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            main.AddDocument(new AudioDocument([[], []], Rate, 16) { Title = "Empty.wav" });
            DocumentViewModel document = main.ActiveDocument!;

            string result = "";
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                Click(window, "analyzeBtn");
                Wpf.Pump();
                result = ((TextBlock)window.FindName("statusText")).Text;
            });
            return result;
        });

        Assert.Contains("no audio to analyze", status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Those five buttons take their enabled state from a selection, and nothing sets that state
    /// until a selection changes — so on a list that opens empty they were lit and inert. The XAML
    /// starts them disabled and <c>OnTrackSelected</c> is what turns them on.
    /// </summary>
    [Fact]
    public void TheRowButtonsStartDisabledWhenThereIsNoRowToActOn()
    {
        bool[] enabled = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            main.AddDocument(new AudioDocument([[], []], Rate, 16) { Title = "Empty.wav" });
            DocumentViewModel document = main.ActiveDocument!;

            bool[] result = [];
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                Wpf.Pump();
                result = RowButtonsEnabled(window);
            });
            return result;
        });

        Assert.All(enabled, Assert.False);
    }

    /// <summary>
    /// The report this came from: "it gives the same message with −30". Three tracks before and
    /// three after is not the same answer — at a looser threshold the fade counts as quiet sooner,
    /// so every gap starts earlier and its midpoint moves into the music. The line has to say that
    /// happened, and the rows have to survive it, because they are the same three tracks.
    /// </summary>
    [Fact]
    public void ALooserThresholdMovesTheBoundariesAndSaysSoWithoutLosingTheRows()
    {
        (int tracks, string title, double firstBoundary, double movedBoundary, string status) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = OpenSideWithGaps(main);

            (int, string, double, double, string) result = default;
            Wpf.Show(new CdTransferDialog(document, main), window =>
            {
                SettleAnalysis(window);
                var list = (ListBox)window.FindName("trackList");
                Assert.Equal(3, list.Items.Count);
                SetRowTitle(list.Items[1]!, "Sister Ray");
                double before = Plans(window)[1].SourceStart;

                ((Slider)window.FindName("thresholdSlider")).Value = -30;
                Wpf.Pump();
                Click(window, "analyzeBtn");
                SettleAnalysis(window);

                result = (list.Items.Count, RowTitle(list.Items[1]!), before, Plans(window)[1].SourceStart,
                    ((TextBlock)window.FindName("statusText")).Text);
            });
            return result;
        });

        Assert.Equal(3, tracks);
        // The same three tracks, so what was typed into them survives the pass.
        Assert.Equal("Sister Ray", title);
        // And the boundary really did move, earlier, into the fade.
        Assert.True(movedBoundary < firstBoundary,
            $"the boundary went from {firstBoundary} to {movedBoundary}; a looser threshold should move it earlier");
        Assert.Contains("Still 3 tracks at -30 dB", status, StringComparison.Ordinal);
        Assert.Contains("boundaries moved by up to", status, StringComparison.Ordinal);
    }
}
