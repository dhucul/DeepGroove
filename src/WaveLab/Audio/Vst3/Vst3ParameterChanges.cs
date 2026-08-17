using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>
/// The host's parameter-change list, handed to a plugin on every <c>process</c> call.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes a rack slider audible.</b> <c>setParamNormalized</c> on the controller moves
/// the plugin's own display and nothing else — VST3 keeps the processor and the controller apart on
/// purpose, and the only route from one to the other is the host carrying the change into
/// <c>process</c> as an <c>IParameterChanges</c>. Without it a slider moves, the plugin's editor
/// agrees with it, and the audio does not change: the most convincing kind of broken.
/// </para>
/// <para>
/// The change list is <b>coalesced per parameter rather than queued</b>. A block is a moment as far
/// as a parameter is concerned, and only the newest value for each one matters when it arrives; a
/// queue of every intermediate value a dragged slider passed through would be more data describing
/// the same outcome. That choice is what lets the whole structure be fixed at construction — one slot
/// per parameter, filled in place — so the audio thread allocates nothing and takes no lock, and a
/// user spinning a knob cannot make the list grow.
/// </para>
/// <para>
/// The native objects are <b>plain memory with a vtable</b>, not managed objects behind a
/// <see cref="GCHandle"/> like <see cref="Vst3MemoryStream"/>. Everything a plugin can ask a value
/// queue — its parameter, how many points, what they are — is a field, so the callbacks read
/// <c>self</c> directly. On the audio thread that is the difference between a pointer dereference and
/// a handle resolution, per parameter, per block.
/// </para>
/// </remarks>
internal sealed unsafe class Vst3ParameterChanges : IDisposable
{
    /// <summary>What a plugin casts the host's change list to.</summary>
    public static readonly Guid IParameterChangesIid = new("a4779663-0bb6-4a56-b443-84a8466feb9d");

    /// <summary>One parameter's points within a block.</summary>
    public static readonly Guid IParamValueQueueIid = new("01263a18-ed07-4f6f-98c9-d3564686f9ba");

    private static readonly Guid FUnknownIid = new("00000000-0000-0000-c000-000000000046");

    /// <summary>The host's list as the plugin sees it: a vtable and the queues behind it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeChanges
    {
        public void** Vtable;
        public int Count;
        public int Capacity;
        public NativeQueue* Queues;
    }

    /// <summary>
    /// One parameter's queue. Exactly one point, at sample offset zero — the block granularity the
    /// coalescing above settles on.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeQueue
    {
        public void** Vtable;
        public uint Id;
        public int PointCount;
        public double Value;
    }

    private static void** _changesVtable;
    private static void** _queueVtable;
    private static readonly object Gate = new();

    private readonly uint[] _ids;
    private readonly double[] _values;
    private readonly int[] _dirty;
    private int _anyDirty;

    private NativeChanges* _changes;
    private NativeQueue* _queues;
    private bool _disposed;

    public Vst3ParameterChanges(IReadOnlyList<uint> parameterIds)
    {
        ArgumentNullException.ThrowIfNull(parameterIds);
        _ids = [.. parameterIds];
        _values = new double[_ids.Length];
        _dirty = new int[_ids.Length];

        // One allocation for the queues and one for the list. Both live as long as the plugin does,
        // so nothing here is ever allocated or freed while audio is running.
        int count = Math.Max(1, _ids.Length);
        _queues = (NativeQueue*)NativeMemory.AllocZeroed((nuint)(count * sizeof(NativeQueue)));
        for (int i = 0; i < _ids.Length; i++)
        {
            _queues[i].Vtable = QueueVtable;
            _queues[i].Id = _ids[i];
            _queues[i].PointCount = 1;
            _queues[i].Value = 0;
        }

        _changes = (NativeChanges*)NativeMemory.AllocZeroed((nuint)sizeof(NativeChanges));
        _changes->Vtable = ChangesVtable;
        _changes->Count = 0;
        _changes->Capacity = _ids.Length;
        _changes->Queues = _queues;
    }

    /// <summary>How many parameters this list can carry.</summary>
    public int Capacity => _ids.Length;

    /// <summary>
    /// Records a new value for one parameter. Safe from any thread, and never blocks the audio one.
    /// </summary>
    /// <remarks>
    /// The value is written before the parameter is flagged, and the parameter before the summary
    /// flag, so a reader that sees a flag always sees the value behind it. A change that lands
    /// between the consumer clearing the summary flag and its scan is picked up by that same scan or,
    /// failing that, by the next block — one buffer late at worst, never lost.
    /// </remarks>
    public void Set(int index, double normalized)
    {
        if (_disposed || (uint)index >= (uint)_ids.Length) return;

        Volatile.Write(ref _values[index], Math.Clamp(normalized, 0, 1));
        Volatile.Write(ref _dirty[index], 1);
        Volatile.Write(ref _anyDirty, 1);
    }

