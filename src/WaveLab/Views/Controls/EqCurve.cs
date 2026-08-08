using System.Windows;
using System.Windows.Media;
using WaveLab.Audio.Dsp;

namespace WaveLab.Views.Controls;

/// <summary>Studio EQ response curve with band node dots. Bind the three gains.</summary>
public sealed class EqCurve : FrameworkElement
{
    private const double Range = 15; // ±dB display

    public static readonly DependencyProperty LowDbProperty = DependencyProperty.Register(
        nameof(LowDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MidDbProperty = DependencyProperty.Register(
        nameof(MidDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighDbProperty = DependencyProperty.Register(
        nameof(HighDb), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double LowDb { get => (double)GetValue(LowDbProperty); set => SetValue(LowDbProperty, value); }
    public double MidDb { get => (double)GetValue(MidDbProperty); set => SetValue(MidDbProperty, value); }
    public double HighDb { get => (double)GetValue(HighDbProperty); set => SetValue(HighDbProperty, value); }

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

        var low = Biquad.LowShelf(fs, StudioEq.LowFreq, LowDb);
        var mid = Biquad.Peaking(fs, StudioEq.MidFreq, StudioEq.MidQ, MidDb);
        var high = Biquad.HighShelf(fs, StudioEq.HighFreq, HighDb);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool first = true;
            for (double x = 0; x <= w; x += 2)
            {
                double f = XToF(x);
                double db = low.MagnitudeDb(f, fs) + mid.MagnitudeDb(f, fs) + high.MagnitudeDb(f, fs);
                var p = new Point(x, DbToY(db));
                if (first) { g.BeginFigure(p, false, false); first = false; }
                else g.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, WaveTheme.AccentPen, geo);

        foreach (var (f, db) in ((double, double)[])[(StudioEq.LowFreq, LowDb), (StudioEq.MidFreq, MidDb), (StudioEq.HighFreq, HighDb)])
        {
            var center = new Point(FToX(f), DbToY(db));
            dc.DrawEllipse(NodeFill, WaveTheme.AccentPen, center, 3.5, 3.5);
        }
    }
}
