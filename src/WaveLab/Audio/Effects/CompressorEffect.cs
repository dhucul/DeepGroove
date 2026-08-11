using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced feed-forward compressor: peak or RMS detection, soft knee,
/// attack/release smoothing, optional lookahead, sidechain high-pass filter,
/// parallel wet/dry mix, program-dependent auto-release, and auto-makeup.
/// </summary>
public sealed class CompressorEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("thresh", "THRESH", -60, 0, -18, EffectParam.Db),
        new("ratio", "RATIO", 1, 20, 3, EffectParam.Ratio),
        new("attack", "ATTACK", 0.1, 100, 12, v => $"{v:0.0} ms"),
        new("release", "RELEASE", 10, 1000, 140, EffectParam.Ms),
        new("makeup", "MAKEUP", 0, 24, 0, EffectParam.Db),
        new("autoMakeup", "AUTO MU", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
        new("knee", "KNEE", 0, 24, 6, EffectParam.Db),
        new("mix", "MIX", 0, 1, 1, EffectParam.Pct),
        new("lookahead", "LOOKAHEAD", 0, 5, 1, EffectParam.Ms),
        new("scHpf", "SC HPF", 20, 500, 20, EffectParam.Hz),
        new("rmsMode", "RMS MODE", 0, 1, 0, v => v > 0.5 ? "RMS" : "PEAK"),
        new("autoRelease", "AUTO REL", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
    ];

    private double _envDb = -90;
    private double _grDb;
    private float[][] _lookaheadBuf = [];
    private int _lookaheadPos;
    private int _lookaheadLen;
    private Biquad[] _sidechainHpf = [];
    private double _msState;       // one-pole mean-square detector state (RMS mode)
    private double _autoMakeupDb;  // slow follower restoring average gain reduction

    public override string TypeId => "compressor";
    public override string DisplayName => "Compressor";
    public override IReadOnlyList<EffectParam> Params => P;

    // Report the actual lookahead delay so offline renders stay sample-aligned.
    public override int LatencySamples
    {
        get
        {
            int max = _lookaheadLen > 0 ? _lookaheadLen : (int)(SampleRate * 5 / 1000.0);
            return Math.Clamp((int)(GetParam("lookahead") / 1000.0 * SampleRate), 0, max);
        }
    }
    public override string? Readout => $"GR −{_grDb:0.0} dB";

    protected override void OnConfigure()
    {
        _lookaheadLen = Math.Max(0, (int)(SampleRate * 5 / 1000.0));
        _lookaheadBuf = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
            _lookaheadBuf[c] = new float[Math.Max(1, _lookaheadLen)];
        _lookaheadPos = 0;
        _sidechainHpf = new Biquad[ChannelCount];
        RebuildSidechain();
    }

    private void RebuildSidechain()
    {
        if (_sidechainHpf.Length != ChannelCount) return;

        double hpfFreq = GetParam("scHpf");
        for (int c = 0; c < ChannelCount; c++)
            _sidechainHpf[c] = hpfFreq > 25
                ? Biquad.FirstOrderHighPass(SampleRate, hpfFreq)
                : Biquad.Identity();
    }

    protected override void OnParamsChanged() => RebuildSidechain();

    public override void ResetState()
    {
        _envDb = -90;
        _grDb = 0;
        _msState = 0;
        _autoMakeupDb = 0;
        foreach (var buf in _lookaheadBuf) Array.Clear(buf);
        _lookaheadPos = 0;
        foreach (var f in _sidechainHpf) f.Reset();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_lookaheadBuf.Length == 0) return;

        double thresh = GetParam("thresh");
        double ratio = GetParam("ratio");
        double attCoeff = Math.Exp(-1.0 / (SampleRate * GetParam("attack") / 1000.0));
        double releaseMs = GetParam("release");
        double relCoeff = Math.Exp(-1.0 / (SampleRate * releaseMs / 1000.0));
        double makeupDb = GetParam("makeup");
        bool autoMakeup = GetParam("autoMakeup") > 0.5;
        double knee = GetParam("knee");
        float mix = (float)GetParam("mix");
        float dryMix = 1 - mix;
        bool useRms = GetParam("rmsMode") > 0.5;
        bool autoRelease = GetParam("autoRelease") > 0.5;
        int lookSamples = (int)(GetParam("lookahead") / 1000.0 * SampleRate);
        lookSamples = Math.Clamp(lookSamples, 0, _lookaheadLen);

        // RMS detector time constant (~10 ms) and auto-makeup follower (~0.5 s)
        double msCoeff = Math.Exp(-1.0 / (SampleRate * 0.010));
        double makeupCoeff = Math.Exp(-1.0 / (SampleRate * 0.5));

        int frames = count / ChannelCount;
        double maxGr = 0;

        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;

            // --- sidechain detection with HPF ---
            double peak = 0;
            double rmsSum = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                float sample = buffer[idx + c];
                float sc = _sidechainHpf[c].Process(sample);
                double a = Math.Abs(sc);
                if (a > peak) peak = a;
                rmsSum += sc * sc;
            }

            double detector;
            if (useRms)
            {
                // True one-pole mean-square integrator (not a short block average)
                _msState = msCoeff * _msState + (1 - msCoeff) * (rmsSum / ChannelCount);
                detector = Math.Sqrt(Math.Max(1e-12, _msState));
            }
            else
            {
                detector = peak;
            }

            double inDb = 20 * Math.Log10(Math.Max(1e-6, detector));

            // Envelope in dB domain, applied exactly once per frame. With
            // auto-release the release coefficient adapts to the crest factor.
            double relCoeffEff = relCoeff;
            if (autoRelease)
            {
                double crestFactor = inDb - _envDb;
                double adaptiveReleaseMs = releaseMs * (0.3 + 0.7 * Math.Clamp(crestFactor / 12.0, 0, 1));
                relCoeffEff = Math.Exp(-1.0 / (SampleRate * adaptiveReleaseMs / 1000.0));
            }
            _envDb = inDb > _envDb
                ? attCoeff * _envDb + (1 - attCoeff) * inDb
                : relCoeffEff * _envDb + (1 - relCoeffEff) * inDb;

            // soft-knee gain computer
            double over = _envDb - thresh;
            double grDb;
            if (knee <= 0.01)
            {
                grDb = over > 0 ? over * (1 - 1 / ratio) : 0;
            }
            else if (over <= -knee / 2)
            {
                grDb = 0;
            }
            else if (over >= knee / 2)
            {
                grDb = over * (1 - 1 / ratio);
            }
            else
            {
                double x = over + knee / 2;
                grDb = x * x / (2 * knee) * (1 - 1 / ratio);
            }

            if (grDb > maxGr) maxGr = grDb;

            // Auto-makeup: slowly track the average gain reduction and restore it,
            // preserving short-term dynamics while keeping apparent loudness.
            if (autoMakeup)
                _autoMakeupDb = makeupCoeff * _autoMakeupDb + (1 - makeupCoeff) * grDb;
            else
                _autoMakeupDb = 0;

            float gain = (float)(Math.Pow(10, (_autoMakeupDb - grDb) / 20.0) * Math.Pow(10, makeupDb / 20.0));

            // --- lookahead delay (true passthrough at 0 ms) ---
            for (int c = 0; c < ChannelCount; c++)
            {
                float input = buffer[idx + c];
                float delayed;
                if (lookSamples > 0)
                {
                    int readPos = (_lookaheadPos - lookSamples + _lookaheadLen) % _lookaheadLen;
                    delayed = _lookaheadBuf[c][readPos];
                    _lookaheadBuf[c][_lookaheadPos] = input;
                }
                else
                {
                    _lookaheadBuf[c][_lookaheadPos] = input; // keep the ring warm
                    delayed = input;
                }

                float wet = delayed * gain;
                buffer[idx + c] = delayed * dryMix + wet * mix;
            }

            _lookaheadPos = (_lookaheadPos + 1) % _lookaheadLen;
        }

        _grDb = maxGr;
    }
}
