using System.Windows.Media;
using WaveLab.Audio;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the restoration workbench offscreen and measures the noise depth line in the real card.
/// </summary>
/// <remarks>
/// <b>Render before claiming a layout works.</b> The mockup's widths were measured with
/// <c>FormattedText</c>, which is the right glyph metric but not the real control: it says nothing
/// about whether the line was given the width the card has, whether it wraps, or whether adding it
/// pushed anything off the card. Every previous render in this repo found something no unit test
/// could — a 38 px segment button, a footer trimmed to "…Set.", a `TextBox` with no disabled state.
/// </remarks>
public sealed class NoiseDepthRenderProbe(ITestOutputHelper output)
{
    private static AudioDocument Document()
    {
        var left = new float[44_100 * 3];
        var right = new float[left.Length];
        var random = new Random(9);
        for (int i = 0; i < left.Length; i++)
        {
            float sample = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / 44_100.0)
                                 + (random.NextDouble() - 0.5) * 0.02);
            left[i] = sample;
            right[i] = sample;
        }
        return new AudioDocument([left, right], 44_100, 24);
    }

    [Fact]
    public void TheDepthLineFitsTheNoiseCard()
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

                // The longest wording the readout can produce, put straight into the control.
                var line = RestorationWorkbenchDialog.DescribeNoiseDepth(
                    analysed: true, hasProfile: true, requestedDb: 10, appliedDb: 0, estimateDb: 12.4);
                workbench.noiseDepthLead.Text = line.Lead;
                workbench.noiseDepthDetail.Text = $" · {line.Detail}";
                workbench.UpdateLayout();
                Wpf.Pump();

                double available = workbench.noiseDepthText.ActualWidth;
                var wanted = workbench.noiseDepthText.DesiredSize;
                double slider = workbench.noiseReduction.ActualWidth;

                output.WriteLine($"card slider {slider:F0} px, readout given {available:F0} px, " +
                    $"wants {wanted.Width:F0} px, {workbench.noiseDepthText.ActualHeight:F0} px tall");
                output.WriteLine($"line: {line}");

                Assert.True(available > 0, "the readout was given no width at all");
                Assert.True(workbench.noiseDepthText.ActualHeight < 24,
                    $"the line wrapped to {workbench.noiseDepthText.ActualHeight:F0} px");

                // Room to spare, so a longer wording or a wider glyph does not immediately clip.
                Assert.True(wanted.Width < available,
                    $"the line wants {wanted.Width:F0} px of {available:F0} and would be trimmed");

            });
        });
    }
}
