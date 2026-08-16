using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>
/// Logarithmic frequency scale for the spectral editor, the counterpart to
/// <see cref="AmplitudeRuler"/> beside the waveform.
/// </summary>
/// <remarks>
/// Built on the same principle as the dBFS ladder: a fixed list of decade landmarks thinned to
/// whatever the pane is tall enough to label, rather than a fixed set that crowds when the split
/// leaves the spectrogram short. The mapping comes from <see cref="SpectrogramImage"/> so the ruler
/// and the image cannot disagree about where a frequency sits.
/// </remarks>
public sealed class FrequencyRuler : FrameworkElement
{
    /// <summary>Landmarks, roundest first: the ones that survive when there is little room.</summary>
    private static readonly double[] Landmarks =
        [1_000, 100, 10_000, 20_000, 500, 5_000, 50, 200, 2_000, 20, 20_000];

    private const double LabelGapPx = 12;
    private const double TickPx = 6;
    private const double LabelSize = 8.5;

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(DocumentViewModel), typeof(FrequencyRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDocumentChanged));

    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings), typeof(SpectrogramImageSettings), typeof(FrequencyRuler),
        new FrameworkPropertyMetadata(SpectrogramImageSettings.Default,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public SpectrogramImageSettings Settings
    {
        get => (SpectrogramImageSettings)GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    public FrequencyRuler()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ruler = (FrequencyRuler)d;
        if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= ruler.OnDocumentPropertyChanged;
        if (e.NewValue is DocumentViewModel current) current.PropertyChanged += ruler.OnDocumentPropertyChanged;
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The sample rate is what moves the scale, and it only changes with the document itself.
        if (e.PropertyName is nameof(DocumentViewModel.PeaksVersion)) RequestRedraw();
    }

    private void RequestRedraw()
    {
        if (Dispatcher.CheckAccess()) InvalidateVisual();
        else Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(InvalidateVisual));
    }

    /// <summary>Landmarks that fit, roundest first, no two closer than a label height.</summary>
    internal static List<(double Frequency, double Y)> BuildScale(
        int height, SpectrogramImageSettings settings, double nyquist)
    {
        var chosen = new List<(double Frequency, double Y)>();
        if (height < LabelGapPx) return chosen;

        double lowest = Math.Clamp(settings.MinimumFrequency, 1, Math.Max(2, nyquist - 1));
        double highest = Math.Clamp(settings.MaximumFrequency, lowest + 1, nyquist);

        foreach (double frequency in Landmarks)
        {
            if (frequency < lowest || frequency > highest) continue;
            double y = SpectrogramImage.RowForFrequency(frequency, height, settings, nyquist);
            if (y < 4 || y > height - 3) continue;
            if (chosen.Exists(other => Math.Abs(other.Y - y) < LabelGapPx)) continue;
            if (chosen.Exists(other => Math.Abs(other.Frequency - frequency) < 0.01)) continue;
            chosen.Add((frequency, y));
        }

        chosen.Sort((a, b) => a.Y.CompareTo(b.Y));
        return chosen;
    }

    /// <summary>"50", "500", "2k", "20k" — the shortest form that stays unambiguous.</summary>
    internal static string Format(double frequency) => frequency >= 1_000
        ? (frequency % 1_000 == 0
            ? (frequency / 1_000).ToString("0", CultureInfo.InvariantCulture) + "k"
            : (frequency / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k")
        : frequency.ToString("0", CultureInfo.InvariantCulture);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(WaveTheme.PanelBg, null, new Rect(0, 0, width, height));
        dc.DrawLine(WaveTheme.ChannelDivider,
            new Point(Math.Max(0, width - 0.5), 0), new Point(Math.Max(0, width - 0.5), height));

        DocumentViewModel? document = Document;
        if (document == null || height < 2) return;

        double nyquist = document.Doc.SampleRate / 2.0;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var (frequency, y) in BuildScale((int)height, Settings, nyquist))
        {
            dc.DrawLine(WaveTheme.ScaleTickPen, new Point(width - TickPx, y), new Point(width, y));

            FormattedText label = WaveTheme.Text(Format(frequency), WaveTheme.MonoFace, LabelSize,
                WaveTheme.TextMuted, pixelsPerDip);
            double x = Math.Max(2, width - label.Width - TickPx - 3);
            double top = Math.Clamp(y - label.Height / 2, 0, Math.Max(0, height - label.Height));
            dc.DrawText(label, new Point(x, top));
        }
    }
}
