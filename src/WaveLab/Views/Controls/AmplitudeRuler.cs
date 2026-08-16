using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>Per-channel dBFS scale aligned with the waveform's vertical amplitude zoom.</summary>
public sealed class AmplitudeRuler : FrameworkElement
{
    /// <summary>
    /// Levels the waveform itself draws guide lines for. Deliberately a short fixed list: the
    /// ruler's ladder adapts to the band height, but putting every rung of it across the wave
    /// would turn the guides into hatching.
    /// </summary>
    internal static readonly double[] MarkerLevelsDb = [0, -3, -6, -12, -24];

    /// <summary>
    /// Steps the ladder may use, coarsest last. Each divides the next (1│3│6│12│24) and that is
    /// load-bearing: the labelling pass re-runs the same rule at the wider gap, so the step it
    /// picks is always a multiple of the step the tick pass picked at that offset. Every level the
    /// label rule asks for is therefore already on the ladder. Break the chain — a 2 dB step, say —
    /// and the rule starts asking for levels that were never ticked, leaving holes in the numbering.
    /// </summary>
    private static readonly int[] StepChainDb = [1, 3, 6, 12, 24];

    /// <summary>Fraction of an offset that a step of <see cref="StepChainDb"/> spans, since the scale is linear in amplitude.</summary>
    private static readonly double[] StepSpanFraction = BuildStepSpans();

    private const int FloorDb = -72;          // below this nothing is a pixel tall at any zoom
    private const double TickGapPx = 4;       // smallest legible spacing between two ticks
    private const double LabelGapPx = 11;     // one 8.5 px mono line, so numbers never touch
    private const double MajorTickPx = 7;
    private const double MinorTickPx = 4;
    private const double LabelSize = 8.5;
    private const int MaxRank = 5;

