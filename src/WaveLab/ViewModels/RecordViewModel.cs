using System.Collections.ObjectModel;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;

namespace WaveLab.ViewModels;

public sealed record CaptureDevice(string Id, string Name);

public sealed class RecordViewModel : ObservableObject, IDisposable
{
    private const int LevelHistoryCapacity = 900; // about 30 s at the 33 ms tick
    private const double DcWarningThresholdDb = -50;

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
    private bool _calibrationSaved;
    private bool _sampleWholeRecord;
    private bool _stoppedLevelCheckWasWholeRecord;
    private string? _completedLevelCheckNote;
    private bool _disposed;

    public RecordViewModel()
    {
        _levelSnapshot = _engine.LevelSnapshot;
        try
        {
            foreach (var (id, name) in RecordingEngine.GetCaptureDevices())
                Devices.Add(new CaptureDevice(id, name));
        }
        catch { }

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
    }

    public ObservableCollection<CaptureDevice> Devices { get; } = [];

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
            RefreshInputLevel();
        }
    }

    /// <summary>Recent input RMS (dB), newest last; drives the scrolling history strip.</summary>
    public ObservableCollection<double> LevelHistory { get; } = [];

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!Set(ref _isRecording, value)) return;
            Raise(nameof(FormatText));
            Raise(nameof(LevelStatusTitle));
            Raise(nameof(LevelStatusDetail));
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

    public bool IsFinalizing { get => _isFinalizing; private set => Set(ref _isFinalizing, value); }
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

    public bool HasStoppedLevelCheck
    {
        get => _levelCheckStopped;
        private set
        {
            if (!Set(ref _levelCheckStopped, value)) return;
            if (!value) _stoppedLevelCheckWasWholeRecord = false;
            Raise(nameof(LevelStatusTitle));
            Raise(nameof(LevelStatusDetail));
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
        : $"WaveLab trim {_inputFineTrimDb:+0.0;-0.0;0.0} dB";
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
                return "Records in the device mix format · saved as 16/24/32-bit WAV";
            string channels = _engine.Channels switch
            {
                1 => "Mono",
                2 => "Stereo",
                int count => $"{count} channels",
            };
            return $"{_engine.SampleRate / 1000.0:0.0} kHz · 32-bit float · {channels} (device mix format)";
        }
    }

    public string LevelCheckButtonText => IsLevelChecking
        ? SampleWholeRecord ? "Finish Full Scan" : "Stop Check"
        : SampleWholeRecord ? "Scan Whole Song" : "Check Levels";
    public double LevelConfidencePercent => _levelSnapshot.Confidence * 100;
    public double TargetMeterMinimumDb => -4 - DisplayedReserveDb;
    public double TargetMeterMaximumDb => -2 - DisplayedReserveDb;

    public string LevelTargetText =>
        $"ESTIMATED CEILING −3 dBTP · {DisplayedReserveDb:0.#} dB SAFETY RESERVE";

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
                    "Flat-topped peaks suggest clipping before WaveLab. Lower the preamp/interface gain and check again." + notes,
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
    public string LoudnessText => FormatDb(_levelSnapshot.ProgramLoudnessLufs, "LUFS");
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
            if (!AppSettings.Instance.InputCalibrations.TryGetValue(_selectedDevice.Id, out var info)) return "";
            string gain = info.SuggestedGainDb > 1
                ? $"raise up to +{info.SuggestedGainDb:0.0} dB"
                : info.SuggestedGainDb < -1
                    ? $"reduce {Math.Abs(info.SuggestedGainDb):0.0} dB"
                    : "no change needed";
            return $"Last check {FormatCalibrationAge(info.CheckedUtc)}: {gain} · programme peak {info.ProgramPeakDb:0.0} dBTP";
        }
    }

    public bool HasDeviceMemory => DeviceMemoryText.Length > 0;

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
    public event Action<RecordingStoppedInfo, Exception?>? UnexpectedStopCompleted;
    public event Action<RecordingStoppedInfo>? MonitoringStopped;
    public event Action<RecordingStoppedInfo>? NeedleDropMonitoringStopped;
    public event Action? NeedleDropStarted;

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
            _calibrationSaved = false;
            _completedLevelCheckNote = null;
            HasStoppedLevelCheck = false;
            IsLevelChecking = true;
            PersistSelectedDevice();
            RaiseLevelProperties();
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
        _calibrationSaved = false;
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
            long sessionId;
            if (IsLevelChecking)
            {
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

    public Task StopAndFinishAsync() => StopAndFinishAsync(sessionId: null);

    private Task StopAndFinishSessionAsync(long sessionId) => StopAndFinishAsync(sessionId);

    private Task StopAndFinishAsync(long? sessionId)
    {
        if (IsFinalizing) return _finalization;
        IsRecording = false;
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
            if (Result != null && _completedLevelCheckNote != null)
                Result.CaptureNote = _completedLevelCheckNote;
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
        LevelHistory.Add(Math.Max(rmsDbL, rmsDbR));
        while (LevelHistory.Count > LevelHistoryCapacity) LevelHistory.RemoveAt(0);
        SaveCalibrationOnceSettled();
        Raise(nameof(ElapsedText));
        SyncSoftwarePlaythroughState();
        RaiseLevelProperties();
    }

    /// <summary>
    /// Remembers the first settled verdict of each check on this input, so the
    /// next session with the same device opens with last time's outcome.
    /// </summary>
    private void SaveCalibrationOnceSettled()
    {
        if (_calibrationSaved || SampleWholeRecord || !IsLevelChecking || _selectedDevice == null) return;
        RecordingLevelSnapshot snapshot = _levelSnapshot;
        // Skip a Hot verdict whose programme suggestion is not a reduction: that
        // combination means an intersample over, which has no stable gain answer
        // worth remembering for next time.
        bool settledVerdict = snapshot.ActiveSeconds >= 10
            && double.IsFinite(snapshot.ProgramPeakDb)
            && (snapshot.Status is RecordingLevelStatus.TooLow or RecordingLevelStatus.Good
                || (snapshot.Status == RecordingLevelStatus.Hot && snapshot.SuggestedGainDb < 0));
        if (!settledVerdict) return;
        _calibrationSaved = true;
        try
        {
            AppSettings.Instance.InputCalibrations[_selectedDevice.Id] =
                new AppSettings.InputCalibrationInfo(
                    snapshot.SuggestedGainDb, snapshot.ProgramPeakDb, DateTime.UtcNow);
            AppSettings.Instance.Save(); // best effort; a failure just skips the memory
            Raise(nameof(DeviceMemoryText));
            Raise(nameof(HasDeviceMemory));
        }
        catch { }
    }

    private void SaveWholeRecordCalibration(RecordingLevelSnapshot snapshot)
    {
        if (_selectedDevice == null
            || !double.IsFinite(_heldRecommendedTotalDb)
            || !double.IsFinite(_heldProgramPeakDb)
            || snapshot.InvalidSamples > 0
            || snapshot.Status is RecordingLevelStatus.Clipping
                or RecordingLevelStatus.UpstreamClipping) return;

        _calibrationSaved = true;
        try
        {
            AppSettings.Instance.InputCalibrations[_selectedDevice.Id] =
                new AppSettings.InputCalibrationInfo(
                    HeldSuggestedGainDb, _heldProgramPeakDb, DateTime.UtcNow);
            AppSettings.Instance.Save();
            Raise(nameof(DeviceMemoryText));
            Raise(nameof(HasDeviceMemory));
        }
        catch { }
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

    private void ResetLevelAnalysisAfterInputChange()
    {
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
        LevelHistory.Clear();
    }

    private void RaiseLevelProperties()
    {
        Raise(nameof(LevelConfidencePercent));
        Raise(nameof(TargetMeterMinimumDb));
        Raise(nameof(TargetMeterMaximumDb));
        Raise(nameof(LevelTargetText));
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
