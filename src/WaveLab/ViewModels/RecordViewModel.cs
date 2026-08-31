using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;

namespace WaveLab.ViewModels;

public sealed record CaptureDevice(string Id, string Name);
public sealed record RecordingBitDepthChoice(int Bits, string Label, string ToolbarLabel);

public sealed class RecordViewModel : ObservableObject, IDisposable
{
    private const int LevelHistoryCapacity = 900; // about 30 s at the 33 ms tick
    private const double DcWarningThresholdDb = -50;

    /// <summary>Comfortably longer than any LP side, so the cap only fires when something went wrong.</summary>
    public const int DefaultAutoStopMinutes = 30;
    public const int MinimumAutoStopMinutes = 1;
    public const int MaximumAutoStopMinutes = 180;

    /// <summary>
    /// The countdown is hidden until the hold is well under way, so an ordinary
    /// inter-track gap does not flash a stop warning at the user.
    /// </summary>
    private const double CountdownVisibleAfterSeconds = 5;

    /// <summary>Floor on how often a settling check may rewrite settings.json.</summary>
    private static readonly TimeSpan CalibrationWriteInterval = TimeSpan.FromSeconds(5);

    private readonly RecordingEngine _engine = new();
    private RecordingLevelSnapshot _levelSnapshot;
    private CaptureDevice? _selectedDevice;
    private bool _isRecording;
    private bool _isLevelChecking;
    private bool _isWaitingForNeedleDrop;
    private bool _isFinalizing;
    private bool _levelCheckStopped;
    private double _peakL = -60, _peakR = -60;
    private double _rmsL = -60, _rmsR = -60;
    private double _holdL = -60, _holdR = -60;
    private double _inputLevelDb;
    private double _inputLevelMinimumDb = -96;
    private double _inputLevelMaximumDb;
    private double _inputLevelStepDb = 0.5;
    private double _inputFineTrimDb;
    private double _targetCeilingDb;
    // What the analyzer and settings file actually hold, so a drag that ends where it
    // started costs nothing.
    private double _committedTargetCeilingDb;
    private double _heldRecommendedTotalDb = double.NaN;
    private double _heldProgramPeakDb = double.NaN;
    private bool _hasInputLevelControl;
    private bool _inputLevelMuted;
    private string? _inputLevelError;
    private bool _refreshingInputLevel;
    private bool _softwarePlaythroughEnabled;
    private string? _softwarePlaythroughError;
    private string _softwarePlaythroughOutputName = "selected output";
    private Task _finalization = Task.CompletedTask;
    private long _expectedRecordingSessionId;
    private DateTime _calibrationSavedUtc = DateTime.MinValue;
    private double _calibrationSavedTotalDb = double.NaN;
    private bool _applyingRecommendation;
    private bool _recommendationApplied;
    private string _applyRecommendationStatusText = "";
    private bool _sampleWholeRecord;
    private bool _stoppedLevelCheckWasWholeRecord;
    private string? _completedLevelCheckNote;
    private bool _autoStopOnRunOut;
    private double _runOutHoldSeconds = RunOutDetector.DefaultHoldSeconds;
    private bool _autoStopOnDuration;
    private int _autoStopMinutes = DefaultAutoStopMinutes;
    private int _recordingBitDepth;
    private bool _punchInsertEnabled;
    private string _autoStopStatusText = "";
    // written on the UI thread in Dispose, read on NAudio's capture/stop
    // callback threads — must not be cached in a register
    private volatile bool _disposed;

    public RecordViewModel()
    {
        _levelSnapshot = _engine.LevelSnapshot;
        try
        {
            foreach (var (id, name) in RecordingEngine.GetCaptureDevices())
                Devices.Add(new CaptureDevice(id, name));
        }
        catch { }

        _engine.LevelTargetCeilingDb = AppSettings.Instance.RecordingTargetCeilingDb;
        // Read back rather than trusting the setting: the analyzer clamps it.
        _targetCeilingDb = _committedTargetCeilingDb = _engine.LevelTargetCeilingDb;
        _recordingBitDepth = NormalizeRecordingBitDepth(AppSettings.Instance.RecordingBitDepth);

        string? preferred = AppSettings.Instance.InputDeviceId;
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == preferred)
            ?? (Devices.Count > 0 ? Devices[0] : null);
        RefreshInputLevel();
        _softwarePlaythroughOutputName = PlaybackEngine.CurrentOutputName();

