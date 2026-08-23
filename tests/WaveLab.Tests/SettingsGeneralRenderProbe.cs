using System.Windows;
using System.Windows.Controls;
using WaveLab.Util;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Renders the Settings dialog's General page offscreen after the noise depth ceiling was added.
/// </summary>
/// <remarks>
/// <b>That page is a bare <c>StackPanel</c>, where the Audio page is a <c>ScrollViewer</c>.</b> So
/// content added to it has nowhere to go once it outgrows the dialog — it does not scroll, it is
/// simply not there, and the control furthest down is the one that disappears. Every render in this
/// repo has found something no unit test could; this is the specific thing to look for here.
/// </remarks>
public sealed class SettingsGeneralRenderProbe(ITestOutputHelper output)
{
    [Fact]
    public void TheCeilingControlFitsTheGeneralPage()
    {
        string originalAppData = AppSettings.AppDataDir;
        string sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        AppSettings.AppDataDir = sandbox;
        try
        {
            double pageHeight = 0, hostHeight = 0, sliderWidth = 0;
            double warningWidth = 0, warningWanted = 0, warningHeight = 0;
            string warning = "", readout = "";

            Wpf.Run(() =>
            {
                var dialog = new SettingsDialog();
                Wpf.Show(dialog, window =>
                {
                    var settings = (SettingsDialog)window;
                    settings.Width = 900;
                    settings.Height = 620;
                    settings.UpdateLayout();
                    Wpf.Pump();

                    // The worst case: raised off the default, so the amber trade line is showing.
                    settings.sldNoiseCeiling.Value = 40;
                    settings.UpdateLayout();
                    Wpf.Pump();

                    sliderWidth = settings.sldNoiseCeiling.ActualWidth;
                    readout = settings.lblNoiseCeiling.Text;
                    warning = settings.lblNoiseCeilingWarning.Text;
                    warningWidth = settings.lblNoiseCeilingWarning.ActualWidth;
                    warningWanted = settings.lblNoiseCeilingWarning.DesiredSize.Width;
                    warningHeight = settings.lblNoiseCeilingWarning.ActualHeight;

                    // DesiredSize, not ActualHeight: the panel is stretched to its host, so its
                    // ActualHeight is the host's whatever the content does. What the content wants
                    // is the number that says whether any of it is being clipped away.
                    pageHeight = settings.pageGeneral.DesiredSize.Height;
                    hostHeight = settings.pageGeneral.Parent is FrameworkElement host
                        ? host.ActualHeight
                        : double.NaN;
                });
            });

            output.WriteLine($"slider {sliderWidth:F0} px, readout \"{readout}\"");
            output.WriteLine($"page wants {pageHeight:F0} px inside a host of {hostHeight:F0} px");
            output.WriteLine($"trade line: given {warningWidth:F0} px, wants {warningWanted:F0}, " +
                             $"{warningHeight:F0} px tall");
            output.WriteLine(warning);

            Assert.True(sliderWidth > 100, $"the slider got {sliderWidth:F0} px");
            Assert.Equal("40 dB", readout);
            Assert.Contains("measured optimum", warning);

            // The page must still fit what holds it, because nothing here scrolls.
            Assert.True(pageHeight > 0, "the General page measured nothing at all");
            Assert.True(double.IsNaN(hostHeight) || pageHeight <= hostHeight + 0.5,
                $"the General page wants {pageHeight:F0} px inside {hostHeight:F0} px and does not scroll");

            // And the trade line must wrap rather than run off the page.
            Assert.True(warningWanted <= warningWidth + 0.5,
                $"the trade line wants {warningWanted:F0} px of {warningWidth:F0}");
        }
        finally
        {
            AppSettings.AppDataDir = originalAppData;
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
            catch { /* a locked temp directory must not fail the run */ }
        }
    }
}
