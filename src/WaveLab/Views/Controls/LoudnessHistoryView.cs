using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>
/// Rolling momentary / short-term loudness graph. The data is sampled continuously by
/// MasterSectionViewModel (so the history covers the whole playback even while this tab
/// is hidden); this control only draws it.
/// </summary>
public sealed class LoudnessHistoryView : FrameworkElement
{
    private const double Floor = -40, Ceil = 0;

    private readonly DispatcherTimer _timer;
    private static readonly Pen MomentaryPen = MakePen(Color.FromArgb(0x70, 0x3F, 0xD6, 0xC2), 1);
    private static readonly Pen ShortTermPen = MakePen(WaveTheme.Accent, 1.8);
    // The −16 LUFS target line and its label are constant. Rebuilding them per frame put an
    // unfrozen brush into WaveTheme.Text, whose cache is keyed by brush identity and skips
    // unfrozen brushes — so a static string was laid out from scratch 10×/s.
    private static readonly Pen TargetPen = MakeDashedPen(Color.FromArgb(0x50, 0xFF, 0xB4, 0x54), 1);
    private static readonly Brush TargetLabelBrush = MakeBrush(Color.FromArgb(0x90, 0xFF, 0xB4, 0x54));

    /// <summary>Source of the shared history ring.</summary>
    public MasterSectionViewModel? Source { get; set; }

    public LoudnessHistoryView()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => { if (IsVisible) InvalidateVisual(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.SpectrumBg, null, new Rect(0, 0, w, h));
        if (w < 10 || h < 10) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double YOf(double lufs) => (Ceil - Math.Clamp(lufs, Floor, Ceil)) / (Ceil - Floor) * h;

        foreach (var db in (double[])[-6, -12, -18, -23, -30])
        {
            double y = YOf(db);
            dc.DrawLine(WaveTheme.GridLine, new Point(0, y), new Point(w, y));
            dc.DrawText(WaveTheme.Text($"{db:0}", WaveTheme.MonoFace, 9, WaveTheme.TextFaint, dpi), new Point(w - 26, y - 12));
        }
        // −16 LUFS streaming target line
        double targetY = YOf(-16);
        dc.DrawLine(TargetPen, new Point(0, targetY), new Point(w, targetY));
        dc.DrawText(WaveTheme.Text("-16 target", WaveTheme.MonoFace, 8.5, TargetLabelBrush, dpi),
            new Point(6, targetY - 13));

        var src = Source;
        if (src == null || src.HistoryCount < 2)
        {
            dc.DrawText(WaveTheme.Text("Play something — loudness history records here.", WaveTheme.UiFace, 10.5,
                WaveTheme.TextFaint, dpi), new Point(8, h - 22));
            return;
        }

        DrawSeries(dc, src.HistoryMomentary, src, MomentaryPen, w, YOf);
        DrawSeries(dc, src.HistoryShortTerm, src, ShortTermPen, w, YOf);

        dc.DrawText(WaveTheme.Text("MOMENTARY", WaveTheme.UiFace, 8.5, MomentaryPen.Brush, dpi), new Point(8, 8));
        dc.DrawText(WaveTheme.Text("SHORT-TERM", WaveTheme.UiFace, 8.5, ShortTermPen.Brush, dpi), new Point(88, 8));
    }

    private static void DrawSeries(DrawingContext dc, double[] data, MasterSectionViewModel src, Pen pen,
        double w, Func<double, double> yOf)
    {
        int count = src.HistoryCount;
        int pos = src.HistoryPos;
        int capacity = MasterSectionViewModel.HistoryCapacity;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool first = true;
            for (int i = 0; i < count; i++)
            {
                int idx = (pos - count + i + capacity * 4) % capacity;
                double x = w - (count - 1 - i) * (w / capacity);
                var p = new Point(x, yOf(data[idx]));
                if (first) { g.BeginFigure(p, false, false); first = false; }
                else g.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static Pen MakePen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        p.Freeze();
        return p;
    }

    private static Pen MakeDashedPen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness) { DashStyle = DashStyles.Dash };
        p.Freeze();
        return p;
    }

    private static Brush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
