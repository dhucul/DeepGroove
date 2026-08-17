using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WaveLab.Audio.Vst3;

/// <summary>A plugin editor's rectangle, in the plugin's own pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ViewRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public override readonly string ToString() => $"{Right - Left}×{Bottom - Top}";
}

/// <summary>
/// A plugin's own editor window, before it has been put anywhere.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of VST3 hosting that actually matters for the plugins installed on this machine:
/// they publish no parameters at all, so their editor is not a convenience over a generated control
/// panel — it is the <b>only</b> way to operate them.
/// </para>
/// <para>
/// The sequence a plugin expects is exact and unforgiving. Ask whether it supports <c>HWND</c>; give
/// it a frame to call back on; hand it a window to live in; and on the way out, <c>removed</c>
/// before <c>release</c>. A view released while still attached leaves a plugin drawing into a window
/// that has gone.
/// </para>
/// </remarks>
public sealed unsafe class Vst3PlugView : IDisposable
{
    /// <summary>What a Windows host offers a plugin to attach to.</summary>
    private static readonly byte[] PlatformHwnd = "HWND\0"u8.ToArray();

    /// <summary>The editor a controller offers by default.</summary>
    private static readonly byte[] ViewTypeEditor = "editor\0"u8.ToArray();

    public static readonly Guid IPlugViewIid = new("5bc32507-d060-49ea-a615-1b522b755b29");

    private void* _view;
    private Vst3PlugFrame? _frame;
    private bool _attached;
    private bool _disposed;

    private Vst3PlugView(void* view) => _view = view;

    /// <summary>Raised when the plugin asks to be a different size.</summary>
    public event Action<ViewRect>? ResizeRequested;

    /// <summary>Asks a controller for its editor. Null when the plugin has none.</summary>
    internal static Vst3PlugView? Create(void* controller)
    {
        if (controller == null) return null;

        void** vtable = *(void***)controller;
        var createView = (delegate* unmanaged[Stdcall]<void*, byte*, void*>)vtable[17];

        void* view;
        fixed (byte* type = ViewTypeEditor) view = createView(controller, type);
        if (view == null) return null;

        var wrapped = new Vst3PlugView(view);
        if (wrapped.SupportsHwnd) return wrapped;

        // A view that cannot live in an HWND is of no use on Windows, and holding it would leave the
        // plugin believing its editor is open.
        wrapped.Dispose();
        return null;
    }

    /// <summary>Whether the plugin will attach to a Windows window handle.</summary>
    public bool SupportsHwnd
    {
        get
        {
            if (_view == null) return false;
            void** vtable = *(void***)_view;
            var isPlatformTypeSupported = (delegate* unmanaged[Stdcall]<void*, byte*, int>)vtable[3];

            fixed (byte* type = PlatformHwnd)
                return Vst3Abi.Ok(isPlatformTypeSupported(_view, type));
        }
    }

    /// <summary>The size the plugin wants, or a usable default when it will not say.</summary>
    public ViewRect PreferredSize
    {
        get
        {
            var rect = new ViewRect { Right = 720, Bottom = 480 };
            if (_view == null) return rect;

            void** vtable = *(void***)_view;
            var getSize = (delegate* unmanaged[Stdcall]<void*, ViewRect*, int>)vtable[9];

            ViewRect asked;
            if (!Vst3Abi.Ok(getSize(_view, &asked))) return rect;

            // A plugin that reports nothing is not asking for a zero-sized window; it has simply
            // not decided yet, and a window of no size cannot be given back to it.
            return asked.Width > 0 && asked.Height > 0 ? asked : rect;
        }
    }

    /// <summary>Whether the plugin will accept being resized by the user.</summary>
    public bool CanResize
    {
        get
        {
            if (_view == null) return false;
            void** vtable = *(void***)_view;
            return Vst3Abi.Ok(((delegate* unmanaged[Stdcall]<void*, int>)vtable[13])(_view));
        }
    }

    /// <summary>
    /// Puts the editor inside a window. The frame goes in first, because a plugin may ask to be
    /// resized during <c>attached</c> and would otherwise have nowhere to ask.
    /// </summary>
    public bool Attach(nint parent)
    {
        if (_disposed || _view == null || parent == 0 || _attached) return false;

        _frame ??= new Vst3PlugFrame(rect => ResizeRequested?.Invoke(rect));

        void** vtable = *(void***)_view;
        var setFrame = (delegate* unmanaged[Stdcall]<void*, void*, int>)vtable[12];
        setFrame(_view, _frame.Pointer);

        var attached = (delegate* unmanaged[Stdcall]<void*, void*, byte*, int>)vtable[4];
        fixed (byte* type = PlatformHwnd)
        {
            if (!Vst3Abi.Ok(attached(_view, (void*)parent, type))) return false;
        }
        _attached = true;
        return true;
    }

