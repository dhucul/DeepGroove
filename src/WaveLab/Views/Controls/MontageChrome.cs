using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Audio.Montage;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>The time scale above the montage lane, in the montage's own clock.</summary>
/// <remarks>
/// A separate control from <c>TimeRuler</c> rather than a reuse of it: that one takes a
/// <c>DocumentViewModel</c> and reads its zoom, and a montage has neither. The tick-choosing rule is
/// the same idea — a step from a fixed ladder, the first that leaves room for its own label.
/// </remarks>
public sealed class MontageRuler : FrameworkElement
{
    public static readonly DependencyProperty MontageProperty = DependencyProperty.Register(
        nameof(Montage), typeof(MontageViewModel), typeof(MontageRuler),
        new FrameworkPropertyMetadata(null, OnMontageChanged));

    public MontageViewModel? Montage
    {
        get => (MontageViewModel?)GetValue(MontageProperty);
        set => SetValue(MontageProperty, value);
    }

    private static void OnMontageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MontageRuler)d;
        if (e.OldValue is MontageViewModel old) old.PropertyChanged -= view.OnChanged;
        if (e.NewValue is MontageViewModel now) now.PropertyChanged += view.OnChanged;
        view.InvalidateVisual();
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        InvalidateVisual();

    /// <summary>Seconds per division, coarsening as the view widens.</summary>
    private static readonly double[] Steps =
        [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800];

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || Montage is not { } vm) return;

        dc.DrawRectangle(WaveTheme.PanelBg, null, new Rect(0, 0, w, h));
        int rate = vm.Montage.SampleRate;
        if (rate <= 0) return;

        double dpi = 96;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            dpi = 96 * target.TransformToDevice.M22;

        double secondsPerPixel = vm.SamplesPerPixel / rate;
        double step = Steps[^1];
        foreach (double candidate in Steps)
        {
            // 62 px is about the width of "10:00.000" with room either side; the first step that
            // clears it is the finest one whose labels will not collide.
            if (candidate / secondsPerPixel >= 62) { step = candidate; break; }
        }

        double firstSecond = Math.Floor(vm.ViewStart / rate / step) * step;
        for (double t = firstSecond; ; t += step)
        {
            double x = Math.Round(vm.PixelOf(t * rate)) + 0.5;
            if (x > w + 40) break;
            if (x < -40) continue;

            dc.DrawLine(WaveTheme.TickPen, new Point(x, h - 6), new Point(x, h));
            FormattedText label = WaveTheme.Text(Format(t, step), WaveTheme.MonoFace, 9,
                WaveTheme.TextFaint, dpi);
            dc.DrawText(label, new Point(x + 4, 2));
        }
    }

    private static string Format(double seconds, double step)
    {
        if (seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return step < 1
            ? span.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture)
            : span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The whole montage at a glance, with the lane's window drawn on it and draggable.
/// </summary>
public sealed class MontageOverview : FrameworkElement
{
    public static readonly DependencyProperty MontageProperty = DependencyProperty.Register(
        nameof(Montage), typeof(MontageViewModel), typeof(MontageOverview),
        new FrameworkPropertyMetadata(null, OnMontageChanged));

    public MontageViewModel? Montage
    {
        get => (MontageViewModel?)GetValue(MontageProperty);
        set => SetValue(MontageProperty, value);
    }

    private bool _dragging;

    public MontageOverview() => ClipToBounds = true;

    private static void OnMontageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MontageOverview)d;
        if (e.OldValue is MontageViewModel old) old.PropertyChanged -= view.OnChanged;
        if (e.NewValue is MontageViewModel now) now.PropertyChanged += view.OnChanged;
        view.InvalidateVisual();
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || Montage is not { } vm) return;

        dc.DrawRectangle(WaveTheme.OverviewBg, null, new Rect(0, 0, w, h));
        int length = vm.Montage.Length;
        if (length <= 0) return;

        double scale = w / length;
        foreach (MontageClip clip in vm.Montage.Clips)
        {
            double x0 = clip.TimelineStart * scale;
            double x1 = clip.TimelineEnd * scale;
            var block = new Rect(x0, 5, Math.Max(1, x1 - x0), h - 10);

            Brush fill = MontageLaneView.OverviewFill(clip.SourceIndex);
            dc.DrawRoundedRectangle(fill, null, block, 2, 2);
        }

        // The lane's window on all of it.
        double left = vm.ViewStart * scale;
        double right = (vm.ViewStart + vm.ViewWidthPixels * vm.SamplesPerPixel) * scale;
        dc.DrawRectangle(WaveTheme.ViewportFill, WaveTheme.ViewportPen,
            new Rect(Math.Max(0, left), 1.5, Math.Max(2, Math.Min(w, right) - Math.Max(0, left)), h - 3));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        ScrollTo(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) ScrollTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void ScrollTo(double x)
    {
        if (Montage is not { } vm || ActualWidth <= 0 || vm.Montage.Length <= 0) return;
        double centre = x / ActualWidth * vm.Montage.Length;
        vm.ViewStart = centre - vm.ViewWidthPixels * vm.SamplesPerPixel / 2;
    }
}

