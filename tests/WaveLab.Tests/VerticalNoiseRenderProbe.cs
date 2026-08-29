using WaveLab.Audio;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the workbench with its three new cards offscreen and measures them in place.
/// </summary>
/// <remarks>
/// The card grid went from two rows to four, so this is not only a question about three captions —
/// it is whether the panel still lays out at the dialog's declared 860 px minimum and whether
/// anything fell off the bottom. Every render in this repo has found something no unit test could,
/// and the two faults nearest this one are the same shape: a caption with no <c>TextWrapping</c>
/// cut to "without shifting stereo alignm", and a footer trimmed to "…Set.".
/// </remarks>
public sealed class VerticalNoiseRenderProbe(ITestOutputHelper output)
{
    private static AudioDocument Document()
    {
        var left = new float[44_100 * 3];
        var right = new float[left.Length];
        var random = new Random(11);
        for (int i = 0; i < left.Length; i++)
        {
            double t = i / 44_100.0;
            double music = 0.25 * Math.Sin(2 * Math.PI * 440 * t);
            double vertical = 0.01 * (random.NextDouble() - 0.5);
            left[i] = (float)(music + vertical);
            right[i] = (float)(music - vertical);
        }
        return new AudioDocument([left, right], 44_100, 24);
    }

    [Fact]
    public void TheThreeNewCardsFitTheWorkbenchAtItsMinimumWidth()
    {
        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            var document = new DocumentViewModel(Document());
            var dialog = new RestorationWorkbenchDialog(document, main);

            double subsonicSlider = 0, sideSlider = 0, crackleSlider = 0;
            double subsonicWanted = 0, sideWanted = 0, crackleWanted = 0;
            double subsonicGiven = 0, sideGiven = 0, crackleGiven = 0;
            double subsonicHeight = 0, sideHeight = 0, crackleHeight = 0;
            string sideLine = "", crackleLine = "";

            Wpf.Show(dialog, window =>
            {
                var workbench = (RestorationWorkbenchDialog)window;
                workbench.Width = 860;                       // the declared minimum
                workbench.UpdateLayout();
                Wpf.Pump();

                // The longest wording each line can produce, put straight into the real control —
                // the same discipline the noise depth probe follows, for the same reason.
                sideLine = RestorationWorkbenchDialog.DescribeSideLevel(
                    enabled: true, analysed: true, stereo: true, sideToMidDb: -11.0, level: 0.25);
                crackleLine = RestorationWorkbenchDialog.DescribeCrackle(
                    enabled: true, analysed: true, events: 61_728, candidates: 123_456,
                    secondsAnalyzed: 12, threshold: 2.0);

                workbench.sideEvidenceText.Text = sideLine;
                workbench.decrackleEvidenceText.Text = crackleLine;
                workbench.subsonicEvidenceText.Text =
                    "Subsonic energy is +18.3 dB relative to bass fundamentals.";
                workbench.UpdateLayout();
                Wpf.Pump();

                subsonicSlider = workbench.subsonicCutoff.ActualWidth;
                sideSlider = workbench.sideLevel.ActualWidth;
                crackleSlider = workbench.decrackleThreshold.ActualWidth;

                subsonicGiven = workbench.subsonicEvidenceText.ActualWidth;
                sideGiven = workbench.sideEvidenceText.ActualWidth;
                crackleGiven = workbench.decrackleEvidenceText.ActualWidth;

                subsonicWanted = workbench.subsonicEvidenceText.DesiredSize.Width;
                sideWanted = workbench.sideEvidenceText.DesiredSize.Width;
                crackleWanted = workbench.decrackleEvidenceText.DesiredSize.Width;

                subsonicHeight = workbench.subsonicEvidenceText.ActualHeight;
                sideHeight = workbench.sideEvidenceText.ActualHeight;
                crackleHeight = workbench.decrackleEvidenceText.ActualHeight;
            });

            output.WriteLine($"subsonic card: slider {subsonicSlider:F0} px, evidence given " +
                             $"{subsonicGiven:F0} px, {subsonicHeight:F0} px tall");
            output.WriteLine($"side card:     slider {sideSlider:F0} px, evidence given " +
                             $"{sideGiven:F0} px, {sideHeight:F0} px tall");
            output.WriteLine($"crackle card:  slider {crackleSlider:F0} px, evidence given " +
                             $"{crackleGiven:F0} px, {crackleHeight:F0} px tall");
            output.WriteLine($"side line:    {sideLine}");
            output.WriteLine($"crackle line: {crackleLine}");

            // Every card was laid out at all — a card in a row that does not exist measures zero.
            Assert.True(subsonicSlider > 100, $"the subsonic card got {subsonicSlider:F0} px");
            Assert.True(sideSlider > 100, $"the side card got {sideSlider:F0} px");
            Assert.True(crackleSlider > 100, $"the crackle card got {crackleSlider:F0} px");

            // These three lines are allowed to wrap, unlike the noise depth line — they carry a
            // whole argument rather than two numbers. What they may not do is run off the card,
            // which is what a missing TextWrapping looks like when it is measured.
            Assert.True(subsonicWanted <= subsonicGiven + 0.5,
                $"the subsonic line wants {subsonicWanted:F0} px of {subsonicGiven:F0}");
            Assert.True(sideWanted <= sideGiven + 0.5,
                $"the side line wants {sideWanted:F0} px of {sideGiven:F0}");
            Assert.True(crackleWanted <= crackleGiven + 0.5,
                $"the crackle line wants {crackleWanted:F0} px of {crackleGiven:F0}");

            // And they may not grow without bound: four lines of 10.5 px text is about 60 px, past
            // which the card is a paragraph rather than a caption.
            Assert.True(sideHeight < 80, $"the side line is {sideHeight:F0} px tall");
            Assert.True(crackleHeight < 80, $"the crackle line is {crackleHeight:F0} px tall");
        });
    }
}
