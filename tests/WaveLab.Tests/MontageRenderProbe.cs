using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WaveLab.Audio;
using WaveLab.Audio.Montage;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The Render Montage footer, measured in place, and the hand-off the CD destinations make.
/// </summary>
/// <remarks>
/// The render button is relabelled per destination — "Render", "Render…", "Render &amp; Prepare CD…",
/// "Render &amp; Prepare DDP…" — and its column was a fixed 190 px shared with Close, leaving it 104.
/// The long labels were clipped to "Render &amp; Prepa", which reads as a broken option rather than a
/// narrow button, and that is how it was reported. A calculation would not have caught it; only
/// laying the real label out in the real footer does.
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class MontageRenderProbe : IDisposable
{
    private const int Rate = 44_100;

    private readonly ITestOutputHelper output;
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public MontageRenderProbe(ITestOutputHelper testOutput)
    {
        output = testOutput;
        AppSettings.AppDataDir = _sandbox;
    }

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private static MontageViewModel Montage()
    {
        var document = new MontageDocument(Rate, 2) { Title = "Side A" };
        var data = new float[2][];
        for (int c = 0; c < 2; c++) data[c] = new float[Rate * 4];
        int source = document.AddSource(MontageSource.From(data, Rate, Rate, 2, "take one"));
        document.Append(source);
        return new MontageViewModel(document);
    }

    /// <summary>Every label the button can carry has to fit the button, in the shipped theme.</summary>
    /// <remarks>
    /// The label is measured against an unconstrained proxy rather than read off the button's own
    /// <c>DesiredSize</c>. A button in a fixed column is <em>measured</em> at that width, so its
    /// desired size comes back equal to its actual one however badly the text is clipped — checking
    /// those two against each other passes on the very layout this was written to catch. What
    /// clipping actually depends on is the text's natural width plus the chrome around it.
    /// </remarks>
    [Theory]
    [InlineData("newTabBtn")]
    [InlineData("fileBtn")]
    [InlineData("cdBtn")]
    [InlineData("ddpBtn")]
    public void EveryDestinationsRenderLabelFitsTheFooter(string destination)
    {
        (string label, double available, double wanted) = Wpf.Run(() =>
        {
            (string Label, double Available, double Wanted) result = default;
            Wpf.Show(new MontageRenderDialog(Montage()), window =>
            {
                var toggle = (ToggleButton)window.FindName(destination);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();
                Wpf.Pump();

                var render = (Button)window.FindName("renderBtn");
                string text = render.Content?.ToString() ?? "";
                var proxy = new TextBlock
                {
                    Text = text,
                    FontFamily = render.FontFamily,
                    FontSize = render.FontSize,
                    FontWeight = render.FontWeight,
                    FontStyle = render.FontStyle,
                    FontStretch = render.FontStretch,
                };
                proxy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double chrome = render.Padding.Left + render.Padding.Right +
                                render.BorderThickness.Left + render.BorderThickness.Right;
                result = (text, render.ActualWidth, proxy.DesiredSize.Width + chrome);
            });
            return result;
        });

        output.WriteLine($"{destination}: \"{label}\" given {available:F0} px, needs {wanted:F0} px");
        Assert.True(available + 0.5 >= wanted,
            $"\"{label}\" is clipped: {available:F0} px available, {wanted:F0} px needed.");
    }

    /// <summary>
    /// A render opens beside the montage it came from, so it cannot carry the montage's own name.
    /// Two tabs both reading "Side A" is indistinguishable from the render having done nothing, and
    /// that is how Render &amp; Prepare CD was reported: the CD window opened, and nothing else in
    /// the app appeared to have changed.
    /// </summary>
    /// <remarks>
    /// The title is read off the static rather than by pressing Render on a shown window: the render
    /// finishes with <c>DialogResult = true</c>, which throws on a window that was not shown modally,
    /// and this dialog is genuinely modal in the app. Wpf.Show cannot drive it.
    /// </remarks>
    [Fact]
    public void TheRenderedTabIsNamedApartFromTheMontage()
    {
        string montageTab = Wpf.Run(() => Montage().Title);
        string renderedTab = MontageRenderDialog.RenderedTitle(montageTab);

        Assert.Equal("Side A", montageTab);
        Assert.NotEqual(montageTab, renderedTab);
        Assert.Equal("Side A (render).wav", renderedTab);
    }

    [Theory]
    [InlineData(null, "Montage (render).wav")]
    [InlineData("", "Montage (render).wav")]
    [InlineData("   ", "Montage (render).wav")]
    [InlineData("  Side B  ", "Side B (render).wav")]
    public void AnUnnamedMontageStillRendersToANamedTab(string? title, string expected) =>
        Assert.Equal(expected, MontageRenderDialog.RenderedTitle(title));

    /// <summary>
    /// The CD destinations do not write anything themselves — they open the CD window on the
    /// rendered programme so the running order and catalogue numbers can be checked first, and that
    /// window's Export is what asks for a folder. This is the link that carries the clips across.
    /// </summary>
    [Fact]
    public void TheCdDestinationsHandOneTrackPerClipToTheCdWindow()
    {
        (int plans, int rows, bool visible) = Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            MontageViewModel montage = Montage();

            List<CdTrackPlan> trackPlan = [];
            Wpf.Show(new MontageRenderDialog(montage), window =>
                trackPlan = ((MontageRenderDialog)window).TrackPlan());

            // What MainWindow.OnMontageRender does with a CdPackage or DdpImage result.
            var rendered = new AudioDocument(
                [new float[Rate * 4], new float[Rate * 4]], Rate, 32) { Title = "Side A (render).wav" };
            main.AddDocument(rendered);
            DocumentViewModel document = main.ActiveDocument!;
            foreach (CdTrackPlan plan in trackPlan)
                document.Regions.Add(new NamedRegion
                {
                    Name = plan.Title,
                    Start = plan.SourceStart,
                    End = plan.SourceEnd,
                    CdTrackOrder = document.Regions.Count + 1,
                });

            CdTransferDialog cd = CdTransferDialog.ShowFor(document, main, null);
            Wpf.Pump();
            (int Plans, int Rows, bool Visible) result =
                (trackPlan.Count, ((ListBox)cd.FindName("trackList")).Items.Count, cd.IsVisible);

            cd.Close();
            long deadline = Environment.TickCount64 + 15_000;
            while (cd.IsVisible && Environment.TickCount64 < deadline) Wpf.Pump();
            return result;
        });

        Assert.Equal(1, plans);
        Assert.Equal(1, rows);
        Assert.True(visible, "the CD window did not open on the rendered programme");
    }
}
