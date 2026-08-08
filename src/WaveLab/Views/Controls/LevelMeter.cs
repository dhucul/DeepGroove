using System.Windows;
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

    public double PeakDb { get => (double)GetValue(PeakDbProperty); set => SetValue(PeakDbProperty, value); }
    public double RmsDb { get => (double)GetValue(RmsDbProperty); set => SetValue(RmsDbProperty, value); }
    public double PeakHoldDb { get => (double)GetValue(PeakHoldDbProperty); set => SetValue(PeakHoldDbProperty, value); }

    private static readonly Brush Fill;
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x0F));
    private static readonly Pen Border = new(new SolidColorBrush(Color.FromRgb(0x1D, 0x22, 0x28)), 1);
    private static readonly Brush RmsFill = new SolidColorBrush(Color.FromArgb(0x2A, 0xE7, 0xEA, 0xEE));
    private static readonly Pen HoldPen = new(Brushes.White, 1.5);

    static LevelMeter()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2B, 0xA3, 0x77), 0.0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x3F, 0xD0, 0x7A), 0.55));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xC8, 0xD8, 0x4E), 0.78));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xB4, 0x54), 0.90));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x5C, 0x5C), 1.0));
        g.Freeze();
        Fill = g;
        Bg.Freeze(); Border.Freeze(); RmsFill.Freeze(); HoldPen.Freeze();
    }

    private static double Frac(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var rect = new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1));
        dc.DrawRoundedRectangle(Bg, Border, rect, 3, 3);

        double peakFrac = Frac(PeakDb);
        if (peakFrac > 0)
        {
            double fh = (h - 2) * peakFrac;
            dc.PushClip(new RectangleGeometry(new Rect(1, h - 1 - fh, w - 2, fh)));
            dc.DrawRectangle(Fill, null, new Rect(1, 1, w - 2, h - 2));
            dc.Pop();
        }

        double rmsFrac = Frac(RmsDb);
        if (rmsFrac > 0)
        {
            double fh = (h - 2) * rmsFrac;
            dc.DrawRectangle(RmsFill, null, new Rect(w * 0.25, h - 1 - fh, w * 0.5, fh));
        }

        double holdFrac = Frac(PeakHoldDb);
        if (holdFrac > 0.005)
        {
            double y = h - 1 - (h - 2) * holdFrac;
            dc.DrawLine(HoldPen, new Point(1, y), new Point(w - 1, y));
        }
    }
}
