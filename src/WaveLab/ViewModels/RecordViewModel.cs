using System.Collections.ObjectModel;
using WaveLab.Audio;
using WaveLab.Util;

namespace WaveLab.ViewModels;

public sealed record CaptureDevice(string Id, string Name);

public sealed class RecordViewModel : ObservableObject
{
    private readonly RecordingEngine _engine = new();
    private CaptureDevice? _selectedDevice;
    private bool _isRecording;
    private bool _isFinalizing;
    private double _peakL = -60, _peakR = -60;
    private Task _finalization = Task.CompletedTask;

    public RecordViewModel()
    {
        try
        {
            foreach (var (id, name) in RecordingEngine.GetCaptureDevices()) Devices.Add(new CaptureDevice(id, name));
        }
        catch { }
        var preferred = Util.AppSettings.Instance.InputDeviceId;
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == preferred) ?? (Devices.Count > 0 ? Devices[0] : null);
        _engine.CaptureStopped += info =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                if (!IsRecording) return;
                Exception? failure = null;
                try { await StopAndFinishAsync(); }
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

    public bool IsRecording { get => _isRecording; private set { Set(ref _isRecording, value); Raise(nameof(FormatText)); } }
    public bool IsFinalizing { get => _isFinalizing; private set => Set(ref _isFinalizing, value); }
    public bool HasPendingCapture => _engine.HasPendingCapture;
    public double PeakLDb { get => _peakL; private set => Set(ref _peakL, value); }
    public double PeakRDb { get => _peakR; private set => Set(ref _peakR, value); }

    public string ElapsedText => TimeFormat.Position(
        (long)(_engine.RecordedSeconds * _engine.SampleRate), _engine.SampleRate);

    public string FormatText => IsRecording
        ? $"{_engine.SampleRate / 1000.0:0.0} kHz · 32-bit float · {(_engine.Channels == 1 ? "Mono" : "Stereo")} (device mix format)"
        : "Records in the device mix format · saved as 16/24/32-bit WAV";

    public AudioDocument? Result { get; private set; }
    public event Action<RecordingStoppedInfo, Exception?>? UnexpectedStopCompleted;

    public bool Start()
    {
        if (IsRecording || IsFinalizing || HasPendingCapture) return false;
        try
        {
            _engine.Start(_selectedDevice?.Id);
            Result = null;
            IsRecording = true;
            if (_selectedDevice != null)
            {
                // Failure to persist a preference must not leave a live capture
                // running while the UI believes Start failed.
                try
                {
                    Util.AppSettings.Instance.InputDeviceId = _selectedDevice.Id;
                    Util.AppSettings.Instance.Save();
                }
                catch { }
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not start recording:\n{ex.Message}", "Record",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    public Task StopAndFinishAsync()
    {
        if (IsFinalizing) return _finalization;
        IsRecording = false;
        IsFinalizing = true;
        _finalization = FinalizeCoreAsync();
        return _finalization;
    }

    private async Task FinalizeCoreAsync()
    {
        try { Result = await _engine.StopAndGetDocumentAsync(); }
        finally
        {
            Raise(nameof(HasPendingCapture));
            IsFinalizing = false;
        }
    }

    public void Cancel()
    {
        if (IsFinalizing) return;
        _engine.Stop();
        IsRecording = false;
        Raise(nameof(HasPendingCapture));
    }

    public void Tick()
    {
        double ToDb(float v) => v <= 1e-5f ? -60 : Math.Max(-60, 20 * Math.Log10(v));
        double DecayTo(double cur, double target) => target >= cur ? target : Math.Max(target, cur - 1.5);
        PeakLDb = DecayTo(_peakL, ToDb(_engine.PeakL));
        PeakRDb = DecayTo(_peakR, ToDb(_engine.PeakR));
        Raise(nameof(ElapsedText));
    }
}
