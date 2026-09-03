using WaveLab.Audio;
using WaveLab.ViewModels;
using WaveLab.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace WaveLab.Tests;

/// <summary>The flat-transfer entry point must not silently fall back to the legacy equalised path.</summary>
public sealed class FlatVinylWorkflowTests
{
    private static AudioDocument Document() => new(
        [new float[48_000], new float[48_000]], 48_000, 24);

    private static bool PumpUntil(Func<bool> ready, int timeoutMs = 30_000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!ready() && Environment.TickCount64 < deadline) Wpf.Pump();
        return ready();
    }

    [Fact]
    public void FlatTransferStartsWithOneDiscCurveAndNoInvalidDryBlend()
    {
        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            var document = new DocumentViewModel(Document());
            var dialog = new RestorationWorkbenchDialog(document, main, startWithFlatTransfer: true);

            Assert.Equal(1, dialog.sourceModeCombo.SelectedIndex);
            Assert.Equal(0, dialog.curveCombo.SelectedIndex);
            Assert.Equal(true, dialog.curveCombo.ReadLocalValue(UIElement.IsEnabledProperty));
            Assert.Equal(100, dialog.globalMix.Value);
            Assert.False(dialog.globalMix.IsEnabled);
            Assert.False(dialog.keepRemovedCheck.IsEnabled);
            Assert.Contains("applied once", dialog.curveSummaryText.Text);
        });
    }

    [Fact]
    public void EqualizedTransferDoesNotApplyAnotherDiscCurve()
    {
        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            var document = new DocumentViewModel(Document());
            var dialog = new RestorationWorkbenchDialog(document, main);

            Assert.Equal(0, dialog.sourceModeCombo.SelectedIndex);
            Assert.False(dialog.curveCombo.IsEnabled);
            Assert.Equal(true, dialog.globalMix.ReadLocalValue(UIElement.IsEnabledProperty));
            Assert.Contains("no disc curve", dialog.curveSummaryText.Text);
        });
    }

    [Fact]
    public void FlatWorkflowAppliesPlaybackRiaaAsPartOfItsSingleUndoableEdit()
    {
        Wpf.Run(() =>
        {
            const int rate = 48_000;
            var left = new float[rate * 2];
            var right = new float[left.Length];
            for (int sample = 0; sample < left.Length; sample++)
                left[sample] = right[sample] = (float)(0.2 * Math.Sin(2 * Math.PI * 10_000 * sample / rate));

            using var main = new MainViewModel();
            main.AddDocument(new AudioDocument([left, right], rate, 24));
            DocumentViewModel document = main.ActiveDocument!;
            var dialog = new RestorationWorkbenchDialog(document, main, startWithFlatTransfer: true);

            Wpf.Show(dialog, window =>
            {
                var workbench = (RestorationWorkbenchDialog)window;
                var apply = (Button)window.FindName("applyBtn");
                Assert.True(PumpUntil(() => apply.IsEnabled), "the flat-transfer analysis never enabled Apply");

                workbench.removeDcEnabled.IsChecked = false;
                workbench.azimuthEnabled.IsChecked = false;
                workbench.wowEnabled.IsChecked = false;
                workbench.declipEnabled.IsChecked = false;
                workbench.clickEnabled.IsChecked = false;
                workbench.decrackleEnabled.IsChecked = false;
                workbench.subsonicEnabled.IsChecked = false;
                workbench.verticalEnabled.IsChecked = false;
                workbench.humEnabled.IsChecked = false;
                workbench.noiseEnabled.IsChecked = false;

                apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(PumpUntil(() => !window.IsVisible), "the flat-transfer render never completed");
            });

            Assert.Equal("Flat Vinyl Transfer", document.Doc.NextUndoName);
            double outputRms = Math.Sqrt(document.Doc.Channels[0]
                .Skip(rate / 2).Take(rate).Average(value => value * value));
            Assert.InRange(outputRms, 0.015, 0.06);
        });
    }
}
