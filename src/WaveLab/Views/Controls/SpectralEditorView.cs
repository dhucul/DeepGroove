using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaveLab.Audio.Dsp;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>Which channel the spectrogram analyses.</summary>
public enum SpectralChannel { Left, Right, Mid, Side }

/// <summary>
/// The spectrogram as an editing surface: analysed in the background, cached as a bitmap, and
/// selectable in both time and frequency. Distinct from <see cref="SpectrogramView"/>, which is the
/// read-only image in the analysis tab and stays as it is.
/// </summary>
/// <remarks>
/// <para>
/// The bitmap is painted only when the paint key changes and blitted otherwise, exactly as
/// <see cref="WaveformView"/> does — the playhead and the selection are vector overlays on top, so
/// they never trigger a re-analysis. That matters far more here than for the waveform: a screenful
/// of spectrogram costs tens of milliseconds to compute, against a peak-pyramid read of well under
/// one.
/// </para>
/// <para>
/// Because it is that expensive, analysis runs on the thread pool and the previous bitmap stays on
/// screen until the new one is ready. The view can therefore be a frame or two stale after a scroll,
/// which is the same bargain the waveform already makes with its peak rebuilds.
/// </para>
/// </remarks>
public sealed class SpectralEditorView : FrameworkElement
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDocumentChanged));

    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings), typeof(SpectrogramSettings), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectrogramSettings.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageSettingsProperty = DependencyProperty.Register(
        nameof(ImageSettings), typeof(SpectrogramImageSettings), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectrogramImageSettings.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChannelProperty = DependencyProperty.Register(
        nameof(Channel), typeof(SpectralChannel), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectralChannel.Mid, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(SpectralRegion), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectralRegion.None,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public SpectrogramSettings Settings
    {
        get => (SpectrogramSettings)GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    public SpectrogramImageSettings ImageSettings
    {
        get => (SpectrogramImageSettings)GetValue(ImageSettingsProperty);
        set => SetValue(ImageSettingsProperty, value);
    }

    public SpectralChannel Channel
    {
        get => (SpectralChannel)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    /// <summary>The current time-frequency selection.</summary>
    public SpectralRegion Selection
    {
        get => (SpectralRegion)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>Raised when the user finishes dragging out a selection.</summary>
    public event Action<SpectralRegion>? SelectionCommitted;

    /// <summary>
    /// Floor for the analysis hop. Below this a zoomed-in view buys overlap it cannot show, at a
    /// full transform per frame.
    /// </summary>
    internal const int MinimumHop = 64;

    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];
    private int _pixelWidth, _pixelHeight;
    private PaintKey _paintKey;
    private bool _painted;

    private CancellationTokenSource? _analysis;
    private Point? _dragOrigin;
    private SpectralRegion _dragRegion;

    /// <summary>
    /// Everything the bitmap depends on. Anything that changes per frame — the playhead, the
    /// selection — is deliberately absent, or scrubbing would re-analyse on every tick.
    /// </summary>
    private readonly record struct PaintKey(
        double ViewStart, double SamplesPerPixel, int PixelWidth, int PixelHeight,
        int FftSize, int Hop, WindowKind Window, bool Reassign, double FloorDb, double CeilingDb,
        SpectrogramPalette Palette, double MinimumFrequency, double MaximumFrequency,
        bool Logarithmic, double Gamma, SpectralChannel Channel, int PeaksVersion, object? Document);

    public SpectralEditorView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SpectralEditorView)d;
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= view.OnDocumentPropertyChanged;
        if (e.NewValue is DocumentViewModel current) current.PropertyChanged += view.OnDocumentPropertyChanged;
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ViewStart)
            or nameof(DocumentViewModel.SamplesPerPixel)
            or nameof(DocumentViewModel.PeaksVersion)
            or nameof(DocumentViewModel.PlayheadSample))
        {
            RequestRedraw();
        }
    }

    /// <summary>Synchronous on the UI thread, matching the other view-following controls.</summary>
    private void RequestRedraw()
    {
        if (Dispatcher.CheckAccess()) InvalidateVisual();
        else Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(InvalidateVisual));
    }

    // ── coordinate mapping ───────────────────────────────────────

    /// <summary>Sample position under an x coordinate.</summary>
    public double SampleAtX(double x)
    {
        DocumentViewModel? vm = Document;
        return vm == null ? 0 : vm.ViewStart + x * vm.SamplesPerPixel;
    }

    /// <summary>Frequency under a y coordinate.</summary>
    public double FrequencyAtY(double y)
    {
        DocumentViewModel? vm = Document;
        double nyquist = vm == null ? 24_000 : vm.Doc.SampleRate / 2.0;
        return SpectrogramImage.FrequencyForRow(y, Math.Max(1, (int)ActualHeight), ImageSettings, nyquist);
    }

    private double XForSample(double sample)
    {
        DocumentViewModel? vm = Document;
        if (vm == null || vm.SamplesPerPixel <= 0) return 0;
        return (sample - vm.ViewStart) / vm.SamplesPerPixel;
    }

    private double YForFrequency(double frequency)
    {
        DocumentViewModel? vm = Document;
        double nyquist = vm == null ? 24_000 : vm.Doc.SampleRate / 2.0;
        return SpectrogramImage.RowForFrequency(frequency, Math.Max(1, (int)ActualHeight), ImageSettings, nyquist);
    }

    // ── mouse ────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Document is not { Doc.Length: > 0 }) return;

        Focus();
        _dragOrigin = e.GetPosition(this);
        _dragRegion = SpectralRegion.None;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragOrigin is not { } origin) return;

        _dragRegion = RegionBetween(origin, e.GetPosition(this));
        RequestRedraw();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragOrigin is not { } origin) return;

        ReleaseMouseCapture();
        SpectralRegion region = RegionBetween(origin, e.GetPosition(this));
        _dragOrigin = null;
        _dragRegion = SpectralRegion.None;

        // A click without a drag clears the selection rather than leaving a sliver behind.
        Selection = region.IsEmpty ? SpectralRegion.None : region;
        SelectionCommitted?.Invoke(Selection);
        RequestRedraw();
        e.Handled = true;
    }

    private SpectralRegion RegionBetween(Point a, Point b)
    {
        DocumentViewModel? vm = Document;
        if (vm == null) return SpectralRegion.None;

        double sampleA = SampleAtX(a.X), sampleB = SampleAtX(b.X);
        double frequencyA = FrequencyAtY(a.Y), frequencyB = FrequencyAtY(b.Y);

        int start = (int)Math.Clamp(Math.Min(sampleA, sampleB), 0, vm.Doc.Length);
        int end = (int)Math.Clamp(Math.Max(sampleA, sampleB), 0, vm.Doc.Length);
        double low = Math.Min(frequencyA, frequencyB);
        double high = Math.Max(frequencyA, frequencyB);

        // A drag of a pixel or two is a click; treating it as a selection would make an accidental
        // repair a single slip away.
        if (end - start < 2 || high - low < 1) return SpectralRegion.None;
        return new SpectralRegion(start, end, low, high);
    }

    // ── rendering ────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        uint background = SpectrogramImage.Sample(ImageSettings.Palette, 0);
        var backgroundBrush = new SolidColorBrush(Color.FromRgb(
            (byte)((background >> 16) & 0xFF), (byte)((background >> 8) & 0xFF), (byte)(background & 0xFF)));
        backgroundBrush.Freeze();
        dc.DrawRectangle(backgroundBrush, null, new Rect(0, 0, w, h));

        DocumentViewModel? vm = Document;
        if (vm == null || vm.Doc.Length == 0 || w < 2 || h < 2)
        {
            _painted = false;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        if (EnsureBitmap(w, h, dpi))
        {
            var key = BuildKey(vm);
            if (!_painted || key != _paintKey) BeginAnalysis(vm, key);
            if (_painted) dc.DrawImage(_bitmap!, new Rect(0, 0, w, h));
        }

        DrawSelection(dc, _dragRegion.IsEmpty ? Selection : _dragRegion, w, h);
        DrawPlayhead(dc, vm, h);
    }

    private PaintKey BuildKey(DocumentViewModel vm)
    {
        SpectrogramSettings settings = Settings;
        SpectrogramImageSettings image = ImageSettings;
        return new PaintKey(
            vm.ViewStart, vm.SamplesPerPixel, _pixelWidth, _pixelHeight,
            settings.FftSize, settings.Hop, settings.Window, settings.Reassign,
            settings.FloorDb, settings.CeilingDb,
            image.Palette, image.MinimumFrequency, image.MaximumFrequency, image.Logarithmic, image.Gamma,
            Channel, vm.PeaksVersion, vm.Doc);
    }

    private bool EnsureBitmap(double width, double height, DpiScale dpi)
    {
        int pixelWidth = Math.Max(1, (int)Math.Round(width * dpi.DpiScaleX));
        int pixelHeight = Math.Max(1, (int)Math.Round(height * dpi.DpiScaleY));
        if (_bitmap != null && pixelWidth == _pixelWidth && pixelHeight == _pixelHeight) return true;

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _pixels = new uint[pixelWidth * pixelHeight];
        _bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32, null);
        _painted = false;
        return true;
    }

    /// <summary>
    /// Analyses off the UI thread and blits when it lands. An earlier run for a key nobody wants any
    /// more is cancelled rather than left to finish.
    /// </summary>
    private void BeginAnalysis(DocumentViewModel vm, PaintKey key)
    {
        _analysis?.Cancel();
        _analysis?.Dispose();
        var cancellation = new CancellationTokenSource();
        _analysis = cancellation;

        // Channel refs are captured here, on the UI thread: splices replace arrays rather than
        // mutating them, so what the worker reads stays valid however the document is edited.
        float[][] channels = vm.Doc.Channels.ToArray();
        int sampleRate = vm.Doc.SampleRate;
        int width = _pixelWidth, height = _pixelHeight;
        double viewStart = vm.ViewStart, samplesPerPixel = vm.SamplesPerPixel;
        SpectrogramSettings settings = Settings;
        SpectrogramImageSettings image = ImageSettings;
        SpectralChannel channel = Channel;
        CancellationToken token = cancellation.Token;

        Task.Run(() =>
        {
            float[] mono = Mix(channels, channel);
            int from = (int)Math.Max(0, viewStart);
            int count = (int)Math.Min(mono.Length - from, Math.Max(1, width * samplesPerPixel));
            if (count <= 0) return;

            int hop = HopFor(settings, count, width);

            SpectrogramData data = Spectrogram.Analyze(mono, from, count, sampleRate,
                settings with { Hop = hop }, token);

            var pixels = new uint[width * height];
            SpectrogramImage.Render(data, pixels, width, height,
                settings.FloorDb, settings.CeilingDb, image);
            token.ThrowIfCancellationRequested();

            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (token.IsCancellationRequested) return;
                if (pixels.Length != _pixels.Length) return;   // resized while we were working
                pixels.CopyTo(_pixels, 0);
                _bitmap!.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight),
                    _pixels, _pixelWidth * 4, 0);
                _paintKey = key;
                _painted = true;
                InvalidateVisual();
            }));
        }, token);
    }

    /// <summary>
    /// The analysis hop for a view covering <paramref name="count"/> samples in
    /// <paramref name="width"/> pixels: roughly one frame per column.
    /// </summary>
    /// <remarks>
    /// The configured hop is the starting point, not a ceiling. Holding it at 512 however far the
    /// view was zoomed out meant a fit-to-window view of a whole side analysed tens of thousands of
    /// frames to draw a fourteen-hundred-pixel image, and nothing appeared for seconds — the feature
    /// looked broken at exactly the zoom a freshly opened file sits at. Halving and doubling from the
    /// configured hop keeps it a divisor of the transform length, which the analysis requires.
    /// </remarks>
    internal static int HopFor(SpectrogramSettings settings, int count, int width)
    {
        int perColumn = Math.Max(1, count / Math.Max(1, width));
        int hop = settings.Hop;
        while (hop > MinimumHop && hop / 2 >= perColumn) hop /= 2;
        while (hop < settings.FftSize && hop * 2 <= perColumn) hop *= 2;
        return settings.FftSize % hop == 0 ? hop : settings.Hop;
    }

    /// <summary>Reduces the document to the one channel being analysed.</summary>
    internal static float[] Mix(float[][] channels, SpectralChannel channel)
    {
        if (channels.Length == 0) return [];
        if (channels.Length == 1) return channels[0];

        float[] left = channels[0], right = channels[1];
        int n = Math.Min(left.Length, right.Length);
        return channel switch
        {
            SpectralChannel.Left => left,
            SpectralChannel.Right => right,
            SpectralChannel.Side => Combine(left, right, n, -1),
            _ => Combine(left, right, n, 1),
        };

        static float[] Combine(float[] left, float[] right, int n, int sign)
        {
            var result = new float[n];
            for (int i = 0; i < n; i++) result[i] = (left[i] + sign * right[i]) * 0.5f;
            return result;
        }
    }

    private void DrawSelection(DrawingContext dc, SpectralRegion region, double width, double height)
    {
        if (region.IsEmpty) return;

        double x0 = XForSample(region.StartSample), x1 = XForSample(region.EndSample);
        double y0 = YForFrequency(region.HighFrequency), y1 = YForFrequency(region.LowFrequency);
        var rect = new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // The heavier overlay rather than the waveform's SelectionFill: an 8% wash reads clearly
        // over a near-black waveform and disappears over a bright spectrogram.
        dc.DrawRectangle(WaveTheme.SelectionOverlay, WaveTheme.SelectionEdge, rect);

        // Corner handles, so it is obvious the region can be adjusted rather than only redrawn.
        const double handle = 5;
        foreach (Point corner in new[]
                 {
                     rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight,
                 })
        {
            dc.DrawRectangle(WaveTheme.SelectionHandle, null,
                new Rect(corner.X - handle / 2, corner.Y - handle / 2, handle, handle));
        }
    }

    private void DrawPlayhead(DrawingContext dc, DocumentViewModel vm, double height)
    {
        double x = XForSample(vm.PlayheadSample);
        if (x < 0 || x > ActualWidth) return;
        dc.DrawLine(WaveTheme.Playhead, new Point(x, 0), new Point(x, height));
    }
}
