using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>What a plugin reports back to the host while its editor is open.</summary>
/// <param name="Id">The parameter that moved.</param>
/// <param name="Normalized">Its new value, on the 0–1 scale.</param>
public readonly record struct Vst3ParameterEdit(uint Id, double Normalized);

/// <summary>
/// The host object a plugin is given so it can tell the host that something changed.
/// </summary>
/// <remarks>
/// <para>
/// This is not optional in any host that means it. When a user moves a control in a plugin's own
/// editor, <c>performEdit</c> is the only way the host hears about it — without one, a plugin's UI
/// and the host's idea of its state drift apart the moment the user touches anything. Some plugins
/// also refuse to create an editor at all until they have been given one, on the reasonable grounds
/// that an editor with nowhere to report is a UI that silently does nothing.
/// </para>
/// <para>
/// <c>restartComponent</c> is the plugin saying its own shape has changed — its latency, its
/// parameter list, its bus layout. A host that ignores it keeps compensating for a delay the plugin
/// no longer has.
/// </para>
/// </remarks>
internal sealed unsafe class Vst3ComponentHandler : IDisposable
{
    public static readonly Guid IComponentHandlerIid = new("93a0bea3-0bd0-45db-8e89-0b0cc1e46ac6");
    private static readonly Guid FUnknownIid = new("00000000-0000-0000-c000-000000000046");

    /// <summary>Restart flags worth acting on.</summary>
    public const int RestartParameterValuesChanged = 1 << 3;
    public const int RestartLatencyChanged = 1 << 5;
    public const int RestartParameterTitlesChanged = 1 << 1;

    private static void** _vtable;
    private static readonly object Gate = new();

    private readonly GCHandle _handle;
    private void** _native;
    private bool _disposed;

    public Vst3ComponentHandler()
    {
        _handle = GCHandle.Alloc(this, GCHandleType.Normal);
        _native = (void**)NativeMemory.Alloc((nuint)(2 * sizeof(void*)));
        _native[0] = Vtable;
        _native[1] = (void*)GCHandle.ToIntPtr(_handle);
    }

    public void* Pointer => _native;

    /// <summary>Raised when the user moves something in the plugin's own editor.</summary>
    public event Action<Vst3ParameterEdit>? ParameterEdited;

    /// <summary>Raised when the plugin says its latency, parameters or layout have changed.</summary>
    public event Action<int>? RestartRequested;

    private static void** Vtable
    {
        get
        {
            if (_vtable != null) return _vtable;
            lock (Gate)
            {
                if (_vtable != null) return _vtable;
                void** table = (void**)NativeMemory.Alloc((nuint)(7 * sizeof(void*)));
                table[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
                table[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                table[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                table[3] = (delegate* unmanaged[Stdcall]<void*, uint, int>)&BeginEdit;
                table[4] = (delegate* unmanaged[Stdcall]<void*, uint, double, int>)&PerformEdit;
                table[5] = (delegate* unmanaged[Stdcall]<void*, uint, int>)&EndEdit;
                table[6] = (delegate* unmanaged[Stdcall]<void*, int, int>)&RestartComponent;
                _vtable = table;
                return _vtable;
            }
        }
    }

    private static Vst3ComponentHandler? From(void* self)
    {
        if (self == null) return null;
        nint handle = (nint)((void**)self)[1];
        return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as Vst3ComponentHandler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;

        if (*iid == FUnknownIid || *iid == IComponentHandlerIid)
        {
            *result = self;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    // beginEdit and endEdit bracket a gesture — a knob grabbed and let go — so a host can record one
    // automation move rather than a hundred. Accepted and not acted on: there is no automation to
    // record yet, and answering anything but success would tell the plugin its edit was rejected.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int BeginEdit(void* self, uint id) => Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EndEdit(void* self, uint id) => Vst3Abi.ResultOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PerformEdit(void* self, uint id, double normalized)
    {
        if (From(self) is not { } handler) return Vst3Abi.InvalidArgument;
        try
        {
            // Must not throw: this returns into a C++ frame that has no idea what a managed
            // exception is, and unwinding through it corrupts the plugin's own stack.
            handler.ParameterEdited?.Invoke(new Vst3ParameterEdit(id, Math.Clamp(normalized, 0, 1)));
            return Vst3Abi.ResultOk;
        }
        catch { return Vst3Abi.ResultFalse; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RestartComponent(void* self, int flags)
    {
        if (From(self) is not { } handler) return Vst3Abi.InvalidArgument;
        try
        {
            handler.RestartRequested?.Invoke(flags);
            return Vst3Abi.ResultOk;
        }
        catch { return Vst3Abi.ResultFalse; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_native != null) { NativeMemory.Free(_native); _native = null; }
        if (_handle.IsAllocated) _handle.Free();
        GC.SuppressFinalize(this);
    }
}
