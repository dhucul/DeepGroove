using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.Audio;

namespace WaveLab.Views.Controls;

/// <summary>Goniometer (Lissajous, mid/side rotated) with stereo correlation and balance meters.</summary>
public sealed class PhaseView : FrameworkElement
{
    private const int Points = 2048;

    private readonly DispatcherTimer _timer;
    private readonly float[] _left = new float[Points];
    private readonly float[] _right = new float[Points];
    private static readonly Brush DotBrush = Make(Color.FromArgb(0x99, 0x3F, 0xD6, 0xC2));
    private static readonly Brush CorrFill = Make(Color.FromArgb(0x66, 0x3F, 0xD6, 0xC2));
    private static readonly Pen CirclePen = MakePen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen NeedlePen = MakePen(Colors.White, 2);
    private static readonly Brush BalanceDot = Make(WaveTheme.Amber);

    public MasterSection? Tap { get; set; }

    public PhaseView()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => { if (IsVisible) InvalidateVisual(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.SpectrumBg, null, new Rect(0, 0, w, h));
        if (w < 60 || h < 60) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double gonioSize = Math.Min(h - 20, w * 0.4);
        double cx = 20 + gonioSize / 2;
        double cy = h / 2;
        double radius = gonioSize / 2;

        // goniometer frame
        dc.DrawEllipse(null, CirclePen, new Point(cx, cy), radius, radius);
        dc.DrawLine(CirclePen, new Point(cx - radius, cy), new Point(cx + radius, cy));
        dc.DrawLine(CirclePen, new Point(cx, cy - radius), new Point(cx, cy + radius));
        dc.DrawLine(CirclePen, new Point(cx - radius * 0.71, cy - radius * 0.71), new Point(cx + radius * 0.71, cy + radius * 0.71));
        dc.DrawLine(CirclePen, new Point(cx - radius * 0.71, cy + radius * 0.71), new Point(cx + radius * 0.71, cy - radius * 0.71));
        dc.DrawText(WaveTheme.Text("L", WaveTheme.MonoFace, 9, WaveTheme.TextFaint, dpi), new Point(cx - radius * 0.78, cy - radius * 0.82));
        dc.DrawText(WaveTheme.Text("R", WaveTheme.MonoFace, 9, WaveTheme.TextFaint, dpi), new Point(cx + radius * 0.70, cy - radius * 0.82));

        if (Tap != null)
        {
            Tap.CopyLatestStereo(_left, _right);
            const double sqrt2Inv = 0.70710678;
            for (int i = 0; i < Points; i += 2)
            {
                double l = _left[i], r = _right[i];
                double mid = (l + r) * sqrt2Inv;
                double side = (r - l) * sqrt2Inv;
                double x = cx + side * radius;
                double y = cy - mid * radius;
                double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dist > radius) continue;
                dc.DrawRectangle(DotBrush, null, new Rect(x, y, 1.4, 1.4));
            }
        }

        // right column: correlation + balance
        double colX = cx + radius + 36;
        double colW = Math.Max(60, w - colX - 30);
        double corr = Tap?.Correlation ?? 0;

        dc.DrawText(WaveTheme.Text("STEREO CORRELATION", WaveTheme.UiFace, 9, WaveTheme.TextFaint, dpi), new Point(colX, cy - 56));
        var barRect = new Rect(colX, cy - 38, colW, 13);
        dc.DrawRoundedRectangle(WaveTheme.OverviewBg, WaveTheme.ViewportPen, barRect, 6, 6);
        double mid0 = barRect.X + barRect.Width / 2;
        double corrX = barRect.X + (corr + 1) / 2 * barRect.Width;
        if (corr >= 0)
            dc.DrawRectangle(CorrFill, null, new Rect(mid0, barRect.Y + 2, Math.Max(0, corrX - mid0), barRect.Height - 4));
        else
            dc.DrawRectangle(CorrFill, null, new Rect(corrX, barRect.Y + 2, mid0 - corrX, barRect.Height - 4));
        dc.DrawLine(NeedlePen, new Point(corrX, barRect.Y - 3), new Point(corrX, barRect.Y + barRect.Height + 3));
        dc.DrawText(WaveTheme.Text("-1", WaveTheme.MonoFace, 8.5, WaveTheme.TextFaint, dpi), new Point(barRect.X, barRect.Bottom + 4));
        dc.DrawText(WaveTheme.Text("0", WaveTheme.MonoFace, 8.5, WaveTheme.TextFaint, dpi), new Point(mid0 - 3, barRect.Bottom + 4));
        dc.DrawText(WaveTheme.Text("+1", WaveTheme.MonoFace, 8.5, WaveTheme.TextFaint, dpi), new Point(barRect.Right - 12, barRect.Bottom + 4));

        var corrText = WaveTheme.Text($"{corr:+0.00;-0.00;0.00}", WaveTheme.MonoFace, 20,
            corr >= 0 ? new SolidColorBrush(WaveTheme.Accent) : new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)), dpi);
        dc.DrawText(corrText, new Point(colX, cy + 2));
        string hint = corr > 0.5 ? "mono-compatible" : corr > 0 ? "wide" : "phase issues";
        dc.DrawText(WaveTheme.Text(hint, WaveTheme.UiFace, 9.5, WaveTheme.TextFaint, dpi), new Point(colX + 84, cy + 12));

        // balance
        double bal = Math.Clamp(Tap?.BalanceDb ?? 0, -12, 12);
        dc.DrawText(WaveTheme.Text("BALANCE", WaveTheme.UiFace, 9, WaveTheme.TextFaint, dpi), new Point(colX, cy + 40));
        var balRect = new Rect(colX, cy + 56, colW, 7);
        dc.DrawRoundedRectangle(WaveTheme.OverviewBg, WaveTheme.ViewportPen, balRect, 3, 3);
        double balX = balRect.X + (bal + 12) / 24 * balRect.Width;
        dc.DrawRoundedRectangle(BalanceDot, null, new Rect(balX - 5, balRect.Y - 1.5, 10, 10), 3, 3);
        dc.DrawText(WaveTheme.Text("L", WaveTheme.MonoFace, 8.5, WaveTheme.TextFaint, dpi), new Point(balRect.X - 12, balRect.Y - 2));
        dc.DrawText(WaveTheme.Text("R", WaveTheme.MonoFace, 8.5, WaveTheme.TextFaint, dpi), new Point(balRect.Right + 5, balRect.Y - 2));
    }

    private static Brush Make(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen MakePen(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }
}