        _engine.CaptureStopped += info =>
        {
            if (_disposed || !_engine.IsCurrentSession(info.SessionId)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                if (_disposed
                    || info.SessionId != Interlocked.Read(ref _expectedRecordingSessionId)
                    || !_engine.IsCurrentSession(info.SessionId)) return;

                if (IsLevelChecking || IsWaitingForNeedleDrop)
                {
                    bool wasWaitingForNeedleDrop = IsWaitingForNeedleDrop;
                    IsLevelChecking = false;
                    IsWaitingForNeedleDrop = false;
                    Interlocked.CompareExchange(ref _expectedRecordingSessionId, 0, info.SessionId);
                    _engine.Stop();
                    RaiseLevelProperties();
                    if (wasWaitingForNeedleDrop)
                        NeedleDropMonitoringStopped?.Invoke(info);
                    else
                        MonitoringStopped?.Invoke(info);
                    return;
                }

                if (!IsRecording) return;
                Exception? failure = null;
                try { await StopAndFinishSessionAsync(info.SessionId); }
                catch (Exception ex) { failure = ex; }
                UnexpectedStopCompleted?.Invoke(info, failure);
            });
        };
        _engine.NeedleDropTriggered += sessionId =>
        {
            if (_disposed
                || sessionId != Interlocked.Read(ref _expectedRecordingSessionId)
                || !_engine.IsCurrentSession(sessionId)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed
                    || !IsWaitingForNeedleDrop
                    || sessionId != Interlocked.Read(ref _expectedRecordingSessionId)
                    || !_engine.IsCurrentSession(sessionId)) return;
                IsWaitingForNeedleDrop = false;
                IsRecording = true;
                NeedleDropStarted?.Invoke();
            });
        };
        _engine.AutoStopTriggered += info =>
        {
            if (_disposed
                || info.SessionId != Interlocked.Read(ref _expectedRecordingSessionId)
                || !_engine.IsCurrentSession(info.SessionId)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                if (_disposed
                    || !IsRecording
                    || IsFinalizing
                    || info.SessionId != Interlocked.Read(ref _expectedRecordingSessionId)
                    || !_engine.IsCurrentSession(info.SessionId)) return;

                Exception? failure = null;
                // Finish through the ordinary stop path so the take is built,
                // trimmed and handed over exactly as a manual stop would be.
                try { await StopAndFinishSessionAsync(info.SessionId); }
                catch (Exception ex) { failure = ex; }
                if (failure == null) NoteAutoStopReason(info.Reason);
                AutoStopped?.Invoke(info, failure);
            });
        };
    }

    /// <summary>
    /// Records why the take ended in the document's capture note. The dialog
    /// closes on an auto-stop, so this is where the reason survives; a banner
    /// would vanish with the window.
    /// </summary>
    private void NoteAutoStopReason(AutoStopReason reason)
    {
        string note = reason switch
        {
            AutoStopReason.RunOut =>
                $"Stopped automatically at the run-out groove; {RunOutDetector.KeepAfterProgramSeconds:0.#} s "
                + "was kept after the last programme content and the rest of the run-out discarded.",
            AutoStopReason.DurationLimit =>
                $"Stopped automatically at the {_autoStopMinutes} minute take limit; all captured audio was kept.",
            _ => "",
        };
        if (Result == null || note.Length == 0) return;
        Result.CaptureNote = string.IsNullOrWhiteSpace(Result.CaptureNote)
            ? note
            : Result.CaptureNote + " " + note;
    }

    public ObservableCollection<CaptureDevice> Devices { get; } = [];

    internal static IReadOnlyList<RecordingBitDepthChoice> AvailableRecordingBitDepthChoices { get; } =
    [
        new(16, "16-bit PCM", "16 BIT"),
        new(24, "24-bit PCM", "24 BIT"),
        new(32, "32-bit float", "32 FLOAT"),
    ];

    public IReadOnlyList<RecordingBitDepthChoice> RecordingBitDepthChoices =>
        AvailableRecordingBitDepthChoices;

    /// <summary>
    /// Output depth assigned to the completed take. The WASAPI stream remains the device's
    /// 32-bit-float mix format so selecting a file depth never changes hardware compatibility.
    /// </summary>
    public int RecordingBitDepth
    {
        get => _recordingBitDepth;
        set
        {
            int normalized = NormalizeRecordingBitDepth(value);
            int previous = _recordingBitDepth;
            if (!Set(ref _recordingBitDepth, normalized)) return;
            Raise(nameof(FormatText));
            if (TryPersistRecordingBitDepth(normalized)) return;

            // Save reports failure rather than throwing. Put both live copies back so a later,
            // unrelated settings write cannot unexpectedly persist a choice this write rejected.
            Set(ref _recordingBitDepth, previous, nameof(RecordingBitDepth));
            Raise(nameof(FormatText));
            RecordingBitDepthSaveFailed?.Invoke(
                AppSettings.Instance.LastSaveError ?? "The settings file could not be written.");
        }
    }

    /// <summary>
    /// A punch becomes part of an existing document, whose output depth remains authoritative.
    /// This also drives the format explanation while the otherwise irrelevant selector is disabled.
    /// </summary>
    public bool PunchInsertEnabled
    {
        get => _punchInsertEnabled;
        set
        {
            if (!Set(ref _punchInsertEnabled, value)) return;
            Raise(nameof(FormatText));
        }
    }

    internal static int NormalizeRecordingBitDepth(int value) => value is 16 or 24 or 32 ? value : 24;

    internal static bool TryPersistRecordingBitDepth(int value)
    {
        AppSettings settings = AppSettings.Instance;
        int previous = settings.RecordingBitDepth;
        settings.RecordingBitDepth = NormalizeRecordingBitDepth(value);
        if (settings.Save()) return true;
        settings.RecordingBitDepth = previous;
        return false;
    }

    internal static string DescribeRecordingBitDepth(int value) => NormalizeRecordingBitDepth(value) == 32
        ? "32-bit float"
        : $"{NormalizeRecordingBitDepth(value)}-bit PCM";

    internal static void ApplyRecordingBitDepth(AudioDocument document, int bitDepth)
    {
        int normalized = NormalizeRecordingBitDepth(bitDepth);
        document.SourceBitDepth = normalized;
        // The editor works in float. Defer the one stochastic reduction to the WAV writer so a
        // 16-bit take is not dithered once here and then quantised a second time after editing.
        document.Dither16BitOnSave = normalized == 16;
    }

    public CaptureDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            // A held total is specific to the endpoint that was measured. It can
            // survive gain adjustments on that endpoint, but never a device change.
            HasStoppedLevelCheck = false;
            _stoppedLevelCheckWasWholeRecord = false;
            _completedLevelCheckNote = null;
            _levelSnapshot = _engine.LevelSnapshot;
            ResetDisplayedLevels();
            RaiseLevelProperties();
            Raise(nameof(DeviceMemoryText));
            Raise(nameof(HasDeviceMemory));
            Raise(nameof(IsKorgDsDac10RSelected));
            Raise(nameof(DeviceCompatibilityText));
            Raise(nameof(CanForgetDeviceMemory));
            RefreshInputLevel();
        }
    }

    public bool IsKorgDsDac10RSelected => AudioHardware.IsKorgDsDac10R(_selectedDevice?.Name);

    public string DeviceCompatibilityText => IsKorgDsDac10RSelected
        ? "KORG DS-DAC-10R detected · Deep Groove records its Windows PCM stream. Set the hardware "
          + "input level and confirm the current Line/Phono mode in KORG's DS-DAC-10R Setting Tool; "
          + "AudioGate changes that input mode. Native DSD capture uses the KORG ASIO 2.1 path: record "
          + "DSF or DSDIFF in AudioGate, then open that file here for editing."
        : "";

    /// <summary>Recent input RMS (dB), newest last; drives the scrolling history strip.</summary>
    public LevelHistoryBuffer LevelHistory { get; } = new(LevelHistoryCapacity);

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!Set(ref _isRecording, value)) return;
            Raise(nameof(FormatText));
            Raise(nameof(LevelStatusTitle));
            Raise(nameof(LevelStatusDetail));
            RaiseInputActionAvailability();
        }
    }

    public bool IsLevelChecking
    {
        get => _isLevelChecking;
        private set
        {
            if (!Set(ref _isLevelChecking, value)) return;
            Raise(nameof(FormatText));
            Raise(nameof(LevelCheckButtonText));
            Raise(nameof(LevelStatusTitle));
            Raise(nameof(LevelStatusDetail));
        }
    }

    public bool IsWaitingForNeedleDrop
    {
        get => _isWaitingForNeedleDrop;
        private set
        {
            if (!Set(ref _isWaitingForNeedleDrop, value)) return;
            Raise(nameof(FormatText));
            Raise(nameof(LevelStatusTitle));
            Raise(nameof(LevelStatusDetail));
            Raise(nameof(ElapsedText));
        }
    }

    public bool IsFinalizing
    {
        get => _isFinalizing;
        private set
        {
            if (!Set(ref _isFinalizing, value)) return;
            RaiseInputActionAvailability();
        }
    }
    public bool SampleWholeRecord
    {
        get => _sampleWholeRecord;
        set
        {
            if (!Set(ref _sampleWholeRecord, value)) return;
            Raise(nameof(LevelCheckButtonText));
            Raise(nameof(LevelStatusTitle));
            Raise(nameof(LevelStatusDetail));
            Raise(nameof(LevelProgressText));
        }
    }

    /// <summary>Watch for the run-out groove and end the take when the side finishes.</summary>
    public bool AutoStopOnRunOut
    {
        get => _autoStopOnRunOut;
        set => Set(ref _autoStopOnRunOut, value);
    }

    /// <summary>Seconds of run-out to hear before stopping.</summary>
    public double RunOutHoldSeconds
    {
        get => _runOutHoldSeconds;
        set => Set(ref _runOutHoldSeconds, RunOutDetector.NormalizeHoldSeconds(value));
    }

    /// <summary>Stop after <see cref="AutoStopMinutes"/> regardless of what the detector hears.</summary>
    public bool AutoStopOnDuration
    {
        get => _autoStopOnDuration;
        set => Set(ref _autoStopOnDuration, value);
    }

    public int AutoStopMinutes
    {
        get => _autoStopMinutes;
        set => Set(ref _autoStopMinutes, Math.Clamp(value, MinimumAutoStopMinutes, MaximumAutoStopMinutes));
    }

    /// <summary>Live countdown while a run-out hold is under way; empty otherwise.</summary>
    public string AutoStopStatusText
    {
        get => _autoStopStatusText;
        private set
        {
            if (!Set(ref _autoStopStatusText, value)) return;
            Raise(nameof(HasAutoStopStatus));
        }
    }

    public bool HasAutoStopStatus => !string.IsNullOrEmpty(_autoStopStatusText);

    public bool HasStoppedLevelCheck
    {
        get => _levelCheckStopped;
        private set
        {
            if (!Set(ref _levelCheckStopped, value)) return;
            if (!value) _stoppedLevelCheckWasWholeRecord = false;
            Raise(nameof(LevelStatusTitle));
            Raise(nameof(LevelStatusDetail));
            Raise(nameof(CeilingChangeDiscardsHeldCheck));
        }
    }
    public bool HasPendingCapture => _engine.HasPendingCapture;
    public double PeakLDb { get => _peakL; private set => Set(ref _peakL, value); }
    public double PeakRDb { get => _peakR; private set => Set(ref _peakR, value); }
    public double RmsLDb { get => _rmsL; private set => Set(ref _rmsL, value); }
    public double RmsRDb { get => _rmsR; private set => Set(ref _rmsR, value); }
    public double HoldLDb { get => _holdL; private set => Set(ref _holdL, value); }
    public double HoldRDb { get => _holdR; private set => Set(ref _holdR, value); }

    public double InputLevelDb
    {
        get => _inputLevelDb;
        set
        {
            if (_refreshingInputLevel || !_hasInputLevelControl || _selectedDevice == null) return;
            double requested = Math.Clamp(value, _inputLevelMinimumDb, _inputLevelMaximumDb);
            if (Math.Abs(requested - _inputLevelDb) < 0.001) return;
            AppSettings settings = AppSettings.Instance;
            AudioInputLevelInfo result = AudioHardware.SetInputLevel(
                _selectedDevice.Id,
                AudioHardwareOptions.ParseRole(settings.InputDefaultRole, NAudio.CoreAudioApi.Role.Console),
                requested);
            ApplyInputLevelInfo(result);
            if (result.IsAvailable) ResetLevelAnalysisAfterInputChange();
        }
    }

    public double InputFineTrimDb
    {
        get => _inputFineTrimDb;
        set
        {
            double normalized = RecordingEngine.NormalizeInputFineTrimDb(value);
            if (!Set(ref _inputFineTrimDb, normalized)) return;
            _engine.InputFineTrimDb = normalized;
            Raise(nameof(InputFineTrimText));
            Raise(nameof(InputTotalLevelText));
            Raise(nameof(RecommendedInputSettingText));
            Raise(nameof(RecommendedInputBreakdownText));
            ResetLevelAnalysisAfterInputChange();
        }
    }

    public double InputLevelMinimumDb => _inputLevelMinimumDb;
    public double InputLevelMaximumDb => _inputLevelMaximumDb;
    public double InputLevelStepDb => _inputLevelStepDb;
    public bool HasInputLevelControl => _hasInputLevelControl;
    public string InputLevelText => _hasInputLevelControl ? $"{_inputLevelDb:+0.0;-0.0;0.0} dB" : "Unavailable";
    public string InputFineTrimText => $"{_inputFineTrimDb:+0.0;-0.0;0.0} dB";
    public string InputTotalLevelText => _hasInputLevelControl
        ? $"Total input gain {_inputLevelDb + _inputFineTrimDb:+0.0;-0.0;0.0} dB"
        : $"Deep Groove trim {_inputFineTrimDb:+0.0;-0.0;0.0} dB";
    public string InputLevelStatusText => !_hasInputLevelControl
        ? "This input does not expose a Windows level control"
            + (string.IsNullOrWhiteSpace(_inputLevelError) ? "." : $": {_inputLevelError}")
        : _inputLevelMuted
            ? "Muted in Windows. Unmute the input before checking levels."
            : $"Windows/driver step: {_inputLevelStepDb:0.0} dB. Use Fine Trim for 0.1 dB adjustment.";

    public bool SoftwarePlaythroughEnabled
    {
        get => _softwarePlaythroughEnabled;
        set
        {
            if (!Set(ref _softwarePlaythroughEnabled, value)) return;
            try
            {
                if (value) _softwarePlaythroughOutputName = PlaybackEngine.CurrentOutputName();
                _engine.SoftwarePlaythroughEnabled = value;
                _softwarePlaythroughError = null;
            }
            catch (Exception ex)
            {
                _softwarePlaythroughError = ex.Message;
                Set(ref _softwarePlaythroughEnabled, false, nameof(SoftwarePlaythroughEnabled));
            }
            Raise(nameof(SoftwarePlaythroughStatusText));
        }
    }

    public string SoftwarePlaythroughStatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_softwarePlaythroughError))
                return $"Playthrough unavailable: {_softwarePlaythroughError}";
            if (!_softwarePlaythroughEnabled)
                return "Off by default. Use headphones to prevent feedback.";
            return _engine.IsSoftwarePlaythroughActive
                ? $"Monitoring through {_softwarePlaythroughOutputName}. Use headphones to prevent feedback."
                : $"Armed for {_softwarePlaythroughOutputName}. Starts with Check Levels or recording.";
        }
    }

    public string ElapsedText => TimeFormat.Position(
        IsWaitingForNeedleDrop ? 0 : (long)(_engine.RecordedSeconds * _engine.SampleRate),
        _engine.SampleRate);

    public string FormatText
    {
        get
        {
            if (!IsRecording && !IsLevelChecking && !IsWaitingForNeedleDrop)
                return PunchInsertEnabled
                    ? "Uses the device mix format · punch keeps the open document's bit depth"
                    : $"Uses the device mix format · completed take: {RecordingBitDepthLabel}";
            string channels = _engine.Channels switch
            {
                1 => "Mono",
                2 => "Stereo",
                int count => $"{count} channels",
            };
            string destination = PunchInsertEnabled
                ? "open document bit depth"
                : $"{RecordingBitDepthLabel} take";
            return $"{_engine.SampleRate / 1000.0:0.0} kHz · 32-bit float device → "
                + $"{destination} · {channels}";
        }
    }

    private string RecordingBitDepthLabel => DescribeRecordingBitDepth(_recordingBitDepth);

    public string LevelCheckButtonText => IsLevelChecking
        ? SampleWholeRecord ? "Finish Full Scan" : "Stop Check"
        : SampleWholeRecord ? "Scan Whole Song" : "Check Levels";
    public double LevelConfidencePercent => _levelSnapshot.Confidence * 100;

    /// <summary>
    /// Peak the recommendation aims at; a user preference, settable here as well as in
    /// Settings so a ceiling can be found mid-transfer.
    ///
    /// Cached rather than read through to the analyzer: three bound properties read
    /// this on every meter tick, and the analyzer's accessor takes the same lock the
    /// capture callback holds for a whole packet. The UI thread has no business
    /// waiting on that to paint a value it already knows.
    /// </summary>
    public double TargetCeilingDb
    {
        get => _targetCeilingDb;
        set
        {
            double ceiling = AppSettings.NormalizeTargetCeilingDb(value);
            if (_targetCeilingDb.Equals(ceiling)) return;

            // Moving the value is deliberately cheap: the chip, the readout and the
            // meter's target band follow the drag, while the analyzer and the settings
            // file wait for CommitTargetCeiling. See there for why.
            _targetCeilingDb = ceiling;
            Raise(nameof(TargetCeilingDb));
            Raise(nameof(TargetCeilingText));
            Raise(nameof(TargetMeterMinimumDb));
            Raise(nameof(TargetMeterMaximumDb));
        }
    }

    /// <summary>
    /// Retargets the analysis and stores the ceiling. Kept out of the setter because a
    /// drag moves the value once per half decibel: each retarget drops the analyzer's
    /// cached snapshot, and the next UI tick would then rebuild the whole history
    /// <em>synchronously on the dispatcher</em> — up to two hours of blocks during a
    /// whole-side scan — besides rewriting settings.json every step. Call it when the
    /// gesture ends.
    /// </summary>
    public void CommitTargetCeiling()
    {
        if (_committedTargetCeilingDb.Equals(_targetCeilingDb)) return;

        _engine.LevelTargetCeilingDb = _targetCeilingDb;
        // Read back rather than trusting the setting: the analyzer clamps it.
        _targetCeilingDb = _engine.LevelTargetCeilingDb;
        _committedTargetCeilingDb = _targetCeilingDb;

        // A finished check measured the programme against the old ceiling, and its
        // blocks are gone — the analyzer is reset on stop — so the held result cannot
        // be re-derived and must not be shown as if it still applied. A running check
        // needs nothing: setting the ceiling drops the analyzer's cached snapshot, so
        // the next tick arrives already re-derived.
        if (HasStoppedLevelCheck)
        {
            HasStoppedLevelCheck = false;
            _completedLevelCheckNote = null;
            _levelSnapshot = _engine.LevelSnapshot;
            ResetDisplayedLevels();
        }

        PersistTargetCeiling();
        Raise(nameof(TargetCeilingDb));
        Raise(nameof(TargetCeilingText));
        RaiseLevelProperties();
    }

    /// <summary>
    /// Best-effort: a ceiling that cannot be written is still the one this session
    /// measures against, and the level card is the wrong place to interrupt with a
    /// settings-file error.
    /// </summary>
    private void PersistTargetCeiling()
    {
        double previous = AppSettings.Instance.RecordingTargetCeilingDb;
        AppSettings.Instance.RecordingTargetCeilingDb = _targetCeilingDb;
        if (!AppSettings.Instance.Save())
            AppSettings.Instance.RecordingTargetCeilingDb = previous;
    }

    /// <summary>True while a finished check is on screen, which changing the ceiling discards.</summary>
    public bool CeilingChangeDiscardsHeldCheck => HasStoppedLevelCheck;

    public double TargetMeterMinimumDb => TargetCeilingDb - 1 - DisplayedReserveDb;
    public double TargetMeterMaximumDb => TargetCeilingDb + 1 - DisplayedReserveDb;

    // The ceiling is always negative, so the typographic minus is written out
    // rather than left to the format string's ASCII hyphen.
    public string TargetCeilingText => $"TARGET CEILING −{Math.Abs(TargetCeilingDb):0.0} dBTP";
    public string SafetyReserveText => $"· {DisplayedReserveDb:0.#} dB SAFETY RESERVE";

    public string LevelStatusTitle
    {
        get
        {
            if (HasStoppedLevelCheck) return "Level check stopped";
            if (IsWaitingForNeedleDrop) return "Armed for needle drop";
            if (!IsLevelChecking && !IsRecording) return "Check levels before the take";
            if (_levelSnapshot.Status == RecordingLevelStatus.Clipping)
                return "Digital clipping detected";
            if (_levelSnapshot.InvalidSamples > 0) return "Input data error detected";
            return _levelSnapshot.Status switch
            {
                RecordingLevelStatus.WaitingForSignal => "Waiting for the record",
                RecordingLevelStatus.Analyzing => SampleWholeRecord
                    ? "Sampling the whole record side…"
                    : "Learning the loudest passage…",
                RecordingLevelStatus.TooLow => "Input level is conservative",
                RecordingLevelStatus.Good => "Recording level is ready",
                RecordingLevelStatus.Hot => "Input level is too hot",
                RecordingLevelStatus.Clipping => "Digital clipping detected",
                RecordingLevelStatus.UpstreamClipping => "Possible upstream clipping",
                _ => "Checking recording level",
            };
        }
    }

    public string LevelStatusDetail
    {
        get
        {
            if (HasStoppedLevelCheck)
            {
                if (_stoppedLevelCheckWasWholeRecord)
                    return "The whole-side scan is complete and its safest setting is frozen. Rewind the record, adjust to the displayed setting if needed, then start the take.";
                return _levelSnapshot.ActiveSeconds >= 10
                    ? "Monitoring is stopped and the completed measurements are frozen. Start recording, or run a new check after changing the input level."
                    : "Monitoring is stopped and the provisional measurements are frozen. Run a new check for at least 10 active seconds before recording.";
            }
            if (IsWaitingForNeedleDrop)
                return "Lower the stylus onto the lead-in groove. Recording starts from the contact click with a 250 ms safety pre-roll.";
            if (!IsLevelChecking && !IsRecording)
                return "Cue the loudest passage on the side and play at least 10 seconds. A 30–60 second scan gives a safer recommendation.";

            RecordingLevelSnapshot snapshot = _levelSnapshot;
            if (snapshot.InvalidSamples > 0 && snapshot.Status != RecordingLevelStatus.Clipping)
                return $"The input driver supplied {snapshot.InvalidSamples:N0} invalid sample(s). They were replaced with silence; restart the check and verify the device before recording.";

            string notes = Math.Abs(snapshot.BalanceDb) >= 50
                ? snapshot.BalanceDb > 0
                    ? " The right channel appears silent; check the cartridge leads, cable, and interface input."
                    : " The left channel appears silent; check the cartridge leads, cable, and interface input."
                : Math.Abs(snapshot.BalanceDb) >= 3
                    ? $" Channels differ by {Math.Abs(snapshot.BalanceDb):0.0} dB; verify cartridge alignment and interface gain."
                    : "";
            if (double.IsFinite(snapshot.DcOffsetDb) && snapshot.DcOffsetDb > DcWarningThresholdDb)
                notes += $" A {snapshot.DcOffsetDb:0.0} dBFS DC offset rides on the input; check cabling and interface grounding.";
            if (snapshot.HumFrequencyHz > 0)
                notes += $" A {snapshot.HumFrequencyHz} Hz hum ({snapshot.HumLevelDb:0.0} dBFS) dominates the quiet passages; check the turntable ground lead.";
            if (snapshot.ActiveSeconds >= 10
                && double.IsFinite(snapshot.TruePeakDb) && double.IsFinite(snapshot.ProgramPeakDb)
                && snapshot.TruePeakDb - snapshot.ProgramPeakDb >= 2)
                notes += snapshot.ClippedSamples > 0 && snapshot.Status != RecordingLevelStatus.Clipping
                    ? $" {snapshot.ClippedSamples:N0} isolated click sample(s) touched digital full scale, but run {snapshot.TruePeakDb - snapshot.ProgramPeakDb:0.0} dB above the programme; they are excluded from the gain advice because declicking replaces them later."
                    : $" Isolated clicks run {snapshot.TruePeakDb - snapshot.ProgramPeakDb:0.0} dB above the programme; the recommendation protects the music, and declicking removes the clicks later.";
            if (SampleWholeRecord && IsLevelChecking)
                notes += " Keep the side playing through the run-out groove; the safest setting encountered anywhere on the side is held.";
            return snapshot.Status switch
            {
                RecordingLevelStatus.WaitingForSignal =>
                    "Play the loudest musical passage; lead-in groove, hum, and silence do not count toward the scan."
                    + (snapshot.HumFrequencyHz > 0 ? notes : ""),
                RecordingLevelStatus.Analyzing =>
                    $"Keep it playing — {Math.Max(0, 10 - snapshot.ActiveSeconds):0.0} more active seconds are needed for a settled result." + notes,
                RecordingLevelStatus.TooLow =>
                    $"The capture is usable. Optionally raise the interface input by up to {snapshot.SuggestedGainDb:0.0} dB only if the analogue chain has known headroom; otherwise leave it and normalize after capture." + notes,
                RecordingLevelStatus.Good =>
                    $"The measured peak plus a {snapshot.ReserveDb:0.0} dB unseen-transient reserve stays near the safe ceiling." + notes,
                RecordingLevelStatus.Hot =>
                    snapshot.SuggestedGainDb < 0
                        ? $"Reduce the interface or phono-preamp gain by about {Math.Abs(snapshot.SuggestedGainDb):0.0} dB, then restart the check." + notes
                        : "The estimated intersample peak has crossed 0 dBTP. Lower the hardware input gain and restart the check." + notes,
                RecordingLevelStatus.Clipping =>
                    "Lower the hardware input gain and replay the passage. Digital gain after capture cannot repair clipped peaks."
                    + (snapshot.InvalidSamples > 0 ? " The input driver also supplied invalid samples." : "") + notes,
                RecordingLevelStatus.UpstreamClipping =>
                    "Flat-topped peaks suggest clipping before Deep Groove. Lower the preamp/interface gain and check again." + notes,
                _ => "Keep playing the loudest passage until the recommendation settles.",
            };
        }
    }

    public string SuggestedGainText
    {
        get
        {
            RecordingLevelSnapshot snapshot = _levelSnapshot;
            if (snapshot.Status == RecordingLevelStatus.Clipping) return "LOWER INPUT";
            if (snapshot.InvalidSamples > 0) return "CHECK DEVICE";
            if (snapshot.Status == RecordingLevelStatus.UpstreamClipping) return "CHECK PREAMP";
            // Hot without a negative programme suggestion means an intersample
            // over (possibly from a click): the only honest advice is lower input.
            if (snapshot.Status == RecordingLevelStatus.Hot && snapshot.SuggestedGainDb >= 0)
                return "LOWER INPUT";
            double change = HeldSuggestedGainDb;
            if (!double.IsFinite(change)) return "PROVISIONAL";
            if (Math.Abs(change) <= 1) return "NO CHANGE";
            return change > 0
                ? $"OPTIONAL +{change:0.0} dB"
                : $"REDUCE {Math.Abs(change):0.0} dB";
        }
    }

    public string RecommendedInputSettingText
    {
        get
        {
            if (HasImmediateInputWarning()) return SuggestedGainText;
            if (!TryGetRecommendedInputSetting(out AudioInputSettingPlan plan))
                return double.IsFinite(_heldRecommendedTotalDb) ? SuggestedGainText : "SCAN TO SET";
            return $"{plan.TotalLevelDb:+0.0;-0.0;0.0} dB TOTAL";
        }
    }

    public string RecommendedInputBreakdownText
    {
        get
        {
            if (HasImmediateInputWarning())
                return "Reduce hardware gain; fine trim cannot repair input clipping.";
            if (!TryGetRecommendedInputSetting(out AudioInputSettingPlan plan))
                return double.IsFinite(_heldRecommendedTotalDb)
                    ? $"Held whole-scan advice: {SuggestedGainText.ToLowerInvariant()}"
                    : "Play at least 10 active seconds; scanning the whole side is safest.";
            return $"Device {plan.DeviceLevelDb:+0.0;-0.0;0.0} dB | "
                + $"Fine {plan.FineTrimDb:+0.0;-0.0;0.0} dB | {SuggestedGainText}";
        }
    }

    public string TruePeakText => FormatDb(_levelSnapshot.TruePeakDb, "dBTP");
    public string ProgramPeakText => FormatDb(_levelSnapshot.ProgramPeakDb, "dBTP");
    public string ProjectedPeakText => _levelSnapshot.ActiveSeconds >= 10
        ? FormatDb(_levelSnapshot.ProjectedPeakDb, "dBTP")
        : "—";
    public string ProgramRmsText => FormatDb(_levelSnapshot.ProgramRmsDb, "dBFS");
    public string LoudnessText => FormatDb(_levelSnapshot.IntegratedLufs, "LUFS");
    public string NoiseFloorText => FormatDb(_levelSnapshot.NoiseFloorDb, "dB");
    public string CrestFactorText => double.IsFinite(_levelSnapshot.CrestFactorDb)
        ? $"{_levelSnapshot.CrestFactorDb:0.0} dB"
        : "—";

    /// <summary>How far the absolute true peak sits above the programme peak — the excluded click margin.</summary>
    public string ClickMarginText =>
        double.IsFinite(_levelSnapshot.TruePeakDb) && double.IsFinite(_levelSnapshot.ProgramPeakDb)
            ? $"+{_levelSnapshot.TruePeakDb - _levelSnapshot.ProgramPeakDb:0.0} dB"
            : "—";

    public string DcText => !double.IsFinite(_levelSnapshot.DcOffsetDb)
        ? "—"
        : _levelSnapshot.DcOffsetDb > DcWarningThresholdDb
            ? $"{_levelSnapshot.DcOffsetDb:0.0} dB"
            : "OK";

    public string HumText => _levelSnapshot.HumFrequencyHz > 0
        ? $"{_levelSnapshot.HumFrequencyHz} Hz"
        : "NONE";

    /// <summary>One-line recall of the last settled check on the selected input, empty when none.</summary>
    public string DeviceMemoryText
    {
        get
        {
            if (_selectedDevice == null) return "";
            if (AppSettings.Instance.GetInputCalibration(_selectedDevice.Id) is not { } info) return "";
            string gain = info.SuggestedGainDb > 1
                ? $"raise up to +{info.SuggestedGainDb:0.0} dB"
                : info.SuggestedGainDb < -1
                    ? $"reduce {Math.Abs(info.SuggestedGainDb):0.0} dB"
                    : "no change needed";
            return $"Last check {FormatCalibrationAge(info.CheckedUtc)}: {gain} · programme peak {info.ProgramPeakDb:0.0} dBTP";
        }
    }

    public bool HasDeviceMemory => DeviceMemoryText.Length > 0;

    /// <summary>The remembered entry for the selected input, or null.</summary>
    private AppSettings.InputCalibrationInfo? RememberedCalibration =>
        AppSettings.Instance.GetInputCalibration(_selectedDevice?.Id);

    /// <summary>
    /// Only entries recorded since applied settings were stored can be replayed;
    /// older ones remember the suggestion but not the setting that produced it.
    /// </summary>
    public bool CanUseRememberedSetting =>
        !IsRecording && !IsWaitingForNeedleDrop && !IsFinalizing
        && _hasInputLevelControl && !_inputLevelMuted
        && RememberedCalibration is { HasAppliedSetting: true } info
        && Math.Abs(info.TotalLevelDb!.Value - CurrentInputTotalDb) > 0.05;

    public bool CanForgetDeviceMemory =>
        !IsRecording && !IsWaitingForNeedleDrop && !IsFinalizing && RememberedCalibration != null;

    public bool CanApplyRecommendedInputSetting =>
        !IsRecording && !IsWaitingForNeedleDrop && !IsFinalizing
        && _hasInputLevelControl && !_inputLevelMuted
        && !HasImmediateInputWarning()
        && TryGetRecommendedInputSetting(out AudioInputSettingPlan plan)
        && Math.Abs(plan.TotalLevelDb - CurrentInputTotalDb) > 0.05;

    public string ApplyRecommendationText
    {
        get
        {
            if (_recommendationApplied && !CanApplyRecommendedInputSetting) return "Applied";
            if (!TryGetRecommendedInputSetting(out AudioInputSettingPlan plan)) return "Nothing to apply";
            double change = plan.TotalLevelDb - CurrentInputTotalDb;
            if (Math.Abs(change) <= 0.05) return "Nothing to apply";
            return change > 0 ? $"Apply +{change:0.0} dB" : $"Apply −{Math.Abs(change):0.0} dB";
        }
    }

    public string ApplyRecommendationStatusText => _applyRecommendationStatusText;

    /// <summary>
    /// Sets the Windows device level and WaveLab fine trim to the recommended plan,
    /// then restarts the measurement so the next reading describes the new gain.
    /// Returns false and reports why when the driver refuses.
    /// </summary>
    public bool ApplyRecommendedInputSetting()
    {
        if (!CanApplyRecommendedInputSetting) return false;
        if (!TryGetRecommendedInputSetting(out AudioInputSettingPlan plan)) return false;
        return ApplyInputSetting(
            plan, "Applied — play the loudest passage again to confirm.", fromRecommendation: true);
    }

    /// <summary>Restores the setting remembered for this input from a previous session.</summary>
    public bool UseRememberedSetting()
    {
        if (!CanUseRememberedSetting) return false;
        if (RememberedCalibration is not { HasAppliedSetting: true } info) return false;
        var plan = new AudioInputSettingPlan(
            info.DeviceLevelDb!.Value, info.FineTrimDb!.Value, info.TotalLevelDb!.Value);
        return ApplyInputSetting(
            plan, "Restored the setting remembered for this input.", fromRecommendation: false);
    }

    /// <summary>What became of a request to forget an input's remembered calibration.</summary>
    public enum ForgetMemoryOutcome
    {
        /// <summary>There was no entry to remove — nothing failed.</summary>
        NothingRemembered,
        Forgotten,
        SaveFailed,
    }

    /// <summary>Discards the remembered calibration for the selected input.</summary>
    public ForgetMemoryOutcome ForgetDeviceMemory()
    {
        if (_selectedDevice == null) return ForgetMemoryOutcome.NothingRemembered;

        // The removal and the write happen under one lock, and the entry is put back if the
        // write fails — reporting a failure while the live dictionary had already dropped it
        // would tell the user the memory survived and then let the next unrelated Save()
        // persist its removal anyway.
        switch (AppSettings.Instance.RemoveInputCalibration(_selectedDevice.Id))
        {
            case AppSettings.InputCalibrationRemoval.NothingRemembered:
                return ForgetMemoryOutcome.NothingRemembered;
            case AppSettings.InputCalibrationRemoval.SaveFailed:
                RaiseDeviceMemoryProperties();
                return ForgetMemoryOutcome.SaveFailed;
        }

        _calibrationSavedUtc = DateTime.MinValue;
        _calibrationSavedTotalDb = double.NaN;
        RaiseDeviceMemoryProperties();
        return ForgetMemoryOutcome.Forgotten;
    }

    private void RaiseDeviceMemoryProperties()
    {
        Raise(nameof(DeviceMemoryText));
        Raise(nameof(HasDeviceMemory));
        Raise(nameof(CanUseRememberedSetting));
        Raise(nameof(CanForgetDeviceMemory));
    }

    /// <param name="fromRecommendation">
    /// True only for Apply. Restoring a remembered setting lands a plan that may differ
    /// from the current recommendation, so it must not relabel Apply as "Applied".
    /// </param>
    private bool ApplyInputSetting(
        AudioInputSettingPlan plan, string successStatus, bool fromRecommendation)
    {
        if (_selectedDevice == null) return false;
        double previousFineTrimDb = _inputFineTrimDb;
        bool changed = true;

        // One reset, not one per control: both setters would otherwise restart the
        // analysis, and the first restart would measure the half-applied setting.
        _applyingRecommendation = true;
        try
        {
            bool fineFirst = AudioHardware.ApplyFineTrimFirst(previousFineTrimDb, plan);
            if (fineFirst) InputFineTrimDb = plan.FineTrimDb;

            AudioInputLevelInfo result = AudioHardware.SetInputLevel(
                _selectedDevice.Id,
                AudioHardwareOptions.ParseRole(
                    AppSettings.Instance.InputDefaultRole, NAudio.CoreAudioApi.Role.Console),
                plan.DeviceLevelDb);
            if (!result.IsAvailable)
            {
                InputFineTrimDb = previousFineTrimDb;
                // "Unavailable" is not proof that nothing reached the device:
                // SetInputLevel writes the level and only then reads it back, so an
                // endpoint that disappears between the two reports a failure for a
                // write that landed. Re-read instead of assuming, or every later
                // reading and the recommendation would keep describing gain the
                // hardware no longer has.
                double totalBeforeRereadDb = CurrentInputTotalDb;
                RefreshInputLevel();
                // Only restart the measurement if something actually moved; a plain
                // driver refusal should not cost the user the scan they have played.
                changed = fineFirst || Math.Abs(CurrentInputTotalDb - totalBeforeRereadDb) > 0.05;
                _applyRecommendationStatusText = string.IsNullOrWhiteSpace(result.Error)
                    ? "The input level could not be set."
                    : $"The input level could not be set: {result.Error}";
                return false;
            }
            ApplyInputLevelInfo(result);
            if (!fineFirst) InputFineTrimDb = plan.FineTrimDb;

            // A driver step wider than Fine Trim's range cannot land exactly on the
            // plan. PlanInputSetting always undershoots, so say so rather than
            // leaving the user to notice a stubborn residual suggestion.
            double residualDb = CurrentInputTotalDb - plan.TotalLevelDb;
            _applyRecommendationStatusText = Math.Abs(residualDb) > 0.05
                ? $"Driver accepted {CurrentInputTotalDb:0.0} dB of the {plan.TotalLevelDb:0.0} dB plan."
                : successStatus;
            if (fromRecommendation) _recommendationApplied = true;

            // The whole-side note quotes a gain that no longer applies.
            _completedLevelCheckNote = null;
            return true;
        }
        catch (Exception ex)
        {
            InputFineTrimDb = previousFineTrimDb;
            // Re-read rather than assume: the endpoint may have taken part of it.
            RefreshInputLevel();
            _applyRecommendationStatusText = $"The input level could not be set: {ex.Message}";
            return false;
        }
        finally
        {
            _applyingRecommendation = false;
            if (changed) ResetLevelAnalysisAfterInputChange();
            RaiseApplyProperties();
        }
    }

    /// <summary>
    /// Apply, Use Last Setting and Forget sit outside <c>inputGainControls</c>, so
    /// nothing else disables them when a take starts — their bound predicates have
    /// to be re-evaluated on every state transition that gates them.
    /// </summary>
    private void RaiseInputActionAvailability()
    {
        Raise(nameof(CanApplyRecommendedInputSetting));
        Raise(nameof(CanUseRememberedSetting));
        Raise(nameof(CanForgetDeviceMemory));
        Raise(nameof(ApplyRecommendationText));
    }

    private void RaiseApplyProperties()
    {
        Raise(nameof(ApplyRecommendationText));
        Raise(nameof(ApplyRecommendationStatusText));
        Raise(nameof(CanApplyRecommendedInputSetting));
        Raise(nameof(CanUseRememberedSetting));
        Raise(nameof(CanForgetDeviceMemory));
        Raise(nameof(DeviceMemoryText));
        Raise(nameof(HasDeviceMemory));
    }

    public string BalanceText
    {
        get
        {
            double balance = _levelSnapshot.BalanceDb;
            if (!double.IsFinite(balance) || Math.Abs(balance) < 0.05) return "CENTERED";
            if (balance >= 50) return "RIGHT SILENT";
            if (balance <= -50) return "LEFT SILENT";
            return balance > 0 ? $"L +{balance:0.0} dB" : $"R +{Math.Abs(balance):0.0} dB";
        }
    }

    public string ClippingText
    {
        get
        {
            RecordingLevelSnapshot snapshot = _levelSnapshot;
            if (snapshot.ClippedSamples > 0)
                return snapshot.Status == RecordingLevelStatus.Clipping
                    ? $"{snapshot.ClippedSamples:N0} DIGITAL"
                    : $"{snapshot.ClippedSamples:N0} ISOLATED";
            if (snapshot.InvalidSamples > 0) return $"{snapshot.InvalidSamples:N0} INVALID";
            if (snapshot.Status == RecordingLevelStatus.UpstreamClipping)
                return $"{snapshot.FlatTopCount:N0} FLAT TOP";
            return "NONE";
        }
    }

    public bool HasOnlyIsolatedClipping => _levelSnapshot.ClippedSamples > 0
        && _levelSnapshot.Status != RecordingLevelStatus.Clipping;

    public string LevelProgressText
    {
        get
        {
            RecordingLevelSnapshot snapshot = _levelSnapshot;
            if ((SampleWholeRecord && IsLevelChecking)
                || (_stoppedLevelCheckWasWholeRecord && HasStoppedLevelCheck))
            {
                TimeSpan scanned = TimeSpan.FromSeconds(snapshot.ElapsedSeconds);
                return $"{(int)scanned.TotalMinutes:00}:{scanned.Seconds:00} scanned · whole-side safest setting held";
            }
            return $"{snapshot.ActiveSeconds:0.0} s active · {snapshot.Confidence:P0} scan maturity";
        }
    }

    public AudioDocument? Result { get; private set; }
    public event Action<string>? RecordingBitDepthSaveFailed;
    public event Action<RecordingStoppedInfo, Exception?>? UnexpectedStopCompleted;
    public event Action<RecordingStoppedInfo>? MonitoringStopped;
    public event Action<RecordingStoppedInfo>? NeedleDropMonitoringStopped;
    public event Action? NeedleDropStarted;

    /// <summary>Raised after a take ended itself and finished finalizing.</summary>
    public event Action<AutoStopInfo, Exception?>? AutoStopped;

    public bool StartLevelCheck()
    {
        if (IsRecording || IsLevelChecking || IsWaitingForNeedleDrop || IsFinalizing || HasPendingCapture) return false;
        try
        {
            Interlocked.Exchange(ref _expectedRecordingSessionId, 0);
            long sessionId = _engine.StartLevelCheck(
                _selectedDevice?.Id,
                fullDurationScan: SampleWholeRecord);
            Interlocked.Exchange(ref _expectedRecordingSessionId, sessionId);
            Result = null;
            _levelSnapshot = _engine.LevelSnapshot;
            ResetDisplayedLevels();
            ResetCalibrationWriteThrottle();
            _completedLevelCheckNote = null;
            _recommendationApplied = false;
            _applyRecommendationStatusText = "";
            HasStoppedLevelCheck = false;
            IsLevelChecking = true;
            PersistSelectedDevice();
            RaiseLevelProperties();
            RaiseApplyProperties();
            return true;
        }
        catch (Exception ex)
        {
            SyncSoftwarePlaythroughState();
            System.Windows.MessageBox.Show($"Could not start the level check:\n{ex.Message}", "Record",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    public bool ResetLevelCheck() => ResetLevelCheck(preserveRecommendation: false);

    private bool ResetLevelCheck(bool preserveRecommendation)
    {
        if (IsRecording || IsWaitingForNeedleDrop || IsFinalizing) return false;
        if (IsLevelChecking)
        {
            long sessionId = Interlocked.Read(ref _expectedRecordingSessionId);
            if (!_engine.ResetLevelCheck(sessionId)) return false;
        }
        else if (!HasStoppedLevelCheck)
        {
            return false;
        }

        _levelSnapshot = _engine.LevelSnapshot;
        ResetDisplayedLevels(clearRecommendation: !preserveRecommendation);
        if (!preserveRecommendation) _completedLevelCheckNote = null;
        ResetCalibrationWriteThrottle();
        HasStoppedLevelCheck = false;
        RaiseLevelProperties();
        return true;
    }

    public bool StopLevelCheck()
    {
        if (!IsLevelChecking || IsRecording || IsWaitingForNeedleDrop || IsFinalizing) return false;
        long sessionId = Interlocked.Read(ref _expectedRecordingSessionId);
        RecordingLevelSnapshot? stoppedSnapshot = _engine.StopLevelCheckAndGetSnapshot(sessionId);
        if (stoppedSnapshot == null) return false;

        // The stream is now quiescent, so this forced snapshot includes the last
        // completed packet rather than the throttled live-summary cache.
        UpdateHeldRecommendation(stoppedSnapshot);
        _stoppedLevelCheckWasWholeRecord = SampleWholeRecord;
        if (SampleWholeRecord)
        {
            SaveWholeRecordCalibration(stoppedSnapshot);
            _completedLevelCheckNote = BuildWholeRecordCaptureNote(stoppedSnapshot);
        }
        else
        {
            // Last chance to persist this check: nothing writes calibrations once
            // the check has stopped.
            SaveCalibrationOnceSettled(stoppedSnapshot, force: true);
        }
        Interlocked.CompareExchange(ref _expectedRecordingSessionId, 0, sessionId);
        _levelSnapshot = stoppedSnapshot;
        HasStoppedLevelCheck = true;
        IsLevelChecking = false;
        RaiseLevelProperties();
        return true;
    }

    public bool Start()
    {
        if (IsRecording || IsWaitingForNeedleDrop || IsFinalizing || HasPendingCapture) return false;
        try
        {
            ApplyAutoStopSettings();
            long sessionId;
            if (IsLevelChecking)
            {
                // Going straight from a check into the take: persist what the check
                // learned before the analyzer is reset for the retained stream.
                SaveCalibrationOnceSettled(_levelSnapshot, force: true);
                sessionId = Interlocked.Read(ref _expectedRecordingSessionId);
                if (!_engine.BeginRetainedCapture(sessionId))
                    throw new InvalidOperationException(
                        "The monitoring session ended before recording could start.");
            }
            else
            {
                Interlocked.Exchange(ref _expectedRecordingSessionId, 0);
                sessionId = _engine.Start(_selectedDevice?.Id);
                Interlocked.Exchange(ref _expectedRecordingSessionId, sessionId);
            }

            Result = null;
            _levelSnapshot = _engine.LevelSnapshot;
            ResetDisplayedLevels();
            HasStoppedLevelCheck = false;
            IsLevelChecking = false;
            IsRecording = true;
            PersistSelectedDevice();
            return true;
        }
        catch (Exception ex)
        {
            if (IsLevelChecking)
            {
                long monitorSessionId = Interlocked.Read(ref _expectedRecordingSessionId);
                if (!_engine.IsCurrentSession(monitorSessionId))
                {
                    Interlocked.CompareExchange(ref _expectedRecordingSessionId, 0, monitorSessionId);
                    _engine.Stop();
                    _levelSnapshot = _engine.LevelSnapshot;
                    IsLevelChecking = false;
                    RaiseLevelProperties();
                }
            }
            SyncSoftwarePlaythroughState();
            System.Windows.MessageBox.Show($"Could not start recording:\n{ex.Message}", "Record",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    public bool StartOnNeedleDrop()
    {
        if (IsRecording || IsWaitingForNeedleDrop || IsFinalizing || HasPendingCapture) return false;
        try
        {
            ApplyAutoStopSettings();
            long sessionId;
            if (IsLevelChecking)
            {
                sessionId = Interlocked.Read(ref _expectedRecordingSessionId);
                if (!_engine.ArmNeedleDrop(sessionId))
                    throw new InvalidOperationException(
                        "The monitoring session ended before the needle trigger could be armed.");
            }
            else
            {
                Interlocked.Exchange(ref _expectedRecordingSessionId, 0);
                sessionId = _engine.StartOnNeedleDrop(_selectedDevice?.Id);
                Interlocked.Exchange(ref _expectedRecordingSessionId, sessionId);
            }

            Result = null;
            _levelSnapshot = _engine.LevelSnapshot;
            ResetDisplayedLevels();
            HasStoppedLevelCheck = false;
            IsLevelChecking = false;
            IsWaitingForNeedleDrop = true;
            PersistSelectedDevice();
            RaiseLevelProperties();
            return true;
        }
        catch (Exception ex)
        {
            long monitorSessionId = Interlocked.Read(ref _expectedRecordingSessionId);
            if (!_engine.IsCurrentSession(monitorSessionId))
            {
                Interlocked.CompareExchange(ref _expectedRecordingSessionId, 0, monitorSessionId);
                _engine.Stop();
                _levelSnapshot = _engine.LevelSnapshot;
                IsLevelChecking = false;
                IsWaitingForNeedleDrop = false;
                RaiseLevelProperties();
            }
            SyncSoftwarePlaythroughState();
            System.Windows.MessageBox.Show($"Could not arm needle-drop recording:\n{ex.Message}", "Record",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// Push the panel's choices into the engine. Called at every start, so a
    /// take always uses what the dialog shows rather than a stale arming.
    /// </summary>
    private void ApplyAutoStopSettings()
    {
        AutoStopStatusText = "";
        _engine.ConfigureAutoStop(
            _autoStopOnRunOut,
            _runOutHoldSeconds,
            _autoStopOnDuration ? _autoStopMinutes : 0);
    }

    public Task StopAndFinishAsync() => StopAndFinishAsync(sessionId: null);

    private Task StopAndFinishSessionAsync(long sessionId) => StopAndFinishAsync(sessionId);

    private Task StopAndFinishAsync(long? sessionId)
    {
        if (IsFinalizing) return _finalization;
        IsRecording = false;
        // The countdown describes a take that is still running. Clear it here so
        // it cannot outlive the take if finalization fails and the dialog stays.
        AutoStopStatusText = "";
        IsFinalizing = true;
        long ownedSessionId = sessionId ?? Interlocked.Read(ref _expectedRecordingSessionId);
        _finalization = FinalizeCoreAsync(ownedSessionId, requireSessionMatch: sessionId.HasValue);
        return _finalization;
    }

    private async Task FinalizeCoreAsync(long ownedSessionId, bool requireSessionMatch)
    {
        try
        {
            Result = requireSessionMatch
                ? await _engine.StopSessionAndGetDocumentAsync(ownedSessionId)
                : await _engine.StopAndGetDocumentAsync();
            if (Result != null) ApplyRecordingBitDepth(Result, _recordingBitDepth);
            // Append rather than replace: the engine may already have written a
            // note at the monitor-to-take boundary, and losing it would discard the
            // level check that actually preceded this take.
            if (Result != null && _completedLevelCheckNote != null)
            {
                Result.CaptureNote = string.IsNullOrWhiteSpace(Result.CaptureNote)
                    ? _completedLevelCheckNote
                    : $"{Result.CaptureNote} {_completedLevelCheckNote}";
            }
        }
        finally
        {
            if (ownedSessionId != 0)
                Interlocked.CompareExchange(ref _expectedRecordingSessionId, 0, ownedSessionId);
            Raise(nameof(HasPendingCapture));
            IsFinalizing = false;
        }
    }

    public void Cancel()
    {
        if (IsFinalizing) return;
        Interlocked.Exchange(ref _expectedRecordingSessionId, 0);
        _engine.Stop();
        _levelSnapshot = _engine.LevelSnapshot;
        HasStoppedLevelCheck = false;
        IsLevelChecking = false;
        IsWaitingForNeedleDrop = false;
        IsRecording = false;
        AutoStopStatusText = "";
        ResetDisplayedLevels();
        Raise(nameof(HasPendingCapture));
        RaiseLevelProperties();
    }

    public void Tick()
    {
        static double ToDb(float value) => value <= 1e-5f
            ? -60
            : Math.Max(-60, 20 * Math.Log10(value));
        static double DecayTo(double current, double target) => target >= current
            ? target
            : Math.Max(target, current - 1.5);

        if (!IsRecording && !IsLevelChecking && !IsWaitingForNeedleDrop) return;

        double rmsDbL = ToDb(_engine.RmsL);
        double rmsDbR = ToDb(_engine.RmsR);
        PeakLDb = DecayTo(_peakL, ToDb(_engine.PeakL));
        PeakRDb = DecayTo(_peakR, ToDb(_engine.PeakR));
        RmsLDb = DecayTo(_rmsL, rmsDbL);
        RmsRDb = DecayTo(_rmsR, rmsDbR);
        _levelSnapshot = _engine.LevelSnapshot;
        RecordingLevelSnapshot snapshot = _levelSnapshot;
        UpdateHeldRecommendation(snapshot);
        HoldLDb = ToMeterDb(snapshot.PeakLeftDb);
        HoldRDb = ToMeterDb(snapshot.PeakRightDb);
        LevelHistory.Append(Math.Max(rmsDbL, rmsDbR));
        SaveCalibrationOnceSettled(snapshot, force: false);
        UpdateAutoStopCountdown();
        Raise(nameof(ElapsedText));
        SyncSoftwarePlaythroughState();
        RaiseLevelProperties();
    }

    /// <summary>
    /// Mirrors the detector's hold into the dialog. It stays blank for the first
    /// few seconds of quiet so a gap between tracks does not look like the end.
    /// </summary>
    private void UpdateAutoStopCountdown()
    {
        if (!IsRecording || !_autoStopOnRunOut) return;
        double remaining = _engine.AutoStopCountdownSeconds;
        double elapsed = _runOutHoldSeconds - remaining;
        double visibleAfter = Math.Min(CountdownVisibleAfterSeconds, _runOutHoldSeconds / 2);
        AutoStopStatusText = double.IsNaN(remaining) || elapsed < visibleAfter
            ? ""
            : $"RUN-OUT DETECTED — STOPPING IN {Math.Max(1, Math.Ceiling(remaining)):0} s";
    }

    private void ResetCalibrationWriteThrottle()
    {
        _calibrationSavedUtc = DateTime.MinValue;
        _calibrationSavedTotalDb = double.NaN;
    }

    /// <summary>
    /// Remembers the <em>safest</em> verdict of each check on this input, so the
    /// next session with the same device opens with what it actually needed rather
    /// than whatever the first ten seconds happened to say. The held recommendation
    /// only ever moves down, so re-writing whenever it does converges on the safest
    /// value; the throttle keeps the 30 Hz tick from rewriting settings.json.
    /// </summary>
    private void SaveCalibrationOnceSettled(RecordingLevelSnapshot snapshot, bool force)
    {
        if (SampleWholeRecord || _selectedDevice == null) return;
        if (!force && !IsLevelChecking) return;
        // Skip a Hot verdict whose programme suggestion is not a reduction: that
        // combination means an intersample over, which has no stable gain answer
        // worth remembering for next time.
        bool settledVerdict = snapshot.ActiveSeconds >= 10
            && double.IsFinite(snapshot.ProgramPeakDb)
            && (snapshot.Status is RecordingLevelStatus.TooLow or RecordingLevelStatus.Good
                || (snapshot.Status == RecordingLevelStatus.Hot && snapshot.SuggestedGainDb < 0));
        if (!settledVerdict || !double.IsFinite(_heldRecommendedTotalDb)) return;

        // Nothing new to record unless the held setting got safer.
        if (double.IsFinite(_calibrationSavedTotalDb)
            && _heldRecommendedTotalDb >= _calibrationSavedTotalDb - 0.001) return;
        // The throttle only rate-limits the *live* writes. The final write always
        // goes through, otherwise a last decrease landing inside the throttle
        // window would leave a remembered setting hotter than what was measured —
        // the opposite of what this memory is for.
        //
        // It deliberately does not require a *successful* previous write: a failing
        // save leaves the total unstamped, and gating on that would turn an
        // unwritable settings file into one write attempt per tick.
        if (!force && DateTime.UtcNow - _calibrationSavedUtc < CalibrationWriteInterval) return;

        WriteCalibration(HeldSuggestedGainDb, _heldProgramPeakDb);
    }

    private void SaveWholeRecordCalibration(RecordingLevelSnapshot snapshot)
    {
        if (_selectedDevice == null
            || !double.IsFinite(_heldRecommendedTotalDb)
            || !double.IsFinite(_heldProgramPeakDb)
            || snapshot.InvalidSamples > 0
            || snapshot.Status is RecordingLevelStatus.Clipping
                or RecordingLevelStatus.UpstreamClipping) return;

        WriteCalibration(HeldSuggestedGainDb, _heldProgramPeakDb);
    }

    /// <summary>
    /// Records the outcome together with the setting that would realize it, so a
    /// later session can replay it rather than only read about it.
    /// </summary>
    private void WriteCalibration(double suggestedGainDb, double programPeakDb)
    {
        if (_selectedDevice == null
            || !double.IsFinite(suggestedGainDb)
            || !double.IsFinite(programPeakDb)) return;

        AudioInputSettingPlan? plan =
            TryGetRecommendedInputSetting(out AudioInputSettingPlan candidate) ? candidate : null;
        try
        {
            // Only claim the memory once it is actually on disk — but still stamp the
            // attempt. The throttle keys off this timestamp, so leaving it unset while
            // the file is unwritable (locked by a second instance, read-only profile,
            // full disk) would put a full serialize and write on every 33 ms tick.
            bool written = AppSettings.Instance.SetInputCalibration(
                _selectedDevice.Id,
                new AppSettings.InputCalibrationInfo(
                    suggestedGainDb,
                    programPeakDb,
                    DateTime.UtcNow,
                    plan?.DeviceLevelDb,
                    plan?.FineTrimDb,
                    plan?.TotalLevelDb));
            if (!written)
            {
                _calibrationSavedUtc = DateTime.UtcNow;
                return;
            }
            _calibrationSavedUtc = DateTime.UtcNow;
            _calibrationSavedTotalDb = _heldRecommendedTotalDb;
            Raise(nameof(DeviceMemoryText));
            Raise(nameof(HasDeviceMemory));
            Raise(nameof(CanUseRememberedSetting));
            Raise(nameof(CanForgetDeviceMemory));
        }
        catch { } // best effort; a failure just skips the memory
    }

    private string? BuildWholeRecordCaptureNote(RecordingLevelSnapshot snapshot)
    {
        if (!double.IsFinite(_heldRecommendedTotalDb)
            || !double.IsFinite(_heldProgramPeakDb)) return null;

        double change = HeldSuggestedGainDb;
        string gain = change > 0 ? $"+{change:0.0}" : $"{change:0.0}";
        TimeSpan scanned = TimeSpan.FromSeconds(snapshot.ElapsedSeconds);
        return $"Whole-side level scan: safest programme peak {_heldProgramPeakDb:0.0} dBTP, "
            + $"suggested input change {gain} dB "
            + $"({(int)scanned.TotalMinutes}:{scanned.Seconds:00} scanned).";
    }

    private void PersistSelectedDevice()
    {
        if (_selectedDevice == null) return;
        try
        {
            string? previousDevice = AppSettings.Instance.InputDeviceId;
            AppSettings.Instance.InputDeviceId = _selectedDevice.Id;
            if (!AppSettings.Instance.Save())
            {
                AppSettings.Instance.InputDeviceId = previousDevice;
                System.Windows.MessageBox.Show("The input preference could not be saved:\n"
                    + AppSettings.Instance.LastSaveError, "Record",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
        catch { }
    }

    private void RefreshInputLevel()
    {
        if (_selectedDevice == null)
        {
            ApplyInputLevelInfo(new AudioInputLevelInfo(
                false, 0, -96, 0, 0.5, false, "No active input device is selected"));
            return;
        }

        AppSettings settings = AppSettings.Instance;
        ApplyInputLevelInfo(AudioHardware.GetInputLevel(
            _selectedDevice.Id,
            AudioHardwareOptions.ParseRole(settings.InputDefaultRole, NAudio.CoreAudioApi.Role.Console)));
    }

    /// <summary>
    /// Re-reads hardware gain after an external device utility closes. A running scan or completed
    /// recommendation is always invalidated: a vendor utility can change gain outside the Windows
    /// endpoint-volume surface, so an unchanged Core Audio reading is not proof that nothing moved.
    /// </summary>
    public void RefreshInputLevelAfterExternalChange()
    {
        if (IsRecording || IsWaitingForNeedleDrop || IsFinalizing) return;
        RefreshInputLevel();

        if (IsLevelChecking || HasStoppedLevelCheck)
            ResetLevelCheck(preserveRecommendation: false);
    }

    private void ApplyInputLevelInfo(AudioInputLevelInfo info)
    {
        _refreshingInputLevel = true;
        try
        {
            Set(ref _inputLevelMinimumDb, info.MinimumDb, nameof(InputLevelMinimumDb));
            Set(ref _inputLevelMaximumDb, info.MaximumDb, nameof(InputLevelMaximumDb));
            Set(ref _inputLevelStepDb, info.IncrementDb, nameof(InputLevelStepDb));
            Set(ref _inputLevelDb, info.LevelDb, nameof(InputLevelDb));
            Set(ref _hasInputLevelControl, info.IsAvailable, nameof(HasInputLevelControl));
            _inputLevelMuted = info.IsMuted;
            _inputLevelError = info.Error;
            Raise(nameof(InputLevelText));
            Raise(nameof(InputLevelStatusText));
            Raise(nameof(InputTotalLevelText));
            Raise(nameof(RecommendedInputSettingText));
            Raise(nameof(RecommendedInputBreakdownText));
        }
        finally { _refreshingInputLevel = false; }
    }

    /// <summary>
    /// Restarts the measurement after the input gain moves, keeping the held
    /// recommendation. Suppressed while a plan is being applied: that path moves
    /// two controls and resets once at the end, so the analysis never measures a
    /// half-applied setting.
    /// </summary>
    private void ResetLevelAnalysisAfterInputChange()
    {
        if (_applyingRecommendation) return;
        if (IsLevelChecking || HasStoppedLevelCheck)
            ResetLevelCheck(preserveRecommendation: true);
    }

    private void SyncSoftwarePlaythroughState()
    {
        bool enabled = _engine.SoftwarePlaythroughEnabled;
        Set(ref _softwarePlaythroughEnabled, enabled, nameof(SoftwarePlaythroughEnabled));
        _softwarePlaythroughError = _engine.SoftwarePlaythroughError;
        Raise(nameof(SoftwarePlaythroughStatusText));
    }

    private void UpdateHeldRecommendation(RecordingLevelSnapshot snapshot)
    {
        bool usable = snapshot.ActiveSeconds >= 10
            && double.IsFinite(snapshot.ProgramPeakDb)
            && snapshot.InvalidSamples == 0
            && snapshot.Status is not RecordingLevelStatus.Clipping
                and not RecordingLevelStatus.UpstreamClipping
            && !(snapshot.Status == RecordingLevelStatus.Hot && snapshot.SuggestedGainDb >= 0);
        if (!usable) return;
        double nextSetting = HoldSafeInputSetting(
            _heldRecommendedTotalDb,
            CurrentInputTotalDb,
            snapshot.SuggestedGainDb);
        if (!double.IsFinite(_heldRecommendedTotalDb)
            || nextSetting < _heldRecommendedTotalDb - 0.001)
            _heldProgramPeakDb = snapshot.ProgramPeakDb;
        _heldRecommendedTotalDb = nextSetting;
    }

    internal static double HoldSafeInputSetting(
        double heldTotalDb,
        double currentTotalDb,
        double suggestedChangeDb)
    {
        double candidateTotal = currentTotalDb + suggestedChangeDb;
        return double.IsFinite(heldTotalDb)
            ? Math.Min(heldTotalDb, candidateTotal)
            : candidateTotal;
    }

    private bool HasImmediateInputWarning()
    {
        RecordingLevelSnapshot snapshot = _levelSnapshot;
        return snapshot.Status is RecordingLevelStatus.Clipping or RecordingLevelStatus.UpstreamClipping
            || snapshot.InvalidSamples > 0
            || snapshot.Status == RecordingLevelStatus.Hot && snapshot.SuggestedGainDb >= 0;
    }

    private bool TryGetRecommendedInputSetting(out AudioInputSettingPlan plan)
    {
        if (!_hasInputLevelControl || !double.IsFinite(_heldRecommendedTotalDb))
        {
            plan = null!;
            return false;
        }

        plan = AudioHardware.PlanInputSetting(
            _heldRecommendedTotalDb,
            _inputLevelMinimumDb,
            _inputLevelMaximumDb,
            _inputLevelStepDb);
        return true;
    }

    private double CurrentInputTotalDb => _inputLevelDb + _inputFineTrimDb;

    private double HeldSuggestedGainDb => double.IsFinite(_heldRecommendedTotalDb)
        ? _heldRecommendedTotalDb - CurrentInputTotalDb
        : double.NaN;

    private void ResetDisplayedLevels(bool clearRecommendation = true)
    {
        if (clearRecommendation)
        {
            _heldRecommendedTotalDb = double.NaN;
            _heldProgramPeakDb = double.NaN;
        }
        PeakLDb = PeakRDb = RmsLDb = RmsRDb = HoldLDb = HoldRDb = -60;
        LevelHistory.Reset();
    }

    private void RaiseLevelProperties()
    {
        Raise(nameof(LevelConfidencePercent));
        Raise(nameof(TargetMeterMinimumDb));
        Raise(nameof(TargetMeterMaximumDb));
        Raise(nameof(SafetyReserveText));
        Raise(nameof(CeilingChangeDiscardsHeldCheck));
        Raise(nameof(LevelStatusTitle));
        Raise(nameof(LevelStatusDetail));
        Raise(nameof(SuggestedGainText));
        Raise(nameof(RecommendedInputSettingText));
        Raise(nameof(RecommendedInputBreakdownText));
        Raise(nameof(TruePeakText));
        Raise(nameof(ProgramPeakText));
        Raise(nameof(ProjectedPeakText));
        Raise(nameof(ProgramRmsText));
        Raise(nameof(LoudnessText));
        Raise(nameof(NoiseFloorText));
        Raise(nameof(CrestFactorText));
        Raise(nameof(ClickMarginText));
        Raise(nameof(DcText));
        Raise(nameof(HumText));
        Raise(nameof(BalanceText));
        Raise(nameof(ClippingText));
        Raise(nameof(HasOnlyIsolatedClipping));
        Raise(nameof(LevelProgressText));
        Raise(nameof(SoftwarePlaythroughStatusText));
        // The recommendation can appear or converge mid-scan, so the Apply button's
        // enablement has to track the readout rather than only state transitions.
        Raise(nameof(ApplyRecommendationText));
        Raise(nameof(CanApplyRecommendedInputSetting));
        Raise(nameof(CanUseRememberedSetting));
        Raise(nameof(CanForgetDeviceMemory));
    }

    private static double ToMeterDb(double value) =>
        double.IsFinite(value) ? Math.Max(-60, value) : -60;

    private double DisplayedReserveDb => _levelSnapshot.ActiveSeconds < 10
        ? 6
        : _levelSnapshot.ReserveDb;

    private static string FormatDb(double value, string suffix) =>
        double.IsFinite(value) ? $"{value:0.0} {suffix}" : "—";

    private static string FormatCalibrationAge(DateTime checkedUtc)
    {
        TimeSpan age = DateTime.UtcNow - checkedUtc;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 30) return $"{(int)age.TotalDays} d ago";
        return checkedUtc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _expectedRecordingSessionId, 0);
        _engine.Dispose();
    }
}

/// <summary>
/// Fixed-capacity input-level history, oldest first. Appending is O(1) and
/// reports a single change, where an ObservableCollection capped with
/// RemoveAt(0) shifted the whole list and raised both a Remove and an Add on
/// every 33 ms tick. Reads exactly like the list it replaces, so the bound
/// history strip is unaffected.
/// </summary>
public sealed class LevelHistoryBuffer : IList<double>, IReadOnlyList<double>, INotifyCollectionChanged
{
    private const string AppendOnly = "The level history is append-only.";
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    private readonly double[] _values;
    private int _start;   // ring index of the oldest sample
    private int _count;

    public LevelHistoryBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new double[capacity];
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Capacity => _values.Length;
    public int Count => _count;
    public bool IsReadOnly => true;

    public double this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _values[Wrap(_start + index)];
        }
        set => throw new NotSupportedException(AppendOnly);
    }

    /// <summary>Adds one sample, dropping the oldest once the ring is full.</summary>
    public void Append(double value)
    {
        if (_count < _values.Length)
        {
            _values[Wrap(_start + _count)] = value;
            _count++;
            CollectionChanged?.Invoke(this,
                new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, value, _count - 1));
            return;
        }

        _values[_start] = value;
        _start = Wrap(_start + 1);
        // Dropping the oldest while appending the newest is not expressible as a
        // single Add or Remove, so the strip is invalidated once instead.
        CollectionChanged?.Invoke(this, ResetArgs);
    }

    /// <summary>Discards the whole history.</summary>
    public void Reset()
    {
        if (_count == 0) return;
        _start = 0;
        _count = 0;
        CollectionChanged?.Invoke(this, ResetArgs);
    }

    public int IndexOf(double item)
    {
        for (int i = 0; i < _count; i++)
            if (_values[Wrap(_start + i)].Equals(item)) return i;
        return -1;
    }

    public bool Contains(double item) => IndexOf(item) >= 0;

    public void CopyTo(double[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || array.Length - arrayIndex < _count)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        for (int i = 0; i < _count; i++) array[arrayIndex + i] = _values[Wrap(_start + i)];
    }

    public IEnumerator<double> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return _values[Wrap(_start + i)];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void IList<double>.Insert(int index, double item) => throw new NotSupportedException(AppendOnly);
    void IList<double>.RemoveAt(int index) => throw new NotSupportedException(AppendOnly);
    void ICollection<double>.Add(double item) => throw new NotSupportedException(AppendOnly);
    void ICollection<double>.Clear() => throw new NotSupportedException(AppendOnly);
    bool ICollection<double>.Remove(double item) => throw new NotSupportedException(AppendOnly);

    private int Wrap(int index) => index >= _values.Length ? index - _values.Length : index;
}
