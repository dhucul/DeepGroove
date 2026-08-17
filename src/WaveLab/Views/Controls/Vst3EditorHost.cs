using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WaveLab.Audio.Vst3;

namespace WaveLab.Views.Controls;

/// <summary>
/// A plugin's own editor, living inside WPF.
/// </summary>
/// <remarks>
/// <para>
/// A VST3 editor is a native window that wants an <c>HWND</c> to be a child of. WPF draws its own
/// content into one window and has no handles to hand out, so <see cref="HwndHost"/> is the seam:
/// it asks for a child window, and whatever is put in it is composited as if it were an element.
/// </para>
/// <para>
/// A plain <c>STATIC</c> window is created rather than a registered class of this app's own. The
/// plugin creates and owns everything inside it; this window exists only to be a parent, and a
/// custom class would mean a window procedure with nothing to do and a class registration to
/// unregister on the way out.
/// </para>
/// <para>
/// <b>The order on the way out matters.</b> The view is detached before the window is destroyed —
/// a plugin whose window disappears underneath it is drawing into a handle that no longer exists,
/// and it will not find out until it faults.
/// </para>
/// </remarks>
public sealed class Vst3EditorHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;

    private readonly Vst3PlugView _view;
    private nint _child;
    private bool _attached;

    public Vst3EditorHost(Vst3PlugView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        _view.ResizeRequested += OnPluginResizeRequested;
    }

    /// <summary>Raised when the plugin asks its window to be a different size.</summary>
    public event Action<int, int>? PluginResized;

    /// <summary>Whether the plugin actually took the window it was given.</summary>
    public bool Attached => _attached;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        ViewRect size = _view.PreferredSize;

        _child = CreateWindowEx(
            0, "STATIC", "",
            WsChild | WsVisible | WsClipChildren,
            0, 0, Math.Max(1, size.Width), Math.Max(1, size.Height),
            hwndParent.Handle, 0, 0, 0);

        if (_child != 0) _attached = _view.Attach(_child);
        return new HandleRef(this, _child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Detach first, always. Destroying the window under a still-attached plugin leaves it
        // drawing into a handle that has gone.
        if (_attached)
        {
            _view.Detach();
            _attached = false;
        }

        if (_child != 0)
        {
            DestroyWindow(_child);
            _child = 0;
        }
    }

    /// <summary>Passes a new size on to the plugin, and moves the child window to match.</summary>
    public void Resize(int width, int height)
    {
        if (_child == 0 || width <= 0 || height <= 0) return;

        SetWindowPos(_child, 0, 0, 0, width, height, SwpNoZOrder | SwpNoActivate | SwpNoMove);
        _view.Resize(width, height);
    }

    private void OnPluginResizeRequested(ViewRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // The plugin's own thread is this one — it was attached from here — but going through the
        // dispatcher anyway costs nothing and means a plugin that resizes from elsewhere does not
        // touch a window from the wrong thread.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnPluginResizeRequested(rect)));
            return;
        }

        if (_child != 0)
            SetWindowPos(_child, 0, 0, 0, rect.Width, rect.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoMove);

        Width = rect.Width;
        Height = rect.Height;
        PluginResized?.Invoke(rect.Width, rect.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _view.ResizeRequested -= OnPluginResizeRequested;
        base.Dispose(disposing);
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
