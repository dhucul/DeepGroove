using NAudio.Wave;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;

namespace WaveLab.Audio;

/// <summary>
/// The playback-time processing hub: an ordered, editable chain of effects followed by
/// metering — peak/RMS, EBU R128 loudness, stereo correlation, and ring buffers feeding
/// the spectrum analyzer and goniometer.
/// </summary>
public sealed class MasterSection : ISampleProvider, IDisposable
{
    private ISampleProvider? _source;
    private readonly object _chainLock = new();
    private readonly List<IAudioEffect> _chain = [];
    private readonly HashSet<IAudioEffect> _pendingResets = [];
    private bool _sourceEnded;
    private bool _sourceHadSamples;
    private int _drainFramesRemaining;
    private bool _rackEnabled = true;
    /// <summary>
    /// Scope/goniometer trace history. A power of two so the write index advances with a mask
    /// rather than a modulo, which is what lets the render thread fill it without a lock.
    /// </summary>
    private const int RingSize = 16384;
    private const int RingMask = RingSize - 1;
    private readonly float[] _ringL = new float[RingSize];
    private readonly float[] _ringR = new float[RingSize];
    private int _ringPos;
    private double _corrSmooth;
    private int _sampleRate = 48000, _channels = 2;
    private int _startRampFrames, _startRampPosition;
    private bool _startRampWaitingForSignal;
    private float[] _processingBuffer = [];

    // A/B comparison snapshots
    private List<IAudioEffect>? _snapshotA;
    private List<IAudioEffect>? _snapshotB;
    private bool _isComparingB;

    // M/S processing mode
    private bool _msMode;
    private int _disposed;

    public MasterSection()
    {
        _chain.AddRange(EffectFactory.Instantiate(EffectFactory.CreateFactoryPreset("Default")));
        ConfigureChain();
    }

    public LoudnessMeter Loudness { get; } = new();

    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public float RmsL { get; private set; }
    public float RmsR { get; private set; }
    /// <summary>Smoothed stereo correlation, −1 … +1.</summary>
    public double Correlation { get; private set; }
    /// <summary>RMS balance L vs R in dB (negative = left louder).</summary>
    public double BalanceDb { get; private set; }

