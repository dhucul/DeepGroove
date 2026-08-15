using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using WaveLab.ViewModels;

namespace WaveLab.Views.Controls;

/// <summary>Per-channel dBFS scale aligned with the waveform's vertical amplitude zoom.</summary>
public sealed class AmplitudeRuler : FrameworkElement
{
    internal static readonly double[] MarkerLevelsDb = [0, -3, -6, -12, -24];

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
        if (e.PropertyName == nameof(DocumentViewModel.AmpZoom))
            Dispatcher.BeginInvoke(InvalidateVisual);
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

        for (int channel = 0; channel < channels; channel++)
        {
            double top = channel * channelHeight;
            double middle = top + channelHeight / 2;
            double amplitudeHeight = channelHeight * 0.46 * document.AmpZoom;

            foreach (double levelDb in MarkerLevelsDb)
            {
                double offset = MarkerOffset(levelDb, amplitudeHeight);
                if (offset > channelHeight / 2 - 2) continue;

                double upperY = middle - offset;
                double lowerY = middle + offset;
                dc.DrawLine(WaveTheme.TickPen, new Point(width - 6, upperY), new Point(width, upperY));
                dc.DrawLine(WaveTheme.TickPen, new Point(width - 4, lowerY), new Point(width, lowerY));

                string text = levelDb == 0 ? "0" : $"{levelDb:0}";
                FormattedText label = WaveTheme.Text(
                    text, WaveTheme.MonoFace, 8.5, WaveTheme.TextMuted, pixelsPerDip);
                dc.DrawText(label, new Point(Math.Max(2, width - label.Width - 9), upperY - label.Height / 2));
            }

            dc.DrawLine(WaveTheme.CenterLine,
                new Point(width - 4, middle), new Point(width, middle));
            if (channel > 0)
                dc.DrawLine(WaveTheme.ChannelDivider, new Point(0, top), new Point(width, top));
        }
    }

    internal static double MarkerOffset(double levelDb, double amplitudeHeight) =>
        Math.Pow(10, levelDb / 20.0) * amplitudeHeight;
}