    /// <summary>Index of a parameter id, or −1. Linear, and called from the UI thread only.</summary>
    public int IndexOf(uint id) => Array.IndexOf(_ids, id);

    /// <summary>
    /// Folds everything pending into the native list and returns it, or null when nothing changed.
    /// </summary>
    /// <remarks>
    /// Returning null rather than an empty list is deliberate: a plugin given no change list at all
    /// takes the cheapest path it has, and the common case by a wide margin is a block during which
    /// the user touched nothing.
    /// </remarks>
    public nint Prepare()
    {
        if (_disposed || _changes == null) return 0;
        if (Interlocked.Exchange(ref _anyDirty, 0) == 0) return 0;

        int used = 0;
        for (int i = 0; i < _ids.Length; i++)
        {
            if (Interlocked.Exchange(ref _dirty[i], 0) == 0) continue;
            _queues[used].Id = _ids[i];
            _queues[used].PointCount = 1;
            _queues[used].Value = Volatile.Read(ref _values[i]);
            used++;
        }

        if (used == 0) return 0;
        _changes->Count = used;
        return (nint)_changes;
    }

    // ── the change list ──────────────────────────────────────────

    private static void** ChangesVtable
    {
        get
        {
            if (_changesVtable != null) return _changesVtable;
            lock (Gate)
            {
                if (_changesVtable != null) return _changesVtable;
                void** table = (void**)NativeMemory.Alloc((nuint)(6 * sizeof(void*)));
                table[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&ChangesQueryInterface;
                table[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                table[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                table[3] = (delegate* unmanaged[Stdcall]<void*, int>)&GetParameterCount;
                table[4] = (delegate* unmanaged[Stdcall]<void*, int, void*>)&GetParameterData;
                table[5] = (delegate* unmanaged[Stdcall]<void*, uint*, int*, void*>)&AddParameterData;
                _changesVtable = table;
                return _changesVtable;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ChangesQueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;
        if (*iid == FUnknownIid || *iid == IParameterChangesIid)
        {
            *result = self;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    // The host owns these for the length of one process call and frees them itself. A plugin must
    // never be able to release a list out from under the audio thread.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetParameterCount(void* self) =>
        self == null ? 0 : ((NativeChanges*)self)->Count;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* GetParameterData(void* self, int index)
    {
        if (self == null) return null;
        var changes = (NativeChanges*)self;
        if ((uint)index >= (uint)changes->Count) return null;
        return &changes->Queues[index];
    }

    // Only meaningful on the *output* change list, which a plugin writes and this host does not ask
    // for. Refusing is the honest answer; inventing a queue would promise to carry values nobody
    // reads.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* AddParameterData(void* self, uint* id, int* index)
    {
        if (index != null) *index = 0;
        return null;
    }

    // ── one parameter's queue ────────────────────────────────────

    private static void** QueueVtable
    {
        get
        {
            if (_queueVtable != null) return _queueVtable;
            lock (Gate)
            {
                if (_queueVtable != null) return _queueVtable;
                void** table = (void**)NativeMemory.Alloc((nuint)(7 * sizeof(void*)));
                table[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueueQueryInterface;
                table[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                table[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                table[3] = (delegate* unmanaged[Stdcall]<void*, uint>)&GetParameterId;
                table[4] = (delegate* unmanaged[Stdcall]<void*, int>)&GetPointCount;
                table[5] = (delegate* unmanaged[Stdcall]<void*, int, int*, double*, int>)&GetPoint;
                table[6] = (delegate* unmanaged[Stdcall]<void*, int, double, int*, int>)&AddPoint;
                _queueVtable = table;
                return _queueVtable;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueueQueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;
        if (*iid == FUnknownIid || *iid == IParamValueQueueIid)
        {
            *result = self;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint GetParameterId(void* self) => self == null ? 0 : ((NativeQueue*)self)->Id;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPointCount(void* self) => self == null ? 0 : ((NativeQueue*)self)->PointCount;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPoint(void* self, int index, int* sampleOffset, double* value)
    {
        if (self == null || sampleOffset == null || value == null) return Vst3Abi.InvalidArgument;
        var queue = (NativeQueue*)self;
        if ((uint)index >= (uint)queue->PointCount) return Vst3Abi.InvalidArgument;

        *sampleOffset = 0;
        *value = queue->Value;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AddPoint(void* self, int sampleOffset, double value, int* index)
    {
        if (index != null) *index = 0;
        return Vst3Abi.NotImplemented;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The list points at the queues, so it goes first: a plugin still holding the list would
        // otherwise be one dereference away from freed memory.
        if (_changes != null) { NativeMemory.Free(_changes); _changes = null; }
        if (_queues != null) { NativeMemory.Free(_queues); _queues = null; }
        GC.SuppressFinalize(this);
    }
}
