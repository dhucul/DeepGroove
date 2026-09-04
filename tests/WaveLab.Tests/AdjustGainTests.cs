using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class AdjustGainTests : IDisposable
{
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public AdjustGainTests() => AppSettings.AppDataDir = _sandbox;
    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData("+5.0", true, 5)]
    [InlineData("-3.5", true, -3.5)]
    [InlineData("60", true, 60)]
    [InlineData("-60", true, -60)]
    [InlineData("NaN", false, 0)]
    [InlineData("Infinity", false, 0)]
    [InlineData("60.1", false, 0)]
    [InlineData("-60.1", false, 0)]
    [InlineData("5.25", false, 0)]
    [InlineData("", false, 0)]
    public void NumericInputIsValidated(string input, bool expected, double value)
    {
        Assert.Equal(expected, AdjustGainDialog.TryParseGain(input, out double actual));
        if (expected) Assert.Equal(value, actual, 8);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    [InlineData(0)]
    public void GainIsExactLinkedAndNotPeakNormalization(double db)
    {
        float[][] data = [[0.1f, -0.2f], [0.05f, -0.1f]];
        double peak = Processing.AdjustGainInPlace(data, db);
        double gain = Math.Pow(10, db / 20);
        Assert.Equal(0.1 * gain, data[0][0], 6);
        Assert.Equal(-0.2 * gain, data[0][1], 6);
        Assert.Equal(2, data[0][0] / data[1][0], 6);
        Assert.Equal(0.2 * gain, peak, 6);
    }

    [Fact]
    public void FloatingPointOversAreReportedAndNotSilentlyLimited()
    {
        float[][] data = [[0.5f]];
        Assert.Equal(5, Processing.AdjustGainInPlace(data, 20), 6);
        Assert.Equal(5, data[0][0]);
    }

    [Fact]
    public void InvalidGainAndCancellationCannotReachTheCommit()
    {
        float[][] data = [[0.1f]];
        Assert.Throws<ArgumentOutOfRangeException>(() => Processing.AdjustGainInPlace(data, double.NaN));
        Assert.Throws<OperationCanceledException>(() =>
            Processing.AdjustGainInPlace(data, 5, new CancellationToken(true)));
        Assert.Equal(0.1f, data[0][0]);
    }

    [Fact]
    public void DialogDisallowsInvalidAndZeroGainAndWarnsForBoosts()
    {
        Wpf.Run(() => Wpf.Show(new AdjustGainDialog("Selected range · test.wav"), window =>
        {
            var dialog = (AdjustGainDialog)window;
            Assert.False(dialog.applyBtn.IsEnabled);
            dialog.gainText.Text = "+5.0";
            Assert.True(dialog.applyBtn.IsEnabled);
            Assert.Equal(Visibility.Visible, dialog.clippingText.Visibility);
            dialog.gainText.Text = "-3.0";
            Assert.True(dialog.applyBtn.IsEnabled);
            Assert.Equal(Visibility.Collapsed, dialog.clippingText.Visibility);
            dialog.gainText.Text = "NaN";
            Assert.False(dialog.applyBtn.IsEnabled);
        }));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void ShellAppliesOneUndoableEditOrDeclines(bool cancel, bool selectionChanged, bool wholeFile)
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), window =>
        {
            var main = (MainViewModel)window.DataContext;
            var audio = new AudioDocument([[0.1f, 0.2f, -0.2f, 0.1f], [0.05f, 0.1f, -0.1f, 0.05f]], 48_000, 24)
            {
                Title = "test.wav", DiscSignalState = DiscSignalState.Flat,
            };
            float[] originalLeft = audio.Channels[0];
            main.AddDocument(audio);
            if (!wholeFile) main.ActiveDocument!.SetSelection(1, 3);
            try
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = window.OwnedWindows.OfType<AdjustGainDialog>().Single();
                    dialog.gainText.Text = "+5.0";
                    if (selectionChanged) main.ActiveDocument!.SetSelection(0, 1);
                    if (cancel) dialog.DialogResult = false;
                    else dialog.applyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }), DispatcherPriority.ApplicationIdle);
                typeof(MainWindow).GetMethod("OnAdjustGain", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                long deadline = Environment.TickCount64 + 30_000;
                bool expectedApply = !cancel && !selectionChanged;
                while (expectedApply && audio.HistoryPosition == 0 && Environment.TickCount64 < deadline) Wpf.Pump();
                Assert.Equal(expectedApply ? 1 : 0, audio.HistoryPosition);
                Assert.Equal(DiscSignalState.Flat, audio.DiscSignalState);
                double outsideGain = expectedApply && wholeFile ? Math.Pow(10, 5.0 / 20) : 1;
                Assert.Equal(0.1 * outsideGain, audio.Channels[0][0], 6);
                Assert.Equal(0.1 * outsideGain, audio.Channels[0][3], 6);
                if (expectedApply)
                {
                    Assert.Equal(0.2 * Math.Pow(10, 5.0 / 20), audio.Channels[0][1], 6);
                    Assert.Equal(2, audio.Channels[0][1] / audio.Channels[1][1], 6);
                    Assert.Equal("Gain +5.0 dB", audio.NextUndoName);
                    Assert.Equal(wholeFile, Assert.Single(audio.GetHistory().Entries).OwnsFullDocument);
                    float[] renderedLeft = audio.Channels[0];
                    audio.Undo();
                    if (wholeFile)
                    {
                        Assert.Same(originalLeft, audio.Channels[0]);
                        audio.Redo();
                        Assert.Same(renderedLeft, audio.Channels[0]);
                        audio.Undo();
                    }
                }
                Assert.Equal(0.2f, audio.Channels[0][1]);
            }
            finally { audio.MarkSaved(); }
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterScalingButBeforeCommitLeavesTheDocumentUntouched(bool wholeFile)
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), window =>
        {
            var main = (MainViewModel)window.DataContext;
            var audio = new AudioDocument([[0.1f, 0.2f, -0.2f, 0.1f]], 48_000, 24)
            {
                Title = "cancel-test.wav", DiscSignalState = DiscSignalState.Flat,
            };
            float[] original = audio.Channels[0];
            main.AddDocument(audio);
            if (!wholeFile) main.ActiveDocument!.SetSelection(1, 3);
            bool scaled = false;
            try
            {
                Func<float[][], int, IProgress<double>, CancellationToken, float[][]?> transform =
                    (data, _, _, token) =>
                    {
                        Processing.AdjustGainInPlace(data, 5, token);
                        scaled = true;
                        // The gain loop has finished and will perform no further token checks.
                        // Cancel on the UI thread before returning its result for commit.
                        window.Dispatcher.Invoke(() => main.Progress.ActiveBlockingOperation!.Cancel());
                        return data;
                    };
                MethodInfo runner = typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Single(method => method.Name == "RunRangeTool" && method.GetParameters().Length == 6);
                var run = (Task<bool>)runner.Invoke(window,
                    ["Gain +5.0 dB", null, transform, main.ActiveDocument, false, null])!;
                long deadline = Environment.TickCount64 + 30_000;
                while (!run.IsCompleted && Environment.TickCount64 < deadline) Wpf.Pump();
                Assert.True(run.IsCompleted, "gain processing did not finish");
                Assert.True(scaled, "the test must cancel after scaling, not before it starts");
                Assert.False(run.GetAwaiter().GetResult());
                Assert.Equal(0, audio.HistoryPosition);
                Assert.Same(original, audio.Channels[0]);
                Assert.Equal(0.2f, audio.Channels[0][1]);
                Assert.Equal(DiscSignalState.Flat, audio.DiscSignalState);
                Assert.False(main.IsDocumentOperationRunning);
            }
            finally { audio.MarkSaved(); }
        }));
    }
}
