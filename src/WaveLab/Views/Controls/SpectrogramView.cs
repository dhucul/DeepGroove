using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;

namespace WaveLab.Views.Controls;

/// <summary>Offline spectrogram of the current view range, rendered to a WriteableBitmap on a worker task.</summary>
public sealed class SpectrogramView : Grid
{
    private const int FftSize = 1024;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _hint;
    private CancellationTokenSource? _renderCts;

    public SpectrogramView()
    {
        Background = WaveTheme.SpectrumBg;
        _hint = new TextBlock
        {
            Text = "Spectrogram renders for the current view — press Refresh or switch to this tab.",
            Foreground = WaveTheme.TextMuted,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_image);
        Children.Add(_hint);
        Unloaded += (_, _) => CancelRender();
    }

    public Task<bool> RenderAsync(AudioDocument? doc, int start, int end)
    {
        CancelRender();
        _image.Source = null;
        _hint.Text = "Select or display at least two FFT windows to render a spectrogram.";
        _hint.Visibility = Visibility.Visible;
        if (doc == null || doc.Length == 0 || doc.ChannelCount == 0) return Task.FromResult(false);
        start = Math.Clamp(start, 0, doc.Length);
        end = Math.Clamp(end, start, doc.Length);
        if (end - start < FftSize * 2) return Task.FromResult(false);

        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var channels = doc.Channels.ToArray();
        _hint.Text = "Rendering spectrogram…";
        return RenderCoreAsync(cts, channels, start, end - start, doc.SampleRate);
    }

    private void CancelRender()
    {
        var old = Interlocked.Exchange(ref _renderCts, null);
        old?.Cancel();
        // RenderAsync owns disposal. Disposing here races its worker's token
        // access and can turn an ordinary cancellation into ObjectDisposedException.
    }

    private async Task<bool> RenderCoreAsync(
        CancellationTokenSource owner,
        float[][] channels,
        int start,
        int count,
        int sr)
    {
        const int cols = 800, rows = 256;
        CancellationToken token = owner.Token;
        try
        {
            byte[] pixels = await Task.Run(() => RenderWorker(
                channels, start, count, sr, cols, rows, token), token);
            if (!ReferenceEquals(owner, _renderCts) || token.IsCancellationRequested) return false;
            var bmp = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, cols, rows), pixels, cols * 4, 0);
            bmp.Freeze();
            _image.Source = bmp;
            _hint.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (ReferenceEquals(owner, _renderCts))
            {
                _hint.Text = "Spectrogram failed: " + ex.Message;
                _hint.Visibility = Visibility.Visible;
            }
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _renderCts, null, owner);
            owner.Dispose();
        }
    }

    private static byte[] RenderWorker(float[][] channels, int start, int count, int sr,
        int cols, int rows, CancellationToken token)
    {
        var window = Fft.HannWindow(FftSize);
        var magDb = new float[FftSize / 2];
        var pixels = new byte[cols * rows * 4];
        double fMax = Math.Min(20000, sr / 2.0);
        double hop = Math.Max(1, (count - FftSize) / (double)cols);
        var frame = new float[FftSize];
        int channelCount = channels.Length;

        for (int x = 0; x < cols; x++)
        {
            token.ThrowIfCancellationRequested();
            int s0 = (int)(x * hop);
            // Downmix only the window this column reads. The visible range can be hundreds of
            // millions of samples, but the columns together only ever touch cols * FftSize of
            // them — buffering the whole range cost a ~635 MB transient on a long file.
            for (int i = 0; i < FftSize; i++)
            {
                int s = s0 + i;
                if (s >= count) { frame[i] = 0; continue; }
                float value = 0;
                for (int c = 0; c < channelCount; c++) value += channels[c][start + s];
                frame[i] = value / channelCount;
            }
            Fft.MagnitudeDb(frame, window, magDb);

            for (int y = 0; y < rows; y++)
            {
                double f = 20 * Math.Pow(fMax / 20, (rows - 1 - y) / (double)(rows - 1));
                int bin = Math.Clamp((int)(f / sr * FftSize), 1, magDb.Length - 1);
                double t = Math.Clamp((magDb[bin] + 90) / 90.0, 0, 1);
                var (r, g, b) = ColorMap(t);
                int o = (y * cols + x) * 4;
                pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>Dark → deep teal → accent → white heat map.</summary>
    private static (byte r, byte g, byte b) ColorMap(double t)
    {
        (double r, double g, double b)[] stops =
        [
            (0x0F / 255.0, 0x11 / 255.0, 0x14 / 255.0),
            (0x11 / 255.0, 0x31 / 255.0, 0x38 / 255.0),
            (0x1B / 255.0, 0x66 / 255.0, 0x63 / 255.0),
            (0x3F / 255.0, 0xD6 / 255.0, 0xC2 / 255.0),
            (0xC8 / 255.0, 0xF5 / 255.0, 0xEE / 255.0),
            (1.0, 1.0, 1.0),
        ];
        double pos = t * (stops.Length - 1);
        int i = Math.Clamp((int)pos, 0, stops.Length - 2);
        double f = pos - i;
        var a = stops[i];
        var b2 = stops[i + 1];
        return ((byte)((a.r + (b2.r - a.r) * f) * 255),
                (byte)((a.g + (b2.g - a.g) * f) * 255),
                (byte)((a.b + (b2.b - a.b) * f) * 255));
    }
}