/// <summary>
/// The pair of gains a crossfade will use, and the level they hold between them.
/// </summary>
/// <remarks>
/// The dashed line is the summed level, and it is the reason the panel exists: it is flat because
/// the law was solved from the measured correlation, and drawing it is the difference between
/// claiming that and showing it.
/// </remarks>
public sealed class CrossfadeCurveView : FrameworkElement
{
    public static readonly DependencyProperty CorrelationProperty = DependencyProperty.Register(
        nameof(Correlation), typeof(double), typeof(CrossfadeCurveView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(FadeShape), typeof(CrossfadeCurveView),
        new FrameworkPropertyMetadata(FadeShape.EqualPower, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Correlation
    {
        get => (double)GetValue(CorrelationProperty);
        set => SetValue(CorrelationProperty, value);
    }

    public FadeShape Shape
    {
        get => (FadeShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    private static readonly Pen OutgoingPen =
        FreezePen(new Pen(new SolidColorBrush(WaveTheme.Amber), 1.5));
    private static readonly Pen IncomingPen =
        FreezePen(new Pen(new SolidColorBrush(WaveTheme.Accent), 1.5));
    private static readonly Pen LevelPen =
        FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x73, 0xE7, 0xEA, 0xEE)), 1)
        { DashStyle = new DashStyle([3, 3], 0) });

    private static Pen FreezePen(Pen pen) { pen.Freeze(); return pen; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        double rho = Math.Clamp(Correlation, 0, 1);
        int steps = Math.Max(8, (int)w);

        var outgoing = new StreamGeometry();
        var incoming = new StreamGeometry();
        var level = new StreamGeometry();

        Trace(outgoing, steps, w, h, t => Crossfade.Partner(Fades.In(Shape, t), rho));
        Trace(incoming, steps, w, h, t => Fades.In(Shape, t));
        Trace(level, steps, w, h, t =>
        {
            double a = Fades.In(Shape, t);
            double b = Crossfade.Partner(a, rho);
            return Math.Sqrt(Math.Max(0, a * a + b * b + 2 * a * b * rho));
        });

        dc.DrawGeometry(null, LevelPen, level);
        dc.DrawGeometry(null, OutgoingPen, outgoing);
        dc.DrawGeometry(null, IncomingPen, incoming);
    }

    private static void Trace(StreamGeometry geometry, int steps, double w, double h,
        Func<double, double> gain)
    {
        using StreamGeometryContext context = geometry.Open();
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double y = h - 3 - Math.Clamp(gain(t), 0, 1.5) * (h - 6);
            var point = new Point(t * w, y);
            if (i == 0) context.BeginFigure(point, false, false);
            else context.LineTo(point, true, false);
        }
        geometry.Freeze();
    }
}
