using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Audio;
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

    private bool _dragging;
    private bool _draggingPlayhead;
    private bool _invalidateQueued;
    private int _dragAnchor;

    // geometry cache — rebuilt only when this key changes
    private readonly record struct CacheKey(double Spp, double W, double H, double AmpZoom,
        int PeaksVersion, int Channels, object? Doc);
    internal readonly record struct GeometryWindow(double StartSample, double EndSample, int PixelWidth);
    internal enum GeometryBuildDisposition
    {
        Reject,
        RetryForCurrentView,
        Apply
    }

    /// <summary>
    /// Serial latest-wins queue used by the geometry worker. Keeping this state machine
    /// independent of Dispatcher/Task makes its concurrency contract deterministic to test.
    /// </summary>
    internal sealed class LatestBuildQueue<T> where T : class
    {
        private readonly IEqualityComparer<T> _comparer;
        private T? _active;
        private T? _pending;

        internal LatestBuildQueue(IEqualityComparer<T>? comparer = null) =>
            _comparer = comparer ?? EqualityComparer<T>.Default;

        internal T? Active => _active;
        internal T? Pending => _pending;

        internal bool Enqueue(T request)
        {
            if ((_active != null && _comparer.Equals(_active, request))
                || (_pending != null && _comparer.Equals(_pending, request)))
                return false;

            _pending = request;
            return true;
        }

        internal T? TryStartNext()
        {
            if (_active != null || _pending == null) return null;
            _active = _pending;
            _pending = null;
            return _active;
        }

        internal void Complete(T request)
        {
            if (!ReferenceEquals(_active, request))
                throw new InvalidOperationException("Only the active geometry build can complete.");
            _active = null;
        }

        internal void ClearPending() => _pending = null;
    }

    private sealed record GeometryBuildRequest(long Epoch, CacheKey Key, GeometryWindow Window,
        PeakStore Peaks, int Channels, double ChannelHeight, double SamplesPerPixel, double AmpZoom);
    private sealed record GeometryBuildResult(CacheKey Key, GeometryWindow Window,
        StreamGeometry[] Peaks, StreamGeometry[] Rms);
    private CacheKey _cacheKey;
    private GeometryWindow _geometryWindow;
    private bool _geometryCacheValid;
    private StreamGeometry[] _peakGeos = [];
    private StreamGeometry[] _rmsGeos = [];
    private readonly LatestBuildQueue<GeometryBuildRequest> _geometryBuildQueue = new();
    private bool _geometryBuildRunning;
    private long _geometryEpoch;

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
        view.InvalidateGeometryCache();
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= view.OnVmChanged;
        if (e.NewValue is DocumentViewModel vm)
        {
            vm.PropertyChanged += view.OnVmChanged;
            vm.ViewWidthPixels = view.ActualWidth;
        }
    }

    private void InvalidateGeometryCache()
    {
        Interlocked.Increment(ref _geometryEpoch);
        _geometryBuildQueue.ClearPending();
        _geometryCacheValid = false;
        _peakGeos = [];
        _rmsGeos = [];
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ViewStart) or nameof(DocumentViewModel.SamplesPerPixel)
            or nameof(DocumentViewModel.SelStart) or nameof(DocumentViewModel.SelEnd)
            or nameof(DocumentViewModel.Cursor) or nameof(DocumentViewModel.PlayheadSample)
            or nameof(DocumentViewModel.PeaksVersion) or nameof(DocumentViewModel.MarkersVersion)
            or nameof(DocumentViewModel.AmpZoom))
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

        double geometryOffsetX = EnsureGeometryCache(vm, w, h, channels, chH, spp, viewStart);

        for (int c = 0; c < channels; c++)
        {
            double mid = c * chH + chH / 2;
            double amp = chH * 0.46 * vm.AmpZoom;

            dc.PushClip(new RectangleGeometry(new Rect(0, c * chH, w, chH)));

            foreach (double levelDb in AmplitudeRuler.MarkerLevelsDb)
            {
                double offset = AmplitudeRuler.MarkerOffset(levelDb, amp);
                if (offset > chH / 2 - 2) continue;
                dc.DrawLine(WaveTheme.GridLine, new Point(0, mid - offset), new Point(w, mid - offset));
                dc.DrawLine(WaveTheme.GridLine, new Point(0, mid + offset), new Point(w, mid + offset));
            }

            if (c < _peakGeos.Length)
            {
                dc.PushTransform(new TranslateTransform(geometryOffsetX, 0));
                dc.DrawGeometry(WaveTheme.WavePeak, null, _peakGeos[c]);
                dc.DrawGeometry(WaveTheme.WaveRms, null, _rmsGeos[c]);
                dc.Pop();
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

    private double EnsureGeometryCache(DocumentViewModel vm, double w, double h, int channels, double chH,
        double spp, double viewStart)
    {
        var key = new CacheKey(spp, w, h, vm.AmpZoom, vm.PeaksVersion, channels, vm.Doc);
        double viewEnd = Math.Min(vm.Doc.Length, viewStart + w * spp);
        if (_geometryCacheValid
            && key == _cacheKey
            && _peakGeos.Length == channels)
        {
            if (GeometryWindowCovers(_geometryWindow, viewStart, viewEnd))
                QueueForwardPrefetchIfNeeded(vm, key, channels, chH, spp, viewStart, viewEnd, w);
            else
                QueueGeometryBuild(CreateScrollBuildRequest(
                    vm, key, channels, chH, spp, viewStart, w,
                    forward: viewStart >= _geometryWindow.StartSample));

            // A scroll miss must never rebuild on the render thread. Keep the old
            // sample-correct geometry translated while the latest window is built;
            // any uncovered edge is briefly blank rather than blocking playback.
            return (_geometryWindow.StartSample - viewStart) / spp;
        }

        if (IsPlaybackActive)
        {
            long currentEpoch = Volatile.Read(ref _geometryEpoch);
            bool matchingBuild = BuildCoversCurrentView(
                    _geometryBuildQueue.Active, currentEpoch, key, viewStart, viewEnd)
                || BuildCoversCurrentView(
                    _geometryBuildQueue.Pending, currentEpoch, key, viewStart, viewEnd);
            if (!matchingBuild)
            {
                long asyncEpoch = Interlocked.Increment(ref _geometryEpoch);
                _geometryBuildQueue.ClearPending();
                _geometryCacheValid = false;
                _peakGeos = [];
                _rmsGeos = [];
                QueueGeometryBuild(new GeometryBuildRequest(asyncEpoch, key,
                    CalculateForwardGeometryWindow(vm.Doc.Length, viewStart, spp, w),
                    vm.Peaks, channels, chH, spp, vm.AmpZoom));
            }

            // Zoom/resize/peak changes during playback also stay off the render
            // thread. A short blank waveform is preferable to stalling transport.
            return 0;
        }

        // A new document, zoom, size, amplitude, or peak version needs an
        // immediately correct first frame. It also invalidates any older worker.
        long epoch = Interlocked.Increment(ref _geometryEpoch);
        _geometryBuildQueue.ClearPending();
        var request = new GeometryBuildRequest(epoch, key,
            CalculateGeometryWindow(vm.Doc.Length, viewStart, spp, w),
            vm.Peaks, channels, chH, spp, vm.AmpZoom);
        ApplyGeometry(BuildGeometry(request));
        QueueForwardPrefetchIfNeeded(vm, key, channels, chH, spp, viewStart, viewEnd, w);

        return (_geometryWindow.StartSample - viewStart) / spp;
    }

    private static bool BuildCoversCurrentView(
        GeometryBuildRequest? request,
        long currentEpoch,
        CacheKey currentKey,
        double viewStart,
        double viewEnd) =>
        request != null
        && ClassifyGeometryBuild(
            request.Epoch,
            currentEpoch,
            request.Key == currentKey,
            request.Window,
            viewStart,
            viewEnd) == GeometryBuildDisposition.Apply;

    private void QueueForwardPrefetchIfNeeded(DocumentViewModel vm, CacheKey key, int channels,
        double chH, double spp, double viewStart, double viewEnd, double viewWidth)
    {
        double visibleSpan = Math.Max(1, viewWidth) * spp;
        double forwardMargin = _geometryWindow.EndSample - viewEnd;
        if (_geometryWindow.EndSample >= vm.Doc.Length - 1e-6
            || forwardMargin > visibleSpan * 0.25)
            return;

        QueueGeometryBuild(CreateScrollBuildRequest(
            vm, key, channels, chH, spp, viewStart, viewWidth, forward: true));
    }

    private GeometryBuildRequest CreateScrollBuildRequest(DocumentViewModel vm, CacheKey key,
        int channels, double chH, double spp, double viewStart, double viewWidth, bool forward)
    {
        GeometryWindow window = forward
            ? CalculateForwardGeometryWindow(vm.Doc.Length, viewStart, spp, viewWidth)
            : CalculateGeometryWindow(vm.Doc.Length, viewStart, spp, viewWidth);
        return new GeometryBuildRequest(Volatile.Read(ref _geometryEpoch), key, window,
            vm.Peaks, channels, chH, spp, vm.AmpZoom);
    }

    private void QueueGeometryBuild(GeometryBuildRequest request)
    {
        if (!_geometryBuildQueue.Enqueue(request)) return;
        if (!_geometryBuildRunning) DrainGeometryBuildQueue();
    }

    private async void DrainGeometryBuildQueue()
    {
        if (_geometryBuildRunning) return;
        _geometryBuildRunning = true;
        try
        {
            while (_geometryBuildQueue.TryStartNext() is { } request)
            {
                try
                {
                    GeometryBuildResult result = await Task.Run(() => BuildGeometry(request));
                    var vm = Document;
                    if (vm == null || vm.Doc.Length == 0 || ActualWidth < 2) continue;
                    int channels = vm.Doc.ChannelCount;
                    double chH = ActualHeight / channels;
                    var currentKey = new CacheKey(vm.SamplesPerPixel, ActualWidth, ActualHeight,
                        vm.AmpZoom, vm.PeaksVersion, channels, vm.Doc);
                    double viewStart = vm.ViewStart;
                    double viewEnd = Math.Min(vm.Doc.Length,
                        viewStart + ActualWidth * vm.SamplesPerPixel);

                    GeometryBuildDisposition disposition = ClassifyGeometryBuild(
                        request.Epoch,
                        Volatile.Read(ref _geometryEpoch),
                        currentKey == result.Key,
                        result.Window,
                        viewStart,
                        viewEnd);
                    if (disposition == GeometryBuildDisposition.Reject) continue;
                    if (disposition == GeometryBuildDisposition.RetryForCurrentView)
                    {
                        _geometryBuildQueue.Enqueue(CreateScrollBuildRequest(
                            vm, currentKey, channels, chH, vm.SamplesPerPixel,
                            viewStart, ActualWidth,
                            forward: viewStart >= result.Window.StartSample));
                        continue;
                    }

                    double visibleSpan = ActualWidth * vm.SamplesPerPixel;
                    if (result.Window.EndSample >= vm.Doc.Length - 1e-6
                        || result.Window.EndSample - viewEnd > visibleSpan * 0.25)
                        _geometryBuildQueue.ClearPending();
                    ApplyGeometry(result);
                    InvalidateVisual();
                }
                catch
                {
                    // Stale/best-effort cache work must not affect the editor.
                }
                finally
                {
                    _geometryBuildQueue.Complete(request);
                }
            }
        }
        finally
        {
            _geometryBuildRunning = false;
            if (_geometryBuildQueue.Pending != null) DrainGeometryBuildQueue();
        }
    }

    private static GeometryBuildResult BuildGeometry(GeometryBuildRequest request)
    {
        var peakGeos = new StreamGeometry[request.Channels];
        var rmsGeos = new StreamGeometry[request.Channels];
        int width = request.Window.PixelWidth;
        var peakTop = new Point[width];
        var peakBot = new Point[width];
        var rmsTop = new Point[width];
        var rmsBot = new Point[width];

        for (int c = 0; c < request.Channels; c++)
        {
            double mid = c * request.ChannelHeight + request.ChannelHeight / 2;
            double amp = request.ChannelHeight * 0.46 * request.AmpZoom;
            for (int x = 0; x < width; x++)
            {
                int s0 = (int)(request.Window.StartSample + x * request.SamplesPerPixel);
                int s1 = Math.Max(s0 + 1,
                    (int)(request.Window.StartSample + (x + 1) * request.SamplesPerPixel));
                request.Peaks.Query(c, s0, s1, out float mn, out float mx, out float rms);
                peakTop[x] = new Point(x, mid - Math.Clamp(mx, -1, 1) * amp);
                peakBot[x] = new Point(x, mid - Math.Clamp(mn, -1, 1) * amp);
                float r = Math.Min(rms, Math.Max(Math.Abs(mn), Math.Abs(mx)));
                rmsTop[x] = new Point(x, mid - r * amp);
                rmsBot[x] = new Point(x, mid + r * amp);
            }
            peakGeos[c] = BuildBand(peakTop, peakBot);
            rmsGeos[c] = BuildBand(rmsTop, rmsBot);
        }

        return new GeometryBuildResult(request.Key, request.Window, peakGeos, rmsGeos);
    }

    private void ApplyGeometry(GeometryBuildResult result)
    {
        _cacheKey = result.Key;
        _geometryWindow = result.Window;
        _peakGeos = result.Peaks;
        _rmsGeos = result.Rms;
        _geometryCacheValid = true;
    }

    internal static GeometryWindow CalculateGeometryWindow(
        int documentLength,
        double viewStart,
        double samplesPerPixel,
        double viewWidth)
    {
        double visibleSpan = Math.Max(1, viewWidth) * samplesPerPixel;
        double cacheSpan = Math.Min(Math.Max(0, documentLength), visibleSpan * 2);
        double maximumStart = Math.Max(0, documentLength - cacheSpan);
        double start = Math.Clamp(viewStart - (cacheSpan - visibleSpan) / 2, 0, maximumStart);
        double end = Math.Min(documentLength, start + cacheSpan);
        int pixels = Math.Max(2, (int)Math.Ceiling((end - start) / samplesPerPixel));
        return new GeometryWindow(start, end, pixels);
    }

    internal static GeometryWindow CalculateForwardGeometryWindow(
        int documentLength,
        double viewStart,
        double samplesPerPixel,
        double viewWidth)
    {
        double visibleSpan = Math.Max(1, viewWidth) * samplesPerPixel;
        double cacheSpan = Math.Min(Math.Max(0, documentLength), visibleSpan * 2);
        double maximumStart = Math.Max(0, documentLength - cacheSpan);
        double start = Math.Clamp(viewStart - visibleSpan * 0.25, 0, maximumStart);
        double end = Math.Min(documentLength, start + cacheSpan);
        int pixels = Math.Max(2, (int)Math.Ceiling((end - start) / samplesPerPixel));
        return new GeometryWindow(start, end, pixels);
    }

    internal static bool GeometryWindowCovers(
        GeometryWindow window,
        double viewStart,
        double viewEnd) =>
        viewStart >= window.StartSample - 1e-6
        && viewEnd <= window.EndSample + 1e-6;

    internal static GeometryBuildDisposition ClassifyGeometryBuild(
        long requestEpoch,
        long currentEpoch,
        bool keyMatches,
        GeometryWindow resultWindow,
        double viewStart,
        double viewEnd)
    {
        if (requestEpoch != currentEpoch || !keyMatches)
            return GeometryBuildDisposition.Reject;
        return GeometryWindowCovers(resultWindow, viewStart, viewEnd)
            ? GeometryBuildDisposition.Apply
            : GeometryBuildDisposition.RetryForCurrentView;
    }

    internal static StreamGeometry BuildBand(Point[] top, Point[] bot)
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

    private bool IsNearPlayhead(Point p)
    {
        var vm = Document;
        if (vm == null || vm.Doc.Length == 0) return false;
        double x = (vm.PlayheadSample - vm.ViewStart) / vm.SamplesPerPixel;
        return x >= 0 && x <= ActualWidth && Math.Abs(p.X - x) <= 8;
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
        if (Document == null) return;
        Focus();
        if (e.ClickCount == 2)
        {
            Document.SelectAll();
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(this);
        if (IsNearPlayhead(point))
        {
            CaptureMouse();
            _draggingPlayhead = true;
            RequestSeek(SampleAt(point), PlayheadSeekPhase.Begin);
            e.Handled = true;
            return;
        }
        CaptureMouse();
        _dragging = true;
        _dragAnchor = SampleAt(point);
        Document.SetCursor(_dragAnchor, clearSelection: true);
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
        if (!_dragging || Document == null) return;
        int s = SampleAt(point);
        if (Math.Abs(s - _dragAnchor) > (int)Document.SamplesPerPixel)
            Document.SetSelection(Math.Min(_dragAnchor, s), Math.Max(_dragAnchor, s));
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

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var vm = Document;
        if (vm == null) return;
        Focus();
        int sample = SampleAt(e.GetPosition(this));
        if (!vm.HasSelection || sample < vm.SelStart || sample >= vm.SelEnd)
            vm.SetCursor(sample, clearSelection: true);
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
            if (IsPlaybackActive)
                vm.ZoomBy(factor, vm.PlayheadSample);
            else
                vm.ZoomAt(e.GetPosition(this).X, factor);
        }
        e.Handled = true;
    }
}
