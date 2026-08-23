using System.Windows.Controls;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The Edit History panel against the shipped theme: what it lists, and that it keeps up with a
/// document that carries on being edited underneath it.
/// </summary>
public sealed class HistoryPanelTests
{
    private static DocumentViewModel ViewModel(int frames = 4_000) =>
        new(new AudioDocument([new float[frames], new float[frames]], 44_100, 32) { Title = "Transfer.wav" });

    private static void Edit(DocumentViewModel vm, string name) =>
        vm.Doc.ReplaceRange(0, 100, [new float[100], new float[100]], name);

    /// <summary>The panel with the document's own primitives behind it and no shell guard.</summary>
    private static HistoryDialog Panel(DocumentViewModel vm, Func<bool>? canEdit = null) =>
        new(vm,
            (document, position) => document.Doc.JumpToHistoryPosition(position),
            (document, index) =>
            {
                if (document.Doc.TruncateHistoryFrom(index)) document.NotifyHistoryChanged();
            },
            canEdit);

    /// <summary>
    /// Row 0 is the document as it was opened, not a step — so a file with three edits shows four
    /// rows, and the last of them is where the document is.
    /// </summary>
    [Fact]
    public void ThePanelListsTheOpenedStateAndOneRowPerStep()
    {
        (int rows, int selected) = Wpf.Run(() =>
        {
            var vm = ViewModel();
            Edit(vm, "Reverse");
            Edit(vm, "Gain +3.0 dB");
            Edit(vm, "Remove DC Offset");

            (int Rows, int Selected) result = default;
            Wpf.Show(Panel(vm), window =>
            {
                var list = (ListBox)window.FindName("list");
                result = (list.Items.Count, list.SelectedIndex);
            });
            return result;
        });

        Assert.Equal(4, rows);
        Assert.Equal(3, selected);
    }

    /// <summary>
    /// An unedited file still has a row — the state it was opened in — and neither action applies to
    /// it: there is nowhere to jump to and nothing to discard.
    /// </summary>
    [Fact]
    public void AnUneditedDocumentShowsOnlyTheOpenedStateAndOffersNoAction()
    {
        (int rows, bool jump, bool delete) = Wpf.Run(() =>
        {
            (int Rows, bool Jump, bool Delete) result = default;
            Wpf.Show(Panel(ViewModel()), window =>
            {
                result = (
                    ((ListBox)window.FindName("list")).Items.Count,
                    ((Button)window.FindName("jumpButton")).IsEnabled,
                    ((Button)window.FindName("deleteButton")).IsEnabled);
            });
            return result;
        });

        Assert.Equal(1, rows);
        Assert.False(jump);
        Assert.False(delete);
    }

    /// <summary>
    /// The markers panel's defect, restated for history: this one is modeless too, so an edit in the
    /// main window while it is open has to reach it.
    /// </summary>
    [Fact]
    public void ThePanelRefreshesWhenTheDocumentIsEditedWhileItIsOpen()
    {
        (int before, int after) = Wpf.Run(() =>
        {
            var vm = ViewModel();
            Edit(vm, "Reverse");

            (int Before, int After) counts = default;
            Wpf.Show(Panel(vm), window =>
            {
                var list = (ListBox)window.FindName("list");
                counts.Before = list.Items.Count;

                Edit(vm, "Gain +3.0 dB");
                Wpf.Pump();
                counts.After = list.Items.Count;
            });
            return counts;
        });

        Assert.Equal(2, before);
        Assert.Equal(3, after);
    }

