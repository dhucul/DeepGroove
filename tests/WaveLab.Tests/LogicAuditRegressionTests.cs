using System.Reflection;
using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.Audio.Montage;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

public sealed class LogicAuditRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void UndoAndRedoRestoreCollapsedMarkersAndDeletedRegions()
    {
        var doc = new AudioDocument([new float[1000]], 48000, 32);
        var vm = new DocumentViewModel(doc);
        var marker = new Marker { Position = 150, Name = "Original" };
        var region = new NamedRegion { Start = 120, End = 180, Name = "Track", CdTrackOrder = 2 };
        vm.Markers.Add(marker);
        vm.Regions.Add(region);
        doc.ReplaceRange(100, 100, [[]], "Delete");
        Assert.Empty(vm.Regions);
        marker.Name = "Renamed";
        for (int i = 0; i < 3; i++)
        {
            doc.Undo();
            Assert.Equal(150, marker.Position);
            Assert.Same(region, Assert.Single(vm.Regions));
            Assert.Equal((120, 180, 2), (region.Start, region.End, region.CdTrackOrder));
            Assert.Equal("Renamed", marker.Name);
            doc.Redo();
            Assert.Equal(100, marker.Position);
            Assert.Empty(vm.Regions);
        }
        vm.Unhook();
    }

    [Fact]
    public void HistoryJumpMatchesIndividualStepsForSeparatedSplices()
    {
        static DocumentViewModel Edited()
        {
            var vm = new DocumentViewModel(new AudioDocument([new float[1000]], 48000, 32));
            vm.Markers.Add(new Marker { Position = 500 });
            vm.Doc.ReplaceRange(100, 0, [new float[100]], "First insertion");
            vm.Doc.ReplaceRange(800, 0, [new float[100]], "Second insertion");
            vm.SetCursor(600, true);
            vm.SetSelection(550, 650);
            return vm;
        }
        var jump = Edited();
        var steps = Edited();
        jump.Doc.JumpToHistoryPosition(0);
        steps.Doc.Undo(); steps.Doc.Undo();
        Assert.Equal(500, jump.Markers[0].Position);
        Assert.Equal(steps.Cursor, jump.Cursor);
        Assert.Equal((steps.SelStart, steps.SelEnd), (jump.SelStart, jump.SelEnd));
        jump.Doc.JumpToHistoryPosition(2);
        Assert.Equal(600, jump.Markers[0].Position);
        jump.Unhook(); steps.Unhook();
    }

    [Fact]
    public void SameLengthAudioUndoDoesNotRevertIndependentMarkerPositions()
    {
        var vm = new DocumentViewModel(new AudioDocument([new float[1000]], 48000, 32));
        vm.Markers.Add(new Marker { Position = 100 });
        vm.Doc.ReplaceRange(0, 100, [new float[100]], "Gain");
        vm.Markers[0].Position = 200;
        vm.Doc.Undo();
        Assert.Equal(200, vm.Markers[0].Position);
        vm.Unhook();
    }

    [Fact]
    public async Task UnsavedSplicesNeverOverwriteTheSavedTimelineSidecar()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.LogicAudit.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "audio.wav");
            var original = new AudioDocument([new float[1000]], 48000, 32);
            WavCodec.Save(original, path, 32, false, markers: [new Marker { Position = 500 }]);
            MarkerStore.Save(path, [new Marker { Position = 500 }], [new NamedRegion { Start = 400, End = 600 }]);
            var vm = new DocumentViewModel(WavCodec.Load(path));
            vm.Doc.ReplaceRange(0, 100, [[]], "Delete");
            await vm.FlushMarkersAsync();
            var saved = MarkerStore.Load(path);
            Assert.Equal(500, Assert.Single(saved.Markers).Position);
            Assert.Equal(400, Assert.Single(saved.Regions).Start);

            // Save captures these anchors; a later edit must not leak into that sidecar.
            var markers = vm.Markers.Select(m => new Marker { Position = m.Position, Name = m.Name }).ToList();
            var regions = vm.Regions.Select(r => new NamedRegion { Start = r.Start, End = r.End }).ToList();
            WavCodec.Save(vm.Doc, path, 32, false, markers: markers);
            Assert.Empty(MarkerStore.Load(path).Regions); // old signature is now invalid
            vm.Doc.ReplaceRange(0, 100, [[]], "Newer deletion");
            vm.PersistMarkers(path, markers, regions);
            await vm.FlushMarkersAsync();
            Assert.Equal(400, Assert.Single(MarkerStore.Load(path).Markers).Position);
            Assert.Equal(300, vm.Markers[0].Position);
            vm.Unhook();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LiveSwitchesResetOnTheProcessingThreadBeforeProcessing(int change)
    {
        using var master = new MasterSection();
        var effect = new ResetBarrier();
        master.ReplaceChain([effect]);
        master.SetSource(new Samples(new float[1000]));
        effect.Armed = true;
        if (change == 0) { master.RackEnabled = false; master.RackEnabled = true; }
        else if (change == 1) master.MidSideMode = true;
        else { master.SetEffectEnabled(effect, false); master.SetEffectEnabled(effect, true); }
        Assert.False(effect.ResetEntered.IsSet);
        var read = Task.Run(() => master.Read(new float[100]));
        try
        {
            Assert.True(effect.ResetEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, effect.ProcessThread);
        }
        finally { effect.ResetRelease.Set(); }
        await read;
        Assert.Equal(effect.ResetThread, effect.ProcessThread);
        Assert.NotEqual(0, effect.ProcessThread);
    }

    [Fact]
    public void LiveLimiterReleasesTheLastSamplesAndEventuallyReturnsEof()
    {
        using var master = new MasterSection();
        master.ReplaceChain([new LimiterEffect()]);
        var source = Enumerable.Repeat(.1f, 1000).ToArray();
        source[^1] = .25f;
        master.SetSource(new Samples(source));
        float[] buffer = new float[127];
        var output = new List<float>();
        for (int calls = 0; calls < 100; calls++)
        {
            int count = master.Read(buffer);
            if (count == 0) break;
            output.AddRange(buffer.Take(count));
        }
        Assert.Equal(1240, output.Count);
        Assert.Contains(output.Skip(1000), s => s > .1f);
        Assert.Equal(0, master.Read(buffer));
        master.SetSource(new Samples(source));
        Assert.Equal(buffer.Length, master.Read(buffer));
    }

    [Fact]
    public void LiveDelayRingsOutAfterSourceEof()
    {
        using var master = new MasterSection();
        var delay = new DelayEffect();
        delay.SetParam("time", 10); delay.SetParam("mix", 1); delay.SetParam("feedback", 0);
        master.ReplaceChain([delay]);
        master.SetSource(new Samples([.25f]));
        var output = new List<float>();
        var block = new float[64];
        for (int calls = 0; calls < 100; calls++)
        {
            int count = master.Read(block);
            if (count == 0) break;
            output.AddRange(block.Take(count));
        }
        Assert.True(output.Count > 480);
        Assert.Contains(output, s => s > 0);
        Assert.Equal(0, master.Read(block));
    }

    [Fact]
    public void LoopChangesReachAnAlreadyCreatedProvider()
    {
        using var engine = new PlaybackEngine();
        var doc = new AudioDocument([[.1f, .2f, .3f, .4f]], 48000, 32);
        var providerType = typeof(PlaybackEngine).GetNestedType("DocumentProvider", BindingFlags.NonPublic)!;
        var provider = (ISampleProvider)Activator.CreateInstance(providerType, doc, 0, (int?)4, false)!;
        typeof(PlaybackEngine).GetField("_provider", Private)!.SetValue(engine, provider);
        provider.Read(new float[960]); // silent lead-in
        engine.Loop = true;
        Assert.Equal(6, provider.Read(new float[6]));
        engine.Loop = false;
        Assert.Equal(2, provider.Read(new float[8]));
        Assert.Equal(0, provider.Read(new float[8]));
    }

    [Fact]
    public void ReleasingAHeadSnapCannotMoveBeforeTimelineZero()
    {
        Wpf.Run(() =>
        {
            var source = Enumerable.Range(0, 1000).Select(i => i < 90 ? -.1f : .1f).ToArray();
            source[90] = 0;
            var doc = new MontageDocument(48000, 1);
            doc.AddSource(MontageSource.From([source], 48000, 48000, 1, "Source"));
            var clip = doc.Add(new MontageClip { SourceIndex = 0, SourceStart = 100, Length = 500 });
            var vm = new MontageViewModel(doc);
            var lane = new MontageLaneView { Montage = vm };
            typeof(MontageLaneView).GetField("_dragClip", Private)!.SetValue(lane, clip);
            var drag = typeof(MontageLaneView).GetField("_drag", Private)!;
            drag.SetValue(lane, Enum.Parse(drag.FieldType, "TrimHead"));
            typeof(MontageLaneView).GetMethod("EndDrag", Private)!.Invoke(lane, null);
            Assert.Equal(0, clip.TimelineStart);
            Assert.Equal(500, MontageRenderer.Render(doc).Length);
            clip.TimelineStart = -1;
            Assert.Contains(doc.Validate(), issue => issue.Severity == MontageIssueSeverity.Error);
            Assert.Throws<InvalidOperationException>(() => MontageRenderer.Render(doc));
        });
    }

    [Fact]
    public void ClipboardBudgetIsCheckedWithoutAllocatingTheSelection()
    {
        Assert.True(MainViewModel.FitsClipboard(2, 67_108_864));
        Assert.False(MainViewModel.FitsClipboard(2, 67_108_865));
        Assert.False(MainViewModel.FitsClipboard(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void RetiredMonitorFailureCannotDisableAReplacementRequest()
    {
        using var monitor = new SoftwareInputMonitor();
        monitor.SetEnabled(true);
        long generation = (long)typeof(SoftwareInputMonitor).GetField("_generation", Private)!.GetValue(monitor)!;
        monitor.SetEnabled(false);
        monitor.SetEnabled(true);
        typeof(SoftwareInputMonitor).GetMethod("PublishFailure", Private)!.Invoke(monitor, [generation, "Old failure"]);
        Assert.True(monitor.Enabled);
        Assert.Null(monitor.LastError);
    }

    [Fact]
    public async Task ShutdownWaitsForCancelledModelessWorkToFinishCleanup()
    {
        var lifetime = new OperationLifetime();
        using var cancellation = new CancellationTokenSource();
        var lease = lifetime.Register(cancellation);
        lifetime.CancelAll();
        var idle = lifetime.WaitForIdleAsync();
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(idle.IsCompleted);
        lease.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        using var late = new CancellationTokenSource();
        using var lateLease = lifetime.Register(late);
        Assert.True(late.IsCancellationRequested);
    }

    [Fact]
    public void AbSlotsRemainStableAcrossRepeatedToggles()
    {
        using var master = new MasterSection();
        var trim = new TrimEffect();
        master.ReplaceChain([trim]);
        trim.SetParam("gain", -6); master.CaptureSnapshotA();
        trim.SetParam("gain", 6); master.CaptureSnapshotB();
        for (int i = 0; i < 10; i++)
        {
            bool b = master.ToggleCompare();
            Assert.Equal(b ? 6 : -6, master.ChainSnapshot[0].GetParam("gain"));
        }
        master.ChainSnapshot[0].SetParam("gain", 3);
        master.ToggleCompare(); master.ToggleCompare();
        Assert.Equal(3, master.ChainSnapshot[0].GetParam("gain"));
    }

    private sealed class Samples(float[] data) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed class ResetBarrier : IAudioEffect
    {
        public bool Armed;
        public int ResetThread, ProcessThread;
        public ManualResetEventSlim ResetEntered { get; } = new();
        public ManualResetEventSlim ResetRelease { get; } = new();
        public string TypeId => "audit-reset";
        public string DisplayName => "Reset barrier";
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<EffectParam> Params => [];
        public double GetParam(string key) => 0;
        public void SetParam(string key, double value) { }
        public void Configure(int sampleRate, int channels) { }
        public int LatencySamples => 0;
        public string? Readout => null;
        public void ResetState()
        {
            if (!Armed) return;
            ResetThread = Environment.CurrentManagedThreadId;
            ResetEntered.Set();
            if (!ResetRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }
        public void Process(float[] buffer, int offset, int count) => ProcessThread = Environment.CurrentManagedThreadId;
    }
}
