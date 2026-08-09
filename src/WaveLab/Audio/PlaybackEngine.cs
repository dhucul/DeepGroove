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
    private EventHandler<StoppedEventArgs>? _playbackStoppedHandler;
    private long _nextPlaybackSession;
    private readonly object _controlLock = new();
    private readonly object _stateLock = new();

    public MasterSection Master { get; } = new();
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public AudioDocument? SourceDocument { get; private set; }
    public bool Loop { get; set; }

    public event Action<long, AudioDocument, int>? PlaybackStopped;

    public int PositionSamples
    {
        get { lock (_stateLock) return _provider?.PositionSamples ?? 0; }
    }

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
            using var dev = id != null ? enumerator.GetDevice(id)
                                       : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev.FriendlyName;
        }
        catch { return "Default output"; }
    }

    private static (WasapiOut Output, MMDevice? Device) CreateOut()
    {
        var settings = AppSettings.Instance;
        int latency = Math.Clamp(settings.BufferMs, 20, 400);
        if (settings.OutputDeviceId != null)
        {
            MMDevice? device = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(settings.OutputDeviceId);
                return (new WasapiOut(device, AudioClientShareMode.Shared, true, latency), device);
            }
            catch
            {
                try { device?.Dispose(); } catch { }
                // Fall back to the current system default when a saved endpoint
                // disappeared or cannot be initialized.
            }
        }
        return (new WasapiOut(AudioClientShareMode.Shared, latency), null);
    }

    public long Play(AudioDocument doc, int startSample, int? endSample)
    {
        lock (_controlLock)
        {
            StopCore();
            long playbackSession = ++_nextPlaybackSession;
            var provider = new DocumentProvider(
                doc, startSample, endSample,
                expandMonoToStereo: doc.ChannelCount == 1 && Master.ExpandsMonoToStereo)
            { Loop = Loop };
            WasapiOut? output = null;
            MMDevice? device = null;
            EventHandler<StoppedEventArgs>? handler = null;
            bool registered = false;

            try
            {
                (output, device) = CreateOut();
                Master.SetSource(provider);
                Master.Loudness.Reset();
                output.Init(Master);

                handler = (_, _) => OnPlaybackStopped(output, device, provider, handler!, playbackSession, doc);
                output.PlaybackStopped += handler;
                lock (_stateLock)
                {
                    _out = output;
                    _outDevice = device;
                    _provider = provider;
                    _playbackStoppedHandler = handler;
                    SourceDocument = doc;
                    IsPlaying = true;
                    IsPaused = false;
                    registered = true;
                }

                output.Play();
                return playbackSession;
            }
            catch
            {
                bool disposeLocally = !registered;
                lock (_stateLock)
                {
                    if (registered && ReferenceEquals(_out, output))
                    {
                        ClearState();
                        disposeLocally = true;
                    }
                    else if (!registered)
                    {
                        Master.ClearSource();
                    }
                }

                if (disposeLocally)
                {
                    if (output != null && handler != null)
                        output.PlaybackStopped -= handler;
                    DisposeOutput(output, device, stopFirst: true);
                }
                throw;
            }
        }
    }

    public void Pause()
    {
        lock (_controlLock)
        {
            WasapiOut? output;
            lock (_stateLock)
            {
                if (_out == null || !IsPlaying) return;
                output = _out;
            }
            output.Pause();
            lock (_stateLock)
            {
                if (!ReferenceEquals(_out, output)) return;
                IsPlaying = false;
                IsPaused = true;
            }
        }
    }

    public void Resume()
    {
        lock (_controlLock)
        {
            WasapiOut? output;
            lock (_stateLock)
            {
                if (_out == null || !IsPaused) return;
                output = _out;
            }
            output.Play();
            lock (_stateLock)
            {
                if (!ReferenceEquals(_out, output)) return;
                IsPlaying = true;
                IsPaused = false;
            }
        }
    }

    public void Stop()
    {
        lock (_controlLock) StopCore();
    }

    private void StopCore()
    {
        WasapiOut? output;
        MMDevice? device;
        EventHandler<StoppedEventArgs>? handler;
        lock (_stateLock)
        {
            output = _out;
            device = _outDevice;
            handler = _playbackStoppedHandler;
            ClearState();
        }

        if (output != null && handler != null)
            output.PlaybackStopped -= handler;
        DisposeOutput(output, device, stopFirst: true);
    }

    private void OnPlaybackStopped(
        WasapiOut output,
        MMDevice? device,
        DocumentProvider provider,
        EventHandler<StoppedEventArgs> handler,
        long playbackSession,
        AudioDocument document)
    {
        int position;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_out, output)) return;
            position = provider.PositionSamples;
            ClearState();
        }

        output.PlaybackStopped -= handler;
        // Some output implementations raise PlaybackStopped on their playback
        // thread. Dispose elsewhere so cleanup cannot join the current thread.
        _ = Task.Run(() => DisposeOutput(output, device, stopFirst: false));
        PlaybackStopped?.Invoke(playbackSession, document, position);
    }

    /// <summary>Must be called while holding <see cref="_stateLock"/>.</summary>
    private void ClearState()
    {
        _out = null;
        _outDevice = null;
        _provider = null;
        _playbackStoppedHandler = null;
        IsPlaying = false;
        IsPaused = false;
        SourceDocument = null;
        Master.ClearSource();
    }

    private static void DisposeOutput(WasapiOut? output, MMDevice? device, bool stopFirst)
    {
        if (output != null)
        {
            if (stopFirst)
            {
                try { output.Stop(); } catch { }
            }
            try { output.Dispose(); } catch { }
        }
        try { device?.Dispose(); } catch { }
    }

    public void Dispose() => Stop();

    private sealed class DocumentProvider : ISampleProvider
    {
        private readonly AudioDocument _doc;
        private readonly int _start;
        private readonly int _end;
        private readonly bool _expandMonoToStereo;
        private int _preRollFrames;
        private int _pos;

        public DocumentProvider(AudioDocument doc, int start, int? end, bool expandMonoToStereo)
        {
            _doc = doc;
            _start = Math.Clamp(start, 0, Math.Max(0, doc.Length - 1));
            _end = Math.Clamp(end ?? doc.Length, _start, doc.Length);
            _expandMonoToStereo = expandMonoToStereo && doc.ChannelCount == 1;
            _pos = _start;
            // Give WASAPI and the device a silent lead-in before the first signal.
            // Resuming a paused stream does not pass through this path again.
            _preRollFrames = Math.Max(1, doc.SampleRate / 50); // 20 ms
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                doc.SampleRate, _expandMonoToStereo ? 2 : doc.ChannelCount);
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
                if (_preRollFrames > 0)
                {
                    int silenceFrames = Math.Min(framesWanted, _preRollFrames);
                    Array.Clear(buffer, offset + written * channels, silenceFrames * channels);
                    _preRollFrames -= silenceFrames;
                    written += silenceFrames;
                    framesWanted -= silenceFrames;
                    continue;
                }

                int available = _end - _pos;
                if (available <= 0)
                {
                    if (!Loop) break;
                    _pos = _start;
                    available = _end - _pos;
                    if (available <= 0) break;
                }
                int n = Math.Min(framesWanted, available);
                int destination = offset + written * channels;
                _doc.ReadInterleaved(_pos, n, buffer, destination);
                if (_expandMonoToStereo)
                {
                    // Expand backwards so the mono source and stereo destination
                    // can safely share the caller's buffer without an allocation.
                    for (int frame = n - 1; frame >= 0; frame--)
                    {
                        float sample = buffer[destination + frame];
                        buffer[destination + frame * 2] = sample;
                        buffer[destination + frame * 2 + 1] = sample;
                    }
                }
                _pos += n;
                written += n;
                framesWanted -= n;
            }
            return written * channels;
        }
    }
}
