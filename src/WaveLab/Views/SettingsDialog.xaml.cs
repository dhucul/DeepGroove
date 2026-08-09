using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveLab.Audio;
using WaveLab.Util;

namespace WaveLab.Views;

public partial class SettingsDialog : Window
{
    private sealed record DeviceItem(string? Id, string Name) { public override string ToString() => Name; }
    private sealed record FormatItem(string Key, string Name) { public override string ToString() => Name; }

    public bool Saved { get; private set; }

    public SettingsDialog()
    {
        InitializeComponent();
        var s = AppSettings.Instance;

        chkReopen.IsChecked = s.ReopenLastSession;
        sldUndo.Value = Math.Clamp(s.UndoLimitMb, 64, 4096);
        chkAutosave.IsChecked = s.AutosaveEnabled;

        cmbOutput.Items.Add(new DeviceItem(null, "System default"));
        try
        {
            foreach (var (id, name) in PlaybackEngine.GetOutputDevices())
                cmbOutput.Items.Add(new DeviceItem(id, name));
        }
        catch (Exception ex) { cmbOutput.ToolTip = "Output devices could not be enumerated: " + ex.Message; }
        cmbOutput.SelectedIndex = 0;
        foreach (DeviceItem item in cmbOutput.Items)
            if (item.Id == s.OutputDeviceId) { cmbOutput.SelectedItem = item; break; }

        cmbInput.Items.Add(new DeviceItem(null, "System default"));
        try
        {
            foreach (var (id, name) in RecordingEngine.GetCaptureDevices())
                cmbInput.Items.Add(new DeviceItem(id, name));
        }
        catch (Exception ex) { cmbInput.ToolTip = "Input devices could not be enumerated: " + ex.Message; }
        cmbInput.SelectedIndex = 0;
        foreach (DeviceItem item in cmbInput.Items)
            if (item.Id == s.InputDeviceId) { cmbInput.SelectedItem = item; break; }

        sldBuffer.Value = Math.Clamp(s.BufferMs, 20, 200);

        foreach (var m in Intervals) cmbAutosaveInterval.Items.Add($"Every {m} min");
        int intervalIdx = Array.IndexOf(Intervals, s.AutosaveMinutes);
        cmbAutosaveInterval.SelectedIndex = intervalIdx >= 0 ? intervalIdx : 2;

        foreach (var f in Formats()) cmbExportFormat.Items.Add(f);
        cmbExportFormat.SelectedIndex = 0;
        foreach (FormatItem item in cmbExportFormat.Items)
            if (item.Key == s.ExportFormat) { cmbExportFormat.SelectedItem = item; break; }

        foreach (var b in Bitrates) cmbExportBitrate.Items.Add($"{b} kbps");
        int bitrateIdx = Array.IndexOf(Bitrates, s.ExportBitrateKbps);
        cmbExportBitrate.SelectedIndex = bitrateIdx >= 0 ? bitrateIdx : 2;

        UpdateLabels();
        UpdateExportFormatUi();
    }

    private static readonly int[] Intervals = [1, 2, 3, 5, 10, 15];
    private static readonly int[] Bitrates = [128, 160, 192, 256, 320];

    private static FormatItem[] Formats() =>
    [
        new("wav32", "Uncompressed WAV · 32-bit float"),
        new("wav24", "Uncompressed WAV · 24-bit PCM"),
        new("wav16", "Uncompressed WAV · 16-bit PCM (dithered)"),
        new("wav16nodither", "Uncompressed WAV · 16-bit PCM (no dither)"),
        new("flac", "Lossless FLAC · 24-bit"),
        new("mp3", "Lossy MP3"),
        new("aac", "Lossy AAC (M4A)"),
        new("wma", "Lossy WMA"),
    ];

    private void UpdateLabels()
    {
        if (lblUndo != null) lblUndo.Text = $"{(int)sldUndo.Value} MB";
        if (lblBuffer != null) lblBuffer.Text = $"{(int)sldBuffer.Value} ms";
    }

    private void OnUndoSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void OnBufferSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void OnExportFormatChanged(object sender, SelectionChangedEventArgs e) => UpdateExportFormatUi();

    private void UpdateExportFormatUi()
    {
        if (exportBitratePanel == null) return;
        string? key = (cmbExportFormat.SelectedItem as FormatItem)?.Key;
        exportBitratePanel.Visibility = key is "mp3" or "aac" or "wma"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (pageGeneral == null) return;
        pageGeneral.Visibility = navGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pageAudio.Visibility = navAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pageAutosave.Visibility = navAutosave.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        pageExport.Visibility = navExport.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        chkReopen.IsChecked = true;
        sldUndo.Value = 512;
        cmbOutput.SelectedIndex = 0;
        cmbInput.SelectedIndex = 0;
        sldBuffer.Value = 60;
        chkAutosave.IsChecked = true;
        cmbAutosaveInterval.SelectedIndex = 2;
        cmbExportFormat.SelectedIndex = 0;
        cmbExportBitrate.SelectedIndex = 2;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Instance;
        var previous = (s.ReopenLastSession, s.UndoLimitMb, s.OutputDeviceId, s.InputDeviceId,
            s.BufferMs, s.AutosaveEnabled, s.AutosaveMinutes, s.ExportFormat, s.ExportBitrateKbps);
        s.ReopenLastSession = chkReopen.IsChecked == true;
        s.UndoLimitMb = (int)sldUndo.Value;
        s.OutputDeviceId = (cmbOutput.SelectedItem as DeviceItem)?.Id;
        s.InputDeviceId = (cmbInput.SelectedItem as DeviceItem)?.Id;
        s.BufferMs = (int)sldBuffer.Value;
        s.AutosaveEnabled = chkAutosave.IsChecked == true;
        s.AutosaveMinutes = Intervals[Math.Max(0, cmbAutosaveInterval.SelectedIndex)];
        s.ExportFormat = (cmbExportFormat.SelectedItem as FormatItem)?.Key ?? "wav32";
        s.ExportBitrateKbps = Bitrates[Math.Max(0, cmbExportBitrate.SelectedIndex)];
        if (!s.Save())
        {
            (s.ReopenLastSession, s.UndoLimitMb, s.OutputDeviceId, s.InputDeviceId,
                s.BufferMs, s.AutosaveEnabled, s.AutosaveMinutes, s.ExportFormat, s.ExportBitrateKbps) = previous;
            MessageBox.Show("Settings could not be saved:\n" + s.LastSaveError, "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AudioDocument.UndoBudgetBytes = s.UndoLimitBytes;
        Saved = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