    public WaveFormat WaveFormat => _source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);

    // ── chain management ─────────────────────────────────────────

    /// <summary>Snapshot of the current chain (live references — mutate params freely, structure via the API below).</summary>
    public IAudioEffect[] ChainSnapshot { get { lock (_chainLock) return _chain.ToArray(); } }

    /// <summary>
    /// True when a mono source should be presented to the rack as stereo so the
    /// enabled mono-to-stereo processor can generate a side signal.
    /// </summary>
    public bool ExpandsMonoToStereo
    {
        get
        {
            lock (_chainLock)
                return _rackEnabled && _chain.Any(fx => fx.Enabled && fx.TypeId == "mono-stereo");
        }
    }

    /// <summary>Global rack bypass. Individual effect enabled states are preserved.</summary>
    public bool RackEnabled
    {
        get { lock (_chainLock) return _rackEnabled; }
        set
        {
            lock (_chainLock)
            {
                if (_rackEnabled == value) return;
                _rackEnabled = value;
                foreach (var effect in _chain) _pendingResets.Add(effect);
            }
        }
    }

    /// <summary>
    /// Mid/Side processing mode. When enabled, stereo input is split into M/S,
    /// processed through the chain, and recombined to L/R. Mono sources pass through unchanged.
    /// </summary>
    public bool MidSideMode
    {
        get { lock (_chainLock) return _msMode; }
        set
        {
            lock (_chainLock)
            {
                if (_msMode == value) return;
                _msMode = value;
                foreach (var effect in _chain) _pendingResets.Add(effect);
            }
        }
    }

    /// <summary>True when an A/B comparison is active and showing snapshot B.</summary>
    public bool IsComparingB
    {
        get => _isComparingB;
        private set => _isComparingB = value;
    }

    /// <summary>True when either snapshot A or B has been captured.</summary>
    public bool HasSnapshots => _snapshotA != null || _snapshotB != null;

    /// <summary>
    /// Capture the current chain state as snapshot A. Subsequent changes can be
    /// compared against this snapshot.
    /// </summary>
    public void CaptureSnapshotA()
    {
        List<IAudioEffect>? oldA, oldB;
        lock (_chainLock)
        {
            oldA = _snapshotA;
            oldB = _snapshotB;
            _snapshotA = CloneAll(_chain);
            _snapshotB = null;
            _isComparingB = false;
        }
        Retire(oldA);
        Retire(oldB);
    }

    /// <summary>
    /// Capture the current chain state as snapshot B. Use after making changes
    /// to compare against snapshot A.
    /// </summary>
    public void CaptureSnapshotB()
    {
        List<IAudioEffect>? oldB;
        lock (_chainLock)
        {
            oldB = _snapshotB;
            _snapshotB = CloneAll(_chain);
            _isComparingB = true;
        }
        Retire(oldB);
    }

    /// <summary>
    /// Toggle between snapshot A and the current chain for A/B comparison.
    /// Returns true when displaying B, false when displaying A. Changes made to the
    /// departing side are retained in that side's slot.
    /// </summary>
    public bool ToggleCompare()
    {
        List<IAudioEffect> incoming;
        List<IAudioEffect> current;
        bool showingB;
        lock (_chainLock)
        {
            if (_snapshotA == null) return false;
            showingB = _snapshotB != null && !_isComparingB;
            incoming = CloneAll(showingB ? _snapshotB! : _snapshotA);
            try { current = CloneAll(_chain); }
            catch { Retire(incoming); throw; }
        }
        try
        {
            foreach (IAudioEffect fx in incoming) fx.Configure(_sampleRate, _channels);
        }
        catch { Retire(incoming); Retire(current); throw; }
        List<IAudioEffect> displaced;
        List<IAudioEffect>? discarded;
        lock (_chainLock)
        {
            displaced = [.. _chain];
            discarded = showingB ? _snapshotA : _snapshotB;
            if (showingB) _snapshotA = current;
            else _snapshotB = current;
            _chain.Clear();
            _pendingResets.Clear();
            _chain.AddRange(incoming);
            _isComparingB = showingB;
        }
        Retire(displaced);
        Retire(discarded);
        return showingB;
    }

    /// <summary>Clear all A/B snapshots.</summary>
    public void ClearSnapshots()
    {
        List<IAudioEffect>? oldA, oldB;
        lock (_chainLock)
        {
            oldA = _snapshotA;
            oldB = _snapshotB;
            _snapshotA = null;
            _snapshotB = null;
            _isComparingB = false;
        }
        Retire(oldA);
        Retire(oldB);
    }

    /// <summary>
    /// Creates an effect, sets any parameters the caller named, and publishes it to the chain.
    /// </summary>
    /// <remarks>
    /// The settings are applied here rather than by the caller for the same reason
    /// <see cref="IAudioEffect.Configure"/> is: <c>Read</c> holds <c>_chainLock</c> for a whole
    /// block, so an effect published at its defaults processes at its defaults until the next one —
    /// and a caller that named a ceiling meant that ceiling from the first sample. Setting them
    /// afterwards also lets the audio thread observe one parameter moved and another not.
    /// </remarks>
    public IAudioEffect AddEffect(string typeId, IReadOnlyList<(string Key, double Value)>? settings = null)
    {
        var fx = EffectFactory.Create(typeId);
        fx.Configure(_sampleRate, _channels);
        if (settings != null)
            foreach (var (key, value) in settings) fx.SetParam(key, value);
        lock (_chainLock) _chain.Add(fx);
        return fx;
    }

    public bool RemoveEffect(IAudioEffect fx)
    {
        lock (_chainLock)
        {
            if (!_chain.Remove(fx)) return false;
            _pendingResets.Remove(fx);
            fx.Enabled = false;
        }

        // Reset after the lock, as the rack, M/S and enable switches do. Once it is out of the
        // chain Read can never reach it again, and clearing a convolver's partition history is
        // real work — doing it in here stalled the audio callback for as long as it took.
        fx.ResetState();

        // Released after the lock, never inside it. Once it is out of the chain the audio thread can
        // never reach it again, and letting go of a VST3 plugin means terminating somebody else's
        // code — which can take long enough to be heard if the audio callback is waiting on it.
        Retire(fx);
        return true;
    }

    /// <summary>
    /// Lets go of an effect that has left the chain.
    /// </summary>
    /// <remarks>
    /// Only plugins have anything to let go of; the built-ins are ordinary managed objects. For a
    /// plugin this releases one reference, which is not the same as unloading it — an A/B snapshot
    /// may still be holding the same instance.
    /// </remarks>
    private static void Retire(IAudioEffect? fx)
    {
        if (fx is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Clones a whole chain, releasing what it has already built if one of them throws.
    /// </summary>
    /// <remarks>
    /// A half-built snapshot is unreachable the moment the exception leaves the method,
    /// and for a plugin every clone in it holds a reference nothing will ever drop.
    /// </remarks>
    private static List<IAudioEffect> CloneAll(IEnumerable<IAudioEffect> source)
    {
        var clones = new List<IAudioEffect>();
        try
        {
            foreach (IAudioEffect effect in source) clones.Add(EffectFactory.Clone(effect));
            return clones;
        }
        catch
        {
            Retire(clones);
            throw;
        }
    }

    private static void Retire(IEnumerable<IAudioEffect>? effects)
    {
        if (effects == null) return;
        foreach (IAudioEffect fx in effects) Retire(fx);
    }

    /// <summary>Releases every effect instance owned by the live chain and its A/B snapshots.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        List<IAudioEffect> owned;
        lock (_chainLock)
        {
            Volatile.Write(ref _source, null);
            owned = [.. _chain];
            if (_snapshotA != null) owned.AddRange(_snapshotA);
            if (_snapshotB != null) owned.AddRange(_snapshotB);
            _chain.Clear();
            _pendingResets.Clear();
            _snapshotA = null;
            _snapshotB = null;
            _isComparingB = false;
        }

        Retire(owned);
        GC.SuppressFinalize(this);
    }

    public bool SetEffectEnabled(IAudioEffect fx, bool enabled)
    {
        lock (_chainLock)
        {
            if (!_chain.Contains(fx) || fx.Enabled == enabled) return false;
            fx.Enabled = enabled;
            _pendingResets.Add(fx);
        }
        return true;
    }

    public bool MoveEffect(IAudioEffect fx, int delta)
    {
        lock (_chainLock)
        {
            int i = _chain.IndexOf(fx);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _chain.Count) return false;
            (_chain[i], _chain[j]) = (_chain[j], _chain[i]);
            return true;
        }
    }

    public EffectFactory.ChainPreset CaptureEffectPreset(IAudioEffect effect, string name)
    {
        lock (_chainLock)
        {
            if (!_chain.Contains(effect)) throw new InvalidOperationException("This effect is no longer in the rack.");
            return EffectFactory.Capture(name, [effect]);
        }
    }

    public void ReplaceChain(IEnumerable<IAudioEffect> effects)
    {
        // configure (allocates delay/reverb buffers) OUTSIDE the lock so the audio
        // callback never stalls on a preset load
        var list = effects.ToList();

        // Only effects that are not already running may be configured out here. The
        // Retire line below shows the incoming and outgoing chains are expected to
        // overlap, and reconfiguring a carried-over effect outside the lock would do it
        // while Read is inside that effect - which for a plugin frees the native buffers
        // the audio thread is writing into. One that stays is already configured for
        // this stream.
        List<IAudioEffect> running;
        lock (_chainLock) running = [.. _chain];
        foreach (var fx in list)
            if (!running.Contains(fx))
                fx.Configure(_sampleRate, _channels);

        List<IAudioEffect> replaced;
        lock (_chainLock)
        {
            replaced = [.. _chain];
            _chain.Clear();
            _pendingResets.Clear();
            _chain.AddRange(list);
        }

        // The outgoing chain is released outside the lock, for the same reason as RemoveEffect. An
        // effect that is also in an A/B snapshot survives this: it is a reference being dropped, not
        // an instance being destroyed.
        Retire(replaced.Where(fx => !list.Contains(fx)));
    }

    private void ConfigureChain()
    {
        lock (_chainLock)
            foreach (var fx in _chain) fx.Configure(_sampleRate, _channels);
    }

    // ── streaming ────────────────────────────────────────────────

    public void SetSource(ISampleProvider source)
    {
        Volatile.Write(ref _source, source);
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        ConfigureChain();
        lock (_chainLock) _pendingResets.Clear();
        _sourceEnded = false;
        _sourceHadSamples = false;
        _drainFramesRemaining = 0;
        Loudness.Configure(_sampleRate, _channels);
        _startRampFrames = Math.Max(1, _sampleRate / 100); // 10 ms
        _startRampPosition = 0;
        _startRampWaitingForSignal = true;
    }

    /// <summary>Release the current playback source after its output has stopped.</summary>
    public void ClearSource() => Volatile.Write(ref _source, null);

    public int LiveLatencySamples
    {
        get { lock (_chainLock) return _rackEnabled ? checked(_chain.Where(f => f.Enabled).Sum(f => Math.Max(0, f.LatencySamples))) : 0; }
    }

    public void ResetMeters()
    {
        PeakL = PeakR = RmsL = RmsR = 0;
        _corrSmooth = 0;
        Correlation = 0;
        BalanceDb = 0;
        Loudness.Reset();
    }

    public int Read(Span<float> destination)
    {
        // ClearSource can run on the control thread while the audio callback is
        // winding down, so keep one stable reference for this entire read.
        var source = Volatile.Read(ref _source);
        if (source == null) return 0;
        if (destination.Length < _channels) return 0;
        if (_processingBuffer.Length < destination.Length)
            _processingBuffer = new float[destination.Length];
        float[] buffer = _processingBuffer;
        const int offset = 0;
        int read = source.Read(buffer.AsSpan(0, destination.Length));
        lock (_chainLock)
        {
            // The callback owns mutable DSP state, including resets. Publishing a flag on
            // the UI thread must never let Process race a reset of the same delay buffers.
            foreach (var effect in _pendingResets) effect.ResetState();
            _pendingResets.Clear();
            if (read > 0)
            {
                _sourceHadSamples = true;
                _sourceEnded = false;
            }
            else
            {
                if (!_sourceEnded)
                {
                    var enabled = _rackEnabled ? _chain.Where(f => f.Enabled).ToArray() : [];
                    _drainFramesRemaining = _sourceHadSamples
                        ? checked(enabled.Sum(f => Math.Max(0, f.LatencySamples)) + TailForCopyRender(enabled, _sampleRate))
                        : 0;
                    _sourceEnded = true;
                }
                int drainFrames = Math.Min(_drainFramesRemaining, destination.Length / _channels);
                if (drainFrames == 0)
                {
                    Loudness.FlushTruePeak();
                    PeakL = PeakR = RmsL = RmsR = 0;
                    return 0;
                }
                _drainFramesRemaining -= drainFrames;
                read = drainFrames * _channels;
                Array.Clear(buffer, 0, read);
            }
            if (_rackEnabled)
            {
                if (_msMode && _channels >= 2)
                {
                    // M/S processing: convert L/R to M/S, process, convert back
                    int msFrames = read / _channels;
                    for (int f = 0; f < msFrames; f++)
                    {
                        int idx = offset + f * _channels;
                        float left = buffer[idx];
                        float right = buffer[idx + 1];
                        float mid = (left + right) * 0.5f;
                        float side = (left - right) * 0.5f;
                        buffer[idx] = mid;
                        buffer[idx + 1] = side;
                    }

                    foreach (var fx in _chain)
                        if (fx.Enabled)
                            fx.Process(buffer, offset, read);

                    for (int f = 0; f < msFrames; f++)
                    {
                        int idx = offset + f * _channels;
                        float mid = buffer[idx];
                        float side = buffer[idx + 1];
                        buffer[idx] = mid + side;
                        buffer[idx + 1] = mid - side;
                    }
                }
                else
                {
                    foreach (var fx in _chain)
                        if (fx.Enabled)
                            fx.Process(buffer, offset, read);
                }
            }
        }

        ApplyStartRamp(buffer, offset, read);
        Loudness.Process(buffer, offset, read);

        int channels = _channels;
        int frames = read / channels;
        float pl = 0, pr = 0;
        double sl = 0, sr = 0, slr = 0;
        // Read is the ring's only writer, so the index needs no lock here. Taking one
        // meant the render thread could park behind a UI-thread copy of the whole
        // 16k-sample history — a priority inversion for a scope trace.
        int ringPos = _ringPos;
        for (int f = 0; f < frames; f++)
        {
            float l = buffer[offset + f * channels];
            float r = channels > 1 ? buffer[offset + f * channels + 1] : l;
            float al = Math.Abs(l), ar = Math.Abs(r);
            if (al > pl) pl = al;
            if (ar > pr) pr = ar;
            sl += l * l; sr += r * r; slr += l * r;
            _ringL[ringPos] = l;
            _ringR[ringPos] = r;
            ringPos = (ringPos + 1) & RingMask;
        }
        // Published once per block. A reader that catches the very newest frames mid-write
        // sees the previous block's samples there instead, which is one refresh of staleness
        // at the trace's leading edge and invisible at 30 Hz.
        Volatile.Write(ref _ringPos, ringPos);
        PeakL = pl; PeakR = pr;
        if (frames > 0)
        {
            RmsL = (float)Math.Sqrt(sl / frames);
            RmsR = (float)Math.Sqrt(sr / frames);
            double denom = Math.Sqrt(sl * sr);
            double corr = denom > 1e-12 ? slr / denom : 0;
            _corrSmooth = 0.85 * _corrSmooth + 0.15 * corr;
            Correlation = _corrSmooth;
            if (RmsL > 1e-5 && RmsR > 1e-5)
                BalanceDb = 20 * Math.Log10(RmsR / RmsL);
        }
        buffer.AsSpan(0, read).CopyTo(destination);
        return read;
    }

    private void ApplyStartRamp(float[] buffer, int offset, int count)
    {
        if (!_startRampWaitingForSignal && _startRampPosition >= _startRampFrames) return;

        int frames = count / _channels;
        for (int f = 0; f < frames; f++)
        {
            int frameOffset = offset + f * _channels;
            if (_startRampWaitingForSignal)
            {
                float peak = 0;
                for (int c = 0; c < _channels; c++)
                    peak = Math.Max(peak, Math.Abs(buffer[frameOffset + c]));
                if (peak < 1e-7f) continue;
                _startRampWaitingForSignal = false;
            }

            double t = (_startRampPosition + 1.0) / _startRampFrames;
            float gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * Math.Min(1, t)));
            for (int c = 0; c < _channels; c++) buffer[frameOffset + c] *= gain;
            if (++_startRampPosition >= _startRampFrames) break;
        }
    }

    /// <summary>Most recent n mono samples for the spectrum analyzer.</summary>
    public void CopyLatest(float[] dest)
    {
        ArgumentNullException.ThrowIfNull(dest);
        // Clamped to the ring: the old "+ Length * 4" bias went negative once a
        // caller asked for more samples than four rings held, and indexed backwards
        // out of the buffer. The mask handles a negative start on its own.
        int n = Math.Min(dest.Length, RingSize);
        int start = Volatile.Read(ref _ringPos) - n;
        for (int i = 0; i < n; i++)
        {
            int p = (start + i) & RingMask;
            dest[i] = (_ringL[p] + _ringR[p]) * 0.5f;
        }
        if (n < dest.Length) Array.Clear(dest, n, dest.Length - n);
    }

    /// <summary>Most recent n stereo sample pairs for the goniometer.</summary>
    public void CopyLatestStereo(float[] destL, float[] destR)
    {
        ArgumentNullException.ThrowIfNull(destL);
        ArgumentNullException.ThrowIfNull(destR);
        int n = Math.Min(Math.Min(destL.Length, destR.Length), RingSize);
        int start = Volatile.Read(ref _ringPos) - n;
        for (int i = 0; i < n; i++)
        {
            int p = (start + i) & RingMask;
            destL[i] = _ringL[p];
            destR[i] = _ringR[p];
        }
        if (n < destL.Length) Array.Clear(destL, n, destL.Length - n);
        if (n < destR.Length) Array.Clear(destR, n, destR.Length - n);
    }

    // ── offline ──────────────────────────────────────────────────

    /// <summary>
    /// Process deinterleaved data through a cloned copy of the enabled chain with
    /// latency compensation. Used by render and apply-to-selection.
    /// </summary>
    public float[][] ProcessOffline(
        float[][] data,
        int sampleRate,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null,
        bool includeTail = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) return [];
        int frames = data[0].Length;
        if (data.Any(channel => channel is null || channel.Length != frames))
            throw new ArgumentException("All source channels must have the same length.", nameof(data));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0);
        EffectFactory.OfflineRenderSnapshot[] snapshots = CaptureOfflineRenderSnapshots(
            out bool midSideRequested);

        var chain = new List<IAudioEffect>(snapshots.Length);
        try
        {
            foreach (EffectFactory.OfflineRenderSnapshot snapshot in snapshots)
                chain.Add(snapshot.Instantiate());

            bool expandMono = data.Length == 1 &&
                              snapshots.Any(snapshot => snapshot.TypeId == "mono-stereo");
            float[][] sourceData = expandMono ? [data[0], data[0]] : data;
            int channels = sourceData.Length;
            bool processMidSide = midSideRequested && channels >= 2;
            foreach (var fx in chain) fx.Configure(sampleRate, channels);
            int latency = checked(chain.Sum(f => f.LatencySamples));
            int tail = includeTail && frames > 0
                ? TailForCopyRender(chain, sampleRate)
                : 0;

            int outputFrames = checked(frames + tail);
            int totalFrames = checked(outputFrames + latency);
            const int block = 65536;
            var interleaved = new float[checked(block * channels)];
            var output = new float[channels][];
            for (int c = 0; c < channels; c++) output[c] = new float[outputFrames];

            int outFrame = -latency; // skip the first `latency` processed frames
            for (int start = 0; start < totalFrames; start += block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = Math.Min(block, totalFrames - start);
                for (int f = 0; f < n; f++)
                {
                    int srcF = start + f;
                    for (int c = 0; c < channels; c++)
                        interleaved[f * channels + c] = srcF < frames ? sourceData[c][srcF] : 0f;
                }
                if (processMidSide) ConvertToMidSide(interleaved, n, channels);
                foreach (var fx in chain) fx.Process(interleaved, 0, n * channels);
                if (processMidSide) ConvertFromMidSide(interleaved, n, channels);
                for (int f = 0; f < n; f++, outFrame++)
                {
                    if (outFrame < 0 || outFrame >= outputFrames) continue;
                    for (int c = 0; c < channels; c++)
                        output[c][outFrame] = interleaved[f * channels + c];
                }
                progress?.Report((double)(start + n) / totalFrames);
            }
            if (totalFrames == 0) progress?.Report(1);
            return output;
        }
        finally
        {
            // Same contract as RemoveEffect and ReplaceChain: a clone that leaves scope
            // drops its reference. Cancellation lands here too.
            Retire(chain);
            DisposeSnapshots(snapshots);
        }
    }

    /// <summary>
    /// Explicit time effects are allowed to ring after the source in a copy render. Tails add when
    /// effects are chained (a reverb can feed a delay), with a generous hard ceiling to keep a
    /// near-unity feedback chain from creating an accidentally enormous document.
    /// </summary>
    private static int TailForCopyRender(IEnumerable<IAudioEffect> chain, int sampleRate)
    {
        long tail = 0;
        long maximum = (long)sampleRate * 120;
        foreach (IAudioEffect effect in chain)
        {
            tail += Math.Max(0, effect.TailSamples);
            if (tail >= maximum) return (int)maximum;
        }
        return (int)tail;
    }

    /// <summary>
    /// Render one compensated output range while warming a cloned rack from the
    /// beginning of the continuous program. This matches a full offline render's
    /// state at the requested range without retaining the preceding output.
    /// </summary>
    public float[][] ProcessOfflineRange(
        float[][] data,
        int sampleRate,
        int rangeStart,
        int frameCount,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) return [];
        int sourceFrames = data[0].Length;
        if (data.Any(channel => channel.Length != sourceFrames))
            throw new ArgumentException("All source channels must have the same length.", nameof(data));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (rangeStart < 0 || rangeStart > sourceFrames)
            throw new ArgumentOutOfRangeException(nameof(rangeStart));
        if (frameCount < 0 || frameCount > sourceFrames - rangeStart)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0);
        EffectFactory.OfflineRenderSnapshot[] snapshots = CaptureOfflineRenderSnapshots(
            out bool midSideRequested);

        var chain = new List<IAudioEffect>(snapshots.Length);
        try
        {
            foreach (EffectFactory.OfflineRenderSnapshot snapshot in snapshots)
                chain.Add(snapshot.Instantiate());
            bool expandMono = data.Length == 1 &&
                              snapshots.Any(snapshot => snapshot.TypeId == "mono-stereo");
            float[][] sourceData = expandMono ? [data[0], data[0]] : data;
            int channels = sourceData.Length;
            bool processMidSide = midSideRequested && channels >= 2;
            var output = new float[channels][];
            for (int channel = 0; channel < channels; channel++) output[channel] = new float[frameCount];
            if (frameCount == 0)
            {
                progress?.Report(1);
                return output;
            }

            if (chain.Count == 0)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Array.Copy(sourceData[channel], rangeStart, output[channel], 0, frameCount);
                }
                progress?.Report(1);
                return output;
            }

            foreach (var effect in chain) effect.Configure(sampleRate, channels);
            int latency = checked(chain.Sum(effect => effect.LatencySamples));
            int rangeEnd = checked(rangeStart + frameCount);
            int framesToProcess = checked(rangeEnd + latency);
            const int block = 65536;
            var interleaved = new float[checked(block * channels)];

            for (int processStart = 0; processStart < framesToProcess; processStart += block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int framesInBlock = Math.Min(block, framesToProcess - processStart);
                for (int frame = 0; frame < framesInBlock; frame++)
                {
                    int sourceFrame = processStart + frame;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        interleaved[frame * channels + channel] = sourceFrame < sourceFrames
                            ? sourceData[channel][sourceFrame]
                            : 0f;
                    }
                }

                if (processMidSide) ConvertToMidSide(interleaved, framesInBlock, channels);
                foreach (var effect in chain)
                    effect.Process(interleaved, 0, framesInBlock * channels);
                if (processMidSide) ConvertFromMidSide(interleaved, framesInBlock, channels);

                int outputFrame = processStart - latency;
                for (int frame = 0; frame < framesInBlock; frame++, outputFrame++)
                {
                    if (outputFrame < rangeStart || outputFrame >= rangeEnd) continue;
                    int destinationFrame = outputFrame - rangeStart;
                    for (int channel = 0; channel < channels; channel++)
                        output[channel][destinationFrame] = interleaved[frame * channels + channel];
                }
                progress?.Report((double)(processStart + framesInBlock) / framesToProcess);
            }
            return output;
        }
        finally
        {
            // Same contract as ProcessOffline: the clones are this render's to release.
            Retire(chain);
            DisposeSnapshots(snapshots);
        }
    }

    private EffectFactory.OfflineRenderSnapshot[] CaptureOfflineRenderSnapshots(
        out bool midSideRequested)
    {
        lock (_chainLock)
        {
            midSideRequested = _rackEnabled && _msMode;
            if (!_rackEnabled) return [];

            var snapshots = new List<EffectFactory.OfflineRenderSnapshot>();
            try
            {
                foreach (IAudioEffect effect in _chain)
                {
                    if (effect.Enabled)
                        snapshots.Add(EffectFactory.CaptureForOfflineRender(effect));
                }
                return [.. snapshots];
            }
            catch
            {
                DisposeSnapshots(snapshots);
                throw;
            }
        }
    }

    private static void DisposeSnapshots(
        IEnumerable<EffectFactory.OfflineRenderSnapshot> snapshots)
    {
        foreach (EffectFactory.OfflineRenderSnapshot snapshot in snapshots)
            snapshot.Dispose();
    }

    private static void ConvertToMidSide(float[] interleaved, int frames, int channels)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            float left = interleaved[index], right = interleaved[index + 1];
            interleaved[index] = (left + right) * 0.5f;
            interleaved[index + 1] = (left - right) * 0.5f;
        }
    }

    private static void ConvertFromMidSide(float[] interleaved, int frames, int channels)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            float mid = interleaved[index], side = interleaved[index + 1];
            interleaved[index] = mid + side;
            interleaved[index + 1] = mid - side;
        }
    }

}
