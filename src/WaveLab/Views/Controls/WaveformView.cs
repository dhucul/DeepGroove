using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>What a press on the waveform takes hold of.</summary>
public enum WaveformGrab
{
    /// <summary>Nothing is under the pointer, so the press starts a new selection.</summary>
    NewSelection,
    Playhead,
    SelectionStart,
    SelectionEnd,
}

/// <summary>
/// The main waveform editor surface.
///
/// Rendering model — deliberately simple, because the previous geometry-cache design
/// had a cost that varied wildly with zoom:
///   * The peak/RMS bands are painted per *device pixel column* straight from
///     <see cref="Audio.PeakStore"/> into a <see cref="WriteableBitmap"/>. There is no
///     geometry, no path rasterisation, no cache window, no background build queue.
///     One frame costs one linear pass over the bitmap, which is the same work at every
///     zoom level — that flatness is the whole point.
///   * Everything else (grid, centre lines, selection, markers, cursor, playhead) is a
///     handful of vector primitives drawn on top.
///   * Invalidation is synchronous. Deferring it through the dispatcher meant the view
///     update landed *after* the render pass that should have shown it, so the waveform
///     was always a frame behind and could double-step or drop a step when the
///     dispatcher and the render loop drifted apart.
///
/// Press Ctrl+Shift+D over the waveform for a live frame/cost readout.
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

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(WaveformView));

    public ICommand? SeekCommand
    {
        get => (ICommand?)GetValue(SeekCommandProperty);
        set => SetValue(SeekCommandProperty, value);
    }

    public static readonly DependencyProperty IsPlaybackActiveProperty = DependencyProperty.Register(
        nameof(IsPlaybackActive), typeof(bool), typeof(WaveformView));

    public bool IsPlaybackActive
    {
        get => (bool)GetValue(IsPlaybackActiveProperty);
        set => SetValue(IsPlaybackActiveProperty, value);
    }

    /// <summary>
    /// Live frame-timing overlay: frame interval, render/paint cost, playhead advance and
    /// view scroll, all as avg/min/max over the last 120 frames, plus the render tier.
    /// Click the waveform and press Ctrl+Shift+D to toggle. Reach for it first whenever
    /// playback smoothness is in question — it separates "frames are expensive" from
    /// "frames are uneven", which look identical on screen.
    /// </summary>
    public static bool ShowDiagnostics { get; set; }

    private bool _dragging;
    private bool _draggingEdge;
    private bool _draggingPlayhead;
    private int _dragAnchor;

    // ── band bitmap ──────────────────────────────────────────────
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private int[] _peakTop = [], _peakBottom = [], _rmsTop = [], _rmsBottom = [];
    private int _pixelWidth, _pixelHeight;
    private double _bitmapDpiX, _bitmapDpiY;
    private bool _painted;
    private PaintKey _paintKey;

    private readonly record struct PaintKey(double ViewStart, double Spp, double AmpZoom,
        int PeaksVersion, int PixelWidth, int PixelHeight, int Channels, object? Doc);

    public WaveformView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SizeChanged += (_, _) => { if (Document != null) Document.ViewWidthPixels = ActualWidth; };
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)d;
        view._painted = false;
        view._pendingZoomDocument = null;
        view._pendingZoomFactor = 1;
        view._lastPlayhead = -1;
        view._lastViewStart = double.NaN;
        view._statCount = 0;
        view._statIndex = 0;
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
            RequestRedraw();
    }

    /// <summary>
    /// Invalidate now when we are already on the UI thread. The playhead is driven from
    /// <c>CompositionTarget.Rendering</c>, and that handler runs *inside* the render pass —
    /// invalidating synchronously means the new position is drawn by that same pass instead
    /// of the next one.
    /// </summary>
    private void RequestRedraw()
    {
        if (Dispatcher.CheckAccess()) InvalidateVisual();
        else Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(InvalidateVisual));
    }

    // ── rendering ────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        long renderStart = Stopwatch.GetTimestamp();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.WaveBg, null, new Rect(0, 0, w, h));

        var vm = Document;
        if (vm == null || vm.Doc.Length == 0 || w < 2 || h < 2)
        {
            _painted = false;
            return;
        }

        int channels = Math.Max(1, vm.Doc.ChannelCount);
        double chH = h / channels;
        double spp = vm.SamplesPerPixel;
        double viewStart = vm.ViewStart;
        var dpiInfo = VisualTreeHelper.GetDpi(this);
        double dpi = dpiInfo.PixelsPerDip;

        // amplitude guides sit under the waveform, exactly as before
        for (int c = 0; c < channels; c++)
        {
            double mid = c * chH + chH / 2;
            double amp = chH * 0.46 * vm.AmpZoom;
            foreach (double levelDb in AmplitudeRuler.MarkerLevelsDb)
            {
                double offset = AmplitudeRuler.MarkerOffset(levelDb, amp);
                if (offset > chH / 2 - 2) continue;
                dc.DrawLine(WaveTheme.GridLine, new Point(0, mid - offset), new Point(w, mid - offset));
                dc.DrawLine(WaveTheme.GridLine, new Point(0, mid + offset), new Point(w, mid + offset));
            }
        }

        long paintStart = Stopwatch.GetTimestamp();
        bool repainted = false;
        if (EnsureBitmap(w, h, dpiInfo))
        {
            var key = new PaintKey(viewStart, spp, vm.AmpZoom, vm.PeaksVersion,
                _pixelWidth, _pixelHeight, channels, vm.Doc);
            if (!_painted || key != _paintKey)
            {
                PaintBands(vm, channels, viewStart, spp, w);
                _bitmap!.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight),
                    _pixels, _pixelWidth * 4, 0);
                _paintKey = key;
                _painted = true;
                repainted = true;
            }
            dc.DrawImage(_bitmap!, new Rect(0, 0, w, h));
        }
        long paintEnd = Stopwatch.GetTimestamp();

        for (int c = 0; c < channels; c++)
        {
            double mid = c * chH + chH / 2;
            dc.PushClip(new RectangleGeometry(new Rect(0, c * chH, w, chH)));
            dc.DrawLine(WaveTheme.CenterLine, new Point(0, mid), new Point(w, mid));
            dc.DrawText(WaveTheme.Text(channels == 1 ? "M" : c == 0 ? "L" : c == 1 ? "R" : $"C{c + 1}",
                WaveTheme.UiFace, 10, WaveTheme.TextMuted, dpi), new Point(8, c * chH + 6));
            dc.Pop();
            if (c > 0)
                dc.DrawLine(WaveTheme.ChannelDivider, new Point(0, c * chH), new Point(w, c * chH));
        }

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

        foreach (var marker in vm.Markers)
        {
            double mx = (marker.Position - viewStart) / spp;
            if (mx >= 0 && mx <= w)
                dc.DrawLine(WaveTheme.MarkerLine, new Point(mx, 0), new Point(mx, h));
        }

        double curX = (vm.Cursor - viewStart) / spp;
        if (curX >= 0 && curX <= w && !vm.HasSelection)
            dc.DrawLine(WaveTheme.CursorPen, new Point(curX, 0), new Point(curX, h));

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

        RecordFrame(renderStart, paintEnd - paintStart, repainted, vm, viewStart, spp);
        if (ShowDiagnostics) DrawDiagnostics(dc, w, spp, dpi);
    }

    /// <summary>Allocate (or resize) the band bitmap to match the control's device pixels.</summary>
    private bool EnsureBitmap(double w, double h, DpiScale dpiInfo)
    {
        double scaleX = dpiInfo.DpiScaleX > 0 ? dpiInfo.DpiScaleX : 1;
        double scaleY = dpiInfo.DpiScaleY > 0 ? dpiInfo.DpiScaleY : 1;
        int pw = Math.Clamp((int)Math.Ceiling(w * scaleX), 1, 8192);
        int ph = Math.Clamp((int)Math.Ceiling(h * scaleY), 1, 8192);
        double dpiX = 96 * scaleX, dpiY = 96 * scaleY;

        if (_bitmap != null && pw == _pixelWidth && ph == _pixelHeight
            && Math.Abs(dpiX - _bitmapDpiX) < 1e-6 && Math.Abs(dpiY - _bitmapDpiY) < 1e-6)
            return true;

        try
        {
            _bitmap = new WriteableBitmap(pw, ph, dpiX, dpiY, PixelFormats.Pbgra32, null);
        }
        catch
        {
            _bitmap = null;
            _painted = false;
            return false;
        }

        _pixelWidth = pw;
        _pixelHeight = ph;
        _bitmapDpiX = dpiX;
        _bitmapDpiY = dpiY;
        _pixels = new int[pw * ph];
        _peakTop = new int[pw];
        _peakBottom = new int[pw];
        _rmsTop = new int[pw];
        _rmsBottom = new int[pw];
        _painted = false;
        return true;
    }

    /// <summary>
    /// Paint the visible columns. Transparent background so the grid drawn underneath
    /// shows through; every waveform pixel is written exactly once in row order.
    /// </summary>
    private void PaintBands(DocumentViewModel vm, int channels, double viewStart, double spp, double width)
    {
        Array.Clear(_pixels);

        int peakArgb = OpaqueArgb(WaveTheme.WavePeak);
        // The RMS brush is translucent over the peak band; pre-blend it so the bitmap
        // stays fully opaque per pixel and needs no alpha compositing.
        int rmsArgb = BlendOver(WaveTheme.WaveRms, WaveTheme.WavePeak);

        // Derive this from the bitmap's own width rather than the DPI scale: if the
        // bitmap ever hits its size clamp, DrawImage stretches it over the full control
        // and a scale-derived mapping would slide the waveform off the cursor.
        double samplesPerDevicePixel = spp * width / _pixelWidth;
        int documentLength = vm.Doc.Length;
        int bandHeight = _pixelHeight / channels;
        if (bandHeight < 1) return;

        for (int c = 0; c < channels; c++)
        {
            int bandTop = c * bandHeight;
            int height = c == channels - 1 ? _pixelHeight - bandTop : bandHeight;
            if (height < 1) continue;
            int bandBottom = bandTop + height - 1;
            double mid = bandTop + height / 2.0;
            double amp = height * 0.46 * vm.AmpZoom;

            int usedTop = bandBottom, usedBottom = bandTop;
            for (int x = 0; x < _pixelWidth; x++)
            {
                double columnStart = viewStart + x * samplesPerDevicePixel;
                int s0 = (int)columnStart;
                int s1 = Math.Max(s0 + 1, (int)(columnStart + samplesPerDevicePixel));
                if (s0 < 0 || s0 >= documentLength)
                {
                    _peakTop[x] = 1;
                    _peakBottom[x] = 0; // empty column
                    continue;
                }

                vm.Peaks.Query(c, s0, s1, out float mn, out float mx, out float rms);
                _peakTop[x] = ClampRow(mid - Math.Clamp(mx, -1, 1) * amp, bandTop, bandBottom);
                _peakBottom[x] = ClampRow(mid - Math.Clamp(mn, -1, 1) * amp, bandTop, bandBottom);
                float r = Math.Min(rms, Math.Max(Math.Abs(mn), Math.Abs(mx)));
                _rmsTop[x] = ClampRow(mid - r * amp, bandTop, bandBottom);
                _rmsBottom[x] = ClampRow(mid + r * amp, bandTop, bandBottom);
                if (_peakTop[x] < usedTop) usedTop = _peakTop[x];
                if (_peakBottom[x] > usedBottom) usedBottom = _peakBottom[x];
            }

            // Only sweep the rows some column actually reaches — quiet material and low
            // amplitude zoom then cost a fraction of the band.
            for (int y = usedTop; y <= usedBottom; y++)
            {
                int row = y * _pixelWidth;
                for (int x = 0; x < _pixelWidth; x++)
                {
                    if (y < _peakTop[x] || y > _peakBottom[x]) continue;
                    _pixels[row + x] = y >= _rmsTop[x] && y <= _rmsBottom[x] ? rmsArgb : peakArgb;
                }
            }
        }
    }

    internal static int ClampRow(double y, int top, int bottom) =>
        (int)Math.Clamp(Math.Round(y), top, bottom);

    internal static int OpaqueArgb(Brush brush)
    {
        Color c = brush is SolidColorBrush solid ? solid.Color : Colors.Gray;
        return (0xFF << 24) | (c.R << 16) | (c.G << 8) | c.B;
    }

    /// <summary>Composite <paramref name="over"/> onto <paramref name="under"/>, returning an opaque pixel.</summary>
    internal static int BlendOver(Brush over, Brush under)
    {
        Color o = over is SolidColorBrush a ? a.Color : Colors.Gray;
        Color u = under is SolidColorBrush b ? b.Color : Colors.Black;
        double alpha = o.A / 255.0;
        int r = (int)Math.Round(o.R * alpha + u.R * (1 - alpha));
        int g = (int)Math.Round(o.G * alpha + u.G * (1 - alpha));
        int bl = (int)Math.Round(o.B * alpha + u.B * (1 - alpha));
        return (0xFF << 24) | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(bl, 0, 255);
    }

    // ── diagnostics ──────────────────────────────────────────────

    private const int StatWindow = 120;
    private readonly double[] _frameMs = new double[StatWindow];
    private readonly double[] _renderMs = new double[StatWindow];
    private readonly double[] _paintMs = new double[StatWindow];
    private readonly double[] _playheadDelta = new double[StatWindow];
    private readonly double[] _viewDeltaPx = new double[StatWindow];
    private int _statCount;
    private int _statIndex;
    private int _repaintCount;
    private long _lastFrameStamp;
    private int _lastPlayhead = -1;
    private double _lastViewStart = double.NaN;

    private void RecordFrame(long renderStart, long paintTicks, bool repainted,
        DocumentViewModel vm, double viewStart, double spp)
    {
        double toMs = 1000.0 / Stopwatch.Frequency;
        long now = Stopwatch.GetTimestamp();

        _frameMs[_statIndex] = _lastFrameStamp == 0 ? 0 : (now - _lastFrameStamp) * toMs;
        _lastFrameStamp = now;
        _renderMs[_statIndex] = (now - renderStart) * toMs;
        _paintMs[_statIndex] = paintTicks * toMs;
        _playheadDelta[_statIndex] = _lastPlayhead < 0 ? 0 : vm.PlayheadSample - _lastPlayhead;
        _lastPlayhead = vm.PlayheadSample;
        _viewDeltaPx[_statIndex] = double.IsNaN(_lastViewStart) || spp <= 0
            ? 0
            : (viewStart - _lastViewStart) / spp;
        _lastViewStart = viewStart;
        if (repainted) _repaintCount++;

        _statIndex = (_statIndex + 1) % StatWindow;
        if (_statCount < StatWindow) _statCount++;
    }

    private void DrawDiagnostics(DrawingContext dc, double w, double spp, double dpi)
    {
        if (_statCount < 2) return;
        var lines = new[]
        {
            $"tier {RenderCapability.Tier >> 16}   spp {spp:0.###}   repaints {_repaintCount}",
            Row("frame ms", _frameMs),
            Row("render ms", _renderMs),
            Row("paint ms", _paintMs),
            Row("playhead", _playheadDelta),
            Row("view px", _viewDeltaPx),
        };

        double lineHeight = 0, width = 0;
        var texts = new FormattedText[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            texts[i] = new FormattedText(lines[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                WaveTheme.MonoFace, 11, WaveTheme.TextMuted, dpi);
            lineHeight = Math.Max(lineHeight, texts[i].Height);
            width = Math.Max(width, texts[i].Width);
        }

        var box = new Rect(Math.Max(4, w - width - 20), 6, width + 14, lineHeight * lines.Length + 10);
        dc.DrawRoundedRectangle(WaveTheme.PanelBg, WaveTheme.ChannelDivider, box, 3, 3);
        for (int i = 0; i < texts.Length; i++)
            dc.DrawText(texts[i], new Point(box.X + 7, box.Y + 5 + i * lineHeight));
    }

    private string Row(string label, double[] samples)
    {
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        for (int i = 0; i < _statCount; i++)
        {
            double v = samples[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        return $"{label,-9} avg {sum / _statCount,8:0.00}  min {min,8:0.00}  max {max,8:0.00}";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.D
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ShowDiagnostics = !ShowDiagnostics;
            _statCount = 0;
            _statIndex = 0;
            _repaintCount = 0;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // ── interaction ──────────────────────────────────────────────

    private int SampleAt(Point p)
    {
        var vm = Document!;
        return (int)Math.Clamp(vm.ViewStart + p.X * vm.SamplesPerPixel, 0, Math.Max(0, vm.Doc.Length - 1));
    }

    private double XOf(double sample)
    {
        var vm = Document!;
        return (sample - vm.ViewStart) / vm.SamplesPerPixel;
    }

    /// <summary>How near the pointer has to be, in pixels, to take hold of the playhead.</summary>
    internal const double PlayheadGrabPixels = 8;

    /// <summary>
    /// The same for a selection edge, and deliberately tighter. The playhead is one line the user
    /// went looking for; a selection has two edges the pointer crosses on the way to anywhere else.
    /// </summary>
    internal const double SelectionEdgeGrabPixels = 6;

    /// <summary>
    /// The band at the top where the playhead keeps precedence over a selection edge sitting on the
    /// same column — which is exactly where every fresh selection leaves it, because building one
    /// sets the cursor and the playhead at the drag anchor. Without the band the edge the user just
    /// finished drawing would be the one edge that could not be grabbed again. The triangle drawn
    /// there is 7 px tall and is the grip this band describes.
    /// </summary>
    internal const double PlayheadGripHeight = 12;

    /// <summary>
    /// Decide what a press at <paramref name="pointer"/> takes hold of. Pure, because the precedence
    /// between an edge and the playhead is the one part of this that is a judgement rather than a
    /// distance, and it is the part worth pinning without a mouse.
    /// </summary>
    internal static WaveformGrab GrabAt(Point pointer, double width, double playheadX,
        bool hasSelection, double selStartX, double selEndX)
    {
        bool onPlayhead = playheadX >= 0 && playheadX <= width
                          && Math.Abs(pointer.X - playheadX) <= PlayheadGrabPixels;
        if (onPlayhead && pointer.Y <= PlayheadGripHeight) return WaveformGrab.Playhead;

        if (hasSelection)
        {
            double toStart = DistanceTo(pointer.X, selStartX);
            double toEnd = DistanceTo(pointer.X, selEndX);
            if (Math.Min(toStart, toEnd) <= SelectionEdgeGrabPixels)
                return toStart <= toEnd ? WaveformGrab.SelectionStart : WaveformGrab.SelectionEnd;
        }

        return onPlayhead ? WaveformGrab.Playhead : WaveformGrab.NewSelection;

        // An edge scrolled off the view is not something the pointer can be near.
        double DistanceTo(double x, double edgeX) =>
            edgeX >= 0 && edgeX <= width ? Math.Abs(x - edgeX) : double.PositiveInfinity;
    }

    private WaveformGrab GrabAt(Point pointer)
    {
        var vm = Document;
        if (vm == null || vm.Doc.Length == 0) return WaveformGrab.NewSelection;
        return GrabAt(pointer, ActualWidth, XOf(vm.PlayheadSample),
            vm.HasSelection, XOf(vm.SelStart), XOf(vm.SelEnd));
    }

    /// <summary>
    /// What a shift-click at <paramref name="sample"/> anchors against, which is the far side of
    /// whatever is already selected: the nearer edge is the one that moves. The split is the
    /// selection's midpoint rather than a distance comparison, which gives the same answer inside
    /// the selection and the right one outside it — a click past either end is nearer the end it is
    /// past, so shift-clicking beyond the selection lengthens it instead of collapsing it.
    /// With nothing selected there is no far edge and the cursor is the anchor, so shift-clicking
    /// after an ordinary click selects between the two.
    /// </summary>
    internal static int ExtendAnchor(int sample, bool hasSelection, int selStart, int selEnd, int cursor)
    {
        if (!hasSelection) return cursor;
        return sample < selStart + (selEnd - selStart) / 2 ? selEnd : selStart;
    }

    private void RequestSeek(int sample, PlayheadSeekPhase phase)
    {
        var vm = Document;
        if (vm == null) return;
        var request = new PlayheadSeekRequest(vm, sample, phase);
        if (SeekCommand?.CanExecute(request) == true)
            SeekCommand.Execute(request);
    }

    private void BeginDrag(Point point, bool extend)
    {
        var vm = Document;
        if (vm == null) return;

        // Shift is checked ahead of what is under the pointer, so it means the same thing wherever
        // the click lands. It leaves the drag in exactly the state an edge grab does, which is what
        // makes a shift-click something you can keep dragging rather than a one-shot.
        if (extend)
        {
            int sample = SampleAt(point);
            _dragging = _draggingEdge = true;
            _dragAnchor = ExtendAnchor(sample, vm.HasSelection, vm.SelStart, vm.SelEnd, vm.Cursor);
            SelectBetweenAnchorAnd(sample);
            return;
        }

        switch (GrabAt(point))
        {
            case WaveformGrab.Playhead:
                _draggingPlayhead = true;
                RequestSeek(SampleAt(point), PlayheadSeekPhase.Begin);
                return;

            // Lengthening or shortening a selection is an ordinary selection drag anchored on the
            // edge that is staying put, rather than a mode of its own. Everything below then comes
            // for free and comes out identical to building one: the min/max, the clamping, the flip
            // when the dragged edge passes the anchor, and the live update with no edit behind it.
            case WaveformGrab.SelectionStart:
                _dragging = _draggingEdge = true;
                _dragAnchor = vm.SelEnd;
                return;
            case WaveformGrab.SelectionEnd:
                _dragging = _draggingEdge = true;
                _dragAnchor = vm.SelStart;
                return;

            default:
                _dragging = true;
                _draggingEdge = false;
                _dragAnchor = SampleAt(point);
                vm.SetCursor(_dragAnchor, clearSelection: true);
                return;
        }
    }

    private void ContinueDrag(Point point)
    {
        var vm = Document;
        if (vm == null) return;
        if (_draggingPlayhead)
        {
            RequestSeek(SampleAt(point), PlayheadSeekPhase.Update);
            return;
        }

        Cursor = _dragging
            ? _draggingEdge ? Cursors.SizeWE : null
            : GrabAt(point) == WaveformGrab.NewSelection ? null : Cursors.SizeWE;
        if (!_dragging) return;

        // A new selection needs a pixel of travel before it becomes one, so a click does not select
        // a sample. An edge drag is already past that threshold on its first move — the anchor is
        // the far edge — and following the hand exactly is what lets one edge be dragged onto the
        // other to clear the selection, which a threshold would leave stuck at its last width.
        int s = SampleAt(point);
        if (_draggingEdge || Math.Abs(s - _dragAnchor) > (int)vm.SamplesPerPixel)
            SelectBetweenAnchorAnd(s);
    }

    private void SelectBetweenAnchorAnd(int sample) =>
        Document?.SetSelection(Math.Min(_dragAnchor, sample), Math.Max(_dragAnchor, sample));

    private void EndDrag(Point point)
    {
        if (_draggingPlayhead && Document != null)
            RequestSeek(SampleAt(point), PlayheadSeekPhase.End);
        _draggingPlayhead = false;
        _dragging = false;
        _draggingEdge = false;
    }

    /// <summary>
    /// Runs one complete press-drag-release against the bound document. The mouse handlers are thin
    /// wrappers over the same three calls, so driving this exercises the path a real drag takes
    /// rather than a parallel copy of it — which is the only way to cover it without a live mouse.
    /// <paramref name="extend"/> stands for the shift key, which the handler reads and this does not:
    /// <see cref="Keyboard.Modifiers"/> is process-wide state and would make the seam depend on what
    /// the machine running the test happens to be holding down.
    /// </summary>
    internal void PerformDrag(Point from, Point to, bool extend = false)
    {
        BeginDrag(from, extend);
        ContinueDrag(to);
        EndDrag(to);
    }

    /// <summary>
    /// Press and release with no movement in between, which is a different gesture from a drag whose
    /// two points happen to coincide: a real click raises no <c>MouseMove</c> at all, so anything
    /// that only takes effect in <see cref="ContinueDrag"/> never runs. Shift-click depends on that
    /// difference, so the seam has to be able to express it.
    /// </summary>
    internal void PerformClick(Point point, bool extend = false)
    {
        BeginDrag(point, extend);
        EndDrag(point);
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
        BeginDrag(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ContinueDrag(e.GetPosition(this));
        if (_draggingPlayhead) e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool wasPlayhead = _draggingPlayhead;
        // Flags are cleared before the release, because releasing raises OnLostMouseCapture and a
        // playhead seek would otherwise be ended twice.
        EndDrag(e.GetPosition(this));
        ReleaseMouseCapture();
        if (wasPlayhead) e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_draggingPlayhead && Document != null)
            RequestSeek(Document.PlayheadSample, PlayheadSeekPhase.End);
        _draggingPlayhead = false;
        _dragging = false;
        _draggingEdge = false;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var vm = Document;
        if (vm == null) return;
        Focus();
        int sample = SampleAt(e.GetPosition(this));
        if (!vm.HasSelection || sample < vm.SelStart || sample >= vm.SelEnd)
            vm.SetCursor(sample, clearSelection: true);
    }

    // A wheel spin arrives as a burst of notches. Fold the burst into one zoom step so
    // the view is recomputed once per frame instead of once per notch.
    private double _pendingZoomFactor = 1;
    private double _pendingZoomX;
    private bool _zoomQueued;
    private DocumentViewModel? _pendingZoomDocument;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var vm = Document;
        if (vm == null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            vm.AmpZoom *= e.Delta > 0 ? 1.25 : 1 / 1.25;
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            vm.ScrollBy(-e.Delta * vm.SamplesPerPixel * 0.5);
        else
            QueueZoom(e.Delta > 0 ? 1 / 1.3 : 1.3, e.GetPosition(this).X);
        e.Handled = true;
    }

    private void QueueZoom(double factor, double anchorX)
    {
        var target = Document;
        if (target == null) return;
        if (!ReferenceEquals(target, _pendingZoomDocument))
        {
            _pendingZoomDocument = target;
            _pendingZoomFactor = 1;
        }
        _pendingZoomFactor *= factor;
        _pendingZoomX = anchorX;
        if (_zoomQueued) return;
        _zoomQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _zoomQueued = false;
            double factorToApply = _pendingZoomFactor;
            var vm = _pendingZoomDocument;
            _pendingZoomFactor = 1;
            _pendingZoomDocument = null;
            if (vm == null || factorToApply == 1 || !ReferenceEquals(vm, Document)) return;
            if (IsPlaybackActive) vm.ZoomBy(factorToApply, vm.PlayheadSample);
            else vm.ZoomAt(_pendingZoomX, factorToApply);
        }));
    }
}
