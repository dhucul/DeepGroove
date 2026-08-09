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
    private readonly object _ringLock = new();
    private readonly float[] _ringL = new float[16384];
    private readonly float[] _ringR = new float[16384];
    private int _ringPos;
    private double _corrSmooth;
    private int _sampleRate = 48000, _channels = 2;
    private int _startRampFrames, _startRampPosition;
    private bool _startRampWaitingForSignal;

    public MasterSection()
    {
        _chain.Add(EffectFactory.Create("eq"));
        _chain.Add(EffectFactory.Create("limiter"));
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

    public IAudioEffect AddEffect(string typeId)
    {
        var fx = EffectFactory.Create(typeId);
        fx.Configure(_sampleRate, _channels);
        lock (_chainLock) _chain.Add(fx);
        return fx;
    }

    public void RemoveEffect(IAudioEffect fx) { lock (_chainLock) _chain.Remove(fx); }

    public void MoveEffect(IAudioEffect fx, int delta)
    {
        lock (_chainLock)
        {
            int i = _chain.IndexOf(fx);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _chain.Count) return;
            (_chain[i], _chain[j]) = (_chain[j], _chain[i]);
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
        _source = source;
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        ConfigureChain();
        Loudness.Configure(_sampleRate, _channels);
        _startRampFrames = Math.Max(1, _sampleRate / 100); // 10 ms
        _startRampPosition = 0;
        _startRampWaitingForSignal = true;
    }

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
        if (_source == null) return 0;
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) { PeakL = PeakR = RmsL = RmsR = 0; return read; }

        lock (_chainLock)
            foreach (var fx in _chain)
                if (fx.Enabled)
                    fx.Process(buffer, offset, read);

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
    public float[][] ProcessOffline(float[][] data, int sampleRate)
    {
        var chain = ChainSnapshot.Where(f => f.Enabled).Select(EffectFactory.Clone).ToList();
        int channels = data.Length;
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
            int n = Math.Min(block, totalFrames - start);
            for (int f = 0; f < n; f++)
            {
                int srcF = start + f;
                for (int c = 0; c < channels; c++)
                    interleaved[f * channels + c] = srcF < frames ? data[c][srcF] : 0f;
            }
            foreach (var fx in chain) fx.Process(interleaved, 0, n * channels);
            for (int f = 0; f < n; f++, outFrame++)
            {
                if (outFrame < 0 || outFrame >= frames) continue;
                for (int c = 0; c < channels; c++)
                    output[c][outFrame] = interleaved[f * channels + c];
            }
        }
        return output;
    }

}
