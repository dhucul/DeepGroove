using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.Audio.Vst3;
using Xunit;

namespace WaveLab.Tests;

public sealed class Vst3InteropRegressionTests
{
    [Theory]
    [InlineData(1, 524288UL)]
    [InlineData(2, 3UL)]
    public void SupportedLayoutsUseSdkSpeakerBitsAndMatchingBuffers(int channels, ulong arrangement)
    {
        using var fake = new FakePlugin();
        Assert.True(fake.Plugin.Configure(48000, channels, 64));
        Assert.Equal(arrangement, FakePlugin.RequestedArrangement);
        float[] samples = Enumerable.Repeat(.25f, channels * 32).ToArray();
        Assert.True(fake.Plugin.ProcessInterleaved(samples, 0, samples.Length));
        Assert.Equal(channels, FakePlugin.ReceivedChannels);
        Assert.All(samples, sample => Assert.Equal(.25f, sample));
    }

    [Fact]
    public void ARefusedArrangementCannotReachProcess()
    {
        using var fake = new FakePlugin();
        FakePlugin.RefuseArrangement = true;
        Assert.False(fake.Plugin.Configure(48000, 1, 64));
        Assert.False(fake.Plugin.ProcessInterleaved(new float[64], 0, 64));
        Assert.Equal(0, FakePlugin.ProcessCalls);
    }

    [Fact]
    public void ClaimedSuccessWithDifferentBusChannelsIsRejected()
    {
        using var fake = new FakePlugin();
        FakePlugin.MisreportChannels = true;
        Assert.False(fake.Plugin.Configure(48000, 2, 64));
        Assert.False(fake.Plugin.ProcessInterleaved(new float[64], 0, 64));
        Assert.Equal(0, FakePlugin.ProcessCalls);
    }

    [Fact]
    public void SurroundIsRefusedInsteadOfPassedToAStereoBus()
    {
        using var fake = new FakePlugin();
        Assert.True(fake.Plugin.Configure(48000, 2, 64));
        Assert.False(fake.Plugin.Configure(48000, 6, 64));
        Assert.False(fake.Plugin.ProcessInterleaved(new float[192], 0, 192));
        Assert.Equal(0, fake.Plugin.LatencySamples);
        Assert.Equal(0, fake.Plugin.TailSamples);
        Assert.Equal(0, FakePlugin.ProcessCalls);
    }

    [Fact]
    public void InactiveSidechainStillReceivesItsBusAndPointerArray()
    {
        using var fake = new FakePlugin();
        FakePlugin.Auxiliary = true;
        Assert.True(fake.Plugin.Configure(48000, 2, 64));
        Assert.True(fake.Plugin.ProcessInterleaved(new float[64], 0, 64));
        Assert.Equal(2, FakePlugin.ReceivedInputs);
        Assert.True(FakePlugin.AuxiliaryPointersPresent);
    }

    [Fact]
    public void SdkLatencyNotificationReactivatesThenRefreshesTheLatency()
    {
        using var fake = new FakePlugin();
        fake.AttachHandler();
        Assert.True(fake.Plugin.Configure(48000, 2, 64));
        int activations = FakePlugin.ActivationCalls;
        FakePlugin.Latency = 512;
        fake.Restart(1 << 5); // MIDI assignment changes are not latency changes.
        Assert.Equal(128, fake.Plugin.LatencySamples);
        fake.Restart(1 << 3); // Independent SDK value.
        Assert.Equal(512, fake.Plugin.LatencySamples);
        Assert.Equal(activations + 2, FakePlugin.ActivationCalls);
        Assert.True(fake.Plugin.IsProcessing);
    }

    [Fact]
    public void NotificationsDuringActivationDoNotRecursivelyRestartTheComponent()
    {
        using var fake = new FakePlugin();
        fake.AttachHandler();
        FakePlugin.RestartOnActivation = true;
        Assert.True(fake.Plugin.Configure(48000, 2, 64));
        int activations = FakePlugin.ActivationCalls;
        FakePlugin.Latency = 512;
        fake.Restart(8);
        Assert.Equal(activations + 2, FakePlugin.ActivationCalls);
        Assert.Equal(512, fake.Plugin.LatencySamples);
        Assert.True(fake.Plugin.IsProcessing);
    }

