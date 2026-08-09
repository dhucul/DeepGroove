using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.Util;

namespace WaveLab.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static float[][]? _clipboard;
    private static int _clipboardRate;

    private DocumentViewModel? _active;
    private DocumentViewModel? _playbackDocument;
    private AudioDocument? _previewDocument;
    private bool? _previewRackRestoreState;
    private int _playbackEditVersion = -1;
    private long _playbackSession;
    private long _stoppedPlaybackSession;
    private AudioDocument? _stoppedPlaybackSource;
    private DocumentViewModel? _seekDocument;
    private bool _resumeAfterSeek;
    private bool _isPlaying;
    private bool _isLooping;
    private readonly RecordingEngine _transportRecorder = new();
    private bool _isRecordArmed;
    private bool _isTransportRecording;
    private bool _isFinalizingRecording;
    private Task _recordFinalization = Task.CompletedTask;
    private long _expectedTransportRecordingSessionId;
    private double _transportPeakL = -60;
    private double _transportPeakR = -60;
    private string _recordInputName = "Default input";
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _autosaveTimer;
    private DateTime? _lastAutosave;
    private readonly Dictionary<Guid, int> _autosavedVersions = [];
    private readonly HashSet<Guid> _savesInFlight = [];
    private readonly HashSet<Task> _saveOperations = [];
    private readonly HashSet<Task> _openOperations = [];
    private readonly HashSet<Task> _tabCloseOperations = [];
    private readonly HashSet<Guid> _tabsClosing = [];
    private readonly Dictionary<Guid, string> _saveFailures = [];
    private Task _autosaveTask = Task.CompletedTask;
    private bool _startupLoaded;
    private bool _shuttingDown;
    private bool _editOperationRunning;
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _cpuPrev;
    private DateTime _cpuPrevAt = DateTime.UtcNow;
    private int _tickCount;
    private string _cpuText = "CPU —";
    private string _ramText = "RAM —";

    public MainViewModel()
    {
        AudioDocument.UndoBudgetBytes = AppSettings.Instance.UndoLimitBytes;
        EffectFactory.EnsureFactoryPresets();

        Engine = new PlaybackEngine();
        Master = new MasterSectionViewModel(Engine.Master);
        Master.ProcessingTopologyChanged += RestartMonoPlaybackForTopologyChange;
        Engine.PlaybackStopped += OnPlaybackStopped;
        Engine.PlaybackFailed += OnPlaybackFailed;
        _transportRecorder.CaptureStopped += OnTransportCaptureStopped;

        foreach (var f in AppSettings.Instance.RecentFiles) RecentFiles.Add(f);
        UpdateRecordInputName();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += (_, _) => AutosaveTick();
        _autosaveTimer.Start();

        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(Save);
        SaveAsCommand = new RelayCommand(SaveAs);
        CloseTabCommand = new RelayCommand<DocumentViewModel>(CloseTab);
        ExitCommand = new RelayCommand(() => Application.Current.MainWindow?.Close());

        UndoCommand = new RelayCommand(() => WithDoc(d => { PrepareForDocumentEdit(d); d.Doc.Undo(); }), () => _active?.Doc.CanUndo == true);
        RedoCommand = new RelayCommand(() => WithDoc(d => { PrepareForDocumentEdit(d); d.Doc.Redo(); }), () => _active?.Doc.CanRedo == true);
        CutCommand = new RelayCommand(Cut, () => !_editOperationRunning && _active?.HasSelection == true);
        CopyCommand = new RelayCommand(Copy, () => !_editOperationRunning && _active?.HasSelection == true);
        PasteCommand = new RelayCommand(Paste, () => !_editOperationRunning && _active != null && _clipboard != null);
        DeleteCommand = new RelayCommand(DeleteSelection, () => !_editOperationRunning && _active?.HasSelection == true);
        TrimCommand = new RelayCommand(Trim, () => !_editOperationRunning && _active?.HasSelection == true);
        SelectAllCommand = new RelayCommand(() => WithDoc(d => d.SelectAll()));

        PlayCommand = new RelayCommand(TogglePlay, () => !IsTransportRecording && !IsFinalizingRecording);
        StopCommand = new RelayCommand(StopTransport);
        GoToStartCommand = new RelayCommand(GoToStart);
        SeekCommand = new RelayCommand<PlayheadSeekRequest>(HandlePlayheadSeek);
        ToggleLoopCommand = new RelayCommand(() => IsLooping = !IsLooping);

        ZoomInCommand = new RelayCommand(() => WithDoc(d => d.ZoomBy(1 / 1.5)));
        ZoomOutCommand = new RelayCommand(() => WithDoc(d => d.ZoomBy(1.5)));
        ZoomFitCommand = new RelayCommand(() => WithDoc(d => d.ZoomFull()));
        ZoomSelectionCommand = new RelayCommand(() => WithDoc(d => d.ZoomToSelection()));

        GainUpCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.Gain(d, s, c, 3)));
        GainDownCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.Gain(d, s, c, -3)));
        NormalizeCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.Normalize(d, s, c, -0.3)));
        FadeInCommand = new RelayCommand(() => ApplyToRange(Processing.FadeIn));
        FadeOutCommand = new RelayCommand(() => ApplyToRange(Processing.FadeOut));
        ReverseCommand = new RelayCommand(() => ApplyToRange(Processing.Reverse));
        RemoveDcCommand = new RelayCommand(() => ApplyToRange(Processing.RemoveDcOffset));
        InsertSilenceCommand = new RelayCommand(() => WithDoc(d =>
        {
            PrepareForDocumentEdit(d);
            Processing.InsertSilence(d.Doc, d.Cursor, 1.0);
        }));

        AddMarkerCommand = new RelayCommand(() => WithDoc(d => d.AddMarker(
            d.HasSelection ? d.SelStart
            : IsPlaying && ReferenceEquals(d, _playbackDocument) ? d.PlayheadSample
            : d.Cursor)));
        AddRegionCommand = new RelayCommand(() => WithDoc(d => d.AddRegionFromSelection()));
        PrevMarkerCommand = new RelayCommand(() => WithDoc(d => d.JumpToNextMarker(forward: false)));
        NextMarkerCommand = new RelayCommand(() => WithDoc(d => d.JumpToNextMarker(forward: true)));
        ClearMarkersCommand = new RelayCommand(() => WithDoc(d =>
        {
            d.Markers.Clear();
            d.Regions.Clear();
            d.NotifyMarkersChanged();
        }));
        SmoothEditCommand = new RelayCommand(SmoothEditPoints);

        RenderCommand = new RelayCommand(RenderMaster);
        ApplyChainCommand = new RelayCommand(ApplyChain);
        RecordCommand = new RelayCommand(ToggleRecord, () => !IsFinalizingRecording);
        RecordSetupCommand = new RelayCommand(() => RequestRecordDialog?.Invoke(),
            () => !IsTransportRecording && !IsFinalizingRecording && !HasPendingTransportRecording);
        SettingsCommand = new RelayCommand(() => RequestSettingsDialog?.Invoke());
        ExportCommand = new RelayCommand(() => { if (_active != null) RequestExportDialog?.Invoke(); });
        StatisticsCommand = new RelayCommand(() => { if (_active != null) RequestStatisticsDialog?.Invoke(); });
        OpenRecentCommand = new RelayCommand<string>(path => { if (path != null) OpenFiles([path]); });
        CommandPaletteCommand = new RelayCommand(() => RequestCommandPalette?.Invoke());
        AboutCommand = new RelayCommand(() => MessageBox.Show(
            "WaveLab 2.0\n\nAudio editor and mastering suite.\nWAV 16/24/32-bit float · MP3/FLAC/AAC import & export\nEffects rack · restoration · EBU R128 metering\nWASAPI playback and recording",
            "About WaveLab", MessageBoxButton.OK, MessageBoxImage.Information));
    }

    public PlaybackEngine Engine { get; }
    public MasterSectionViewModel Master { get; }
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand<DocumentViewModel> CloseTabCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand TrimCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand GoToStartCommand { get; }
    public RelayCommand<PlayheadSeekRequest> SeekCommand { get; }
    public RelayCommand ToggleLoopCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomFitCommand { get; }
    public RelayCommand ZoomSelectionCommand { get; }
    public RelayCommand GainUpCommand { get; }
    public RelayCommand GainDownCommand { get; }
    public RelayCommand NormalizeCommand { get; }
    public RelayCommand FadeInCommand { get; }
    public RelayCommand FadeOutCommand { get; }
    public RelayCommand ReverseCommand { get; }
    public RelayCommand RemoveDcCommand { get; }
    public RelayCommand InsertSilenceCommand { get; }
    public RelayCommand AddMarkerCommand { get; }
    public RelayCommand AddRegionCommand { get; }
    public RelayCommand PrevMarkerCommand { get; }
    public RelayCommand NextMarkerCommand { get; }
    public RelayCommand ClearMarkersCommand { get; }
    public RelayCommand SmoothEditCommand { get; }
    public RelayCommand RenderCommand { get; }
    public RelayCommand ApplyChainCommand { get; }
    public RelayCommand RecordCommand { get; }
    public RelayCommand RecordSetupCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand StatisticsCommand { get; }
    public RelayCommand<string> OpenRecentCommand { get; }
    public RelayCommand CommandPaletteCommand { get; }
    public RelayCommand AboutCommand { get; }

    public ObservableCollection<string> RecentFiles { get; } = [];

    /// <summary>The window shows the record dialog when this fires.</summary>
    public event Action? RequestRecordDialog;
    /// <summary>Ask the window to refresh the spectrogram for the active view.</summary>
    public event Action? RequestSpectrogram;
    public event Action? RequestSettingsDialog;
    public event Action? RequestExportDialog;
    public event Action? RequestStatisticsDialog;
    public event Action? RequestCommandPalette;

    public DocumentViewModel? ActiveDocument
    {
        get => _active;
        set
        {
            var previous = _active;
            if (!Set(ref _active, value)) return;
            if (previous != null)
            {
                previous.PropertyChanged -= OnActiveDocumentPropertyChanged;
                previous.Doc.Changed -= OnActiveDocumentEdited;
            }
            if (_active != null)
            {
                _active.PropertyChanged += OnActiveDocumentPropertyChanged;
                _active.Doc.Changed += OnActiveDocumentEdited;
            }
            Raise(nameof(HasDocument));
            Raise(nameof(CanAnalyzeCleanup));
            Raise(nameof(WindowTitle));
            Raise(nameof(StatusSamples));
            RefreshEditCommandStates();
        }
    }

    public bool HasDocument => _active != null;
    public bool CanAnalyzeCleanup => HasDocument &&
                                     !IsTransportRecording &&
                                     !IsFinalizingRecording &&
                                     !HasPendingTransportRecording;
    public string WindowTitle => _active == null ? "WaveLab" : $"{_active.Doc.Title} — {_active.FormatText} · {TimeFormat.Compact(_active.Doc.Duration)}";

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => Set(ref _isPlaying, value);
    }

    public bool IsLooping
    {
        get => _isLooping;
        set { if (Set(ref _isLooping, value)) Engine.Loop = value; }
    }

    /// <summary>
    /// When armed, Record starts the persisted input device immediately instead
    /// of opening the recording setup dialog. Arm intentionally resets on launch.
    /// </summary>
    public bool IsRecordArmed
    {
        get => _isRecordArmed;
        set
        {
            if (IsTransportRecording || IsFinalizingRecording || HasPendingTransportRecording) return;
            if (!Set(ref _isRecordArmed, value)) return;
            Raise(nameof(RecordStatusText));
            Raise(nameof(RecordButtonToolTip));
        }
    }

    public bool IsTransportRecording
    {
        get => _isTransportRecording;
        private set
        {
            if (!Set(ref _isTransportRecording, value)) return;
            Raise(nameof(RecordStatusText));
            Raise(nameof(RecordButtonToolTip));
            Raise(nameof(CanChangeRecordArm));
            Raise(nameof(CanAnalyzeCleanup));
            PlayCommand.RaiseCanExecuteChanged();
            RecordSetupCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsFinalizingRecording
    {
        get => _isFinalizingRecording;
        private set
        {
            if (!Set(ref _isFinalizingRecording, value)) return;
            Raise(nameof(RecordStatusText));
            Raise(nameof(RecordButtonToolTip));
            Raise(nameof(CanChangeRecordArm));
            Raise(nameof(CanAnalyzeCleanup));
            PlayCommand.RaiseCanExecuteChanged();
            RecordCommand.RaiseCanExecuteChanged();
            RecordSetupCommand.RaiseCanExecuteChanged();
        }
    }

    public double TransportPeakLDb { get => _transportPeakL; private set => Set(ref _transportPeakL, value); }
    public double TransportPeakRDb { get => _transportPeakR; private set => Set(ref _transportPeakR, value); }
    public bool CanChangeRecordArm => !IsTransportRecording && !IsFinalizingRecording && !HasPendingTransportRecording;
    public bool HasPendingTransportRecording => _transportRecorder.HasPendingCapture;
    public string RecordStatusText => IsFinalizingRecording ? "FINALIZING…"
        : IsTransportRecording
            ? $"REC {TimeFormat.Position((long)(_transportRecorder.RecordedSeconds * _transportRecorder.SampleRate), _transportRecorder.SampleRate)}"
            : HasPendingTransportRecording ? "CAPTURE NEEDS RETRY"
            : IsRecordArmed ? $"ARMED · {_recordInputName}" : "";
    public string RecordButtonToolTip => IsFinalizingRecording ? "Finalizing captured audio"
        : IsTransportRecording ? "Stop recording"
        : HasPendingTransportRecording ? "Retry preserving the buffered recording"
        : IsRecordArmed ? "Record now from the selected input (Ctrl+R)" : "Record setup (Ctrl+R)";

    public string StatusEngine => $"Out: {PlaybackEngine.CurrentOutputName()} · WASAPI · {AppSettings.Instance.BufferMs} ms";
    public string StatusSamples => _active == null ? "" : $"{_active.Doc.Length:N0} samples";

    public string StatusAutosave =>
        !AppSettings.Instance.AutosaveEnabled ? "Autosave off"
        : _lastAutosave == null ? "Autosave on"
        : $"Autosaved {(int)Math.Max(0, (DateTime.Now - _lastAutosave.Value).TotalMinutes)} min ago";

    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }

    public void RefreshEngineStatus()
    {
        UpdateRecordInputName();
        Raise(nameof(StatusEngine));
        Raise(nameof(RecordStatusText));
    }

    private void UpdateRecordInputName()
    {
        try
        {
            var settings = AppSettings.Instance;
            string? preferred = settings.InputDeviceId;
            var selected = RecordingEngine.GetCaptureDevices()
                .FirstOrDefault(device => device.Id == preferred);
            if (preferred != null && selected == default)
            {
                // A removed endpoint must not leave Arm pointing at an ID that
                // will fail. Fall back to the current Windows default input.
                settings.InputDeviceId = null;
                if (!settings.Save()) ReportSettingsSaveFailure();
            }
            _recordInputName = selected.Name ?? "Default input";
        }
        catch { _recordInputName = "Default input"; }
    }

    // ── file ─────────────────────────────────────────────────────

    public async void OpenFiles(IEnumerable<string> paths, OpenBitDepth? openAs = null)
    {
        var operation = OpenFilesAsync(paths, openAs);
        _openOperations.Add(operation);
        try { await operation; }
        finally { _openOperations.Remove(operation); }
    }

    private async Task OpenFilesAsync(IEnumerable<string> paths, OpenBitDepth? openAs = null)
    {
        if (_shuttingDown) return;
        foreach (var path in paths.ToList())
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // decode AND build the peak pyramid off the UI thread — the tab appears fully drawn
                var (doc, peaks) = await Task.Run(() =>
                {
                    var loaded = openAs.HasValue
                        ? AudioImporter.LoadAs(path, openAs.Value)
                        : AudioImporter.Load(path);
                    var store = new PeakStore();
                    store.Rebuild(loaded);
                    return (loaded, store);
                });
                AddDocument(doc, peaks);
                AppSettings.Instance.LastOpenFolder = Path.GetDirectoryName(path);
                if (!AppSettings.Instance.AddRecentFile(path)) ReportSettingsSaveFailure();
                SyncRecentFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open {Path.GetFileName(path)}:\n{ex.Message}", "Open failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    private void SyncRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var f in AppSettings.Instance.RecentFiles) RecentFiles.Add(f);
    }

    private static void ReportSettingsSaveFailure() => MessageBox.Show(
        "WaveLab could not save its settings:\n" + AppSettings.Instance.LastSaveError,
        "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Open()
    {
        var dlg = new OpenFileDialog
        {
            Filter = AudioImporter.OpenFilter,
            Multiselect = true,
            InitialDirectory = AppSettings.Instance.LastOpenFolder ?? "",
        };
        if (dlg.ShowDialog() == true) OpenFiles(dlg.FileNames);
    }

    public void AddDocument(AudioDocument doc, PeakStore? prebuiltPeaks = null)
    {
        var vm = new DocumentViewModel(doc, prebuiltPeaks);
        Documents.Add(vm);
        ActiveDocument = vm;
    }

    public void AddGeneratedDocument(AudioDocument doc)
    {
        doc.MarkUnsaved();
        AddDocument(doc);
    }

    /// <summary>Point-in-time copy sharing the current channel arrays (splices never mutate old arrays).</summary>
    private static AudioDocument SnapshotDoc(AudioDocument doc)
    {
        var refs = doc.Channels.ToArray();
        return new AudioDocument(refs, doc.SampleRate, doc.SourceBitDepth)
        {
            Title = doc.Title,
            FilePath = doc.FilePath,
            Dither16BitOnSave = doc.Dither16BitOnSave,
        };
    }

    private async void Save()
    {
        if (_active != null) await TrackSaveOperationAsync(SaveCoreAsync(_active));
    }

    private async Task SaveCoreAsync(DocumentViewModel d)
    {
        if (_shuttingDown) return;
        var doc = d.Doc;
        if (doc.FilePath == null) { await SaveAsCoreAsync(d); return; }
        if (!_savesInFlight.Add(doc.SessionId)) return; // a save for this document is already writing
        int version = doc.EditVersion;
        var snapshot = SnapshotDoc(doc);
        string path = doc.FilePath!;
        int depth = doc.SourceBitDepth;
        try
        {
            await Task.Run(() => WavCodec.Save(snapshot, path, depth,
                dither: depth == 16 && snapshot.Dither16BitOnSave));
            // Do not declare the document fully persisted, or discard its
            // recovery copy, while the latest marker sidecar is still pending
            // (or has failed).
            await d.FlushMarkersAsync();
            _saveFailures.Remove(doc.SessionId);
            if (doc.EditVersion == version) // only mark clean if nothing changed while writing
            {
                doc.MarkSaved();
                d.NotifySaved();
                AutosaveService.Remove(doc.SessionId);
            }
        }
        catch (Exception ex)
        {
            _saveFailures[doc.SessionId] = ex.Message;
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _savesInFlight.Remove(doc.SessionId);
        }
    }

    private async void SaveAs()
    {
        if (_active != null) await TrackSaveOperationAsync(SaveAsCoreAsync(_active));
    }

    private async Task SaveAsCoreAsync(DocumentViewModel d)
    {
        if (_shuttingDown) return;
        var doc = d.Doc;
        var dlg = new SaveFileDialog
        {
            Filter = "WAV — 32-bit float|*.wav|WAV — 24-bit PCM|*.wav|" +
                     "WAV — 16-bit PCM (dithered)|*.wav|WAV — 16-bit PCM (no dither)|*.wav",
            FilterIndex = doc.SourceBitDepth switch
            {
                24 => 2,
                16 when !doc.Dither16BitOnSave => 4,
                16 => 3,
                _ => 1,
            },
            FileName = Path.GetFileNameWithoutExtension(doc.Title),
            DefaultExt = ".wav",
        };
        if (dlg.ShowDialog() != true) return;
        if (!_savesInFlight.Add(doc.SessionId)) return; // a save for this document is already writing
        int depth = dlg.FilterIndex switch { 2 => 24, 3 or 4 => 16, _ => 32 };
        bool dither16 = dlg.FilterIndex != 4;
        int version = doc.EditVersion;
        var snapshot = SnapshotDoc(doc);
        try
        {
            await Task.Run(() => WavCodec.Save(snapshot, dlg.FileName, depth,
                dither: depth == 16 && dither16));
            _saveFailures.Remove(doc.SessionId);
            doc.FilePath = dlg.FileName;
            doc.Title = Path.GetFileName(dlg.FileName);
            doc.SourceBitDepth = depth;
            doc.Dither16BitOnSave = dither16;
            // Generated documents could accumulate markers/regions before they had
            // a path. Persist that in-memory metadata alongside the first Save As.
            d.NotifyMarkersChanged();
            await d.FlushMarkersAsync();
            if (doc.EditVersion == version)
            {
                doc.MarkSaved();
                AutosaveService.Remove(doc.SessionId);
            }
            d.NotifySaved();
            AppSettings.Instance.LastOpenFolder = Path.GetDirectoryName(dlg.FileName);
            if (!AppSettings.Instance.AddRecentFile(dlg.FileName)) ReportSettingsSaveFailure();
            SyncRecentFiles();
            Raise(nameof(WindowTitle));
        }
        catch (Exception ex)
        {
            _saveFailures[doc.SessionId] = ex.Message;
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _savesInFlight.Remove(doc.SessionId);
        }
    }

    private async Task TrackSaveOperationAsync(Task operation)
    {
        _saveOperations.Add(operation);
        try { await operation; }
        finally { _saveOperations.Remove(operation); }
    }

    private async void CloseTab(DocumentViewModel? vm)
    {
        var operation = CloseTabAsync(vm);
        _tabCloseOperations.Add(operation);
        try { await operation; }
        finally { _tabCloseOperations.Remove(operation); }
    }

    private async Task CloseTabAsync(DocumentViewModel? vm)
    {
        vm ??= _active;
        if (vm == null || !Documents.Contains(vm)) return;
        if (_tabsClosing.Contains(vm.Doc.SessionId)) return;
        if (_savesInFlight.Contains(vm.Doc.SessionId))
        {
            MessageBox.Show("Wait for this file's save to finish before closing its tab.", "Save in progress",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (vm.IsDirty &&
            MessageBox.Show($"{vm.Doc.Title} has unsaved changes. Close anyway?", "Unsaved changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (!_tabsClosing.Add(vm.Doc.SessionId)) return;
        try
        {
        if (ReferenceEquals(vm, _playbackDocument)) ReleasePlayback();
        if (ReferenceEquals(vm, _seekDocument))
        {
            _seekDocument = null;
            _resumeAfterSeek = false;
        }
        try { await vm.FlushMarkersAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("Marker metadata could not be saved, so the tab was left open:\n" + ex.Message,
                "Close file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AutosaveService.Remove(vm.Doc.SessionId);
        _autosavedVersions.Remove(vm.Doc.SessionId);
        _saveFailures.Remove(vm.Doc.SessionId);
        int idx = Documents.IndexOf(vm);
        Documents.Remove(vm);
        if (_active == vm)
            ActiveDocument = Documents.Count > 0 ? Documents[Math.Clamp(idx, 0, Documents.Count - 1)] : null;
        }
        finally { _tabsClosing.Remove(vm.Doc.SessionId); }
    }

    // ── edit ─────────────────────────────────────────────────────

    private async void Copy()
    {
        if (_active is not { HasSelection: true } d) return;
        await CaptureSelectionAsync(d);
    }

    private async void Cut()
    {
        if (_active is not { HasSelection: true } d) return;
        if (!await CaptureSelectionAsync(d) || !Documents.Contains(d) || !d.HasSelection) return;
        int start = d.SelStart;
        int count = d.SelEnd - start;
        PrepareForDocumentEdit(d);
        d.Doc.ReplaceRange(start, count, EmptyData(d.Doc.ChannelCount), "Cut");
        d.SetCursor(start, clearSelection: true);
    }

    private async Task<bool> CaptureSelectionAsync(DocumentViewModel d)
    {
        if (_editOperationRunning || !d.HasSelection) return false;
        int start = d.SelStart, count = d.SelEnd - d.SelStart;
        var channels = d.Doc.Channels.ToArray();
        int sampleRate = d.Doc.SampleRate;
        SetEditOperationRunning(true);
        try
        {
            _clipboard = await Task.Run(() =>
            {
                var copy = new float[channels.Length][];
                for (int c = 0; c < channels.Length; c++)
                {
                    copy[c] = new float[count];
                    Array.Copy(channels[c], start, copy[c], 0, count);
                }
                return copy;
            });
            _clipboardRate = sampleRate;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { SetEditOperationRunning(false); }
    }

    private void DeleteSelection()
    {
        if (_active is not { HasSelection: true } d) return;
        int start = d.SelStart;
        PrepareForDocumentEdit(d);
        d.Doc.ReplaceRange(start, d.SelEnd - start, EmptyData(d.Doc.ChannelCount), "Delete");
        d.SetCursor(start, clearSelection: true);
    }

    private async void Trim()
    {
        if (_editOperationRunning || _active is not { HasSelection: true } d) return;
        int selStart = d.SelStart, selLen = d.SelEnd - d.SelStart;
        var channels = d.Doc.Channels.ToArray();
        float[][] kept;
        SetEditOperationRunning(true);
        try
        {
            kept = await Task.Run(() => channels.Select(ch => ch.AsSpan(selStart, selLen).ToArray()).ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Trim failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { SetEditOperationRunning(false); }
        if (!Documents.Contains(d)) return;
        PrepareForDocumentEdit(d);
        d.Doc.ReplaceRange(0, d.Doc.Length, kept, "Trim");
        d.SetCursor(0, clearSelection: true);
        d.ZoomFull();
    }

    private async void Paste()
    {
        if (_editOperationRunning || _active == null || _clipboard == null) return;
        var d = _active;
        var clipboard = _clipboard;
        if (clipboard.Length != d.Doc.ChannelCount)
        {
            MessageBox.Show("Clipboard channel count doesn't match this file.", "Paste",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        float[][] data = clipboard;
        if (_clipboardRate != d.Doc.SampleRate)
        {
            SetEditOperationRunning(true);
            try { data = await Task.Run(() => Resampler.Resample(clipboard, _clipboardRate, d.Doc.SampleRate)); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Paste failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { SetEditOperationRunning(false); }
        }
        if (!Documents.Contains(d)) return;
        int at = d.HasSelection ? d.SelStart : d.Cursor;
        int remove = d.HasSelection ? d.SelEnd - d.SelStart : 0;
        PrepareForDocumentEdit(d);
        d.Doc.ReplaceRange(at, remove, data, "Paste");
        d.SetCursor(at + data[0].Length, clearSelection: true);
    }

    private void SetEditOperationRunning(bool value)
    {
        _editOperationRunning = value;
        var window = Application.Current?.MainWindow;
        if (window != null) window.IsEnabled = !value;
        Mouse.OverrideCursor = value ? Cursors.Wait : null;
        RefreshEditCommandStates();
    }

    private static float[][] EmptyData(int channels)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++) data[c] = [];
        return data;
    }

    private void ApplyToRange(Action<AudioDocument, int, int> op)
    {
        if (_active == null) return;
        var (start, count) = _active.EditRange();
        if (count <= 0) return;
        PrepareForDocumentEdit(_active);
        op(_active.Doc, start, count);
    }

    public void PrepareForDocumentEdit(DocumentViewModel document)
    {
        if (ReferenceEquals(document, _playbackDocument)
            || ReferenceEquals(Engine.SourceDocument, document.Doc))
            ReleasePlayback();
    }

    private void WithDoc(Action<DocumentViewModel> action)
    {
        if (_active != null) action(_active);
    }

    // ── transport ────────────────────────────────────────────────

    private void ToggleRecord()
    {
        if (IsFinalizingRecording) return;
        if (HasPendingTransportRecording)
        {
            _ = FinishTransportRecordingAsync();
            return;
        }
        if (IsTransportRecording)
        {
            _ = FinishTransportRecordingAsync();
            return;
        }
        if (!IsRecordArmed)
        {
            RequestRecordDialog?.Invoke();
            return;
        }

        if (Engine.IsPlaying || Engine.IsPaused) ReleasePlayback();
        try
        {
            UpdateRecordInputName();
            Interlocked.Exchange(ref _expectedTransportRecordingSessionId, 0);
            long sessionId = _transportRecorder.Start(AppSettings.Instance.InputDeviceId);
            Interlocked.Exchange(ref _expectedTransportRecordingSessionId, sessionId);
            TransportPeakLDb = TransportPeakRDb = -60;
            IsTransportRecording = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start recording from the selected input:\n{ex.Message}",
                "Record", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopTransport()
    {
        if (IsTransportRecording || HasPendingTransportRecording) _ = FinishTransportRecordingAsync();
        else PausePlayback();
    }

    public Task FinishTransportRecordingAsync()
        => FinishTransportRecordingAsync(sessionId: null);

    private Task FinishTransportRecordingAsync(long? sessionId)
    {
        if (IsFinalizingRecording) return _recordFinalization;
        if (!IsTransportRecording && !HasPendingTransportRecording) return Task.CompletedTask;
        IsTransportRecording = false;
        IsFinalizingRecording = true;
        long ownedSessionId = sessionId ?? Interlocked.Read(ref _expectedTransportRecordingSessionId);
        _recordFinalization = FinalizeCoreAsync(ownedSessionId, requireSessionMatch: sessionId.HasValue);
        return _recordFinalization;
    }

    private async Task FinalizeCoreAsync(long ownedSessionId, bool requireSessionMatch)
    {
        try
        {
            var result = requireSessionMatch
                ? await _transportRecorder.StopSessionAndGetDocumentAsync(ownedSessionId)
                : await _transportRecorder.StopAndGetDocumentAsync();
            if (result != null) AddGeneratedDocument(result);
            if (_transportRecorder.LastStopError != null)
                MessageBox.Show($"The input device stopped unexpectedly. Audio captured before the failure was kept.\n\n{_transportRecorder.LastStopError.Message}",
                    "Recording stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (_transportRecorder.CapacityReached)
                MessageBox.Show("The recording reached WaveLab's in-memory safety limit. Audio captured up to the limit was kept.",
                    "Recording stopped", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not finalize the recording:\n{ex.Message}",
                "Record", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ownedSessionId != 0)
                Interlocked.CompareExchange(ref _expectedTransportRecordingSessionId, 0, ownedSessionId);
            TransportPeakLDb = TransportPeakRDb = -60;
            IsFinalizingRecording = false;
            Raise(nameof(HasPendingTransportRecording));
            Raise(nameof(RecordStatusText));
            Raise(nameof(RecordButtonToolTip));
            Raise(nameof(CanChangeRecordArm));
            RecordSetupCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnTransportCaptureStopped(RecordingStoppedInfo info)
    {
        if (!_transportRecorder.IsCurrentSession(info.SessionId)) return;
        // A user-requested stop clears IsTransportRecording before asking the
        // engine to stop. If it is still true, the device or safety cap ended it.
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            if (IsTransportRecording
                && info.SessionId == Interlocked.Read(ref _expectedTransportRecordingSessionId)
                && _transportRecorder.IsCurrentSession(info.SessionId))
                await FinishTransportRecordingAsync(info.SessionId);
        });
    }

    private void TogglePlay()
    {
        if (IsTransportRecording || IsFinalizingRecording) return;
        if (Engine.IsPlaying) { PausePlayback(); return; }
        IsPlaying = false;
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        bool ownsPausedStream = Engine.IsPaused
            && ReferenceEquals(_playbackDocument, d)
            && ReferenceEquals(Engine.SourceDocument, d.Doc);
        if (ownsPausedStream
            && _playbackEditVersion == d.Doc.EditVersion
            && d.PlayheadSample == Engine.PositionSamples)
        {
            try
            {
                Engine.Resume();
                IsPlaying = true;
            }
            catch (Exception ex) { HandlePlaybackFailure(ex); }
            return;
        }

        if (Engine.IsPlaying || Engine.IsPaused)
            ReleasePlayback(updatePosition: !ownsPausedStream);

        int start = d.PlayheadSample;
        if (d.HasSelection && (start < d.SelStart || start >= d.SelEnd)) start = d.SelStart;
        else if (!d.HasSelection && start >= d.Doc.Length - 1) start = 0;
        StartPlaybackAt(d, start);
    }

    private void PausePlayback()
    {
        if (!Engine.IsPlaying) { IsPlaying = false; return; }
        var d = _playbackDocument;
        try
        {
            Engine.Pause();
            if (d != null && Documents.Contains(d)) SetTransportPosition(d, Engine.PositionSamples);
            IsPlaying = false;
        }
        catch (Exception ex) { HandlePlaybackFailure(ex); }
    }

    private void GoToStart()
    {
        if (_active == null) return;
        var target = _active;
        if (Engine.IsPlaying || Engine.IsPaused)
            ReleasePlayback(updatePosition: !ReferenceEquals(_playbackDocument, target));
        _active.SetCursor(0, clearSelection: true);
        _active.CenterViewOn(0);
    }

    private void HandlePlayheadSeek(PlayheadSeekRequest? request)
    {
        if (IsTransportRecording || IsFinalizingRecording) return;
        if (request == null || !Documents.Contains(request.Document) || request.Document.Doc.Length == 0) return;
        var document = request.Document;
        int sample = Math.Clamp(request.Sample, 0, document.Doc.Length - 1);

        if (request.Phase == PlayheadSeekPhase.Begin)
        {
            _seekDocument = document;
            bool ownsPlayback = ReferenceEquals(document, _playbackDocument)
                && ReferenceEquals(document.Doc, Engine.SourceDocument);
            _resumeAfterSeek = ownsPlayback && Engine.IsPlaying;
            if (ownsPlayback && (Engine.IsPlaying || Engine.IsPaused))
                ReleasePlayback(updatePosition: false);
        }
        else if (!ReferenceEquals(document, _seekDocument))
        {
            return;
        }

        SetTransportPosition(document, sample);

        if (request.Phase != PlayheadSeekPhase.End) return;
        bool resume = _resumeAfterSeek;
        _seekDocument = null;
        _resumeAfterSeek = false;
        if (resume && Documents.Contains(document))
            StartPlaybackAt(document, sample);
    }

    private bool StartPlaybackAt(DocumentViewModel document, int sample)
    {
        try
        {
            int start = Math.Clamp(sample, 0, Math.Max(0, document.Doc.Length - 1));
            int? end = document.HasSelection && start >= document.SelStart && start < document.SelEnd
                ? document.SelEnd
                : null;
            Engine.Loop = IsLooping;
            Master.ResetMeters();
            long playbackSession = Engine.Play(document.Doc, start, end);
            _playbackDocument = document;
            _playbackEditVersion = document.Doc.EditVersion;
            _playbackSession = playbackSession;
            IsPlaying = true;
            return true;
        }
        catch (Exception ex)
        {
            HandlePlaybackFailure(ex);
            return false;
        }
    }

    private void RestartMonoPlaybackForTopologyChange()
    {
        AudioDocument? source = Engine.SourceDocument;
        bool wasPlaying = Engine.IsPlaying;
        bool wasPaused = Engine.IsPaused;
        if (source?.ChannelCount != 1 || (!wasPlaying && !wasPaused)) return;

        int position = Engine.PositionSamples;
        bool loop = Engine.Loop;
        var document = _playbackDocument;
        var preview = _previewDocument;
        ReleasePlayback(updatePosition: false);

        try
        {
            if (document != null && Documents.Contains(document))
            {
                if (!StartPlaybackAt(document, position)) return;
            }
            else if (preview != null)
            {
                Engine.Loop = loop;
                Master.ResetMeters();
                _playbackSession = Engine.Play(preview, position, preview.Length);
                _playbackDocument = null;
                _previewDocument = preview;
                _playbackEditVersion = -1;
                IsPlaying = true;
            }
            else
            {
                return;
            }

            if (wasPaused)
            {
                Engine.Pause();
                IsPlaying = false;
            }
        }
        catch (Exception ex)
        {
            ReleasePlayback(updatePosition: false);
            MessageBox.Show($"Playback could not restart after the rack topology changed:\n{ex.Message}",
                "Audio rack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReleasePlayback(bool updatePosition = true)
    {
        var d = _playbackDocument;
        int position = Engine.PositionSamples;
        try { Engine.Stop(); }
        catch { /* continue clearing ownership so later transport actions can recover */ }
        if (updatePosition && d != null && Documents.Contains(d)) SetTransportPosition(d, position);
        _playbackDocument = null;
        _previewDocument = null;
        _playbackEditVersion = -1;
        _playbackSession = 0;
        IsPlaying = false;
        RestorePreviewRackOverride();
    }

    private void HandlePlaybackFailure(Exception exception)
    {
        ReleasePlayback(updatePosition: false);
        MessageBox.Show($"Playback failed:\n{exception.Message}", "Playback",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Play a transient document without adding it to the tab collection.</summary>
    public bool PlayPreview(AudioDocument preview, bool loop = true, bool bypassRack = false)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Length == 0 || IsTransportRecording || IsFinalizingRecording || HasPendingTransportRecording)
            return false;
        if (Engine.IsPlaying || Engine.IsPaused || _previewDocument != null || _previewRackRestoreState.HasValue)
            ReleasePlayback();

        if (bypassRack)
        {
            // Preview bypass is an internal, scoped engine override. Keep the public
            // rack VM untouched so a transient A/B audition cannot rewrite the
            // user's rack status or make its toggle flash while the dialog is open.
            _previewRackRestoreState = Engine.Master.RackEnabled;
            Engine.Master.RackEnabled = false;
        }

        try
        {
            Engine.Loop = loop;
            Master.ResetMeters();
            _playbackSession = Engine.Play(preview, 0, preview.Length);
            _playbackDocument = null;
            _previewDocument = preview;
            _playbackEditVersion = -1;
            IsPlaying = true;
            return true;
        }
        catch
        {
            RestorePreviewRackOverride();
            throw;
        }
    }

    public void StopPreview()
    {
        if (_previewDocument != null || _previewRackRestoreState.HasValue)
            ReleasePlayback(updatePosition: false);
    }

    private void RestorePreviewRackOverride()
    {
        bool? restore = _previewRackRestoreState;
        _previewRackRestoreState = null;
        if (restore.HasValue) Engine.Master.RackEnabled = restore.Value;
    }

    private static void SetTransportPosition(DocumentViewModel document, int position)
    {
        if (document.Doc.Length <= 0) return;
        int playhead = Math.Clamp(position, 0, document.Doc.Length);
        document.SetCursor(Math.Min(playhead, document.Doc.Length - 1), clearSelection: false);
        document.PlayheadSample = playhead;
    }

    private void OnPlaybackStopped(long playbackSession, AudioDocument sourceDocument, int position)
    {
        // NAudio raises this from its playback thread. Marshal UI-bound state back
        // to the dispatcher and retain the final transport position.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Ignore a stale completion if this file (or another one) was
            // restarted before the dispatcher callback had a chance to run.
            if (playbackSession != _playbackSession || Engine.IsPlaying || Engine.IsPaused) return;
            var document = _playbackDocument;
            if (document != null && !ReferenceEquals(document.Doc, sourceDocument)) return;
            if (document != null && Documents.Contains(document) && document.Doc.Length > 0)
                SetTransportPosition(document, position);
            _playbackDocument = null;
            _previewDocument = null;
            _playbackEditVersion = -1;
            _playbackSession = 0;
            _stoppedPlaybackSession = playbackSession;
            _stoppedPlaybackSource = sourceDocument;
            IsPlaying = false;
            RestorePreviewRackOverride();
        });
    }

    private void OnPlaybackFailed(long playbackSession, AudioDocument sourceDocument, Exception error)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_stoppedPlaybackSession != playbackSession
                || !ReferenceEquals(_stoppedPlaybackSource, sourceDocument)) return;
            _stoppedPlaybackSession = 0;
            _stoppedPlaybackSource = null;
            MessageBox.Show($"Playback stopped because the audio device failed:\n{error.Message}",
                "Playback", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OnActiveDocumentEdited(int start, int removed, int inserted) => RefreshEditCommandStates();

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.HasSelection)
            or nameof(DocumentViewModel.SelStart) or nameof(DocumentViewModel.SelEnd))
            RefreshEditCommandStates();
    }

    private void RefreshEditCommandStates()
    {
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        CutCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        PasteCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        TrimCommand.RaiseCanExecuteChanged();
    }

    private async void RenderMaster()
    {
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        var doc = d.Doc;
        // capture stable channel refs on the UI thread — splices never mutate old arrays
        var input = doc.Channels.ToArray();
        int sr = doc.SampleRate;

        await RunBlocking(async () =>
        {
            var output = await Task.Run(() => Engine.Master.ProcessOffline(input, sr));
            AddGeneratedDocument(new AudioDocument(output, sr, sourceBitDepth: 32)
            {
                Title = Path.GetFileNameWithoutExtension(doc.Title) + " (rendered copy).wav",
            });
        });
    }

    /// <summary>Render the selection (or whole file) as one undoable document edit.</summary>
    private async void ApplyChain()
    {
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        var (start, count) = d.EditRange();
        if (count <= 0) return;
        var channels = d.Doc.Channels.ToArray();
        int sr = d.Doc.SampleRate;
        int sourceVersion = d.Doc.EditVersion;

        await RunBlocking(async () =>
        {
            var output = await Task.Run(() =>
            {
                var input = channels.Select(ch => ch.AsSpan(start, count).ToArray()).ToArray();
                return Engine.Master.ProcessOffline(input, sr);
            });
            if (d.Doc.EditVersion != sourceVersion)
                throw new InvalidOperationException("The source changed while the master render was running. Try again.");

            bool wholeDocument = start == 0 && count == d.Doc.Length;
            if (output.Length != d.Doc.ChannelCount && !wholeDocument)
            {
                throw new InvalidOperationException(
                    "The enabled rack changes the channel layout, so it cannot be inserted into only part of this file. " +
                    "Select the whole file for an undoable in-place render, or use Render Copy.");
            }
            if (start + count <= d.Doc.Length)
            {
                PrepareForDocumentEdit(d);
                if (wholeDocument)
                    d.Doc.ReplaceAllOwned(output, "Render Master Chain");
                else
                    d.Doc.ReplaceRange(start, count, output, "Render Master Chain");
            }
        });
    }

    /// <summary>Disable the main window and show a wait cursor while a long operation runs.</summary>
    private static async Task RunBlocking(Func<Task> work)
    {
        var win = Application.Current?.MainWindow;
        if (win != null) win.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try { await work(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "WaveLab", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            if (win != null) win.IsEnabled = true;
        }
    }

    public void RefreshSpectrogram() => RequestSpectrogram?.Invoke();

    /// <summary>De-click the selection boundaries (or the cursor position) after an edit.</summary>
    private void SmoothEditPoints()
    {
        if (_active == null || _active.Doc.Length == 0) return;
        PrepareForDocumentEdit(_active);
        if (_active.HasSelection)
        {
            Processing.SmoothEditPoint(_active.Doc, _active.SelEnd);
            Processing.SmoothEditPoint(_active.Doc, _active.SelStart);
        }
        else
        {
            Processing.SmoothEditPoint(_active.Doc, _active.Cursor);
        }
    }

    /// <summary>Replace the active selection with recorded audio (punch), matching layout and rate, smoothing the joins.</summary>
    public async Task PunchInsertAsync(AudioDocument recorded)
    {
        if (_active is not { HasSelection: true } d) { AddGeneratedDocument(recorded); return; }

        var target = d.Doc;
        float[][] data;
        SetEditOperationRunning(true);
        try
        {
            data = await Task.Run(() =>
            {
                float[][] result = recorded.Channels.ToArray();
                if (recorded.SampleRate != target.SampleRate)
                    result = Resampler.Resample(result, recorded.SampleRate, target.SampleRate);
                if (result.Length == target.ChannelCount) return result;

                var converted = new float[target.ChannelCount][];
                float[]? mixed = null;
                if (result.Length > 1)
                {
                    mixed = new float[result[0].Length];
                    for (int i = 0; i < mixed.Length; i++)
                    {
                        float v = 0;
                        foreach (var ch in result) v += ch[i];
                        mixed[i] = v / result.Length;
                    }
                }
                for (int c = 0; c < converted.Length; c++)
                    converted[c] = (float[])(result.Length == 1 ? result[0] : mixed!).Clone();
                return converted;
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Punch Record", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { SetEditOperationRunning(false); }

        if (!Documents.Contains(d) || !d.HasSelection) return;
        PrepareForDocumentEdit(d);
        int start = d.SelStart;
        target.ReplaceRange(start, d.SelEnd - start, data, "Punch Record");
        int end = start + data[0].Length;
        Processing.SmoothEditPoint(target, end);
        Processing.SmoothEditPoint(target, start);
        d.SetSelection(start, end);
    }

    // ── autosave / session ───────────────────────────────────────

    private void AutosaveTick()
    {
        if (!_startupLoaded || _shuttingDown || !_autosaveTask.IsCompleted) return;
        var s = AppSettings.Instance;
        if (!s.AutosaveEnabled) { Raise(nameof(StatusAutosave)); return; }
        if (_lastAutosave != null && (DateTime.Now - _lastAutosave.Value).TotalMinutes < s.AutosaveMinutes)
        {
            Raise(nameof(StatusAutosave));
            return;
        }

        // skip documents whose content hasn't changed since the last autosave
        var dirty = Documents.Where(d => d.IsDirty && d.Doc.Length > 0
            && _autosavedVersions.GetValueOrDefault(d.Doc.SessionId, -1) != d.Doc.EditVersion).ToList();
        if (dirty.Count == 0) { Raise(nameof(StatusAutosave)); return; }

        // snapshot channel-array references (splicing replaces arrays, so refs are point-in-time consistent)
        var snapshots = dirty.Select(d => (snap: SnapshotDoc(d.Doc), d.Doc.SessionId)).ToList();

        var versions = dirty.Select(d => (Id: d.Doc.SessionId, Version: d.Doc.EditVersion)).ToList();
        _autosaveTask = Task.Run(() =>
        {
            int saved = AutosaveService.RunNow(snapshots.Select(s2 => (s2.snap, s2.SessionId)));
            // only record versions as autosaved if the whole batch made it to disk —
            // a failed write retries on the next tick instead of silently going stale
            if (saved == versions.Count)
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _lastAutosave = DateTime.Now;
                    foreach (var (id, version) in versions)
                    {
                        var document = Documents.FirstOrDefault(d => d.Doc.SessionId == id);
                        if (document != null && document.Doc.EditVersion == version)
                            _autosavedVersions[id] = version;
                    }
                });
        });
        Raise(nameof(StatusAutosave));
    }

    /// <summary>Called once from the window after load: crash recovery, session restore, command-line files.</summary>
    public async Task StartupLoadAsync(string[] args)
    {
      try
      {
        var recoverable = AutosaveService.GetRecoverable();
        if (recoverable.Count > 0)
        {
            var names = string.Join("\n", recoverable.Select(r => "  • " + r.Title));
            if (MessageBox.Show(
                    $"WaveLab didn't shut down cleanly last time. Recover unsaved work?\n\n{names}",
                    "Crash recovery", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var recoveredKeys = new List<string>();
                var recoveredDocuments = new List<AudioDocument>();
                var failures = new List<string>();
                foreach (var entry in recoverable)
                {
                    try
                    {
                        var (doc, peaks) = await Task.Run(() =>
                        {
                            var loaded = WavCodec.Load(entry.AutosaveFile);
                            var store = new PeakStore();
                            store.Rebuild(loaded);
                            return (loaded, store);
                        });
                        doc.FilePath = entry.OriginalPath;
                        string recoveredTitle = string.IsNullOrWhiteSpace(entry.Title)
                            ? "Recovered audio"
                            : entry.Title.Replace(" •", "").Trim();
                        doc.Title = recoveredTitle + " (recovered)";
                        doc.MarkUnsaved();
                        AddDocument(doc, peaks);
                        recoveredKeys.Add(entry.ManifestKey);
                        recoveredDocuments.Add(doc);
                    }
                    catch (Exception ex) { failures.Add($"{entry.Title}: {ex.Message}"); }
                }
                if (recoveredDocuments.Count > 0)
                {
                    var replacementSnapshots = recoveredDocuments
                        .Select(doc => (SnapshotDoc(doc), doc.SessionId)).ToList();
                    int published = await Task.Run(() => AutosaveService.RunNow(replacementSnapshots));
                    if (published == replacementSnapshots.Count)
                    {
                        AutosaveService.RemoveRecoverable(recoveredKeys);
                        foreach (var doc in recoveredDocuments)
                            _autosavedVersions[doc.SessionId] = doc.EditVersion;
                        _lastAutosave = DateTime.Now;
                    }
                    else failures.Add("Recovered documents could not be re-secured in autosave; original recovery files were retained.");
                }
                if (failures.Count > 0)
                    MessageBox.Show("Some recovery work could not be completed; original recovery files were retained:\n\n"
                        + string.Join("\n", failures), "Crash recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (!AutosaveService.ClearAll())
                MessageBox.Show("Recovery files could not be discarded and may be offered again next launch.",
                    "Crash recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (args.Length > 0)
        {
            await OpenFilesAsync(args);
        }
        else if (AppSettings.Instance.ReopenLastSession && Documents.Count == 0)
        {
            var files = AppSettings.Instance.LastSessionFiles.Where(File.Exists).ToList();
            if (files.Count > 0) await OpenFilesAsync(files);
        }
      }
      finally { _startupLoaded = true; }
    }

    /// <summary>Called from the window on clean shutdown.</summary>
    public async Task OnCleanExitAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _autosaveTimer.Stop();
        _timer.Stop();
        Exception? failure = null;
        try
        {
            if (Engine.IsPlaying || Engine.IsPaused) ReleasePlayback();
            if (!_recordFinalization.IsCompleted) await _recordFinalization;
            await _autosaveTask;
            if (_openOperations.Count > 0) await Task.WhenAll(_openOperations.ToArray());
            if (_saveOperations.Count > 0) await Task.WhenAll(_saveOperations.ToArray());
            if (_tabCloseOperations.Count > 0) await Task.WhenAll(_tabCloseOperations.ToArray());
            var markerTasks = Documents.Select(d => d.FlushMarkersAsync()).ToArray();
            if (markerTasks.Length > 0) await Task.WhenAll(markerTasks);
            if (_saveFailures.Count > 0)
                throw new IOException("One or more file saves failed: " + string.Join("; ", _saveFailures.Values));

            AppSettings.Instance.LastSessionFiles =
                Documents.Where(d => d.Doc.FilePath != null).Select(d => d.Doc.FilePath!).ToList();
            if (!AppSettings.Instance.Save())
                throw new IOException("Settings could not be saved: " + AppSettings.Instance.LastSaveError);
            if (!AutosaveService.ClearAll())
                throw new IOException("Autosave recovery files could not be cleared.");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Interlocked.Exchange(ref _expectedTransportRecordingSessionId, 0);
            try { _transportRecorder.Dispose(); } catch (Exception ex) { failure ??= ex; }
            try { Engine.Dispose(); } catch (Exception ex) { failure ??= ex; }
            try { _process.Dispose(); } catch (Exception ex) { failure ??= ex; }
        }
        if (failure != null) throw failure;
    }

    private void OnTick()
    {
        var playbackDocument = _playbackDocument;
        if (playbackDocument != null && IsPlaying && Documents.Contains(playbackDocument))
        {
            playbackDocument.PlayheadSample = Math.Clamp(Engine.PositionSamples, 0, playbackDocument.Doc.Length);
            playbackDocument.EnsurePlayheadVisible();
        }
        Master.Tick(0.033, IsPlaying);
        if (IsTransportRecording)
        {
            static double ToDb(float value) => value <= 1e-5f ? -60 : Math.Max(-60, 20 * Math.Log10(value));
            static double Decay(double current, double target) => target >= current ? target : Math.Max(target, current - 1.5);
            TransportPeakLDb = Decay(_transportPeakL, ToDb(_transportRecorder.PeakL));
            TransportPeakRDb = Decay(_transportPeakR, ToDb(_transportRecorder.PeakR));
            Raise(nameof(RecordStatusText));
        }

        if (++_tickCount % 30 == 0) // ~1 s
        {
            try
            {
                var now = DateTime.UtcNow;
                _process.Refresh();
                var cpu = _process.TotalProcessorTime;
                double pct = (cpu - _cpuPrev).TotalMilliseconds /
                             Math.Max(1, (now - _cpuPrevAt).TotalMilliseconds) / Environment.ProcessorCount * 100;
                _cpuPrev = cpu;
                _cpuPrevAt = now;
                CpuText = $"CPU {Math.Clamp(pct, 0, 100):0}%";
                RamText = $"RAM {_process.WorkingSet64 / (1024.0 * 1024.0):0} MB";
            }
            catch { }
            Raise(nameof(StatusAutosave));
        }
    }
}
