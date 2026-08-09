using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;

namespace WaveLab.Views;

public partial class ExportDialog : Window
{
    private sealed record FormatItem(ExportFormat Format, string Key, string Name)
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
            new(ExportFormat.Wav32Float, "wav32", "WAV — 32-bit float"),
            new(ExportFormat.Wav24, "wav24", "WAV — 24-bit"),
            new(ExportFormat.Wav16, "wav16", "WAV — 16-bit (dithered)"),
            new(ExportFormat.Mp3, "mp3", "MP3"),
            new(ExportFormat.Aac, "aac", "AAC (M4A)"),
            new(ExportFormat.Wma, "wma", "WMA"),
        };
        if (AudioExporter.FlacAvailable())
            formats.Add(new FormatItem(ExportFormat.Flac, "flac", "FLAC"));

        foreach (var f in formats) cmbFormat.Items.Add(f);
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
        cmbBitrate.IsEnabled = AudioExporter.IsLossy(f.Format);
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
            await Task.Run(() => AudioExporter.Export(
                doc, dlg.FileName, f.Format, bitrate, start, count, targetRate, cts.Token), cts.Token);
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
