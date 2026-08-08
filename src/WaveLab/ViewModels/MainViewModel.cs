using System.Collections.ObjectModel;
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
    private bool _isPlaying;
    private bool _isLooping;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _autosaveTimer;
    private DateTime? _lastAutosave;
    private readonly Dictionary<Guid, int> _autosavedVersions = [];
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
        Engine.PlaybackStopped += () => { IsPlaying = false; };

        foreach (var f in AppSettings.Instance.RecentFiles) RecentFiles.Add(f);

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

        UndoCommand = new RelayCommand(() => WithDoc(d => d.Doc.Undo()));
        RedoCommand = new RelayCommand(() => WithDoc(d => d.Doc.Redo()));
        CutCommand = new RelayCommand(Cut);
        CopyCommand = new RelayCommand(Copy);
        PasteCommand = new RelayCommand(Paste);
        DeleteCommand = new RelayCommand(DeleteSelection);
        TrimCommand = new RelayCommand(Trim);
        SelectAllCommand = new RelayCommand(() => WithDoc(d => d.SelectAll()));

        PlayCommand = new RelayCommand(TogglePlay);
        StopCommand = new RelayCommand(StopPlayback);
        GoToStartCommand = new RelayCommand(() => WithDoc(d => { d.SetCursor(0, clearSelection: false); d.PlayheadSample = 0; d.CenterViewOn(0); }));
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
        InsertSilenceCommand = new RelayCommand(() => WithDoc(d => Processing.InsertSilence(d.Doc, d.Cursor, 1.0)));

        AddMarkerCommand = new RelayCommand(() => WithDoc(d => d.AddMarker(d.HasSelection ? d.SelStart : IsPlaying ? d.PlayheadSample : d.Cursor)));
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
        RecordCommand = new RelayCommand(() => RequestRecordDialog?.Invoke());
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
            if (!Set(ref _active, value)) return;
            Raise(nameof(HasDocument));
            Raise(nameof(WindowTitle));
            Raise(nameof(StatusSamples));
        }
    }

    public bool HasDocument => _active != null;
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

    public string StatusEngine => $"Out: {PlaybackEngine.CurrentOutputName()} · WASAPI · {AppSettings.Instance.BufferMs} ms";
    public string StatusSamples => _active == null ? "" : $"{_active.Doc.Length:N0} samples";

    public string StatusAutosave =>
        !AppSettings.Instance.AutosaveEnabled ? "Autosave off"
        : _lastAutosave == null ? "Autosave on"
        : $"Autosaved {(int)Math.Max(0, (DateTime.Now - _lastAutosave.Value).TotalMinutes)} min ago";

    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }

    public void RefreshEngineStatus() => Raise(nameof(StatusEngine));

    // ── file ─────────────────────────────────────────────────────

    public async void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.ToList())
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // decode AND build the peak pyramid off the UI thread — the tab appears fully drawn
                var (doc, peaks) = await Task.Run(() =>
                {
                    var loaded = AudioImporter.Load(path);
                    var store = new PeakStore();
                    store.Rebuild(loaded);
                    return (loaded, store);
                });
                AddDocument(doc, peaks);
                AppSettings.Instance.AddRecentFile(path);
                AppSettings.Instance.LastOpenFolder = Path.GetDirectoryName(path);
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

    /// <summary>Point-in-time copy sharing the current channel arrays (splices never mutate old arrays).</summary>
    private static AudioDocument SnapshotDoc(AudioDocument doc)
    {
        var refs = new float[doc.ChannelCount][];
        for (int c = 0; c < doc.ChannelCount; c++) refs[c] = doc.Channels[c];
        return new AudioDocument(refs, doc.SampleRate, doc.SourceBitDepth)
        {
            Title = doc.Title,
            FilePath = doc.FilePath,
        };
    }

    private async void Save()
    {
        if (_active == null) return;
        if (_active.Doc.FilePath == null) { SaveAs(); return; }
        var d = _active;
        var doc = d.Doc;
        int version = doc.EditVersion;
        var snapshot = SnapshotDoc(doc);
        string path = doc.FilePath!;
        int depth = doc.SourceBitDepth;
        try
        {
            await Task.Run(() => WavCodec.Save(snapshot, path, depth, dither: depth == 16));
            if (doc.EditVersion == version) // only mark clean if nothing changed while writing
            {
                doc.MarkSaved();
                d.NotifySaved();
                AutosaveService.Remove(doc.SessionId);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SaveAs()
    {
        if (_active == null) return;
        var d = _active;
        var doc = d.Doc;
        var dlg = new SaveFileDialog
        {
            Filter = "WAV — 32-bit float|*.wav|WAV — 24-bit|*.wav|WAV — 16-bit (dithered)|*.wav",
            FilterIndex = doc.SourceBitDepth switch { 24 => 2, 16 => 3, _ => 1 },
            FileName = Path.GetFileNameWithoutExtension(doc.Title),
            DefaultExt = ".wav",
        };
        if (dlg.ShowDialog() != true) return;
        int depth = dlg.FilterIndex switch { 2 => 24, 3 => 16, _ => 32 };
        int version = doc.EditVersion;
        var snapshot = SnapshotDoc(doc);
        try
        {
            await Task.Run(() => WavCodec.Save(snapshot, dlg.FileName, depth, dither: depth == 16));
            doc.FilePath = dlg.FileName;
            doc.Title = Path.GetFileName(dlg.FileName);
            doc.SourceBitDepth = depth;
            if (doc.EditVersion == version)
            {
                doc.MarkSaved();
                AutosaveService.Remove(doc.SessionId);
            }
            d.NotifySaved();
            AppSettings.Instance.AddRecentFile(dlg.FileName);
            SyncRecentFiles();
            Raise(nameof(WindowTitle));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseTab(DocumentViewModel? vm)
    {
        vm ??= _active;
        if (vm == null) return;
        if (vm.IsDirty &&
            MessageBox.Show($"{vm.Doc.Title} has unsaved changes. Close anyway?", "Unsaved changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (vm == _active) StopPlayback();
        AutosaveService.Remove(vm.Doc.SessionId);
        _autosavedVersions.Remove(vm.Doc.SessionId);
        int idx = Documents.IndexOf(vm);
        Documents.Remove(vm);
        if (_active == vm)
            ActiveDocument = Documents.Count > 0 ? Documents[Math.Clamp(idx, 0, Documents.Count - 1)] : null;
    }

    // ── edit ─────────────────────────────────────────────────────

    private void Copy()
    {
        if (_active is not { HasSelection: true } d) return;
        _clipboard = d.Doc.CopyRange(d.SelStart, d.SelEnd - d.SelStart);
        _clipboardRate = d.Doc.SampleRate;
    }

    private void Cut()
    {
        if (_active is not { HasSelection: true } d) return;
        Copy();
        d.Doc.ReplaceRange(d.SelStart, d.SelEnd - d.SelStart, EmptyData(d.Doc.ChannelCount), "Cut");
        d.SetCursor(d.SelStart, clearSelection: true);
    }

    private void DeleteSelection()
    {
        if (_active is not { HasSelection: true } d) return;
        int start = d.SelStart;
        d.Doc.ReplaceRange(start, d.SelEnd - start, EmptyData(d.Doc.ChannelCount), "Delete");
        d.SetCursor(start, clearSelection: true);
    }

    private void Trim()
    {
        if (_active is not { HasSelection: true } d) return;
        int selStart = d.SelStart, selLen = d.SelEnd - d.SelStart;
        var kept = d.Doc.CopyRange(selStart, selLen);
        d.Doc.ReplaceRange(0, d.Doc.Length, kept, "Trim");
        d.SetCursor(0, clearSelection: true);
        d.ZoomFull();
    }

    private void Paste()
    {
        if (_active == null || _clipboard == null) return;
        var d = _active;
        if (_clipboard.Length != d.Doc.ChannelCount)
        {
            MessageBox.Show("Clipboard channel count doesn't match this file.", "Paste",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int at = d.HasSelection ? d.SelStart : d.Cursor;
        int remove = d.HasSelection ? d.SelEnd - d.SelStart : 0;
        d.Doc.ReplaceRange(at, remove, _clipboard, "Paste");
        d.SetCursor(at + _clipboard[0].Length, clearSelection: true);
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
        op(_active.Doc, start, count);
    }

    private void WithDoc(Action<DocumentViewModel> action)
    {
        if (_active != null) action(_active);
    }

    // ── transport ────────────────────────────────────────────────

    private void TogglePlay()
    {
        if (IsPlaying) { StopPlayback(); return; }
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        int start = d.HasSelection ? d.SelStart : d.PlayheadSample >= d.Doc.Length - 1 ? 0 : Math.Max(d.Cursor, 0);
        int? end = d.HasSelection ? d.SelEnd : null;
        Engine.Loop = IsLooping;
        Master.ResetMeters();
        Engine.Play(d.Doc, start, end);
        IsPlaying = true;
    }

    private void StopPlayback()
    {
        Engine.Stop();
        IsPlaying = false;
    }

    private async void RenderMaster()
    {
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        var doc = d.Doc;
        // capture stable channel refs on the UI thread — splices never mutate old arrays
        var input = new float[doc.ChannelCount][];
        for (int c = 0; c < doc.ChannelCount; c++) input[c] = doc.Channels[c];
        int sr = doc.SampleRate;

        await RunBlocking(async () =>
        {
            var output = await Task.Run(() => Engine.Master.ProcessOffline(input, sr));
            AddDocument(new AudioDocument(output, sr, doc.SourceBitDepth)
            {
                Title = Path.GetFileNameWithoutExtension(doc.Title) + " (mastered).wav",
            });
        });
    }

    /// <summary>Destructively process the selection (or whole file) through the current master chain.</summary>
    private async void ApplyChain()
    {
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        var (start, count) = d.EditRange();
        if (count <= 0) return;
        var input = d.Doc.CopyRange(start, count);
        int sr = d.Doc.SampleRate;

        await RunBlocking(async () =>
        {
            var output = await Task.Run(() => Engine.Master.ProcessOffline(input, sr));
            if (start + count <= d.Doc.Length) // re-validate: the doc may only change via this window, but stay safe
                d.Doc.ReplaceRange(start, count, output, "Apply Master Chain");
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
    public void PunchInsert(AudioDocument recorded)
    {
        if (_active is not { HasSelection: true } d) { AddDocument(recorded); return; }

        var target = d.Doc;
        float[][] data = recorded.Channels.ToArray();

        if (recorded.SampleRate != target.SampleRate)
            data = Resampler.Resample(data, recorded.SampleRate, target.SampleRate);

        if (data.Length != target.ChannelCount)
        {
            var converted = new float[target.ChannelCount][];
            for (int c = 0; c < target.ChannelCount; c++)
            {
                if (data.Length == 1) converted[c] = (float[])data[0].Clone();
                else
                {
                    // mix down all recorded channels
                    var mono = new float[data[0].Length];
                    for (int i = 0; i < mono.Length; i++)
                    {
                        float v = 0;
                        foreach (var ch in data) v += ch[i];
                        mono[i] = v / data.Length;
                    }
                    converted[c] = mono;
                }
            }
            data = converted;
        }

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

        foreach (var d in dirty) _autosavedVersions[d.Doc.SessionId] = d.Doc.EditVersion;
        _lastAutosave = DateTime.Now;
        Task.Run(() => AutosaveService.RunNow(snapshots.Select(s2 => (s2.snap, s2.SessionId))));
        Raise(nameof(StatusAutosave));
    }

    /// <summary>Called once from the window after load: crash recovery, session restore, command-line files.</summary>
    public void StartupLoad(string[] args)
    {
        var recoverable = AutosaveService.GetRecoverable();
        if (recoverable.Count > 0)
        {
            var names = string.Join("\n", recoverable.Select(r => "  • " + r.Title));
            if (MessageBox.Show(
                    $"WaveLab didn't shut down cleanly last time. Recover unsaved work?\n\n{names}",
                    "Crash recovery", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (var entry in recoverable)
                {
                    try
                    {
                        var doc = WavCodec.Load(entry.AutosaveFile);
                        doc.FilePath = entry.OriginalPath;
                        doc.Title = entry.Title.Replace(" •", "") + " (recovered)";
                        AddDocument(doc);
                        Documents[^1].NotifySaved();
                    }
                    catch { }
                }
            }
            AutosaveService.ClearAll();
        }

        if (args.Length > 0)
        {
            OpenFiles(args);
        }
        else if (AppSettings.Instance.ReopenLastSession && Documents.Count == 0)
        {
            var files = AppSettings.Instance.LastSessionFiles.Where(File.Exists).ToList();
            if (files.Count > 0) OpenFiles(files);
        }
    }

    /// <summary>Called from the window on clean shutdown.</summary>
    public void OnCleanExit()
    {
        AppSettings.Instance.LastSessionFiles =
            Documents.Where(d => d.Doc.FilePath != null).Select(d => d.Doc.FilePath!).ToList();
        AppSettings.Instance.Save();
        AutosaveService.ClearAll();
        Engine.Dispose();
    }

    private void OnTick()
    {
        if (_active != null && IsPlaying)
        {
            _active.PlayheadSample = Engine.PositionSamples;
            _active.EnsurePlayheadVisible();
        }
        Master.Tick(0.033, IsPlaying);

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
