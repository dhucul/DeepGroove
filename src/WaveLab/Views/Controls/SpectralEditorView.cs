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

    /// <summary>
    /// Which analysis and axis the picture is made with. Design: docs/design/constant_q.png.
    /// </summary>
    /// <remarks>
    /// One control for three choices, two of which share an analysis. The user is choosing how the
    /// picture is made; that LINEAR and LOG differ only in the axis while CONSTANT-Q is a different
    /// transform is this app's business, not theirs.
    /// </remarks>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale), typeof(SpectralFrequencyScale), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectralFrequencyScale.Logarithmic,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BinsPerOctaveProperty = DependencyProperty.Register(
        nameof(BinsPerOctave), typeof(int), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(36, FrameworkPropertyMetadataOptions.AffectsRender));

    public SpectralFrequencyScale Scale
    {
        get => (SpectralFrequencyScale)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public int BinsPerOctave
    {
        get => (int)GetValue(BinsPerOctaveProperty);
        set => SetValue(BinsPerOctaveProperty, value);
    }

    /// <summary>
    /// The image settings the scale implies. Constant-Q bins are geometric, so its axis has to be
    /// logarithmic — offering the combination of a constant-Q analysis on a linear axis would be
    /// offering a picture with most of its height empty.
    /// </summary>
    private SpectrogramImageSettings EffectiveImageSettings =>
        ImageSettings with { Logarithmic = Scale != SpectralFrequencyScale.Linear };

    public static readonly DependencyProperty ChannelProperty = DependencyProperty.Register(
        nameof(Channel), typeof(SpectralChannel), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectralChannel.Mid, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(SpectralSelection), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectralSelection.None,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectionChanged));

    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool), typeof(SpectralTool), typeof(SpectralEditorView),
        new FrameworkPropertyMetadata(SpectralTool.Rectangle));

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
    public SpectralSelection Selection
    {
        get => (SpectralSelection)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>Which tool the next gesture uses.</summary>
    public SpectralTool Tool
    {
        get => (SpectralTool)GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <summary>Raised when the user finishes drawing a selection.</summary>
    public event Action<SpectralSelection>? SelectionCommitted;

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

    /// <summary>How much audio the wand analyses around the click, in seconds.</summary>
    private const int WandWindowSeconds = 6;

    /// <summary>
    /// How far a drag must travel to count as one. Measured in pixels, because that is what the
    /// user's hand controls — a guard in samples means something different at every zoom, and at a
    /// hundred-odd samples per pixel a single-pixel slip would clear it and select a region.
    /// </summary>
    private const double MinimumDragPixels = 3;

    private CancellationTokenSource? _analysis;
    private Point? _dragOrigin;
    private readonly List<Point> _dragPoints = [];

    private Geometry? _selectionGeometry;
    private object? _geometryKey;

    /// <summary>
    /// Everything the bitmap depends on. Anything that changes per frame — the playhead, the
    /// selection — is deliberately absent, or scrubbing would re-analyse on every tick.
    /// </summary>
    private readonly record struct PaintKey(
        double ViewStart, double SamplesPerPixel, int PixelWidth, int PixelHeight,
        int FftSize, int Hop, WindowKind Window, bool Reassign, double FloorDb, double CeilingDb,
        SpectrogramPalette Palette, double MinimumFrequency, double MaximumFrequency,
        bool Logarithmic, double Gamma, SpectralChannel Channel, int PeaksVersion, object? Document,
        SpectralFrequencyScale Scale, int BinsPerOctave);

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

    /// <summary>Analysis frame under an x coordinate, in the repair grid.</summary>
    private double FrameAtX(double x) => SampleAtX(x) / Math.Max(1, Settings.Hop);

    /// <summary>Analysis bin under a y coordinate.</summary>
    private double BinAtY(double y)
    {
        DocumentViewModel? vm = Document;
        int rate = vm?.Doc.SampleRate ?? 48_000;
        return FrequencyAtY(y) * Settings.FftSize / rate;
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpectralEditorView)d)._geometryKey = null;

    // ── mouse ────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Document is not { Doc.Length: > 0 }) return;

        Focus();
        Point position = e.GetPosition(this);
        e.Handled = true;

        // The wand takes a single click; there is nothing to drag out.
        if (Tool == SpectralTool.MagicWand)
        {
            Commit(GrowFrom(position));
            return;
        }

        _dragOrigin = position;
        _dragPoints.Clear();
        _dragPoints.Add(position);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragOrigin is null) return;

        Point position = e.GetPosition(this);
        if (Tool == SpectralTool.Lasso)
        {
            // Thin the trail: a freehand drag delivers far more points than the outline needs, and
            // the point-in-polygon test is linear in them for every cell it fills.
            Point last = _dragPoints[^1];
            if (Math.Abs(position.X - last.X) + Math.Abs(position.Y - last.Y) >= 3)
                _dragPoints.Add(position);
        }
        else
        {
            if (_dragPoints.Count > 1) _dragPoints.RemoveRange(1, _dragPoints.Count - 1);
            _dragPoints.Add(position);
        }

        RequestRedraw();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragOrigin is not { } origin) return;

        ReleaseMouseCapture();
        Point end = e.GetPosition(this);
        SpectralTool tool = Tool;
        var points = new List<Point>(_dragPoints);
        _dragOrigin = null;
        _dragPoints.Clear();

        Commit(SelectionFor(tool, origin, end, points));
        e.Handled = true;
    }

    private SpectralSelection? SelectionFor(SpectralTool tool, Point from, Point to,
        IReadOnlyList<Point> points) => tool switch
    {
        SpectralTool.Lasso => LassoFrom(points),
        SpectralTool.MagicWand => GrowFrom(from),
        SpectralTool.Harmonic => HarmonicFrom(from, to),
        _ => RectangleFrom(from, to),
    };

    /// <summary>
    /// Runs one complete gesture and returns what it selected. The mouse handlers are thin wrappers
    /// over the same call, so driving this exercises the path a real drag takes rather than a
    /// parallel copy of it — which is the only way to cover the gestures without a live mouse.
    /// </summary>
    internal SpectralSelection PerformGesture(Point from, Point to, IReadOnlyList<Point>? path = null)
    {
        Commit(SelectionFor(Tool, from, to, path ?? [from, to]));
        return Selection;
    }

    private void Commit(SpectralSelection? selection)
    {
        // Anything too small to be deliberate clears the selection rather than leaving a sliver
        // behind, which would make an accidental repair a single slip away.
        Selection = selection is { IsEmpty: false } ? selection : SpectralSelection.None;
        SelectionCommitted?.Invoke(Selection);
        RequestRedraw();
    }

    // ── the tools ────────────────────────────────────────────────

    private SpectralSelection? RectangleFrom(Point a, Point b)
    {
        DocumentViewModel? vm = Document;
        if (vm == null) return null;

        if (Math.Abs(b.X - a.X) < MinimumDragPixels || Math.Abs(b.Y - a.Y) < MinimumDragPixels) return null;

        int start = (int)Math.Clamp(Math.Min(SampleAtX(a.X), SampleAtX(b.X)), 0, vm.Doc.Length);
        int end = (int)Math.Clamp(Math.Max(SampleAtX(a.X), SampleAtX(b.X)), 0, vm.Doc.Length);
        double low = Math.Min(FrequencyAtY(a.Y), FrequencyAtY(b.Y));
        double high = Math.Max(FrequencyAtY(a.Y), FrequencyAtY(b.Y));
        if (end - start < 2 || high - low < 1) return null;

        SpectrogramSettings settings = Settings;
        return Wrap(vm, SpectralMask.ForRegion(start, end, low, high,
            vm.Doc.SampleRate, settings.FftSize, settings.Hop));
    }

    private SpectralSelection? LassoFrom(IReadOnlyList<Point> points)
    {
        DocumentViewModel? vm = Document;
        if (vm == null || points.Count < 3) return null;

        var outline = new List<(double Frame, double Bin)>(points.Count);
        foreach (Point point in points) outline.Add((FrameAtX(point.X), BinAtY(point.Y)));

        return Wrap(vm, SpectralMask.Lasso(outline));
    }

    /// <summary>
    /// The comb the drag describes: the fundamental is the frequency the drag started on, and the
    /// span is how far it travelled. A buzz is not a region of the plane, so selecting it as one
    /// would take the music between the teeth as well.
    /// </summary>
    private SpectralSelection? HarmonicFrom(Point a, Point b)
    {
        DocumentViewModel? vm = Document;
        if (vm == null) return null;

        // Only horizontal travel counts: the vertical position is picking the fundamental, not
        // describing a band, so a drag straight along a partial is a legitimate selection.
        if (Math.Abs(b.X - a.X) < MinimumDragPixels) return null;

        double fundamental = FrequencyAtY(a.Y);
        int start = (int)Math.Clamp(Math.Min(SampleAtX(a.X), SampleAtX(b.X)), 0, vm.Doc.Length);
        int end = (int)Math.Clamp(Math.Max(SampleAtX(a.X), SampleAtX(b.X)), 0, vm.Doc.Length);
        if (end - start < 2 || !(fundamental > 0)) return null;

        SpectrogramSettings settings = Settings;
        return Wrap(vm, SpectralMask.Harmonic(
            settings.FftSize / 2 + 1, settings.FftSize, vm.Doc.SampleRate,
            start / settings.Hop, end / settings.Hop + 1, fundamental));
    }

    /// <summary>
    /// Grows a region from the clicked cell through connected energy — the tool for a cough or a
    /// thump, whose edges are wherever its energy stops rather than anywhere the user can see to
    /// draw.
    /// </summary>
    /// <remarks>
    /// This analyses its own small window rather than reusing the display's. The display's grid moves
    /// with the zoom, so a mask grown in it would have to be renumbered to be repairable at all; and
    /// the display is reassigned, which scatters a noise floor into isolated points and would break
    /// the connectivity the growth depends on. The window is bounded because the cost is what makes
    /// this safe to run on a click, with no progress to report.
    /// </remarks>
    private SpectralSelection? GrowFrom(Point position)
    {
        DocumentViewModel? vm = Document;
        if (vm == null || vm.Doc.Channels.Count == 0) return null;

        SpectrogramSettings settings = Settings;
        int hop = settings.Hop;
        int rate = vm.Doc.SampleRate;
        int seedFrame = (int)Math.Round(SampleAtX(position.X) / hop);
        int seedBin = (int)Math.Round(BinAtY(position.Y));

        int radius = Math.Max(8, WandWindowSeconds * rate / hop / 2);
        int firstFrame = Math.Max(0, seedFrame - radius);
        int from = firstFrame * hop;
        int count = Math.Min(vm.Doc.Length - from, (2 * radius + 1) * hop);
        if (count <= 0) return null;

        float[] mono = Mix(vm.Doc.Channels.ToArray(), Channel);
        SpectrogramData data = Spectrogram.Analyze(mono, from, count, rate,
            settings with { Hop = hop, Reassign = false });

        SpectralMask grown = SpectralMask.MagicWand(data, seedFrame - firstFrame, seedBin);
        return Wrap(vm, grown.Shifted(firstFrame));
    }

    private SpectralSelection Wrap(DocumentViewModel vm, SpectralMask mask)
    {
        SpectrogramSettings settings = Settings;
        return new SpectralSelection(Tool, mask, vm.Doc.SampleRate, settings.FftSize, settings.Hop);
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

        if (_dragOrigin is null) DrawSelection(dc, Selection);
        else DrawDrag(dc, h);
        DrawPlayhead(dc, vm, h);
    }

    private PaintKey BuildKey(DocumentViewModel vm)
    {
        SpectrogramSettings settings = Settings;
        SpectrogramImageSettings image = EffectiveImageSettings;
        return new PaintKey(
            vm.ViewStart, vm.SamplesPerPixel, _pixelWidth, _pixelHeight,
            settings.FftSize, settings.Hop, settings.Window, settings.Reassign,
            settings.FloorDb, settings.CeilingDb,
            image.Palette, image.MinimumFrequency, image.MaximumFrequency, image.Logarithmic, image.Gamma,
            Channel, vm.PeaksVersion, vm.Doc, Scale, BinsPerOctave);
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
        SpectrogramImageSettings image = EffectiveImageSettings;
        SpectralChannel channel = Channel;
        SpectralFrequencyScale scale = Scale;
        int perOctave = BinsPerOctave;
        CancellationToken token = cancellation.Token;

        Task.Run(() =>
        {
            float[] mono = Mix(channels, channel);
            int from = (int)Math.Max(0, viewStart);
            int count = (int)Math.Min(mono.Length - from, Math.Max(1, width * samplesPerPixel));
            if (count <= 0) return;

            int hop = HopFor(settings, count, width);

            // Only the picture changes. The magic wand and every repair keep analysing linearly,
            // because the mask lives in the repair grid rather than in whatever the display was
            // drawn from — which is precisely what lets the display change without the edit changing.
            SpectrogramData data = scale == SpectralFrequencyScale.ConstantQ
                ? ConstantQ.Analyze(mono, from, count, sampleRate, new ConstantQSettings(
                    BinsPerOctave: perOctave,
                    MinimumFrequency: image.MinimumFrequency,
                    MaximumFrequency: image.MaximumFrequency,
                    MaximumWindow: ConstantQSettings.Default.MaximumWindow,
                    Hop: hop), token)
                : Spectrogram.Analyze(mono, from, count, sampleRate,
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

    /// <summary>
    /// Draws the selection from the mask itself rather than from the shape that produced it, so what
    /// is on screen is what will actually be repaired — including the feathered rim, which is real
    /// and which a tidied outline would hide.
    /// </summary>
    private void DrawSelection(DrawingContext dc, SpectralSelection selection)
    {
        if (selection.IsEmpty) return;

        DocumentViewModel? vm = Document;
        if (vm == null) return;

        var key = (selection, vm.ViewStart, vm.SamplesPerPixel, ActualWidth, ActualHeight,
            ImageSettings, Settings.FftSize, Settings.Hop);
        if (!key.Equals(_geometryKey) || _selectionGeometry == null)
        {
            _selectionGeometry = BuildSelectionGeometry(selection);
            _geometryKey = key;
        }

        // Dim the surround rather than tint the selection. The geometry is one figure per cell run,
        // so stroking it outlines every run rather than the region — thousands of edges a pixel
        // apart, painting the selection solid — while a tint light enough to keep the detail
        // readable vanishes against the bright end of the colour ramp. Scrimming the outside reads
        // over every colour and leaves the audio being repaired untouched.
        dc.DrawGeometry(WaveTheme.SelectionScrim, null, _selectionGeometry);

        // Only a rectangle gets an edge and handles, because only for a rectangle are the bounds the
        // shape. Outlining a lasso or a wand blob with its bounding box would claim it had selected
        // audio it had not.
        if (selection.Tool != SpectralTool.Rectangle) return;

        // Taken from the selection's own extent, not the geometry's — the geometry is now the
        // scrimmed surround, whose bounds are the whole pane.
        SpectralRegion extent = selection.Bounds;
        double x0 = XForSample(extent.StartSample), x1 = XForSample(extent.EndSample);
        double y0 = YForFrequency(extent.HighFrequency), y1 = YForFrequency(extent.LowFrequency);
        if (!double.IsFinite(y0) || !double.IsFinite(y1)) return;

        var bounds = new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        dc.DrawRectangle(null, WaveTheme.SelectionEdge, bounds);
        const double handle = 5;
        foreach (Point corner in new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight })
        {
            dc.DrawRectangle(WaveTheme.SelectionHandle, null,
                new Rect(corner.X - handle / 2, corner.Y - handle / 2, handle, handle));
        }
    }

    /// <summary>
    /// Everything the selection does <em>not</em> cover, as one even-odd figure set: the whole pane,
    /// then a hole for each covered run. The runs tile without overlapping, so a point inside one
    /// crosses two boundaries and is left unfilled, while a point outside them all crosses one and
    /// is scrimmed.
    /// </summary>
    private Geometry BuildSelectionGeometry(SpectralSelection selection)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(ActualWidth, 0), false, false);
            context.LineTo(new Point(ActualWidth, ActualHeight), false, false);
            context.LineTo(new Point(0, ActualHeight), false, false);

            double hop = Math.Max(1, selection.Hop);
            foreach (var (frame, fromBin, toBin) in selection.Runs())
            {
                // A cell spans half a hop either side of its centre, and half a bin above and below.
                double x0 = XForSample((frame - 0.5) * hop);
                double x1 = XForSample((frame + 0.5) * hop);
                double y0 = YForFrequency(selection.FrequencyAt(toBin) + 0.5 * selection.FrequencyAt(1));
                double y1 = YForFrequency(Math.Max(1, selection.FrequencyAt(fromBin) - 0.5 * selection.FrequencyAt(1)));
                if (!double.IsFinite(y0) || !double.IsFinite(y1)) continue;
                if (x1 < 0 || x0 > ActualWidth) continue;

                // Clamped to the pane: zoomed in, a single cell can be thousands of pixels wide, and
                // the scrim only has to be right where it is visible.
                x0 = Math.Max(x0, -1);
                x1 = Math.Min(x1, ActualWidth + 1);

                context.BeginFigure(new Point(x0, y0), isFilled: true, isClosed: true);
                context.LineTo(new Point(x1, y0), false, false);
                context.LineTo(new Point(x1, y1), false, false);
                context.LineTo(new Point(x0, y1), false, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The shape being dragged out, before it becomes a mask.</summary>
    private void DrawDrag(DrawingContext dc, double height)
    {
        if (_dragOrigin is not { } origin || _dragPoints.Count < 2) return;
        Point current = _dragPoints[^1];

        switch (Tool)
        {
            case SpectralTool.Lasso:
            {
                var geometry = new StreamGeometry();
                using (StreamGeometryContext context = geometry.Open())
                {
                    context.BeginFigure(_dragPoints[0], isFilled: true, isClosed: true);
                    for (int i = 1; i < _dragPoints.Count; i++)
                        context.LineTo(_dragPoints[i], true, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(WaveTheme.SelectionOverlay, WaveTheme.SelectionEdge, geometry);
                break;
            }

            case SpectralTool.Harmonic:
            {
                // Preview the comb, not a box: the fundamental is where the drag began.
                double x0 = Math.Min(origin.X, current.X), x1 = Math.Max(origin.X, current.X);
                double fundamental = FrequencyAtY(origin.Y);
                if (!(fundamental > 0)) return;
                DocumentViewModel? vm = Document;
                double nyquist = (vm?.Doc.SampleRate ?? 48_000) / 2.0;
                for (int n = 1; n <= 12 && fundamental * n < nyquist; n++)
                {
                    double y = YForFrequency(fundamental * n);
                    if (!double.IsFinite(y)) continue;
                    dc.DrawRectangle(WaveTheme.SelectionOverlay, WaveTheme.SelectionEdge,
                        new Rect(x0, y - 3, Math.Max(1, x1 - x0), 6));
                }
                break;
            }

            default:
            {
                var rect = new Rect(
                    Math.Min(origin.X, current.X), Math.Min(origin.Y, current.Y),
                    Math.Abs(current.X - origin.X), Math.Abs(current.Y - origin.Y));
                if (rect.Width <= 0 || rect.Height <= 0) return;
                dc.DrawRectangle(WaveTheme.SelectionOverlay, WaveTheme.SelectionEdge, rect);
                break;
            }
        }
    }

    private void DrawPlayhead(DrawingContext dc, DocumentViewModel vm, double height)
    {
        double x = XForSample(vm.PlayheadSample);
        if (x < 0 || x > ActualWidth) return;
        dc.DrawLine(WaveTheme.Playhead, new Point(x, 0), new Point(x, height));
    }
}
