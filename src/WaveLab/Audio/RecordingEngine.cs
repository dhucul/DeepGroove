using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace WaveLab.Audio;

public sealed record RecordingStoppedInfo(Exception? Error, bool CapacityReached);

/// <summary>WASAPI capture into memory; produces a new AudioDocument on stop.</summary>
public sealed class RecordingEngine : IDisposable
{
    // Flattening temporarily needs a second copy of the captured samples. A
    // 768 MiB source cap bounds finalization near 1.5 GiB while retaining roughly
    // 35 minutes of stereo 48 kHz audio—long enough for unusually long LP sides.
    private const long MaxCaptureBytes = 768L * 1024 * 1024;

    private WasapiCapture? _capture;
    private readonly List<float[]> _blocks = [];
    private int _channels = 2, _sampleRate = 48000;
    private long _totalSamples;
    private bool _capacityReached;
    private TaskCompletionSource<StoppedEventArgs>? _recordingStopped;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly SemaphoreSlim _finalizeGate = new(1, 1);
    private CaptureSnapshot? _pendingSnapshot;

    private sealed record CaptureSnapshot(float[][] Blocks, long TotalSamples, int Channels, int SampleRate, DateTime StoppedAt);

    public bool IsRecording { get; private set; }
    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public double RecordedSeconds => _sampleRate > 0 ? (double)_totalSamples / _channels / _sampleRate : 0;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public Exception? LastStopError { get; private set; }
    public bool CapacityReached => _capacityReached;
    public bool HasPendingCapture { get { lock (_blocks) return _pendingSnapshot != null; } }
    public event Action<RecordingStoppedInfo>? CaptureStopped;

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
        LastStopError = null;
        PeakL = PeakR = 0;

        _capture.DataAvailable += OnData;
        _recordingStopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.RecordingStopped += OnRecordingStopped;
        try
        {
            // Publish the running state before asking the driver to start. A
            // device is allowed to stop immediately, and its callback must win
            // that race rather than being overwritten after StartRecording.
            IsRecording = true;
            _capture.StartRecording();
        }
        catch
        {
            // Start can fail after the endpoint and event graph are initialized.
            // Leave no hidden capture behind (notably when Record Setup closes
            // with neither IsRecording nor a pending buffer set).
            IsRecording = false;
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { }
            _capture = null;
            _recordingStopped = null;
            throw;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // A discarded WASAPI capture can already have queued this callback on
        // WPF's synchronization context. Never let that stale notification stop
        // or finalize a newer capture owned by this engine.
        if (!ReferenceEquals(sender, _capture)) return;
        IsRecording = false;
        LastStopError = e.Exception;
        _recordingStopped?.TrySetResult(e);
        CaptureStopped?.Invoke(new RecordingStoppedInfo(e.Exception, _capacityReached));
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capacityReached) return;

        // WASAPI shared-mode capture delivers 32-bit float (validated in Start).
        // Only retain complete sample frames if a driver supplies a partial tail.
        int samples = (e.BytesRecorded / sizeof(float) / _channels) * _channels;
        if (samples <= 0) return;

        float[]? block = null;
        lock (_blocks)
        {
            long maxSamples = MaxCaptureBytes / sizeof(float);
            if (_totalSamples > maxSamples - samples)
            {
                _capacityReached = true;
            }
            else
            {
                block = new float[samples];
                Buffer.BlockCopy(e.Buffer, 0, block, 0, samples * sizeof(float));
                _blocks.Add(block);
                _totalSamples += samples;
            }
        }

        if (block == null)
        {
            // Stop capturing but keep the buffered audio for finalization.
            var capture = _capture;
            Task.Run(() => { try { capture?.StopRecording(); } catch { } });
            return;
        }

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

    /// <summary>
    /// Stop capture immediately, then flatten the buffered interleaved blocks on
    /// a worker thread so long recordings never stall the UI.
    /// </summary>
    public async Task<AudioDocument?> StopAndGetDocumentAsync(CancellationToken cancellationToken = default)
    {
        await _finalizeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await StopAndSnapshotAsync().ConfigureAwait(false);
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

    private async Task<CaptureSnapshot?> StopAndSnapshotAsync()
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var capture = _capture;
            if (capture == null)
            {
                lock (_blocks) return _pendingSnapshot;
            }
            var stopped = _recordingStopped;
            if (stopped is { Task.IsCompleted: false })
            {
                try { capture.StopRecording(); }
                catch (Exception ex) { stopped.TrySetResult(new StoppedEventArgs(ex)); }
            }

            if (stopped != null)
            {
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
            }

            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            if (ReferenceEquals(_capture, capture)) _capture = null;
            _recordingStopped = null;
            IsRecording = false;

            lock (_blocks)
            {
                var snapshot = new CaptureSnapshot(_blocks.ToArray(), _totalSamples, _channels, _sampleRate, DateTime.Now);
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
                var capture = _capture;
                if (capture != null)
                {
                    capture.DataAvailable -= OnData;
                    capture.RecordingStopped -= OnRecordingStopped;
                    try { capture.StopRecording(); } catch { }
                    try { capture.Dispose(); } catch { }
                    if (ReferenceEquals(_capture, capture)) _capture = null;
                }

                _recordingStopped = null;
                IsRecording = false;
                lock (_blocks)
                {
                    _blocks.Clear();
                    _pendingSnapshot = null;
                    _totalSamples = 0;
                }
                PeakL = PeakR = 0;
                _capacityReached = false;
                LastStopError = null;
            }
            finally { _stopGate.Release(); }
        }
        finally { _finalizeGate.Release(); }
    }

    public void Dispose() => Stop();
}
