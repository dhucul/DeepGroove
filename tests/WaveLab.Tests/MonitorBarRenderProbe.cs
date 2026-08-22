using System.IO;
using System.Windows;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the shell with a residual open and measures the monitor bar in the real window.
/// </summary>
/// <remarks>
/// <para>
/// The bar is a third stacked bar in the transport's grid row, beside the spectral one, and it is
/// there rather than in the transport because that row is full — a finding this repo already paid
/// for once, when the spectral actions were crammed in beside it and clipped. That reasoning is
/// worth something only if the new bar is actually rendered, so it is, at the shell's declared
/// 1180 px minimum.
/// </para>
/// <para>
/// <b>Nothing is asserted inside the callback, and the documents are marked saved in a finally.</b>
/// Both are load-bearing here in a way they are not for an ordinary dialog. The shell asks about
/// unsaved work with a modal box as it closes and there is nobody on this thread to answer it, so
/// an assertion that throws before the cleanup takes the whole test host down rather than
/// reporting — which is exactly what the first version of this test did, and it looked like a
/// crash in the feature rather than a wrong assertion about a margin.
/// </para>
/// <para>
/// Runs in the settings-sandbox collection with <see cref="MainWindow"/>, for the reasons
/// <see cref="ShellWindowTests"/> records.
/// </para>
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class MonitorBarRenderProbe : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public MonitorBarRenderProbe(ITestOutputHelper output)
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
        Visibility BeforeResidual,
        Visibility OnResidual,
        Visibility BackOnSource,
        double BarHeight,
        double NoteGiven,
        double NoteWanted,
        double NoteHeight,
        double SliderWidth,
        string Status);

    /// <summary>The note's own left margin, which <c>DesiredSize</c> includes and ActualWidth does not.</summary>
    private const double NoteMargin = 14;

    private static float[][] Removed()
    {
        var removed = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            removed[c] = new float[44_100];
            removed[c][1000 + c] = 0.01f;      // one click's worth, 40 dB under full scale
        }
        return removed;
    }

    [Fact]
    public void TheMonitorBarAppearsOnlyOnAResidualAndFitsTheShellAtItsMinimumWidth()
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
                    var source = new AudioDocument(
                        [new float[44_100], new float[44_100]], 44_100, 24) { Title = "side one.aif" };
                    main.AddDocument(source);
                    shell.UpdateLayout();
                    Wpf.Pump();
                    Visibility before = shell.monitorBar.Visibility;

                    main.AddResidualDocument(source, Removed(), "The restoration");
                    main.ActiveTab = main.AudioDocuments.Last();
                    shell.UpdateLayout();
                    Wpf.Pump();
                    Visibility on = shell.monitorBar.Visibility;
                    var snapshot = new Measurement(before, on, Visibility.Visible,
                        shell.monitorBar.ActualHeight,
                        shell.monitorNote.ActualWidth,
                        shell.monitorNote.DesiredSize.Width,
                        shell.monitorNote.ActualHeight,
                        shell.monitorGainSlider.ActualWidth,
                        main.ActionStatusText);

                    main.ActiveTab = main.AudioDocuments.First();
                    shell.UpdateLayout();
                    Wpf.Pump();
                    result = snapshot with { BackOnSource = shell.monitorBar.Visibility };
                }
                finally
                {
                    // A residual arrives unsaved, which is right in the app and fatal here.
                    foreach (DocumentViewModel open in main.AudioDocuments) open.Doc.MarkSaved();
                }
            });
            return (result, (IReadOnlyList<string>)errors.Messages.ToArray());
        });

        foreach (string failure in failures) _output.WriteLine(failure);
        Assert.Empty(failures);
        Assert.NotNull(measured);
        _output.WriteLine($"status: {measured.Status}");
        _output.WriteLine($"bar {measured.BarHeight:F1} px tall, slider {measured.SliderWidth:F0} px, " +
                          $"note given {measured.NoteGiven:F1} px and wants " +
                          $"{measured.NoteWanted - NoteMargin:F1}, {measured.NoteHeight:F1} px tall");

        Assert.Equal(Visibility.Collapsed, measured.BeforeResidual);
        Assert.Equal(Visibility.Visible, measured.OnResidual);
        Assert.Equal(Visibility.Collapsed, measured.BackOnSource);

        Assert.True(measured.SliderWidth > 200, "the monitor slider was squeezed out of the bar");
        // One line. The bar sits above the waveform, so a bar that wraps takes editor height away
        // every time a residual is opened.
        Assert.True(measured.BarHeight is > 0 and < 52,
            $"the monitor bar ran to {measured.BarHeight:F0} px, which is more than one row of controls");
        Assert.True(measured.NoteHeight is > 0 and < 20,
            $"the note wrapped to {measured.NoteHeight:F0} px");
        Assert.True(measured.NoteWanted - NoteMargin <= measured.NoteGiven + 0.5,
            $"the note wants {measured.NoteWanted - NoteMargin:F1} px of {measured.NoteGiven:F1} " +
            "and is being trimmed at the shell's minimum width");
    }
}
