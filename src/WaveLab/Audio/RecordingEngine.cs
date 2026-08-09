using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace WaveLab.Audio;

public sealed record RecordingStoppedInfo(Exception? Error, bool CapacityReached, long SessionId = 0);

/// <summary>WASAPI capture into memory; produces a new AudioDocument on stop.</summary>
public sealed class RecordingEngine : IDisposable
{
    // Flattening temporarily needs a second copy of the captured samples. A
    // 768 MiB source cap bounds finalization near 1.5 GiB while retaining roughly
    // 35 minutes of stereo 48 kHz audio—long enough for unusually long LP sides.
    private const long MaxCaptureBytes = 768L * 1024 * 1024;

    private CaptureSession? _session;
    private readonly object _sessionLock = new();
    private readonly List<float[]> _blocks = [];
    private int _channels = 2, _sampleRate = 48000;
    private long _totalSamples;
    private bool _capacityReached;
    private bool _isRecording;
    private float _peakL;
    private float _peakR;
    private Exception? _lastStopError;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly SemaphoreSlim _finalizeGate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private CaptureSnapshot? _pendingSnapshot;
    private int _activeFinalizations;
    private int _disposed;
    private long _nextSessionId;

    private sealed class CaptureSession(
        long id,
        WasapiCapture capture,
        TaskCompletionSource<StoppedEventArgs> stopped)
    {
        public long Id { get; } = id;
        public WasapiCapture Capture { get; } = capture;
        public TaskCompletionSource<StoppedEventArgs> Stopped { get; } = stopped;
        /// <summary>Access only while holding the owning engine's session lock.</summary>
        public bool AcceptCallbacks { get; set; } = true;
    }

    private sealed record CaptureSnapshot(
        long SessionId,
        float[][] Blocks,
        long TotalSamples,
        int Channels,
        int SampleRate,
        DateTime StoppedAt);

    public bool IsRecording
    {
        get => Volatile.Read(ref _isRecording);
        private set => Volatile.Write(ref _isRecording, value);
    }
    public float PeakL
    {
        get => Volatile.Read(ref _peakL);
        private set => Volatile.Write(ref _peakL, value);
    }
    public float PeakR
    {
        get => Volatile.Read(ref _peakR);
        private set => Volatile.Write(ref _peakR, value);
    }
    public double RecordedSeconds
    {
        get
        {
            int channels = Volatile.Read(ref _channels);
            int sampleRate = Volatile.Read(ref _sampleRate);
            return channels > 0 && sampleRate > 0
                ? (double)Interlocked.Read(ref _totalSamples) / channels / sampleRate
                : 0;
        }
    }
    public int SampleRate => Volatile.Read(ref _sampleRate);
    public int Channels => Volatile.Read(ref _channels);
    public Exception? LastStopError
    {
        get => Volatile.Read(ref _lastStopError);
        private set => Volatile.Write(ref _lastStopError, value);
    }
    public bool CapacityReached => Volatile.Read(ref _capacityReached);
    public bool HasPendingCapture { get { lock (_blocks) return _pendingSnapshot != null; } }
    public event Action<RecordingStoppedInfo>? CaptureStopped;

    /// <summary>
    /// True while <paramref name="sessionId"/> still identifies the session
    /// owned by this engine, including the interval after its stop callback and
    /// before buffered audio is finalized. Event consumers use this to reject a
    /// notification that was delayed until after a replacement was published.
    /// </summary>
    public bool IsCurrentSession(long sessionId)
    {
        if (sessionId == 0) return false;
        lock (_sessionLock) return _session?.Id == sessionId;
    }

    private CaptureSession? GetCurrentSession()
    {
        lock (_sessionLock) return _session;
    }

    private CaptureSession? GetSessionFor(object? sender)
    {
        lock (_sessionLock)
        {
            return _session is { AcceptCallbacks: true } && ReferenceEquals(sender, _session.Capture)
                ? _session
                : null;
        }
    }

    private bool IsCurrentSession(CaptureSession session)
    {
        lock (_sessionLock)
            return ReferenceEquals(_session, session) && session.AcceptCallbacks;
    }

    private void PublishSession(CaptureSession session)
    {
        lock (_sessionLock)
        {
            if (_session != null)
                throw new InvalidOperationException("A recording session is already active.");
            _session = session;
            IsRecording = true;
        }
    }

    private bool DetachSession(CaptureSession session)
    {
        lock (_sessionLock)
        {
            if (!ReferenceEquals(_session, session)) return false;
            session.AcceptCallbacks = false;
            _session = null;
            IsRecording = false;
            return true;
        }
    }

    private bool DeactivateSession(CaptureSession session)
    {
        lock (_sessionLock)
        {
            if (!ReferenceEquals(_session, session)) return false;
            session.AcceptCallbacks = false;
            IsRecording = false;
            return true;
        }
    }

    private CaptureSession? DetachCurrentSession()
    {
        lock (_sessionLock)
        {
            CaptureSession? session = _session;
            if (session != null) session.AcceptCallbacks = false;
            _session = null;
            IsRecording = false;
            return session;
        }
    }

