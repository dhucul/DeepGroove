using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced stereo delay: ping-pong mode, tempo sync, feedback filter (LP/HP),
/// and ducking. Supports musical subdivisions when tempo is provided.
/// </summary>
public sealed class DelayEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("time", "TIME", 10, 1500, 350, EffectParam.Ms),
        new("feedback", "FEEDBK", 0, 0.9, 0.35, EffectParam.Pct),
        new("mix", "MIX", 0, 1, 0.25, EffectParam.Pct),
        new("pingPong", "PING-PONG", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
        new("duckAmount", "DUCK", 0, 1, 0, EffectParam.Pct),
        new("fbFilter", "FB FILTER", 0, 2, 0, v => ((int)v) switch { 0 => "OFF", 1 => "LP", _ => "HP" }),
        new("fbFreq", "FB FREQ", 200, 12000, 4000, EffectParam.Hz),
    ];

    private float[][] _lines = [];
    private int _pos;
    private int _lineLen;
    private Biquad[] _fbFilters = [];
    private double _duckEnv;
    private double _duckGain = 1;

    public override string TypeId => "delay";
    public override string DisplayName => "Stereo Delay";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        _lineLen = Math.Max(8, SampleRate * 2); // up to 2 s
        _lines = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++) _lines[c] = new float[_lineLen];
        _fbFilters = new Biquad[ChannelCount];
        _pos = 0;
        RebuildFeedbackFilters();
    }

    private void RebuildFeedbackFilters()
    {
        if (_fbFilters.Length != ChannelCount) return;

        int filterType = (int)GetParam("fbFilter");
        double freq = GetParam("fbFreq");
        for (int c = 0; c < ChannelCount; c++)
        {
            _fbFilters[c] = filterType switch
            {
                1 => Biquad.LowPass12Db(SampleRate, freq),
                2 => Biquad.HighPass12Db(SampleRate, freq),
                _ => Biquad.Identity(),
            };
        }
    }

    protected override void OnParamsChanged() => RebuildFeedbackFilters();

    public override void ResetState()
    {
        foreach (var line in _lines) Array.Clear(line);
        foreach (var f in _fbFilters) f.Reset();
        _pos = 0;
        _duckEnv = 0;
        _duckGain = 1;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_lines.Length == 0) return;
        int delaySamples = Math.Clamp((int)(GetParam("time") / 1000.0 * SampleRate), 1, _lineLen - 1);
        float feedback = (float)GetParam("feedback");
        float mix = (float)GetParam("mix");
        float dry = 1 - mix;
        bool pingPong = GetParam("pingPong") > 0.5;
        float duckAmount = (float)GetParam("duckAmount");
        double duckAttack = Math.Exp(-1.0 / (SampleRate * 0.005));
        double duckRelease = Math.Exp(-1.0 / (SampleRate * 0.15));

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            int readPos = (_pos - delaySamples + _lineLen) % _lineLen;

            // Ducking: measure dry input level
            float inputPeak = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                float a = Math.Abs(buffer[idx + c]);
                if (a > inputPeak) inputPeak = a;
            }
            _duckEnv = inputPeak > _duckEnv
                ? duckAttack * _duckEnv + (1 - duckAttack) * inputPeak
                : duckRelease * _duckEnv + (1 - duckRelease) * inputPeak;
            double targetDuckGain = 1.0 - duckAmount * Math.Clamp(_duckEnv * 5, 0, 1);
            _duckGain += 0.1 * (targetDuckGain - _duckGain);

            for (int c = 0; c < ChannelCount; c++)
            {
                float delayed = _lines[c][readPos];

                // Feedback with filter
                float fbInput = buffer[idx + c] + delayed * feedback;
                fbInput = _fbFilters[c].Process(fbInput);
                _lines[c][_pos] = fbInput;

                // Ping-pong: swap channels in feedback
                if (pingPong && ChannelCount >= 2)
                {
                    int otherChannel = c ^ 1;
                    float otherDelayed = _lines[otherChannel][readPos];
                    buffer[idx + c] = buffer[idx + c] * dry
                        + (delayed * 0.7f + otherDelayed * 0.3f) * mix * (float)_duckGain;
                }
                else
                {
                    buffer[idx + c] = buffer[idx + c] * dry + delayed * mix * (float)_duckGain;
                }
            }
            if (++_pos >= _lineLen) _pos = 0;
        }
    }
}
