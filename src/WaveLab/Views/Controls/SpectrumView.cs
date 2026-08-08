using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;

namespace WaveLab.Views.Controls;

/// <summary>
/// Real-time spectrum analyzer: 4096-point Hann FFT over the master section's ring buffer,
/// log frequency axis 20 Hz–20 kHz, smoothed fill with amber peak-hold trace.
/// </summary>
public sealed class SpectrumView : FrameworkElement
{
    private const int FftSize = 4096;
    private const double DbFloor = -84, FMin = 20;

    private readonly DispatcherTimer _timer;
    private readonly float[] _samples = new float[FftSize];
    private readonly float[] _window = Fft.HannWindow(FftSize);
    private readonly float[] _magDb = new float[FftSize / 2];
    private readonly float[] _smooth = new float[FftSize / 2];
    private readonly float[] _hold = new float[FftSize / 2];
    private static readonly Brush FillBrush;

    public MasterSection? Tap { get; set; }

    static SpectrumView()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, 0x3F, 0xD6, 0xC2), 0.0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0x29, 0x3F, 0xD6, 0xC2), 0.6));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0x08, 0x3F, 0xD6, 0xC2), 1.0));
        g.Freeze();
        FillBrush = g;
    }

    public SpectrumView()
    {
        ClipToBounds = true;
        for (int i = 0; i < _smooth.Length; i++) { _smooth[i] = (float)DbFloor; _hold[i] = (float)DbFloor; }
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Update()
    {
        if (Tap == null || !IsVisible) return;
        Tap.CopyLatest(_samples);
        Fft.MagnitudeDb(_samples, _window, _magDb);
        for (int i = 0; i < _magDb.Length; i++)
        {
            float v = Math.Max((float)DbFloor, _magDb[i]);
            _smooth[i] = v > _smooth[i] ? 0.6f * _smooth[i] + 0.4f * v : 0.90f * _smooth[i] + 0.10f * v;
            _hold[i] = Math.Max(v, _hold[i] - 0.25f);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.SpectrumBg, null, new Rect(0, 0, w, h));
        if (w < 10 || h < 10) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int sr = Tap?.WaveFormat.SampleRate ?? 48000;
        double fMax = Math.Min(20000, sr / 2.0);
        double FreqToX(double f) => Math.Log10(f / FMin) / Math.Log10(fMax / FMin) * w;
        double DbToY(double db) => -db / -DbFloor * h;

        foreach (var (f, label) in ((double, string)[])
                 [(31.5, "31"), (63, "63"), (125, "125"), (250, "250"), (500, "500"),
                  (1000, "1k"), (2000, "2k"), (4000, "4k"), (8000, "8k"), (16000, "16k")])
        {
            if (f > fMax) break;
            double x = FreqToX(f);
            dc.DrawLine(WaveTheme.GridLine, new Point(x, 0), new Point(x, h));
            dc.DrawText(WaveTheme.Text(label, WaveTheme.MonoFace, 9, WaveTheme.TextFaint, dpi), new Point(x + 3, h - 14));
        }
        for (int db = -12; db > DbFloor; db -= 12)
        {
            double y = DbToY(db);
            dc.DrawLine(WaveTheme.GridLine, new Point(0, y), new Point(w, y));
            dc.DrawText(WaveTheme.Text(db.ToString(), WaveTheme.MonoFace, 9, WaveTheme.TextFaint, dpi), new Point(w - 26, y - 12));
        }

        if (Tap == null) return;

        var fill = new StreamGeometry();
        var line = new List<Point>();
        var hold = new List<Point>();
        int cols = (int)(w / 2);
        for (int i = 0; i <= cols; i++)
        {
            double x = i * 2;
            double f = FMin * Math.Pow(fMax / FMin, x / w);
            int bin = Math.Clamp((int)(f / sr * FftSize), 1, _smooth.Length - 1);
            line.Add(new Point(x, DbToY(_smooth[bin])));
            hold.Add(new Point(x, DbToY(_hold[bin])));
        }
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(0, h), true, true);
            foreach (var p in line) g.LineTo(p, false, false);
            g.LineTo(new Point(w, h), false, false);
        }
        fill.Freeze();
        dc.DrawGeometry(FillBrush, null, fill);

        DrawPolyline(dc, line, WaveTheme.AccentPen);
        DrawPolyline(dc, hold, WaveTheme.PeakHoldPen);
    }

    private static void DrawPolyline(DrawingContext dc, List<Point> pts, Pen pen)
    {
        if (pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i], true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
