using System.Collections.ObjectModel;
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
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD6, 0xC2));
    private static readonly Brush FaintBrush = new SolidColorBrush(Color.FromRgb(0x5D, 0x64, 0x6D));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xD0, 0x7A));

    public BatchConvertDialog()
    {
        InitializeComponent();
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
        var dlg = new OpenFileDialog { Filter = AudioImporter.OpenFilter, Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            if (_items.All(i => i.Path != f))
                _items.Add(new BatchItem { Path = f, StatusBrush = FaintBrush });
    }

    private void OnClearFiles(object sender, RoutedEventArgs e)
    {
        if (_cts == null) _items.Clear();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
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

        _cts = new CancellationTokenSource();
        btnStart.IsEnabled = false;
        btnCancel.Content = "Cancel";
        progress.Maximum = _items.Count;
        progress.Value = 0;

        var token = _cts.Token;
        int done = 0, failed = 0;

        foreach (var item in _items)
        {
            if (token.IsCancellationRequested) { item.Status = "cancelled"; item.StatusBrush = FaintBrush; continue; }
            item.Status = "converting…";
            item.StatusBrush = AccentBrush;
            try
            {
                await Task.Run(() =>
                {
                    var doc = AudioImporter.Load(item.Path);

                    if (chainName != null)
                    {
                        var preset = EffectFactory.LoadPresets().FirstOrDefault(p => p.Name == chainName);
                        if (preset != null)
                        {
                            var chain = EffectFactory.Instantiate(preset);
                            var section = new MasterSection();
                            section.ReplaceChain(chain);
                            var processed = section.ProcessOffline(doc.Channels.ToArray(), doc.SampleRate);
                            doc = new AudioDocument(processed, doc.SampleRate, doc.SourceBitDepth) { Title = doc.Title };
                        }
                    }

                    if (normalizeMode > 0) Normalize(doc, normalizeMode);

                    string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(item.Path) + ext);
                    AudioExporter.Export(doc, outPath, fmt, bitrate, 0, doc.Length, 0);
                }, token);
                item.Status = "✓ done";
                item.StatusBrush = GreenBrush;
                done++;
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

        _cts = null;
        btnStart.IsEnabled = true;
        btnCancel.Content = "Close";
    }

    private static void Normalize(AudioDocument doc, int mode)
    {
        if (mode == 1) // peak
        {
            float peak = 0;
            foreach (var ch in doc.Channels)
                foreach (var v in ch)
                    peak = Math.Max(peak, Math.Abs(v));
            if (peak <= 0) return;
            ApplyGain(doc, Math.Pow(10, -0.3 / 20.0) / peak);
            return;
        }

        double target = mode switch { 2 => -16, 3 => -14, _ => -23 };
        var meter = new LoudnessMeter();
        meter.Configure(doc.SampleRate, doc.ChannelCount);
        const int block = 65536;
        var buf = new float[block * doc.ChannelCount];
        for (int start = 0; start < doc.Length; start += block)
        {
            int n = Math.Min(block, doc.Length - start);
            doc.ReadInterleaved(start, n, buf, 0);
            meter.Process(buf, 0, n * doc.ChannelCount);
        }
        double current = meter.IntegratedLufs;
        if (!double.IsFinite(current)) return;
        ApplyGain(doc, Math.Pow(10, (target - current) / 20.0));
    }

    private static void ApplyGain(AudioDocument doc, double gain)
    {
        float g = (float)gain;
        foreach (var ch in doc.Channels)
            for (int i = 0; i < ch.Length; i++)
                ch[i] *= g;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
