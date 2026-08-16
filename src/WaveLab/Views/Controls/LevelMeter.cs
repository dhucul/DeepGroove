using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WaveLab.Views.Controls;

/// <summary>Vertical peak/RMS meter, 60 dB range, gradient fill with peak hold.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public const double FloorDb = -60;

    public static readonly DependencyProperty PeakDbProperty = DependencyProperty.Register(
        nameof(PeakDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(FloorDb, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RmsDbProperty = DependencyProperty.Register(
        nameof(RmsDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(FloorDb, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PeakHoldDbProperty = DependencyProperty.Register(
        nameof(PeakHoldDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(FloorDb, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowTargetRangeProperty = DependencyProperty.Register(
        nameof(ShowTargetRange), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TargetMinimumDbProperty = DependencyProperty.Register(
        nameof(TargetMinimumDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-9.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TargetMaximumDbProperty = DependencyProperty.Register(
        nameof(TargetMaximumDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PeakDb { get => (double)GetValue(PeakDbProperty); set => SetValue(PeakDbProperty, value); }
    public double RmsDb { get => (double)GetValue(RmsDbProperty); set => SetValue(RmsDbProperty, value); }
    public double PeakHoldDb { get => (double)GetValue(PeakHoldDbProperty); set => SetValue(PeakHoldDbProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public bool ShowTargetRange { get => (bool)GetValue(ShowTargetRangeProperty); set => SetValue(ShowTargetRangeProperty, value); }
    public double TargetMinimumDb { get => (double)GetValue(TargetMinimumDbProperty); set => SetValue(TargetMinimumDbProperty, value); }
    public double TargetMaximumDb { get => (double)GetValue(TargetMaximumDbProperty); set => SetValue(TargetMaximumDbProperty, value); }

    private static readonly Brush VerticalFill;
    private static readonly Brush HorizontalFill;
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x0F));
    private static readonly Pen Border = new(new SolidColorBrush(Color.FromRgb(0x1D, 0x22, 0x28)), 1);
    private static readonly Brush RmsFill = new SolidColorBrush(Color.FromArgb(0x2A, 0xE7, 0xEA, 0xEE));
    private static readonly Pen HoldPen = new(Brushes.White, 1.5);
    private static readonly Brush TargetFill = new SolidColorBrush(Color.FromArgb(0x24, 0x3F, 0xD6, 0xC2));
    private static readonly Pen TargetPen = new(new SolidColorBrush(Color.FromArgb(0x88, 0x3F, 0xD6, 0xC2)), 1);

    static LevelMeter()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2B, 0xA3, 0x77), 0.0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x3F, 0xD0, 0x7A), 0.55));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xC8, 0xD8, 0x4E), 0.78));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xB4, 0x54), 0.90));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x5C, 0x5C), 1.0));
        g.Freeze();
        VerticalFill = g;

        var horizontal = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (GradientStop stop in g.GradientStops)
            horizontal.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        horizontal.Freeze();
        HorizontalFill = horizontal;

        Bg.Freeze(); Border.Freeze(); RmsFill.Freeze(); HoldPen.Freeze();
        TargetFill.Freeze(); TargetPen.Freeze();
    }

    private static double Frac(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        // Below this the inner rects (w - 2 / h - 2) go negative and Rect's ctor throws
        // from inside the render pass. Nothing legible fits at that size anyway.
        if (w < 4 || h < 4 || double.IsNaN(w) || double.IsNaN(h)) return;

        var rect = new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1));
        dc.DrawRoundedRectangle(Bg, Border, rect, 3, 3);

        if (Orientation == Orientation.Horizontal)
        {
            RenderHorizontal(dc, w, h);
            return;
        }

        double innerWidth = Math.Max(0, w - 2);
        double innerHeight = Math.Max(0, h - 2);

        double peakFrac = Frac(PeakDb);
        if (peakFrac > 0)
        {
            double fh = Math.Max(0, innerHeight * peakFrac);
            dc.PushClip(new RectangleGeometry(new Rect(1, h - 1 - fh, innerWidth, fh)));
            dc.DrawRectangle(VerticalFill, null, new Rect(1, 1, innerWidth, innerHeight));
            dc.Pop();
        }

        double rmsFrac = Frac(RmsDb);
        if (rmsFrac > 0)
        {
            double fh = Math.Max(0, innerHeight * rmsFrac);
            dc.DrawRectangle(RmsFill, null, new Rect(w * 0.25, h - 1 - fh, w * 0.5, fh));
        }

        double holdFrac = Frac(PeakHoldDb);
        if (holdFrac > 0.005)
        {
            double y = h - 1 - innerHeight * holdFrac;
            dc.DrawLine(HoldPen, new Point(1, y), new Point(w - 1, y));
        }
    }

    private void RenderHorizontal(DrawingContext dc, double width, double height)
    {
        double innerWidth = Math.Max(0, width - 2);
        double innerHeight = Math.Max(0, height - 2);

        double peakWidth = innerWidth * Frac(PeakDb);
        if (peakWidth > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(1, 1, peakWidth, innerHeight)));
            dc.DrawRectangle(HorizontalFill, null, new Rect(1, 1, innerWidth, innerHeight));
            dc.Pop();
        }

        double rmsWidth = innerWidth * Frac(RmsDb);
        if (rmsWidth > 0)
            dc.DrawRectangle(RmsFill, null, new Rect(1, height * 0.27, rmsWidth, height * 0.46));

        if (ShowTargetRange)
        {
            double minimum = Math.Min(TargetMinimumDb, TargetMaximumDb);
            double maximum = Math.Max(TargetMinimumDb, TargetMaximumDb);
            double x1 = 1 + innerWidth * Frac(minimum);
            double x2 = 1 + innerWidth * Frac(maximum);
            dc.DrawRectangle(TargetFill, null, new Rect(x1, 1, Math.Max(0, x2 - x1), innerHeight));
            dc.DrawLine(TargetPen, new Point(x1, 1), new Point(x1, height - 1));
            dc.DrawLine(TargetPen, new Point(x2, 1), new Point(x2, height - 1));
        }

        double holdFrac = Frac(PeakHoldDb);
        if (holdFrac > 0.005)
        {
            double x = 1 + innerWidth * holdFrac;
            dc.DrawLine(HoldPen, new Point(x, 1), new Point(x, height - 1));
        }
    }
}
