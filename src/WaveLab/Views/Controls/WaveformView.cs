using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>
/// The main waveform editor surface. The expensive per-pixel peak geometry is cached and
/// only rebuilt when the view actually changes (scroll/zoom/edit) — playhead, cursor,
/// selection and markers are cheap overlays, so playback and dragging stay fluid.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDocumentChanged));

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private bool _dragging;
    private int _dragAnchor;

    // geometry cache — rebuilt only when this key changes
    private readonly record struct CacheKey(double ViewStart, double Spp, double W, double H, double AmpZoom,
        int PeaksVersion, int Channels, object? Doc);
    private CacheKey _cacheKey;
    private StreamGeometry[] _peakGeos = [];
    private StreamGeometry[] _rmsGeos = [];

    public WaveformView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
        SizeChanged += (_, _) => { if (Document != null) Document.ViewWidthPixels = ActualWidth; };
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)d;
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= view.OnVmChanged;
        if (e.NewValue is DocumentViewModel vm)
        {
            vm.PropertyChanged += view.OnVmChanged;
            vm.ViewWidthPixels = view.ActualWidth;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ViewStart) or nameof(DocumentViewModel.SamplesPerPixel)
            or nameof(DocumentViewModel.SelStart) or nameof(DocumentViewModel.SelEnd)
            or nameof(DocumentViewModel.Cursor) or nameof(DocumentViewModel.PlayheadSample)
            or nameof(DocumentViewModel.PeaksVersion) or nameof(DocumentViewModel.MarkersVersion)
            or nameof(DocumentViewModel.AmpZoom))
            Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.WaveBg, null, new Rect(0, 0, w, h));
        var vm = Document;
        if (vm == null || vm.Doc.Length == 0 || w < 2) return;

        int channels = vm.Doc.ChannelCount;
        double chH = h / channels;
        double spp = vm.SamplesPerPixel;
        double viewStart = vm.ViewStart;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        EnsureGeometryCache(vm, w, h, channels, chH, spp, viewStart);

        for (int c = 0; c < channels; c++)
        {
            double mid = c * chH + chH / 2;
            double amp = chH * 0.46 * vm.AmpZoom;

            dc.PushClip(new RectangleGeometry(new Rect(0, c * chH, w, chH)));

            foreach (double frac in (double[])[0.25, 0.5, 0.75])
            {
                dc.DrawLine(WaveTheme.GridLine, new Point(0, mid - amp * frac), new Point(w, mid - amp * frac));
                dc.DrawLine(WaveTheme.GridLine, new Point(0, mid + amp * frac), new Point(w, mid + amp * frac));
            }

            if (c < _peakGeos.Length)
            {
                dc.DrawGeometry(WaveTheme.WavePeak, null, _peakGeos[c]);
                dc.DrawGeometry(WaveTheme.WaveRms, null, _rmsGeos[c]);
            }

            dc.DrawLine(WaveTheme.CenterLine, new Point(0, mid), new Point(w, mid));

            var label = WaveTheme.Text(channels == 1 ? "M" : c == 0 ? "L" : c == 1 ? "R" : $"C{c + 1}",
                WaveTheme.UiFace, 10, WaveTheme.TextMuted, dpi);
            dc.DrawText(label, new Point(8, c * chH + 6));

            dc.Pop();

            if (c > 0)
                dc.DrawLine(WaveTheme.ChannelDivider, new Point(0, c * chH), new Point(w, c * chH));
        }

        // selection overlay (tint + edges — no geometry rebuild needed while dragging)
        if (vm.HasSelection)
        {
            double selX0 = (vm.SelStart - viewStart) / spp;
            double selX1 = (vm.SelEnd - viewStart) / spp;
            if (selX1 > 0 && selX0 < w)
                dc.DrawRectangle(WaveTheme.SelectionOverlay, null,
                    new Rect(Math.Max(0, selX0), 0, Math.Min(w, selX1) - Math.Max(0, selX0), h));
            if (selX0 >= 0 && selX0 <= w) dc.DrawLine(WaveTheme.SelectionEdge, new Point(selX0, 0), new Point(selX0, h));
            if (selX1 >= 0 && selX1 <= w) dc.DrawLine(WaveTheme.SelectionEdge, new Point(selX1, 0), new Point(selX1, h));
        }

        // marker lines
        foreach (var marker in vm.Markers)
        {
            double mx = (marker.Position - viewStart) / spp;
            if (mx >= 0 && mx <= w)
                dc.DrawLine(WaveTheme.MarkerLine, new Point(mx, 0), new Point(mx, h));
        }

        // cursor
        double curX = (vm.Cursor - viewStart) / spp;
        if (curX >= 0 && curX <= w && !vm.HasSelection)
            dc.DrawLine(WaveTheme.CursorPen, new Point(curX, 0), new Point(curX, h));

        // playhead
        double phX = (vm.PlayheadSample - viewStart) / spp;
        if (phX >= 0 && phX <= w)
        {
            dc.DrawLine(WaveTheme.Playhead, new Point(phX, 0), new Point(phX, h));
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(phX - 5, 0), true, true);
                g.LineTo(new Point(phX + 5, 0), false, false);
                g.LineTo(new Point(phX, 7), false, false);
            }
            tri.Freeze();
            dc.DrawGeometry(WaveTheme.Playhead.Brush, null, tri);
        }
    }

    private void EnsureGeometryCache(DocumentViewModel vm, double w, double h, int channels, double chH,
        double spp, double viewStart)
    {
        var key = new CacheKey(viewStart, spp, w, h, vm.AmpZoom, vm.PeaksVersion, channels, vm.Doc);
        if (key == _cacheKey && _peakGeos.Length == channels) return;
        _cacheKey = key;

        _peakGeos = new StreamGeometry[channels];
        _rmsGeos = new StreamGeometry[channels];
        int width = (int)w;
        var peakTop = new Point[width];
        var peakBot = new Point[width];
        var rmsTop = new Point[width];
        var rmsBot = new Point[width];

        for (int c = 0; c < channels; c++)
        {
            double mid = c * chH + chH / 2;
            double amp = chH * 0.46 * vm.AmpZoom;

            for (int x = 0; x < width; x++)
            {
                int s0 = (int)(viewStart + x * spp);
                int s1 = Math.Max(s0 + 1, (int)(viewStart + (x + 1) * spp));
                vm.Peaks.Query(c, s0, s1, out float mn, out float mx, out float rms);
                peakTop[x] = new Point(x, mid - Math.Clamp(mx, -1, 1) * amp);
                peakBot[x] = new Point(x, mid - Math.Clamp(mn, -1, 1) * amp);
                float r = Math.Min(rms, Math.Max(Math.Abs(mn), Math.Abs(mx)));
                rmsTop[x] = new Point(x, mid - r * amp);
                rmsBot[x] = new Point(x, mid + r * amp);
            }

            _peakGeos[c] = BuildBand(peakTop, peakBot);
            _rmsGeos[c] = BuildBand(rmsTop, rmsBot);
        }
    }

    private static StreamGeometry BuildBand(Point[] top, Point[] bot)
    {
        var geo = new StreamGeometry();
        int n = top.Length;
        if (n > 1)
        {
            using var g = geo.Open();
            g.BeginFigure(top[0], true, true);
            for (int x = 1; x < n; x++) g.LineTo(top[x], false, false);
            for (int x = n - 1; x >= 0; x--) g.LineTo(bot[x], false, false);
        }
        geo.Freeze();
        return geo;
    }

    // ── interaction ──────────────────────────────────────────────

    private int SampleAt(Point p)
    {
        var vm = Document!;
        return (int)Math.Clamp(vm.ViewStart + p.X * vm.SamplesPerPixel, 0, Math.Max(0, vm.Doc.Length - 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Document == null) return;
        Focus();
        if (e.ClickCount == 2)
        {
            Document.SelectAll();
            e.Handled = true;
            return;
        }
        CaptureMouse();
        _dragging = true;
        _dragAnchor = SampleAt(e.GetPosition(this));
        Document.SetCursor(_dragAnchor, clearSelection: true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || Document == null) return;
        int s = SampleAt(e.GetPosition(this));
        if (Math.Abs(s - _dragAnchor) > (int)Document.SamplesPerPixel)
            Document.SetSelection(Math.Min(_dragAnchor, s), Math.Max(_dragAnchor, s));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var vm = Document;
        if (vm == null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            vm.AmpZoom *= e.Delta > 0 ? 1.25 : 1 / 1.25;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            vm.ScrollBy(-e.Delta * vm.SamplesPerPixel * 0.5);
        }
        else
        {
            double factor = e.Delta > 0 ? 1 / 1.3 : 1.3;
            vm.ZoomAt(e.GetPosition(this).X, factor);
        }
        e.Handled = true;
    }
}
