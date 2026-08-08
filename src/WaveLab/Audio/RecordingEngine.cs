using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace WaveLab.Audio;

/// <summary>WASAPI capture into memory; produces a new AudioDocument on stop.</summary>
public sealed class RecordingEngine : IDisposable
{
    private const long MaxCaptureBytes = 2L * 1024 * 1024 * 1024; // safety cap on in-memory recording

    private WasapiCapture? _capture;
    private readonly List<float[]> _blocks = [];
    private int _channels = 2, _sampleRate = 48000;
    private long _totalSamples;
    private bool _capacityReached;

    public bool IsRecording { get; private set; }
    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public double RecordedSeconds => _sampleRate > 0 ? (double)_totalSamples / _channels / _sampleRate : 0;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    public static List<(string Id, string Name)> GetCaptureDevices()
    {
        var result = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            result.Add((dev.ID, dev.FriendlyName));
        return result;
    }

    public void Start(string? deviceId)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = deviceId == null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);

        _capture = new WasapiCapture(device);
        bool isFloat = _capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (_capture.WaveFormat is WaveFormatExtensible ext && ext.SubFormat == AudioSubtypes.MFAudioFormat_Float);
        if (!isFloat)
        {
            _capture.Dispose();
            _capture = null;
            throw new NotSupportedException($"Unsupported capture format ({device.FriendlyName}) — expected 32-bit float shared-mode mix.");
        }
        _channels = _capture.WaveFormat.Channels;
        _sampleRate = _capture.WaveFormat.SampleRate;
        _blocks.Clear();
        _totalSamples = 0;
        _capacityReached = false;

        _capture.DataAvailable += OnData;
        _capture.StartRecording();
        IsRecording = true;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capacityReached) return;
        if (_totalSamples * 4L > MaxCaptureBytes)
        {
            // stop capturing but keep the buffered audio for StopAndGetDocument
            _capacityReached = true;
            var capture = _capture;
            Task.Run(() => { try { capture?.StopRecording(); } catch { } });
            return;
        }

        // WASAPI shared-mode capture delivers 32-bit float (validated in Start)
        int samples = e.BytesRecorded / 4;
        var block = new float[samples];
        Buffer.BlockCopy(e.Buffer, 0, block, 0, e.BytesRecorded);
        lock (_blocks) { _blocks.Add(block); _totalSamples += samples; }

        float pl = 0, pr = 0;
        for (int i = 0; i < samples; i += _channels)
        {
            float al = Math.Abs(block[i]);
            if (al > pl) pl = al;
            if (_channels > 1)
            {
                float ar = Math.Abs(block[i + 1]);
                if (ar > pr) pr = ar;
            }
        }
        PeakL = pl;
        PeakR = _channels > 1 ? pr : pl;
    }

    public AudioDocument? StopAndGetDocument()
    {
        if (_capture == null) return null;
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _capture = null;
        IsRecording = false;

        lock (_blocks)
        {
            int frames = (int)(_totalSamples / _channels);
            if (frames == 0) return null;
            var ch = new float[_channels][];
            for (int c = 0; c < _channels; c++) ch[c] = new float[frames];
            int frame = 0, chIdx = 0;
            foreach (var block in _blocks)
                foreach (var s in block)
                {
                    if (frame >= frames) break;
                    ch[chIdx][frame] = s;
                    if (++chIdx == _channels) { chIdx = 0; frame++; }
                }
            _blocks.Clear();
            return new AudioDocument(ch, _sampleRate, 32) { Title = $"Recording {DateTime.Now:HH-mm-ss}.wav" };
        }
    }

    public void Stop()
    {
        if (_capture != null)
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
        IsRecording = false;
    }

    public void Dispose() => Stop();
}