    /// <summary>Tells the plugin the window it lives in has changed size.</summary>
    public bool Resize(int width, int height)
    {
        if (_disposed || _view == null || !_attached || width <= 0 || height <= 0) return false;

        void** vtable = *(void***)_view;
        var onSize = (delegate* unmanaged[Stdcall]<void*, ViewRect*, int>)vtable[10];

        var rect = new ViewRect { Left = 0, Top = 0, Right = width, Bottom = height };
        return Vst3Abi.Ok(onSize(_view, &rect));
    }

    /// <summary>Asks the plugin what size it would settle for, given one the host can offer.</summary>
    public ViewRect ConstrainSize(int width, int height)
    {
        var rect = new ViewRect { Left = 0, Top = 0, Right = width, Bottom = height };
        if (_disposed || _view == null) return rect;

        void** vtable = *(void***)_view;
        var checkSizeConstraint = (delegate* unmanaged[Stdcall]<void*, ViewRect*, int>)vtable[14];

        // The plugin rewrites the rectangle in place when it wants a different one; a plugin that
        // does not implement this leaves it as it was, which is the answer we already had.
        checkSizeConstraint(_view, &rect);
        return rect.Width > 0 && rect.Height > 0 ? rect : new ViewRect { Right = width, Bottom = height };
    }

    public void Detach()
    {
        if (!_attached || _view == null) return;
        _attached = false;

        void** vtable = *(void***)_view;
        try { ((delegate* unmanaged[Stdcall]<void*, int>)vtable[5])(_view); } catch { }

        // The frame is cleared after removal, not before: a plugin tidying up during `removed` may
        // still call back, and a null frame at that moment is a null dereference inside the plugin.
        try { ((delegate* unmanaged[Stdcall]<void*, void*, int>)vtable[12])(_view, null); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Detach();
        if (_view != null)
        {
            try { Vst3Module.Release(_view); } catch { }
            _view = null;
        }
        _frame?.Dispose();
        _frame = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The host object a plugin calls when its editor wants to change size.
/// </summary>
/// <remarks>
/// A managed object exposed to native code, like <see cref="Vst3MemoryStream"/>: a vtable of
/// <see cref="UnmanagedCallersOnlyAttribute"/> statics and a two-pointer object carrying a
/// <see cref="GCHandle"/> so the callback can find its way back. Plugins that resize themselves —
/// anything with a collapsible panel — do not work without one.
/// </remarks>
internal sealed unsafe class Vst3PlugFrame : IDisposable
{
    public static readonly Guid IPlugFrameIid = new("367faf01-afa9-4693-8d4d-a2a0ed0882a3");
    private static readonly Guid FUnknownIid = new("00000000-0000-0000-c000-000000000046");

    private static void** _vtable;
    private static readonly object Gate = new();

    private readonly GCHandle _handle;
    private readonly Action<ViewRect> _onResize;
    private void** _native;
    private bool _disposed;

    public Vst3PlugFrame(Action<ViewRect> onResize)
    {
        _onResize = onResize;
        _handle = GCHandle.Alloc(this, GCHandleType.Normal);

        _native = (void**)NativeMemory.Alloc((nuint)(2 * sizeof(void*)));
        _native[0] = Vtable;
        _native[1] = (void*)GCHandle.ToIntPtr(_handle);
    }

    public void* Pointer => _native;

    private static void** Vtable
    {
        get
        {
            if (_vtable != null) return _vtable;
            lock (Gate)
            {
                if (_vtable != null) return _vtable;
                void** table = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
                table[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
                table[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
                table[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
                table[3] = (delegate* unmanaged[Stdcall]<void*, void*, ViewRect*, int>)&ResizeView;
                _vtable = table;
                return _vtable;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (result == null || iid == null) return Vst3Abi.InvalidArgument;
        *result = null;

        if (*iid == FUnknownIid || *iid == IPlugFrameIid)
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ResizeView(void* self, void* view, ViewRect* rect)
    {
        if (self == null || rect == null) return Vst3Abi.InvalidArgument;

        nint handle = (nint)((void**)self)[1];
        if (handle == 0) return Vst3Abi.InvalidArgument;
        if (GCHandle.FromIntPtr(handle).Target is not Vst3PlugFrame frame) return Vst3Abi.InvalidArgument;

        try
        {
            // This arrives on whatever thread the plugin's editor runs on, which on Windows is the
            // UI thread it was attached from — so the handler resizes the window directly. It must
            // not throw: an exception crossing back into native code unwinds through a C++ frame
            // that has no idea what a managed exception is.
            frame._onResize(*rect);
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
