namespace WaveLab.Audio.Effects;

/// <summary>Stereo feedback delay.</summary>
public sealed class DelayEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("time", "TIME", 10, 1500, 350, EffectParam.Ms),
        new("feedback", "FEEDBK", 0, 0.9, 0.35, EffectParam.Pct),
        new("mix", "MIX", 0, 1, 0.25, EffectParam.Pct),
    ];

    private float[][] _lines = [];
    private int _pos;
    private int _lineLen;

    public override string TypeId => "delay";
    public override string DisplayName => "Stereo Delay";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        _lineLen = Math.Max(8, SampleRate * 2); // up to 2 s
        _lines = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++) _lines[c] = new float[_lineLen];
        _pos = 0;
    }

    public override void ResetState()
    {
        foreach (var line in _lines) Array.Clear(line);
        _pos = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_lines.Length == 0) return;
        int delaySamples = Math.Clamp((int)(GetParam("time") / 1000.0 * SampleRate), 1, _lineLen - 1);
        float feedback = (float)GetParam("feedback");
        float mix = (float)GetParam("mix");
        float dry = 1 - mix;

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            int readPos = (_pos - delaySamples + _lineLen) % _lineLen;
            for (int c = 0; c < ChannelCount; c++)
            {
                float delayed = _lines[c][readPos];
                _lines[c][_pos] = buffer[idx + c] + delayed * feedback;
                buffer[idx + c] = buffer[idx + c] * dry + delayed * mix;
            }
            if (++_pos >= _lineLen) _pos = 0;
        }
    }
}