    [Fact]
    public void AnIoChangeStopsProcessingUntilTheBusIsNegotiatedAgain()
    {
        using var fake = new FakePlugin();
        fake.AttachHandler();
        using var effect = new Vst3Effect(new Vst3PluginRef(fake.Plugin, "fixture", null));
        effect.Configure(48000, 2);
        Assert.True(effect.Configured);
        fake.Restart(1 << 1);
        Assert.False(effect.Configured);
        Assert.False(fake.Plugin.ProcessInterleaved(new float[64], 0, 64));
        Assert.Equal(0, effect.LatencySamples);
    }

    [Fact]
    public async Task LatencyRestartCannotDeactivateAPlugInInsideProcess()
    {
        using var fake = new FakePlugin();
        fake.AttachHandler();
        Assert.True(fake.Plugin.Configure(48000, 2, 64));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var restarting = new ManualResetEventSlim();
        FakePlugin.ProcessEntered = entered; FakePlugin.ProcessRelease = release;
        int activations = FakePlugin.ActivationCalls;
        var processing = Task.Run(() => fake.Plugin.ProcessInterleaved(new float[64], 0, 64));
        Task? restart = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            FakePlugin.Latency = 512;
            restart = Task.Run(() => { restarting.Set(); fake.Restart(8); });
            Assert.True(restarting.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(activations, FakePlugin.ActivationCalls);
            Assert.Equal(128, fake.Plugin.LatencySamples);
        }
        finally { release.Set(); }
        Assert.True(await processing);
        if (restart != null) await restart;
        Assert.Equal(512, fake.Plugin.LatencySamples);
    }

