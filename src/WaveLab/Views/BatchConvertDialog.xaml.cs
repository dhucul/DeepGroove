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

    public BatchConvertDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
        fileList.ItemsSource = _items;

        foreach (var f in (string[])["WAV — 32-bit float", "WAV — 24-bit", "WAV — 16-bit (dithered)", "MP3 · 192 kbps",
                     "MP3 · 320 kbps", "AAC · 192 kbps", "WMA · 192 kbps"])
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
            3 => (ExportFormat.Mp3, 192, ".mp3"),
            4 => (ExportFormat.Mp3, 320, ".mp3"),
            5 => (ExportFormat.Aac, 192, ".m4a"),
            6 => (ExportFormat.Wma, 192, ".wma"),
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
            else outputPaths[item] = output;
        }

        var operation = new CancellationTokenSource();
        _cts = operation;
        btnStart.IsEnabled = false;
        fileList.IsEnabled = cmbFormat.IsEnabled = cmbNormalize.IsEnabled = cmbChain.IsEnabled = txtOutput.IsEnabled = false;
        btnCancel.Content = "Cancel";
        progress.Maximum = queue.Length;
        progress.Value = 0;

        var token = operation.Token;
        int done = 0, failed = queue.Length - outputPaths.Count;

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

                    if (normalizeMode > 0) Normalize(doc, normalizeMode, token);

                    token.ThrowIfCancellationRequested();
                    AudioExporter.Export(doc, outPath, fmt, bitrate, 0, doc.Length, 0, token);
                }, token);
                item.Status = "✓ done";
                item.StatusBrush = GreenBrush;
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
            statusText.Text = $"{done} done · {failed} failed — output to {outDir}";
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

    private static void Normalize(AudioDocument doc, int mode, CancellationToken token)
    {
        var channels = doc.Channels.ToArray();
        if (channels.Length == 0) return;
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
            if (peak <= 0) return;
            ApplyGain(channels, Math.Pow(10, -0.3 / 20.0) / peak, token);
            return;
        }

        double target = mode switch { 2 => -16, 3 => -14, _ => -23 };
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
        double current = meter.IntegratedLufs;
        if (!double.IsFinite(current)) return;
        ApplyGain(channels, Math.Pow(10, (target - current) / 20.0), token);
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
