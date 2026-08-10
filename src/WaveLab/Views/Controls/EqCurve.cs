using System.Windows;
using System.Windows.Media;
using WaveLab.Audio.Dsp;

namespace WaveLab.Views.Controls;

/// <summary>Parametric EQ response curve with band node dots. Bind the five band gains.</summary>
public sealed class EqCurve : FrameworkElement
{
    private const double Range = 15; // ±dB display

    public static readonly DependencyProperty LowGainDbProperty = DependencyProperty.Register(
        nameof(LowGainDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LowMidGainDbProperty = DependencyProperty.Register(
        nameof(LowMidGainDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MidGainDbProperty = DependencyProperty.Register(
        nameof(MidGainDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighMidGainDbProperty = DependencyProperty.Register(
        nameof(HighMidGainDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighGainDbProperty = DependencyProperty.Register(
        nameof(HighGainDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double LowGainDb { get => (double)GetValue(LowGainDbProperty); set => SetValue(LowGainDbProperty, value); }
    public double LowMidGainDb { get => (double)GetValue(LowMidGainDbProperty); set => SetValue(LowMidGainDbProperty, value); }
    public double MidGainDb { get => (double)GetValue(MidGainDbProperty); set => SetValue(MidGainDbProperty, value); }
    public double HighMidGainDb { get => (double)GetValue(HighMidGainDbProperty); set => SetValue(HighMidGainDbProperty, value); }
    public double HighGainDb { get => (double)GetValue(HighGainDbProperty); set => SetValue(HighGainDbProperty, value); }

    private static readonly Brush NodeFill = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x12));

    static EqCurve() { NodeFill.Freeze(); }

    public EqCurve() { ClipToBounds = true; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.OverviewBg, null, new Rect(0, 0, w, h));
        if (w < 10 || h < 10) return;

        const int fs = 48000;
        const double fMin = 20, fMax = 20000;
        double XToF(double x) => fMin * Math.Pow(fMax / fMin, x / w);
        double FToX(double f) => Math.Log10(f / fMin) / Math.Log10(fMax / fMin) * w;
        double DbToY(double db) => h / 2 - db / Range * (h / 2);

        foreach (var f in (double[])[100, 1000, 10000])
            dc.DrawLine(WaveTheme.GridLine, new Point(FToX(f), 0), new Point(FToX(f), h));
        dc.DrawLine(WaveTheme.CenterLine, new Point(0, h / 2), new Point(w, h / 2));

        // 5-band parametric EQ: low shelf 80Hz, low-mid 250Hz, mid 650Hz, high-mid 2500Hz, high shelf 8kHz
        var low = Biquad.LowShelf(fs, 80, LowGainDb);
        var lm = Biquad.Peaking(fs, 250, 1.0, LowMidGainDb);
        var mid = Biquad.Peaking(fs, 650, 1.0, MidGainDb);
        var hm = Biquad.Peaking(fs, 2500, 1.0, HighMidGainDb);
        var high = Biquad.HighShelf(fs, 8000, HighGainDb);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool first = true;
            for (double x = 0; x <= w; x += 2)
            {
                double f = XToF(x);
                double db = low.MagnitudeDb(f, fs) + lm.MagnitudeDb(f, fs) + mid.MagnitudeDb(f, fs)
                          + hm.MagnitudeDb(f, fs) + high.MagnitudeDb(f, fs);
                var p = new Point(x, DbToY(db));
                if (first) { g.BeginFigure(p, false, false); first = false; }
                else g.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, WaveTheme.AccentPen, geo);

        foreach (var (f, db) in ((double, double)[])[(80, LowGainDb), (250, LowMidGainDb), (650, MidGainDb), (2500, HighMidGainDb), (8000, HighGainDb)])
        {
            var center = new Point(FToX(f), DbToY(db));
            dc.DrawEllipse(NodeFill, WaveTheme.AccentPen, center, 3.5, 3.5);
        }
    }
}