    private readonly List<ScaleTick> _scale = [];

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(AmplitudeRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            OnDocumentChanged));

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public AmplitudeRuler()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ruler = (AmplitudeRuler)d;
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= ruler.OnDocumentPropertyChanged;
        if (e.NewValue is DocumentViewModel current)
            current.PropertyChanged += ruler.OnDocumentPropertyChanged;
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // PeaksVersion is the notification an edit actually raises. The scale is laid out from
        // Doc.ChannelCount, and ReplaceAllOwned (a rack containing MonoToStereoEffect) changes the
        // channel topology without changing the Document DP value — so AmpZoom alone left the ruler
        // drawing a one-channel scale beside a two-channel waveform.
        if (e.PropertyName is nameof(DocumentViewModel.AmpZoom) or nameof(DocumentViewModel.PeaksVersion))
            RequestRedraw();
    }

    /// <summary>
    /// Synchronous on the UI thread, matching WaveformView/TimeRuler/OverviewBar: a Normal-priority
    /// BeginInvoke lands in the *next* render pass and outranks the Render-priority meter timers and
    /// the Input-priority wheel events that produced the zoom change.
    /// </summary>
    private void RequestRedraw()
    {
        if (Dispatcher.CheckAccess()) InvalidateVisual();
        else Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(InvalidateVisual));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(WaveTheme.PanelBg, null, new Rect(0, 0, width, height));
        dc.DrawLine(WaveTheme.ChannelDivider,
            new Point(Math.Max(0, width - 0.5), 0),
            new Point(Math.Max(0, width - 0.5), height));

        DocumentViewModel? document = Document;
        if (document == null || document.Doc.ChannelCount <= 0 || height < 2) return;

        int channels = document.Doc.ChannelCount;
        double channelHeight = height / channels;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Every channel band is the same size, so the ladder is solved once and mirrored into each.
        BuildScale(_scale, channelHeight * 0.46 * document.AmpZoom, channelHeight / 2 - 2);

        // Whether the band is numbered at all is a property of the ladder, not of the channel: a
        // band too short for a single number gets ticks only, never a lone -inf on the centre line.
        bool anyLabel = _scale.Exists(static tick => tick.Labeled);
        FormattedText? centre = anyLabel
            ? WaveTheme.Text("-∞", WaveTheme.MonoFace, LabelSize, WaveTheme.TextFaint, pixelsPerDip)
            : null;

        for (int channel = 0; channel < channels; channel++)
        {
            double top = channel * channelHeight;
            double middle = top + channelHeight / 2;

            foreach (ScaleTick tick in _scale)
            {
                double length = tick.Labeled ? MajorTickPx : MinorTickPx;
                Pen pen = tick.Labeled ? WaveTheme.ScaleTickPen : WaveTheme.ScaleTickMinorPen;
                double upperY = middle - tick.Offset;
                double lowerY = middle + tick.Offset;
                dc.DrawLine(pen, new Point(width - length, upperY), new Point(width, upperY));
                dc.DrawLine(pen, new Point(width - length, lowerY), new Point(width, lowerY));
                if (!tick.Labeled) continue;

                FormattedText label = WaveTheme.Text(
                    tick.LevelDb.ToString(CultureInfo.InvariantCulture),
                    WaveTheme.MonoFace, LabelSize, WaveTheme.TextMuted, pixelsPerDip);
                DrawLabel(dc, label, width, upperY, top, channelHeight);
                DrawLabel(dc, label, width, lowerY, top, channelHeight);
            }

            // The centre line is -inf, and BuildScale keeps a label slot clear for it.
            if (centre != null) DrawLabel(dc, centre, width, middle, top, channelHeight);

            dc.DrawLine(WaveTheme.CenterLine,
                new Point(width - MinorTickPx, middle), new Point(width, middle));
            if (channel > 0)
                dc.DrawLine(WaveTheme.ChannelDivider, new Point(0, top), new Point(width, top));
        }
    }

    /// <summary>
    /// Right-aligns a label on its tick, nudged back inside the band at the extremes. The nudge is
    /// deliberate: full scale sits within half a line of the band edge on any band under ~140 px, and
    /// letting the number spill would clip it against the top of the control or print it over the
    /// channel above. It costs at most ~3.5 px of alignment with its own tick, and only for the
    /// outermost number of a band.
    /// </summary>
    private static void DrawLabel(DrawingContext dc, FormattedText label, double width,
        double centreY, double top, double channelHeight)
    {
        double x = Math.Max(2, width - label.Width - MajorTickPx - 2);
        double bottom = top + channelHeight - label.Height;
        double y = centreY - label.Height / 2;
        dc.DrawText(label, new Point(x, bottom <= top ? top : Math.Clamp(y, top, bottom)));
    }

    /// <summary>A rung of the scale: its level, its distance from the centre line, and whether it carries a number.</summary>
    internal readonly record struct ScaleTick(int LevelDb, double Offset, bool Labeled);

    /// <summary>
    /// Solves the ladder for one channel band, from 0 dBFS at the top down to the centre line.
    /// </summary>
    /// <param name="ticks">Receives the rungs, outermost first. Reused between renders.</param>
    /// <param name="amplitudeHeight">Pixels between the centre line and full scale — may exceed the band.</param>
    /// <param name="maxOffset">Largest offset that still fits inside the band.</param>
    internal static void BuildScale(List<ScaleTick> ticks, double amplitudeHeight, double maxOffset)
    {
        ticks.Clear();
        if (!(amplitudeHeight > 0) || maxOffset < TickGapPx) return;

        // Ticks: walk every whole dB down from 0 and keep a level when the step that fits at its
        // own offset divides it, so the ladder coarsens as the linear scale compresses.
        for (int levelDb = 0; levelDb >= FloorDb; levelDb--)
        {
            double offset = MarkerOffset(levelDb, amplitudeHeight);
            if (offset > maxOffset) continue;   // off the top of the band at this amplitude zoom
            if (offset < TickGapPx) break;      // the rest of the ladder is inside the centre line
            int step = StepFor(offset, TickGapPx);
            if (step == 0 || levelDb % step != 0) continue;
            ticks.Add(new ScaleTick(levelDb, offset, false));
        }

        // Labels: the same rule at the wider label gap decides which levels may carry a number —
        // that is what keeps the numbering regular (no lone -1 stranded between 0 and -3) — and
        // roundest-first order with a real collision check decides which of them actually do.
        for (int rank = 0; rank <= MaxRank; rank++)
        {
            for (int i = 0; i < ticks.Count; i++)
            {
                ScaleTick tick = ticks[i];
                if (tick.Labeled || Rank(tick.LevelDb) != rank) continue;
                if (tick.Offset < LabelGapPx) continue;   // reserved for -inf on the centre line
                int step = StepFor(tick.Offset, LabelGapPx);
                // A level so deep that not even the coarsest step fits is the end of the ladder;
                // it stays eligible so the innermost number never drops out.
                if (step != 0 && tick.LevelDb % step != 0) continue;
                if (Crowded(ticks, tick.Offset)) continue;
                ticks[i] = tick with { Labeled = true };
            }
        }
    }

    /// <summary>Convenience overload for tests; the renderer reuses one list instead.</summary>
    internal static List<ScaleTick> BuildScale(double amplitudeHeight, double maxOffset)
    {
        List<ScaleTick> ticks = [];
        BuildScale(ticks, amplitudeHeight, maxOffset);
        return ticks;
    }

    internal static double MarkerOffset(double levelDb, double amplitudeHeight) =>
        Math.Pow(10, levelDb / 20.0) * amplitudeHeight;

    /// <summary>Smallest step whose own neighbour spacing at this offset clears <paramref name="gap"/>; 0 if none does.</summary>
    private static int StepFor(double offset, double gap)
    {
        for (int i = 0; i < StepChainDb.Length; i++)
            if (offset * StepSpanFraction[i] >= gap) return StepChainDb[i];
        return 0;
    }

    /// <summary>Roundest levels get first refusal on a label slot.</summary>
    private static int Rank(int levelDb)
    {
        if (levelDb == 0) return 0;
        int magnitude = -levelDb;
        if (magnitude % 24 == 0) return 1;
        if (magnitude % 12 == 0) return 2;
        if (magnitude % 6 == 0) return 3;
        if (magnitude % 3 == 0) return 4;
        return MaxRank;
    }

    private static bool Crowded(List<ScaleTick> ticks, double offset)
    {
        foreach (ScaleTick other in ticks)
            if (other.Labeled && Math.Abs(other.Offset - offset) < LabelGapPx) return true;
        return false;
    }

    private static double[] BuildStepSpans()
    {
        var spans = new double[StepChainDb.Length];
        for (int i = 0; i < spans.Length; i++)
            spans[i] = 1 - Math.Pow(10, -StepChainDb[i] / 20.0);
        return spans;
    }
}
