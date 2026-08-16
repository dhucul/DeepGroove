using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace WaveLab.Views.Controls;

/// <summary>
/// Scrolling input-level history strip for the Recording Level Assistant: dB on
/// the vertical axis, time flowing to the right, with the assistant's target
/// band shaded behind the trace.
/// </summary>
public sealed class LevelHistoryGraph : FrameworkElement
{
    public const double FloorDb = -60;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IList<double>), typeof(LevelHistoryGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));
    public static readonly DependencyProperty TargetMinimumDbProperty = DependencyProperty.Register(
        nameof(TargetMinimumDb), typeof(double), typeof(LevelHistoryGraph),
        new FrameworkPropertyMetadata(-9.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TargetMaximumDbProperty = DependencyProperty.Register(
        nameof(TargetMaximumDb), typeof(double), typeof(LevelHistoryGraph),
        new FrameworkPropertyMetadata(-3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Level samples in dB, oldest first. An observable source re-renders on change.</summary>
    public IList<double>? Values { get => (IList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double TargetMinimumDb { get => (double)GetValue(TargetMinimumDbProperty); set => SetValue(TargetMinimumDbProperty, value); }
    public double TargetMaximumDb { get => (double)GetValue(TargetMaximumDbProperty); set => SetValue(TargetMaximumDbProperty, value); }

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x15));
    private static readonly Pen Border = new(new SolidColorBrush(Color.FromRgb(0x29, 0x35, 0x32)), 1);
    private static readonly Brush TargetFill = new SolidColorBrush(Color.FromArgb(0x24, 0x3F, 0xD6, 0xC2));
    private static readonly Brush TraceFill = new SolidColorBrush(Color.FromArgb(0x2E, 0x3F, 0xD6, 0xC2));
    private static readonly Pen TracePen = new(new SolidColorBrush(Color.FromArgb(0xCC, 0x3F, 0xD6, 0xC2)), 1.25);
    private static readonly Pen FloorPen = new(new SolidColorBrush(Color.FromArgb(0x40, 0x71, 0x81, 0x7C)), 1);

    static LevelHistoryGraph()
    {
        Bg.Freeze(); Border.Freeze(); TargetFill.Freeze(); TraceFill.Freeze(); TracePen.Freeze(); FloorPen.Freeze();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (LevelHistoryGraph)d;
        if (e.OldValue is INotifyCollectionChanged oldSource)
            oldSource.CollectionChanged -= graph.OnSourceChanged;
        if (e.NewValue is INotifyCollectionChanged newSource)
            newSource.CollectionChanged += graph.OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private static double Frac(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        dc.DrawRoundedRectangle(Bg, Border, new Rect(0.5, 0.5, w - 1, h - 1), 3, 3);

        double innerWidth = w - 2;
        double innerHeight = h - 2;

        // Target band: the assistant's safe ceiling zone as a horizontal stripe.
        double bandTop = 1 + innerHeight * (1 - Frac(Math.Max(TargetMinimumDb, TargetMaximumDb)));
        double bandBottom = 1 + innerHeight * (1 - Frac(Math.Min(TargetMinimumDb, TargetMaximumDb)));
        dc.DrawRectangle(TargetFill, null, new Rect(1, bandTop, innerWidth, Math.Max(0, bandBottom - bandTop)));

        var values = Values;
        if (values == null || values.Count == 0)
        {
            double midY = 1 + innerHeight; // floor line
            dc.DrawLine(FloorPen, new Point(1, midY), new Point(w - 1, midY));
            return;
        }

        // One vertex per device-pixel column, carrying that column's loudest
        // sample. The ring holds 900 entries but the strip is only a few hundred
        // pixels wide, and this repaints on every 33 ms append — drawing sub-pixel
        // detail twice (fill and stroke) is work nobody can see.
        Point[] points = BuildTrace(values, innerWidth, innerHeight);
        if (points.Length == 0) return;

        double baseline = 1 + innerHeight;
        var fill = new StreamGeometry();
        using (StreamGeometryContext ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, baseline), isFilled: true, isClosed: true);
            foreach (Point point in points)
                ctx.LineTo(point, isStroked: false, isSmoothJoin: true);
            ctx.LineTo(new Point(points[^1].X, baseline), isStroked: false, isSmoothJoin: false);
        }
        fill.Freeze();
        dc.DrawGeometry(TraceFill, null, fill);

        // Crisp top edge over the fill.
        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (int index = 1; index < points.Length; index++)
                ctx.LineTo(points[index], isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();
        dc.DrawGeometry(null, TracePen, line);
    }

    private Point[] BuildTrace(IList<double> values, double innerWidth, double innerHeight)
    {
        int count = values.Count;
        if (count == 0) return [];
        if (count == 1)
            return [new Point(1, 1 + innerHeight * (1 - Frac(values[0])))];

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int columns = Math.Clamp((int)Math.Ceiling(innerWidth * scale), 2, count);
        var loudest = new double[columns];
        Array.Fill(loudest, double.NegativeInfinity);
        for (int index = 0; index < count; index++)
        {
            int column = (int)((long)index * (columns - 1) / (count - 1));
            if (values[index] > loudest[column]) loudest[column] = values[index];
        }

        var points = new Point[columns];
        double step = innerWidth / (columns - 1);
        double carried = FloorDb;
        for (int column = 0; column < columns; column++)
        {
            // A column can be empty when the ring is shorter than the strip is
            // wide; hold the previous level rather than dropping to the floor.
            if (double.IsFinite(loudest[column])) carried = loudest[column];
            points[column] = new Point(1 + column * step, 1 + innerHeight * (1 - Frac(carried)));
        }
        return points;
    }
}
