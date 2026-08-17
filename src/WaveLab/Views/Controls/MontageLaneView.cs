using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaveLab.Audio;
using WaveLab.Audio.Montage;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>
/// The montage's single lane: clips drawn along the timeline, with their joins marked.
/// </summary>
/// <remarks>
/// <para>
/// Follows the <see cref="WaveformView"/> discipline — the clip waveforms are painted into a
/// <see cref="WriteableBitmap"/> only when the paint key changes, and everything that moves
/// underneath a drag (selection, edge grips, the crossfade zones and their labels, the playhead) is
/// drawn as vectors on top. A montage repaints on every pixel of a clip drag, so putting the
/// waveforms in the moving layer would repaint the whole side at mouse rate.
/// </para>
/// <para>
/// <b>An overlap is drawn between the clips, not on them.</b> A crossfade belongs to the pair — it
/// is one decision about one region — and drawing a fade shape on each clip's edge says the
/// opposite, that each clip fades on its own and the two happen to coincide.
/// </para>
/// </remarks>
public sealed class MontageLaneView : FrameworkElement
{
    public static readonly DependencyProperty MontageProperty = DependencyProperty.Register(
        nameof(Montage), typeof(MontageViewModel), typeof(MontageLaneView),
        new FrameworkPropertyMetadata(null, OnMontageChanged));

    public MontageViewModel? Montage
    {
        get => (MontageViewModel?)GetValue(MontageProperty);
        set => SetValue(MontageProperty, value);
    }

    /// <summary>Raised when the user picks a clip, so the window can show it in the inspector.</summary>
    public event EventHandler? SelectionChanged;

    private readonly record struct PaintKey(
        double ViewStart, double Spp, int Revision, double Width, double Height, int Clips);

    private WriteableBitmap? _bitmap;
    private PaintKey _paintKey;
    private bool _painted;
    private int _pixelWidth, _pixelHeight;
    private double _bitmapDpiX, _bitmapDpiY;
    private int[]? _pixels;

    private const double HeaderHeight = 17;
    private const double LaneMargin = 10;
    private const double LabelBand = 18;
    private const double GripWidth = 5;

    private enum DragKind { None, Move, TrimHead, TrimTail }

    private DragKind _drag = DragKind.None;
    private MontageClip? _dragClip;
    private double _dragGrabSample;
    private int _dragOriginalStart;

