using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.Audio.Montage;
using WaveLab.Util;

using WaveLab.Views.Controls;

namespace WaveLab.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static float[][]? _clipboard;
    private static int _clipboardRate;

    /// <summary>
    /// Largest selection held on the clipboard for a paste that may never come.
    /// </summary>
    /// <remarks>
    /// It is static, so it outlives the view model that filled it, and nothing cleared
    /// it: copying an hour-long stereo selection kept about 600 MB resident for the life
    /// of the process whether or not any file was still open.
    /// </remarks>
    private const long MaximumClipboardBytes = 512L * 1024 * 1024;

    private DocumentViewModel? _active;
    /// <summary>Steps the byte budget released since the last line was written about it.</summary>
    private int _releasedSteps;
    /// <summary>
    /// Set while a history move owns the status line, so the change event it raises does not write
    /// one that the caller is about to overwrite — taking the eviction note with it.
    /// </summary>
    private bool _suppressEditReport;
    private TabViewModel? _activeTab;
    private DocumentViewModel? _playbackDocument;
    private AudioDocument? _previewDocument;
    private bool? _previewRackRestoreState;
    private int _playbackEditVersion = -1;
    private long _playbackSession;
    private long _stoppedPlaybackSession;
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
    private TimeSpan _lastPlaybackRenderTime = TimeSpan.MinValue;
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
    private bool _documentOperationRunning;
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _cpuPrev;
    private DateTime _cpuPrevAt = DateTime.UtcNow;
    private int _tickCount;
    private string _cpuText = "CPU —";
    private string _ramText = "RAM —";
    private string _actionStatusText = "Ready.";
    private int _resourcesDisposed;

    public MainViewModel()
    {
        AudioDocument.UndoBudgetBytes = AppSettings.Instance.UndoLimitBytes;
        EffectFactory.EnsureFactoryPresets();

        Engine = new PlaybackEngine();
        Master = new MasterSectionViewModel(Engine.Master);
        Master.ProcessingTopologyChanged += RestartMonoPlaybackForTopologyChange;
        Master.StatusChanged += ReportAction;
        Engine.PlaybackStopped += OnPlaybackStopped;
        Engine.PlaybackFailed += OnPlaybackFailed;
        _transportRecorder.CaptureStopped += OnTransportCaptureStopped;

        UpdateRecordInputName();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        CompositionTarget.Rendering += OnRendering;

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += (_, _) => AutosaveTick();
        _autosaveTimer.Start();

        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(Save, () => !_documentOperationRunning && _active != null);
        SaveAsCommand = new RelayCommand(SaveAs, () => !_documentOperationRunning && _active != null);
        CloseTabCommand = new RelayCommand<TabViewModel>(CloseTab,
            tab => !_documentOperationRunning && (tab != null ? Documents.Contains(tab) : _activeTab != null));
        CloseAllCommand = new RelayCommand(CloseAll,
            () => !_documentOperationRunning && Documents.Count > 0);
        ExitCommand = new RelayCommand(() => Application.Current.MainWindow?.Close());

        UndoCommand = new RelayCommand(Undo,
            () => CanMutateDocument && _active?.Doc.CanUndo == true);
        RedoCommand = new RelayCommand(Redo,
            () => CanMutateDocument && _active?.Doc.CanRedo == true);
        CutCommand = new RelayCommand(Cut, () => CanMutateDocument && _active?.HasSelection == true);
        CopyCommand = new RelayCommand(Copy, () => CanMutateDocument && _active?.HasSelection == true);
        PasteCommand = new RelayCommand(Paste,
            () => CanMutateDocument && _active != null && _clipboard != null);
        DeleteCommand = new RelayCommand(DeleteSelection,
            () => CanMutateDocument && _active?.HasSelection == true);
        TrimCommand = new RelayCommand(Trim,
            () => CanMutateDocument && _active?.HasSelection == true);
        SelectAllCommand = new RelayCommand(() => WithDoc(d => d.SelectAll()), () => HasAudioDocument);

        PlayCommand = new RelayCommand(TogglePlay,
            () => HasAudioDocument && !IsTransportRecording && !IsFinalizingRecording);
        StopCommand = new RelayCommand(StopTransport,
            () => IsTransportRecording || HasPendingTransportRecording || Engine.IsPlaying || Engine.IsPaused);
        GoToStartCommand = new RelayCommand(GoToStart, () => HasDocument);
        SeekCommand = new RelayCommand<PlayheadSeekRequest>(HandlePlayheadSeek);
        ToggleLoopCommand = new RelayCommand(() => IsLooping = !IsLooping);

        ZoomInCommand = new RelayCommand(() => ZoomActiveDocument(1 / 1.5), () => HasAudioDocument);
        ZoomOutCommand = new RelayCommand(() => ZoomActiveDocument(1.5), () => HasAudioDocument);
        ZoomFitCommand = new RelayCommand(() => WithDoc(d => d.ZoomFull()), () => HasAudioDocument);
        ZoomSelectionCommand = new RelayCommand(() => WithDoc(d => d.ZoomToSelection()),
            () => _active?.HasSelection == true);

        GainUpCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.Gain(d, s, c, 3)),
            () => CanMutateAudio);
        GainDownCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.Gain(d, s, c, -3)),
            () => CanMutateAudio);
        NormalizeCommand = new RelayCommand(() => RequestNormalizePeakDialog?.Invoke(),
            () => CanMutateAudio);
        NormalizeLoudnessCommand = new RelayCommand(() => RequestNormalizeLoudnessDialog?.Invoke(),
            () => CanMutateAudio);
        FadeInCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.FadeIn(d, s, c)),
            () => CanMutateAudio);
        FadeOutCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.FadeOut(d, s, c)),
            () => CanMutateAudio);
        ReverseCommand = new RelayCommand(() => ApplyToRange(Processing.Reverse), () => CanMutateAudio);
        RemoveDcCommand = new RelayCommand(() => ApplyToRange(Processing.RemoveDcOffset), () => CanMutateAudio);
        InsertSilenceCommand = new RelayCommand(() => WithDoc(d =>
        {
            PrepareForDocumentEdit(d);
            Processing.InsertSilence(d.Doc, d.Cursor, 1.0);
        }), () => CanMutateAudio);

        AddMarkerCommand = new RelayCommand(() => WithDoc(d => d.AddMarker(
            d.HasSelection ? d.SelStart
            : IsPlaying && ReferenceEquals(d, _playbackDocument) ? d.PlayheadSample
            : d.Cursor)), () => HasAudioDocument);
        AddRegionCommand = new RelayCommand(() => WithDoc(d => d.AddRegionFromSelection()),
            () => _active?.HasSelection == true);
        PrevMarkerCommand = new RelayCommand(() => WithDoc(d => d.JumpToNextMarker(forward: false)),
            () => _active?.Markers.Count > 0);
        NextMarkerCommand = new RelayCommand(() => WithDoc(d => d.JumpToNextMarker(forward: true)),
            () => _active?.Markers.Count > 0);
        ClearMarkersCommand = new RelayCommand(() => WithDoc(d =>
        {
            d.Markers.Clear();
            d.Regions.Clear();
            d.NotifyMarkersChanged();
        }), () => _active is { } d && (d.Markers.Count > 0 || d.Regions.Count > 0));
        SmoothEditCommand = new RelayCommand(SmoothEditPoints, () => CanMutateAudio);

        ShowWaveformCommand = new RelayCommand(() => EditorView = EditorViewMode.Waveform);
        ShowSplitCommand = new RelayCommand(() => EditorView = EditorViewMode.Split);
        ShowSpectrogramCommand = new RelayCommand(() => EditorView = EditorViewMode.Spectrogram);
        UseRectangleToolCommand = new RelayCommand(() => SpectralTool = SpectralTool.Rectangle);
        UseLassoToolCommand = new RelayCommand(() => SpectralTool = SpectralTool.Lasso);
        UseMagicWandToolCommand = new RelayCommand(() => SpectralTool = SpectralTool.MagicWand);
        UseHarmonicToolCommand = new RelayCommand(() => SpectralTool = SpectralTool.Harmonic);
        UseLinearScaleCommand = new RelayCommand(() => SpectralScale = SpectralFrequencyScale.Linear);
        UseLogarithmicScaleCommand = new RelayCommand(() => SpectralScale = SpectralFrequencyScale.Logarithmic);
        UseConstantQScaleCommand = new RelayCommand(() => SpectralScale = SpectralFrequencyScale.ConstantQ);
        RenderCommand = new RelayCommand(RenderMaster, () => HasAudioDocument);
        ApplyChainCommand = new RelayCommand(ApplyChain, () => CanMutateAudio);
        RecordCommand = new RelayCommand(ToggleRecord, () => !IsFinalizingRecording);
        RecordSetupCommand = new RelayCommand(OpenRecordDialog,
            () => !IsTransportRecording && !IsFinalizingRecording && !HasPendingTransportRecording);
        SettingsCommand = new RelayCommand(() => RequestSettingsDialog?.Invoke());
        ExportCommand = new RelayCommand(() => RequestExportDialog?.Invoke(),
            () => !_documentOperationRunning && HasAudioDocument);
        StatisticsCommand = new RelayCommand(() => RequestStatisticsDialog?.Invoke(),
            () => !_documentOperationRunning && HasAudioDocument);
        OpenRecentCommand = new RelayCommand<string>(path => { if (path != null) OpenFiles([path]); });
        ClearRecentFilesCommand = new RelayCommand(ClearRecentFiles, () => RecentFiles.Count > 0);
        RecentFileActions = [new MenuEntry("Clear Recent Files", ClearRecentFilesCommand)];
        CommandPaletteCommand = new RelayCommand(() => RequestCommandPalette?.Invoke());
        HistoryCommand = new RelayCommand(() => RequestHistoryPanel?.Invoke(), () => HasAudioDocument);
        MatchLoudnessCommand = new RelayCommand(
            () => RequestMatchLoudnessDialog?.Invoke(), () => AudioDocuments.Any());
        AboutCommand = new RelayCommand(() => MessageBox.Show(
            $"Deep Groove {AppVersion}\n\nAudio editor and mastering suite.\nWAV/AIFF · MP3/FLAC/AAC import & export\nEffects rack · restoration · EBU R128 metering\nWASAPI playback and recording",
            "About Deep Groove", MessageBoxButton.OK, MessageBoxImage.Information));

        // Last, because it refreshes the command it has just been given: the recent list has
        // exactly one way in, so nothing can add a path and leave Clear looking at a stale count.
        SyncRecentFiles();
    }

    /// <summary>
    /// The version this build actually carries, read off the assembly rather than written down.
    /// </summary>
    /// <remarks>
    /// About said "2.0" for thirty-five releases because the number lived in a string literal and
    /// the release bump only ever touched the csproj; reading it here means the two cannot part
    /// company again. <c>InformationalVersion</c> is <c>2.0.35+&lt;commit&gt;</c> once the SDK
    /// stamps the source revision onto it, and that suffix is build provenance rather than
    /// something to show someone, so it is cut. Falls back to the three-part assembly version,
    /// and then to nothing: a name with no number after it is honest, a stale number is not.
    /// </remarks>
    private static string AppVersion { get; } = ResolveAppVersion();

    private static string ResolveAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string? stamped = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(stamped))
        {
            int plus = stamped.IndexOf('+');
            return plus < 0 ? stamped : stamped[..plus];
        }
        return assembly.GetName().Version?.ToString(3) ?? "";
    }

    public PlaybackEngine Engine { get; }
    public MasterSectionViewModel Master { get; }
    /// <summary>
    /// Everything open in a tab: audio documents and montages.
    /// </summary>
    /// <remarks>
    /// Widened from <c>DocumentViewModel</c> when the montage arrived. <see cref="ActiveDocument"/>
    /// stays typed to a document and is null whenever the active tab is not one, which is what keeps
    /// the forty-odd audio commands from having to ask what sort of tab they are looking at — they
    /// simply become unavailable. Anything that genuinely means "every open file" says
    /// <c>OfType&lt;DocumentViewModel&gt;()</c>.
    /// </remarks>
    public ObservableCollection<TabViewModel> Documents { get; } = [];

    /// <summary>Every open audio document, which is not the same as every open tab.</summary>
    public IEnumerable<DocumentViewModel> AudioDocuments => Documents.OfType<DocumentViewModel>();

    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    /// <summary>
    /// Takes any tab, not just a document: the close button passes the tab itself, and
    /// <c>RelayCommand&lt;T&gt;</c> hard-casts its parameter — so a montage tab would throw on every
    /// requery, not merely when clicked.
    /// </summary>
    public RelayCommand<TabViewModel> CloseTabCommand { get; }
    public RelayCommand CloseAllCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand HistoryCommand { get; }
    public RelayCommand MatchLoudnessCommand { get; }

    /// <summary>
    /// What the Edit menu says Undo will do. The engine has always known the name of the next step;
    /// the menu simply never said it.
    /// </summary>
    public string UndoMenuHeader =>
        _active?.Doc.NextUndoName is { } name ? $"Undo {name}" : "Undo";

    public string RedoMenuHeader =>
        _active?.Doc.NextRedoName is { } name ? $"Redo {name}" : "Redo";

    /// <summary>
    /// True while a long operation owns a document — a restoration pass, a render, a stretch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from the clipboard flag beside it, and the distinction is load-bearing. The progress
    /// overlay covers the shell but the Edit History panel is a window of its own, so it is the one
    /// surface in the app that can reach a document from outside that overlay — and the tools commit
    /// with a <i>length</i> check rather than an identity one, so a jump that leaves the length
    /// alone would let a result computed from the old audio be spliced into the new. Nothing may
    /// move a document's samples while this is set.
    /// </para>
    /// </remarks>
    public bool IsDocumentOperationRunning => _documentOperationRunning;
    private bool CanMutateDocument => !_editOperationRunning && !_documentOperationRunning;
    private bool CanMutateAudio => CanMutateDocument && HasAudioDocument;

    /// <summary>Told by the shell as a long document operation starts and finishes.</summary>
    public void SetDocumentOperationRunning(bool value)
    {
        if (!Set(ref _documentOperationRunning, value, nameof(IsDocumentOperationRunning))) return;
        RefreshEditCommandStates();
        DocumentOperationRunningChanged?.Invoke();
    }
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
    public RelayCommand NormalizeLoudnessCommand { get; }
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
    public RelayCommand ClearRecentFilesCommand { get; }
    public RelayCommand CommandPaletteCommand { get; }
    public RelayCommand AboutCommand { get; }

    /// <summary>The recent paths, as the submenu's own entries.</summary>
    public ObservableCollection<MenuEntry> RecentFiles { get; } = [];

    /// <summary>
    /// What sits below the separator at the foot of the submenu. A collection rather than a
    /// declared menu item so that the menu generates it: see <see cref="MenuEntry"/>.
    /// </summary>
    public IReadOnlyList<MenuEntry> RecentFileActions { get; }

    /// <summary>
    /// Whether the Recent Files submenu has any paths above its separator. Bound rather than
    /// derived in the menu because an empty list would otherwise open on a rule with nothing
    /// above it.
    /// </summary>
    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>The window shows the record dialog when this fires.</summary>
    public event Action? RequestRecordDialog;
    /// <summary>Ask the window to refresh the spectrogram for the active view.</summary>
    public event Action? RequestSpectrogram;
    public event Action? RequestSettingsDialog;
    public event Action? RequestExportDialog;
    public event Action? RequestStatisticsDialog;
    public event Action? RequestCommandPalette;
    /// <summary>The window shows the Edit History panel for the active document when this fires.</summary>
    public event Action? RequestHistoryPanel;
    public event Action? RequestMatchLoudnessDialog;

    /// <summary>
    /// The window asks for a peak ceiling and then calls <see cref="ApplyPeakNormalize"/>. The
    /// command lives here and the dialog does not, for the same reason every other Request does:
    /// a <c>ParamDialog</c> needs an owner window, and the view model has none.
    /// </summary>
    public event Action? RequestNormalizePeakDialog;

    /// <summary>
    /// The window asks for a loudness target and applies it. Unlike the peak command this one does
    /// not come back through the view model at all: the measurement is long enough to need progress
    /// and cancellation, which is the window's job.
    /// </summary>
    public event Action? RequestNormalizeLoudnessDialog;

    /// <summary>Raised when <see cref="IsDocumentOperationRunning"/> moves, in either direction.</summary>
    public event Action? DocumentOperationRunningChanged;

    /// <summary>
    /// The selected tab, whatever kind it is. This is what the tab strip binds to.
    /// </summary>
    /// <remarks>
    /// Bound rather than <see cref="ActiveDocument"/> because <c>SelectedItem</c> is typed
    /// <c>object</c>: clicking a montage tab would push a value the document setter cannot take, the
    /// binding would fail silently, and the waveform and transport would carry on operating on the
    /// file behind the tab the user had just left.
    /// </remarks>
    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (ReferenceEquals(_activeTab, value)) return;
            _activeTab = value;
            Raise();
            Raise(nameof(ActiveMontage));
            Raise(nameof(HasMontage));

            // Always assigned, even when both old and new tabs are montages and this is null both
            // times: the setter below early-returns on an unchanged value, so the notifications it
            // owns would be skipped and the editor would keep showing the previous tab's state.
            ApplyActiveDocument(value as DocumentViewModel);
        }
    }

    public MontageViewModel? ActiveMontage => _activeTab as MontageViewModel;
    public bool HasMontage => _activeTab is MontageViewModel;

    public DocumentViewModel? ActiveDocument
    {
        get => _active;
        set
        {
            // Selecting a document directly selects its tab, so the two cannot disagree.
            if (value != null && !ReferenceEquals(_activeTab, value))
            {
                ActiveTab = value;
                return;
            }
            ApplyActiveDocument(value);
        }
    }

    /// <summary>
    /// Points the editor at a document, or at nothing when the active tab is not one.
    /// </summary>
    /// <remarks>
    /// The early return is deliberate and correct: moving between two montage tabs leaves this null
    /// both times, and nothing it announces — whether a document exists, its title, its channel
    /// count — has changed. What <em>has</em> changed is announced by <see cref="ActiveTab"/>.
    /// </remarks>
    private void ApplyActiveDocument(DocumentViewModel? value)
    {
        {
            var previous = _active;
            if (!Set(ref _active, value, nameof(ActiveDocument))) return;
            if (previous != null)
            {
                previous.PropertyChanged -= OnActiveDocumentPropertyChanged;
                previous.Doc.Changed -= OnActiveDocumentEdited;
                previous.Doc.HistoryReleased -= OnActiveDocumentHistoryReleased;
            }
            if (_active != null)
            {
                _active.PropertyChanged += OnActiveDocumentPropertyChanged;
                _active.Doc.Changed += OnActiveDocumentEdited;
                _active.Doc.HistoryReleased += OnActiveDocumentHistoryReleased;
            }
            Raise(nameof(HasDocument));
            Raise(nameof(HasAudioDocument));
            Raise(nameof(ShowsSpectralBar));
            Raise(nameof(HasMultichannelDocument));
            Raise(nameof(HasMonoDocument));
            Raise(nameof(CanAnalyzeCleanup));
            Raise(nameof(WindowTitle));
            Raise(nameof(StatusSamples));
            Raise(nameof(IsActiveDocumentPlaying));
            Raise(nameof(ShowsMonitorGain));
            Raise(nameof(MonitorGainDb));
            Raise(nameof(MonitorGainText));
            // A time-frequency region belongs to the file it was drawn on; carrying it to another
            // tab would offer a repair at a position that means nothing there.
            SpectralSelection = SpectralSelection.None;
            // Raised again unconditionally: the setter above says nothing when the region was
            // already empty, and the tab switched to has a time selection of its own to report.
            RaiseSpectralSelectionState();
            // A note about one file's history is not a note about the next one's.
            _releasedSteps = 0;
            RefreshEditCommandStates();
        }
    }

    public bool HasDocument => _active != null;
    public bool HasAudioDocument => _active?.Doc.Length > 0;
    public bool HasMultichannelDocument => _active?.Doc.ChannelCount > 1;
    public bool HasMonoDocument => _active?.Doc.ChannelCount == 1;
    public bool CanAnalyzeCleanup => HasAudioDocument &&
                                     !IsTransportRecording &&
                                     !IsFinalizingRecording &&
                                     !HasPendingTransportRecording;
    public string WindowTitle => _active == null ? "Deep Groove" : $"{_active.Doc.Title} — {_active.FormatText} · {TimeFormat.Compact(_active.Doc.Duration)}";

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            Raise(nameof(IsActiveDocumentPlaying));
            PlayCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// True only when the document shown in the waveform owns the active transport.
    /// Preview playback and playback continuing in another tab must not change how
    /// the visible document anchors zoom.
    /// </summary>
    public bool IsActiveDocumentPlaying =>
        IsPlaybackActiveForDocument(_active, _playbackDocument, _isPlaying);

    internal static bool IsPlaybackActiveForDocument(
        DocumentViewModel? document,
        DocumentViewModel? playbackDocument,
        bool isPlaying) =>
        isPlaying && document != null && ReferenceEquals(document, playbackDocument);

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
            StopCommand.RaiseCanExecuteChanged();
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
            StopCommand.RaiseCanExecuteChanged();
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

    public string StatusEngine
    {
        get
        {
            var settings = AppSettings.Instance;
            string mode = string.Equals(settings.OutputShareMode, "exclusive", StringComparison.OrdinalIgnoreCase)
                ? "Exclusive"
                : "Shared";
            string scheduler = settings.OutputEventSync ? "event" : "poll";
            return $"Out: {PlaybackEngine.CurrentOutputName()} · WASAPI {mode} · {settings.BufferMs} ms {scheduler}";
        }
    }
    public string StatusSamples => _active == null ? "" : $"{_active.Doc.Length:N0} samples";
    public string ActionStatusText { get => _actionStatusText; private set => Set(ref _actionStatusText, value); }

    public string StatusAutosave =>
        !AppSettings.Instance.AutosaveEnabled ? "Autosave off"
        : _lastAutosave == null ? "Autosave on"
        : $"Autosaved {(int)Math.Max(0, (DateTime.Now - _lastAutosave.Value).TotalMinutes)} min ago";

    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }

    /// <summary>
    /// Progress and cancellation for long operations. Driven from a UI timer in the window; the DSP
    /// layer already produces the tokens and progress reports this surfaces.
    /// </summary>
    public ProgressHost Progress { get; } = new();

    private EditorViewMode _editorView = EditorViewMode.Waveform;

    /// <summary>
    /// Which representation the editor area shows. Waveform is the default so that nothing about
    /// the app changes until the spectrogram is asked for.
    /// </summary>
    public EditorViewMode EditorView
    {
        get => _editorView;
        set
        {
            if (!Set(ref _editorView, value)) return;
            Raise(nameof(IsWaveformView));
            Raise(nameof(IsSplitView));
            Raise(nameof(IsSpectrogramView));
            Raise(nameof(ShowsSpectrogram));
            Raise(nameof(ShowsSpectralScale));
            Raise(nameof(ShowsBinsPerOctave));
            Raise(nameof(SpectralToolHint));
            EditorViewChanged?.Invoke();
        }
    }

    public bool IsWaveformView => _editorView == EditorViewMode.Waveform;
    public bool IsSplitView => _editorView == EditorViewMode.Split;
    public bool IsSpectrogramView => _editorView == EditorViewMode.Spectrogram;

    /// <summary>
    /// Whether the spectrogram is on screen at all, and so whether the tools that draw <em>on</em>
    /// it belong in the toolbar. Bound directly rather than through an inverting converter, so the
    /// rule is one testable property instead of markup.
    /// </summary>
    public bool ShowsSpectrogram => _editorView != EditorViewMode.Waveform;

    /// <summary>
    /// Whether the spectral bar is on screen. Wider than <see cref="ShowsSpectrogram"/>, and
    /// deliberately: the four actions work through a mask, and an ordinary waveform selection is a
    /// mask across the whole frequency band, so there is nothing about them that needs the picture.
    /// What needs the picture is the four <em>selection tools</em> and the scale switch, and those
    /// still follow <see cref="ShowsSpectrogram"/>.
    /// </summary>
    public bool ShowsSpectralBar => HasAudioDocument;

    /// <summary>
    /// Width of the shell, so the spectral bar can give up its least important control rather than
    /// have it cut. Infinite until the window says otherwise, which is what a view model with no
    /// window — every unit test — should assume.
    /// </summary>
    public double ShellWidthPixels
    {
        get => _shellWidth;
        set
        {
            if (!double.IsFinite(value) || value <= 0) return;
            if (!Set(ref _shellWidth, value)) return;
            Raise(nameof(ShowsSpectralScale));
            Raise(nameof(ShowsBinsPerOctave));
        }
    }

    /// <summary>
    /// Below this the spectral bar cannot hold everything in it, and the scale switch is what goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured rather than chosen. With the tools, the four actions, both rules and a drawn band's
    /// readout the bar's children want <b>1314 px</b>, and the window is 28 px wider than the panel
    /// they sit in — so everything fits from about 1342 px, and this is that rounded up. Below it a
    /// <c>ClipToBounds</c> Border was cutting <c>CONSTANT-Q</c> mid-glyph, which this repo already
    /// records as reading like a drawing fault rather than like a control that did not fit.
    /// </para>
    /// <para>
    /// The switch is the right thing to lose and the readout is not: the readout says what a repair
    /// is about to act on and exists nowhere else, while the scale is one choice out of three that
    /// View ▸ Frequency Scale carries at any width. Nothing becomes unreachable — the shell's
    /// declared minimum is 1180 and its default is 1680, so this only bites in between.
    /// </para>
    /// </remarks>
    public const double SpectralScaleMinimumWidth = 1350;

    /// <summary>Whether the bar has room for the scale switch as well as everything else in it.</summary>
    public bool ShowsSpectralScale => ShowsSpectrogram && _shellWidth >= SpectralScaleMinimumWidth;

    /// <summary>Raised so the window can lay the editor rows out for the new mode.</summary>
    public event Action? EditorViewChanged;

    // ── monitor gain ─────────────────────────────────────────────

    /// <summary>
    /// Whether the monitor bar belongs on screen. Keyed on the document being a residual and
    /// not on the gain being above unity, so pulling the lift back to 0 dB to hear the true
    /// level is not a one-way door that takes the control away with it.
    /// </summary>
    public bool ShowsMonitorGain => _active?.Doc.IsResidual == true;

    /// <summary>
    /// The lift in decibels, live. Writing it changes what reaches the speakers on the next
    /// audio callback and nothing else — the samples, the waveform, and every save and export
    /// are untouched, which is the property that makes a residual worth keeping at all.
    /// </summary>
    public double MonitorGainDb
    {
        get => _active is { } document ? ResidualSummary.GainToDb(document.Doc.MonitorGain) : 0;
        set
        {
            if (_active is not { } document) return;
            double clamped = Math.Clamp(value, 0, ResidualSummary.MaximumMonitorGainDb);
            float gain = (float)Math.Pow(10, clamped / 20.0);
            if (Math.Abs(document.Doc.MonitorGain - gain) < 1e-6f) return;
            document.Doc.MonitorGain = gain;
            Raise(nameof(MonitorGainDb));
            Raise(nameof(MonitorGainText));
            Raise(nameof(ShowsMonitorGain));
        }
    }

    public string MonitorGainText => $"+{MonitorGainDb:0.0} dB";

    /// <summary>Return the active document to its true level; the bar disappears with it.</summary>
    public void ResetMonitorGain() => MonitorGainDb = 0;

    public RelayCommand ShowWaveformCommand { get; }
    public RelayCommand ShowSplitCommand { get; }
    public RelayCommand ShowSpectrogramCommand { get; }

    // ── spectral selection ───────────────────────────────────────

    private SpectralSelection _spectralSelection = SpectralSelection.None;
    private SpectralTool _spectralTool = SpectralTool.Rectangle;
    private SpectralFrequencyScale _spectralScale = SpectralFrequencyScale.Logarithmic;
    private int _spectralBinsPerOctave = 36;
    private double _shellWidth = double.PositiveInfinity;

    /// <summary>
    /// Which analysis and axis the spectral picture is made with. Design:
    /// <c>docs/design/constant_q.png</c>.
    /// </summary>
    public SpectralFrequencyScale SpectralScale
    {
        get => _spectralScale;
        set
        {
            bool changed = Set(ref _spectralScale, value);

            // Announced even when the scale did not move. The three View menu items are
            // IsCheckable, so a click flips the tick locally before the command runs, and their
            // binding is one-way: only the source can put it back. Choosing the scale already in
            // force is a no-op everywhere else, and used to leave that item sitting unticked
            // beside the scale it names until some later change happened to speak up.
            Raise(nameof(IsLinearScale));
            Raise(nameof(IsLogarithmicScale));
            Raise(nameof(IsConstantQScale));
            if (changed) Raise(nameof(ShowsBinsPerOctave));
        }
    }

    public bool IsLinearScale => _spectralScale == SpectralFrequencyScale.Linear;
    public bool IsLogarithmicScale => _spectralScale == SpectralFrequencyScale.Logarithmic;
    public bool IsConstantQScale => _spectralScale == SpectralFrequencyScale.ConstantQ;

    /// <summary>
    /// Shown only for constant-Q, because it is the only scale for which the phrase means anything:
    /// the other two draw an analysis whose bins are evenly spaced in hertz.
    /// </summary>
    /// <remarks>
    /// Follows the scale switch rather than the spectrogram: it sits immediately beside it and
    /// describes it, so leaving it behind when the switch goes would strand a control explaining a
    /// choice that is no longer on screen — and it is 100 px the bar has already run out of.
    /// </remarks>
    public bool ShowsBinsPerOctave =>
        ShowsSpectralScale && _spectralScale == SpectralFrequencyScale.ConstantQ;

    /// <summary>Constant-Q resolution. 12 is a semitone; 36 is a third of one.</summary>
    public int SpectralBinsPerOctave
    {
        get => _spectralBinsPerOctave;
        set => Set(ref _spectralBinsPerOctave, Math.Clamp(value, 6, 96));
    }

    public IReadOnlyList<int> BinsPerOctaveChoices { get; } = [12, 24, 36, 48];

    public RelayCommand UseLinearScaleCommand { get; }
    public RelayCommand UseLogarithmicScaleCommand { get; }
    public RelayCommand UseConstantQScaleCommand { get; }

    /// <summary>
    /// What the spectral editor currently has selected. It lives here rather than on the control so
    /// that the repair actions and the readout beside them can bind to it, and so that switching
    /// away from the spectrogram and back does not quietly lose it.
    /// </summary>
    public SpectralSelection SpectralSelection
    {
        get => _spectralSelection;
        set
        {
            if (!Set(ref _spectralSelection, value ?? SpectralSelection.None)) return;
            RaiseSpectralSelectionState();
        }
    }

    /// <summary>
    /// Re-reads what the spectral actions would work through. Raised from the drawn selection, from
    /// the document's own selection, and from anything that changes which document those belong to
    /// — the four properties answer from whichever of the two is in force.
    /// </summary>
    private void RaiseSpectralSelectionState()
    {
        Raise(nameof(HasSpectralSelection));
        Raise(nameof(NeedsSpectralSelection));
        Raise(nameof(SpectralSpanText));
        Raise(nameof(SpectralBandText));
    }

    /// <summary>Which tool the next gesture on the spectrogram uses.</summary>
    public SpectralTool SpectralTool
    {
        get => _spectralTool;
        set
        {
            if (!Set(ref _spectralTool, value)) return;
            Raise(nameof(IsRectangleTool));
            Raise(nameof(IsLassoTool));
            Raise(nameof(IsMagicWandTool));
            Raise(nameof(IsHarmonicTool));
            Raise(nameof(SpectralToolHint));
        }
    }

    public bool IsRectangleTool => _spectralTool == SpectralTool.Rectangle;
    public bool IsLassoTool => _spectralTool == SpectralTool.Lasso;
    public bool IsMagicWandTool => _spectralTool == SpectralTool.MagicWand;
    public bool IsHarmonicTool => _spectralTool == SpectralTool.Harmonic;

    public RelayCommand UseRectangleToolCommand { get; }
    public RelayCommand UseLassoToolCommand { get; }
    public RelayCommand UseMagicWandToolCommand { get; }
    public RelayCommand UseHarmonicToolCommand { get; }

    /// <summary>What the current tool expects the user to do, shown until something is selected.</summary>
    /// <remarks>
    /// The spectrogram wordings are left exactly as they were, and deliberately: this sits at the
    /// far right of a bar that has already run out of room once, and it is the last thing laid out,
    /// so it is the first thing cut. Measured at the shell's 1180 px minimum it is given 88 px —
    /// naming the waveform route here as well took it to 343 px wanted and cut it at 1400 too. The
    /// route belongs on the buttons' tool tips, which have room, and in waveform mode, where there
    /// is no picture to name instead.
    /// </remarks>
    public string SpectralToolHint => !ShowsSpectrogram
        ? "Select a range on the waveform"
        : _spectralTool switch
        {
            SpectralTool.Lasso => "Draw around the defect",
            SpectralTool.MagicWand => "Click the defect to grow a region through it",
            SpectralTool.Harmonic => "Drag from the fundamental to take it and its partials",
            _ => "Drag a region on the spectrogram",
        };

    /// <summary>
    /// The grid every spectral edit is expressed in — anchored at sample zero, at the transform
    /// length and hop the repair itself uses, never the display's.
    /// </summary>
    private static readonly SpectrogramSettings RepairGrid = SpectrogramSettings.Default;

    public bool HasSpectralSelection => !_spectralSelection.IsEmpty || CanUseTimeSelection;

    /// <summary>Whether to prompt for a selection instead of showing one. The toolbar binds both.</summary>
    public bool NeedsSpectralSelection => !HasSpectralSelection;

    /// <summary>
    /// The document's ordinary selection, when a spectral action could work through it: nothing has
    /// been drawn on the spectrogram, a range is selected, and it is short enough to build a
    /// full-band mask for.
    /// </summary>
    /// <remarks>
    /// Deliberately does <em>not</em> build the mask. This is read from a binding on four buttons
    /// and re-read on every pixel of a selection drag; a full band over a minute of audio is
    /// millions of cells, and allocating that inside a property getter would make dragging a
    /// selection cost what a repair costs. <see cref="ResolveSpectralSelection"/> builds it, once,
    /// when an action actually runs.
    /// </remarks>
    private (int Start, int End)? TimeSelectionSpan
    {
        get
        {
            if (!_spectralSelection.IsEmpty) return null;
            if (_active is not { } d || d.Doc.Length == 0 || !d.HasSelection) return null;
            int start = Math.Clamp(Math.Min(d.SelStart, d.SelEnd), 0, d.Doc.Length);
            int end = Math.Clamp(Math.Max(d.SelStart, d.SelEnd), 0, d.Doc.Length);
            if (end - start < 2) return null;
            return SpectralMask.FullBandFits(start, end, RepairGrid.FftSize, RepairGrid.Hop)
                ? (start, end)
                : null;
        }
    }

    private bool CanUseTimeSelection => TimeSelectionSpan is not null;

    /// <summary>
    /// What a spectral action will act through: the region drawn on the spectrogram if there is one,
    /// otherwise the time selection taken across the whole frequency band. Builds the mask, so it is
    /// called once per action rather than from a binding.
    /// </summary>
    public SpectralSelection ResolveSpectralSelection()
    {
        if (!_spectralSelection.IsEmpty) return _spectralSelection;
        if (_active is not { } d || TimeSelectionSpan is not var (start, end)) return SpectralSelection.None;

        SpectralMask mask = SpectralMask.FullBand(start, end, RepairGrid.FftSize, RepairGrid.Hop);
        return mask.IsEmpty
            ? SpectralSelection.None
            : new SpectralSelection(SpectralTool.Rectangle, mask, d.Doc.SampleRate,
                RepairGrid.FftSize, RepairGrid.Hop);
    }

    /// <summary>The selected time span, as the toolbar shows it.</summary>
    public string SpectralSpanText
    {
        get
        {
            if (!_spectralSelection.IsEmpty)
            {
                SpectralRegion drawn = _spectralSelection.Bounds;
                int drawnRate = _spectralSelection.SampleRate;
                return $"{TimeFormat.Position(drawn.StartSample, drawnRate)} → " +
                       $"{TimeFormat.Position(drawn.EndSample, drawnRate)}";
            }
            if (_active is not { } d || TimeSelectionSpan is not var (start, end)) return "—";
            return $"{TimeFormat.Position(start, d.Doc.SampleRate)} → " +
                   $"{TimeFormat.Position(end, d.Doc.SampleRate)}";
        }
    }

    /// <summary>The selected frequency band, as the toolbar shows it.</summary>
    public string SpectralBandText
    {
        get
        {
            if (!_spectralSelection.IsEmpty)
            {
                SpectralRegion drawn = _spectralSelection.Bounds;
                return $"{Hertz(drawn.LowFrequency)} → {Hertz(drawn.HighFrequency)}";
            }
            // Two words rather than "0 Hz → 22.05 kHz", which says the same thing and is 100 px
            // wider. The scale switch is docked after this readout, so it is what pays for every
            // pixel spent here — measured, the numbers took it from 37.5 px to 2 at the shell's
            // 1180 px minimum. And they add nothing: DC to Nyquist *is* the whole band, which is
            // what needed saying, and the rate is already in the title bar.
            if (!CanUseTimeSelection) return "—";
            return "full band";
        }
    }

    private static string Hertz(double frequency) => frequency >= 1_000
        ? $"{frequency / 1_000:0.##} kHz"
        : $"{frequency:0} Hz";

    public void ReportAction(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ActionStatusText = $"✓ {message.Trim()}";
    }

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

    private async Task OpenFilesAsync(IEnumerable<string> paths, OpenBitDepth? openAs = null,
        CancellationToken cancellationToken = default)
    {
        if (_shuttingDown) return;
        foreach (var path in paths.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // The decoders report no total — Media Foundation in particular cannot — so this is
                // an indeterminate operation. It is still worth hosting: a long decode used to freeze
                // the window outright, and now it can at least be seen and abandoned.
                await Progress.RunBlockingAsync($"Opening {Path.GetFileName(path)}",
                    "Decoding and building the waveform overview",
                    async (_, token) =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                            token, cancellationToken);
                        CancellationToken operationToken = linked.Token;
                        // decode AND build the peak pyramid off the UI thread — the tab appears fully drawn
                        var (doc, peaks) = await Task.Run(() =>
                        {
                            var loaded = openAs.HasValue
                                ? AudioImporter.LoadAs(path, openAs.Value, operationToken)
                                : AudioImporter.Load(path, operationToken);
                            var store = new PeakStore();
                            store.Rebuild(loaded, cancellationToken: operationToken);
                            return (loaded, store);
                        }, operationToken);
                        operationToken.ThrowIfCancellationRequested();
                        AddDocument(doc, peaks);
                        ReportAction($"{doc.Title} opened.");
                        AppSettings.Instance.LastOpenFolder = Path.GetDirectoryName(path);
                        if (!AppSettings.Instance.AddRecentFile(path)) ReportSettingsSaveFailure();
                        SyncRecentFiles();
                    });
            }
            catch (OperationCanceledException)
            {
                ReportAction($"Opening {Path.GetFileName(path)} cancelled.");
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
        foreach (string path in AppSettings.Instance.RecentFilesSnapshot())
            RecentFiles.Add(new MenuEntry(path, OpenRecentCommand, path));
        Raise(nameof(HasRecentFiles));
        ClearRecentFilesCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Empties the recent-file list. The settings write can fail, and the list is rolled back
    /// when it does, so the status line is only claimed after the write is known to have landed.
    /// </summary>
    private void ClearRecentFiles()
    {
        if (AppSettings.Instance.ClearRecentFiles()) ReportAction("Recent file list cleared.");
        else ReportSettingsSaveFailure();
        SyncRecentFiles();
    }

    private static void ReportSettingsSaveFailure() => MessageBox.Show(
        "Deep Groove could not save its settings:\n" + AppSettings.Instance.LastSaveError,
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

    public void AddDocument(AudioDocument doc, PeakStore? prebuiltPeaks = null, bool activate = true)
    {
        var vm = new DocumentViewModel(doc, prebuiltPeaks);
        Documents.Add(vm);
        // A workspace with documents and no selection is not a state anything else here handles,
        // so the first tab always activates whatever the caller asked for.
        if (activate || ActiveTab == null) ActiveTab = vm;
    }

    /// <summary>Opens a montage in its own tab and makes it the active one.</summary>
    public MontageViewModel AddMontage(MontageDocument montage)
    {
        ArgumentNullException.ThrowIfNull(montage);
        var vm = new MontageViewModel(montage);
        Documents.Add(vm);
        ActiveTab = vm;
        return vm;
    }

    public void AddGeneratedDocument(AudioDocument doc, string? completedAction = null,
        bool activate = true)
    {
        doc.MarkUnsaved();
        AddDocument(doc, activate: activate);
        ReportAction(completedAction ?? (doc.CaptureNote is { } note
            ? $"{doc.Title} created in a new tab. {note}"
            : $"{doc.Title} created in a new tab."));
    }

    /// <summary>
    /// Open what a restoration pass removed as its own tab, so the claim the tool just made —
    /// that this was damage and not music — can be listened to rather than taken on trust.
    /// Returns false, and opens nothing, when the pass took nothing out.
    /// </summary>
    /// <remarks>
    /// The tab does not steal focus. It arrives immediately after an Apply, and moving the user
    /// off the file they just restored to a file of clicks is not what they asked for; the tab
    /// strip and the status line are enough to find it.
    /// <para>
    /// The samples are the exact difference. What makes it audible is
    /// <see cref="AudioDocument.MonitorGain"/>, which reaches the speakers and nothing else, so
    /// saving or exporting this tab still writes the true residual.
    /// </para>
    /// </remarks>
    /// <param name="rangeStart">
    /// Where the pass started in the source. A residual is only as long as the range it came from,
    /// so two selections restored in one session would otherwise arrive as two identically named
    /// tabs with nothing to tell them apart or say where either belongs.
    /// </param>
    /// <param name="levels">
    /// Peak and RMS, measured on the worker that built the residual. Omitting them costs a full
    /// pass over an album-sized buffer on whatever thread calls this — which for every caller here
    /// is the UI thread, where this repo does no O(file) work.
    /// </param>
    public bool AddResidualDocument(AudioDocument source, float[][] removed, string toolName,
        int rangeStart = 0, RestorationPreview.ResidualLevels? levels = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        RestorationPreview.ResidualLevels measured =
            levels ?? RestorationPreview.MeasureLevels(removed);
        if (measured.Peak <= ResidualSummary.SilenceThreshold)
        {
            ReportAction(ResidualSummary.DescribeNothingRemoved(toolName));
            return false;
        }

        // 32-bit float because this is computed rather than captured, matching the channel
        // tools; it also makes Save As offer float first, which is the only depth that can
        // hold a residual without dithering the very thing being examined.
        var doc = new AudioDocument(removed, source.SampleRate, 32)
        {
            Title = ResidualTitle(source, rangeStart),
            IsResidual = true,
            MonitorGain = ResidualSummary.MonitorGainFor(measured.Peak, measured.Rms),
        };
        AddGeneratedDocument(doc, ResidualSummary.Describe(doc.Title, measured.Peak, doc.MonitorGain),
            activate: false);
        return true;
    }

    private static string ResidualTitle(AudioDocument source, int rangeStart)
    {
        string name = Path.GetFileNameWithoutExtension(source.Title);
        if (rangeStart <= 0 || source.SampleRate <= 0) return $"{name} (removed).wav";
        return $"{name} (removed at {TimeFormat.Compact((double)rangeStart / source.SampleRate)}).wav";
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
            RequiresSaveAs = doc.RequiresSaveAs,
            CaptureNote = doc.CaptureNote,

            // Shared, not copied. The codecs clone before touching a chunk, so the snapshot only
            // ever reads this — and leaving it out is what would quietly drop the file's broadcast
            // metadata on the first ordinary Save, which is the whole point of carrying it.
            Riff = doc.Riff,
        };
    }

    private static bool IsAiffFamilyPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".aifc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClassicAiffPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="markers">
    /// A point-in-time copy of the document's markers, so they are embedded in the file itself as
    /// well as written to the sidecar. The sidecar is invisible to every other program and is lost
    /// the moment the audio file is copied on its own.
    /// </param>
    private static void SaveEditableDocument(
        AudioDocument doc,
        string path,
        int depth,
        bool dither,
        bool? writeAiff = null,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null,
        IReadOnlyList<Marker>? markers = null)
    {
        bool useAiff = writeAiff ?? IsClassicAiffPath(path);
        string extension = Path.GetExtension(path);
        if (useAiff && !IsClassicAiffPath(path))
            throw new NotSupportedException(
                "AIFF output requires a .aif or .aiff file name; AIFF-C output is not supported.");
        if (!useAiff && !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("WAV output requires a .wav file name.");

        if (useAiff)
            AiffCodec.Save(doc, path, depth, dither, cancellationToken, progress,
                Audio.Dsp.DitherKind.FlatTpdf, markers);
        else
            WavCodec.Save(doc, path, depth, dither, cancellationToken, progress,
                Audio.Dsp.DitherKind.FlatTpdf, markers);
    }

    /// <summary>The markers as they stand, copied on the UI thread before any background save.</summary>
    private static List<Marker> MarkerSnapshot(DocumentViewModel d) =>
        [.. d.Markers.Select(m => new Marker { Name = m.Name, Position = m.Position })];

    private async void Save()
    {
        if (_active != null) await TrackSaveOperationAsync(SaveCoreAsync(_active));
    }

    private async Task SaveCoreAsync(DocumentViewModel d)
    {
        if (_shuttingDown) return;
        var doc = d.Doc;
        if (doc.FilePath == null || doc.RequiresSaveAs) { await SaveAsCoreAsync(d); return; }
        if (!_savesInFlight.Add(doc.SessionId)) return; // a save for this document is already writing
        int version = doc.EditVersion;
        var snapshot = SnapshotDoc(doc);
        var markers = MarkerSnapshot(d);
        string path = doc.FilePath!;
        int depth = doc.SourceBitDepth;
        try
        {
            // Both codecs write to a staged temp file and move it into place at the end, so a
            // cancelled save abandons the temp and leaves the original file untouched.
            await Progress.RunBlockingAsync($"Saving {doc.Title}",
                $"{depth}-bit{(depth == 16 && snapshot.Dither16BitOnSave ? " · dithered" : "")}",
                (progress, token) => Task.Run(() => SaveEditableDocument(snapshot, path, depth,
                    dither: depth == 16 && snapshot.Dither16BitOnSave,
                    writeAiff: null, cancellationToken: token, progress: progress,
                    markers: markers), token));
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
            ReportAction(doc.EditVersion == version
                ? $"{doc.Title} saved."
                : $"{doc.Title} save completed · newer edits remain unsaved.");
        }
        catch (OperationCanceledException)
        {
            ReportAction($"Saving {doc.Title} cancelled · the file on disk is unchanged.");
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
        bool preferAiff = IsAiffFamilyPath(doc.FilePath ?? doc.Title);
        var dlg = new SaveFileDialog
        {
            Filter = "WAV — 32-bit float|*.wav|WAV — 24-bit PCM|*.wav|" +
                     "WAV — 16-bit PCM (dithered)|*.wav|WAV — 16-bit PCM (no dither)|*.wav|" +
                     "AIFF — 32-bit PCM|*.aiff|AIFF — 24-bit PCM|*.aiff|" +
                     "AIFF — 16-bit PCM (dithered)|*.aiff|AIFF — 16-bit PCM (no dither)|*.aiff",
            FilterIndex = (preferAiff, doc.SourceBitDepth, doc.Dither16BitOnSave) switch
            {
                (true, 24, _) => 6,
                (true, 16, false) => 8,
                (true, 16, _) => 7,
                (true, _, _) => 5,
                (false, 24, _) => 2,
                (false, 16, false) => 4,
                (false, 16, _) => 3,
                _ => 1,
            },
            FileName = Path.GetFileNameWithoutExtension(doc.Title),
            DefaultExt = preferAiff ? ".aiff" : ".wav",
        };
        if (dlg.ShowDialog() != true) return;
        if (!_savesInFlight.Add(doc.SessionId)) return; // a save for this document is already writing
        int depth = dlg.FilterIndex switch { 2 or 6 => 24, 3 or 4 or 7 or 8 => 16, _ => 32 };
        bool dither16 = dlg.FilterIndex is not (4 or 8);
        bool writeAiff = dlg.FilterIndex >= 5;
        int version = doc.EditVersion;
        var snapshot = SnapshotDoc(doc);
        var markers = MarkerSnapshot(d);
        try
        {
            await Task.Run(() => SaveEditableDocument(snapshot, dlg.FileName, depth,
                dither: depth == 16 && dither16, writeAiff: writeAiff, markers: markers));
            _saveFailures.Remove(doc.SessionId);
            doc.FilePath = dlg.FileName;
            doc.RequiresSaveAs = false;
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
            ReportAction(doc.EditVersion == version
                ? $"{doc.Title} saved."
                : $"{doc.Title} save completed · newer edits remain unsaved.");
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

    private async void CloseTab(TabViewModel? tab)
    {
        // The tab's own × button names the tab it sits on; the File menu item and Ctrl+W name
        // nothing and mean "the one in front". Resolve that here, against the active *tab* rather
        // than the active document: _active is null whenever a montage is in front, so falling back
        // to it below would have sent the montage down the document path and closed nothing at all.
        tab ??= _activeTab;

        // A montage has no samples, no autosave and no marker sidecar, so the document close path
        // has nothing to do for it: it is asked about unsaved work and then simply removed.
        if (tab is MontageViewModel montage) { CloseMontageTab(montage); return; }

        var operation = CloseTabAsync(tab as DocumentViewModel ?? _active);
        _tabCloseOperations.Add(operation);
        try { await operation; }
        finally { _tabCloseOperations.Remove(operation); }
    }

    /// <summary>
    /// Close every open tab, asking once rather than once per file.
    /// </summary>
    /// <remarks>
    /// One prompt covering all of them, all or nothing, which is the bargain batch convert already
    /// makes for the same reason: a CD import opens a tab per track, and a dozen separate "close
    /// anyway?" boxes is not a question, it is an obstacle. Files are taken in a snapshot because
    /// closing mutates the collection, and one at a time because each close still has real work to
    /// do — flushing markers, releasing playback, clearing autosave.
    /// </remarks>
    private async void CloseAll()
    {
        var tabs = Documents.ToList();
        if (tabs.Count == 0) return;

        int dirty = tabs.Count(tab => tab.IsDirty);
        if (dirty > 0 && MessageBox.Show(
                dirty == 1
                    ? "One open file has unsaved changes. Close everything anyway?"
                    : $"{dirty} open files have unsaved changes. Close everything anyway?",
                "Close all files", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (TabViewModel tab in tabs)
        {
            if (!Documents.Contains(tab)) continue;
            if (tab is MontageViewModel montage) { CloseMontageTab(montage, prompt: false); continue; }
            if (tab is not DocumentViewModel document) continue;

            var operation = CloseTabAsync(document, prompt: false);
            _tabCloseOperations.Add(operation);
            try { await operation; }
            finally { _tabCloseOperations.Remove(operation); }
        }

        ReportAction(tabs.Count == 1 ? "File closed." : $"{tabs.Count} files closed.");
    }

    private void CloseMontageTab(MontageViewModel montage, bool prompt = true)
    {
        if (prompt && montage.IsDirty && MessageBox.Show(
                $"“{montage.Title}” has unsaved changes. Close it anyway?", "Close montage",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int index = Documents.IndexOf(montage);
        Documents.Remove(montage);
        if (ReferenceEquals(_activeTab, montage))
            ActiveTab = Documents.Count > 0 ? Documents[Math.Clamp(index, 0, Documents.Count - 1)] : null;
    }

    private async Task CloseTabAsync(DocumentViewModel? vm, bool prompt = true)
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
        if (prompt && vm.IsDirty &&
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
        vm.Unhook();
        int idx = Documents.IndexOf(vm);
        Documents.Remove(vm);

        // The neighbour in *tab* order, whatever kind it is — closing a file should land on the tab
        // next to it, not skip past a montage to find another file.
        if (ReferenceEquals(_activeTab, vm))
            ActiveTab = Documents.Count > 0 ? Documents[Math.Clamp(idx, 0, Documents.Count - 1)] : null;
        }
        finally { _tabsClosing.Remove(vm.Doc.SessionId); }
    }

    // ── edit ─────────────────────────────────────────────────────

    private void Undo()
    {
        if (!CanMutateDocument || _active is not { } document) return;
        if (document.Doc.NextUndoName is not { } operation)
        {
            // Nothing left to undo. That is only worth a line when the reason is that steps were
            // released — otherwise the user is simply back at the file as they opened it.
            if (ReleasedHistoryNote(document.Doc) is { } exhausted) ReportAction(exhausted);
            return;
        }
        PrepareForDocumentEdit(document);
        // Undoing can itself evict — it moves an edit onto the redo stack, which is retained too.
        // The change event fires from inside Doc.Undo() and writes a line of its own, so without
        // this the eviction is announced and then overwritten by the line below. Measured: one
        // Undo on a tight budget released two older steps and said nothing about either.
        _suppressEditReport = true;
        try { document.Doc.Undo(); }
        finally { _suppressEditReport = false; }

        // Consumed either way, so a note this line does not use cannot leak onto the next edit's.
        string released = TakeReleasedHistoryNote();
        // Running out supersedes rather than joins: it already names the cumulative total, so
        // saying both would state the same fact twice in one line.
        string note = document.Doc.CanUndo || ReleasedHistoryNote(document.Doc) is not { } ranOut
            ? released
            : $" · {ranOut}";
        ReportAction($"{operation} undone{note}.");
    }

    /// <summary>
    /// What to say about a history the byte budget has shortened, or null when it has not.
    /// </summary>
    /// <remarks>
    /// The Edit History panel has always reported this and plain Ctrl+Z never did, so undo simply
    /// stopped — with the document still carrying the edits whose steps had been released, which
    /// reads as undo failing to undo rather than as a memory limit doing what it was asked.
    /// </remarks>
    private static string? ReleasedHistoryNote(AudioDocument doc)
    {
        int released = doc.DiscardedOlderSteps;
        if (released == 0) return null;
        // The count is cumulative and the limit may have moved since, so the limit is quoted as
        // what it is *now* rather than as the one those steps went under — which is also the only
        // figure the reader can act on.
        long megabytes = AudioDocument.UndoBudgetBytes / (1024 * 1024);
        return $"{released} earlier step(s) have been released to stay inside the undo memory "
             + $"limit, so the file cannot be taken back further. It is {megabytes} MB now — raise "
             + "it in Settings ▸ General.";
    }

    private void Redo()
    {
        if (!CanMutateDocument || _active is not { } document ||
            document.Doc.NextRedoName is not { } operation) return;
        PrepareForDocumentEdit(document);
        _suppressEditReport = true;
        try { document.Doc.Redo(); }
        finally { _suppressEditReport = false; }
        ReportAction($"{operation} reapplied · Undo available{TakeReleasedHistoryNote()}.");
    }

    /// <summary>
    /// Takes a document to any point on its edit history in one action, for the Edit History panel.
    /// </summary>
    /// <remarks>
    /// Playback is released once for the whole run rather than once per step: the jump raises a
    /// single change, so there is a single moment at which the audio under the stream moves.
    /// </remarks>
    public void JumpToHistoryPosition(DocumentViewModel document, int position)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanMoveHistory(document)) return;
        if (position < 0 || position > document.Doc.HistoryCount)
        {
            // The document refuses an out-of-range position rather than clamping it, and that is
            // right there: a silently wrong jump is much harder to notice than a thrown one. Here it
            // is absorbed and reported instead, because the panel is modeless and its indices can go
            // stale under a budget eviction — a stale click is not worth taking the session down.
            ReportAction("That step is no longer in the history.");
            return;
        }

        PrepareForDocumentEdit(document);
        // Same reason as Undo and Redo: the jump raises one change event, which would write a line
        // this method is about to replace — and take any eviction note with it.
        _suppressEditReport = true;
        int moved;
        try { moved = document.Doc.JumpToHistoryPosition(position); }
        finally { _suppressEditReport = false; }
        string released = TakeReleasedHistoryNote();
        if (moved == 0) return;

        string landed = document.Doc.NextUndoName ?? "the opened state";
        int steps = Math.Abs(moved);
        string plural = steps == 1 ? "step" : "steps";
        ReportAction(moved < 0
            ? $"Stepped back {steps} {plural} · now at {landed}{released}."
            : $"Stepped forward {steps} {plural} · now at {landed}{released}.");
    }

    /// <summary>
    /// Discards one step of a document's history and everything after it, permanently.
    /// </summary>
    /// <remarks>
    /// The document raises nothing for a discard — no samples move — so the refresh that an ordinary
    /// edit gets for free has to be asked for here. Redo has just become unavailable.
    /// </remarks>
    public void TruncateHistoryFrom(DocumentViewModel document, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanMoveHistory(document)) return;

        var history = document.Doc.GetHistory();
        if (index < 0 || index >= history.Entries.Count)
        {
            ReportAction("That step is no longer in the history.");
            return;
        }
        string name = history.Entries[index].Name;
        int discarded = history.Entries.Count - index;

        // Discarding a step that is still applied takes it out of the audio first, so this moves
        // samples as surely as an undo does and has to let go of the stream the same way.
        if (document.Doc.HistoryPosition > index) PrepareForDocumentEdit(document);

        if (!document.Doc.TruncateHistoryFrom(index)) return;
        document.NotifyHistoryChanged();
        RefreshEditCommandStates();
        ReportAction(discarded == 1
            ? $"Discarded {name}."
            : $"Discarded {name} and the {discarded - 1} step(s) after it.");
    }

    private async void Copy()
    {
        if (!CanMutateDocument || _active is not { HasSelection: true } d) return;
        if (await CaptureSelectionAsync(d)) ReportAction("Selection copied.");
    }

    private async void Cut()
    {
        if (!CanMutateDocument || _active is not { HasSelection: true } d) return;
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
            long bytes = (long)_clipboard.Length * count * sizeof(float);
            if (bytes > MaximumClipboardBytes)
            {
                _clipboard = null;
                _clipboardRate = 0;
                MessageBox.Show(
                    "That selection is too large to hold on the clipboard. Render or export "
                    + "the range instead, or copy it in smaller pieces.",
                    "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

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
        if (!CanMutateDocument || _active is not { HasSelection: true } d) return;
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
        if (!CanMutateDocument || _active == null || _clipboard == null) return;
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

    /// <summary>
    /// Scales the edit range so its loudest sample reaches <paramref name="ceilingDbfs"/>.
    /// </summary>
    /// <remarks>
    /// The ceiling arrives already chosen and already clamped — see
    /// <see cref="AppSettings.NormalizePeakCeiling"/>. Kept on the range rather than the whole
    /// document because that is what this command has always done: normalizing a selection to its
    /// own peak is a meaningful edit, in a way that measuring a selection's programme loudness is
    /// not.
    /// </remarks>
    /// <returns>False when there was nothing to normalize, so the caller can say so.</returns>
    public bool ApplyPeakNormalize(double ceilingDbfs)
    {
        if (!CanMutateDocument || _active == null) return false;
        var (start, count) = _active.EditRange();
        if (count <= 0) return false;
        // Playback is released before the edit exactly as ApplyToRange does it for every other
        // operation; Normalize may still decline, and releasing for a declined edit is the same
        // cost every other tool on that path already pays.
        PrepareForDocumentEdit(_active);
        return Processing.Normalize(_active.Doc, start, count, ceilingDbfs);
    }

    private void ApplyToRange(Action<AudioDocument, int, int> op)
    {
        if (!CanMutateDocument || _active == null) return;
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

    private void ZoomActiveDocument(double factor)
    {
        WithDoc(document =>
        {
            if (IsPlaybackActiveForDocument(document, _playbackDocument, IsPlaying))
                document.ZoomBy(factor, document.PlayheadSample);
            else
                document.ZoomBy(factor);
        });
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
            OpenRecordDialog();
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

    /// <summary>
    /// Opens the recording setup dialog. The dialog builds its own recording
    /// engine, so the output stream has to be released first: capturing while
    /// WASAPI is still streaming records the playback monitor path when
    /// software playthrough is on.
    /// </summary>
    private void OpenRecordDialog()
    {
        if (IsTransportRecording || IsFinalizingRecording || HasPendingTransportRecording) return;
        if (Engine.IsPlaying || Engine.IsPaused) ReleasePlayback();
        RequestRecordDialog?.Invoke();
    }

    private void StopTransport()
    {
        if (IsTransportRecording || HasPendingTransportRecording) _ = FinishTransportRecordingAsync();
        else if (Engine.IsPlaying || Engine.IsPaused) ReleasePlayback();
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
                MessageBox.Show("The recording reached Deep Groove's in-memory safety limit. Audio captured up to the limit was kept.",
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
            StopCommand.RaiseCanExecuteChanged();
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
        // ReleasePlayback restores (and clears) a preview's rack bypass, so the
        // override has to be captured here and re-established for the restart —
        // otherwise an A/B "dry" audition comes back wet and stopping it no
        // longer returns the rack to the user's setting.
        bool? previewRackOverride = _previewRackRestoreState;
        ReleasePlayback(updatePosition: false);
        if (preview != null && previewRackOverride.HasValue)
        {
            _previewRackRestoreState = previewRackOverride;
            Engine.Master.RackEnabled = false;
        }

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
        // A paused stream already has IsPlaying == false, so the property setter
        // cannot notify StopCommand when Engine.Stop clears Engine.IsPaused.
        StopCommand.RaiseCanExecuteChanged();
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
            // Recorded before the staleness guard below. The session id is what pairs this
            // stop with the device-failure notification queued immediately behind it, and a
            // session that has already been superseded still has to be able to say why it
            // ended — otherwise pressing Play again before the dispatcher drained swallowed
            // the "the audio device failed" message entirely.
            _stoppedPlaybackSession = playbackSession;

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
            IsPlaying = false;
            RestorePreviewRackOverride();
        });
    }

    private void OnPlaybackFailed(long playbackSession, AudioDocument sourceDocument, Exception error)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_stoppedPlaybackSession != playbackSession) return;
            _stoppedPlaybackSession = 0;
            MessageBox.Show($"Playback stopped because the audio device failed:\n{error.Message}",
                "Playback", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OnActiveDocumentEdited(int start, int removed, int inserted)
    {
        if (!_suppressEditReport && _active?.Doc.NextUndoName is { } operation)
            ReportAction($"{operation} applied · Undo available{TakeReleasedHistoryNote()}.");
        Raise(nameof(HasAudioDocument));
        Raise(nameof(ShowsSpectralBar));
        RaiseSpectralSelectionState();
        Raise(nameof(HasMultichannelDocument));
        Raise(nameof(HasMonoDocument));
        Raise(nameof(CanAnalyzeCleanup));
        RefreshEditCommandStates();
    }

    /// <summary>
    /// Notes an eviction so the edit that caused it can report it, rather than reporting here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget is enforced from inside <c>ReplaceRange</c>, <em>before</em> the change event the
    /// status line is written from — so a line written here is overwritten by "… applied · Undo
    /// available" a moment later, and the one thing the user needed to know is the one thing that
    /// does not survive. It is held and appended instead.
    /// </para>
    /// <para>
    /// Subscribed for the active document only, matching <see cref="OnActiveDocumentEdited"/>. A
    /// tool working on another tab — Match Loudness across several — can therefore evict without a
    /// line; the Edit History panel still says what each document released.
    /// </para>
    /// </remarks>
    private void OnActiveDocumentHistoryReleased(int older, int newer) => _releasedSteps += older;

    /// <summary>Reads the pending eviction note and clears it, empty when there was none.</summary>
    private string TakeReleasedHistoryNote()
    {
        if (_releasedSteps == 0) return "";
        int released = _releasedSteps;
        _releasedSteps = 0;
        long megabytes = AudioDocument.UndoBudgetBytes / (1024 * 1024);
        return $" · undo history full, {released} of the oldest step(s) released to stay inside the "
             + $"{megabytes} MB limit (Settings ▸ General)";
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.MarkersVersion))
            ReportAction("Markers or regions updated.");
        // SelStart/SelEnd never move without DocumentViewModel.RaiseSelection
        // re-raising HasSelection, so listening for the extra two only meant
        // re-querying all 33 commands three times per mouse-move.
        if (e.PropertyName is nameof(DocumentViewModel.HasSelection)
            or nameof(DocumentViewModel.MarkersVersion))
            RefreshEditCommandStates();
        // The spectral actions read the time selection when nothing is drawn on the spectrogram, so
        // they follow it. HasSelection is re-raised whenever either edge moves, which is what makes
        // this enough — see DocumentViewModel.RaiseSelection.
        if (e.PropertyName == nameof(DocumentViewModel.HasSelection)) RaiseSpectralSelectionState();
    }

    /// <summary>
    /// Whether the history may be moved right now. Both flags matter: the clipboard one because a
    /// copy is reading the samples, and the operation one because a tool has already taken its
    /// snapshot and will commit against a length check that a same-length jump would slip past.
    /// </summary>
    public bool CanMoveHistory(DocumentViewModel? document) =>
        document != null
        && !_editOperationRunning
        && !_documentOperationRunning
        && Documents.Contains(document);

    private void RefreshEditCommandStates()
    {
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        HistoryCommand.RaiseCanExecuteChanged();
        MatchLoudnessCommand.RaiseCanExecuteChanged();
        Raise(nameof(UndoMenuHeader));
        Raise(nameof(RedoMenuHeader));
        CutCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        PasteCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        TrimCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        SaveAsCommand.RaiseCanExecuteChanged();
        CloseTabCommand.RaiseCanExecuteChanged();
        CloseAllCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
        GoToStartCommand.RaiseCanExecuteChanged();
        ZoomInCommand.RaiseCanExecuteChanged();
        ZoomOutCommand.RaiseCanExecuteChanged();
        ZoomFitCommand.RaiseCanExecuteChanged();
        ZoomSelectionCommand.RaiseCanExecuteChanged();
        GainUpCommand.RaiseCanExecuteChanged();
        GainDownCommand.RaiseCanExecuteChanged();
        NormalizeCommand.RaiseCanExecuteChanged();
        NormalizeLoudnessCommand.RaiseCanExecuteChanged();
        FadeInCommand.RaiseCanExecuteChanged();
        FadeOutCommand.RaiseCanExecuteChanged();
        ReverseCommand.RaiseCanExecuteChanged();
        RemoveDcCommand.RaiseCanExecuteChanged();
        InsertSilenceCommand.RaiseCanExecuteChanged();
        AddMarkerCommand.RaiseCanExecuteChanged();
        AddRegionCommand.RaiseCanExecuteChanged();
        PrevMarkerCommand.RaiseCanExecuteChanged();
        NextMarkerCommand.RaiseCanExecuteChanged();
        ClearMarkersCommand.RaiseCanExecuteChanged();
        SmoothEditCommand.RaiseCanExecuteChanged();
        RenderCommand.RaiseCanExecuteChanged();
        ApplyChainCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        StatisticsCommand.RaiseCanExecuteChanged();
    }

    private async void RenderMaster()
    {
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        var doc = d.Doc;
        // capture stable channel refs on the UI thread — splices never mutate old arrays
        var input = doc.Channels.ToArray();
        int sr = doc.SampleRate;

        await RunBlocking("Rendering master chain", "Writing to a new tab · source unchanged",
            async (progress, token) =>
            {
                var output = await Task.Run(
                    () => Engine.Master.ProcessOffline(input, sr, token, progress), token);
                AddGeneratedDocument(new AudioDocument(output, sr, sourceBitDepth: 32)
                {
                    Title = Path.GetFileNameWithoutExtension(doc.Title) + " (rendered copy).wav",
                }, "Effects rack rendered to a new tab · source audio unchanged.");
            });
    }

    /// <summary>Render the selection (or whole file) as one undoable document edit.</summary>
    private async void ApplyChain()
    {
        if (!CanMutateAudio || _active == null || _active.Doc.Length == 0) return;
        var d = _active;
        var (start, count) = d.EditRange();
        if (count <= 0) return;
        var channels = d.Doc.Channels.ToArray();
        int sr = d.Doc.SampleRate;
        int sourceVersion = d.Doc.EditVersion;

        SetDocumentOperationRunning(true);
        try
        {
            await RunBlocking("Applying effect chain", "Rendering the selection as one undoable edit",
                async (progress, token) =>
            {
                var output = await Task.Run(() =>
                {
                    var input = channels.Select(ch => ch.AsSpan(start, count).ToArray()).ToArray();
                    return Engine.Master.ProcessOffline(input, sr, token, progress);
                }, token);
                if (!Documents.Contains(d) || d.Doc.EditVersion != sourceVersion)
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
        finally { SetDocumentOperationRunning(false); }
    }

    /// <summary>
    /// Runs a long operation behind the progress overlay. Work that cannot report progress still
    /// gets an indeterminate one, which is the point: the window used to simply freeze.
    /// </summary>
    private Task RunBlocking(Func<Task> work) =>
        RunBlocking("Working", null, (_, _) => work());

    /// <summary>
    /// Runs a long operation behind the progress overlay, reporting progress and honouring cancel.
    /// </summary>
    /// <remarks>
    /// The window is deliberately no longer disabled outright: <c>IsEnabled = false</c> would take
    /// the overlay's own Cancel button with it. The overlay covers everything below the title bar
    /// instead, so nothing underneath can be clicked. The wait cursor stays for the first fraction of
    /// a second, before the overlay is due to appear.
    /// </remarks>
    private async Task RunBlocking(string title, string? detail,
        Func<IProgress<double>, CancellationToken, Task> work)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Progress.RunBlockingAsync(title, detail, work);
        }
        catch (OperationCanceledException)
        {
            ReportAction($"{title} cancelled.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Deep Groove", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
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
        var dirty = AudioDocuments.Where(d => d.IsDirty && d.Doc.Length > 0
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
                        var document = AudioDocuments.FirstOrDefault(d => d.Doc.SessionId == id);
                        if (document != null && document.Doc.EditVersion == version)
                            _autosavedVersions[id] = version;
                    }
                });
        });
        Raise(nameof(StatusAutosave));
    }

    /// <summary>Called once from the window after load: crash recovery, session restore, command-line files.</summary>
    public async Task StartupLoadAsync(string[] args,
        CancellationToken cancellationToken = default)
    {
      try
      {
        var recoverable = AutosaveService.GetRecoverable();
        if (recoverable.Count > 0)
        {
            var names = string.Join("\n", recoverable.Select(r => "  • " + r.Title));
            if (MessageBox.Show(
                    $"Deep Groove didn't shut down cleanly last time. Recover unsaved work?\n\n{names}",
                    "Crash recovery", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var recoveredKeys = new List<string>();
                var recoveredDocuments = new List<AudioDocument>();
                var failures = new List<string>();
                foreach (var entry in recoverable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var (doc, peaks) = await Task.Run(() =>
                        {
                            var loaded = WavCodec.Load(entry.AutosaveFile, cancellationToken);
                            AutosaveService.RestoreFormatMetadata(loaded, entry);
                            var store = new PeakStore();
                            store.Rebuild(loaded, cancellationToken: cancellationToken);
                            return (loaded, store);
                        }, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
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
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failures.Add($"{entry.Title}: {ex.Message}"); }
                }
                if (recoveredDocuments.Count > 0)
                {
                    var replacementSnapshots = recoveredDocuments
                        .Select(doc => (SnapshotDoc(doc), doc.SessionId)).ToList();
                    int published = await Task.Run(
                        () => AutosaveService.RunNow(replacementSnapshots, cancellationToken),
                        cancellationToken);
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
            await OpenFilesAsync(args, cancellationToken: cancellationToken);
        }
        else if (AppSettings.Instance.ReopenLastSession && !AudioDocuments.Any())
        {
            var files = AppSettings.Instance.LastSessionFiles.Where(File.Exists).ToList();
            if (files.Count > 0)
                await OpenFilesAsync(files, cancellationToken: cancellationToken);
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
            var markerTasks = AudioDocuments.Select(d => d.FlushMarkersAsync()).ToArray();
            if (markerTasks.Length > 0) await Task.WhenAll(markerTasks);
            if (_saveFailures.Count > 0)
                throw new IOException("One or more file saves failed: " + string.Join("; ", _saveFailures.Values));

            AppSettings.Instance.LastSessionFiles =
                AudioDocuments.Where(d => d.Doc.FilePath != null).Select(d => d.Doc.FilePath!).ToList();
            if (!AppSettings.Instance.Save())
                throw new IOException("Settings could not be saved: " + AppSettings.Instance.LastSaveError);
            if (!AutosaveService.ClearAll())
                throw new IOException("Autosave recovery files could not be cleared.");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            failure = DisposeOwnedResources(failure);
        }
        if (failure != null) throw failure;
    }

    /// <summary>
    /// Releases timers, event subscriptions, audio engines, and process handles
    /// without persisting settings or deleting recovery data. Normal application
    /// shutdown should use <see cref="OnCleanExitAsync"/>; tests and abandoned
    /// view models can use this method directly.
    /// </summary>
    public void Dispose()
    {
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }

    private Exception? DisposeOwnedResources(Exception? failure = null)
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return failure;

        _shuttingDown = true;

        // The clipboard is static and survives this object; releasing it here is the only
        // point at which we know nothing is going to paste it.
        _clipboard = null;
        _clipboardRate = 0;

        CompositionTarget.Rendering -= OnRendering;
        _autosaveTimer.Stop();
        _timer.Stop();
        Master.ProcessingTopologyChanged -= RestartMonoPlaybackForTopologyChange;
        Master.StatusChanged -= ReportAction;
        Engine.PlaybackStopped -= OnPlaybackStopped;
        Engine.PlaybackFailed -= OnPlaybackFailed;
        _transportRecorder.CaptureStopped -= OnTransportCaptureStopped;
        Interlocked.Exchange(ref _expectedTransportRecordingSessionId, 0);
        try { _transportRecorder.Dispose(); } catch (Exception ex) { failure ??= ex; }
        try { Engine.Dispose(); } catch (Exception ex) { failure ??= ex; }
        try { _process.Dispose(); } catch (Exception ex) { failure ??= ex; }
        return failure;
    }

    private void OnTick()
    {
        // Nothing feeding the master and the meters already settled: ticking
        // would only re-format readouts that cannot have changed.
        if (IsPlaying || IsTransportRecording || Master.NeedsDecay)
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

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsPlaying || e is not RenderingEventArgs rendering
            || rendering.RenderingTime == _lastPlaybackRenderTime)
            return;
        _lastPlaybackRenderTime = rendering.RenderingTime;

        var playbackDocument = _playbackDocument;
        if (playbackDocument == null || !Documents.Contains(playbackDocument)) return;
        playbackDocument.PlayheadSample = Math.Clamp(
            Engine.PositionSamples, 0, playbackDocument.Doc.Length);
        playbackDocument.EnsurePlayheadVisible();
    }
}
