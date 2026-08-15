using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>Whole-file overview with a draggable viewport rectangle.</summary>
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
    private bool _invalidateQueued;

    public OverviewBar()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (OverviewBar)d;
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= view.OnVmChanged;
        if (e.NewValue is DocumentViewModel vm) vm.PropertyChanged += view.OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ViewStart) or nameof(DocumentViewModel.SamplesPerPixel)
            or nameof(DocumentViewModel.PeaksVersion) or nameof(DocumentViewModel.ViewWidthPixels)
            or nameof(DocumentViewModel.PlayheadSample))
            QueueInvalidateVisual();
    }

    private void QueueInvalidateVisual()
    {
        if (_invalidateQueued) return;
        _invalidateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _invalidateQueued = false;
            InvalidateVisual();
        });
    }

    // whole-file geometry cache — rebuilt only on resize or when the peaks change
    private StreamGeometry? _cachedGeo;
    private (double W, double H, int Version, object? Doc) _cacheKey;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.OverviewBg, null, new Rect(0, 0, w, h));
        var vm = Document;
        if (vm == null || vm.Doc.Length == 0 || w < 2) return;

        double mid = h / 2;

        var key = (w, h, vm.PeaksVersion, (object?)vm.Doc);
        if (_cachedGeo == null || key != _cacheKey)
        {
            _cacheKey = key;
            double sppFull = vm.Doc.Length / w;
            double amp = h * 0.44;
            int channels = vm.Doc.ChannelCount;

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                var top = new Point[(int)w];
                var bot = new Point[(int)w];
                for (int x = 0; x < (int)w; x++)
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
                    top[x] = new Point(x, mid - Math.Clamp(mx, -1, 1) * amp);
                    bot[x] = new Point(x, mid - Math.Clamp(mn, -1, 1) * amp);
                }
                g.BeginFigure(top[0], true, true);
                for (int x = 1; x < top.Length; x++) g.LineTo(top[x], false, false);
                for (int x = bot.Length - 1; x >= 0; x--) g.LineTo(bot[x], false, false);
            }
            geo.Freeze();
            _cachedGeo = geo;
        }

        dc.PushOpacity(0.75);
        dc.DrawGeometry(WaveTheme.WavePeak, null, _cachedGeo);
        dc.Pop();
        dc.DrawLine(WaveTheme.CenterLine, new Point(0, mid), new Point(w, mid));

        // viewport rectangle
        double vx0 = vm.ViewStart / vm.Doc.Length * w;
        double vx1 = (vm.ViewStart + vm.SamplesPerPixel * vm.ViewWidthPixels) / vm.Doc.Length * w;
        vx1 = Math.Min(vx1, w);
        if (vx1 > vx0)
            dc.DrawRectangle(WaveTheme.ViewportFill, WaveTheme.ViewportPen,
                new Rect(vx0, 1, Math.Max(4, vx1 - vx0), h - 2));

        // playhead
        double phX = (double)vm.PlayheadSample / vm.Doc.Length * w;
        dc.DrawLine(WaveTheme.Playhead, new Point(phX, 0), new Point(phX, h));
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
