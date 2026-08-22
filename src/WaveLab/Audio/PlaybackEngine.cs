using System.Diagnostics;
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
    private readonly object _cleanupLock = new();
    private readonly List<Task> _pendingCleanupTasks = [];
    /// <summary>Upper bound on how long a UI-thread Play/Stop may wait for endpoint teardown.</summary>
    private static readonly TimeSpan CleanupDrainTimeout = TimeSpan.FromSeconds(2);
    private long _positionClockAccumulatedTicks;
    private long _positionClockStartedAt;
    private bool _positionClockRunning;
    [ThreadStatic] private static int _playbackCallbackDepth;

    public MasterSection Master { get; } = new();
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public AudioDocument? SourceDocument { get; private set; }
    public Exception? LastPlaybackError { get; private set; }
    public bool Loop { get; set; }

    public event Action<long, AudioDocument, int>? PlaybackStopped;
    public event Action<long, AudioDocument, Exception>? PlaybackFailed;

    public int PositionSamples
    {
        get
        {
            lock (_stateLock)
            {
                if (_provider == null) return 0;
                long timelineFrames = PositionClockFrames(_provider.WaveFormat.SampleRate);
                int position = CalculatePresentedPosition(
                    _provider.StartSample,
                    _provider.EndSample,
                    _provider.InitialPreRollFrames,
                    timelineFrames,
                    _provider.WaveFormat.SampleRate,
                    _provider.WaveFormat.SampleRate,
                    _provider.Loop);

                // Reads normally stay one device buffer ahead of the monotonic
                // timeline. Capping non-looping playback prevents the UI from
                // outrunning audio if decoding or an effect temporarily stalls.
                return _provider.Loop
                    ? position
                    : Math.Min(position, _provider.PositionSamples);
            }
        }
    }

    /// <summary>Must be called while holding <see cref="_stateLock"/>.</summary>
    private long PositionClockFrames(int sampleRate)
    {
        long ticks = _positionClockAccumulatedTicks;
        if (_positionClockRunning)
            ticks += Math.Max(0, Stopwatch.GetTimestamp() - _positionClockStartedAt);
        double frames = ticks * (double)sampleRate / Stopwatch.Frequency;
        return frames >= long.MaxValue ? long.MaxValue : (long)Math.Floor(frames);
    }

    /// <summary>Must be called while holding <see cref="_stateLock"/>.</summary>
    private void StartPositionClock()
    {
        _positionClockStartedAt = Stopwatch.GetTimestamp();
        _positionClockRunning = true;
    }

    /// <summary>Must be called while holding <see cref="_stateLock"/>.</summary>
    private void FreezePositionClock()
    {
        if (!_positionClockRunning) return;
        _positionClockAccumulatedTicks += Math.Max(
            0, Stopwatch.GetTimestamp() - _positionClockStartedAt);
        _positionClockRunning = false;
    }

    internal static int CalculatePresentedPosition(
        int start,
        int end,
        int preRollFrames,
        long outputFrames,
        int outputSampleRate,
        int sourceSampleRate,
        bool loop)
    {
        if (end <= start || outputSampleRate <= 0 || sourceSampleRate <= 0) return start;

        double convertedFrames = Math.Floor(
            Math.Max(0, outputFrames) * (double)sourceSampleRate / outputSampleRate);
        long sourceFrames = convertedFrames >= long.MaxValue
            ? long.MaxValue
            : (long)convertedFrames;
        long elapsed = Math.Max(0, sourceFrames - Math.Max(0, preRollFrames));
        long span = (long)end - start;
        if (loop) elapsed %= span;
        return (int)Math.Clamp((long)start + elapsed, start, end);
    }

    public static List<(string Id, string Name)> GetOutputDevices()
    {
        var result = new List<(string, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                    result.Add((dev.ID, dev.FriendlyName));
            }
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
            Role role = AudioHardwareOptions.ParseRole(
                AppSettings.Instance.OutputDefaultRole, Role.Multimedia);
            using var dev = id != null ? enumerator.GetDevice(id)
                                       : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            return dev.FriendlyName;
        }
        catch { return "Default output"; }
    }

    private static (WasapiOut Output, MMDevice? Device) CreateOut()
    {
        var settings = AppSettings.Instance;
        int latency = Math.Clamp(settings.BufferMs, 3, 500);
        var shareMode = AudioHardwareOptions.ParseShareMode(settings.OutputShareMode);
        Role role = AudioHardwareOptions.ParseRole(settings.OutputDefaultRole, Role.Multimedia);
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = settings.OutputDeviceId != null
                ? enumerator.GetDevice(settings.OutputDeviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            return (new WasapiOut(device, shareMode, settings.OutputEventSync, latency), device);
        }
        catch
        {
            try { device?.Dispose(); } catch { }
            // A saved endpoint may have disappeared. Preserve the chosen engine
            // mode but retry on the selected Windows default role.
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            try
            {
                return (new WasapiOut(device, shareMode, settings.OutputEventSync, latency), device);
            }
            catch
            {
                try { device.Dispose(); } catch { }
                throw;
            }
        }
    }

    public long Play(AudioDocument doc, int startSample, int? endSample)
    {
        ArgumentNullException.ThrowIfNull(doc);
        DrainPendingCleanups();
        lock (_controlLock)
        {
            StopCore();
            lock (_stateLock) LastPlaybackError = null;
            long playbackSession = ++_nextPlaybackSession;
            var provider = new DocumentProvider(
                doc, startSample, endSample,
                expandMonoToStereo: Master.ExpandsMonoToStereo)
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

                handler = (_, args) => OnPlaybackStopped(
                    output, device, provider, handler!, playbackSession, doc, args.Exception);
                output.PlaybackStopped += handler;
                lock (_stateLock)
                {
                    _out = output;
                    _outDevice = device;
                    _provider = provider;
                    _playbackStoppedHandler = handler;
                    SourceDocument = doc;
                    LastPlaybackError = null;
                    IsPlaying = true;
                    IsPaused = false;
                    _positionClockAccumulatedTicks = 0;
                    _positionClockRunning = false;
                    registered = true;
                }

                output.Play();
                lock (_stateLock)
                {
                    if (ReferenceEquals(_out, output)) StartPositionClock();
                }
                return playbackSession;
            }
            catch (Exception error)
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
                    LastPlaybackError = error;
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
                FreezePositionClock();
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
                StartPositionClock();
                IsPlaying = true;
                IsPaused = false;
            }
        }
    }

    public void Stop()
    {
        lock (_controlLock) StopCore();
        DrainPendingCleanups();
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
        AudioDocument document,
        Exception? error)
    {
        int position;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_out, output)) return;
            position = provider.PositionSamples;
            ClearState();
            LastPlaybackError = error;
        }

        output.PlaybackStopped -= handler;
        // Some output implementations raise PlaybackStopped on their playback
        // thread. Dispose elsewhere so cleanup cannot join the current thread.
        QueueOutputCleanup(output, device);
        _playbackCallbackDepth++;
        try
        {
            InvokePlaybackStopped(playbackSession, document, position);
            if (error != null)
                InvokePlaybackFailed(playbackSession, document, error);
        }
        finally
        {
            _playbackCallbackDepth--;
        }
    }

    private void QueueOutputCleanup(WasapiOut output, MMDevice? device)
    {
        Task cleanup = Task.Run(() => DisposeOutput(output, device, stopFirst: false));
        lock (_cleanupLock) _pendingCleanupTasks.Add(cleanup);
    }

    private void DrainPendingCleanups()
    {
        // An event subscriber can synchronously call Stop/Dispose from WASAPI's
        // callback thread. Waiting there would deadlock with WasapiOut.Dispose,
        // which may join that same thread. A later non-callback Stop/Dispose drains it.
        if (_playbackCallbackDepth > 0) return;

        while (true)
        {
            Task[] pending;
            lock (_cleanupLock)
            {
                if (_pendingCleanupTasks.Count == 0) return;
                pending = [.. _pendingCleanupTasks];
                _pendingCleanupTasks.Clear();
            }

            bool completed;
            // Bounded: Play() and Stop() reach this from the UI thread, and the
            // queued teardown (WasapiOut.Dispose joins the render thread) is at the
            // driver's mercy once an endpoint has been invalidated.
            try { completed = Task.WhenAll(pending).Wait(CleanupDrainTimeout); }
            catch { completed = true; /* DisposeOutput is best-effort; cleanup must not block shutdown. */ }
            if (completed) continue;

            // Re-queue whatever is still running so a later Stop/Dispose drains it.
            lock (_cleanupLock)
            {
                foreach (Task task in pending)
                {
                    if (!task.IsCompleted) _pendingCleanupTasks.Add(task);
                }
            }
            return;
        }
    }

    private void InvokePlaybackStopped(long session, AudioDocument document, int position)
    {
        var handlers = PlaybackStopped;
        if (handlers == null) return;
        foreach (Action<long, AudioDocument, int> handler in handlers.GetInvocationList())
        {
            try { handler(session, document, position); }
            catch { /* A subscriber must not crash the WASAPI callback thread. */ }
        }
    }

    private void InvokePlaybackFailed(long session, AudioDocument document, Exception error)
    {
        var handlers = PlaybackFailed;
        if (handlers == null) return;
        foreach (Action<long, AudioDocument, Exception> handler in handlers.GetInvocationList())
        {
            try { handler(session, document, error); }
            catch { /* A subscriber must not crash the WASAPI callback thread. */ }
        }
    }

    /// <summary>Must be called while holding <see cref="_stateLock"/>.</summary>
    private void ClearState()
    {
        _out = null;
        _outDevice = null;
        _provider = null;
        _playbackStoppedHandler = null;
        _positionClockAccumulatedTicks = 0;
        _positionClockStartedAt = 0;
        _positionClockRunning = false;
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

    /// <summary>
    /// Apply a document's playback-only monitor gain to frames already written into the output
    /// buffer, hard-limited to full scale.
    /// </summary>
    /// <param name="limit">
    /// Clamp even at unity gain. Load-bearing for a residual, and measured rather than assumed:
    /// what click repair removes is a click that stood above the local level, so it comes out
    /// <b>louder than the record it came from</b> — one measured here peaks at +6.7 dBFS against a
    /// programme peak of −7.8. That is exactly the case the lift rule correctly leaves at unity, so
    /// gating the limit on "the gain is not one" would skip it precisely where it is needed, and
    /// send a full-scale-and-then-some click to the speakers of somebody about to hear it for the
    /// first time. Ordinary documents pass through untouched, as they always have.
    /// </param>
    internal static void ApplyMonitorGain(float[] buffer, int offset, int count, float gain, bool limit)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0 || (gain == 1f && !limit)) return;
        int end = offset + count;
        for (int i = offset; i < end; i++)
            buffer[i] = Math.Clamp(buffer[i] * gain, -1f, 1f);
    }

    private sealed class DocumentProvider : ISampleProvider
    {
        private readonly AudioDocument _document;
        private readonly float[][] _channels;
        private readonly int _start;
        private readonly int _end;
        private readonly bool _expandMonoToStereo;
        private readonly int _initialPreRollFrames;
        private int _preRollFrames;
        private int _pos;

        public DocumentProvider(AudioDocument doc, int start, int? end, bool expandMonoToStereo)
        {
            _document = doc;
            _channels = doc.Channels.ToArray();
            if (_channels.Length == 0)
                throw new InvalidOperationException("Cannot play a document with no audio channels.");
            int length = _channels[0].Length;
            if (_channels.Any(channel => channel.Length != length))
                throw new InvalidOperationException("Cannot play a document whose channel lengths differ.");

            _start = Math.Clamp(start, 0, Math.Max(0, length - 1));
            _end = Math.Clamp(end ?? length, _start, length);
            _expandMonoToStereo = expandMonoToStereo && _channels.Length == 1;
            _pos = _start;
            // Give WASAPI and the device a silent lead-in before the first signal.
            // Resuming a paused stream does not pass through this path again.
            _initialPreRollFrames = Math.Max(1, doc.SampleRate / 50); // 20 ms
            _preRollFrames = _initialPreRollFrames;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                doc.SampleRate, _expandMonoToStereo ? 2 : _channels.Length);
        }

        public bool Loop { get; set; }
        public WaveFormat WaveFormat { get; }
        public int StartSample => _start;
        public int EndSample => _end;
        public int InitialPreRollFrames => _initialPreRollFrames;
        public int PositionSamples => Volatile.Read(ref _pos);

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int framesWanted = count / channels;
            int written = 0;
            // Read once per callback so one buffer is never half at the old gain and half
            // at the new one when the monitor slider moves under it.
            float monitorGain = _document.MonitorGain;
            bool limitToFullScale = _document.IsResidual;

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
                    Volatile.Write(ref _pos, _start);
                    available = _end - _pos;
                    if (available <= 0) break;
                }
                int n = Math.Min(framesWanted, available);
                int destination = offset + written * channels;
                ReadSnapshotInterleaved(_pos, n, buffer, destination);
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
                // After the mono expansion, so both copies are lifted, and outside the
                // pre-roll above, which stays silent.
                ApplyMonitorGain(buffer, destination, n * channels, monitorGain, limitToFullScale);
                // Published, to match the Volatile.Read in PositionSamples: the UI polls
                // this while the render thread is inside Read.
                Volatile.Write(ref _pos, _pos + n);
                written += n;
                framesWanted -= n;
            }
            return written * channels;
        }

        private void ReadSnapshotInterleaved(int start, int frames, float[] destination, int offset)
        {
            int sourceChannels = _channels.Length;
            for (int frame = 0; frame < frames; frame++)
            {
                int sourceFrame = start + frame;
                for (int channel = 0; channel < sourceChannels; channel++)
                    destination[offset + frame * sourceChannels + channel] = _channels[channel][sourceFrame];
            }
        }
    }
}
