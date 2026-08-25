using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Help;
using WaveLab.Util;
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
/// What is not here: <c>MainWindow</c> and <c>SettingsDialog</c>, which both need the redirected
/// app-data root and so live in <see cref="ShellWindowTests"/>; and the dialogs that want hardware
/// or a disc — record, CD import, CD transfer — or a long analysis to be running before they are
/// worth looking at.
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
        // Both normalize dialogs are ParamDialog configurations rather than types of their own, so
        // what is checked here is that the configuration lays out — a slider whose range excludes
        // its own default, or a combo built from an empty list, fails on the way up. Only the
        // static bounds are read, never AppSettings.Instance, so this stays out of the app-data
        // sandbox that keeps SettingsDialog in ShellWindowTests.
        "normalize-peak" => new ParamDialog(
            "Normalize peak",
            "Normalize",
            null, null, 0,
            new ParamDialog.SliderSpec("Ceiling",
                AppSettings.MinimumNormalizePeakCeilingDb,
                AppSettings.MaximumNormalizePeakCeilingDb,
                AppSettings.DefaultNormalizePeakCeilingDb,
                v => $"{v:0.0} dBFS",
                AppSettings.NormalizePeakCeilingStepDb)),
        "normalize-loudness" => new ParamDialog(
            "Normalize loudness — whole file",
            "Measure",
            "Target",
            [
                .. LoudnessTarget.All.Select(t =>
                    $"{t.Name} — {t.IntegratedLufs:0.0} LUFS, ≤ {t.TruePeakDbtp:0.0} dBTP"),
                "Custom",
            ],
            LoudnessTarget.All.Count - 1,
            new ParamDialog.SliderSpec("Custom target", -31, -6, -14, v => $"{v:0.0} LUFS", 0.5),
            new ParamDialog.SliderSpec("Custom ceiling", -6, 0, LoudnessMatch.RelativeCeilingDbtp,
                v => $"{v:0.0} dBTP", 0.1)),
        "export" => new ExportDialog(ViewModel()),
        "statistics" => new StatisticsDialog(Document()),
        "file-info" => new FileInfoDialog(ViewModel()),
        "command-palette" => new CommandPalette([.. Commands()]),
        "markers" => new MarkersDialog(ViewModel()),
        "history" => new HistoryDialog(
            ViewModel(),
            (document, position) => document.Doc.JumpToHistoryPosition(position),
            (document, index) => document.Doc.TruncateHistoryFrom(index)),
        "match-loudness" => new MatchLoudnessDialog([ViewModel()]),
        "help" => new HelpDialog(HelpCatalog.RecordingTopicId),
        // Built from the wording the command actually puts up, rather than from placeholders: the
        // labels carry figures, so their length is a property of the message and not of the test.
        "choice" => new ChoiceDialog(
            "Normalize loudness",
            LoudnessMatch.DescribeCeilingChoice(CeilingBoundPlan, CeilingBoundStep).Message,
            LoudnessMatch.DescribeCeilingChoice(CeilingBoundPlan, CeilingBoundStep).Labels),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such dialog."),
    };

    public static TheoryData<string> DialogNames() =>
    [
        "info", "text-prompt", "param", "normalize-peak", "normalize-loudness", "export",
        "statistics", "file-info", "command-palette", "markers", "history", "match-loudness", "help",
        "choice",
    ];

    /// <summary>The true-peak-limited case Normalize Loudness prompts about, for the choice dialog.</summary>
    private static LoudnessMatchPlan CeilingBoundPlan { get; } = LoudnessMatch.Plan(
        [new LoudnessMeasurement("Take 1", -21.8, -5.5, 6.0, 44_100, 44_100 * 30)],
        LoudnessMatchMode.Target,
        LoudnessTarget.CompactDisc);

    private static LoudnessMatchStep CeilingBoundStep => CeilingBoundPlan.Steps[0];

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
    /// The restoration tools ask "keep what was removed?" through this, so the box has to survive
    /// being added after the constructor has already built the combo and the sliders — which is
    /// the only way it can be added at all, the constructors ending in a params array.
    /// </summary>
    [Fact]
    public void AnOptionAddedAfterConstructionIsShownAndHandedBack()
    {
        (bool[] checks, double[] values, IReadOnlyList<string> failures) = Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            var dialog = (ParamDialog)Build("param");
            dialog.AddCheck("Keep what was removed", initial: true,
                "Opens what was removed in a second tab so you can hear it.");
            (bool[] Checks, double[] Values) result = ([], []);
            Wpf.Show(dialog, _ => result = (dialog.Checks, dialog.Values));
            return (result.Checks, result.Values, (IReadOnlyList<string>)errors.Messages.ToArray());
        });

        Assert.Equal([true], checks);
        // The sliders the constructor built are untouched by the late arrival.
        Assert.Equal([0.4, 40], values);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Past the memory ceiling the option is offered and disabled rather than hidden, so it has to
    /// report both its state and whether it could be reached — a disabled box reads as unchecked,
    /// and remembering that answer would overwrite a preference the user was never offered.
    /// </summary>
    [Fact]
    public void ADisabledOptionSaysThatItWasNotReachable()
    {
        (bool[] checks, bool[] enabled) = Wpf.Run(() =>
        {
            var dialog = (ParamDialog)Build("param");
            dialog.AddCheck("Keep what was removed", initial: false,
                "This range would need about 2.6 GB of memory, past the 512 MB limit.",
                enabled: false);
            (bool[] Checks, bool[] Enabled) result = ([], []);
            Wpf.Show(dialog, _ => result = (dialog.Checks, dialog.ChecksEnabled));
            return result;
        });

        Assert.Equal([false], checks);
        Assert.Equal([false], enabled);
    }

    [Fact]
    public void ADialogWithNoOptionHandsBackNoneRatherThanThrowing()
    {
        bool[] checks = Wpf.Run(() =>
        {
            var dialog = (ParamDialog)Build("param");
            bool[] result = [];
            Wpf.Show(dialog, _ => result = dialog.Checks);
            return result;
        });

        Assert.Empty(checks);
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
                "history" => new HistoryDialog(
                    viewModel,
                    (document, position) => document.Doc.JumpToHistoryPosition(position),
                    (document, index) => document.Doc.TruncateHistoryFrom(index)),
                // Match Loudness must not measure on Loaded: a dialog that scanned every open file
                // the moment it appeared would be unusable on an album, and the dirty check here is
                // what holds it to a button press.
                "match-loudness" => new MatchLoudnessDialog([viewModel]),
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
        ["export", "file-info", "markers", "history", "match-loudness", "statistics"];
}
