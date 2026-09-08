using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class RecordingPauseResetTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _previousSettingsDir = AppSettings.AppDataDir;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public RecordingPauseResetTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _previousSettingsDir;
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void ResetClearsMetersButPreservesAudioClockGainAndRecordingNote()
    {
        using var take = new CaptureHarness();
        Set(take.Engine, "_autoStopOnRunOut", true);
        var first = Enumerable.Repeat(0.1f, 800).ToArray();
        first[50] = 1;
        take.Feed(first);
        Set(take.Engine, "_pendingCaptureNote", "Original level check");
        Assert.Equal(1, take.Engine.PeakL);
        Assert.True(take.Engine.LevelSnapshot.ClippedSamples > 0);
        double seconds = take.Engine.RecordedSeconds;
        long epoch = take.DataState;
        object? runOut = take.RunOut;
        Assert.NotNull(runOut);

        Assert.True(take.Engine.ResetRecordingLevels(1));

        Assert.True(take.Engine.IsRecording);
        Assert.Equal(seconds, take.Engine.RecordedSeconds);
        Assert.Equal(epoch, take.DataState);
        Assert.Same(runOut, take.RunOut);
        Assert.Equal(0, take.Engine.InputFineTrimDb);
        Assert.Equal(0, take.Engine.PeakL);
        Assert.Equal(0, take.Engine.LevelSnapshot.ClippedSamples);
        Assert.True(double.IsNegativeInfinity(take.Engine.LevelSnapshot.TruePeakDb));
        var second = Enumerable.Repeat(0.25f, 800).ToArray();
        take.Feed(second);
        Assert.Equal(0, take.Engine.PeakL); // pending boundary audio is saved but not re-measured
        take.Feed(second);
        Assert.Equal(0.25f, take.Engine.PeakL);
        var document = take.BuildDocument();
        Assert.Equal(first.Concat(second).Concat(second), document.Channels[0]);
        Assert.Equal("Original level check", document.CaptureNote);
    }

    [Fact]
    public void AnInFlightPacketIsSavedButCannotRestoreThePeakAfterReset()
    {
        using var take = new CaptureHarness();
        take.Feed(Enumerable.Repeat(0.5f, 800).ToArray());
        Exception? error = null;
        var queued = Enumerable.Repeat(0.99f, 800).ToArray();
        var worker = new Thread(() => { try { take.Feed(queued); } catch (Exception ex) { error = ex; } });
        lock (take.Blocks)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(3)), "The capture callback did not reach the block lock");
            Assert.True(take.Engine.ResetRecordingLevels(1));
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
        Assert.Null(error);
        Assert.Equal(0.2, take.Engine.RecordedSeconds, 6);
        Assert.Equal(0, take.Engine.PeakL);
        Assert.True(double.IsNegativeInfinity(take.Engine.LevelSnapshot.PeakLeftDb));
        Assert.Equal(0.99f, take.BuildDocument().Channels[0].Max());
    }

    [Fact]
    public void PauseMonitorsWithoutSavingAndContinueAppendsToTheSameTake()
    {
        using var take = new CaptureHarness();
        var first = Enumerable.Repeat(0.125f, 800).ToArray();
        var last = Enumerable.Repeat(-0.25f, 800).ToArray();
        Set(take.Engine, "_autoStopOnRunOut", true);
        take.Feed(first);
        Assert.NotNull(take.RunOut);
        Set(take.Engine, "_autoStopLimitSeconds", 2.0);
        Assert.True(take.Engine.SetRecordingPaused(1, true));
        Assert.True(take.Engine.IsPaused);
        Assert.True(take.Engine.IsRecording);
        Assert.True(take.RetainAudio);
        long pauseEpoch = take.DataState;
        Assert.True(take.Engine.SetRecordingPaused(1, true));
        Assert.Equal(pauseEpoch, take.DataState);
        for (int i = 0; i < 30; i++) take.Feed(Enumerable.Repeat(0.9f, 800).ToArray());
        Assert.Equal(0.9f, take.Engine.PeakL);
        Assert.Equal(0.1, take.Engine.RecordedSeconds, 6);
        Assert.False(take.AutoStopRequested);
        Assert.Null(take.RunOut);
        Assert.Equal(first, take.BuildDocument().Channels[0]); // stopping while paused keeps the take

        Assert.True(take.Engine.SetRecordingPaused(1, false));
        Assert.False(take.Engine.IsPaused);
        take.Feed(Enumerable.Repeat(0.8f, 800).ToArray()); // mixed resume-boundary packet is excluded
        Assert.Equal(0.1, take.Engine.RecordedSeconds, 6);
        take.Feed(last);
        Assert.Equal(0.2, take.Engine.RecordedSeconds, 6);
        Assert.Equal(first.Concat(last), take.BuildDocument().Channels[0]);
    }

    [Fact]
    public void APacketQueuedWhilePausedCannotLeakAcrossContinue()
    {
        using var take = new CaptureHarness();
        var first = Enumerable.Repeat(0.125f, 800).ToArray();
        var resumed = Enumerable.Repeat(-0.25f, 800).ToArray();
        take.Feed(first);
        Assert.True(take.Engine.SetRecordingPaused(1, true));
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { take.Feed(Enumerable.Repeat(0.95f, 800).ToArray()); }
            catch (Exception ex) { error = ex; }
        });
        lock (take.Blocks)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(3)), "The paused callback did not reach the block lock");
            Assert.True(take.Engine.SetRecordingPaused(1, false));
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
        Assert.Null(error);
        Assert.Equal(0.1, take.Engine.RecordedSeconds, 6);
        take.Feed(Enumerable.Repeat(0.8f, 800).ToArray()); // first resume packet still needs discarding
        Assert.Equal(0.1, take.Engine.RecordedSeconds, 6);
        take.Feed(resumed);
        Assert.Equal(first.Concat(resumed), take.BuildDocument().Channels[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndingTakesRejectLatePauseAndResetWithoutChangingAudio(bool capacityReached)
    {
        using var take = new CaptureHarness();
        var saved = Enumerable.Repeat(0.5f, 800).ToArray();
        take.Feed(saved);
        long epoch = take.DataState;
        if (capacityReached) Set(take.Engine, "_capacityReached", true);
        else Assert.True(take.RequestAutoStop());

        Assert.False(take.Engine.SetRecordingPaused(1, true));
        Assert.False(take.Engine.ResetRecordingLevels(1));
        Assert.False(take.Engine.IsPaused);
        Assert.Equal(epoch, take.DataState);
        Assert.Equal(0.5f, take.Engine.PeakL);
        Assert.Equal(0.1, take.Engine.RecordedSeconds, 6);
        Assert.Equal(saved, take.BuildDocument().Channels[0]);
    }

    [Fact]
    public void StaleAndStoppedSessionsCannotPauseOrResetAnotherTake()
    {
        using var take = new CaptureHarness();
        take.Feed(Enumerable.Repeat(0.5f, 800).ToArray());
        Assert.False(take.Engine.ResetRecordingLevels(2));
        Assert.False(take.Engine.SetRecordingPaused(2, true));
        take.Stopped.TrySetResult(new StoppedEventArgs());
        Assert.False(take.Engine.ResetRecordingLevels(1));
        Assert.False(take.Engine.SetRecordingPaused(1, true));
        Assert.Equal(0.5f, take.Engine.PeakL);
        Assert.Equal(0.1, take.Engine.RecordedSeconds, 6);
    }

    [Fact]
    public void MonitoringOnlySessionsCannotBecomePausedRecordings()
    {
        using var take = new CaptureHarness(retainAudio: false);
        take.Feed(Enumerable.Repeat(0.5f, 800).ToArray());
        Assert.False(take.Engine.ResetRecordingLevels(1));
        Assert.False(take.Engine.SetRecordingPaused(1, true));
        Assert.False(take.RetainAudio);
    }

    [Fact]
    public void RecordingButtonsResetAndPauseWithoutUnlockingSetupControls()
    {
        Wpf.Run(() => Wpf.Show(new RecordDialog(), window =>
        {
            var vm = (RecordViewModel)window.DataContext;
            using var take = new CaptureHarness(Get<RecordingEngine>(vm, "_engine"));
            Set(vm, "_expectedRecordingSessionId", 1L);
            SetProperty(vm, nameof(vm.IsRecording), true);
            var pause = (Button)window.FindName("pauseContinueBtn");
            var reset = (Button)window.FindName("resetLevelCheckBtn");
            try
            {
                take.Feed(Enumerable.Repeat(0.5f, 800).ToArray());
                Wpf.Pump();
                Assert.True(pause.IsEnabled);
                Assert.True(reset.IsEnabled);
                Assert.Equal("Reset Levels", reset.Content);
                string elapsed = vm.ElapsedText;
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wpf.Pump();
                Assert.True(vm.IsRecordingPaused);
                Assert.Equal("Continue", pause.Content);
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wpf.Pump();
                Assert.Equal(elapsed, vm.ElapsedText);
                Assert.True(vm.IsRecording);
                Assert.True(vm.IsRecordingPaused);
                Assert.False(((ComboBox)window.FindName("deviceCombo")).IsEnabled);
                Assert.False(((StackPanel)window.FindName("inputGainControls")).IsEnabled);
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(vm.IsRecordingPaused);
                SetProperty(vm, nameof(vm.IsFinalizing), true);
                Assert.False(pause.IsEnabled);
                Assert.False(reset.IsEnabled);
            }
            finally
            {
                SetProperty(vm, nameof(vm.IsRecording), false);
                SetProperty(vm, nameof(vm.IsFinalizing), false);
                Set(vm, "_expectedRecordingSessionId", 0L);
            }
        }));
    }

    private sealed class CaptureHarness : IDisposable
    {
        private delegate void ReceivePacket(RecordingEngine engine, object session, ReadOnlySpan<byte> data);
        private readonly ReceivePacket _receive;
        private readonly object _session;
        private readonly bool _ownsEngine;
        public RecordingEngine Engine { get; }
        public TaskCompletionSource<StoppedEventArgs> Stopped { get; } = new();
        public List<float[]> Blocks => Get<List<float[]>>(Engine, "_blocks");
        public long DataState => (long)_session.GetType().GetProperty("DataState")!.GetValue(_session)!;
        public bool RetainAudio => (bool)_session.GetType().GetProperty("RetainAudio")!.GetValue(_session)!;
        public bool AutoStopRequested => (bool)_session.GetType().GetProperty("AutoStopRequested")!.GetValue(_session)!;
        public object? RunOut => _session.GetType().GetProperty("RunOut")!.GetValue(_session);
        public bool RequestAutoStop() => (bool)_session.GetType().GetMethod("TryRequestAutoStop")!.Invoke(_session, null)!;

        public CaptureHarness(RecordingEngine? engine = null, bool retainAudio = true)
        {
            Engine = engine ?? new RecordingEngine();
            _ownsEngine = engine == null;
            var sessionType = typeof(RecordingEngine).GetNestedType("CaptureSession", BindingFlags.NonPublic)!;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(8_000, 1);
            _session = Activator.CreateInstance(sessionType, Private | BindingFlags.Public, null,
                [1L, null, Stopped, retainAudio, format, AudioClientShareMode.Exclusive], null)!;
            Set(Engine, "_channels", 1);
            Set(Engine, "_sampleRate", 8_000);
            Get<RecordingLevelAnalyzer>(Engine, "_levelAnalyzer").Configure(8_000, 1);
            typeof(RecordingEngine).GetMethod("PublishSession", Private)!.Invoke(Engine, [_session]);
            // Invoke the production span-based callback without opening a device.
            // A small typed trampoline avoids boxing ReadOnlySpan for reflection.
            var method = new DynamicMethod("FeedCapturePacket", typeof(void),
                [typeof(RecordingEngine), typeof(object), typeof(ReadOnlySpan<byte>)], typeof(RecordingEngine), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, sessionType);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, typeof(RecordingEngine).GetMethod("OnData", Private)!);
            il.Emit(OpCodes.Ret);
            _receive = method.CreateDelegate<ReceivePacket>();
        }

        public void Feed(float[] samples) => _receive(Engine, _session, MemoryMarshal.AsBytes(samples.AsSpan()));

        public AudioDocument BuildDocument()
        {
            var snapshotType = typeof(RecordingEngine).GetNestedType("CaptureSnapshot", BindingFlags.NonPublic)!;
            object snapshot = Activator.CreateInstance(snapshotType, Private | BindingFlags.Public, null,
                [1L, Blocks.ToArray(), Get<long>(Engine, "_totalSamples"), 1, 8_000, DateTime.UtcNow,
                    typeof(RecordingEngine).GetField("_pendingCaptureNote", Private)!.GetValue(Engine)], null)!;
            return (AudioDocument)typeof(RecordingEngine).GetMethod("BuildDocument", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [snapshot, CancellationToken.None])!;
        }

        public void Dispose()
        {
            typeof(RecordingEngine).GetField("_session", Private)!.SetValue(Engine, null);
            if (_ownsEngine) Engine.Dispose();
        }
    }

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static void SetProperty(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
}
