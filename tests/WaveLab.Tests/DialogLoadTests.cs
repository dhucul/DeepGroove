using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WaveLab.Audio;
using WaveLab.Help;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Every self-contained dialog, opened for real: constructed against the shipped theme, shown,
/// loaded, laid out and closed.
/// </summary>
/// <remarks>
/// <para>
/// The audit could say nothing about any of this — the app was never launched, so no XAML binding,
/// dialog flow or render path had ever been exercised, and the bindings were checked by reading
/// them. That is the gap these close. A dialog that throws on construction, resolves a
/// <c>StaticResource</c> that is not there, or hangs a binding on a property that has been renamed
/// fails here, and every one of those is invisible to a unit test that never builds a window.
/// </para>
/// <para>
/// What is deliberately not here: <c>MainWindow</c>, whose close path writes its position to the
/// real settings file; <c>SettingsDialog</c>, which needs the redirected app-data root that lives
/// in another collection; and the dialogs that want hardware or a disc — record, CD import, CD
/// transfer — or a long analysis to be running before they are worth looking at.
/// </para>
/// </remarks>
public sealed class DialogLoadTests
{
    private static AudioDocument Document(bool aiff = false)
    {
        var document = new AudioDocument(
            [new float[48_000], new float[48_000]],
            48_000,
            24)
        {
            Title = aiff ? "Transfer.aif" : "Transfer.wav",
        };
        if (aiff) document.Riff = RiffMetadata.ForAiff();
        return document;
    }

    private static DocumentViewModel ViewModel(bool aiff = false) => new(Document(aiff));

    private static CommandPalette.Command[] Commands() =>
    [
        new("Open…", "Ctrl+O", () => { }),
        new("Save As…", "Ctrl+Shift+S", () => { }),
        new("Normalize", null, () => { }),
        new("Remove DC Offset", null, () => { }, () => false),
    ];

    /// <summary>Every dialog here builds from nothing but a document, and closes without saving anything.</summary>
    private static Window Build(string name) => name switch
    {
        "info" => new InfoDialog("Sample rate converted", "The document is now at 44.1 kHz.", "44 100 Hz"),
        "text-prompt" => new TextPromptDialog("Name this region", "Region 1"),
        "param" => new ParamDialog(
            "Remove Clicks",
            "Repair",
            [new ParamDialog.ComboSpec("Strength", ["Gentle", "Balanced", "Strong"], 1)],
            new ParamDialog.SliderSpec("Sensitivity", 0, 1, 0.4, v => $"{v:P0}"),
            new ParamDialog.SliderSpec("Maximum length", 1, 200, 40, v => $"{v:0} samples")),
        "export" => new ExportDialog(ViewModel()),
        "statistics" => new StatisticsDialog(Document()),
        "file-info" => new FileInfoDialog(ViewModel()),
        "command-palette" => new CommandPalette([.. Commands()]),
        "markers" => new MarkersDialog(ViewModel()),
        "help" => new HelpDialog(HelpCatalog.RecordingTopicId),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such dialog."),
    };

    public static TheoryData<string> DialogNames() =>
    [
        "info", "text-prompt", "param", "export", "statistics",
        "file-info", "command-palette", "markers", "help",
    ];

    [Theory]
    [MemberData(nameof(DialogNames))]
    public void EveryDialogOpensAgainstTheShippedThemeWithoutABindingFailure(string name)
    {
        IReadOnlyList<string> failures = Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            Wpf.Show(Build(name), window =>
            {
                // Loaded has run and the tree is laid out, so anything the dialog does on the way
                // up has already happened.
                Assert.True(window.IsLoaded);
                Assert.True(window.ActualWidth > 0 && window.ActualHeight > 0);
            });
            return errors.Messages.ToArray();
        });

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The generic parameter dialog is the one most of the restoration and channel tools are made
    /// of, so what it hands back is the argument list of a dozen commands.
    /// </summary>
    [Fact]
    public void TheParameterDialogHandsBackWhatItsControlsAreSetTo()
    {
        (double[] values, int combo, int[] combos) = Wpf.Run(() =>
        {
            var dialog = (ParamDialog)Build("param");
            (double[] Values, int Combo, int[] Combos) result = default;
            Wpf.Show(dialog, _ => result = (dialog.Values, dialog.ComboIndex, dialog.ComboIndices));
            return result;
        });

        Assert.Equal([0.4, 40], values);      // the specs' defaults, in the order they were given
        Assert.Equal(1, combo);               // "Balanced"
        Assert.Equal([1], combos);
    }

    /// <summary>
    /// The palette is a search box over every command in the app, so the one thing it must do is
    /// narrow.
    /// </summary>
    [Fact]
    public void TheCommandPaletteNarrowsToWhatWasTyped()
    {
        (int all, int filtered) = Wpf.Run(() =>
        {
            var palette = new CommandPalette([.. Commands()]);
            (int All, int Filtered) counts = default;
            Wpf.Show(palette, window =>
            {
                var results = (ListBox)window.FindName("results");
                var search = (TextBox)window.FindName("search");
                counts.All = results.Items.Count;

                search.Text = "save";
                Wpf.Pump();
                counts.Filtered = results.Items.Count;
            });
            return counts;
        });

        Assert.Equal(4, all);
        Assert.Equal(1, filtered);
    }

    /// <summary>
    /// An AIFF has no broadcast extension to edit, and the tab is disabled rather than hidden so
    /// that the reason it does not apply is visible instead of inferred from its absence.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void TheBroadcastTabIsOfferedOnlyWhereThereIsOneToEdit(bool aiff, bool expected)
    {
        bool enabled = Wpf.Run(() =>
        {
            var dialog = new FileInfoDialog(ViewModel(aiff));
            bool result = false;
            Wpf.Show(dialog, window =>
                result = ((ToggleButton)window.FindName("broadcastTab")).IsEnabled);
            return result;
        });

        Assert.Equal(expected, enabled);
    }

    /// <summary>
    /// Opening a dialog is not an edit. Every one of these is built on a document and none of them
    /// may dirty it merely by being looked at — the file-information dialog least of all, since it
    /// is the one that edits metadata in place when it is used in earnest.
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentDialogNames))]
    public void OpeningADialogDoesNotDirtyTheDocument(string name)
    {
        (bool dirty, int version) = Wpf.Run(() =>
        {
            var viewModel = ViewModel();
            long before = viewModel.Doc.EditVersion;
            Window dialog = name switch
            {
                "export" => new ExportDialog(viewModel),
                "file-info" => new FileInfoDialog(viewModel),
                "markers" => new MarkersDialog(viewModel),
                "statistics" => new StatisticsDialog(viewModel.Doc),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such dialog."),
            };
            Wpf.Show(dialog, _ => { });
            return (viewModel.Doc.Dirty, (int)(viewModel.Doc.EditVersion - before));
        });

        Assert.False(dirty);
        Assert.Equal(0, version);
    }

    public static TheoryData<string> DocumentDialogNames() =>
        ["export", "file-info", "markers", "statistics"];
}
