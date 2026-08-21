using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WaveLab.Audio.Vst3;

namespace WaveLab.Tests;

/// <summary>
/// A VST3 plugin built in this process, out of vtables of function pointers, so the host's
/// parameter path has something that actually publishes parameters to talk to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> All 22 plugins installed on this machine report <b>zero host-visible
/// parameters</b>, which was chased down to the plugins rather than the host — five hypotheses
/// eliminated, including a real <c>IHostApplication</c> and a real <c>IComponentHandler</c>. The
/// consequence is that <c>Vst3Plugin.ReadParameters</c>, <c>SetParameter</c>, <c>ApplyParameter</c>
/// and the whole <c>IParameterChanges</c> route are correct-looking code that <b>never executes</b>
/// here. That is not the same as working, and the failure it hides is the documented one: setting
/// only the controller moves a plugin's own display and leaves the audio untouched.
/// </para>
/// <para>
/// <b>It is the existing pattern turned around.</b> <c>Vst3HostContext</c> and
/// <c>Vst3MemoryStream</c> are managed objects handed <i>outwards</i> to native code as a vtable of
/// <see cref="UnmanagedCallersOnlyAttribute"/> statics plus a <see cref="GCHandle"/> to find the way
/// back. This is a managed object handed <i>inwards</i> the same way, and the host cannot tell the
/// difference — <c>Vst3Plugin</c> runs completely unmodified against it, through the same
/// <c>createInstance</c>, the same slot numbers and the same calling convention.
/// </para>
/// <para>
/// <b>The component and the processor are separate pointers, which is not a detail.</b> In C++ they
/// are different base subobjects of one class, so <c>IComponent</c> slot 7 is <c>getBusCount</c>
/// while <c>IAudioProcessor</c> slot 7 is <c>setupProcessing</c> — returning one pointer for both
/// would call the wrong function with the right arguments. The native block carries two vtable
/// pointers and hands out its own address for one and that address plus eight for the other, which
/// is exactly what a compiler emits. The controller is a third, separately created object, because
/// that is what every real plugin measured here does.
/// </para>
/// <para>
/// The audio it makes is a gain, and the gain is a parameter. That is deliberate and minimal: a
/// test can then assert that moving a parameter changes the samples, which is the one claim the
/// installed plugins cannot support.
/// </para>
/// </remarks>
internal sealed unsafe class Vst3SyntheticPlugin : IDisposable
{
    // ── the classes this factory offers ──────────────────────────

    public static readonly byte[] ComponentClassId =
        [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01];

    public static readonly byte[] ControllerClassId =
        [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x02];

    /// <summary>Gain, the one parameter that reaches the audio.</summary>
    public const uint GainId = 100;

    /// <summary>A bypass parameter, which the rack must recognise and not draw as a slider.</summary>
    public const uint BypassId = 101;

    /// <summary>Hidden, so the rack's filtering has something to filter.</summary>
    public const uint HiddenId = 102;

    /// <summary>Read-only, likewise.</summary>
    public const uint ReadOnlyId = 103;

    private sealed record Descriptor(uint Id, string Title, string Units, double Default, int Steps, int Flags);

    private static readonly Descriptor[] Descriptors =
    [
        new(GainId, "Gain", "dB", 0.5, 0, Vst3Abi.ParamCanAutomate),
        new(BypassId, "Bypass", "", 0.0, 1, Vst3Abi.ParamCanAutomate | Vst3Abi.ParamIsBypass),
        new(HiddenId, "Hidden", "", 0.25, 0, Vst3Abi.ParamCanAutomate | Vst3Abi.ParamIsHidden),
        new(ReadOnlyId, "Meter", "dB", 0.75, 0, Vst3Abi.ParamIsReadOnly),
    ];

    // ── per-instance state ───────────────────────────────────────

    // Declared here for the same reason the five host-side classes each declare it: Vst3Abi names
    // every interface identifier the app dispatches on, and FUnknown is not one of them.
    private static readonly Guid FUnknownIid = new("00000000-0000-0000-c000-000000000046");

