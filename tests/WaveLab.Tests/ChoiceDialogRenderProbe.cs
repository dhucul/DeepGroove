using System.Windows;
using System.Windows.Controls;
using WaveLab.Audio.Dsp;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the Normalize Loudness ceiling prompt offscreen and measures its three choices in place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Render before claiming a layout works.</b> This dialog's labels are sentences carrying
/// figures rather than verbs — "Apply +9.8 dB and add a limiter" — and this repo's record on
/// labels that outgrow their control is long: the rack's render buttons came out as
/// <c>NDOABLE · SELECTION OR FIL</c>, a plugin name ran under the power LED, and
/// <c>SegmentButton</c> exists because <c>ToolButton</c>'s fixed 38 px width rendered "Waveform"
/// as "Wa".
/// </para>
/// <para>
/// So what is measured is that every choice is drawn whole. The buttons stretch and their content
/// wraps, which means the failure mode is a taller dialog rather than a clipped word — and that is
/// the property worth pinning, because it is the one that stops being true the moment someone sets
/// a fixed <c>Height</c> back on the style.
/// </para>
/// </remarks>
public sealed class ChoiceDialogRenderProbe(ITestOutputHelper output)
{
    private static CeilingChoice Choice()
    {
        var plan = LoudnessMatch.Plan(
            [new LoudnessMeasurement("Take 1", -21.8, -5.5, 6.0, 44_100, 44_100 * 30)],
            LoudnessMatchMode.Target,
            LoudnessTarget.CompactDisc);
        return LoudnessMatch.DescribeCeilingChoice(plan, plan.Steps[0]);
    }

    [Fact]
    public void EveryChoiceIsDrawnWholeAtTheDialogsOwnWidth()
    {
        CeilingChoice choice = Choice();
        var measured = new List<(string Label, double Wanted, double Given, double Height)>();

        Wpf.Run(() =>
        {
            var dialog = new ChoiceDialog("Normalize loudness", choice.Message, choice.Labels);
            Wpf.Show(dialog, window =>
            {
                window.UpdateLayout();
                Wpf.Pump();

                var panel = (StackPanel)window.FindName("buttons")!;
                for (int i = 0; i < panel.Children.Count; i++)
                {
                    var button = (Button)panel.Children[i];
                    var text = (TextBlock)button.Content;
                    // Measured unbounded in width, which is what the label wants; the button is
                    // what it was given. DesiredSize carries the element's own margin and
                    // ActualWidth does not — the trap that made the monitor bar and the spectral
                    // scale switch both read as clipped at every width — so the comparison is
                    // against the text, which has no margin of its own.
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    measured.Add((choice.Labels[i], text.DesiredSize.Width,
                        button.ActualWidth, button.ActualHeight));
                }
            });
        });

        Assert.Equal(3, measured.Count);
        foreach (var (label, wanted, given, height) in measured)
        {
            output.WriteLine($"{label,-46} wants {wanted,7:0.0} px, given {given,7:0.0}, {height,5:0.0} tall");
            Assert.True(given > 0, $"'{label}' was given no width at all");
            Assert.True(height >= 38, $"'{label}' is {height:0.0} px tall, under the 38 px minimum");
            // Wrapping is what makes this survivable, so the assertion is on the outcome — the
            // label is drawn whole — rather than on it fitting a line.
            Assert.True(wanted <= given || height > 38,
                $"'{label}' wants {wanted:0.0} px, was given {given:0.0}, and did not wrap");
        }
    }
}
