using System.Windows;
using System.Windows.Controls;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The vinyl workbench as a modeless window: what it does when the recording it measured, the
/// selection it scoped itself to, or the tab it belongs to changes while it is open.
/// </summary>
/// <remarks>
/// <para>
/// It holds a point-in-time analysis — channel arrays, a range, an edit version — and commits a
/// render of that analysis over the document. Modal, the source could not move; the version check
/// before the splice was for an async race nobody expected to hit. Modeless it is routine, so the
/// window says the analysis is stale at the edit and refuses Apply there, instead of discovering it
/// after a full render.
/// </para>
/// <para>
/// The one path not covered here is the rack: a preview bypasses the master rack and close puts it
/// back, and driving that needs a completed analysis <em>and</em> playback. The snapshot it restores
/// now follows a bypass the user works themselves, which is inspection only.
/// </para>
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class RestorationWorkbenchModelessTests : IDisposable
{
    private const int Rate = 44_100;

    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public RestorationWorkbenchModelessTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    /// <summary>Two seconds of tone: long enough to analyse, short enough to do it in a test.</summary>
    private static DocumentViewModel Open(MainViewModel main)
    {
        var left = new float[Rate * 2];
        var right = new float[left.Length];
        for (int i = 0; i < left.Length; i++)
            left[i] = right[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / (double)Rate));
        main.AddDocument(new AudioDocument([left, right], Rate, 24) { Title = "Side A.wav" });
        return main.ActiveDocument!;
    }

    /// <summary>
    /// Drain the dispatcher until <paramref name="ready"/> holds. The workbench's analysis and render
    /// are on worker threads, so a fixed number of pumps would be a race rather than a wait.
    /// </summary>
    private static bool PumpUntil(Func<bool> ready, int timeoutMs = 30_000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!ready() && Environment.TickCount64 < deadline) Wpf.Pump();
        return ready();
    }

    private static Button Reanalyze(Window window) => (Button)window.FindName("reanalyzeBtn");

    /// <summary>
    /// An edit to the recording invalidates the analysis, and the window says so where the range it
    /// analysed is printed rather than waiting to refuse a commit it has already spent a render on.
    /// </summary>
    [Fact]
    public void AnEditMarksTheAnalysisStaleAndOffersToRunItAgain()
    {
        (Visibility before, Visibility after, string message) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main);

            (Visibility Before, Visibility After, string Message) result = default;
            Wpf.Show(new RestorationWorkbenchDialog(document, main), window =>
            {
                result.Before = Reanalyze(window).Visibility;

                document.Doc.ReplaceRange(0, 0, [new float[Rate], new float[Rate]], "Insert Silence");
                Wpf.Pump();
                result.After = Reanalyze(window).Visibility;
                result.Message = ((TextBlock)window.FindName("staleText")).Text;
            });
            return result;
        });

        Assert.Equal(Visibility.Collapsed, before);
        Assert.Equal(Visibility.Visible, after);
        Assert.Contains("recording changed", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moving the selection is not an edit: the captured range is still the range that was analysed,
    /// so the offer is to re-scope and Apply is left alone. The two states are worded apart because
    /// only one of them makes what the window is holding wrong.
    /// </summary>
    [Fact]
    public void MovingTheSelectionOffersARescopeRatherThanInvalidatingTheAnalysis()
    {
        (Visibility offered, string message) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main);

            (Visibility Offered, string Message) result = default;
            Wpf.Show(new RestorationWorkbenchDialog(document, main), window =>
            {
                document.SetSelection(Rate / 2, Rate);
                Wpf.Pump();
                result = (Reanalyze(window).Visibility, ((TextBlock)window.FindName("staleText")).Text);
            });
            return result;
        });

        Assert.Equal(Visibility.Visible, offered);
        Assert.Contains("selection moved", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-analyze re-takes the range, the format and the audio, and clears the warning it was
    /// offered for — the equivalent of closing the workbench and reopening it, without losing it.
    /// </summary>
    [Fact]
    public void ReanalyzeRescopesToTheCurrentSelectionAndClearsTheWarning()
    {
        (string range, Visibility offered) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main);

            (string Range, Visibility Offered) result = default;
            Wpf.Show(new RestorationWorkbenchDialog(document, main), window =>
            {
                var reanalyze = Reanalyze(window);
                var apply = (Button)window.FindName("applyBtn");
                // Apply becoming available is the only signal that the first analysis finished; the
                // Re-analyze button is enabled from the outset, so waiting on it waits for nothing.
                Assert.True(PumpUntil(() => apply.IsEnabled), "the first analysis never finished");

                document.SetSelection(Rate / 2, Rate);
                Wpf.Pump();
                Assert.Equal(Visibility.Visible, reanalyze.Visibility);

                reanalyze.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(PumpUntil(() => apply.IsEnabled), "the re-analysis never finished");

                result = (((TextBlock)window.FindName("rangeText")).Text, reanalyze.Visibility);
            });
            return result;
        });

        Assert.Equal(Visibility.Collapsed, offered);
        Assert.StartsWith("Selection", range, StringComparison.Ordinal);
    }

    /// <summary>
    /// The end-to-end commit. Worth its own test because the modal version signalled it with
    /// <c>DialogResult = true</c>, which throws on a window that was shown rather than shown modally
    /// — a break that no amount of layout or analysis coverage would reach.
    /// </summary>
    [Fact]
    public void ApplyingCommitsOneUndoableEditAndReportsThroughTheAppliedEvent()
    {
        (bool raised, bool prepareCd, int edits, bool closed) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main);
            var dialog = new RestorationWorkbenchDialog(document, main);

            bool wasRaised = false;
            bool cd = true;
            dialog.Applied += requested => { wasRaised = true; cd = requested; };

            (bool Raised, bool PrepareCd, int Edits, bool Closed) result = default;
            Wpf.Show(dialog, window =>
            {
                var apply = (Button)window.FindName("applyBtn");
                Assert.True(PumpUntil(() => apply.IsEnabled), "the analysis never enabled Apply");

                apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(PumpUntil(() => wasRaised), "the restoration never committed");
                Wpf.Pump();

                result = (wasRaised, cd, document.Doc.HistoryPosition, !window.IsVisible);
            });
            return result;
        });

        Assert.True(raised);
        Assert.False(prepareCd);          // plain Apply, not Apply & Prepare CD
        Assert.Equal(1, edits);
        Assert.True(closed);
    }

    /// <summary>Closing the file closes the workbench; there is nothing left to restore.</summary>
    [Fact]
    public void ClosingTheDocumentClosesTheWorkbench()
    {
        bool visible = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main);

            bool stillOpen = true;
            Wpf.Show(new RestorationWorkbenchDialog(document, main), window =>
            {
                main.CloseAllCommand.Execute(null);
                Assert.True(PumpUntil(() => !window.IsVisible), "the workbench outlived its document");
                stillOpen = window.IsVisible;
            });
            return stillOpen;
        });

        Assert.False(visible);
    }

    /// <summary>
    /// Two workbenches on one file would each hold their own analysis of it and each be willing to
    /// commit that analysis over the other's edit, so the second request raises the first window.
    /// </summary>
    [Fact]
    public void AskingTwiceRaisesTheWorkbenchThatIsAlreadyOpen()
    {
        (bool same, bool closed) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            DocumentViewModel document = Open(main);

            RestorationWorkbenchDialog first = RestorationWorkbenchDialog.ShowFor(document, main, null);
            Wpf.Pump();
            bool isSame = ReferenceEquals(first, RestorationWorkbenchDialog.ShowFor(document, main, null));

            // Closing is part of the test rather than cleanup after it. This is the one place here
            // that goes through the real ShowFor, so the window is shown and activated rather than
            // parked offscreen by Wpf.Show — and it holds subscriptions to the view model this block
            // is about to dispose. One left open takes those, and the focus, into the next test.
            // The wait is generous because a close during the opening analysis is vetoed until the
            // scan cancels and re-issues it.
            first.Close();
            return (isSame, PumpUntil(() => !first.IsVisible, 15_000));
        });

        Assert.True(same);
        Assert.True(closed, "the workbench outlived the test that opened it");
    }
}
