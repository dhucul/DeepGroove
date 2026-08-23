using WaveLab.Audio;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the restoration workbench offscreen and measures the output-mix ceiling line in place.
/// </summary>
/// <remarks>
/// <b>Render before claiming a layout works.</b> The mockup's width came from the XAML column
/// arithmetic — 817 px of content at the 860 px minimum, less 150 and 270 for the outer columns and
/// 28 px of margin, so 339 px — and that is a calculation rather than a measurement. This card is
/// also the one place in the dialog where a readout was added to a row that had two columns beside
/// it, so it is the layout most likely to have taken its room from something else.
/// </remarks>
public sealed class OutputMixRenderProbe(ITestOutputHelper output)
{
    private static AudioDocument Document()
    {
        var left = new float[44_100];
        var right = new float[left.Length];
        for (int i = 0; i < left.Length; i++)
            left[i] = right[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / 44_100.0));
        return new AudioDocument([left, right], 44_100, 24);
    }

    [Fact]
    public void TheMixCeilingLineFitsTheOutputCard()
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

                // The widest wording the line can produce. Not a ceiling: the deepest ceilings carry
                // the shortest detail, so the fully dry line is the one that has to fit.
                var line = RestorationWorkbenchDialog.DescribeOutputMix(0, bypass: false);
                workbench.mixCeilingLead.Text = line.Lead;
                workbench.mixCeilingDetail.Text = $" · {line.Detail}";
                workbench.UpdateLayout();
                Wpf.Pump();

                double available = workbench.mixCeilingText.ActualWidth;
                double wanted = workbench.mixCeilingText.DesiredSize.Width;
                double slider = workbench.globalMix.ActualWidth;

                output.WriteLine($"mix slider {slider:F0} px, readout given {available:F0} px, " +
                    $"wants {wanted:F0} px, {workbench.mixCeilingText.ActualHeight:F0} px tall");
                output.WriteLine($"line: {line}");

                Assert.True(available > 0, "the readout was given no width at all");
                Assert.True(workbench.mixCeilingText.ActualHeight < 24,
                    $"the line wrapped to {workbench.mixCeilingText.ActualHeight:F0} px");
                Assert.True(wanted < available,
                    $"the line wants {wanted:F0} px of {available:F0} and would be trimmed");

                // The row it was added to still has to leave the audition controls their width -
                // adding a third row must not have squeezed the column beside it.
                Assert.True(workbench.auditionChannelCombo.ActualWidth >= 120,
                    $"the audition combo came out {workbench.auditionChannelCombo.ActualWidth:F0} px wide");
            });
        });
    }
}
