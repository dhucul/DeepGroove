using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WaveLab.Audio;
using WaveLab.Util;

namespace WaveLab.Views;

public partial class CdImportDialog : Window
{
    private sealed class DriveRow
    {
        public DriveRow(CdAudioDrive drive) => Drive = drive;
        public CdAudioDrive Drive { get; }
        public string DisplayText => Drive.Status == CdAudioDriveStatus.Ready
            ? $"{Drive.Device.DisplayName} · {Drive.Disc!.AudioTracks.Count} audio track(s)"
            : $"{Drive.Device.DisplayName} · {StatusLabel(Drive.Status)}";

        private static string StatusLabel(CdAudioDriveStatus status) => status switch
        {
            CdAudioDriveStatus.NoMedia => "no disc",
            CdAudioDriveStatus.NoAudioTracks => "no audio tracks",
            CdAudioDriveStatus.AccessDenied => "access denied",
            CdAudioDriveStatus.Unsupported => "unsupported drive",
            _ => "unavailable",
        };
    }

    private sealed class TrackRow : ObservableObject
    {
        private bool _selected = true;
        public TrackRow(CdAudioTrack track) => Track = track;
        public CdAudioTrack Track { get; }
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
        public string Name => $"Track {Track.Number:00}";
        public string StartText => TimeFormat.Position((long)Track.StartSector * CdAudioFormat.FramesPerSector, CdAudioFormat.SampleRate);
        public string DurationText => TimeFormat.Compact(Track.Duration.TotalSeconds);
    }

    private readonly ICdAudioService _service;
    private readonly ObservableCollection<TrackRow> _tracks = [];
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _allowClose;

    public IReadOnlyList<CdAudioTrackImport> Imports { get; private set; } = [];

    public CdImportDialog(ICdAudioService? service = null)
    {
        InitializeComponent();
        _service = service ?? new CdAudioService();
        trackList.ItemsSource = _tracks;
        Loaded += async (_, _) => await RefreshDrivesAsync();
        Closing += (_, e) =>
        {
            if (!_busy || _allowClose) return;
            e.Cancel = true;
            _operation?.Cancel();
            statusText.Text = "Cancelling the current CD operation…";
        };
        Closed += (_, _) => _operation?.Cancel();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshDrivesAsync();

    private async Task RefreshDrivesAsync()
    {
        if (_busy) return;
        SetBusy(true, "Scanning optical drives…");
        _tracks.Clear();
        driveCombo.ItemsSource = null;
        try
        {
            _operation = new CancellationTokenSource();
            var drives = await _service.EnumerateDrivesAsync(_operation.Token);
            var rows = drives.Select(d => new DriveRow(d)).ToList();
            driveCombo.ItemsSource = rows;
            var ready = rows.FirstOrDefault(r => r.Drive.Status == CdAudioDriveStatus.Ready);
            driveCombo.SelectedItem = ready ?? rows.FirstOrDefault();
            statusText.Text = rows.Count == 0
                ? "No optical drives were found."
                : ready == null ? "Insert an audio CD, then choose Refresh." : "Choose tracks to extract.";
        }
        catch (OperationCanceledException) { statusText.Text = "Scan cancelled."; }
        catch (Exception ex) { statusText.Text = ex.Message; }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            SetBusy(false, statusText.Text);
            UpdateSelectionSummary();
        }
    }

    private void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        _tracks.Clear();
        if (driveCombo.SelectedItem is DriveRow { Drive: { Status: CdAudioDriveStatus.Ready, Disc: not null } drive })
            foreach (var track in drive.Disc.AudioTracks)
            {
                var row = new TrackRow(track);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(TrackRow.Selected)) UpdateSelectionSummary();
                };
                _tracks.Add(row);
            }
        UpdateSelectionSummary();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        bool value = selectAll.IsChecked == true;
        foreach (var track in _tracks) track.Selected = value;
        UpdateSelectionSummary();
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_busy || driveCombo.SelectedItem is not DriveRow { Drive.Status: CdAudioDriveStatus.Ready } drive) return;
        var selected = _tracks.Where(t => t.Selected).Select(t => t.Track.Number).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select at least one audio track.", "Extract Audio CD",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Starting extraction…");
        _operation = new CancellationTokenSource();
        cancelBtn.Content = "Cancel Extraction";
        try
        {
            var progress = new Progress<CdAudioExtractionProgress>(p =>
            {
                progressBar.Value = p.TotalFraction;
                statusText.Text = $"Extracting track {p.TrackNumber:00} · {p.TrackFraction:P0} · overall {p.TotalFraction:P0}";
            });
            Imports = await _service.ExtractTracksAsync(
                drive.Drive.Device.DevicePath, selected, progress, _operation.Token);
            progressBar.Value = 1;
            _allowClose = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "Extraction cancelled; no partial tracks were imported.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "CD extraction failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            statusText.Text = "The disc was not imported.";
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            cancelBtn.Content = "Cancel";
            SetBusy(false, statusText.Text);
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        driveCombo.IsEnabled = !busy;
        trackList.IsEnabled = !busy;
        selectAll.IsEnabled = !busy;
        importBtn.IsEnabled = !busy && _tracks.Any(t => t.Selected) &&
                              driveCombo.SelectedItem is DriveRow { Drive.Status: CdAudioDriveStatus.Ready };
        statusText.Text = status;
        if (!busy) progressBar.Value = 0;
    }

    private void UpdateSelectionSummary()
    {
        int count = _tracks.Count(t => t.Selected);
        double seconds = _tracks.Where(t => t.Selected).Sum(t => t.Track.Duration.TotalSeconds);
        summaryText.Text = count == 0 ? "No tracks selected" : $"{count} track(s) · {TimeFormat.Compact(seconds)}";
        selectAll.IsChecked = _tracks.Count > 0 && count == _tracks.Count;
        importBtn.IsEnabled = !_busy && count > 0 &&
                              driveCombo.SelectedItem is DriveRow { Drive.Status: CdAudioDriveStatus.Ready };
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_busy) _operation?.Cancel();
        else
        {
            _allowClose = true;
            DialogResult = false;
        }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
