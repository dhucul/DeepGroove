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
    private double _peakL = -60, _peakR = -60;

    public RecordViewModel()
    {
        try
        {
            foreach (var (id, name) in RecordingEngine.GetCaptureDevices()) Devices.Add(new CaptureDevice(id, name));
        }
        catch { }
        var preferred = Util.AppSettings.Instance.InputDeviceId;
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == preferred) ?? (Devices.Count > 0 ? Devices[0] : null);
    }

    public ObservableCollection<CaptureDevice> Devices { get; } = [];

    public CaptureDevice? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    public bool IsRecording { get => _isRecording; private set { Set(ref _isRecording, value); Raise(nameof(FormatText)); } }
    public double PeakLDb { get => _peakL; private set => Set(ref _peakL, value); }
    public double PeakRDb { get => _peakR; private set => Set(ref _peakR, value); }

    public string ElapsedText => TimeFormat.Position(
        (long)(_engine.RecordedSeconds * _engine.SampleRate), _engine.SampleRate);

    public string FormatText => IsRecording
        ? $"{_engine.SampleRate / 1000.0:0.0} kHz · 32-bit float · {(_engine.Channels == 1 ? "Mono" : "Stereo")} (device mix format)"
        : "Records in the device mix format · saved as 16/24/32-bit WAV";

    public AudioDocument? Result { get; private set; }

    public bool Start()
    {
        try
        {
            _engine.Start(_selectedDevice?.Id);
            IsRecording = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not start recording:\n{ex.Message}", "Record",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
    }

    public void StopAndFinish()
    {
        Result = _engine.StopAndGetDocument();
        IsRecording = false;
    }

    public void Cancel()
    {
        _engine.Stop();
        IsRecording = false;
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