    [Fact]
    public async Task ResetRequestsWaitForAProcessingBoundaryWithoutBlockingTheirCaller()
    {
        using var fake = new FakePlugin();
        Assert.True(fake.Plugin.Configure(48000, 2, 64));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        FakePlugin.ProcessEntered = entered; FakePlugin.ProcessRelease = release;
        int calls = FakePlugin.ProcessingStateCalls;
        var processing = Task.Run(() => fake.Plugin.ProcessInterleaved(new float[64], 0, 64));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            fake.Plugin.FlushProcessingState();
            Assert.Equal(calls, FakePlugin.ProcessingStateCalls);
        }
        finally { release.Set(); }
        Assert.True(await processing);
        Assert.True(fake.Plugin.ProcessInterleaved(new float[64], 0, 64));
        Assert.Equal(calls + 2, FakePlugin.ProcessingStateCalls);
    }

    [Fact]
    public void InfiniteTailIsBoundedAndFiniteTailReachesTheMaster()
    {
        using var fake = new FakePlugin();
        using var effect = new Vst3Effect(new Vst3PluginRef(fake.Plugin, "fixture", null));
        effect.Configure(48000, 2);
        Assert.Equal(48000, effect.TailSamples);
        FakePlugin.ReportedTail = uint.MaxValue;
        effect.Configure(48000, 2);
        Assert.Equal(48000 * 120, effect.TailSamples);
    }

    [Fact]
    public void LivePlaybackDrainsThePlugInTailBeyondTheLastSourceFrame()
    {
        using var fake = new FakePlugin();
        FakePlugin.Latency = 0;
        FakePlugin.ReportedTail = 32;
        FakePlugin.DelayFrames = 16;
        using var master = new MasterSection();
        var effect = new Vst3Effect(new Vst3PluginRef(fake.Plugin, "fixture", null));
        master.ReplaceChain([effect]);
        float[] source = new float[1024 * 2];
        source[0] = .1f; source[1023 * 2] = .5f;
        master.SetSource(new Samples(source));
        var rendered = new List<float>();
        var buffer = new float[256];
        int n;
        while ((n = master.Read(buffer)) > 0)
        {
            rendered.AddRange(buffer.Take(n));
            Assert.True(rendered.Count <= (1024 + 32) * 2);
        }
        Assert.Equal((1024 + 32) * 2, rendered.Count);
        Assert.Equal(.5f, rendered[(1023 + 16) * 2]);
        int plannedTail = (int)typeof(MasterSection).GetMethod("TailForCopyRender", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new IAudioEffect[] { effect }, 48000])!;
        Assert.Equal(32, plannedTail);
    }

    private sealed class Samples(float[] samples) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed unsafe class FakePlugin : IDisposable
    {
        public Vst3Plugin Plugin { get; }
        private void** _componentTable, _processorTable, _controllerTable;
        private void** _component, _processor, _controller;
        private static nint _handler;
        public static int ReceivedChannels;
        public static uint Latency;
        public static ulong RequestedArrangement, MainArrangement;
        public static bool RefuseArrangement, MisreportChannels, Auxiliary, RestartOnActivation;
        public static int MainChannels, ReceivedInputs, ProcessCalls, ActivationCalls, ProcessingStateCalls;
        public static bool AuxiliaryPointersPresent;
        public static uint ReportedTail;
        public static int DelayFrames, DelayPosition;
        private static float[][] Delay = [new float[64], new float[64]];
        public static ManualResetEventSlim? ProcessEntered, ProcessRelease;

        public FakePlugin()
        {
            ReceivedChannels = ReceivedInputs = ProcessCalls = ActivationCalls = ProcessingStateCalls = 0;
            RefuseArrangement = MisreportChannels = Auxiliary = AuxiliaryPointersPresent = RestartOnActivation = false;
            MainArrangement = 3; MainChannels = 2; ReportedTail = 48000;
            DelayFrames = DelayPosition = 0;
            Delay = [new float[64], new float[64]];
            ProcessEntered = ProcessRelease = null;
            Latency = 128;
            _handler = 0;
            _componentTable = (void**)NativeMemory.AllocZeroed(14, (nuint)sizeof(void*));
            _processorTable = (void**)NativeMemory.AllocZeroed(11, (nuint)sizeof(void*));
            _controllerTable = (void**)NativeMemory.AllocZeroed(18, (nuint)sizeof(void*));
            _componentTable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
            _componentTable[4] = (delegate* unmanaged[Stdcall]<void*, int>)&Terminate;
            _componentTable[7] = (delegate* unmanaged[Stdcall]<void*, int, int, int>)&BusCount;
            _componentTable[8] = (delegate* unmanaged[Stdcall]<void*, int, int, int, Vst3Abi.BusInfo*, int>)&BusInfo;
            _componentTable[10] = (delegate* unmanaged[Stdcall]<void*, int, int, int, byte, int>)&Activate;
            _componentTable[11] = (delegate* unmanaged[Stdcall]<void*, byte, int>)&ActivateComponent;
            _processorTable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
            _processorTable[3] = (delegate* unmanaged[Stdcall]<void*, ulong*, int, ulong*, int, int>)&Arrangements;
            _processorTable[4] = (delegate* unmanaged[Stdcall]<void*, int, int, ulong*, int>)&Arrangement;
            _processorTable[5] = (delegate* unmanaged[Stdcall]<void*, int, int>)&SampleSize;
            _processorTable[6] = (delegate* unmanaged[Stdcall]<void*, uint>)&GetLatency;
            _processorTable[7] = (delegate* unmanaged[Stdcall]<void*, Vst3Abi.ProcessSetup*, int>)&Setup;
            _processorTable[8] = (delegate* unmanaged[Stdcall]<void*, byte, int>)&SetBool;
            _processorTable[9] = (delegate* unmanaged[Stdcall]<void*, Vst3Abi.ProcessData*, int>)&Process;
            _processorTable[10] = (delegate* unmanaged[Stdcall]<void*, uint>)&GetTail;
            _controllerTable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
            _controllerTable[16] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&SetHandler;
            _component = MakeSelf(_componentTable);
            _processor = MakeSelf(_processorTable);
            _controller = MakeSelf(_controllerTable);
            Plugin = (Vst3Plugin)Activator.CreateInstance(typeof(Vst3Plugin), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [null, new Vst3ClassInfo(new byte[16], "Review stereo-only fixture", "Audio Module Class", "Fx", "Review", "1", "3")], null)!;
            SetPointer("_component", _component);
            SetPointer("_processor", _processor);
            SetPointer("_controller", _controller);
            typeof(Vst3Plugin).GetField("_controllerIsComponent", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Plugin, true);
        }

        private static void** MakeSelf(void** table)
        {
            var self = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
            *self = table;
            return self;
        }
        private void SetPointer(string field, void* pointer) => typeof(Vst3Plugin)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Plugin, Pointer.Box(pointer, typeof(void*)));
        public void AttachHandler() => typeof(Vst3Plugin).GetMethod("GiveComponentHandler", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Plugin, null);
        public void Restart(int flags)
        {
            void* handler = (void*)_handler;
            ((delegate* unmanaged[Stdcall]<void*, int, int>)(*(void***)handler)[6])(handler, flags);
        }
        public uint ReadNativeTail() => ((delegate* unmanaged[Stdcall]<void*, uint>)_processorTable[10])(_processor);
        public void Dispose()
        {
            Plugin.Dispose();
            NativeMemory.Free(_component); NativeMemory.Free(_processor); NativeMemory.Free(_controller);
            NativeMemory.Free(_componentTable); NativeMemory.Free(_processorTable); NativeMemory.Free(_controllerTable);
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint Release(void* self) => 1;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Terminate(void* self) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int BusCount(void* self, int media, int direction) => Auxiliary && direction == 0 ? 2 : 1;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int BusInfo(void* self, int media, int direction, int index, Vst3Abi.BusInfo* info) { *info = default; info->ChannelCount = index == 0 ? MainChannels : 1; info->BusType = index == 0 ? 0 : 1; return 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Activate(void* self, int media, int direction, int index, byte active) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int SetBool(void* self, byte value) { ProcessingStateCalls++; return 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Arrangements(void* self, ulong* input, int ni, ulong* output, int no)
        {
            RequestedArrangement = *input;
            if (RefuseArrangement || ni != (Auxiliary ? 2 : 1) || no != 1 || *input != *output || (*input != 3 && *input != (1UL << 19))) return 1;
            MainArrangement = *input;
            MainChannels = *input == 3 ? 2 : 1;
            if (MisreportChannels) MainChannels = 3 - MainChannels;
            return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Arrangement(void* self, int direction, int index, ulong* arrangement) { *arrangement = index == 0 ? MainArrangement : 1UL << 19; return 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int ActivateComponent(void* self, byte state)
        {
            ActivationCalls++;
            // Bound a broken implementation's recursion so this fixture fails assertions safely.
            if (RestartOnActivation && ActivationCalls < 10 && _handler != 0)
            {
                void* handler = (void*)_handler;
                ((delegate* unmanaged[Stdcall]<void*, int, int>)(*(void***)handler)[6])(handler, 8);
            }
            return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int SampleSize(void* self, int size) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint GetLatency(void* self) => Latency;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Setup(void* self, Vst3Abi.ProcessSetup* setup) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Process(void* self, Vst3Abi.ProcessData* data)
        {
            ProcessCalls++;
            ProcessEntered?.Set();
            if (ProcessRelease != null && !ProcessRelease.Wait(TimeSpan.FromSeconds(5))) return 1;
            ReceivedChannels = data->Inputs->ChannelCount;
            ReceivedInputs = data->NumInputs;
            if (ReceivedChannels != MainChannels || data->Outputs->ChannelCount != MainChannels) return 1;
            if (Auxiliary)
                AuxiliaryPointersPresent = data->NumInputs == 2 && data->Inputs[1].ChannelCount == 1 &&
                    data->Inputs[1].ChannelBuffers != null && data->Inputs[1].ChannelBuffers[0] == null;
            for (int i = 0; i < data->NumSamples; i++)
            {
                for (int c = 0; c < MainChannels; c++)
                {
                    float sample = data->Inputs->ChannelBuffers[c][i];
                    data->Outputs->ChannelBuffers[c][i] = DelayFrames == 0 ? sample : Delay[c][DelayPosition];
                    if (DelayFrames > 0) Delay[c][DelayPosition] = sample;
                }
                if (DelayFrames > 0) DelayPosition = (DelayPosition + 1) % DelayFrames;
            }
            return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int SetHandler(void* self, void* handler) { _handler = (nint)handler; return 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint GetTail(void* self) => ReportedTail;
    }
}
