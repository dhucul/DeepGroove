using System.Windows;
using System.Windows.Input;
using WaveLab.Audio.Vst3;
using WaveLab.Views.Controls;

namespace WaveLab.Views;

/// <summary>
/// A window holding one plugin's own editor.
/// </summary>
/// <remarks>
/// <para>
/// The window sizes itself to whatever the plugin asks for and follows it if the plugin asks again —
/// a plugin with a collapsible panel changes size while it is open, and a host that ignores that
/// leaves either a band of dead space or a clipped editor.
/// </para>
/// <para>
/// <b>A plugin without an editor is not an error</b>, and this says so rather than showing an empty
/// frame: some plugins publish parameters and no editor, and a few — as it turns out — publish
/// neither, and can only be operated from the software they shipped with.
/// </para>
/// </remarks>
public partial class Vst3EditorWindow : Window
{
    private readonly Vst3Plugin _plugin;
    private Vst3PlugView? _view;
    private Vst3EditorHost? _host;

    public Vst3EditorWindow(Vst3Plugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        InitializeComponent();
        _plugin = plugin;

        pluginName.Text = plugin.Class.Name;
        pluginVendor.Text = plugin.Class.Vendor;
        latencyText.Text = plugin.LatencySamples > 0
            ? $"latency {plugin.LatencySamples} smp"
            : "no latency";

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Created here rather than in the constructor: the editor is a native window and wants a
        // parent that already exists.
        _view = _plugin.CreateEditor();
        if (_view == null)
        {
            ShowFallback(
                "This plugin has no editor of its own.",
                _plugin.Parameters.Count > 0
                    ? "It publishes parameters, so it can be operated from the rack's own controls."
                    : "It publishes neither an editor nor any parameters through VST3, so there is "
                      + "nothing for a host to show. Plugins built to run only inside the software "
                      + "they shipped with sometimes behave this way; the audio still processes.");
            return;
        }

        _host = new Vst3EditorHost(_view);
        _host.PluginResized += OnPluginResized;
        editorFrame.Child = _host;

        ViewRect size = _view.PreferredSize;
        _host.Width = size.Width;
        _host.Height = size.Height;

        // Whether the plugin actually took the window is only known once the host has built one, so
        // it is checked after the visual tree has had a chance to.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_host is { Attached: false })
            {
                editorFrame.Child = null;
                ShowFallback("The plugin would not open its editor here.",
                    "It offers one, but refused the window it was given.");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnPluginResized(int width, int height)
    {
        editorFrame.Width = width;
        editorFrame.Height = height;
        SizeToContent = SizeToContent.WidthAndHeight;
    }

    private void ShowFallback(string title, string detail)
    {
        editorFrame.MinWidth = 380;
        editorFrame.MinHeight = 0;
        editorFrame.Background = null;
        fallbackTitle.Text = title;
        fallbackText.Text = detail;
        fallbackPanel.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // The host is disposed first: it detaches the view from its window, and releasing a view
        // that is still attached leaves the plugin drawing into a window that has gone.
        editorFrame.Child = null;
        _host?.Dispose();
        _host = null;

        _view?.Dispose();
        _view = null;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