    public static List<(string Id, string Name)> GetCaptureDevices()
    {
        var result = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (dev)
                result.Add((dev.ID, dev.FriendlyName));
        }
        return result;
    }

    public long Start(string? deviceId)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return StartCore(deviceId);
        }
    }

    private long StartCore(string? deviceId)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = deviceId == null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);

        var capture = new WasapiCapture(device);
        CaptureSession? session = null;
        try
        {
            WaveFormat format = capture.WaveFormat;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (format is WaveFormatExtensible ext &&
                            ext.SubFormat == AudioSubtypes.MFAudioFormat_Float);
            if (!isFloat)
            {
                throw new NotSupportedException(
                    $"Unsupported capture format ({device.FriendlyName}) — expected 32-bit float shared-mode mix.");
            }
            Volatile.Write(ref _channels, format.Channels);
            Volatile.Write(ref _sampleRate, format.SampleRate);
            _blocks.Clear();
            Interlocked.Exchange(ref _totalSamples, 0);
            Volatile.Write(ref _capacityReached, false);
            LastStopError = null;
            PeakL = PeakR = 0;

            session = new CaptureSession(
                Interlocked.Increment(ref _nextSessionId),
                capture,
                new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnRecordingStopped;

            // Publish the running state before asking the driver to start. A
            // device is allowed to stop immediately, and its callback must win
            // that race rather than being overwritten after StartRecording.
            PublishSession(session);
            capture.StartRecording();
            return session.Id;
        }
        catch
        {
            // Start can fail after the endpoint and event graph are initialized.
            // Leave no hidden capture behind (notably when Record Setup closes
            // with neither IsRecording nor a pending buffer set).
            if (session != null) DetachSession(session);
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.Dispose(); } catch { }
            throw;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // A discarded WASAPI capture can already have queued this callback on
        // WPF's synchronization context. Never let that stale notification stop
        // or finalize a newer capture owned by this engine.
        RecordingStoppedInfo info;
        lock (_sessionLock)
        {
            CaptureSession? session = _session;
            if (session is not { AcceptCallbacks: true } || !ReferenceEquals(sender, session.Capture)) return;
            IsRecording = false;
            LastStopError = e.Exception;
            session.Stopped.TrySetResult(e);
            info = new RecordingStoppedInfo(e.Exception, CapacityReached, session.Id);
        }
        RaiseCaptureStopped(info);
    }

    private void RaiseCaptureStopped(RecordingStoppedInfo info)
    {
        var handlers = CaptureStopped;
        if (handlers == null) return;
        foreach (Action<RecordingStoppedInfo> handler in handlers.GetInvocationList())
        {
            try { handler(info); }
            catch { /* A subscriber must not terminate NAudio's callback thread. */ }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        CaptureSession? session = GetSessionFor(sender);
        if (session == null || CapacityReached) return;

        int channels = Volatile.Read(ref _channels);
        if (channels <= 0) return;

        // WASAPI shared-mode capture delivers 32-bit float (validated in Start).
        // Only retain complete sample frames if a driver supplies a partial tail.
        int samples = (e.BytesRecorded / sizeof(float) / channels) * channels;
        if (samples <= 0) return;

        float[]? block = null;
        lock (_blocks)
        {
            // Unsubscribing does not revoke a delegate invocation that was
            // already queued. Recheck under the mutation lock before allowing
            // an old capture to append into a newer session's block list.
            if (!IsCurrentSession(session) || CapacityReached) return;

            long maxSamples = MaxCaptureBytes / sizeof(float);
            if (Interlocked.Read(ref _totalSamples) > maxSamples - samples)
            {
                Volatile.Write(ref _capacityReached, true);
            }
            else
            {
                block = new float[samples];
                Buffer.BlockCopy(e.Buffer, 0, block, 0, samples * sizeof(float));
                _blocks.Add(block);
                Interlocked.Add(ref _totalSamples, samples);
            }
        }

        if (block == null)
        {
            // Stop capturing but keep the buffered audio for finalization.
            _ = Task.Run(() => { try { session.Capture.StopRecording(); } catch { } });
            return;
        }

        float pl = 0, pr = 0;
        for (int i = 0; i < samples; i += channels)
        {
            float al = Math.Abs(block[i]);
            if (al > pl) pl = al;
            if (channels > 1)
            {
                float ar = Math.Abs(block[i + 1]);
                if (ar > pr) pr = ar;
            }
        }
        if (!IsCurrentSession(session)) return;
        PeakL = pl;
        PeakR = channels > 1 ? pr : pl;
    }

    /// <summary>
    /// Stop capture immediately, then flatten the buffered interleaved blocks on
    /// a worker thread so long recordings never stall the UI.
    /// </summary>
    public Task<AudioDocument?> StopAndGetDocumentAsync(CancellationToken cancellationToken = default) =>
        StopAndGetDocumentCoreAsync(expectedSessionId: null, cancellationToken);

    /// <summary>
    /// Finalize only the named recording session. A delayed stop notification
    /// therefore cannot stop or consume a newer capture published by Start.
    /// </summary>
    public Task<AudioDocument?> StopSessionAndGetDocumentAsync(
        long sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId <= 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
        return StopAndGetDocumentCoreAsync(sessionId, cancellationToken);
    }

    private async Task<AudioDocument?> StopAndGetDocumentCoreAsync(
        long? expectedSessionId,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _activeFinalizations++;
        }
        try
        {
            await _finalizeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await StopAndSnapshotAsync(expectedSessionId).ConfigureAwait(false);
                if (snapshot == null) return null;

                // Do not clear the pending snapshot until construction succeeds. An
                // allocation failure or cancellation can therefore be retried without
                // losing the audio blocks that were already captured.
                var document = await Task.Run<AudioDocument?>(
                        () => BuildDocument(snapshot, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                lock (_blocks)
                {
                    if (ReferenceEquals(_pendingSnapshot, snapshot))
                        _pendingSnapshot = null;
                }
                return document;
            }
            finally { _finalizeGate.Release(); }
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _activeFinalizations--;
                if (_activeFinalizations == 0) Monitor.PulseAll(_lifecycleLock);
            }
        }
    }

    private async Task<CaptureSnapshot?> StopAndSnapshotAsync(long? expectedSessionId)
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CaptureSession? session = GetCurrentSession();
            if (session == null)
            {
                lock (_blocks)
                {
                    return expectedSessionId is null || _pendingSnapshot?.SessionId == expectedSessionId
                        ? _pendingSnapshot
                        : null;
                }
            }
            if (expectedSessionId is long expected && session.Id != expected)
                return null;
            WasapiCapture capture = session.Capture;
            TaskCompletionSource<StoppedEventArgs> stopped = session.Stopped;
            if (stopped is { Task.IsCompleted: false })
            {
                try { capture.StopRecording(); }
                catch (Exception ex) { stopped.TrySetResult(new StoppedEventArgs(ex)); }
            }

            try
            {
                var stoppedArgs = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                LastStopError = stoppedArgs.Exception;
            }
            catch (TimeoutException)
            {
                // Preserve everything received so far even if a driver never
                // acknowledges StopRecording; disposal is the final fallback.
            }

            // Disable this session before unhook/dispose. Delegates already
            // queued by NAudio can still run after event unsubscription.
            DeactivateSession(session);
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            DetachSession(session);

            lock (_blocks)
            {
                var snapshot = new CaptureSnapshot(
                    session.Id,
                    _blocks.ToArray(),
                    Interlocked.Read(ref _totalSamples),
                    Volatile.Read(ref _channels),
                    Volatile.Read(ref _sampleRate),
                    DateTime.Now);
                _blocks.Clear();
                _pendingSnapshot = snapshot;
                return snapshot;
            }
        }
        finally { _stopGate.Release(); }
    }

    private static AudioDocument? BuildDocument(CaptureSnapshot snapshot, CancellationToken cancellationToken)
    {
        int frames = checked((int)(snapshot.TotalSamples / snapshot.Channels));
        if (frames == 0) return null;
        var channels = new float[snapshot.Channels][];
        for (int c = 0; c < channels.Length; c++) channels[c] = new float[frames];

        int frame = 0, channelIndex = 0;
        foreach (var block in snapshot.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (float sample in block)
            {
                if (frame >= frames) break;
                channels[channelIndex][frame] = sample;
                if (++channelIndex == snapshot.Channels) { channelIndex = 0; frame++; }
            }
        }
        var document = new AudioDocument(channels, snapshot.SampleRate, 32)
        {
            Title = $"Recording {snapshot.StoppedAt:HH-mm-ss}.wav",
        };
        document.MarkUnsaved();
        return document;
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            StopCore();
        }
    }

    private void StopCore()
    {
        _finalizeGate.Wait();
        try
        {
            _stopGate.Wait();
            try
            {
                // This is the explicit discard path. WasapiCapture posts its
                // RecordingStopped event back to the synchronization context on
                // which it was created (normally WPF's UI thread), so blocking
                // that thread waiting for the event would force the 5 s timeout.
                // Dispose requests stop and joins NAudio's capture thread without
                // requiring the posted callback to run.
                CaptureSession? session = DetachCurrentSession();
                if (session != null)
                {
                    WasapiCapture capture = session.Capture;
                    capture.DataAvailable -= OnData;
                    capture.RecordingStopped -= OnRecordingStopped;
                    try { capture.StopRecording(); } catch { }
                    try { capture.Dispose(); } catch { }
                }

                lock (_blocks)
                {
                    _blocks.Clear();
                    _pendingSnapshot = null;
                    Interlocked.Exchange(ref _totalSamples, 0);
                }
                PeakL = PeakR = 0;
                Volatile.Write(ref _capacityReached, false);
                LastStopError = null;
            }
            finally { _stopGate.Release(); }
        }
        finally { _finalizeGate.Release(); }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            while (_activeFinalizations != 0)
                Monitor.Wait(_lifecycleLock);
            try
            {
                StopCore();
            }
            finally
            {
                CaptureStopped = null;
                try { _stopGate.Dispose(); }
                finally { _finalizeGate.Dispose(); }
            }
        }
    }
}
