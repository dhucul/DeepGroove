using NAudio.CoreAudioApi;
using NAudio.Wave;
using WaveLab.Util;

namespace WaveLab.Audio;

/// <summary>WASAPI shared-mode playback of a document region through the master section.</summary>
public sealed class PlaybackEngine : IDisposable
{
    private WasapiOut? _out;
    private MMDevice? _outDevice;
    private DocumentProvider? _provider;

    public MasterSection Master { get; } = new();
    public bool IsPlaying { get; private set; }
    public bool Loop { get; set; }

    public event Action? PlaybackStopped;

    public int PositionSamples => _provider?.PositionSamples ?? 0;

    public static List<(string Id, string Name)> GetOutputDevices()
    {
        var result = new List<(string, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                result.Add((dev.ID, dev.FriendlyName));
        }
        catch { }
        return result;
    }

    /// <summary>Friendly name of the device playback will use (settings choice or system default).</summary>
    public static string CurrentOutputName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var id = AppSettings.Instance.OutputDeviceId;
            var dev = id != null ? enumerator.GetDevice(id)
                                 : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev.FriendlyName;
        }
        catch { return "Default output"; }
    }

    private WasapiOut CreateOut()
    {
        var settings = AppSettings.Instance;
        int latency = Math.Clamp(settings.BufferMs, 20, 400);
        if (settings.OutputDeviceId != null)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _outDevice = enumerator.GetDevice(settings.OutputDeviceId);
                return new WasapiOut(_outDevice, AudioClientShareMode.Shared, true, latency);
            }
            catch { /* fall back to default */ }
        }
        return new WasapiOut(AudioClientShareMode.Shared, latency);
    }

    public void Play(AudioDocument doc, int startSample, int? endSample)
    {
        Stop();
        _provider = new DocumentProvider(doc, startSample, endSample) { Loop = Loop };
        Master.SetSource(_provider);
        Master.Loudness.Reset();
        _out = CreateOut();
        _out.PlaybackStopped += (_, _) =>
        {
            IsPlaying = false;
            PlaybackStopped?.Invoke();
        };
        _out.Init(Master);
        _out.Play();
        IsPlaying = true;
    }

    public void Stop()
    {
        if (_out != null)
        {
            var o = _out;
            _out = null;
            try { o.Stop(); o.Dispose(); } catch { }
        }
        if (_outDevice != null)
        {
            try { _outDevice.Dispose(); } catch { }
            _outDevice = null;
        }
        IsPlaying = false;
    }

    public void Dispose() => Stop();

    private sealed class DocumentProvider : ISampleProvider
    {
        private readonly AudioDocument _doc;
        private readonly int _start;
        private readonly int _end;
        private int _pos;

        public DocumentProvider(AudioDocument doc, int start, int? end)
        {
            _doc = doc;
            _start = Math.Clamp(start, 0, Math.Max(0, doc.Length - 1));
            _end = Math.Clamp(end ?? doc.Length, _start, doc.Length);
            _pos = _start;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(doc.SampleRate, doc.ChannelCount);
        }

        public bool Loop { get; set; }
        public WaveFormat WaveFormat { get; }
        public int PositionSamples => _pos;

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int framesWanted = count / channels;
            int written = 0;

            while (framesWanted > 0)
            {
                int available = _end - _pos;
                if (available <= 0)
                {
                    if (!Loop) break;
                    _pos = _start;
                    available = _end - _pos;
                    if (available <= 0) break;
                }
                int n = Math.Min(framesWanted, available);
                _doc.ReadInterleaved(_pos, n, buffer, offset + written * channels);
                _pos += n;
                written += n;
                framesWanted -= n;
            }
            return written * channels;
        }
    }
}
