using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>
/// Whole-file overview with a draggable viewport rectangle.
/// The wave is painted into a bitmap once per size/peaks change; the per-frame work
/// during playback is a bitmap blit plus two lines.
/// </summary>
public sealed class OverviewBar : FrameworkElement
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(OverviewBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDocumentChanged));

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(OverviewBar));

    public ICommand? SeekCommand
    {
        get => (ICommand?)GetValue(SeekCommandProperty);
        set => SetValue(SeekCommandProperty, value);
    }

    private bool _dragging;
    private bool _draggingPlayhead;

    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private int _pixelWidth, _pixelHeight;
    private double _bitmapDpiX, _bitmapDpiY;
    private bool _painted;
    private (double W, double H, int Version, object? Doc) _paintKey;

    public OverviewBar()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (OverviewBar)d;
        view._painted = false;
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= view.OnVmChanged;
        if (e.NewValue is DocumentViewModel vm) vm.PropertyChanged += view.OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ViewStart) or nameof(DocumentViewModel.SamplesPerPixel)
            or nameof(DocumentViewModel.PeaksVersion) or nameof(DocumentViewModel.ViewWidthPixels)
            or nameof(DocumentViewModel.PlayheadSample))
            RequestRedraw();
    }

    /// <summary>Synchronous on the UI thread so a change made during the render pass is drawn by it.</summary>
    private void RequestRedraw()
    {
        if (Dispatcher.CheckAccess()) InvalidateVisual();
        else Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(InvalidateVisual));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.OverviewBg, null, new Rect(0, 0, w, h));
        var vm = Document;
        if (vm == null || vm.Doc.Length == 0 || w < 2 || h < 4)
        {
            _painted = false;
            return;
        }

        double mid = h / 2;
        var dpiInfo = VisualTreeHelper.GetDpi(this);
        if (EnsureBitmap(w, h, dpiInfo))
        {
            var key = (w, h, vm.PeaksVersion, (object?)vm.Doc);
            if (!_painted || key != _paintKey)
            {
                PaintWave(vm);
                _bitmap!.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight),
                    _pixels, _pixelWidth * 4, 0);
                _paintKey = key;
                _painted = true;
            }
            dc.PushOpacity(0.75);
            dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
            dc.Pop();
        }

        dc.DrawLine(WaveTheme.CenterLine, new Point(0, mid), new Point(w, mid));

        double vx0 = vm.ViewStart / vm.Doc.Length * w;
        double vx1 = (vm.ViewStart + vm.SamplesPerPixel * vm.ViewWidthPixels) / vm.Doc.Length * w;
        vx1 = Math.Min(vx1, w);
        if (vx1 > vx0)
            dc.DrawRectangle(WaveTheme.ViewportFill, WaveTheme.ViewportPen,
                new Rect(vx0, 1, Math.Max(4, vx1 - vx0), h - 2));

        double phX = (double)vm.PlayheadSample / vm.Doc.Length * w;
        dc.DrawLine(WaveTheme.Playhead, new Point(phX, 0), new Point(phX, h));
    }

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
        _painted = false;
        return true;
    }

    private void PaintWave(DocumentViewModel vm)
    {
        Array.Clear(_pixels);
        int argb = WaveformView.OpaqueArgb(WaveTheme.WavePeak);
        int channels = vm.Doc.ChannelCount;
        double sppFull = vm.Doc.Length / (double)_pixelWidth;
        double mid = _pixelHeight / 2.0;
        double amp = _pixelHeight * 0.44;

        var top = new int[_pixelWidth];
        var bottom = new int[_pixelWidth];
        for (int x = 0; x < _pixelWidth; x++)
        {
            int s0 = (int)(x * sppFull);
            int s1 = Math.Max(s0 + 1, (int)((x + 1) * sppFull));
            float mn = 0, mx = 0;
            for (int c = 0; c < channels; c++)
            {
                vm.Peaks.Query(c, s0, s1, out float cmn, out float cmx, out _);
                mn = Math.Min(mn, cmn);
                mx = Math.Max(mx, cmx);
            }
            top[x] = WaveformView.ClampRow(mid - Math.Clamp(mx, -1, 1) * amp, 0, _pixelHeight - 1);
            bottom[x] = WaveformView.ClampRow(mid - Math.Clamp(mn, -1, 1) * amp, 0, _pixelHeight - 1);
        }

        for (int y = 0; y < _pixelHeight; y++)
        {
            int row = y * _pixelWidth;
            for (int x = 0; x < _pixelWidth; x++)
                if (y >= top[x] && y <= bottom[x]) _pixels[row + x] = argb;
        }
    }

    private void MoveViewTo(Point p)
    {
        var vm = Document;
        if (vm == null || vm.Doc.Length == 0 || ActualWidth < 1) return;
        double centerSample = p.X / ActualWidth * vm.Doc.Length;
        vm.CenterViewOn(centerSample);
    }

    private int SampleAt(Point p)
    {
        var vm = Document!;
        return (int)Math.Clamp(p.X / Math.Max(1, ActualWidth) * vm.Doc.Length,
            0, Math.Max(0, vm.Doc.Length - 1));
    }

    private bool IsNearPlayhead(Point p)
    {
        var vm = Document;
        if (vm == null || vm.Doc.Length == 0) return false;
        double x = (double)vm.PlayheadSample / vm.Doc.Length * ActualWidth;
        return Math.Abs(p.X - x) <= 8;
    }

    private void RequestSeek(int sample, PlayheadSeekPhase phase)
    {
        var vm = Document;
        if (vm == null) return;
        var request = new PlayheadSeekRequest(vm, sample, phase);
        if (SeekCommand?.CanExecute(request) == true)
            SeekCommand.Execute(request);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        if (Document != null && IsNearPlayhead(point))
        {
            _draggingPlayhead = true;
            CaptureMouse();
            RequestSeek(SampleAt(point), PlayheadSeekPhase.Begin);
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        MoveViewTo(point);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_draggingPlayhead && Document != null)
        {
            RequestSeek(SampleAt(point), PlayheadSeekPhase.Update);
            e.Handled = true;
            return;
        }
        Cursor = IsNearPlayhead(point) ? Cursors.SizeWE : null;
        if (_dragging) MoveViewTo(point);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_draggingPlayhead && Document != null)
        {
            RequestSeek(SampleAt(e.GetPosition(this)), PlayheadSeekPhase.End);
            _draggingPlayhead = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_draggingPlayhead && Document != null)
            RequestSeek(Document.PlayheadSample, PlayheadSeekPhase.End);
        _draggingPlayhead = false;
        _dragging = false;
        base.OnLostMouseCapture(e);
    }
}