    /// <summary>
    /// The controller's values, and the processor's, which are deliberately <b>not</b> the same store.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole reason <c>IParameterChanges</c> exists.</b> VST3 splits a plugin so the
    /// two halves can live in different processes; they hold their own copies and the host is what
    /// keeps them in step. Backing both with one dictionary here would have made
    /// <c>setParamNormalized</c> alone appear to change the audio - which is exactly the bug this
    /// file was written to catch, quietly reproduced inside the instrument meant to detect it. The
    /// first version did that, and <c>SettingOnlyTheControllerDoesNotChangeTheAudio</c> failed on it.
    /// </remarks>
    private readonly Dictionary<uint, double> _values = [];

    private readonly Dictionary<uint, double> _processorValues = [];
    private GCHandle _self;
    private void* _factory;
    private readonly List<nint> _allocations = [];

    /// <summary>Every value the processor was handed through <c>IParameterChanges</c>, in order.</summary>
    public List<(uint Id, double Value)> ProcessorSawParameters { get; } = [];

    /// <summary>How many times <c>process</c> has been entered.</summary>
    public int ProcessCalls { get; private set; }

    /// <summary>What the last <c>setupProcessing</c> declared, so the host's configure can be checked.</summary>
    public double SampleRate { get; private set; }

    public int MaxBlockSize { get; private set; }

    /// <summary>Whether the component is currently active.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether the controller took a component handler.</summary>
    public bool TookComponentHandler => _componentHandler != null;

    private void* _componentHandler;

    /// <summary>
    /// What a plugin does when a user moves something in its <b>own</b> editor: tell the host.
    /// </summary>
    /// <remarks>
    /// <c>performEdit</c> is the host's only way to hear about it, and it is a genuine callback in
    /// the direction the app almost never gets to exercise - plugin into host. Driving it from here
    /// rather than reaching into <c>Vst3Plugin</c>'s private handler is both more honest and the
    /// only way to test it: the handler's entry points are <c>UnmanagedCallersOnly</c> and reachable
    /// only through a function pointer.
    /// </remarks>
    public void PerformEditFromEditor(uint id, double normalized)
    {
        if (_componentHandler == null) throw new InvalidOperationException("no component handler");
        void** vtable = *(void***)_componentHandler;
        ((delegate* unmanaged[Stdcall]<void*, uint, int>)vtable[3])(_componentHandler, id);          // beginEdit
        ((delegate* unmanaged[Stdcall]<void*, uint, double, int>)vtable[4])(_componentHandler, id, normalized);
        ((delegate* unmanaged[Stdcall]<void*, uint, int>)vtable[5])(_componentHandler, id);          // endEdit
    }

    /// <summary>Whether the controller was handed the component's state.</summary>
    public bool TookComponentState { get; private set; }

    /// <summary>The latency this plugin claims, which a test can move to exercise the host's read.</summary>
    public int LatencySamples { get; set; }

    public Vst3SyntheticPlugin()
    {
        foreach (Descriptor descriptor in Descriptors)
        {
            _values[descriptor.Id] = descriptor.Default;
            _processorValues[descriptor.Id] = descriptor.Default;
        }
        _self = GCHandle.Alloc(this);
        _factory = CreateObject(FactoryVtable);
    }

    /// <summary>A module over this factory, which <c>Vst3Plugin.Create</c> takes unmodified.</summary>
    public Vst3Module CreateModule() => Vst3Module.FromFactory(_factory, "synthetic.vst3");

    /// <summary>The <b>controller's</b> current normalised value for a parameter.</summary>
    public double ValueOf(uint id) => _values.TryGetValue(id, out double value) ? value : 0;

    /// <summary>The <b>processor's</b>, which only a change list or a state restore can move.</summary>
    public double ProcessorValueOf(uint id) => _processorValues.TryGetValue(id, out double value) ? value : 0;

    // ── native object layout ─────────────────────────────────────

    /// <summary>
    /// The layout every object here shares: a primary vtable, a secondary one, and the handle back.
    /// </summary>
    /// <remarks>
    /// <b>One layout for all of them, and the uniformity is load-bearing.</b> A component needs a
    /// second vtable so its <c>IAudioProcessor</c> view can be a different address; the factory and
    /// the controller do not, and giving them a null one costs eight bytes and puts <c>Handle</c> at
    /// the same offset in every object. With two different structs it is not the same offset - a
    /// component block puts its processor vtable exactly where a plain object puts its handle, so
    /// recovering the managed object from a component pointer read a vtable as a
    /// <see cref="GCHandle"/>. That is not a wrong answer, it is a fault, and it is precisely the
    /// class of bug this file's own remarks warn about for <c>AudioBusBuffers</c>.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct Object
    {
        public void** Vtable;
        public void** SecondVtable;
        public nint Handle;
    }

