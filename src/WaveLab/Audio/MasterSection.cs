using NAudio.Wave;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;

namespace WaveLab.Audio;

/// <summary>
/// The playback-time processing hub: an ordered, editable chain of effects followed by
/// metering — peak/RMS, EBU R128 loudness, stereo correlation, and ring buffers feeding
/// the spectrum analyzer and goniometer.
/// </summary>
public sealed class MasterSection : ISampleProvider
{
    private ISampleProvider? _source;
    private readonly object _chainLock = new();
    private readonly List<IAudioEffect> _chain = [];
    private bool _rackEnabled = true;
    private readonly object _ringLock = new();
    private readonly float[] _ringL = new float[16384];
    private readonly float[] _ringR = new float[16384];
    private int _ringPos;
    private double _corrSmooth;
    private int _sampleRate = 48000, _channels = 2;
    private int _startRampFrames, _startRampPosition;
    private bool _startRampWaitingForSignal;

    // A/B comparison snapshots
    private List<IAudioEffect>? _snapshotA;
    private List<IAudioEffect>? _snapshotB;
    private bool _isComparingB;

    // M/S processing mode
    private bool _msMode;

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
                foreach (var fx in _chain) fx.ResetState();
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
                foreach (var fx in _chain) fx.ResetState();
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
        lock (_chainLock)
        {
            _snapshotA = _chain.Select(EffectFactory.Clone).ToList();
            _snapshotB = null;
            _isComparingB = false;
        }
    }

    /// <summary>
    /// Capture the current chain state as snapshot B. Use after making changes
    /// to compare against snapshot A.
    /// </summary>
    public void CaptureSnapshotB()
    {
        lock (_chainLock)
        {
            _snapshotB = _chain.Select(EffectFactory.Clone).ToList();
            _isComparingB = false;
        }
    }

    /// <summary>
    /// Toggle between snapshot A and the current chain for A/B comparison.
    /// Returns true if toggling to B (snapshot), false if returning to current.
    /// </summary>
    public bool ToggleCompare()
    {
        lock (_chainLock)
        {
            if (_snapshotA == null) return false;

            if (_isComparingB)
            {
                // Restore current chain from snapshot A
                var current = _chain.Select(EffectFactory.Clone).ToList();
                _chain.Clear();
                _chain.AddRange(_snapshotA.Select(EffectFactory.Clone));
                _snapshotA = current;
                _isComparingB = false;
                foreach (var fx in _chain) fx.Configure(_sampleRate, _channels);
                return false;
            }
            else
            {
                // Save current as A, show B (or swap if B exists)
                var current = _chain.Select(EffectFactory.Clone).ToList();
                if (_snapshotB != null)
                {
                    _chain.Clear();
                    _chain.AddRange(_snapshotB.Select(EffectFactory.Clone));
                    _snapshotB = current;
                }
                else
                {
                    _snapshotB = current;
                }
                _isComparingB = true;
                foreach (var fx in _chain) fx.Configure(_sampleRate, _channels);
                return true;
            }
        }
    }

    /// <summary>Clear all A/B snapshots.</summary>
    public void ClearSnapshots()
    {
        lock (_chainLock)
        {
            _snapshotA = null;
            _snapshotB = null;
            _isComparingB = false;
        }
    }

    public IAudioEffect AddEffect(string typeId)
    {
        var fx = EffectFactory.Create(typeId);
        fx.Configure(_sampleRate, _channels);
        lock (_chainLock) _chain.Add(fx);
        return fx;
    }

    public bool RemoveEffect(IAudioEffect fx)
    {
        lock (_chainLock)
        {
            if (!_chain.Remove(fx)) return false;
            fx.Enabled = false;
            fx.ResetState();
            return true;
        }
    }

    public bool SetEffectEnabled(IAudioEffect fx, bool enabled)
    {
        lock (_chainLock)
        {
            if (!_chain.Contains(fx) || fx.Enabled == enabled) return false;
            fx.Enabled = enabled;
            fx.ResetState();
            return true;
        }
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

    public void ReplaceChain(IEnumerable<IAudioEffect> effects)
    {
        // configure (allocates delay/reverb buffers) OUTSIDE the lock so the audio
        // callback never stalls on a preset load
        var list = effects.ToList();
        foreach (var fx in list) fx.Configure(_sampleRate, _channels);
        lock (_chainLock)
        {
            _chain.Clear();
            _chain.AddRange(list);
        }
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
        Loudness.Configure(_sampleRate, _channels);
        _startRampFrames = Math.Max(1, _sampleRate / 100); // 10 ms
        _startRampPosition = 0;
        _startRampWaitingForSignal = true;
    }

    /// <summary>Release the current playback source after its output has stopped.</summary>
    public void ClearSource() => Volatile.Write(ref _source, null);

    public void ResetMeters()
    {
        PeakL = PeakR = RmsL = RmsR = 0;
        _corrSmooth = 0;
        Correlation = 0;
        BalanceDb = 0;
        Loudness.Reset();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // ClearSource can run on the control thread while the audio callback is
        // winding down, so keep one stable reference for this entire read.
        var source = Volatile.Read(ref _source);
        if (source == null) return 0;
        int read = source.Read(buffer, offset, count);
        if (read <= 0)
        {
            Loudness.FlushTruePeak();
            PeakL = PeakR = RmsL = RmsR = 0;
            return read;
        }

        lock (_chainLock)
        {
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
        lock (_ringLock)
        {
            for (int f = 0; f < frames; f++)
            {
                float l = buffer[offset + f * channels];
                float r = channels > 1 ? buffer[offset + f * channels + 1] : l;
                float al = Math.Abs(l), ar = Math.Abs(r);
                if (al > pl) pl = al;
                if (ar > pr) pr = ar;
                sl += l * l; sr += r * r; slr += l * r;
                _ringL[_ringPos] = l;
                _ringR[_ringPos] = r;
                _ringPos = (_ringPos + 1) % _ringL.Length;
            }
        }
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
        lock (_ringLock)
        {
            int n = dest.Length;
            int start = (_ringPos - n + _ringL.Length * 4) % _ringL.Length;
            for (int i = 0; i < n; i++)
            {
                int p = (start + i) % _ringL.Length;
                dest[i] = (_ringL[p] + _ringR[p]) * 0.5f;
            }
        }
    }

    /// <summary>Most recent n stereo sample pairs for the goniometer.</summary>
    public void CopyLatestStereo(float[] destL, float[] destR)
    {
        lock (_ringLock)
        {
            int n = destL.Length;
            int start = (_ringPos - n + _ringL.Length * 4) % _ringL.Length;
            for (int i = 0; i < n; i++)
            {
                int p = (start + i) % _ringL.Length;
                destL[i] = _ringL[p];
                destR[i] = _ringR[p];
            }
        }
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
        IProgress<double>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0);
        IAudioEffect[] enabledEffects;
        lock (_chainLock)
            enabledEffects = _rackEnabled ? _chain.Where(f => f.Enabled).ToArray() : [];
        var chain = enabledEffects.Select(EffectFactory.Clone).ToList();
        bool expandMono = data.Length == 1 && enabledEffects.Any(fx => fx.TypeId == "mono-stereo");
        float[][] sourceData = expandMono ? [data[0], data[0]] : data;
        int channels = sourceData.Length;
        foreach (var fx in chain) fx.Configure(sampleRate, channels);
        int latency = chain.Sum(f => f.LatencySamples);

        int frames = data[0].Length;
        int totalFrames = frames + latency;
        const int block = 65536;
        var interleaved = new float[block * channels];
        var output = new float[channels][];
        for (int c = 0; c < channels; c++) output[c] = new float[frames];

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
            foreach (var fx in chain) fx.Process(interleaved, 0, n * channels);
            for (int f = 0; f < n; f++, outFrame++)
            {
                if (outFrame < 0 || outFrame >= frames) continue;
                for (int c = 0; c < channels; c++)
                    output[c][outFrame] = interleaved[f * channels + c];
            }
            progress?.Report((double)(start + n) / totalFrames);
        }
        if (totalFrames == 0) progress?.Report(1);
        return output;
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
        IAudioEffect[] enabledEffects;
        lock (_chainLock)
            enabledEffects = _rackEnabled ? _chain.Where(effect => effect.Enabled).ToArray() : [];

        var chain = enabledEffects.Select(EffectFactory.Clone).ToList();
        bool expandMono = data.Length == 1 && enabledEffects.Any(effect => effect.TypeId == "mono-stereo");
        float[][] sourceData = expandMono ? [data[0], data[0]] : data;
        int channels = sourceData.Length;
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

            foreach (var effect in chain)
                effect.Process(interleaved, 0, framesInBlock * channels);

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

}
