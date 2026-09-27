using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class LyricsTransportTests : IDisposable
{
    [Fact]
    public void RestartPreviewYieldsDuringRealCleanupAndCancellationPreventsReopeningTheDevice()
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), shell =>
        {
            var main = Assert.IsType<MainViewModel>(shell.DataContext);
            var preview = new AudioDocument([new float[80]], 1000, 32);
            var (_, output) = SeedPlayingPreview(main, preview);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.ReleaseGate = release;
            using var cancel = new CancellationTokenSource();
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Task<bool> restarting = main.RestartPreviewAsync(preview, false, cancel.Token);
                Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500));
                Assert.False(restarting.IsCompleted);
                Assert.False(main.Engine.IsPlaying);
                Assert.True(main.Engine.Master.RackEnabled);
                bool responsive = false;
                shell.Dispatcher.BeginInvoke(new Action(() => responsive = true));
                Wpf.Pump();
                Assert.True(responsive);
                cancel.Cancel();
                long deadline = Environment.TickCount64 + 5000;
                while (!restarting.IsCompleted && Environment.TickCount64 < deadline) { Wpf.Pump(); Thread.Sleep(5); }
                Assert.True(restarting.IsCanceled);
                Assert.Equal(0, output.Plays);
                Assert.Null(main.Engine.SourceDocument);
            }
            finally { release.TrySetResult(); }
        }));
    }

    [Fact]
    public void PreviewPauseAndResumeKeepTheSameStreamPositionAndFullLoopRange()
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), shell =>
        {
            var main = Assert.IsType<MainViewModel>(shell.DataContext);
            var preview = new AudioDocument([Enumerable.Range(0, 80).Select(i => i / 100f).ToArray()], 1000, 32);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var (provider, output) = SeedPlayingPreview(main, preview);
            Assert.False(main.PausePreview(new AudioDocument([new float[80]], 1000, 32)));
            Assert.True(main.PausePreview(preview));
            int stopped = main.Engine.PositionSamples;
            Assert.InRange(stopped, 15, 16);
            Assert.True(main.PausePreview(preview));
            Assert.Equal(stopped, main.Engine.PositionSamples);
            Assert.Equal(1, output.Pauses);
            Assert.True(main.ResumePreview(preview, false));
            Assert.Same(provider, typeof(PlaybackEngine).GetField("_provider", fields)!.GetValue(main.Engine));
            Assert.True(main.IsPlaying);
            var next = new float[1];
            Assert.Equal(1, provider.Read(next));
            Assert.Equal(.16f, next[0]);
            Assert.True(main.PausePreview(preview));
            Assert.True(main.ResumePreview(preview, true));
            var loop = new float[80];
            Assert.Equal(80, provider.Read(loop));
            Assert.Equal(.17f, loop[0]);
            Assert.Equal(0f, loop[63]); // Loop returns to the recording's beginning.
            Assert.Equal(2, output.Plays);
            Assert.False(main.Engine.Master.RackEnabled);
            main.StopPreview();
            Assert.False(main.Engine.IsPaused);
            Assert.False(main.Engine.IsPlaying);
            Assert.True(main.Engine.Master.RackEnabled);
            Assert.False(main.ResumePreview(preview, false));
        }));
    }

    private static (ISampleProvider Provider, PreviewPlayer Output) SeedPlayingPreview(MainViewModel main, AudioDocument preview)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var providerType = typeof(PlaybackEngine).GetNestedType("DocumentProvider", BindingFlags.NonPublic)!;
        var provider = (ISampleProvider)Activator.CreateInstance(providerType, preview, 0, (int?)80, false)!;
        provider.Read(new float[36]); // 20 silent frames, then 16 frames of audio.
        var output = new PreviewPlayer();
        typeof(PlaybackEngine).GetField("_out", fields)!.SetValue(main.Engine, output);
        typeof(PlaybackEngine).GetField("_provider", fields)!.SetValue(main.Engine, provider);
        typeof(PlaybackEngine).GetField("_positionClockAccumulatedTicks", fields)!.SetValue(main.Engine,
            (long)(36.0 * System.Diagnostics.Stopwatch.Frequency / 1000));
        typeof(PlaybackEngine).GetProperty(nameof(PlaybackEngine.SourceDocument))!.SetValue(main.Engine, preview);
        typeof(PlaybackEngine).GetProperty(nameof(PlaybackEngine.IsPlaying))!.SetValue(main.Engine, true);
        typeof(MainViewModel).GetField("_previewDocument", fields)!.SetValue(main, preview);
        typeof(MainViewModel).GetField("_previewRackRestoreState", fields)!.SetValue(main, (bool?)true);
        main.Engine.Master.RackEnabled = false;
        return (provider, output);
    }

    [Fact]
    public void RepeatedPlayInTheActualLyricsDialogKeepsTheActiveDeviceAndPosition()
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), shell =>
        {
            var main = Assert.IsType<MainViewModel>(shell.DataContext);
            var source = new AudioDocument([Enumerable.Range(0, 80).Select(i => i / 100f).ToArray()], 1000, 32);
            main.AddDocument(source);
            bool inspected = false;
            shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = shell.OwnedWindows.OfType<LyricsDialog>().Single();
                var preview = new AudioDocument(source.Channels.ToArray(), 1000, 32);
                var (provider, output) = SeedPlayingPreview(main, preview);
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(LyricsDialog).GetField("_wholeAudio", fields)!.SetValue(dialog, preview);
                var pending = (List<Task>)typeof(PlaybackEngine).GetField("_pendingCleanupTasks", fields)!.GetValue(main.Engine)!;
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(gate.Task); // A regression must never open a real audio device in this test.
                try
                {
                    int position = main.Engine.PositionSamples;
                    var play = (Button)dialog.FindName("playAllButton");
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Same(output, typeof(PlaybackEngine).GetField("_out", fields)!.GetValue(main.Engine));
                    Assert.Same(provider, typeof(PlaybackEngine).GetField("_provider", fields)!.GetValue(main.Engine));
                    Assert.Equal(position, main.Engine.PositionSamples);
                    Assert.Equal(0, output.Stops);
                    Assert.Equal(0, output.Plays);
                    ((Button)dialog.FindName("stopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(main.Engine.IsPaused);
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // A double-click on Continue must not restart either.
                    Assert.True(main.Engine.IsPlaying);
                    Assert.Same(output, typeof(PlaybackEngine).GetField("_out", fields)!.GetValue(main.Engine));
                    Assert.Equal(1, output.Plays);
                    Assert.Equal(0, output.Stops);
                    var next = new float[1];
                    Assert.Equal(1, provider.Read(next));
                    Assert.Equal(.16f, next[0]);
                    inspected = true;
                }
                finally { gate.TrySetResult(); dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            typeof(MainWindow).GetMethod("OnLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, [shell, new RoutedEventArgs()]);
            Assert.True(inspected);
            Assert.False(main.Engine.IsPlaying);
            Assert.False(main.IsDocumentOperationRunning);
        }));
    }

    private sealed class PreviewPlayer : IWavePlayer, IAsyncDisposable
    {
        public TaskCompletionSource? ReleaseGate { get; set; }
        public int Plays { get; private set; }
        public int Pauses { get; private set; }
        public int Stops { get; private set; }
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Playing;
        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(1000, 1);
#pragma warning disable CS0067
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
#pragma warning restore CS0067
#pragma warning disable CS0618
        public float Volume { get; set; } = 1;
#pragma warning restore CS0618
        public void Init(IWaveProvider waveProvider) { }
        public void Play() { Plays++; PlaybackState = PlaybackState.Playing; }
        public void Pause() { Pauses++; PlaybackState = PlaybackState.Paused; }
        public void Stop() { Stops++; PlaybackState = PlaybackState.Stopped; }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ReleaseGate == null ? ValueTask.CompletedTask : new ValueTask(ReleaseGate.Task);
    }

    private readonly string _originalSettings = AppSettings.AppDataDir;
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
    public LyricsTransportTests() => AppSettings.AppDataDir = _settings;
    public void Dispose()
    {
        AppSettings.AppDataDir = _originalSettings;
        try { Directory.Delete(_settings, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LyricsStopReleasesAnExistingNormalTransport(bool paused)
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), shell =>
        {
            var main = Assert.IsType<MainViewModel>(shell.DataContext);
            main.AddDocument(new AudioDocument([new float[44100]], 44100, 32));
            // Seed the public transport state without opening an audio device. There is
            // deliberately no preview owner: StopPreview alone cannot release this state.
            typeof(PlaybackEngine).GetProperty(paused ? "IsPaused" : "IsPlaying")!
                .SetValue(main.Engine, true);
            bool inspected = false;
            shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = shell.OwnedWindows.OfType<LyricsDialog>().Single();
                try
                {
                    ((Button)dialog.FindName("stopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(main.Engine.IsPlaying);
                    Assert.False(main.Engine.IsPaused);
                    inspected = true;
                }
                finally { dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            typeof(MainWindow).GetMethod("OnLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, [shell, new RoutedEventArgs()]);
            Assert.True(inspected, "The actual shell command must open the lyrics dialog and wire its Stop button.");
            Assert.False(main.IsDocumentOperationRunning);
        }));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VocalExtractionOpensOnlyACompletedResultAndPreservesTheSource(bool completed)
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), shell =>
        {
            var main = Assert.IsType<MainViewModel>(shell.DataContext);
            var original = new AudioDocument([new float[44100]], 44100, 32) { Title = "Original.wav" };
            main.AddDocument(original);
            var sourceTab = main.ActiveDocument!;
            var transcript = new WaveLab.Audio.Transcription.LyricsTranscript();
            sourceTab.LyricsTranscript = transcript;
            var result = new AudioDocument([new float[44100], new float[44100]], 44100, 32)
                { Title = "Original - vocals.wav", RequiresSaveAs = true };
            shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = shell.OwnedWindows.OfType<LyricsDialog>().Single();
                if (completed)
                    typeof(LyricsDialog).GetProperty(nameof(LyricsDialog.IsolatedVocals))!.SetValue(dialog, result);
                dialog.Close();
            }), DispatcherPriority.ApplicationIdle);
            typeof(MainWindow).GetMethod("OnLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, [shell, new RoutedEventArgs()]);
            Assert.Equal(completed ? 2 : 1, main.Documents.Count);
            Assert.Same(transcript, sourceTab.LyricsTranscript);
            Assert.False(original.Dirty);
            Assert.Equal(0, original.EditVersion);
            Assert.False(main.IsDocumentOperationRunning);
            if (completed)
            {
                Assert.Same(result, main.ActiveDocument!.Doc);
                Assert.True(result.Dirty);
                Assert.Null(result.FilePath);
                result.MarkSaved(); // The test shell can close without prompting for this generated file.
            }
        }));
    }

}
