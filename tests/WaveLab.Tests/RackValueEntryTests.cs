using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

public sealed class RackValueEntryTests
{
    private static EffectParamViewModel Parameter(string type, string key, Action? changed = null)
    {
        var effect = EffectFactory.Create(type);
        return new(effect, effect.Params.Single(p => p.Key == key), changed ?? (() => { }));
    }

    [Theory]
    [InlineData("mono-stereo", "amount", "75", 0.75)]
    [InlineData("mono-stereo", "amount", "75.25%", 0.7525)]
    [InlineData("mono-stereo", "delay", "18.125 ms", 18.125)]
    [InlineData("eq", "highFreq", "3500", 3500)]
    [InlineData("eq", "highFreq", "3.5 kHz", 3500)]
    [InlineData("eq", "midGain", "−2.75 dB", -2.75)]
    [InlineData("eq", "midQ", "0.707123", 0.707123)]
    [InlineData("compressor", "ratio", "3.25:1", 3.25)]
    [InlineData("stereo-width", "width", "175%", 1.75)]
    [InlineData("mono-stereo", "algorithm", "3", 3)]
    public void NumbersUseTheControlsUnitsWithoutRounding(string type, string key, string text, double expected)
    {
        var vm = Parameter(type, key);
        Assert.True(vm.TryParseEntry(text, out double value, out string error), error);
        vm.Value = value;
        Assert.Equal(expected, vm.Value, 12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("75 Hz")]
    public void InvalidAndOutOfRangeInputDoesNotChangeTheEffect(string text)
    {
        int changes = 0;
        var vm = Parameter("mono-stereo", "amount", () => changes++);
        double before = vm.Value;
        Assert.False(vm.TryParseEntry(text, out _, out _));
        Assert.Equal(before, vm.Value);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void FixedStepsAreEnforcedAndPreviewUsesTheActualModeName()
    {
        var vm = Parameter("deemphasis", "standard");
        Assert.False(vm.TryParseEntry("1.5", out _, out _));
        Assert.True(vm.TryParseEntry("2", out double value, out _));
        Assert.Contains("CD", vm.FormatEntryValue(value));
    }

    [Fact]
    public void CommaDecimalsAndInvariantDecimalsAreBothSupported()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var vm = Parameter("mono-stereo", "delay");
            Assert.True(vm.TryParseEntry("18,125", out double local, out _));
            Assert.True(vm.TryParseEntry("18.125", out double invariant, out _));
            Assert.Equal(18.125, local);
            Assert.Equal(local, invariant);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void EveryBuiltInParameterCanReenterItsCurrentValue()
    {
        foreach ((string type, _) in EffectFactory.Available)
        {
            var fx = EffectFactory.Create(type);
            foreach (var param in fx.Params)
            {
                var vm = new EffectParamViewModel(fx, param, () => { });
                Assert.True(vm.TryParseEntry(vm.EntryText, out double value, out string error), $"{type}/{param.Key}: {error}");
                Assert.Equal(vm.Value, value, 10);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DialogAppliesOnceOrCancelsWithoutChangingTheEffect(bool cancel)
    {
        Wpf.Run(() =>
        {
            int changes = 0;
            var parameter = Parameter("mono-stereo", "amount", () => changes++);
            double before = parameter.Value;
            var dialog = new RackValueDialog(parameter)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowActivated = false,
            };
            Exception? failure = null;
            dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    dialog.valueInput.Text = "101";
                    Assert.False(dialog.applyBtn.IsEnabled);
                    dialog.valueInput.Text = "75.25";
                    Assert.True(dialog.applyBtn.IsEnabled);
                    Assert.Equal("Will set: 75.25 %", dialog.previewText.Text);
                    Assert.Equal(before, parameter.Value);
                    if (cancel) dialog.DialogResult = false;
                    else dialog.applyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                catch (Exception ex) { failure = ex; dialog.DialogResult = false; }
            }), DispatcherPriority.ApplicationIdle);
            Assert.Equal(!cancel, dialog.ShowDialog());
            if (failure != null) throw failure;
            Assert.Equal(cancel ? before : 0.7525, parameter.Value, 12);
            Assert.Equal(cancel ? 0 : 1, changes);
        });
    }

    [Fact]
    public void EntryDialogKeepsValidationAndButtonsVisible()
    {
        Wpf.Run(() => Wpf.Show(new RackValueDialog(Parameter("mono-stereo", "amount")), window =>
        {
            var dialog = (RackValueDialog)window;
            dialog.valueInput.Text = "101";
            dialog.UpdateLayout();
            Assert.True(dialog.errorText.ActualHeight > 0);
            var bottom = dialog.applyBtn.TransformToAncestor(dialog)
                .Transform(new Point(0, dialog.applyBtn.ActualHeight));
            Assert.True(bottom.Y < dialog.ActualHeight);
            dialog.valueInput.Text = "75.25";
            dialog.UpdateLayout();
            // Optional visual review artifact, kept outside the application's data folders.
            if (Environment.GetEnvironmentVariable("WAVELAB_RACK_ENTRY_PREVIEW") is { Length: > 0 } path)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),
                    (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(dialog);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(path);
                encoder.Save(stream);
            }
        }));
    }

    [Fact]
    public void ManualEntryResumesRackPreviewAndUpdatesTheRealParameter()
    {
        using var master = new MasterSection();
        master.ReplaceChain([new MonoToStereoEffect()]);
        var rack = new MasterSectionViewModel(master);
        rack.BypassAfterRender();
        var parameter = rack.Effects[0].Params.Single(p => p.Label == "AMOUNT");
        Assert.True(parameter.TryParseEntry("75", out double value, out _));
        parameter.Value = value;
        Assert.True(master.RackEnabled);
        Assert.Equal(0.75, master.ChainSnapshot[0].GetParam("amount"));
    }
}
