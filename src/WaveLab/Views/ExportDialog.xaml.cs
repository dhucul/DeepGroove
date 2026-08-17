using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class ExportDialog : Window
{
    private sealed record FormatItem(
        ExportFormat Format,
        string Key,
        string Name,
        string Category,
        string Hint)
    {
        public override string ToString() => Name;
    }

    private readonly DocumentViewModel _doc;
    private CancellationTokenSource? _cts;
    private bool _allowClose;
    private bool _closeWhenFinished;

    public ExportDialog(DocumentViewModel doc)
    {
        InitializeComponent();
        _doc = doc;
        Closing += OnClosing;
        titleText.Text = $"Export — {doc.Doc.Title}";

        var formats = new List<FormatItem>
        {
            new(ExportFormat.Wav32Float, "wav32", "WAV · 32-bit float", "UNCOMPRESSED",
                "WAV stores uncompressed audio. 32-bit float preserves the editor's full working precision."),
            new(ExportFormat.Wav24, "wav24", "WAV · 24-bit PCM", "UNCOMPRESSED",
                "WAV stores uncompressed audio. 24-bit PCM is a high-resolution delivery format."),
            new(ExportFormat.Wav16, "wav16", "WAV · 16-bit PCM (dithered)", "UNCOMPRESSED",
                "WAV stores uncompressed audio. TPDF dither is applied when reducing processed audio to 16-bit PCM."),
            new(ExportFormat.Wav16Undithered, "wav16nodither", "WAV · 16-bit PCM (no dither)", "UNCOMPRESSED",
                "WAV stores uncompressed audio. No dither is added before 16-bit quantization."),
            new(ExportFormat.Aiff32, "aiff32", "AIFF · 32-bit PCM", "UNCOMPRESSED",
                "AIFF stores portable, uncompressed big-endian audio as 32-bit integer PCM."),
            new(ExportFormat.Aiff24, "aiff24", "AIFF · 24-bit PCM", "UNCOMPRESSED",
                "AIFF stores portable, uncompressed audio. 24-bit PCM is a high-resolution delivery format."),
            new(ExportFormat.Aiff16, "aiff16", "AIFF · 16-bit PCM (dithered)", "UNCOMPRESSED",
                "TPDF dither is applied when reducing processed audio to 16-bit AIFF PCM."),
            new(ExportFormat.Aiff16Undithered, "aiff16nodither", "AIFF · 16-bit PCM (no dither)", "UNCOMPRESSED",
                "No dither is added before 16-bit AIFF quantization."),
        };
        if (AudioExporter.FlacAvailable())
            formats.Add(new FormatItem(ExportFormat.Flac, "flac", "FLAC · 24-bit", "LOSSLESS COMPRESSED",
                "FLAC reduces file size without discarding audio information."));
        formats.AddRange([
            new(ExportFormat.Mp3, "mp3", "MP3", "LOSSY",
                "MP3 makes a smaller file by discarding audio information. Choose a bitrate below."),
            new(ExportFormat.Aac, "aac", "AAC (M4A)", "LOSSY",
                "AAC makes a smaller file by discarding audio information. Choose a bitrate below."),
            new(ExportFormat.Wma, "wma", "WMA", "LOSSY",
                "WMA makes a smaller file by discarding audio information. Choose a bitrate below."),
        ]);

        var formatsView = CollectionViewSource.GetDefaultView(formats);
        formatsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FormatItem.Category)));
        cmbFormat.ItemsSource = formatsView;
        cmbFormat.SelectedIndex = 0;
        foreach (FormatItem item in cmbFormat.Items)
            if (item.Key == AppSettings.Instance.ExportFormat) { cmbFormat.SelectedItem = item; break; }

        foreach (var b in (int[])[128, 160, 192, 256, 320]) cmbBitrate.Items.Add($"{b} kbps");
        int bi = Array.IndexOf([128, 160, 192, 256, 320], AppSettings.Instance.ExportBitrateKbps);
        cmbBitrate.SelectedIndex = bi >= 0 ? bi : 2;

        cmbRate.Items.Add($"Keep ({_doc.Doc.SampleRate / 1000.0:0.###} kHz)");
        foreach (var r in (int[])[44100, 48000, 88200, 96000]) cmbRate.Items.Add($"{r / 1000.0:0.###} kHz");
        cmbRate.SelectedIndex = 0;

        cmbRange.Items.Add("Whole file");
        if (_doc.HasSelection) cmbRange.Items.Add($"Selection ({_doc.SelLenText})");
        cmbRange.SelectedIndex = _doc.HasSelection ? 1 : 0;

        OnFormatChanged(this, null!);
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmbBitrate == null || cmbFormat.SelectedItem is not FormatItem f) return;
        bool lossy = AudioExporter.IsLossy(f.Format);
        cmbBitrate.IsEnabled = lossy;
        bitratePanel.Visibility = lossy ? Visibility.Visible : Visibility.Collapsed;
        bitrateColumn.Width = lossy ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        bitrateGap.Width = lossy ? new GridLength(14) : new GridLength(0);
        formatHint.Text = f.Hint;
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        if (cmbFormat.SelectedItem is not FormatItem f) return;

        var dlg = new SaveFileDialog
        {
            Filter = AudioExporter.FilterFor(f.Format),
            FileName = Path.GetFileNameWithoutExtension(_doc.Doc.Title),
        };
        if (dlg.ShowDialog() != true) return;

        int bitrate = ((int[])[128, 160, 192, 256, 320])[Math.Max(0, cmbBitrate.SelectedIndex)];
        int targetRate = cmbRate.SelectedIndex switch
        {
            1 => 44100, 2 => 48000, 3 => 88200, 4 => 96000, _ => 0,
        };
        int start = 0, count = _doc.Doc.Length;
        if (cmbRange.SelectedIndex == 1 && _doc.HasSelection)
        {
            start = _doc.SelStart;
            count = _doc.SelEnd - _doc.SelStart;
        }

        btnExport.IsEnabled = false;
        btnCancel.IsEnabled = true;
        btnCancel.Content = "Cancel";
        busyText.Text = "Exporting…";
        busyText.Visibility = Visibility.Visible;
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var doc = _doc.Doc;
            string outputPath = dlg.FileName;

            // Whatever File Information holds, falling back to the output name for the title so an
            // untagged document still produces an MP3 that identifies itself.
            FileTags fileTags = FileTags.ReadFrom(doc.Riff);
            if (string.IsNullOrWhiteSpace(fileTags.Title))
                fileTags.Title = Path.GetFileNameWithoutExtension(outputPath);
            Id3Tags tags = fileTags.ToId3();
            await Task.Run(() => AudioExporter.Export(
                doc, outputPath, f.Format, bitrate, start, count, targetRate, cts.Token, tags),
                cts.Token);
            _allowClose = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            busyText.Text = "Export cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            btnExport.IsEnabled = true;
            btnCancel.IsEnabled = true;
            btnCancel.Content = "Close";
            if (!_closeWhenFinished && busyText.Text != "Export cancelled.")
                busyText.Visibility = Visibility.Collapsed;
            if (_closeWhenFinished && !_allowClose)
            {
                _allowClose = true;
                DialogResult = false;
                Close();
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            busyText.Text = "Cancelling…";
            _cts.Cancel();
            return;
        }
        DialogResult = false;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _cts == null) return;
        e.Cancel = true;
        _closeWhenFinished = true;
        busyText.Text = "Cancelling…";
        _cts.Cancel();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