    /// <summary>
    /// The budget can release the row the panel had selected, which renumbers everything after it.
    /// Holding that index would have the panel pointing at a different step; it goes back to where
    /// the document actually is instead.
    /// </summary>
    [Fact]
    public void ThePanelSurvivesTheBudgetReleasingTheRowItHadSelected()
    {
        long original = AudioDocument.UndoBudgetBytes;
        try
        {
            (int selected, int rows, int position) = Wpf.Run(() =>
            {
                var vm = ViewModel(40_000);
                for (int i = 0; i < 4; i++)
                    vm.Doc.ReplaceRange(0, 5_000, [new float[5_000], new float[5_000]], $"edit {i}");

                (int Selected, int Rows, int Position) result = default;
                Wpf.Show(Panel(vm), window =>
                {
                    var list = (ListBox)window.FindName("list");
                    list.SelectedIndex = 1;
                    Wpf.Pump();

                    AudioDocument.UndoBudgetBytes = 2L * 2 * 2 * 5_000 * sizeof(float);
                    vm.Doc.ReplaceRange(0, 5_000, [new float[5_000], new float[5_000]], "edit 4");
                    Wpf.Pump();
                    result = (list.SelectedIndex, list.Items.Count, vm.Doc.HistoryPosition);
                });
                return result;
            });

            // Renumbered, so the panel goes back to where the document is rather than keeping an
            // index that now names a different step.
            Assert.Equal(position, selected);
            Assert.Equal(position + 1, rows);
        }
        finally
        {
            AudioDocument.UndoBudgetBytes = original;
        }
    }

    /// <summary>
    /// The panel is a window of its own, so the shell's progress overlay does not cover it. While a
    /// tool owns the document nothing here may move it, and the panel says why rather than leaving
    /// two dead buttons.
    /// </summary>
    [Fact]
    public void ThePanelIsReadOnlyWhileAnOperationOwnsTheDocument()
    {
        (bool jump, bool delete, bool copy, string caution) = Wpf.Run(() =>
        {
            var vm = ViewModel();
            Edit(vm, "Reverse");
            Edit(vm, "Gain +3.0 dB");

            (bool Jump, bool Delete, bool Copy, string Caution) result = default;
            Wpf.Show(Panel(vm, canEdit: () => false), window =>
            {
                var list = (ListBox)window.FindName("list");
                list.SelectedIndex = 1;
                Wpf.Pump();
                result = (
                    ((Button)window.FindName("jumpButton")).IsEnabled,
                    ((Button)window.FindName("deleteButton")).IsEnabled,
                    ((Button)window.FindName("copyButton")).IsEnabled,
                    ((TextBlock)window.FindName("cautionText")).Text);
            });
            return result;
        });

        Assert.False(jump, "the panel offered to jump while an operation owned the document.");
        Assert.False(delete, "the panel offered to discard a step while an operation owned the document.");
        Assert.True(copy, "reading the history is safe at any time and should stay offered.");
        Assert.Contains("read-only", caution);
    }

    /// <summary>
    /// The shell tells the panel directly when an operation starts or ends, because nothing else
    /// reaches a separate window.
    /// </summary>
    [Fact]
    public void ThePanelPicksUpTheGuardWhenTheShellRefreshesIt()
    {
        (bool duringOperation, bool afterwards) = Wpf.Run(() =>
        {
            var vm = ViewModel();
            Edit(vm, "Reverse");
            bool busy = true;

            (bool During, bool After) result = default;
            Wpf.Show(Panel(vm, canEdit: () => !busy), window =>
            {
                var panel = (HistoryDialog)window;
                var list = (ListBox)window.FindName("list");
                list.SelectedIndex = 0;
                Wpf.Pump();
                result.During = ((Button)window.FindName("jumpButton")).IsEnabled;

                busy = false;
                panel.RefreshActions();
                Wpf.Pump();
                result.After = ((Button)window.FindName("jumpButton")).IsEnabled;
            });
            return result;
        });

        Assert.False(duringOperation);
        Assert.True(afterwards);
    }

