using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the restoration workbench offscreen and measures the "keep what was removed" option in
/// the real output card.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="NoiseDepthRenderProbe"/>: the caption carries a memory figure, and
/// a figure the user cannot read before ticking the box is not worth printing. The option was put
/// on its own full-width row rather than beside the two checkboxes already there precisely because
/// a render is the only thing that can say whether it fits, and 270 px was not going to.
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class KeepRemovedRenderProbe(ITestOutputHelper output)
{
    private static AudioDocument Document()
    {
        var left = new float[44_100 * 3];
        var right = new float[left.Length];
        var random = new Random(4);
        for (int i = 0; i < left.Length; i++)
        {
            float sample = (float)(0.25 * Math.Sin(2 * Math.PI * 330 * i / 44_100.0)
                                 + (random.NextDouble() - 0.5) * 0.02);
            left[i] = sample;
            right[i] = sample;
        }
        return new AudioDocument([left, right], 44_100, 24);
    }

    [Fact]
    public void TheOptionAndItsCostCaptionFitTheOutputCard()
    {
        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            var document = new DocumentViewModel(Document());
            var dialog = new RestorationWorkbenchDialog(document, main);

            Wpf.Show(dialog, window =>
            {
                var workbench = (RestorationWorkbenchDialog)window;
                workbench.Width = 860;                       // the declared minimum
                workbench.UpdateLayout();
                Wpf.Pump();

                // The longest caption the rule can produce: an hour-long side at 96 kHz.
                workbench.keepRemovedCaption.Text = ResidualSummary.DescribeCost(60L * 60 * 96_000, 2);
                workbench.UpdateLayout();
                Wpf.Pump();

                double available = workbench.keepRemovedCaption.ActualWidth;
                double height = workbench.keepRemovedCaption.ActualHeight;
                output.WriteLine($"caption given {available:F0} px, {height:F0} px tall");
                output.WriteLine($"caption: {workbench.keepRemovedCaption.Text}");
                output.WriteLine($"checkbox: {workbench.keepRemovedCheck.ActualWidth:F0} x " +
                                 $"{workbench.keepRemovedCheck.ActualHeight:F0} px");

                Assert.True(available > 0, "the caption was given no width at all");
                Assert.True(workbench.keepRemovedCheck.ActualWidth > 0, "the option was not laid out");
                // CardCaption wraps rather than truncating, which is the fix that line inherited
                // from the hum caption; two lines is the most it may take.
                Assert.True(height <= 40, $"the caption ran to {height:F0} px, which is three lines or more");
            });
        });
    }

    /// <summary>
    /// The box starts where the last record left it. Checked against the sandboxed settings root
    /// so the developer's real preference is neither read nor written.
    /// </summary>
    [Fact]
    public void TheOptionOpensWhereTheLastPassLeftIt()
    {
        string original = AppSettings.AppDataDir;
        string sandbox = Path.Combine(Path.GetTempPath(), $"wavelab-keep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        try
        {
            AppSettings.AppDataDir = sandbox;
            AppSettings.Instance.KeepRemovedMaterial = true;

            bool ticked = Wpf.Run(() =>
            {
                using var main = new MainViewModel();
                var document = new DocumentViewModel(Document());
                var dialog = new RestorationWorkbenchDialog(document, main);
                bool result = false;
                Wpf.Show(dialog, window =>
                    result = ((RestorationWorkbenchDialog)window).keepRemovedCheck.IsChecked == true);
                return result;
            });

            Assert.True(ticked);
        }
        finally
        {
            AppSettings.AppDataDir = original;
            try { Directory.Delete(sandbox, recursive: true); } catch (IOException) { }
        }
    }
}
