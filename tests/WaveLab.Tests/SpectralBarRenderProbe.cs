using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the shell in waveform mode and measures the spectral bar, which now appears there.
/// </summary>
/// <remarks>
/// <para>
/// The bar used to appear only with the spectrogram, so its layout had only ever been judged with
/// four selection tools and a scale switch taking up the ends of it. Showing it in waveform mode
/// hides both of those and puts a readout beside them that was never there before, and this repo's
/// own record is that a control which does not fit is cut mid-glyph and reads as a drawing fault.
/// So it is rendered, at the shell's declared 1180 px minimum, rather than reasoned about from the
/// XAML's column arithmetic.
/// </para>
/// <para>
/// It earned its keep immediately: the first wording of the band readout — "0 Hz → 22.05 kHz · full
/// band" — took the scale switch from 37.5 px to 2 at that width, because the readout is docked
/// first of the three right-docked groups and the switch is docked last, so the switch pays for
/// every pixel the readout spends. Saying "full band" says the same thing and gives 50 px back.
/// </para>
/// <para>
/// Measuring that also showed the bar had never fitted at 1180 px in the first place: with a drawn
/// band it left the switch 37.5 px of the 199.5 it wants, so CONSTANT-Q shipped cut mid-glyph. The
/// switch is dropped below <see cref="MainViewModel.SpectralScaleMinimumWidth"/> now rather than
/// squeezed, and this is where that is pinned — whole or absent, never in between.
/// </para>
/// <para>
/// <b>Nothing is asserted inside the callback, and the documents are marked saved in a finally</b>,
/// for the reasons <see cref="MonitorBarRenderProbe"/> records: the shell asks about unsaved work
/// with a modal box as it closes, and an assertion thrown before the cleanup takes the host down.
/// </para>
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class SpectralBarRenderProbe : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public SpectralBarRenderProbe(ITestOutputHelper output)
    {
        _output = output;
        AppSettings.AppDataDir = _sandbox;
    }

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private sealed record Measurement(
        Visibility BarOnWaveform,
        Visibility ToolsOnWaveform,
        Visibility ToolsOnSpectrogram,
        bool HealBeforeSelection,
        bool HealAfterSelection,
        double BarHeight,
        double BandGiven,
        double BandWanted,
        double BandHeight,
        double ScaleAtMinimumWidth,
        double ScaleWhenShown,
        double ScaleWantedWhenShown,
        double HintGiven,
        double HintWanted,
        string BandText,
        string HintText);

    /// <summary>The readout's own left margin, which <c>DesiredSize</c> includes and ActualWidth does not.</summary>
    private const double BandMargin = 8;

    /// <summary>The hint's own right margin, likewise.</summary>
    private const double HintMargin = 14;

    /// <summary>
    /// The scale switch, which is docked after the readout and so is what pays for its width.
    /// Returns its width and how much it wanted, both zero when it is not on screen.
    /// </summary>
    private static (double Got, double Wanted) Scale(MainWindow shell)
    {
        var panel = (DockPanel)shell.spectralBar.Child;
        foreach (FrameworkElement child in panel.Children)
        {
            if (child is Border { Child: StackPanel { Children.Count: 3 } segments }
                && segments.Children[0] is RadioButton { Content: "LINEAR" })
            {
                return child.Visibility != Visibility.Visible
                    ? (0, 0)
                    : (child.ActualWidth, child.DesiredSize.Width - child.Margin.Right);
            }
        }
        return (double.NaN, double.NaN);
    }

    [Fact]
    public void TheSpectralBarFitsTheShellInWaveformModeWithItsDrawingToolsHidden()
    {
        (Measurement? measured, IReadOnlyList<string> failures) = Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            Measurement? result = null;
            Wpf.Show(new MainWindow(), window =>
            {
                var shell = (MainWindow)window;
                var main = (MainViewModel)shell.DataContext;
                try
                {
                    shell.Width = 1180;                     // the shell's declared minimum
                    var doc = new AudioDocument(
                        [new float[441_000], new float[441_000]], 44_100, 24) { Title = "side one.wav" };
                    main.AddDocument(doc);
                    shell.UpdateLayout();
                    Wpf.Pump();

                    Visibility bar = shell.spectralBar.Visibility;
                    Visibility toolsOffPicture = shell.spectralRectangleTool.Visibility;
                    bool healBefore = shell.spectralHeal.IsEnabled;
                    // The hint is shown *instead of* a selection, so it has to be measured before
                    // there is one — afterwards it is collapsed and every width reads zero.
                    double hintGiven = shell.spectralToolHint.ActualWidth;
                    double hintWanted = shell.spectralToolHint.DesiredSize.Width;

                    main.ActiveDocument!.SetSelection(44_100, 132_300);
                    shell.UpdateLayout();
                    Wpf.Pump();

                    var snapshot = new Measurement(
                        bar, toolsOffPicture, Visibility.Collapsed,
                        healBefore, shell.spectralHeal.IsEnabled,
                        shell.spectralBar.ActualHeight,
                        shell.spectralBandText.ActualWidth,
                        shell.spectralBandText.DesiredSize.Width,
                        shell.spectralBandText.ActualHeight,
                        ScaleAtMinimumWidth: 0, ScaleWhenShown: 0, ScaleWantedWhenShown: 0,
                        hintGiven, hintWanted,
                        main.SpectralBandText,
                        main.SpectralToolHint);

                    // Everything back on: the four tools and the scale switch on either end,
                    // with the wider of the two readouts — a band somebody drew. This is the
                    // arrangement the bar cannot hold at its minimum width.
                    main.ShowSpectrogramCommand.Execute(null);
                    main.SpectralSelection = new SpectralSelection(
                        SpectralTool.Rectangle,
                        SpectralMask.ForRegion(44_100, 132_300, 410, 3_200, 44_100, 2048, 512),
                        44_100, 2048, 512);
                    shell.UpdateLayout();
                    Wpf.Pump();
                    (double atMinimum, _) = Scale(shell);

                    shell.Width = MainViewModel.SpectralScaleMinimumWidth;
                    shell.UpdateLayout();
                    Wpf.Pump();
                    (double shown, double wanted) = Scale(shell);

                    result = snapshot with
                    {
                        ToolsOnSpectrogram = shell.spectralRectangleTool.Visibility,
                        ScaleAtMinimumWidth = atMinimum,
                        ScaleWhenShown = shown,
                        ScaleWantedWhenShown = wanted,
                    };
                }
                finally
                {
                    foreach (DocumentViewModel open in main.AudioDocuments) open.Doc.MarkSaved();
                }
            });
            return (result, (IReadOnlyList<string>)errors.Messages.ToArray());
        });

        foreach (string failure in failures) _output.WriteLine(failure);
        Assert.Empty(failures);
        Assert.NotNull(measured);
        _output.WriteLine($"band \"{measured.BandText}\" given {measured.BandGiven:F1} px, " +
                          $"wants {measured.BandWanted - BandMargin:F1}, {measured.BandHeight:F1} px tall");
        _output.WriteLine($"scale switch: {measured.ScaleAtMinimumWidth:F1} px at 1180, " +
                          $"{measured.ScaleWhenShown:F1} of {measured.ScaleWantedWhenShown:F1} wanted " +
                          $"at {MainViewModel.SpectralScaleMinimumWidth:F0}");
        _output.WriteLine($"hint \"{measured.HintText}\" given {measured.HintGiven:F1} px, " +
                          $"wants {measured.HintWanted - HintMargin:F1}");
        _output.WriteLine($"bar {measured.BarHeight:F1} px tall");

        Assert.Equal(Visibility.Visible, measured.BarOnWaveform);
        Assert.Equal(Visibility.Collapsed, measured.ToolsOnWaveform);
        Assert.Equal(Visibility.Visible, measured.ToolsOnSpectrogram);

        Assert.False(measured.HealBeforeSelection, "Heal was offered with nothing selected");
        Assert.True(measured.HealAfterSelection, "a waveform selection left Heal disabled");

        // One row. The bar sits above the editor, so a bar that wraps takes waveform height away
        // for the whole session rather than only while the spectrogram is open.
        Assert.True(measured.BarHeight is > 0 and < 52,
            $"the spectral bar ran to {measured.BarHeight:F0} px, more than one row of controls");
        Assert.True(measured.BandHeight is > 0 and < 20,
            $"the band readout wrapped to {measured.BandHeight:F0} px");
        Assert.True(measured.BandWanted - BandMargin <= measured.BandGiven + 0.5,
            $"the band readout wants {measured.BandWanted - BandMargin:F1} px of {measured.BandGiven:F1} " +
            "and is being trimmed at the shell's minimum width");
        Assert.True(measured.HintGiven > 0, "the tool hint was not on screen to be measured");
        Assert.True(measured.HintWanted - HintMargin <= measured.HintGiven + 0.5,
            $"the tool hint wants {measured.HintWanted - HintMargin:F1} px of {measured.HintGiven:F1} " +
            "and is being trimmed at the shell's minimum width");

        // The switch is either whole or absent, and never in between. In between is what a
        // ClipToBounds Border does with less room than it wants: CONSTANT-Q cut mid-glyph, which
        // reads as a drawing fault rather than as a control that did not fit.
        Assert.Equal(0, measured.ScaleAtMinimumWidth);
        Assert.True(measured.ScaleWhenShown > 0,
            $"the scale switch was still hidden at {MainViewModel.SpectralScaleMinimumWidth:F0} px, "
            + "so the threshold is above the width it actually needs");
        Assert.True(measured.ScaleWhenShown >= measured.ScaleWantedWhenShown - 0.5,
            $"the scale switch was shown at {measured.ScaleWhenShown:F1} px of "
            + $"{measured.ScaleWantedWhenShown:F1} wanted, so it is being cut rather than dropped");
    }

    /// <summary>
    /// The other half of dropping the switch: the choice it carried has to survive both ways of
    /// losing it — waveform mode, and a window too narrow to hold the bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It shipped for a day gated on <c>ShowsSpectrogram</c>, which greyed it out in waveform mode.
    /// The scale is a sticky preference and not an action — choosing it there is choosing what Split
    /// will draw the moment it opens — and a menu item greyed for a reason the user cannot see reads
    /// as broken rather than as not applicable. So the gate is gone and this is what holds it out.
    /// </para>
    /// <para>
    /// The submenus are opened rather than read closed, because a declared <c>MenuItem</c>'s
    /// bindings are what is being checked and a broken one does not throw — it leaves the property
    /// at its default, which for <c>IsEnabled</c> is the answer this test wants either way.
    /// </para>
    /// <para>
    /// Nothing is asserted inside the callback here either: every reading is collected into the
    /// strings below and judged outside it, for the reason the class remarks give.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFrequencyScaleStaysReachableWhicheverWayTheSwitchIsLost()
    {
        (string[] states, IReadOnlyList<string> failures) = Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            var seen = new List<string>();
            Wpf.Show(new MainWindow(), window =>
            {
                var shell = (MainWindow)window;
                var main = (MainViewModel)shell.DataContext;
                try
                {
                    main.AddDocument(new AudioDocument(
                        [new float[441_000], new float[441_000]], 44_100, 24) { Title = "s.wav" });
                    shell.Width = 1180;
                    shell.UpdateLayout();
                    Wpf.Pump();

                    // Waveform mode: no picture, so no switch on the bar. Choosing from the
                    // menu still has to work, which is the whole reason the gate came off.
                    MenuItem scale = FrequencyScaleMenu(shell);
                    var constantQ = (MenuItem)scale.Items[2];
                    constantQ.Command.Execute(null);
                    Wpf.Pump();
                    seen.Add(Report(shell, main, "waveform"));

                    // Spectrogram at a width the bar cannot hold: the switch goes, the menu stays.
                    main.ShowSpectrogramCommand.Execute(null);
                    shell.UpdateLayout();
                    Wpf.Pump();
                    seen.Add(Report(shell, main, "spectrogram, too narrow"));
                }
                finally
                {
                    foreach (DocumentViewModel open in main.AudioDocuments) open.Doc.MarkSaved();
                }
            });
            return (seen.ToArray(), (IReadOnlyList<string>)errors.Messages.ToArray());
        });

        foreach (string state in states) _output.WriteLine(state);
        foreach (string failure in failures) _output.WriteLine(failure);
        Assert.Empty(failures);
        // Both ways of losing the switch, and in both the menu is live and carries the choice the
        // waveform-mode click made through it.
        Assert.Equal(2, states.Length);
        Assert.All(states, state => Assert.Contains("switch shown=False", state));
        Assert.All(states, state => Assert.Contains("enabled=True", state));
        Assert.All(states, state => Assert.Contains("checked=Constant-Q", state));
    }

    /// <summary>Opens View ▸ Frequency Scale so its items are realised, and returns it.</summary>
    private static MenuItem FrequencyScaleMenu(MainWindow shell)
    {
        var menu = (Menu)Find(shell, typeof(Menu))!;
        MenuItem view = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "_View");
        view.IsSubmenuOpen = true;
        Wpf.Pump();
        MenuItem scale = view.Items.OfType<MenuItem>()
            .Single(i => i.Header as string == "Frequency Scale");
        scale.IsSubmenuOpen = true;
        Wpf.Pump();
        scale.IsSubmenuOpen = false;
        view.IsSubmenuOpen = false;
        Wpf.Pump();
        return scale;
    }

    private static string Report(MainWindow shell, MainViewModel main, string where)
    {
        MenuItem scale = FrequencyScaleMenu(shell);
        MenuItem[] items = [.. scale.Items.OfType<MenuItem>()];
        string ticked = items.Single(i => i.IsChecked).Header as string ?? "?";
        bool enabled = scale.IsEnabled && items.All(i => i.IsEnabled);
        return $"{where}: switch shown={main.ShowsSpectralScale}, menu enabled={enabled}, "
             + $"checked={ticked}";
    }

    private static DependencyObject? Find(DependencyObject root, Type type)
    {
        if (type.IsInstanceOfType(root)) return root;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject? hit = Find(VisualTreeHelper.GetChild(root, i), type);
            if (hit != null) return hit;
        }
        return null;
    }
}
