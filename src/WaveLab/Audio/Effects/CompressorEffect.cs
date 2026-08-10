using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced feed-forward compressor: peak or RMS detection, soft knee (6 dB),
/// attack/release smoothing, optional lookahead, sidechain high-pass filter,
/// parallel wet/dry mix, and program-dependent auto-release.
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
    private double _rmsAccum;
    private int _rmsCount;
    private const int RmsWindowFrames = 64;

    public override string TypeId => "compressor";
    public override string DisplayName => "Compressor";
    public override IReadOnlyList<EffectParam> Params => P;
    public override int LatencySamples => _lookaheadLen;
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
        _rmsAccum = 0;
        _rmsCount = 0;
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
        double makeup = Math.Pow(10, GetParam("makeup") / 20.0);
        double knee = GetParam("knee");
        float mix = (float)GetParam("mix");
        float dryMix = 1 - mix;
        bool useRms = GetParam("rmsMode") > 0.5;
        bool autoRelease = GetParam("autoRelease") > 0.5;
        int lookSamples = (int)(GetParam("lookahead") / 1000.0 * SampleRate);
        lookSamples = Math.Clamp(lookSamples, 0, _lookaheadLen);

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
                _rmsAccum += rmsSum / ChannelCount;
                _rmsCount++;
                if (_rmsCount >= RmsWindowFrames)
                {
                    _rmsAccum /= _rmsCount;
                    _rmsCount = 0;
                }
                detector = Math.Sqrt(Math.Max(1e-12, _rmsAccum));
            }
            else
            {
                detector = peak;
            }

            double inDb = 20 * Math.Log10(Math.Max(1e-6, detector));

            // envelope in dB domain
            _envDb = inDb > _envDb
                ? attCoeff * _envDb + (1 - attCoeff) * inDb
                : relCoeff * _envDb + (1 - relCoeff) * inDb;

            // program-dependent auto-release
            if (autoRelease)
            {
                double crestFactor = inDb - _envDb;
                double adaptiveReleaseMs = releaseMs * (0.3 + 0.7 * Math.Clamp(crestFactor / 12.0, 0, 1));
                double adaptiveCoeff = Math.Exp(-1.0 / (SampleRate * adaptiveReleaseMs / 1000.0));
                _envDb = inDb > _envDb
                    ? attCoeff * _envDb + (1 - attCoeff) * inDb
                    : adaptiveCoeff * _envDb + (1 - adaptiveCoeff) * inDb;
            }

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

            float gain = (float)(Math.Pow(10, -grDb / 20.0) * makeup);

            // --- lookahead delay ---
            for (int c = 0; c < ChannelCount; c++)
            {
                float delayed = _lookaheadBuf[c][_lookaheadPos];
                _lookaheadBuf[c][_lookaheadPos] = buffer[idx + c];

                if (lookSamples > 0)
                {
                    int readPos = (_lookaheadPos - lookSamples + _lookaheadLen) % _lookaheadLen;
                    delayed = _lookaheadBuf[c][readPos];
                }

                float wet = delayed * gain;
                buffer[idx + c] = delayed * dryMix + wet * mix;
            }

            _lookaheadPos = (_lookaheadPos + 1) % _lookaheadLen;
        }

        _grDb = maxGr;
    }
}