    private void* CreateObject(void** vtable, void** second = null)
    {
        var block = (Object*)NativeMemory.AllocZeroed((nuint)sizeof(Object));
        block->Vtable = vtable;
        block->SecondVtable = second;
        block->Handle = GCHandle.ToIntPtr(_self);
        _allocations.Add((nint)block);
        return block;
    }

    private void* CreateComponent() => CreateObject(ComponentVtable, ProcessorVtable);

    private static Vst3SyntheticPlugin? FromObject(void* self) =>
        self == null ? null : GCHandle.FromIntPtr(((Object*)self)->Handle).Target as Vst3SyntheticPlugin;

    // The processor pointer is the block's address plus one pointer, so the handle - which sits two
    // pointers into the block - is one pointer past it. The mirror of how it was handed out.
    private static Vst3SyntheticPlugin? FromProcessor(void* self) =>
        self == null ? null : GCHandle.FromIntPtr(*(nint*)((byte*)self + sizeof(void*))).Target as Vst3SyntheticPlugin;

    // ── vtables ──────────────────────────────────────────────────

    private static readonly object Gate = new();
    private static void** _factoryVtable, _componentVtable, _processorVtable, _controllerVtable;

    private static void** FactoryVtable
    {
        get
        {
            lock (Gate)
            {
                if (_factoryVtable != null) return _factoryVtable;
                void** t = (void**)NativeMemory.AllocZeroed((nuint)(7 * sizeof(void*)));
                t[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&FactoryQueryInterface;
                t[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                t[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                t[3] = (delegate* unmanaged[Stdcall]<void*, int>)&CountClasses;
                t[4] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&GetFactoryInfo;
                t[5] = (delegate* unmanaged[Stdcall]<void*, int, Vst3Abi.PClassInfo*, int>)&GetClassInfo;
                t[6] = (delegate* unmanaged[Stdcall]<void*, byte*, byte*, void**, int>)&CreateInstance;
                return _factoryVtable = t;
            }
        }
    }

    private static void** ComponentVtable
    {
        get
        {
            lock (Gate)
            {
                if (_componentVtable != null) return _componentVtable;
                void** t = (void**)NativeMemory.AllocZeroed((nuint)(14 * sizeof(void*)));
                t[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&ComponentQueryInterface;
                t[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                t[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                t[3] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&ComponentInitialize;
                t[4] = (delegate* unmanaged[Stdcall]<void*, int>)&ComponentTerminate;
                t[5] = (delegate* unmanaged[Stdcall]<void*, byte*, int>)&GetControllerClassId;
                t[6] = (delegate* unmanaged[Stdcall]<void*, int, int>)&SetIoMode;
                t[7] = (delegate* unmanaged[Stdcall]<void*, int, int, int>)&GetBusCount;
                t[8] = (delegate* unmanaged[Stdcall]<void*, int, int, int, Vst3Abi.BusInfo*, int>)&GetBusInfo;
                t[9] = (delegate* unmanaged[Stdcall]<void*, int, int, void*, int>)&GetRoutingInfo;
                t[10] = (delegate* unmanaged[Stdcall]<void*, int, int, int, byte, int>)&ActivateBus;
                t[11] = (delegate* unmanaged[Stdcall]<void*, byte, int>)&SetActive;
                t[12] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&SetState;
                t[13] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&GetState;
                return _componentVtable = t;
            }
        }
    }

    private static void** ProcessorVtable
    {
        get
        {
            lock (Gate)
            {
                if (_processorVtable != null) return _processorVtable;
                void** t = (void**)NativeMemory.AllocZeroed((nuint)(11 * sizeof(void*)));
                t[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&ProcessorQueryInterface;
                t[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                t[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                t[3] = (delegate* unmanaged[Stdcall]<void*, ulong*, int, ulong*, int, int>)&SetBusArrangements;
                t[4] = (delegate* unmanaged[Stdcall]<void*, int, int, ulong*, int>)&GetBusArrangement;
                t[5] = (delegate* unmanaged[Stdcall]<void*, int, int>)&CanProcessSampleSize;
                t[6] = (delegate* unmanaged[Stdcall]<void*, uint>)&GetLatencySamples;
                t[7] = (delegate* unmanaged[Stdcall]<void*, Vst3Abi.ProcessSetup*, int>)&SetupProcessing;
                t[8] = (delegate* unmanaged[Stdcall]<void*, byte, int>)&SetProcessing;
                t[9] = (delegate* unmanaged[Stdcall]<void*, Vst3Abi.ProcessData*, int>)&Process;
                t[10] = (delegate* unmanaged[Stdcall]<void*, uint>)&GetTailSamples;
                return _processorVtable = t;
            }
        }
    }

    private static void** ControllerVtable
    {
        get
        {
            lock (Gate)
            {
                if (_controllerVtable != null) return _controllerVtable;
                void** t = (void**)NativeMemory.AllocZeroed((nuint)(18 * sizeof(void*)));
                t[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&ControllerQueryInterface;
                t[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                t[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                t[3] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&ControllerInitialize;
                t[4] = (delegate* unmanaged[Stdcall]<void*, int>)&ControllerTerminate;
                t[5] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&SetComponentState;
                t[6] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&ControllerSetState;
                t[7] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&ControllerGetState;
                t[8] = (delegate* unmanaged[Stdcall]<void*, int>)&GetParameterCount;
                t[9] = (delegate* unmanaged[Stdcall]<void*, int, Vst3Abi.ParameterInfo*, int>)&GetParameterInfo;
                t[10] = (delegate* unmanaged[Stdcall]<void*, uint, double, char*, int>)&GetParamStringByValue;
                t[11] = (delegate* unmanaged[Stdcall]<void*, uint, char*, double*, int>)&GetParamValueByString;
                t[12] = (delegate* unmanaged[Stdcall]<void*, uint, double, double>)&NormalizedParamToPlain;
                t[13] = (delegate* unmanaged[Stdcall]<void*, uint, double, double>)&PlainParamToNormalized;
                t[14] = (delegate* unmanaged[Stdcall]<void*, uint, double>)&GetParamNormalized;
                t[15] = (delegate* unmanaged[Stdcall]<void*, uint, double, int>)&SetParamNormalized;
                t[16] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&SetComponentHandler;
                t[17] = (delegate* unmanaged[Stdcall]<void*, char*, void*>)&CreateView;
                return _controllerVtable = t;
            }
        }
    }

    // ── FUnknown ─────────────────────────────────────────────────

    // Refcounting is a no-op: the managed object owns every block and frees them all in Dispose,
    // and a test's lifetime is one method. Answering 1 rather than 0 keeps a host that checks the
    // count from concluding the object has already gone.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FactoryQueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;
        if (*iid == FUnknownIid || *iid == Vst3Abi.IPluginFactory) { *result = self; return Vst3Abi.ResultOk; }
        return Vst3Abi.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ComponentQueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null || self == null) return Vst3Abi.InvalidArgument;
        *result = null;
        if (*iid == FUnknownIid || *iid == Vst3Abi.IPluginBase || *iid == Vst3Abi.IComponent)
        {
            *result = self;
            return Vst3Abi.ResultOk;
        }
        if (*iid == Vst3Abi.IAudioProcessor)
        {
            // The processor subobject, one pointer in. This is the whole reason the block carries
            // two vtables: the same address under two interfaces would dispatch slot 7 to
            // getBusCount when the caller meant setupProcessing.
            *result = (byte*)self + sizeof(void*);
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ProcessorQueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null || self == null) return Vst3Abi.InvalidArgument;
        *result = null;
        if (*iid == FUnknownIid || *iid == Vst3Abi.IAudioProcessor) { *result = self; return Vst3Abi.ResultOk; }
        if (*iid == Vst3Abi.IComponent) { *result = (byte*)self - sizeof(void*); return Vst3Abi.ResultOk; }
        return Vst3Abi.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ControllerQueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;
        if (*iid == FUnknownIid || *iid == Vst3Abi.IPluginBase || *iid == Vst3Abi.IEditController)
        {
            *result = self;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    // ── IPluginFactory ───────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CountClasses(void* self) => 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetFactoryInfo(void* self, void* info) => Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetClassInfo(void* self, int index, Vst3Abi.PClassInfo* info)
    {
        if (info == null || (uint)index > 1) return Vst3Abi.InvalidArgument;
        byte[] id = index == 0 ? ComponentClassId : ControllerClassId;
        for (int i = 0; i < 16; i++) info->ClassId[i] = id[i];
        info->Cardinality = 0x7FFFFFFF;
        WriteAscii(info->Category, 32, index == 0 ? "Audio Module Class" : "Component Controller Class");
        WriteAscii(info->Name, 64, index == 0 ? "Synthetic Gain" : "Synthetic Gain Controller");
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateInstance(void* self, byte* classId, byte* iid, void** result)
    {
        if (result == null || classId == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;

        Vst3SyntheticPlugin? plugin = FromObject(self);
        if (plugin == null) return Vst3Abi.NoInterface;

        bool isComponent = Matches(classId, ComponentClassId);
        if (!isComponent && !Matches(classId, ControllerClassId)) return Vst3Abi.NoInterface;

        void* instance = isComponent ? plugin.CreateComponent() : plugin.CreateObject(ControllerVtable);
        var wanted = new Guid(new ReadOnlySpan<byte>(iid, 16));

        void** vtable = *(void***)instance;
        var queryInterface = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtable[0];
        return queryInterface(instance, &wanted, result);
    }

    private static bool Matches(byte* candidate, byte[] expected)
    {
        for (int i = 0; i < 16; i++) if (candidate[i] != expected[i]) return false;
        return true;
    }

    // ── IComponent ───────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ComponentInitialize(void* self, void* context) => Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ComponentTerminate(void* self) => Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetControllerClassId(void* self, byte* classId)
    {
        if (classId == null) return Vst3Abi.InvalidArgument;
        for (int i = 0; i < 16; i++) classId[i] = ControllerClassId[i];
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetIoMode(void* self, int mode) => Vst3Abi.NotImplemented;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetBusCount(void* self, int mediaType, int direction) =>
        mediaType == Vst3Abi.MediaTypeAudio ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetBusInfo(void* self, int mediaType, int direction, int index, Vst3Abi.BusInfo* info)
    {
        if (info == null || mediaType != Vst3Abi.MediaTypeAudio || index != 0) return Vst3Abi.InvalidArgument;
        info->MediaType = mediaType;
        info->Direction = direction;
        info->ChannelCount = 2;
        info->BusType = Vst3Abi.BusTypeMain;
        info->Flags = 1;
        WriteUtf16(info->Name, 128, direction == Vst3Abi.BusDirectionInput ? "In" : "Out");
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetRoutingInfo(void* self, int input, int output, void* unused) => Vst3Abi.NotImplemented;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ActivateBus(void* self, int mediaType, int direction, int index, byte state) =>
        Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetActive(void* self, byte state)
    {
        Vst3SyntheticPlugin? plugin = FromObject(self);
        if (plugin == null) return Vst3Abi.NoInterface;
        plugin.Active = state != 0;
        return Vst3Abi.ResultOk;
    }

    // State is the gain and nothing else, which is enough to prove the round trip carries something
    // the parameter list alone does not.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetState(void* self, void* stream)
    {
        Vst3SyntheticPlugin? plugin = FromObject(self);
        if (plugin == null || stream == null) return Vst3Abi.InvalidArgument;

        double gain = plugin.ProcessorValueOf(GainId);
        int written = 0;
        void** vtable = *(void***)stream;
        var write = (delegate* unmanaged[Stdcall]<void*, void*, int, int*, int>)vtable[4];
        int result = write(stream, &gain, sizeof(double), &written);
        return Vst3Abi.Ok(result) && written == sizeof(double) ? Vst3Abi.ResultOk : Vst3Abi.ResultFalse;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetState(void* self, void* stream) =>
        ReadStateInto(FromObject(self), stream, controller: false);

    // Shared by the component's setState and the controller's setComponentState, and it has to be a
    // separate method rather than one calling the other: an UnmanagedCallersOnly method is reachable
    // only through a function pointer, never from managed code. Which store it lands in is the
    // caller's business - that is the point of the host doing the handover at all.
    private static int ReadStateInto(Vst3SyntheticPlugin? plugin, void* stream, bool controller)
    {
        if (plugin == null || stream == null) return Vst3Abi.InvalidArgument;

        double gain = 0;
        int read = 0;
        void** vtable = *(void***)stream;
        var readCall = (delegate* unmanaged[Stdcall]<void*, void*, int, int*, int>)vtable[3];
        if (!Vst3Abi.Ok(readCall(stream, &gain, sizeof(double), &read)) || read != sizeof(double))
            return Vst3Abi.ResultFalse;

        double clamped = Math.Clamp(gain, 0, 1);
        if (controller) plugin._values[GainId] = clamped;
        else plugin._processorValues[GainId] = clamped;
        return Vst3Abi.ResultOk;
    }

    // ── IAudioProcessor ──────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetBusArrangements(void* self, ulong* inputs, int numIn, ulong* outputs, int numOut) =>
        numIn == 1 && numOut == 1 ? Vst3Abi.ResultOk : Vst3Abi.ResultFalse;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetBusArrangement(void* self, int direction, int index, ulong* arrangement)
    {
        if (arrangement == null) return Vst3Abi.InvalidArgument;
        *arrangement = 3;                               // stereo: left | right
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CanProcessSampleSize(void* self, int size) =>
        size == Vst3Abi.SampleSize32 ? Vst3Abi.ResultOk : Vst3Abi.ResultFalse;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint GetLatencySamples(void* self)
    {
        Vst3SyntheticPlugin? plugin = FromProcessor(self);
        return plugin == null ? 0 : (uint)Math.Max(0, plugin.LatencySamples);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint GetTailSamples(void* self) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetupProcessing(void* self, Vst3Abi.ProcessSetup* setup)
    {
        Vst3SyntheticPlugin? plugin = FromProcessor(self);
        if (plugin == null || setup == null) return Vst3Abi.InvalidArgument;
        plugin.SampleRate = setup->SampleRate;
        plugin.MaxBlockSize = setup->MaxSamplesPerBlock;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetProcessing(void* self, byte state) => Vst3Abi.ResultOk;

    /// <summary>
    /// Applies whatever arrived through <c>IParameterChanges</c>, then scales the block by the gain.
    /// </summary>
    /// <remarks>
    /// Reading the change list is the entire point of this class. A host that only calls
    /// <c>setParamNormalized</c> on the controller moves the plugin's own display and nothing else;
    /// the processor hears a parameter <b>here</b> or not at all.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Process(void* self, Vst3Abi.ProcessData* data)
    {
        Vst3SyntheticPlugin? plugin = FromProcessor(self);
        if (plugin == null || data == null) return Vst3Abi.InvalidArgument;
        plugin.ProcessCalls++;

        if (data->InputParameterChanges != 0)
        {
            void* changes = (void*)data->InputParameterChanges;
            void** vtable = *(void***)changes;
            int count = ((delegate* unmanaged[Stdcall]<void*, int>)vtable[3])(changes);
            var getData = (delegate* unmanaged[Stdcall]<void*, int, void*>)vtable[4];

            for (int i = 0; i < count; i++)
            {
                void* queue = getData(changes, i);
                if (queue == null) continue;
                void** queueVtable = *(void***)queue;
                uint id = ((delegate* unmanaged[Stdcall]<void*, uint>)queueVtable[3])(queue);
                int points = ((delegate* unmanaged[Stdcall]<void*, int>)queueVtable[4])(queue);
                if (points <= 0) continue;

                // The last point in the block is the value the block ends at, which is what a
                // coalesced-per-block list carries.
                var getPoint = (delegate* unmanaged[Stdcall]<void*, int, int*, double*, int>)queueVtable[5];
                int offset;
                double value;
                if (!Vst3Abi.Ok(getPoint(queue, points - 1, &offset, &value))) continue;

                plugin._processorValues[id] = Math.Clamp(value, 0, 1);
                plugin.ProcessorSawParameters.Add((id, value));
            }
        }

        int samples = data->NumSamples;
        if (samples <= 0 || data->Inputs == null || data->Outputs == null) return Vst3Abi.ResultOk;

        bool bypassed = plugin.ProcessorValueOf(BypassId) >= 0.5;
        float gain = bypassed ? 1f : (float)(plugin.ProcessorValueOf(GainId) * 2.0);

        Vst3Abi.AudioBusBuffers* input = data->Inputs;
        Vst3Abi.AudioBusBuffers* output = data->Outputs;
        int channels = Math.Min(input->ChannelCount, output->ChannelCount);

        float** inputChannels = input->ChannelBuffers;
        float** outputChannels = output->ChannelBuffers;
        if (inputChannels == null || outputChannels == null) return Vst3Abi.ResultOk;

        for (int c = 0; c < channels; c++)
        {
            float* source = inputChannels[c];
            float* destination = outputChannels[c];
            if (source == null || destination == null) continue;
            for (int i = 0; i < samples; i++) destination[i] = source[i] * gain;
        }
        return Vst3Abi.ResultOk;
    }

    // ── IEditController ──────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ControllerInitialize(void* self, void* context) => Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ControllerTerminate(void* self) => Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetComponentState(void* self, void* stream)
    {
        Vst3SyntheticPlugin? plugin = FromObject(self);
        if (plugin == null) return Vst3Abi.InvalidArgument;
        plugin.TookComponentState = true;
        return ReadStateInto(plugin, stream, controller: true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ControllerSetState(void* self, void* stream) => Vst3Abi.NotImplemented;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ControllerGetState(void* self, void* stream) => Vst3Abi.NotImplemented;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetParameterCount(void* self) => Descriptors.Length;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetParameterInfo(void* self, int index, Vst3Abi.ParameterInfo* info)
    {
        if (info == null || (uint)index >= (uint)Descriptors.Length) return Vst3Abi.InvalidArgument;
        Descriptor descriptor = Descriptors[index];
        info->Id = descriptor.Id;
        WriteUtf16(info->Title, 128, descriptor.Title);
        WriteUtf16(info->ShortTitle, 128, descriptor.Title);
        WriteUtf16(info->Units, 128, descriptor.Units);
        info->StepCount = descriptor.Steps;
        info->DefaultNormalizedValue = descriptor.Default;
        info->UnitId = 0;
        info->Flags = descriptor.Flags;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetParamStringByValue(void* self, uint id, double normalized, char* text)
    {
        if (text == null) return Vst3Abi.InvalidArgument;
        WriteUtf16(text, 128, $"{normalized * 100:F1}%");
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetParamValueByString(void* self, uint id, char* text, double* normalized) =>
        Vst3Abi.NotImplemented;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static double NormalizedParamToPlain(void* self, uint id, double normalized) => normalized * 100;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static double PlainParamToNormalized(void* self, uint id, double plain) => plain / 100;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static double GetParamNormalized(void* self, uint id)
    {
        Vst3SyntheticPlugin? plugin = FromObject(self);
        return plugin?.ValueOf(id) ?? 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetParamNormalized(void* self, uint id, double value)
    {
        Vst3SyntheticPlugin? plugin = FromObject(self);
        if (plugin == null || !plugin._values.ContainsKey(id)) return Vst3Abi.InvalidArgument;
        plugin._values[id] = Math.Clamp(value, 0, 1);
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetComponentHandler(void* self, void* handler)
    {
        Vst3SyntheticPlugin? plugin = FromObject(self);
        if (plugin == null) return Vst3Abi.InvalidArgument;
        plugin._componentHandler = handler;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* CreateView(void* self, char* name) => null;

    // ── text helpers ─────────────────────────────────────────────

    private static void WriteAscii(byte* destination, int capacity, string value)
    {
        int i = 0;
        for (; i < value.Length && i < capacity - 1; i++) destination[i] = (byte)value[i];
        for (; i < capacity; i++) destination[i] = 0;
    }

    private static void WriteUtf16(char* destination, int capacity, string value)
    {
        int i = 0;
        for (; i < value.Length && i < capacity - 1; i++) destination[i] = value[i];
        for (; i < capacity; i++) destination[i] = '\0';
    }

    public void Dispose()
    {
        foreach (nint block in _allocations) NativeMemory.Free((void*)block);
        _allocations.Clear();
        _factory = null;
        if (_self.IsAllocated) _self.Free();
        GC.SuppressFinalize(this);
    }
}
