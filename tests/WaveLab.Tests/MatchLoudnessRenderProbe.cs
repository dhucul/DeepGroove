using System.Windows.Controls;
using WaveLab.Audio;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Measures the Match Loudness control strip in the built dialog at its declared minimum width.
/// </summary>
/// <remarks>
/// The target line is the longest string in the dialog that is not free to wrap — a name, a
/// loudness and a ceiling in one closed combo box — and the strip was three even columns, which
/// trimmed the last two characters off it: "≤ -1.0 dBT". A trimmed ceiling is the one number in
/// this dialog nobody can afford to misread, so the column is wider than its neighbours and this is
/// what holds it there. Rendered rather than reasoned about, for the same reason the noise-depth and
/// output-mix readouts are: column arithmetic off the XAML was wrong by a scroll bar both times.
/// </remarks>
public sealed class MatchLoudnessRenderProbe(ITestOutputHelper output)
{
    private static DocumentViewModel Document(string title) =>
        new(new AudioDocument([new float[44_100], new float[44_100]], 44_100, 32) { Title = title });

    [Fact]
    public void TheTargetLineFitsItsBoxAtTheDialogsMinimumWidth()
    {
        (double given, double wanted, string text) = Wpf.Run(() =>
        {
            var dialog = new MatchLoudnessDialog([Document("01 Side one.wav"), Document("02 Ballad.wav")]);
            (double Given, double Wanted, string Text) result = default;
            Wpf.Show(dialog, window =>
            {
                window.Width = 900;                       // the dialog's declared minimum
                window.UpdateLayout();
                Wpf.Pump();

                var combo = (ComboBox)window.FindName("targetCombo");
                // The longest of the presets, which is the one that has to fit.
                combo.SelectedIndex = 3;
                window.UpdateLayout();
                Wpf.Pump();

                result = (combo.ActualWidth, combo.DesiredSize.Width, combo.SelectedItem?.ToString() ?? "");
            });
            return result;
        });

        output.WriteLine($"target box {given:F1} px given, {wanted:F1} px wanted for \"{text}\"");

        Assert.Contains("dBTP", text);
        Assert.True(
            wanted <= given + 0.5,
            $"the target line wants {wanted:F1} px of {given:F1} and is being trimmed — "
            + $"\"{text}\" would lose its ceiling.");
    }
}