    /// <summary>
    /// The caution is about regions, so a file without any is not warned; a jump that crosses a
    /// length-changing step in a file that has one is.
    /// </summary>
    [Fact]
    public void TheRegionCautionOnlyAppearsWhereALengthChangingStepIsBeingCrossed()
    {
        var document = new AudioDocument([new float[1_000]], 44_100, 32);
        document.ReplaceRange(0, 100, [new float[100]], "Gain +3.0 dB");
        document.ReplaceRange(200, 0, [new float[400]], "Insert Silence");
        var history = document.GetHistory();

        Assert.Null(HistoryDialog.Caution(history, position: 0, regionCount: 0));
        Assert.Null(HistoryDialog.Caution(history, position: 2, regionCount: 3));
        Assert.Null(HistoryDialog.Caution(history, position: 1, regionCount: 0));

        string? caution = HistoryDialog.Caution(history, position: 0, regionCount: 3);
        Assert.NotNull(caution);
        Assert.Contains("1 step", caution);
    }
}

/// <summary>
/// The Match Loudness dialog: what it offers before anything has been measured, and that changing
/// the mode never reaches for the audio again.
/// </summary>
public sealed class MatchLoudnessDialogTests
{
    private static DocumentViewModel ViewModel(string title) =>
        new(new AudioDocument([new float[44_100], new float[44_100]], 44_100, 32) { Title = title });

    /// <summary>
    /// The relative modes have no delivery specification to take a ceiling from, so the target combo
    /// has nothing to say. Offered and disabled rather than hidden, the way the rest of the app
    /// treats an option that does not apply.
    /// </summary>
    [Fact]
    public void TheTargetComboIsOfferedAndDisabledInTheRelativeModes()
    {
        (bool atTarget, bool atQuietest, bool referenceEnabled) = Wpf.Run(() =>
        {
            (bool A, bool B, bool C) result = default;
            Wpf.Show(new MatchLoudnessDialog([ViewModel("A"), ViewModel("B")]), window =>
            {
                var mode = (ComboBox)window.FindName("modeCombo");
                var target = (ComboBox)window.FindName("targetCombo");
                var reference = (ComboBox)window.FindName("referenceCombo");

                mode.SelectedIndex = 0;
                Wpf.Pump();
                result.A = target.IsEnabled;

                mode.SelectedIndex = 1;
                Wpf.Pump();
                result.B = target.IsEnabled;

                mode.SelectedIndex = 3;
                Wpf.Pump();
                result.C = reference.IsEnabled;
            });
            return result;
        });

        Assert.True(atTarget);
        Assert.False(atQuietest);
        Assert.True(referenceEnabled);
    }

    /// <summary>
    /// Nothing has been measured, so there is no plan and nothing to apply. Enabling Apply before
    /// that would offer a gain derived from no measurement at all.
    /// </summary>
    [Fact]
    public void ApplyAndCopyAreRefusedUntilSomethingHasBeenMeasured()
    {
        (bool apply, bool copy, bool measure) = Wpf.Run(() =>
        {
            (bool Apply, bool Copy, bool Measure) result = default;
            Wpf.Show(new MatchLoudnessDialog([ViewModel("A"), ViewModel("B")]), window =>
            {
                result = (
                    ((Button)window.FindName("applyBtn")).IsEnabled,
                    ((Button)window.FindName("copyBtn")).IsEnabled,
                    ((Button)window.FindName("measureBtn")).IsEnabled);
            });
            return result;
        });

        Assert.False(apply);
        Assert.False(copy);
        Assert.True(measure);
    }

    /// <summary>
    /// Every tab starts ticked, because the whole point is levelling a set against each other; an
    /// empty selection would make the first thing anyone does a round of clicking.
    /// </summary>
    [Fact]
    public void EveryOpenTabStartsTickedAndListed()
    {
        string[] titles = Wpf.Run(() =>
        {
            var dialog = new MatchLoudnessDialog([ViewModel("Side one"), ViewModel("Side two")]);
            string[] result = [];
            Wpf.Show(dialog, _ => result = [.. dialog.Rows.Where(r => r.IsSelected).Select(r => r.Title)]);
            return result;
        });

        Assert.Equal(["Side one", "Side two"], titles);
    }
}
