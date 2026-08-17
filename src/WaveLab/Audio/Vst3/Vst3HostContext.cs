using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>
/// The host object a plugin is handed when it is initialised: an <c>IHostApplication</c>.
/// </summary>
/// <remarks>
/// <para>
/// Passing null here is legal and every plugin tested still loads, processes audio and reports its
/// buses with one — but several build their <b>parameter list</b> only once they can ask the host who
/// it is, so with a null context they come up with no parameters at all and look broken. Providing
/// one is what makes a plugin's controls visible.
/// </para>
/// <para>
/// This is a managed object exposed <em>to</em> native code, which is the opposite direction from
/// everything else here: a vtable of function pointers into <see cref="UnmanagedCallersOnlyAttribute"/>
/// statics, and an object whose first and only field is a pointer to it. There is exactly one, it
/// lives for the life of the process, and its reference count is therefore a formality — a plugin
/// that releases it more times than it retains it must not be able to free the host.
/// </para>
/// </remarks>
internal static unsafe class Vst3HostContext
{
    /// <summary>IUnknown's own identifier, which <c>FUnknown</c> shares on Windows.</summary>
    private static readonly Guid FUnknownIid = new("00000000-0000-0000-c000-000000000046");

    /// <summary>What a plugin asks for when it wants to know who is hosting it.</summary>
    private static readonly Guid IHostApplicationIid = new("58e595cc-db2d-4969-8b6a-af8c36a664e5");

    private static void* _instance;
    private static void** _vtable;
    private static readonly object Gate = new();

    /// <summary>The host object, created once. Never freed: plugins may hold it past shutdown.</summary>
    public static void* Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (Gate)
            {
                if (_instance != null) return _instance;

                _vtable = (void**)NativeMemory.Alloc((nuint)(5 * sizeof(void*)));
                _vtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
                _vtable[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                _vtable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                _vtable[3] = (delegate* unmanaged[Stdcall]<void*, char*, int>)&GetName;
                _vtable[4] = (delegate* unmanaged[Stdcall]<void*, byte*, byte*, void**, int>)&CreateInstance;

                // The object is one pointer wide: a vtable pointer and nothing else, because there
                // is no per-instance state to carry.
                void** self = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
                self[0] = _vtable;
                _instance = self;
                return _instance;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null) return Vst3Abi.InvalidArgument;
        *result = null;
        if (iid == null) return Vst3Abi.InvalidArgument;

        if (*iid == FUnknownIid || *iid == IHostApplicationIid)
        {
            *result = self;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    // The host outlives every plugin by construction, so these are honest about doing nothing rather
    // than keeping a count that could reach zero and free the object out from under one.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetName(void* self, char* name)
    {
        if (name == null) return Vst3Abi.InvalidArgument;

        // String128: 128 UTF-16 units including the terminator.
        const string host = "Deep Groove";
        for (int i = 0; i < host.Length; i++) name[i] = host[i];
        name[host.Length] = '\0';
        return Vst3Abi.ResultOk;
    }

    /// <summary>
    /// Where a plugin asks the host to make it an <c>IMessage</c> or an <c>IAttributeList</c>.
    /// </summary>
    /// <remarks>
    /// Not implemented, deliberately. Those exist so a component and its controller can talk when
    /// they live in separate processes; this host runs them in one, and a plugin that cannot get a
    /// message object falls back to working without one. Answering <c>kNotImplemented</c> is the
    /// stated way to say so — returning success with a null object would be a promise broken later,
    /// inside the plugin, with no way to trace it back here.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateInstance(void* self, byte* cid, byte* iid, void** obj)
    {
        if (obj != null) *obj = null;
        return Vst3Abi.NotImplemented;
    }
}
