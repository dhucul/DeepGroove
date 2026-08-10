using System.Collections.ObjectModel;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;

namespace WaveLab.ViewModels;

public sealed record CaptureDevice(string Id, string Name);

public sealed class RecordViewModel : ObservableObject, IDisposable
{
    private readonly RecordingEngine _engine = new();
    private RecordingLevelSnapshot _levelSnapshot;
    private CaptureDevice? _selectedDevice;
    private bool _isRecording;
    private bool _isLevelChecking;
    private bool _isFinalizing;
    private double _peakL = -60, _peakR = -60;
    private double _rmsL = -60, _rmsR = -60;
    private double _holdL = -60, _holdR = -60;
    private Task _finalization = Task.CompletedTask;
    private long _expectedRecordingSessionId;
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

        _engine.CaptureStopped += info =>
        {
            if (_disposed || !_engine.IsCurrentSession(info.SessionId)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                if (_disposed
                    || info.SessionId != Interlocked.Read(ref _expectedRecordingSessionId)
                    || !_engine.IsCurrentSession(info.SessionId)) return;

                if (IsLevelChecking)
                {
                    IsLevelChecking = false;
                    Interlocked.CompareExchange(ref _expectedRecordingSessionId, 0, info.SessionId);
                    _engine.Stop();
                    RaiseLevelProperties();
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
    }

    public ObservableCollection<CaptureDevice> Devices { get; } = [];

    public CaptureDevice? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

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

    public bool IsFinalizing { get => _isFinalizing; private set => Set(ref _isFinalizing, value); }
    public bool HasPendingCapture => _engine.HasPendingCapture;
    public double PeakLDb { get => _peakL; private set => Set(ref _peakL, value); }
    public double PeakRDb { get => _peakR; private set => Set(ref _peakR, value); }
    public double RmsLDb { get => _rmsL; private set => Set(ref _rmsL, value); }
    public double RmsRDb { get => _rmsR; private set => Set(ref _rmsR, value); }
    public double HoldLDb { get => _holdL; private set => Set(ref _holdL, value); }
    public double HoldRDb { get => _holdR; private set => Set(ref _holdR, value); }

    public string ElapsedText => TimeFormat.Position(
        (long)(_engine.RecordedSeconds * _engine.SampleRate), _engine.SampleRate);

    public string FormatText
    {
        get
        {
            if (!IsRecording && !IsLevelChecking)
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

    public string LevelCheckButtonText => IsLevelChecking ? "Restart Check" : "Check Levels";
    public double LevelConfidencePercent => _levelSnapshot.Confidence * 100;
    public double TargetMeterMinimumDb => -4 - DisplayedReserveDb;
    public double TargetMeterMaximumDb => -2 - DisplayedReserveDb;

    public string LevelTargetText =>
        $"ESTIMATED CEILING −3 dBTP · {DisplayedReserveDb:0.#} dB SAFETY RESERVE";

    public string LevelStatusTitle
    {
        get
        {
            if (!IsLevelChecking && !IsRecording) return "Check levels before the take";
            if (_levelSnapshot.Status == RecordingLevelStatus.Clipping)
                return "Digital clipping detected";
            if (_levelSnapshot.InvalidSamples > 0) return "Input data error detected";
            return _levelSnapshot.Status switch
            {
                RecordingLevelStatus.WaitingForSignal => "Waiting for the record",
                RecordingLevelStatus.Analyzing => "Learning the loudest passage…",
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
            if (!IsLevelChecking && !IsRecording)
                return "Cue the loudest passage on the side and play at least 10 seconds. A 30–60 second scan gives a safer recommendation.";

            RecordingLevelSnapshot snapshot = _levelSnapshot;
            if (snapshot.InvalidSamples > 0 && snapshot.Status != RecordingLevelStatus.Clipping)
                return $"The input driver supplied {snapshot.InvalidSamples:N0} invalid sample(s). They were replaced with silence; restart the check and verify the device before recording.";

            string imbalance = Math.Abs(snapshot.BalanceDb) >= 50
                ? snapshot.BalanceDb > 0
                    ? " The right channel appears silent; check the cartridge leads, cable, and interface input."
                    : " The left channel appears silent; check the cartridge leads, cable, and interface input."
                : Math.Abs(snapshot.BalanceDb) >= 3
                    ? $" Channels differ by {Math.Abs(snapshot.BalanceDb):0.0} dB; verify cartridge alignment and interface gain."
                    : "";
            return snapshot.Status switch
            {
                RecordingLevelStatus.WaitingForSignal =>
                    "Play the loudest musical passage; lead-in groove, hum, and silence do not count toward the scan.",
                RecordingLevelStatus.Analyzing =>
                    $"Keep it playing — {Math.Max(0, 10 - snapshot.ActiveSeconds):0.0} more active seconds are needed for a settled result." + imbalance,
                RecordingLevelStatus.TooLow =>
                    $"The capture is usable. Optionally raise the interface input by up to {snapshot.SuggestedGainDb:0.0} dB only if the analogue chain has known headroom; otherwise leave it and normalize after capture." + imbalance,
                RecordingLevelStatus.Good =>
                    $"The measured peak plus a {snapshot.ReserveDb:0.0} dB unseen-transient reserve stays near the safe ceiling." + imbalance,
                RecordingLevelStatus.Hot =>
                    snapshot.SuggestedGainDb < 0
                        ? $"Reduce the interface or phono-preamp gain by about {Math.Abs(snapshot.SuggestedGainDb):0.0} dB, then restart the check." + imbalance
                        : "The estimated intersample peak has crossed 0 dBTP. Lower the hardware input gain and restart the check." + imbalance,
                RecordingLevelStatus.Clipping =>
                    "Lower the hardware input gain and replay the passage. Digital gain after capture cannot repair clipped peaks."
                    + (snapshot.InvalidSamples > 0 ? " The input driver also supplied invalid samples." : "") + imbalance,
                RecordingLevelStatus.UpstreamClipping =>
                    "Flat-topped peaks suggest clipping before WaveLab. Lower the preamp/interface gain and check again." + imbalance,
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
            if (snapshot.Status == RecordingLevelStatus.Hot && snapshot.ActiveSeconds < 10)
                return "LOWER INPUT";
            if (snapshot.ActiveSeconds < 10) return "PROVISIONAL";
            if (snapshot.Status == RecordingLevelStatus.Good) return "NO CHANGE";
            return snapshot.SuggestedGainDb > 0
                ? $"OPTIONAL +{snapshot.SuggestedGainDb:0.0} dB"
                : $"REDUCE {Math.Abs(snapshot.SuggestedGainDb):0.0} dB";
        }
    }

    public string TruePeakText => FormatDb(_levelSnapshot.TruePeakDb, "dBTP");
    public string ProjectedPeakText => _levelSnapshot.ActiveSeconds >= 10
        ? FormatDb(_levelSnapshot.ProjectedPeakDb, "dBTP")
        : "—";
    public string ProgramRmsText => FormatDb(_levelSnapshot.ProgramRmsDb, "dBFS");
    public string CrestFactorText => double.IsFinite(_levelSnapshot.CrestFactorDb)
        ? $"{_levelSnapshot.CrestFactorDb:0.0} dB"
        : "—";

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
            if (snapshot.ClippedSamples > 0) return $"{snapshot.ClippedSamples:N0} DIGITAL";
            if (snapshot.InvalidSamples > 0) return $"{snapshot.InvalidSamples:N0} INVALID";
            if (snapshot.Status == RecordingLevelStatus.UpstreamClipping)
                return $"{snapshot.FlatTopCount:N0} FLAT TOP";
            return "NONE";
        }
    }

    public string LevelProgressText
    {
        get
        {
            RecordingLevelSnapshot snapshot = _levelSnapshot;
            return $"{snapshot.ActiveSeconds:0.0} s active · {snapshot.Confidence:P0} scan maturity";
        }
    }

    public AudioDocument? Result { get; private set; }
    public event Action<RecordingStoppedInfo, Exception?>? UnexpectedStopCompleted;
    public event Action<RecordingStoppedInfo>? MonitoringStopped;

    public bool StartLevelCheck()
    {
        if (IsRecording || IsLevelChecking || IsFinalizing || HasPendingCapture) return false;
        try
        {
            Interlocked.Exchange(ref _expectedRecordingSessionId, 0);
            long sessionId = _engine.StartLevelCheck(_selectedDevice?.Id);
            Interlocked.Exchange(ref _expectedRecordingSessionId, sessionId);
            Result = null;
            _levelSnapshot = _engine.LevelSnapshot;
            ResetDisplayedLevels();
            IsLevelChecking = true;
            PersistSelectedDevice();
            RaiseLevelProperties();
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not start the level check:\n{ex.Message}", "Record",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    public bool RestartLevelCheck()
    {
        if (!IsLevelChecking || IsRecording || IsFinalizing) return false;
        long sessionId = Interlocked.Read(ref _expectedRecordingSessionId);
        if (!_engine.ResetLevelCheck(sessionId)) return false;
        _levelSnapshot = _engine.LevelSnapshot;
        ResetDisplayedLevels();
        RaiseLevelProperties();
        return true;
    }

    public bool Start()
    {
        if (IsRecording || IsFinalizing || HasPendingCapture) return false;
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
            System.Windows.MessageBox.Show($"Could not start recording:\n{ex.Message}", "Record",
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
        IsLevelChecking = false;
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

        PeakLDb = DecayTo(_peakL, ToDb(_engine.PeakL));
        PeakRDb = DecayTo(_peakR, ToDb(_engine.PeakR));
        RmsLDb = DecayTo(_rmsL, ToDb(_engine.RmsL));
        RmsRDb = DecayTo(_rmsR, ToDb(_engine.RmsR));
        _levelSnapshot = _engine.LevelSnapshot;
        RecordingLevelSnapshot snapshot = _levelSnapshot;
        HoldLDb = ToMeterDb(snapshot.PeakLeftDb);
        HoldRDb = ToMeterDb(snapshot.PeakRightDb);
        Raise(nameof(ElapsedText));
        RaiseLevelProperties();
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

    private void ResetDisplayedLevels()
    {
        PeakLDb = PeakRDb = RmsLDb = RmsRDb = HoldLDb = HoldRDb = -60;
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
        Raise(nameof(TruePeakText));
        Raise(nameof(ProjectedPeakText));
        Raise(nameof(ProgramRmsText));
        Raise(nameof(CrestFactorText));
        Raise(nameof(BalanceText));
        Raise(nameof(ClippingText));
        Raise(nameof(LevelProgressText));
    }

    private static double ToMeterDb(double value) =>
        double.IsFinite(value) ? Math.Max(-60, value) : -60;

    private double DisplayedReserveDb => _levelSnapshot.ActiveSeconds < 10
        ? 6
        : _levelSnapshot.ReserveDb;

    private static string FormatDb(double value, string suffix) =>
        double.IsFinite(value) ? $"{value:0.0} {suffix}" : "—";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _expectedRecordingSessionId, 0);
        _engine.Dispose();
    }
}
