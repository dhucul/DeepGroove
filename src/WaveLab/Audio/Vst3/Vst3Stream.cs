using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>
/// An <c>IBStream</c> over a managed buffer: what a plugin reads its state from and writes it to.
/// </summary>
/// <remarks>
/// <para>
/// Needed for two things that look unrelated and are not. Saving a plugin's settings with a chain
/// preset is <c>component-&gt;getState(stream)</c> and nothing else — a VST3 plugin's state is an
/// opaque run of bytes and there is no other way to ask for it. And the handshake that hands a
/// freshly created controller the component's current state, without which some plugins never
/// populate their parameter list, is <c>getState</c> into a stream and <c>setComponentState</c> out
/// of it.
/// </para>
/// <para>
/// This is a managed object exposed to native code, so unlike <see cref="Vst3HostContext"/> it has
/// per-instance state and needs a way back. The native object is two pointers wide — a vtable and a
/// <see cref="GCHandle"/> — and every callback recovers the managed stream from the second.
/// </para>
/// </remarks>
internal sealed unsafe class Vst3MemoryStream : IDisposable
{
    /// <summary>What a plugin asks for when it wants somewhere to put its state.</summary>
    public static readonly Guid IBStreamIid = new("c3bf6ea2-3099-4752-9b6b-f9901ee33e9b");

    private static readonly Guid FUnknownIid = new("00000000-0000-0000-c000-000000000046");

    private const int SeekSet = 0;
    private const int SeekCurrent = 1;
    private const int SeekEnd = 2;

    private static void** _vtable;
    private static readonly object Gate = new();

    private readonly GCHandle _handle;
    private void** _native;
    private byte[] _buffer;
    private int _length;
    private int _position;
    private bool _disposed;

    public Vst3MemoryStream(byte[]? initial = null)
    {
        _buffer = initial is { Length: > 0 } ? (byte[])initial.Clone() : new byte[4096];
        _length = initial?.Length ?? 0;
        _handle = GCHandle.Alloc(this, GCHandleType.Normal);

        _native = (void**)NativeMemory.Alloc((nuint)(2 * sizeof(void*)));
        _native[0] = Vtable;
        _native[1] = (void*)GCHandle.ToIntPtr(_handle);
    }

    /// <summary>The pointer a plugin is handed.</summary>
    public void* Pointer => _native;

    /// <summary>What has been written, trimmed to the length actually used.</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public void Rewind() => _position = 0;

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
                table[3] = (delegate* unmanaged[Stdcall]<void*, void*, int, int*, int>)&Read;
                table[4] = (delegate* unmanaged[Stdcall]<void*, void*, int, int*, int>)&Write;
                table[5] = (delegate* unmanaged[Stdcall]<void*, long, int, long*, int>)&Seek;
                table[6] = (delegate* unmanaged[Stdcall]<void*, long*, int>)&Tell;
                _vtable = table;
                return _vtable;
            }
        }
    }

    private static Vst3MemoryStream? From(void* self)
    {
        if (self == null) return null;
        nint handle = (nint)((void**)self)[1];
        if (handle == 0) return null;

        var target = GCHandle.FromIntPtr(handle).Target;
        return target as Vst3MemoryStream;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;

        if (*iid == FUnknownIid || *iid == IBStreamIid)
        {
            *result = self;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    // The host owns this object for the length of one call and disposes it itself, so the count is
    // a formality — a plugin must never be able to free a stream out from under the caller.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Read(void* self, void* buffer, int numBytes, int* numBytesRead)
    {
        if (numBytesRead != null) *numBytesRead = 0;
        if (From(self) is not { } stream || buffer == null || numBytes < 0)
            return Vst3Abi.InvalidArgument;

        int available = Math.Max(0, stream._length - stream._position);
        int take = Math.Min(available, numBytes);
        if (take > 0)
        {
            fixed (byte* source = &stream._buffer[stream._position])
                Buffer.MemoryCopy(source, buffer, numBytes, take);
            stream._position += take;
        }

        if (numBytesRead != null) *numBytesRead = take;

        // Reading nothing at the end is success with a count of zero, not a failure: a plugin
        // reading its state until exhaustion would otherwise treat the end as an error.
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Write(void* self, void* buffer, int numBytes, int* numBytesWritten)
    {
        if (numBytesWritten != null) *numBytesWritten = 0;
        if (From(self) is not { } stream || buffer == null || numBytes < 0)
            return Vst3Abi.InvalidArgument;
        if (numBytes == 0) return Vst3Abi.ResultOk;

        stream.EnsureCapacity(stream._position + numBytes);
        fixed (byte* destination = &stream._buffer[stream._position])
            Buffer.MemoryCopy(buffer, destination, stream._buffer.Length - stream._position, numBytes);

        stream._position += numBytes;
        stream._length = Math.Max(stream._length, stream._position);
        if (numBytesWritten != null) *numBytesWritten = numBytes;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Seek(void* self, long position, int mode, long* result)
    {
        if (From(self) is not { } stream) return Vst3Abi.InvalidArgument;

        long target = mode switch
        {
            SeekSet => position,
            SeekCurrent => stream._position + position,
            SeekEnd => stream._length + position,
            _ => -1,
        };
        if (target < 0 || target > int.MaxValue) return Vst3Abi.InvalidArgument;

        stream._position = (int)target;
        if (result != null) *result = target;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Tell(void* self, long* position)
    {
        if (From(self) is not { } stream || position == null) return Vst3Abi.InvalidArgument;
        *position = stream._position;
        return Vst3Abi.ResultOk;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        int size = Math.Max(_buffer.Length * 2, needed);
        Array.Resize(ref _buffer, size);
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
