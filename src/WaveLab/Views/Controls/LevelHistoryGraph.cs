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

        int count = values.Count;
        double step = count > 1 ? innerWidth / (count - 1) : 0;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            double firstY = 1 + innerHeight * (1 - Frac(values[0]));
            ctx.BeginFigure(new Point(1, 1 + innerHeight), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(1, firstY), isStroked: false, isSmoothJoin: false);
            for (int i = 1; i < count; i++)
            {
                double y = 1 + innerHeight * (1 - Frac(values[i]));
                ctx.LineTo(new Point(1 + i * step, y), isStroked: false, isSmoothJoin: true);
            }
            ctx.LineTo(new Point(1 + (count - 1) * step, 1 + innerHeight), isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(TraceFill, null, geometry);

        // Crisp top edge over the fill.
        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            double firstY = 1 + innerHeight * (1 - Frac(values[0]));
            ctx.BeginFigure(new Point(1, firstY), isFilled: false, isClosed: false);
            for (int i = 1; i < count; i++)
            {
                double y = 1 + innerHeight * (1 - Frac(values[i]));
                ctx.LineTo(new Point(1 + i * step, y), isStroked: true, isSmoothJoin: true);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, TracePen, line);
    }
}
