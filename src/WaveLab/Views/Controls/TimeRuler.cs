using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>Time ruler with adaptive tick spacing; click to place the cursor.</summary>
public sealed class TimeRuler : FrameworkElement
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(TimeRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDocumentChanged));

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public TimeRuler()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TimeRuler)d;
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= view.OnVmChanged;
        if (e.NewValue is DocumentViewModel vm) vm.PropertyChanged += view.OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ViewStart) or nameof(DocumentViewModel.SamplesPerPixel)
            or nameof(DocumentViewModel.MarkersVersion))
            Dispatcher.BeginInvoke(InvalidateVisual);
    }

    private static readonly Brush MarkerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54)));
    private static readonly Brush MarkerLabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xB4, 0x54)));
    private static readonly Brush RegionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0x3F, 0xD6, 0xC2)));
    private static readonly Pen RegionPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x3F, 0xD6, 0xC2)), 1));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(WaveTheme.PanelBg, null, new Rect(0, 0, w, h));
        var vm = Document;
        if (vm == null || vm.Doc.SampleRate <= 0 || w < 2) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int sr = vm.Doc.SampleRate;
        double secPerPx = vm.SamplesPerPixel / sr;
        double tick = TimeFormat.NiceTick(80 * secPerPx); // aim for a label every ~80 px
        double minor = tick / 5;

        double t0 = vm.ViewStart / sr;
        double firstMinor = Math.Floor(t0 / minor) * minor;

        for (double t = firstMinor; ; t += minor)
        {
            double x = (t * sr - vm.ViewStart) / vm.SamplesPerPixel;
            if (x > w) break;
            if (x < 0 || t < 0) continue;
            bool major = Math.Abs(t / tick - Math.Round(t / tick)) < 1e-6;
            dc.DrawLine(WaveTheme.TickPen, new Point(x, h), new Point(x, major ? h - 11 : h - 5));
            if (major)
            {
                string label = tick < 1 ? $"{TimeFormat.Compact(t)}.{(int)Math.Round(t % 1 * 1000):000}" : TimeFormat.Compact(t);
                var ft = WaveTheme.Text(label, WaveTheme.MonoFace, 9.5, WaveTheme.TextMuted, dpi);
                dc.DrawText(ft, new Point(x + 4, h - 12 - ft.Height + 2));
            }
        }

        // regions
        foreach (var region in vm.Regions)
        {
            double x0 = (region.Start - vm.ViewStart) / vm.SamplesPerPixel;
            double x1 = (region.End - vm.ViewStart) / vm.SamplesPerPixel;
            if (x1 < 0 || x0 > w) continue;
            var rect = new Rect(Math.Max(0, x0), 1, Math.Min(w, x1) - Math.Max(0, x0), 9);
            dc.DrawRoundedRectangle(RegionFill, RegionPen, rect, 2, 2);
            if (rect.Width > 30)
            {
                var ft = WaveTheme.Text(region.Name, WaveTheme.UiFace, 8, WaveTheme.TextMuted, dpi);
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawText(ft, new Point(rect.X + 4, rect.Y - 0.5));
                dc.Pop();
            }
        }

        // marker flags
        foreach (var marker in vm.Markers)
        {
            double x = (marker.Position - vm.ViewStart) / vm.SamplesPerPixel;
            if (x < -4 || x > w + 4) continue;
            var flag = new StreamGeometry();
            using (var g = flag.Open())
            {
                g.BeginFigure(new Point(x - 4.5, 0), true, true);
                g.LineTo(new Point(x + 4.5, 0), false, false);
                g.LineTo(new Point(x, 7), false, false);
            }
            flag.Freeze();
            dc.DrawGeometry(MarkerBrush, null, flag);
            var name = WaveTheme.Text(marker.Name, WaveTheme.UiFace, 8.5, MarkerBrush, dpi);
            if (name.Width < 120)
            {
                dc.DrawRoundedRectangle(MarkerLabelBg, null, new Rect(x + 6, 0, name.Width + 8, 11), 3, 3);
                dc.DrawText(name, new Point(x + 10, -0.5));
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var vm = Document;
        if (vm == null) return;
        var pos = e.GetPosition(this);

        // marker hit-test first (top half of ruler)
        if (pos.Y < 14)
        {
            foreach (var marker in vm.Markers)
            {
                double x = (marker.Position - vm.ViewStart) / vm.SamplesPerPixel;
                if (Math.Abs(pos.X - x) < 7)
                {
                    vm.JumpToMarker(marker);
                    e.Handled = true;
                    return;
                }
            }
            foreach (var region in vm.Regions)
            {
                double x0 = (region.Start - vm.ViewStart) / vm.SamplesPerPixel;
                double x1 = (region.End - vm.ViewStart) / vm.SamplesPerPixel;
                if (pos.X >= x0 && pos.X <= x1)
                {
                    vm.SetSelection(region.Start, region.End);
                    e.Handled = true;
                    return;
                }
            }
        }

        int s = (int)Math.Clamp(vm.ViewStart + pos.X * vm.SamplesPerPixel, 0, Math.Max(0, vm.Doc.Length - 1));
        vm.SetCursor(s, clearSelection: false);
        e.Handled = true;
    }
}