    public MontageLaneView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private static void OnMontageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MontageLaneView)d;
        if (e.OldValue is MontageViewModel old) old.PropertyChanged -= view.OnMontagePropertyChanged;
        if (e.NewValue is MontageViewModel now) now.PropertyChanged += view.OnMontagePropertyChanged;
        view._painted = false;
        view.InvalidateVisual();
    }

    private void OnMontagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Synchronous where possible, for the reason WaveformView is: posting the invalidate lands
        // it in the next render pass and the lane lags the pointer by a frame.
        if (Dispatcher.CheckAccess()) InvalidateVisual();
        else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
            new Action(InvalidateVisual));
    }

    // ── geometry ─────────────────────────────────────────────────

    private double ClipTop => LaneMargin;
    private double ClipBottom => Math.Max(ClipTop + 24, ActualHeight - LaneMargin - LabelBand);

    private MontageClip? ClipAt(Point point, out DragKind edge)
    {
        edge = DragKind.Move;
        if (Montage is not { } vm) return null;
        if (point.Y < ClipTop || point.Y > ClipBottom) return null;

        // Last first: later clips are drawn on top, so they should be picked first.
        for (int i = vm.Montage.Clips.Count - 1; i >= 0; i--)
        {
            MontageClip clip = vm.Montage.Clips[i];
            double left = vm.PixelOf(clip.TimelineStart);
            double right = vm.PixelOf(clip.TimelineEnd);
            if (point.X < left || point.X > right) continue;

            if (point.X - left <= GripWidth) edge = DragKind.TrimHead;
            else if (right - point.X <= GripWidth) edge = DragKind.TrimTail;
            else edge = DragKind.Move;
            return clip;
        }
        return null;
    }

    // ── painting ─────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(WaveTheme.WaveBg, null, new Rect(0, 0, w, h));
        if (Montage is not { } vm) return;

        vm.ViewWidthPixels = w;
        double dpiX = 96, dpiY = 96;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            dpiX = 96 * target.TransformToDevice.M11;
            dpiY = 96 * target.TransformToDevice.M22;
        }

        EnsureBitmap(w, h, dpiX, dpiY);
        if (_bitmap != null)
        {
            var key = new PaintKey(vm.ViewStart, vm.SamplesPerPixel, vm.Revision, w, h,
                vm.Montage.Clips.Count);
            if (!_painted || key != _paintKey)
            {
                PaintClips(vm, w, h);
                _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight),
                    _pixels!, _pixelWidth * 4, 0);
                _paintKey = key;
                _painted = true;
            }
            dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
        }

        DrawOverlays(dc, vm, w, h);
    }

    private void EnsureBitmap(double w, double h, double dpiX, double dpiY)
    {
        int pw = Math.Max(1, (int)Math.Round(w * dpiX / 96));
        int ph = Math.Max(1, (int)Math.Round(h * dpiY / 96));
        if (_bitmap != null && pw == _pixelWidth && ph == _pixelHeight &&
            Math.Abs(dpiX - _bitmapDpiX) < 1e-6 && Math.Abs(dpiY - _bitmapDpiY) < 1e-6) return;

        try
        {
            _bitmap = new WriteableBitmap(pw, ph, dpiX, dpiY, PixelFormats.Pbgra32, null);
            _pixels = new int[pw * ph];
        }
        catch (OutOfMemoryException)
        {
            _bitmap = null;
            _pixels = null;
        }
        _pixelWidth = pw;
        _pixelHeight = ph;
        _bitmapDpiX = dpiX;
        _bitmapDpiY = dpiY;
        _painted = false;
    }

    /// <summary>Paints each clip's waveform into the bitmap, one device-pixel column at a time.</summary>
    private void PaintClips(MontageViewModel vm, double w, double h)
    {
        if (_pixels == null) return;
        Array.Clear(_pixels);

        double scaleX = _pixelWidth / Math.Max(1e-9, w);
        double scaleY = _pixelHeight / Math.Max(1e-9, h);
        int top = (int)Math.Round((ClipTop + HeaderHeight) * scaleY);
        int bottom = (int)Math.Round(ClipBottom * scaleY);
        if (bottom <= top) return;

        for (int index = 0; index < vm.Montage.Clips.Count; index++)
        {
            MontageClip clip = vm.Montage.Clips[index];
            if (clip.SourceIndex < 0 || clip.SourceIndex >= vm.Montage.Sources.Count) continue;

            MontageSource source = vm.Montage.Sources[clip.SourceIndex];
            if (source.Length == 0) continue;

            PeakStore peaks = vm.PeaksFor(clip.SourceIndex);
            int channels = Math.Max(1, source.ChannelCount);

            int from = Math.Max(0, (int)Math.Floor(vm.PixelOf(clip.TimelineStart) * scaleX));
            int to = Math.Min(_pixelWidth - 1, (int)Math.Ceiling(vm.PixelOf(clip.TimelineEnd) * scaleX));

            // A palette per source, so two clips of the same take read as the same take.
            int body = SourceColour(clip.SourceIndex, 0xB0);
            double sppDevice = vm.SamplesPerPixel / scaleX;

            for (int px = from; px <= to; px++)
            {
                double timeline = vm.ViewStart + px * sppDevice;
                int offset = (int)(timeline - clip.TimelineStart);
                if (offset < 0 || offset >= clip.Length) continue;

                int s0 = clip.SourceStart + offset;
                int s1 = s0 + Math.Max(1, (int)sppDevice);
                if (s0 >= source.Length) continue;

                float lo = 0, hi = 0;
                for (int c = 0; c < channels; c++)
                {
                    peaks.Query(c, s0, Math.Min(s1, source.Length), out float mn, out float mx, out _);
                    lo = Math.Min(lo, mn);
                    hi = Math.Max(hi, mx);
                }

                double mid = (top + bottom) / 2.0;
                double half = (bottom - top) / 2.0;
                int y0 = (int)Math.Round(mid - hi * half);
                int y1 = (int)Math.Round(mid - lo * half);
                if (y1 < y0) (y0, y1) = (y1, y0);
                y0 = Math.Clamp(y0, top, bottom - 1);
                y1 = Math.Clamp(y1, top, bottom - 1);

                for (int y = y0; y <= y1; y++) _pixels[y * _pixelWidth + px] = body;
            }
        }
    }

    /// <summary>
    /// A colour per source. Clips of the same take share one, which is what makes a retake spliced
    /// into a side visible as a different colour rather than having to be read.
    /// </summary>
    private static int SourceColour(int sourceIndex, int alpha)
    {
        (int R, int G, int B)[] palette =
        [
            (0x31, 0xA9, 0x98),   // the house teal
            (0x9B, 0x8C, 0xE0),   // violet
            (0xE0, 0xA0, 0x6B),   // amber-brown
            (0x6B, 0xA8, 0xE0),   // blue
            (0xD0, 0x7E, 0xA8),   // rose
        ];
        (int r, int g, int b) = palette[Math.Abs(sourceIndex) % palette.Length];
        return (alpha << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>The overview's block colour for a source, so the two views agree at a glance.</summary>
    public static Brush OverviewFill(int sourceIndex) => SourceBrush(sourceIndex, 0x8C);

    private static Brush SourceBrush(int sourceIndex, byte alpha)
    {
        int packed = SourceColour(sourceIndex, alpha);
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush HeaderFill =
        Freeze(new SolidColorBrush(Color.FromArgb(0x62, 0x00, 0x00, 0x00)));
    private static readonly Brush SelectedFill =
        Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0x3F, 0xD6, 0xC2)));
    private static readonly Brush XfFill =
        Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xB4, 0x54)));
    private static readonly Brush XfLabelFill =
        Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x13, 0x0F)));
    private static readonly Brush XfText =
        Freeze(new SolidColorBrush(WaveTheme.Amber));
    private static readonly Pen XfEdge =
        FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xB4, 0x54)), 1)
        { DashStyle = new DashStyle([3, 3], 0) });
    private static readonly Pen XfLabelEdge =
        FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x3B, 0x22)), 1));
    private static readonly Brush ClipText =
        Freeze(new SolidColorBrush(Color.FromRgb(0xC7, 0xCE, 0xD6)));
    private static readonly Pen SelectedEdge =
        FreezePen(new Pen(new SolidColorBrush(WaveTheme.Accent), 1.4));

    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }
    private static Pen FreezePen(Pen pen) { pen.Freeze(); return pen; }

    private void DrawOverlays(DrawingContext dc, MontageViewModel vm, double w, double h)
    {
        double top = ClipTop, bottom = ClipBottom;
        double dpi = _bitmapDpiY / 96.0;

        for (int i = 0; i < vm.Montage.Clips.Count; i++)
        {
            MontageClip clip = vm.Montage.Clips[i];
            double left = vm.PixelOf(clip.TimelineStart);
            double right = vm.PixelOf(clip.TimelineEnd);
            if (right < -20 || left > w + 20) continue;

            double x0 = Math.Max(-2, left), x1 = Math.Min(w + 2, right);
            if (x1 - x0 < 1) continue;

            var body = new Rect(x0, top, x1 - x0, bottom - top);
            bool selected = ReferenceEquals(clip, vm.Selected);

            // A tint of the source's own colour behind the waveform. Without it two clips cut from
            // the same take butt together into what looks like one clip, and the boundary a user is
            // about to drag is invisible.
            dc.DrawRoundedRectangle(selected ? SelectedFill : SourceBrush(clip.SourceIndex, 0x1C),
                selected ? SelectedEdge : new Pen(SourceBrush(clip.SourceIndex, 0xC0), 1),
                body, 5, 5);

            // Header strip with the clip's name and gain.
            var header = new Rect(x0, top, x1 - x0, Math.Min(HeaderHeight, body.Height));
            dc.PushClip(new RectangleGeometry(header, 5, 5));
            dc.DrawRectangle(HeaderFill, null, header);
            dc.Pop();

            if (header.Width > 46)
            {
                dc.PushClip(new RectangleGeometry(header));
                FormattedText name = WaveTheme.Text(clip.Name, WaveTheme.UiFace, 10, ClipText, dpi);
                dc.DrawText(name, new Point(x0 + 6, top + 3));

                if (Math.Abs(clip.GainDb) > 0.05 && header.Width > 110)
                {
                    FormattedText gain = WaveTheme.Text(
                        clip.GainDb.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " dB",
                        WaveTheme.MonoFace, 9, WaveTheme.TextFaint, dpi);
                    dc.DrawText(gain, new Point(x1 - gain.Width - 6, top + 4));
                }
                dc.Pop();
            }

            if (selected)
            {
                dc.DrawRectangle(WaveTheme.SelectionHandle, null,
                    new Rect(x0, top, GripWidth, bottom - top));
                dc.DrawRectangle(WaveTheme.SelectionHandle, null,
                    new Rect(x1 - GripWidth, top, GripWidth, bottom - top));
            }
        }

        DrawCrossfades(dc, vm, w, top, bottom, dpi);
    }

    private void DrawCrossfades(DrawingContext dc, MontageViewModel vm, double w,
        double top, double bottom, double dpi)
    {
        for (int i = 0; i + 1 < vm.Montage.Clips.Count; i++)
        {
            MontageClip a = vm.Montage.Clips[i];
            MontageClip b = vm.Montage.Clips[i + 1];
            int overlap = MontageDocument.Overlap(a, b);
            if (overlap <= 0) continue;

            int start = Math.Max(a.TimelineStart, b.TimelineStart);
            double x0 = vm.PixelOf(start);
            double x1 = vm.PixelOf(start + overlap);
            if (x1 < -40 || x0 > w + 40) continue;

            var zone = new Rect(Math.Max(-2, x0), top - 4,
                Math.Max(1, Math.Min(w + 2, x1) - Math.Max(-2, x0)), bottom - top + 8);
            dc.DrawRectangle(XfFill, null, zone);
            dc.DrawLine(XfEdge, new Point(x0, top - 4), new Point(x0, bottom + 4));
            dc.DrawLine(XfEdge, new Point(x1, top - 4), new Point(x1, bottom + 4));

            // The label goes under the clips, never over them: at the top it lands on the header
            // strip and hides the name of the clip it belongs to.
            double seconds = (double)overlap / vm.Montage.SampleRate;
            string text = seconds >= 0.05
                ? $"{seconds:0.00} s"
                : $"{overlap} smp";

            FormattedText label = WaveTheme.Text(text, WaveTheme.MonoFace, 9, XfText, dpi);
            double cx = (x0 + x1) / 2;
            var box = new Rect(cx - label.Width / 2 - 5, bottom + 3, label.Width + 10, label.Height + 3);
            if (box.Right < 0 || box.Left > w) continue;

            dc.DrawRoundedRectangle(XfLabelFill, XfLabelEdge, box, 3, 3);
            dc.DrawText(label, new Point(box.Left + 5, box.Top + 1));
        }
    }

    // ── pointer ──────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Montage is not { } vm) return;
        Point point = e.GetPosition(this);

        if (_drag == DragKind.None)
        {
            MontageClip? over = ClipAt(point, out DragKind edge);
            Cursor = over == null ? Cursors.Arrow
                : vm.Tool == MontageTool.Split ? Cursors.Cross
                : edge is DragKind.TrimHead or DragKind.TrimTail ? Cursors.SizeWE
                : Cursors.SizeAll;
            return;
        }

        if (_dragClip == null) return;
        int sample = (int)Math.Max(0, vm.SampleAt(point.X));

        switch (_drag)
        {
            case DragKind.Move:
                vm.MoveClip(_dragClip, _dragOriginalStart + (int)(vm.SampleAt(point.X) - _dragGrabSample));
                break;
            case DragKind.TrimHead:
                vm.TrimClip(_dragClip, head: true, sample);
                break;
            case DragKind.TrimTail:
                vm.TrimClip(_dragClip, head: false, sample);
                break;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Montage is not { } vm) return;

        Point point = e.GetPosition(this);
        MontageClip? clip = ClipAt(point, out DragKind edge);
        vm.Selected = clip;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (clip == null) return;

        if (vm.Tool == MontageTool.Split)
        {
            MontageClip? right = vm.SplitClip(clip, (int)vm.SampleAt(point.X));
            if (right != null) vm.Selected = right;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dragClip = clip;
        _dragGrabSample = vm.SampleAt(point.X);
        _dragOriginalStart = clip.TimelineStart;
        _drag = vm.Tool == MontageTool.Trim && edge == DragKind.Move
            ? DragKind.TrimTail
            : edge;

        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_drag == DragKind.None) return;

        // The zero-crossing snap is applied once, when the edge is let go. Snapping while dragging
        // makes the clip jump between crossings under the pointer, which reads as the drag fighting
        // the hand rather than helping it.
        if (Montage is { } vm && _dragClip is { } clip && vm.SnapToZeroCrossing)
        {
            if (_drag == DragKind.TrimHead)
            {
                int snapped = vm.SnapSource(clip.SourceIndex, clip.SourceStart);
                int delta = snapped - clip.SourceStart;
                clip.SourceStart = snapped;
                clip.TimelineStart += delta;
                clip.Length = Math.Max(1, clip.Length - delta);
                vm.Touch();
            }
            else if (_drag == DragKind.TrimTail)
            {
                int end = clip.SourceStart + clip.Length;
                int snapped = vm.SnapSource(clip.SourceIndex, end);
                clip.Length = Math.Max(1, snapped - clip.SourceStart);
                vm.Touch();
            }
        }

        _drag = DragKind.None;
        _dragClip = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Montage is not { } vm) return;

        double anchor = vm.SampleAt(e.GetPosition(this).X);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            vm.Zoom(e.Delta > 0 ? 1 / 1.25 : 1.25, anchor);
        else
            vm.ViewStart -= e.Delta / 120.0 * vm.SamplesPerPixel * 90;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Montage is not { } vm) return;

        if (e.Key == Key.Delete && vm.Selected != null)
        {
            vm.RemoveSelected();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
