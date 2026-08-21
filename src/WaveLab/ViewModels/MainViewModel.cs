using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WaveLab.Audio;
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

        foreach (var f in AppSettings.Instance.RecentFiles) RecentFiles.Add(f);
        UpdateRecordInputName();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        CompositionTarget.Rendering += OnRendering;

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += (_, _) => AutosaveTick();
        _autosaveTimer.Start();

        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(Save, () => _active != null);
        SaveAsCommand = new RelayCommand(SaveAs, () => _active != null);
        CloseTabCommand = new RelayCommand<TabViewModel>(CloseTab,
            tab => tab != null ? Documents.Contains(tab) : _activeTab != null);
        CloseAllCommand = new RelayCommand(CloseAll, () => Documents.Count > 0);
        ExitCommand = new RelayCommand(() => Application.Current.MainWindow?.Close());

        UndoCommand = new RelayCommand(Undo, () => _active?.Doc.CanUndo == true);
        RedoCommand = new RelayCommand(Redo, () => _active?.Doc.CanRedo == true);
        CutCommand = new RelayCommand(Cut, () => !_editOperationRunning && _active?.HasSelection == true);
        CopyCommand = new RelayCommand(Copy, () => !_editOperationRunning && _active?.HasSelection == true);
        PasteCommand = new RelayCommand(Paste, () => !_editOperationRunning && _active != null && _clipboard != null);
        DeleteCommand = new RelayCommand(DeleteSelection, () => !_editOperationRunning && _active?.HasSelection == true);
        TrimCommand = new RelayCommand(Trim, () => !_editOperationRunning && _active?.HasSelection == true);
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
            () => HasAudioDocument);
        GainDownCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.Gain(d, s, c, -3)),
            () => HasAudioDocument);
        NormalizeCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.Normalize(d, s, c, -0.3)),
            () => HasAudioDocument);
        FadeInCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.FadeIn(d, s, c)), () => HasAudioDocument);
        FadeOutCommand = new RelayCommand(() => ApplyToRange((d, s, c) => Processing.FadeOut(d, s, c)), () => HasAudioDocument);
        ReverseCommand = new RelayCommand(() => ApplyToRange(Processing.Reverse), () => HasAudioDocument);
        RemoveDcCommand = new RelayCommand(() => ApplyToRange(Processing.RemoveDcOffset), () => HasAudioDocument);
        InsertSilenceCommand = new RelayCommand(() => WithDoc(d =>
        {
            PrepareForDocumentEdit(d);
            Processing.InsertSilence(d.Doc, d.Cursor, 1.0);
        }), () => HasDocument);

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
        SmoothEditCommand = new RelayCommand(SmoothEditPoints, () => HasAudioDocument);

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
        ApplyChainCommand = new RelayCommand(ApplyChain, () => HasAudioDocument);
        RecordCommand = new RelayCommand(ToggleRecord, () => !IsFinalizingRecording);
        RecordSetupCommand = new RelayCommand(OpenRecordDialog,
            () => !IsTransportRecording && !IsFinalizingRecording && !HasPendingTransportRecording);
        SettingsCommand = new RelayCommand(() => RequestSettingsDialog?.Invoke());
        ExportCommand = new RelayCommand(() => RequestExportDialog?.Invoke(), () => HasAudioDocument);
        StatisticsCommand = new RelayCommand(() => RequestStatisticsDialog?.Invoke(), () => HasAudioDocument);
        OpenRecentCommand = new RelayCommand<string>(path => { if (path != null) OpenFiles([path]); });
        CommandPaletteCommand = new RelayCommand(() => RequestCommandPalette?.Invoke());
        AboutCommand = new RelayCommand(() => MessageBox.Show(
            "Deep Groove 2.0\n\nAudio editor and mastering suite.\nWAV/AIFF · MP3/FLAC/AAC import & export\nEffects rack · restoration · EBU R128 metering\nWASAPI playback and recording",
            "About Deep Groove", MessageBoxButton.OK, MessageBoxImage.Information));
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
            }
            if (_active != null)
            {
                _active.PropertyChanged += OnActiveDocumentPropertyChanged;
                _active.Doc.Changed += OnActiveDocumentEdited;
            }
            Raise(nameof(HasDocument));
            Raise(nameof(HasAudioDocument));
            Raise(nameof(HasMultichannelDocument));
            Raise(nameof(HasMonoDocument));
            Raise(nameof(CanAnalyzeCleanup));
            Raise(nameof(WindowTitle));
            Raise(nameof(StatusSamples));
            Raise(nameof(IsActiveDocumentPlaying));
            // A time-frequency region belongs to the file it was drawn on; carrying it to another
            // tab would offer a repair at a position that means nothing there.
            SpectralSelection = SpectralSelection.None;
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
            EditorViewChanged?.Invoke();
        }
    }

    public bool IsWaveformView => _editorView == EditorViewMode.Waveform;
    public bool IsSplitView => _editorView == EditorViewMode.Split;
    public bool IsSpectrogramView => _editorView == EditorViewMode.Spectrogram;

    /// <summary>
    /// Whether the spectrogram is on screen at all, and so whether the spectral repair controls
    /// belong in the toolbar. Bound directly rather than through an inverting converter, so the
    /// rule is one testable property instead of markup.
    /// </summary>
    public bool ShowsSpectrogram => _editorView != EditorViewMode.Waveform;

    /// <summary>Raised so the window can lay the editor rows out for the new mode.</summary>
    public event Action? EditorViewChanged;

    public RelayCommand ShowWaveformCommand { get; }
    public RelayCommand ShowSplitCommand { get; }
    public RelayCommand ShowSpectrogramCommand { get; }

    // ── spectral selection ───────────────────────────────────────

    private SpectralSelection _spectralSelection = SpectralSelection.None;
    private SpectralTool _spectralTool = SpectralTool.Rectangle;
    private SpectralFrequencyScale _spectralScale = SpectralFrequencyScale.Logarithmic;
    private int _spectralBinsPerOctave = 36;

    /// <summary>
    /// Which analysis and axis the spectral picture is made with. Design:
    /// <c>docs/design/constant_q.png</c>.
    /// </summary>
    public SpectralFrequencyScale SpectralScale
    {
        get => _spectralScale;
        set
        {
            if (!Set(ref _spectralScale, value)) return;
            Raise(nameof(IsLinearScale));
            Raise(nameof(IsLogarithmicScale));
            Raise(nameof(IsConstantQScale));
            Raise(nameof(ShowsBinsPerOctave));
        }
    }

    public bool IsLinearScale => _spectralScale == SpectralFrequencyScale.Linear;
    public bool IsLogarithmicScale => _spectralScale == SpectralFrequencyScale.Logarithmic;
    public bool IsConstantQScale => _spectralScale == SpectralFrequencyScale.ConstantQ;

    /// <summary>
    /// Shown only for constant-Q, because it is the only scale for which the phrase means anything:
    /// the other two draw an analysis whose bins are evenly spaced in hertz.
    /// </summary>
    public bool ShowsBinsPerOctave => _spectralScale == SpectralFrequencyScale.ConstantQ;

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
            Raise(nameof(HasSpectralSelection));
            Raise(nameof(NeedsSpectralSelection));
            Raise(nameof(SpectralSpanText));
            Raise(nameof(SpectralBandText));
        }
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
    public string SpectralToolHint => _spectralTool switch
    {
        SpectralTool.Lasso => "Draw around the defect",
        SpectralTool.MagicWand => "Click the defect to grow a region through it",
        SpectralTool.Harmonic => "Drag from the fundamental to take it and its partials",
        _ => "Drag a region on the spectrogram",
    };

    public bool HasSpectralSelection => !_spectralSelection.IsEmpty;

    /// <summary>Whether to prompt for a selection instead of showing one. The toolbar binds both.</summary>
    public bool NeedsSpectralSelection => _spectralSelection.IsEmpty;

    /// <summary>The selected time span, as the toolbar shows it.</summary>
    public string SpectralSpanText
    {
        get
        {
            if (_spectralSelection.IsEmpty) return "—";
            SpectralRegion bounds = _spectralSelection.Bounds;
            int rate = _spectralSelection.SampleRate;
            return $"{TimeFormat.Position(bounds.StartSample, rate)} → " +
                   $"{TimeFormat.Position(bounds.EndSample, rate)}";
        }
    }

    /// <summary>The selected frequency band, as the toolbar shows it.</summary>
    public string SpectralBandText
    {
        get
        {
            if (_spectralSelection.IsEmpty) return "—";
            SpectralRegion bounds = _spectralSelection.Bounds;
            return $"{Hertz(bounds.LowFrequency)} → {Hertz(bounds.HighFrequency)}";
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

    private async Task OpenFilesAsync(IEnumerable<string> paths, OpenBitDepth? openAs = null)
    {
        if (_shuttingDown) return;
        foreach (var path in paths.ToList())
        {
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
                        // decode AND build the peak pyramid off the UI thread — the tab appears fully drawn
                        var (doc, peaks) = await Task.Run(() =>
                        {
                            var loaded = openAs.HasValue
                                ? AudioImporter.LoadAs(path, openAs.Value, token)
                                : AudioImporter.Load(path, token);
                            var store = new PeakStore();
                            store.Rebuild(loaded);
                            return (loaded, store);
                        }, token);
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
        foreach (var f in AppSettings.Instance.RecentFiles) RecentFiles.Add(f);
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

    public void AddDocument(AudioDocument doc, PeakStore? prebuiltPeaks = null)
    {
        var vm = new DocumentViewModel(doc, prebuiltPeaks);
        Documents.Add(vm);
        ActiveTab = vm;
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

    public void AddGeneratedDocument(AudioDocument doc, string? completedAction = null)
    {
        doc.MarkUnsaved();
        AddDocument(doc);
        ReportAction(completedAction ?? (doc.CaptureNote is { } note
            ? $"{doc.Title} created in a new tab. {note}"
            : $"{doc.Title} created in a new tab."));
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
        if (_active is not { } document || document.Doc.NextUndoName is not { } operation) return;
        PrepareForDocumentEdit(document);
        document.Doc.Undo();
        ReportAction($"{operation} undone.");
    }

    private void Redo()
    {
        if (_active is not { } document || document.Doc.NextRedoName is not { } operation) return;
        PrepareForDocumentEdit(document);
        document.Doc.Redo();
        ReportAction($"{operation} reapplied · Undo available.");
    }

    private async void Copy()
    {
        if (_active is not { HasSelection: true } d) return;
        if (await CaptureSelectionAsync(d)) ReportAction("Selection copied.");
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
            // The session id is unique per Play(), so it is on its own enough to
            // correlate a following device-failure event. Holding the document
            // as well would root its whole sample buffer until the next stop.
            _stoppedPlaybackSession = playbackSession;
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
        if (_active?.Doc.NextUndoName is { } operation)
            ReportAction($"{operation} applied · Undo available.");
        Raise(nameof(HasAudioDocument));
        Raise(nameof(HasMultichannelDocument));
        Raise(nameof(HasMonoDocument));
        Raise(nameof(CanAnalyzeCleanup));
        RefreshEditCommandStates();
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
        if (_active == null || _active.Doc.Length == 0) return;
        var d = _active;
        var (start, count) = d.EditRange();
        if (count <= 0) return;
        var channels = d.Doc.Channels.ToArray();
        int sr = d.Doc.SampleRate;
        int sourceVersion = d.Doc.EditVersion;

        await RunBlocking("Applying effect chain", "Rendering the selection as one undoable edit",
            async (progress, token) =>
        {
            var output = await Task.Run(() =>
            {
                var input = channels.Select(ch => ch.AsSpan(start, count).ToArray()).ToArray();
                return Engine.Master.ProcessOffline(input, sr, token, progress);
            }, token);
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
    public async Task StartupLoadAsync(string[] args)
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
                    try
                    {
                        var (doc, peaks) = await Task.Run(() =>
                        {
                            var loaded = WavCodec.Load(entry.AutosaveFile);
                            AutosaveService.RestoreFormatMetadata(loaded, entry);
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
        else if (AppSettings.Instance.ReopenLastSession && !AudioDocuments.Any())
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
