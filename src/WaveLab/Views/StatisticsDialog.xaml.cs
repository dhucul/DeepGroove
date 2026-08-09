using System.Text;
using System.Windows;
using System.Windows.Input;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;

namespace WaveLab.Views;

public partial class StatisticsDialog : Window
{
    public sealed record Row(string Label, string Left, string Right);

    private readonly List<Row> _rows = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly AudioDocument _doc;

    public StatisticsDialog(AudioDocument doc)
    {
        InitializeComponent();
        _doc = doc;
        titleText.Text = $"Audio Statistics — {doc.Title}";
        Loaded += OnLoaded;
        Closing += (_, _) => _lifetimeCts.Cancel();
        Closed += (_, _) => _lifetimeCts.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CancellationToken token = _lifetimeCts.Token;
        try
        {
            var rowsData = await Task.Run(() => Compute(_doc, token), token);
            if (token.IsCancellationRequested) return;
            _rows.AddRange(rowsData);
            rows.ItemsSource = _rows;
            busyText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            busyText.Text = "Analysis failed: " + ex.Message;
        }
    }

    private static List<Row> Compute(AudioDocument doc, CancellationToken token)
    {
        // stable snapshot of the channel arrays — edits splice in NEW arrays, so these refs are safe off-thread
        var chans = doc.Channels.ToArray();
        int channels = chans.Length;
        int frames = chans.Length > 0 ? chans[0].Length : 0;
        if (channels == 0) return [new Row("Audio", "", "No channels")];
        var peak = new double[channels];
        var sumSq = new double[channels];
        var dc = new double[channels];
        var clipped = new long[channels];
        var invalid = new long[channels];
        const float clipLevel = 0.999969f; // ~0 dBFS for 16-bit-origin material

        for (int c = 0; c < channels; c++)
        {
            token.ThrowIfCancellationRequested();
            var data = chans[c];
            double p = 0, sq = 0, sum = 0;
            long clip = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                float v = data[i];
                if (!float.IsFinite(v)) { invalid[c]++; continue; }
                double a = Math.Abs(v);
                if (a > p) p = a;
                sq += v * v;
                sum += v;
                if (a >= clipLevel) clip++;
            }
            peak[c] = p;
            sumSq[c] = sq;
            dc[c] = frames > 0 ? sum / frames : 0;
            clipped[c] = clip;
        }

        // offline loudness + true peak (interleave from the snapshot, not the live document)
        var meter = new LoudnessMeter();
        meter.Configure(doc.SampleRate, channels);
        const int block = 65536;
        var interleaved = new float[block * channels];
        for (int start = 0; start < frames; start += block)
        {
            token.ThrowIfCancellationRequested();
            int n = Math.Min(block, frames - start);
            for (int f = 0; f < n; f++)
                for (int c = 0; c < channels; c++)
                {
                    float value = chans[c][start + f];
                    interleaved[f * channels + c] = float.IsFinite(value) ? value : 0;
                }
            meter.Process(interleaved, 0, n * channels);
        }
        meter.FlushTruePeak();

        string Db(double linear) => linear <= 1e-7 ? "−∞" : $"{20 * Math.Log10(linear):0.00} dBFS";

        var result = new List<Row>();
        bool stereo = channels >= 2;
        result.Add(new Row("", channels == 1 ? "MONO" : "LEFT", stereo ? "RIGHT" : ""));
        result.Add(new Row("Peak", Db(peak[0]), stereo ? Db(peak[1]) : ""));
        result.Add(new Row("RMS (whole file)",
            $"{20 * Math.Log10(Math.Max(1e-7, Math.Sqrt(sumSq[0] / Math.Max(1, frames)))):0.0} dB",
            stereo ? $"{20 * Math.Log10(Math.Max(1e-7, Math.Sqrt(sumSq[1] / Math.Max(1, frames)))):0.0} dB" : ""));
        result.Add(new Row("DC offset", $"{dc[0] * 100:0.0000} %", stereo ? $"{dc[1] * 100:0.0000} %" : ""));
        result.Add(new Row("Clipped samples", clipped[0].ToString("N0"), stereo ? clipped[1].ToString("N0") : ""));
        if (invalid.Any(count => count > 0))
            result.Add(new Row("Invalid samples", invalid[0].ToString("N0"), stereo ? invalid[1].ToString("N0") : ""));
        for (int c = 2; c < channels; c++)
        {
            string rms = $"{20 * Math.Log10(Math.Max(1e-7, Math.Sqrt(sumSq[c] / Math.Max(1, frames)))):0.0} dB";
            result.Add(new Row($"Channel {c + 1}", Db(peak[c]),
                $"RMS {rms} · DC {dc[c] * 100:0.0000}% · {clipped[c]:N0} clipped · {invalid[c]:N0} invalid"));
        }
        result.Add(new Row("True peak (4×)", "", double.IsFinite(meter.TruePeakDb) ? $"{meter.TruePeakDb:0.00} dBTP" : "—"));
        result.Add(new Row("Integrated loudness", "", double.IsFinite(meter.IntegratedLufs) ? $"{meter.IntegratedLufs:0.0} LUFS" : "—"));
        result.Add(new Row("Loudness range", "", $"{meter.LoudnessRangeLu:0.0} LU"));
        result.Add(new Row("Sample rate", "", $"{doc.SampleRate:N0} Hz"));
        result.Add(new Row("Length", "", $"{TimeFormat.Position(frames, doc.SampleRate)} · {frames:N0} smp"));
        return result;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var row in _rows)
            sb.AppendLine($"{row.Label}\t{row.Left}\t{row.Right}".Trim());
        try { Clipboard.SetText(sb.ToString()); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
