using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>One parameter a plugin exposes.</summary>
/// <param name="Id">The plugin's own identifier, which is not an index and need not be contiguous.</param>
/// <param name="Title">What the plugin calls it.</param>
/// <param name="Units">Its unit, where it states one.</param>
/// <param name="DefaultNormalized">Its default, on the normalised 0–1 scale every VST3 parameter uses.</param>
/// <param name="StepCount">0 for continuous, otherwise the number of steps above the first.</param>
/// <param name="Flags">The VST3 flag word, for the bypass and read-only bits.</param>
public sealed record Vst3Parameter(
    uint Id, string Title, string Units, double DefaultNormalized, int StepCount, int Flags)
{
    public bool IsBypass => (Flags & Vst3Abi.ParamIsBypass) != 0;
    public bool IsReadOnly => (Flags & Vst3Abi.ParamIsReadOnly) != 0;
    public bool IsHidden => (Flags & Vst3Abi.ParamIsHidden) != 0;
    public bool CanAutomate => (Flags & Vst3Abi.ParamCanAutomate) != 0;
}

/// <summary>
/// An instantiated VST3 effect: its component, its processor, its controller, and the buffers one
/// <c>process</c> call needs.
/// </summary>
/// <remarks>
/// <para>
/// The awkward part is not the calls but the buffers. This app processes <b>interleaved</b> float
/// blocks; VST3 processes <b>deinterleaved</b> ones, taking an array of per-channel pointers. So a
/// scratch plane is allocated once at <see cref="Configure"/> and the block is scattered into it and
/// gathered back on every call. Allocating that per call would put a garbage collection on the audio
/// thread, which is the one thing a real-time path cannot have.
/// </para>
/// <para>
/// <b>Everything here runs somebody else's code.</b> Every entry point is guarded, and a plugin that
/// faults takes down the process regardless — which is why the catalogue scans out of process and
/// only instantiates what has already survived a scan.
/// </para>
/// </remarks>
public sealed unsafe class Vst3Plugin : IDisposable
{
    private readonly Vst3Module _module;
    private void* _component;
    private void* _processor;
    private void* _controller;
    private bool _controllerIsComponent;
    private Vst3ComponentHandler? _handler;
    private Vst3ParameterChanges? _changes;

    private float** _inputPointers;
    private float** _outputPointers;
    private float* _inputPlane;
    private float* _outputPlane;
    private Vst3Abi.AudioBusBuffers* _inputBus;
    private Vst3Abi.AudioBusBuffers* _outputBus;

    private int _channels;
    private int _blockSize;
    private bool _active;
    private bool _processing;
    private bool _disposed;

    private Vst3Plugin(Vst3Module module, Vst3ClassInfo info)
    {
        _module = module;
        Class = info;
    }

    public Vst3ClassInfo Class { get; }
    public IReadOnlyList<Vst3Parameter> Parameters { get; private set; } = [];
    public int LatencySamples { get; private set; }
    public int InputChannels { get; private set; }
    public int OutputChannels { get; private set; }

    /// <summary>Whether the plugin accepted the channel count it was configured with.</summary>
    public bool ArrangementAccepted { get; private set; }

    /// <summary>
    /// Instantiates an effect class. Returns null with a reason rather than throwing.
    /// </summary>
    public static Vst3Plugin? Create(Vst3Module module, Vst3ClassInfo info, out string error)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(info);
        error = "";

