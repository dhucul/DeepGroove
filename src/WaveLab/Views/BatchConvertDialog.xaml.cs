using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.Util;

namespace WaveLab.Views;

public partial class BatchConvertDialog : Window
{
    public sealed class BatchItem : Util.ObservableObject
    {
        private string _status = "queued";
        private Brush _statusBrush = Brushes.Gray;
        public required string Path { get; init; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Status { get => _status; set => Set(ref _status, value); }
        public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    }

    private readonly ObservableCollection<BatchItem> _items = [];
    private CancellationTokenSource? _cts;
    private bool _allowClose;
    private bool _closeWhenFinished;
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD6, 0xC2));
    private static readonly Brush FaintBrush = new SolidColorBrush(Color.FromRgb(0x5D, 0x64, 0x6D));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD0, 0x7A));

    /// <summary>
    /// Done, but not at the target. It cannot be <see cref="AccentBrush"/>, which is what a row
    /// still converting wears — colour is what an unattended queue is scanned by, and a finished
    /// row and an in-flight one must not read the same at a glance. The shade matches the one
    /// <see cref="MatchLoudnessDialog"/> already uses for a true-peak-limited track.
    /// </summary>
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA8, 0x3C));

    public BatchConvertDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
        fileList.ItemsSource = _items;

        foreach (var f in (string[])["Uncompressed WAV · 32-bit float", "Uncompressed WAV · 24-bit PCM",
                     "Uncompressed WAV · 16-bit PCM (dithered)", "Uncompressed WAV · 16-bit PCM (no dither)",
                     "Uncompressed AIFF · 32-bit PCM", "Uncompressed AIFF · 24-bit PCM",
                     "Uncompressed AIFF · 16-bit PCM (dithered)", "Uncompressed AIFF · 16-bit PCM (no dither)",
                     "Lossy MP3 · 192 kbps", "Lossy MP3 · 320 kbps", "Lossy AAC · 192 kbps", "Lossy WMA · 192 kbps"])
            cmbFormat.Items.Add(f);
        cmbFormat.SelectedIndex = 0;

        foreach (var f in (string[])["Off", "Peak −0.3 dBFS", "−16 LUFS (streaming)", "−14 LUFS (loud)", "−23 LUFS (broadcast)"])
            cmbNormalize.Items.Add(f);
        cmbNormalize.SelectedIndex = 0;

        cmbChain.Items.Add("None");
        foreach (var p in EffectFactory.LoadPresets()) cmbChain.Items.Add(p.Name);
        cmbChain.SelectedIndex = 0;

        txtOutput.Text = AppSettings.Instance.LastOpenFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        statusText.Text = "Add files, pick a format and output folder, then Start.";
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var dlg = new OpenFileDialog { Filter = AudioImporter.OpenFilter, Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            if (_items.All(i => !string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase)))
                _items.Add(new BatchItem { Path = f, StatusBrush = FaintBrush });
    }

    private void OnClearFiles(object sender, RoutedEventArgs e)
    {
        if (_cts == null) _items.Clear();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) txtOutput.Text = dlg.FolderName;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        if (_items.Count == 0 || !Directory.Exists(txtOutput.Text))
        {
            statusText.Text = _items.Count == 0 ? "Add some files first." : "Choose a valid output folder.";
            return;
        }

        (ExportFormat fmt, int bitrate, string ext) = cmbFormat.SelectedIndex switch
        {
            1 => (ExportFormat.Wav24, 0, ".wav"),
            2 => (ExportFormat.Wav16, 0, ".wav"),
            3 => (ExportFormat.Wav16Undithered, 0, ".wav"),
            4 => (ExportFormat.Aiff32, 0, ".aiff"),
            5 => (ExportFormat.Aiff24, 0, ".aiff"),
            6 => (ExportFormat.Aiff16, 0, ".aiff"),
            7 => (ExportFormat.Aiff16Undithered, 0, ".aiff"),
            8 => (ExportFormat.Mp3, 192, ".mp3"),
            9 => (ExportFormat.Mp3, 320, ".mp3"),
            10 => (ExportFormat.Aac, 192, ".m4a"),
            11 => (ExportFormat.Wma, 192, ".wma"),
            _ => (ExportFormat.Wav32Float, 0, ".wav"),
        };
        int normalizeMode = cmbNormalize.SelectedIndex;
        string? chainName = cmbChain.SelectedIndex > 0 ? cmbChain.SelectedItem as string : null;
        string outDir = txtOutput.Text;
        var queue = _items.ToArray();
        var presetDefinition = chainName == null
            ? null
            : EffectFactory.LoadPresets().FirstOrDefault(p => p.Name == chainName);
        if (chainName != null && presetDefinition == null)
        {
            statusText.Text = "The selected effect chain is no longer available. Choose another chain.";
            return;
        }
        var outputPaths = new Dictionary<BatchItem, string>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = queue.Select(item => Path.GetFullPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingOutputs = new List<BatchItem>();
        foreach (var item in queue)
        {
            string output = Path.GetFullPath(Path.Combine(outDir, Path.GetFileNameWithoutExtension(item.Path) + ext));
            if (sourcePaths.Contains(output))
            {
                item.Status = "failed: output collides with a source file";
                item.StatusBrush = RedBrush;
            }
            else if (!usedPaths.Add(output))
            {
                item.Status = "failed: output path collision";
                item.StatusBrush = RedBrush;
            }
            else
            {
                outputPaths[item] = output;
                // AudioExporter.Export finishes with File.Move(..., overwrite: true), so an
                // unrelated file already sitting on the output path is destroyed silently.
                if (File.Exists(output)) existingOutputs.Add(item);
            }
        }

        int failed = queue.Length - outputPaths.Count;
        int skipped = 0;
        if (existingOutputs.Count > 0)
        {
            // One prompt for the whole batch — prompting per file is unusable on a long queue.
            const int listLimit = 8;
            string names = string.Join(Environment.NewLine,
                existingOutputs.Take(listLimit).Select(i => "    " + Path.GetFileName(outputPaths[i])));
            if (existingOutputs.Count > listLimit)
                names += $"{Environment.NewLine}    …and {existingOutputs.Count - listLimit} more";
            var answer = MessageBox.Show(this,
                $"{existingOutputs.Count} file(s) in the output folder would be overwritten:{Environment.NewLine}{Environment.NewLine}"
                + names + Environment.NewLine + Environment.NewLine
                + "Overwrite them? Choose No to skip these files and convert the rest of the queue.",
                "Batch Converter", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                foreach (var item in existingOutputs)
                {
                    item.Status = "skipped: file already exists";
                    item.StatusBrush = FaintBrush;
                    outputPaths.Remove(item);
                    skipped++;
                }
        }

        var operation = new CancellationTokenSource();
        _cts = operation;
        btnStart.IsEnabled = false;
        fileList.IsEnabled = cmbFormat.IsEnabled = cmbNormalize.IsEnabled = cmbChain.IsEnabled = txtOutput.IsEnabled = false;
        btnCancel.Content = "Cancel";
        progress.Maximum = queue.Length;
        progress.Value = 0;

        var token = operation.Token;
        int done = 0;
        int limited = 0;
        string skippedText = skipped > 0 ? $" · {skipped} skipped" : "";

        try
        {
          foreach (var item in queue)
          {
            if (!outputPaths.TryGetValue(item, out string? outPath)) { progress.Value++; continue; }
            if (token.IsCancellationRequested)
            {
                item.Status = "cancelled"; item.StatusBrush = FaintBrush; progress.Value++; continue;
            }
            item.Status = "converting…";
            item.StatusBrush = AccentBrush;
            // Assigned inside the background work and read after it: a batch runs unattended, so a
            // file that could not reach the target has to leave a mark that is still there when
            // someone comes back to the window.
            double shortfallDb = 0;
            try
            {
                await Task.Run(() =>
                {
                    var doc = AudioImporter.Load(item.Path, token);
                    token.ThrowIfCancellationRequested();

                    if (presetDefinition != null)
                    {
                        var chain = EffectFactory.Instantiate(presetDefinition);
                        var section = new MasterSection();
                        section.ReplaceChain(chain);
                        var processed = section.ProcessOffline(doc.Channels.ToArray(), doc.SampleRate, token);
                        token.ThrowIfCancellationRequested();
                        doc = new AudioDocument(processed, doc.SampleRate, doc.SourceBitDepth) { Title = doc.Title };
                    }

                    if (normalizeMode > 0) shortfallDb = Normalize(doc, normalizeMode, token);

                    token.ThrowIfCancellationRequested();
                    AudioExporter.Export(doc, outPath, fmt, bitrate, 0, doc.Length, 0, token);
                }, token);
                bool heldBack = shortfallDb > LoudnessMatch.NegligibleGainDb;
                item.Status = heldBack
                    ? $"✓ done · true-peak limited, {shortfallDb:0.0} dB short of target"
                    : "✓ done";
                item.StatusBrush = heldBack ? AmberBrush : GreenBrush;
                if (heldBack) limited++;
                done++;
            }
            catch (OperationCanceledException)
            {
                item.Status = "cancelled";
                item.StatusBrush = FaintBrush;
            }
            catch (Exception ex)
            {
                item.Status = "failed: " + ex.Message.Split('\n')[0];
                item.StatusBrush = RedBrush;
                failed++;
            }
            progress.Value++;
            statusText.Text = $"{done} done · {failed} failed{skippedText}"
                + (limited > 0 ? $" · {limited} true-peak limited" : "")
                + $" — output to {outDir}";
          }
        }
        finally
        {
            if (ReferenceEquals(_cts, operation)) _cts = null;
            operation.Dispose();
            btnStart.IsEnabled = true;
            fileList.IsEnabled = cmbFormat.IsEnabled = cmbNormalize.IsEnabled = cmbChain.IsEnabled = txtOutput.IsEnabled = true;
            btnCancel.Content = "Close";
            if (_closeWhenFinished)
            {
                _allowClose = true;
                Close();
            }
        }
    }

    /// <summary>Levels one document, and says how far the true-peak ceiling held it back.</summary>
    /// <remarks>
    /// <para>
    /// The LUFS branch used to apply what loudness asked for and nothing else — the defect
    /// <see cref="LoudnessMatch"/> names in its own remarks, which would push a track past 0 dBTP
    /// and report nothing. The gain now comes from <see cref="LoudnessMatch.Plan"/>, the same seam
    /// Normalize Loudness and Match Loudness take, so a ceiling cannot be honoured in the two paths
    /// with someone watching and skipped in the one without.
    /// </para>
    /// <para>
    /// The true peak this needs costs nothing: the meter already driven over the whole file for the
    /// loudness figure computes it, and it was previously read and discarded.
    /// </para>
    /// </remarks>
    /// <returns>
    /// How far short of the requested target the ceiling left this file, in dB. Zero when nothing
    /// was held back, which is also what an unmeasurable or peak-mode file returns.
    /// </returns>
    private static double Normalize(AudioDocument doc, int mode, CancellationToken token)
    {
        var channels = doc.Channels.ToArray();
        if (channels.Length == 0) return 0;
        int frames = channels[0].Length;
        foreach (var channel in channels)
        {
            if (channel.Length != frames)
                throw new InvalidDataException("Audio channels do not have a consistent frame count.");
            for (int i = 0; i < channel.Length; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                if (!float.IsFinite(channel[i])) channel[i] = 0;
            }
        }

        if (mode == 1) // peak
        {
            float peak = 0;
            foreach (var ch in channels)
                for (int i = 0; i < ch.Length; i++)
                {
                    if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                    peak = Math.Max(peak, Math.Abs(ch[i]));
                }
            if (peak <= 0) return 0;
            ApplyGain(channels, Math.Pow(10, -0.3 / 20.0) / peak, token);
            return 0;
        }

        // The three LUFS entries are the delivery presets they name, taken whole so the ceiling
        // travels with the target instead of being restated — and forgotten — here.
        LoudnessTarget target = mode switch
        {
            2 => LoudnessTarget.Apple,      // −16 LUFS, ≤ −1 dBTP
            3 => LoudnessTarget.Streaming,  // −14 LUFS, ≤ −1 dBTP
            _ => LoudnessTarget.Ebu,        // −23 LUFS, ≤ −1 dBTP
        };
        var meter = new LoudnessMeter();
        meter.Configure(doc.SampleRate, channels.Length);
        const int block = 65536;
        var buf = new float[block * channels.Length];
        for (int start = 0; start < frames; start += block)
        {
            token.ThrowIfCancellationRequested();
            int n = Math.Min(block, frames - start);
            for (int frame = 0; frame < n; frame++)
                for (int channel = 0; channel < channels.Length; channel++)
                    buf[frame * channels.Length + channel] = channels[channel][start + frame];
            meter.Process(buf, 0, n * channels.Length);
        }
        // Only now the loop is done: flushing rings the final taps out against zeros, which reads
        // high on a measurement that is still running.
        meter.FlushTruePeak();
        double current = meter.IntegratedLufs;
        if (!double.IsFinite(current)) return 0;

        LoudnessMatchStep step = LoudnessMatch.Plan(
            [new LoudnessMeasurement(doc.Title, current, meter.TruePeakDb, meter.LoudnessRangeLu,
                doc.SampleRate, frames)],
            LoudnessMatchMode.Target,
            target).Steps[0];

        // A gain under the negligible threshold is not worth a pass over the file, but the
        // shortfall is still worth reporting: it is why the file did not reach the target.
        if (step.CanApply) ApplyGain(channels, Math.Pow(10, step.GainDb / 20.0), token);
        return step.ShortfallDb;
    }

    private static void ApplyGain(float[][] channels, double gain, CancellationToken token)
    {
        if (!double.IsFinite(gain) || gain <= 0)
            throw new InvalidDataException("Normalization produced an invalid gain value.");
        foreach (var ch in channels)
            for (int i = 0; i < ch.Length; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                double scaled = ch[i] * gain;
                if (!double.IsFinite(scaled) || scaled > float.MaxValue || scaled < -float.MaxValue)
                    throw new InvalidDataException("Normalization would produce non-finite audio samples.");
                ch[i] = (float)scaled;
            }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            statusText.Text = "Cancelling…";
            _cts.Cancel();
            return;
        }
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _cts == null) return;
        e.Cancel = true;
        _closeWhenFinished = true;
        statusText.Text = "Cancelling…";
        _cts.Cancel();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
