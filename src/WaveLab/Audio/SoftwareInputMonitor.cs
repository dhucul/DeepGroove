using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WaveLab.Util;

namespace WaveLab.Audio;

/// <summary>
/// Bounded, low-latency software playthrough from the active capture stream to
/// the configured output endpoint. Disabled by default to avoid acoustic feedback.
/// </summary>
internal sealed class SoftwareInputMonitor : IDisposable
{
    private static readonly TimeSpan MaximumBufferedDuration = TimeSpan.FromMilliseconds(350);
    private readonly object _sync = new();
    private MonitorSession? _session;
    private bool _enabled;
    private string? _lastError;

    public bool Enabled { get { lock (_sync) return _enabled; } }
    public bool IsActive => Volatile.Read(ref _session) != null;
    public string? LastError { get { lock (_sync) return _lastError; } }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _lastError = null;
            if (!enabled) StopSession();
        }
    }

    public void Configure(WaveFormat inputFormat)
    {
        ArgumentNullException.ThrowIfNull(inputFormat);
        lock (_sync)
        {
            StopSession();
            if (!_enabled) return;
            try
            {
                MonitorSession session = MonitorSession.Start(inputFormat, OnSessionStopped);
                // Published for the capture thread, which reads _session without
                // ever taking _sync (this lock is held across device open/teardown).
                Volatile.Write(ref _session, session);
                _lastError = null;
                // With no synchronization context, WasapiOut can report a worker-
                // thread failure before Start returns and before _session is set.
                if (session.TryGetUnexpectedStop(out Exception? error))
                    FailSessionCore(session, error);
            }
            catch (Exception ex)
            {
                _enabled = false;
                _lastError = ex.Message;
                throw new InvalidOperationException(
                    $"Software playthrough could not start: {ex.Message}", ex);
            }
        }
    }

    private void OnSessionStopped(MonitorSession session, Exception? error)
    {
        lock (_sync) FailSessionCore(session, error);
    }

    /// <summary>Call while holding <see cref="_sync"/>.</summary>
    private void FailSessionCore(MonitorSession session, Exception? error)
    {
        if (!ReferenceEquals(Volatile.Read(ref _session), session)) return;
        Volatile.Write(ref _session, null);
        _enabled = false;
        _lastError = error?.Message ?? "The monitoring output stopped unexpectedly.";

        // PlaybackStopped can run on WasapiOut's own worker thread. Disposing on
        // that thread would make WasapiOut.Stop try to join itself.
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try { ((MonitorSession)state!).Dispose(); } catch { }
        }, session);
    }

    /// <summary>
    /// Called from NAudio's capture thread for every buffer. It must never wait on
    /// <see cref="_sync"/>, which is held across WASAPI device open and teardown:
    /// stalling here overruns the endpoint buffer and drops recorded audio.
    /// </summary>
    public void Enqueue(float[] samples, int count)
    {
        MonitorSession? session = Volatile.Read(ref _session);
        if (session == null) return;
        try { session.Enqueue(samples, count); }
        catch (Exception ex)
        {
            // Drop the failed session without blocking, so later capture buffers
            // return immediately, then hand the teardown to a worker.
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session)) return;
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var (monitor, failed, message) = ((SoftwareInputMonitor, MonitorSession, string))state!;
                lock (monitor._sync)
                {
                    monitor._lastError = message;
                    monitor._enabled = false;
                }
                try { failed.Dispose(); } catch { }
            }, (this, session, ex.Message));
        }
    }

    /// <summary>Stops this capture stream but remembers whether monitoring is armed.</summary>
    public void StopStream()
    {
        lock (_sync) StopSession();
    }

    private void StopSession()
    {
        MonitorSession? session = Volatile.Read(ref _session);
        Volatile.Write(ref _session, null);
        if (session == null) return;
        try { session.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            StopSession();
        }
    }

    private sealed class MonitorSession : IDisposable
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _output;
        private readonly BufferedWaveProvider _buffer;
        private readonly Action<MonitorSession, Exception?> _onUnexpectedStop;
        private byte[] _transfer = [];
        private Exception? _unexpectedStopError;
        private int _unexpectedlyStopped;
        private int _stoppingOrDisposed;

        private MonitorSession(
            MMDevice device,
            WasapiOut output,
            BufferedWaveProvider buffer,
            Action<MonitorSession, Exception?> onUnexpectedStop)
        {
            _device = device;
            _output = output;
            _buffer = buffer;
            _onUnexpectedStop = onUnexpectedStop;
            _output.PlaybackStopped += OnPlaybackStopped;
        }

        public static MonitorSession Start(
            WaveFormat inputFormat,
            Action<MonitorSession, Exception?> onUnexpectedStop)
        {
            AppSettings settings = AppSettings.Instance;
            Role role = AudioHardwareOptions.ParseRole(settings.OutputDefaultRole, Role.Multimedia);
            MMDevice? device = null;
            WasapiOut? output = null;
            MonitorSession? session = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                try
                {
                    device = settings.OutputDeviceId != null
                        ? enumerator.GetDevice(settings.OutputDeviceId)
                        : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                }
                catch
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                }

                using AudioClient client = device.AudioClient;
                WaveFormat outputMix = client.MixFormat;
                int outputChannels = Math.Max(1, outputMix.Channels);
                int outputRate = outputMix.SampleRate;

                var buffer = new BufferedWaveProvider(inputFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(700),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                ISampleProvider provider = buffer.ToSampleProvider();
                if (provider.WaveFormat.Channels != outputChannels)
                    provider = new ChannelMappingSampleProvider(provider, outputChannels);
                if (provider.WaveFormat.SampleRate != outputRate)
                    provider = new WdlResamplingSampleProvider(provider, outputRate);

                // Monitoring always uses shared mode so it can coexist with the
                // metronome and other normal desktop audio.
                output = new WasapiOut(
                    device,
                    AudioClientShareMode.Shared,
                     settings.OutputEventSync,
                     Math.Clamp(settings.BufferMs, 3, 500));
                output.Init(provider);
                session = new MonitorSession(device, output, buffer, onUnexpectedStop);
                session.StartOutput();
                return session;
            }
            catch
            {
                if (session != null)
                {
                    try { session.Dispose(); } catch { }
                }
                else
                {
                    if (output != null)
                    {
                        try { output.Stop(); } catch { }
                        try { output.Dispose(); } catch { }
                    }
                    try { device?.Dispose(); } catch { }
                }
                throw;
            }
        }

        private void StartOutput() => _output.Play();

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (Volatile.Read(ref _stoppingOrDisposed) != 0) return;
            Volatile.Write(ref _unexpectedStopError, e.Exception);
            Volatile.Write(ref _unexpectedlyStopped, 1);

            // Never take the owner's lock on WasapiOut's play thread. A concurrent
            // StopStream/Dispose/Configure holds it and calls WasapiOut.Stop, which joins
            // this very thread: the flag check above cannot close that window, because it
            // is passed before the other thread has begun tearing anything down.
            // Configure's own TryGetUnexpectedStop check still sees an early failure
            // synchronously, so nothing is lost by reporting it from elsewhere.
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var (session, error) = ((MonitorSession, Exception?))state!;
                if (Volatile.Read(ref session._stoppingOrDisposed) != 0) return;
                session._onUnexpectedStop(session, error);
            }, (this, e.Exception));
        }

        public bool TryGetUnexpectedStop(out Exception? error)
        {
            if (Volatile.Read(ref _unexpectedlyStopped) == 0)
            {
                error = null;
                return false;
            }
            error = Volatile.Read(ref _unexpectedStopError);
            return true;
        }

        public void Enqueue(float[] samples, int count)
        {
            if (count <= 0) return;
            if (_output.PlaybackState != PlaybackState.Playing)
                throw new InvalidOperationException("The monitoring output stopped.");
            if (_buffer.BufferedDuration > MaximumBufferedDuration)
                _buffer.ClearBuffer();

            int bytes = checked(count * sizeof(float));
            if (_transfer.Length < bytes) _transfer = new byte[bytes];
            Buffer.BlockCopy(samples, 0, _transfer, 0, bytes);
            _buffer.AddSamples(_transfer, 0, bytes);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stoppingOrDisposed, 1) != 0) return;
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); }
            finally { _device.Dispose(); }
        }
    }

    /// <summary>Maps complete sample frames without changing their sample rate.</summary>
    internal sealed class ChannelMappingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private float[] _sourceBuffer = [];

        public ChannelMappingSampleProvider(ISampleProvider source, int outputChannels)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (outputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(outputChannels));
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate, outputChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int outputChannels = WaveFormat.Channels;
            int framesWanted = count / outputChannels;
            int sourceSamplesWanted = checked(framesWanted * _sourceChannels);
            if (_sourceBuffer.Length < sourceSamplesWanted)
                _sourceBuffer = new float[sourceSamplesWanted];
            int sourceSamples = _source.Read(_sourceBuffer, 0, sourceSamplesWanted);
            int frames = sourceSamples / _sourceChannels;

            for (int frame = 0; frame < frames; frame++)
            {
                int sourceOffset = frame * _sourceChannels;
                int destinationOffset = offset + frame * outputChannels;
                if (outputChannels == 1)
                {
                    double sum = 0;
                    for (int channel = 0; channel < _sourceChannels; channel++)
                        sum += _sourceBuffer[sourceOffset + channel];
                    buffer[destinationOffset] = (float)(sum / _sourceChannels);
                    continue;
                }

                if (_sourceChannels == 1)
                {
                    float mono = _sourceBuffer[sourceOffset];
                    for (int channel = 0; channel < outputChannels; channel++)
                        buffer[destinationOffset + channel] = mono;
                    continue;
                }

                int copied = Math.Min(_sourceChannels, outputChannels);
                Array.Copy(_sourceBuffer, sourceOffset, buffer, destinationOffset, copied);
                if (copied < outputChannels)
                    Array.Clear(buffer, destinationOffset + copied, outputChannels - copied);
            }
            return frames * outputChannels;
        }
    }
}