        var plugin = new Vst3Plugin(module, info);
        try
        {
            plugin._component = module.CreateInstance(info.ClassId, Vst3Abi.IComponent);
            if (plugin._component == null)
            {
                error = "The plugin would not create its component.";
                plugin.Dispose();
                return null;
            }

            // initialize comes before anything else is asked of it, and it is given a real host
            // context. A null one is legal and every plugin tested still loads and processes with it
            // — but several build their parameter list only once they can ask the host who it is,
            // and with null they come up with no parameters at all and look broken.
            int initialised = plugin.ComponentInitialize(Vst3HostContext.Instance);
            if (!Vst3Abi.Ok(initialised))
            {
                error = $"The plugin refused to initialise (0x{initialised:X8}).";
                plugin.Dispose();
                return null;
            }

            plugin._processor = Vst3Module.QueryInterface(plugin._component, Vst3Abi.IAudioProcessor);
            if (plugin._processor == null)
            {
                error = "The component is not an audio processor.";
                plugin.Dispose();
                return null;
            }

            plugin.AttachController();
            plugin.GiveComponentHandler();
            plugin.HandOverComponentState();
            plugin.ReadParameters();
            plugin.ReadBusCounts();

            // Built here, once, from the parameter list that has just been read: the change list is
            // fixed-size, and its size is the number of parameters there are to change.
            plugin._changes = new Vst3ParameterChanges([.. plugin.Parameters.Select(p => p.Id)]);
            return plugin;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            plugin.Dispose();
            return null;
        }
    }

    // ── vtable helpers ───────────────────────────────────────────

    private int ComponentInitialize(void* context)
    {
        void** vtable = *(void***)_component;
        return ((delegate* unmanaged[Stdcall]<void*, void*, int>)vtable[3])(_component, context);
    }

    private int ComponentTerminate()
    {
        void** vtable = *(void***)_component;
        return ((delegate* unmanaged[Stdcall]<void*, int>)vtable[4])(_component);
    }

    private int GetBusCount(int mediaType, int direction)
    {
        void** vtable = *(void***)_component;
        return ((delegate* unmanaged[Stdcall]<void*, int, int, int>)vtable[7])(
            _component, mediaType, direction);
    }

    private bool GetBusInfo(int mediaType, int direction, int index, out Vst3Abi.BusInfo info)
    {
        void** vtable = *(void***)_component;
        var call = (delegate* unmanaged[Stdcall]<void*, int, int, int, Vst3Abi.BusInfo*, int>)vtable[8];

        Vst3Abi.BusInfo local;
        bool ok = Vst3Abi.Ok(call(_component, mediaType, direction, index, &local));
        info = local;
        return ok;
    }

    private int ActivateBus(int mediaType, int direction, int index, bool state)
    {
        void** vtable = *(void***)_component;
        return ((delegate* unmanaged[Stdcall]<void*, int, int, int, byte, int>)vtable[10])(
            _component, mediaType, direction, index, state ? (byte)1 : (byte)0);
    }

    private int SetActive(bool state)
    {
        void** vtable = *(void***)_component;
        return ((delegate* unmanaged[Stdcall]<void*, byte, int>)vtable[11])(
            _component, state ? (byte)1 : (byte)0);
    }

    private int SetBusArrangements(ulong* inputs, int numIn, ulong* outputs, int numOut)
    {
        void** vtable = *(void***)_processor;
        return ((delegate* unmanaged[Stdcall]<void*, ulong*, int, ulong*, int, int>)vtable[3])(
            _processor, inputs, numIn, outputs, numOut);
    }

    private int CanProcessSampleSize(int size)
    {
        void** vtable = *(void***)_processor;
        return ((delegate* unmanaged[Stdcall]<void*, int, int>)vtable[5])(_processor, size);
    }

    private uint GetLatencySamples()
    {
        void** vtable = *(void***)_processor;
        return ((delegate* unmanaged[Stdcall]<void*, uint>)vtable[6])(_processor);
    }

    private int SetupProcessing(Vst3Abi.ProcessSetup* setup)
    {
        void** vtable = *(void***)_processor;
        return ((delegate* unmanaged[Stdcall]<void*, Vst3Abi.ProcessSetup*, int>)vtable[7])(
            _processor, setup);
    }

    private int SetProcessing(bool state)
    {
        void** vtable = *(void***)_processor;
        return ((delegate* unmanaged[Stdcall]<void*, byte, int>)vtable[8])(
            _processor, state ? (byte)1 : (byte)0);
    }

    private int Process(Vst3Abi.ProcessData* data)
    {
        void** vtable = *(void***)_processor;
        return ((delegate* unmanaged[Stdcall]<void*, Vst3Abi.ProcessData*, int>)vtable[9])(
            _processor, data);
    }

    // ── controller ───────────────────────────────────────────────

    /// <summary>
    /// Finds the plugin's controller, which is where the parameter list lives.
    /// </summary>
    /// <remarks>
    /// VST3 separates the processor from its controller so that the two can live in different
    /// processes, and a host is expected to create both. Many plugins are simpler than that and
    /// answer to both interfaces from one object, so that is tried first — creating a second
    /// instance of something that is already the controller would give it two states to keep in step.
    /// </remarks>
    private void AttachController()
    {
        _controller = Vst3Module.QueryInterface(_component, Vst3Abi.IEditController);
        if (_controller != null)
        {
            _controllerIsComponent = true;
            ControllerStatus = "the component answers to it";
            return;
        }

        void** vtable = *(void***)_component;
        var getControllerClassId = (delegate* unmanaged[Stdcall]<void*, byte*, int>)vtable[5];

        var classId = new byte[16];
        int asked;
        fixed (byte* id = classId)
        {
            asked = getControllerClassId(_component, id);
        }
        if (!Vst3Abi.Ok(asked))
        {
            ControllerStatus = $"getControllerClassId returned 0x{asked:X8}";
            return;
        }
        ControllerClassIdText = Vst3Abi.ClassIdText(classId);
        if (classId.All(b => b == 0))
        {
            ControllerStatus = "the component names no controller class";
            return;
        }

        _controller = _module.CreateInstance(classId, Vst3Abi.IEditController);
        if (_controller == null)
        {
            // Falling back through the base interface tells the two failures apart: a controller
            // that cannot be created at all, and one that can but does not recognise the identifier
            // this host asked it for.
            void* asBase = _module.CreateInstance(classId, Vst3Abi.IPluginBase);
            if (asBase != null)
            {
                _controller = Vst3Module.QueryInterface(asBase, Vst3Abi.IEditController);
                Vst3Module.Release(asBase);
                ControllerStatus = _controller != null
                    ? "created through IPluginBase"
                    : "created, but does not answer to IEditController";
            }
            else
            {
                ControllerStatus = $"controller class {Vst3Abi.ClassIdText(classId)} would not be created";
            }
            if (_controller == null) return;
        }
        else
        {
            ControllerStatus = "created from the component's controller class";
        }

        void** controllerVtable = *(void***)_controller;
        var initialize = (delegate* unmanaged[Stdcall]<void*, void*, int>)controllerVtable[3];
        int result = initialize(_controller, Vst3HostContext.Instance);
        if (!Vst3Abi.Ok(result))
        {
            ControllerStatus = $"the controller refused to initialise (0x{result:X8})";
            Vst3Module.Release(_controller);
            _controller = null;
        }
    }

    /// <summary>How the controller was found, or why it was not. For diagnosis, and for the scan log.</summary>
    public string ControllerStatus { get; private set; } = "not attached";

    /// <summary>The controller class the component named, for checking against the factory's list.</summary>
    public string ControllerClassIdText { get; private set; } = "";

    /// <summary>
    /// What the controller said its parameter count was, which is not the same as how many were
    /// readable — a count with nothing behind it is a different fault from a count of nothing.
    /// </summary>
    public int DeclaredParameterCount { get; private set; }

    /// <summary>
    /// Gives the controller somewhere to report edits, which is a host obligation and not a courtesy.
    /// </summary>
    /// <remarks>
    /// When a user moves a control in a plugin's own editor, <c>performEdit</c> on this handler is
    /// the only way the host hears about it. Some plugins also refuse to create an editor until they
    /// have one, on the reasonable grounds that an editor with nowhere to report is a UI that
    /// silently does nothing.
    /// </remarks>
    private void GiveComponentHandler()
    {
        if (_controller == null) return;

        _handler = new Vst3ComponentHandler();
        _handler.ParameterEdited += edit => ParameterEdited?.Invoke(edit);
        _handler.RestartRequested += flags =>
        {
            // A plugin that changes its own latency has to be believed: the host is compensating for
            // a delay it no longer has until it reads the new one.
            if ((flags & Vst3ComponentHandler.RestartLatencyChanged) != 0 && _processor != null)
                LatencySamples = (int)Math.Min(GetLatencySamples(), int.MaxValue);
            RestartRequested?.Invoke(flags);
        };

        void** vtable = *(void***)_controller;
        var setComponentHandler = (delegate* unmanaged[Stdcall]<void*, void*, int>)vtable[16];
        int result = setComponentHandler(_controller, _handler.Pointer);
        HandlerAccepted = Vst3Abi.Ok(result);
    }

    /// <summary>Whether the controller took the host's edit handler.</summary>
    public bool HandlerAccepted { get; private set; }

    /// <summary>Raised when the user moves something in the plugin's own editor.</summary>
    public event Action<Vst3ParameterEdit>? ParameterEdited;

    /// <summary>Raised when the plugin says its latency, parameters or layout have changed.</summary>
    public event Action<int>? RestartRequested;

    /// <summary>
    /// Hands the controller the component's current state, which is the step a host owes a plugin
    /// whose two halves it created separately.
    /// </summary>
    /// <remarks>
    /// VST3 splits a plugin so the processor and its controller can live in different processes, and
    /// the price is that they start out knowing nothing about each other. The host is what joins
    /// them: it reads the component's state and gives it to the controller. Skipping it leaves the
    /// controller showing defaults for a processor that is not at its defaults — and some plugins
    /// build no parameter list at all until they have been told.
    /// </remarks>
    private void HandOverComponentState()
    {
        if (_controller == null || _controllerIsComponent) return;

        using var stream = new Vst3MemoryStream();
        void** componentVtable = *(void***)_component;
        var getState = (delegate* unmanaged[Stdcall]<void*, void*, int>)componentVtable[13];
        if (!Vst3Abi.Ok(getState(_component, stream.Pointer))) return;

        // Rewound, because the component wrote to it and left the cursor at the end. A controller
        // handed a stream positioned past its own data reads nothing and reports no error.
        stream.Rewind();

        void** controllerVtable = *(void***)_controller;
        var setComponentState = (delegate* unmanaged[Stdcall]<void*, void*, int>)controllerVtable[5];
        int result = setComponentState(_controller, stream.Pointer);
        StateHandover = Vst3Abi.Ok(result)
            ? $"{stream.ToArray().Length} bytes"
            : $"refused (0x{result:X8})";
    }

    /// <summary>What happened when the component's state was handed to the controller.</summary>
    public string StateHandover { get; private set; } = "not needed";

    private void ReadParameters()
    {
        if (_controller == null) return;

        void** vtable = *(void***)_controller;
        var getParameterCount = (delegate* unmanaged[Stdcall]<void*, int>)vtable[8];
        var getParameterInfo = (delegate* unmanaged[Stdcall]<void*, int, Vst3Abi.ParameterInfo*, int>)vtable[9];

        int count = getParameterCount(_controller);
        DeclaredParameterCount = count;
        var parameters = new List<Vst3Parameter>(Math.Clamp(count, 0, 4096));

        for (int i = 0; i < Math.Clamp(count, 0, 4096); i++)
        {
            Vst3Abi.ParameterInfo info;
            int asked = getParameterInfo(_controller, i, &info);
            if (!Vst3Abi.Ok(asked))
            {
                if (i == 0) ControllerStatus += $"; getParameterInfo(0) returned 0x{asked:X8}";
                continue;
            }

            parameters.Add(new Vst3Parameter(
                info.Id,
                Vst3Abi.Utf16(info.Title, 128),
                Vst3Abi.Utf16(info.Units, 128),
                Math.Clamp(info.DefaultNormalizedValue, 0, 1),
                Math.Max(0, info.StepCount),
                info.Flags));
        }
        Parameters = parameters;
    }

    /// <summary>Reads a parameter's value as the plugin would display it, e.g. "−6.0 dB".</summary>
    public string DisplayValue(uint id, double normalized)
    {
        if (_controller == null || _disposed) return "";

        void** vtable = *(void***)_controller;
        var getParamStringByValue =
            (delegate* unmanaged[Stdcall]<void*, uint, double, char*, int>)vtable[10];

        char* text = stackalloc char[128];
        for (int i = 0; i < 128; i++) text[i] = '\0';
        return Vst3Abi.Ok(getParamStringByValue(_controller, id, Math.Clamp(normalized, 0, 1), text))
            ? Vst3Abi.Utf16(text, 128)
            : "";
    }

    /// <summary>Sets a parameter on the controller, which is where a plugin's own editor would set it.</summary>
    public bool SetParameter(uint id, double normalized)
    {
        if (_controller == null || _disposed) return false;

        void** vtable = *(void***)_controller;
        var setParamNormalized = (delegate* unmanaged[Stdcall]<void*, uint, double, int>)vtable[15];
        return Vst3Abi.Ok(setParamNormalized(_controller, id, Math.Clamp(normalized, 0, 1)));
    }

    public double GetParameter(uint id)
    {
        if (_controller == null || _disposed) return 0;

        void** vtable = *(void***)_controller;
        var getParamNormalized = (delegate* unmanaged[Stdcall]<void*, uint, double>)vtable[14];
        return getParamNormalized(_controller, id);
    }

    /// <summary>
    /// Moves a parameter for real: on the controller, so the plugin's own editor and its readouts
    /// agree, and in the change list the next <c>process</c> call carries, so the audio follows.
    /// </summary>
    /// <remarks>
    /// Both halves are needed and neither is sufficient. VST3 keeps the processor and the controller
    /// apart, and setting only the controller is the failure that looks like success: the slider
    /// moves, the plugin's display agrees, and nothing can be heard.
    /// </remarks>
    public bool ApplyParameter(uint id, double normalized)
    {
        if (_disposed) return false;

        double clamped = Math.Clamp(normalized, 0, 1);
        QueueParameter(id, clamped);
        return SetParameter(id, clamped);
    }

    /// <summary>
    /// Carries a value to the processor without touching the controller — what an edit made in the
    /// plugin's own editor needs, since the controller already has it.
    /// </summary>
    public void QueueParameter(uint id, double normalized)
    {
        if (_disposed || _changes is not { } changes) return;
        int index = changes.IndexOf(id);
        if (index >= 0) changes.Set(index, normalized);
    }

    /// <summary>
    /// The plugin's own editor, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Must be created and disposed on the thread that will own the window. For the plugins tested
    /// here this is not a nicety: they publish no parameters, so the editor is the only way to
    /// operate them at all.
    /// </remarks>
    public Vst3PlugView? CreateEditor() =>
        _disposed || _controller == null ? null : Vst3PlugView.Create(_controller);

    /// <summary>Whether the plugin offers an editor, without keeping one open.</summary>
    public bool HasEditor
    {
        get
        {
            using Vst3PlugView? view = CreateEditor();
            return view != null;
        }
    }

    private void ReadBusCounts()
    {
        InputChannels = ChannelsOn(Vst3Abi.BusDirectionInput);
        OutputChannels = ChannelsOn(Vst3Abi.BusDirectionOutput);
    }

    private int ChannelsOn(int direction)
    {
        int buses = GetBusCount(Vst3Abi.MediaTypeAudio, direction);
        if (buses <= 0) return 0;

        // The main bus only. Auxiliary buses are side-chains and extra outputs, and feeding a
        // side-chain the programme would be a decision this host has not been asked to make.
        for (int i = 0; i < buses; i++)
        {
            if (!GetBusInfo(Vst3Abi.MediaTypeAudio, direction, i, out Vst3Abi.BusInfo info)) continue;
            if (info.BusType == Vst3Abi.BusTypeMain) return info.ChannelCount;
        }
        return 0;
    }

    // ── configuration ────────────────────────────────────────────

    /// <summary>
    /// Prepares the plugin for a stream. Returns false when it will not take this shape of audio.
    /// </summary>
    public bool Configure(int sampleRate, int channels, int maxBlockSize, bool offline = false)
    {
        if (_disposed || _processor == null || _component == null) return false;
        if (channels is < 1 or > 8 || sampleRate <= 0 || maxBlockSize <= 0) return false;

        Deactivate();

        // 32-bit float only. A plugin that cannot take it is rare enough that converting the whole
        // path to double for its sake would be paying for it everywhere.
        if (!Vst3Abi.Ok(CanProcessSampleSize(Vst3Abi.SampleSize32))) return false;

        ulong arrangement = channels == 1 ? Vst3Abi.SpeakerMono : Vst3Abi.SpeakerStereo;
        ulong input = arrangement, output = arrangement;
        ArrangementAccepted = Vst3Abi.Ok(SetBusArrangements(&input, 1, &output, 1));

        var setup = new Vst3Abi.ProcessSetup
        {
            ProcessMode = offline ? Vst3Abi.ProcessModeOffline : Vst3Abi.ProcessModeRealtime,
            SymbolicSampleSize = Vst3Abi.SampleSize32,
            MaxSamplesPerBlock = maxBlockSize,
            SampleRate = sampleRate,
        };
        if (!Vst3Abi.Ok(SetupProcessing(&setup))) return false;

        ActivateBus(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionInput, 0, true);
        ActivateBus(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionOutput, 0, true);

        AllocateBuffers(channels, maxBlockSize);
        if (!Vst3Abi.Ok(SetActive(true))) return false;
        _active = true;

        if (!Vst3Abi.Ok(SetProcessing(true))) return false;
        _processing = true;

        // Read after setup, not before: latency depends on the block size and rate for anything that
        // works in frames, and a figure read too early is the plugin's default rather than its own.
        LatencySamples = (int)Math.Min(GetLatencySamples(), int.MaxValue);
        ReadBusCounts();
        return true;
    }

    private void AllocateBuffers(int channels, int blockSize)
    {
        FreeBuffers();
        _channels = channels;
        _blockSize = blockSize;

        _inputPlane = (float*)NativeMemory.AlignedAlloc((nuint)(channels * blockSize * sizeof(float)), 32);
        _outputPlane = (float*)NativeMemory.AlignedAlloc((nuint)(channels * blockSize * sizeof(float)), 32);
        _inputPointers = (float**)NativeMemory.Alloc((nuint)(channels * sizeof(float*)));
        _outputPointers = (float**)NativeMemory.Alloc((nuint)(channels * sizeof(float*)));

        for (int c = 0; c < channels; c++)
        {
            _inputPointers[c] = _inputPlane + c * blockSize;
            _outputPointers[c] = _outputPlane + c * blockSize;
        }

        NativeMemory.Clear(_inputPlane, (nuint)(channels * blockSize * sizeof(float)));
        NativeMemory.Clear(_outputPlane, (nuint)(channels * blockSize * sizeof(float)));

        _inputBus = (Vst3Abi.AudioBusBuffers*)NativeMemory.AllocZeroed(
            (nuint)sizeof(Vst3Abi.AudioBusBuffers));
        _outputBus = (Vst3Abi.AudioBusBuffers*)NativeMemory.AllocZeroed(
            (nuint)sizeof(Vst3Abi.AudioBusBuffers));

        _inputBus->ChannelCount = channels;
        _inputBus->ChannelBuffers = _inputPointers;
        _outputBus->ChannelCount = channels;
        _outputBus->ChannelBuffers = _outputPointers;
    }

    private void FreeBuffers()
    {
        if (_inputPlane != null) { NativeMemory.AlignedFree(_inputPlane); _inputPlane = null; }
        if (_outputPlane != null) { NativeMemory.AlignedFree(_outputPlane); _outputPlane = null; }
        if (_inputPointers != null) { NativeMemory.Free(_inputPointers); _inputPointers = null; }
        if (_outputPointers != null) { NativeMemory.Free(_outputPointers); _outputPointers = null; }
        if (_inputBus != null) { NativeMemory.Free(_inputBus); _inputBus = null; }
        if (_outputBus != null) { NativeMemory.Free(_outputBus); _outputBus = null; }
    }

    // ── processing ───────────────────────────────────────────────

    /// <summary>
    /// Runs one interleaved block through the plugin, in place.
    /// </summary>
    /// <remarks>
    /// Blocks longer than the configured maximum are split rather than refused: a host that promised
    /// a maximum and then exceeded it is handing a plugin a buffer overrun, and callers here are not
    /// all in a position to know the limit.
    /// </remarks>
    public bool ProcessInterleaved(float[] buffer, int offset, int count)
    {
        if (_disposed || !_processing || _channels <= 0) return false;
        if (buffer == null || count <= 0) return false;

        int frames = count / _channels;
        if (frames <= 0) return false;

        int done = 0;
        while (done < frames)
        {
            int block = Math.Min(_blockSize, frames - done);
            if (!ProcessBlock(buffer, offset + done * _channels, block)) return false;
            done += block;
        }
        return true;
    }

    private bool ProcessBlock(float[] buffer, int offset, int frames)
    {
        // Scatter into the plugin's per-channel planes.
        for (int c = 0; c < _channels; c++)
        {
            float* destination = _inputPointers[c];
            for (int i = 0; i < frames; i++)
            {
                float sample = buffer[offset + i * _channels + c];
                destination[i] = float.IsFinite(sample) ? sample : 0f;
            }
        }

        var data = new Vst3Abi.ProcessData
        {
            ProcessMode = Vst3Abi.ProcessModeRealtime,
            SymbolicSampleSize = Vst3Abi.SampleSize32,
            NumSamples = frames,
            NumInputs = 1,
            NumOutputs = 1,
            Inputs = _inputBus,
            Outputs = _outputBus,

            // Zero for a block in which nothing moved, which is nearly all of them. Prepare folds
            // whatever the UI has set since the last block into a list the plugin reads in place —
            // no allocation, no lock, and no work at all when there is nothing to carry.
            InputParameterChanges = _changes?.Prepare() ?? 0,
        };
        _inputBus->SilenceFlags = 0;
        _outputBus->SilenceFlags = 0;

        if (!Vst3Abi.Ok(Process(&data))) return false;

        // Gather back. A plugin may report a bus as silent rather than writing zeros into it, and
        // reading the buffer regardless would hand back whatever was left there last time.
        ulong silence = _outputBus->SilenceFlags;
        for (int c = 0; c < _channels; c++)
        {
            bool silent = (silence & (1UL << c)) != 0;
            float* source = _outputPointers[c];
            for (int i = 0; i < frames; i++)
                buffer[offset + i * _channels + c] = silent ? 0f : source[i];
        }
        return true;
    }

    // ── state ────────────────────────────────────────────────────

    /// <summary>
    /// The plugin's settings as an opaque run of bytes, or empty when it will not give them up.
    /// </summary>
    /// <remarks>
    /// There is no other way to ask. A VST3 plugin's state is not its parameter values — it is
    /// whatever the plugin decides to write, which for anything with an impulse response, a wavetable
    /// or a modulation matrix is a great deal more than a host can see. Saving only the parameters
    /// would round-trip most plugins badly and the plugins on this machine, which publish none, not
    /// at all.
    /// </remarks>
    public byte[] SaveState()
    {
        if (_disposed || _component == null) return [];
        try
        {
            using var stream = new Vst3MemoryStream();
            void** vtable = *(void***)_component;
            var getState = (delegate* unmanaged[Stdcall]<void*, void*, int>)vtable[13];
            return Vst3Abi.Ok(getState(_component, stream.Pointer)) ? stream.ToArray() : [];
        }
        catch { return []; }
    }

    /// <summary>Puts a saved state back, and tells the controller about it.</summary>
    /// <remarks>
    /// The controller is a separate step and not an optional one: <c>setState</c> restores the
    /// processor, and a controller that is not told stays showing whatever it had, so the plugin
    /// sounds restored and looks unrestored. Rewound in between, because the component read the
    /// stream to its end.
    /// </remarks>
    public bool RestoreState(byte[] state)
    {
        if (_disposed || _component == null || state is not { Length: > 0 }) return false;
        try
        {
            using var stream = new Vst3MemoryStream(state);
            void** vtable = *(void***)_component;
            var setState = (delegate* unmanaged[Stdcall]<void*, void*, int>)vtable[12];
            if (!Vst3Abi.Ok(setState(_component, stream.Pointer))) return false;

            if (_controller != null && !_controllerIsComponent)
            {
                stream.Rewind();
                void** controllerVtable = *(void***)_controller;
                var setComponentState = (delegate* unmanaged[Stdcall]<void*, void*, int>)controllerVtable[5];
                setComponentState(_controller, stream.Pointer);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Clears whatever the plugin is holding — delay lines, reverb tails, envelope followers.
    /// </summary>
    /// <remarks>
    /// Toggling <c>setProcessing</c> is the only flush VST3 defines; there is no reset call. It
    /// matters on bypass: a plugin taken out of the chain and put back keeps its tail otherwise, and
    /// the tail arrives without the signal that produced it.
    /// </remarks>
    public void FlushProcessingState()
    {
        if (_disposed || _processor == null || !_processing) return;
        try
        {
            SetProcessing(false);
            SetProcessing(true);
        }
        catch { }
    }

    private void Deactivate()
    {
        if (_processing) { try { SetProcessing(false); } catch { } _processing = false; }
        if (_active) { try { SetActive(false); } catch { } _active = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Deactivate(); } catch { }

        // Freed only after processing has stopped: the change list is memory a plugin dereferences
        // inside process, and releasing it while a block is in flight is a read of freed memory.
        _changes?.Dispose();
        _changes = null;

        if (_controller != null)
        {
            try
            {
                // Only terminate a controller this host created. One that is the component itself is
                // terminated with it, and doing it twice is a plugin's business end being told to
                // shut down after it already has.
                if (!_controllerIsComponent)
                {
                    void** vtable = *(void***)_controller;
                    ((delegate* unmanaged[Stdcall]<void*, int>)vtable[4])(_controller);
                }
                Vst3Module.Release(_controller);
            }
            catch { }
            _controller = null;
        }

        if (_processor != null)
        {
            try { Vst3Module.Release(_processor); } catch { }
            _processor = null;
        }

        // After the controller has been released, never before: a plugin still holding the handler
        // would be calling into freed memory.
        _handler?.Dispose();
        _handler = null;

        if (_component != null)
        {
            try { ComponentTerminate(); } catch { }
            try { Vst3Module.Release(_component); } catch { }
            _component = null;
        }

        FreeBuffers();
        GC.SuppressFinalize(this);
    }
}
