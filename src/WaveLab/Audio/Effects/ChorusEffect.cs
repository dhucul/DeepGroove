namespace WaveLab.Audio.Effects;

/// <summary>Chorus: sine-modulated delay (~20 ms base) with per-channel LFO phase offset.</summary>
public sealed class ChorusEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("rate", "RATE", 0.05, 5, 0.8, v => $"{v:0.00} Hz"),
        new("depth", "DEPTH", 1, 15, 6, EffectParam.Ms),
        new("mix", "MIX", 0, 1, 0.35, EffectParam.Pct),
    ];

    private const double BaseDelayMs = 20;

    private float[][] _lines = [];
    private int _lineLen;
    private int _pos;
    private double _phase;

    public override string TypeId => "chorus";
    public override string DisplayName => "Chorus";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        _lineLen = Math.Max(16, (int)(SampleRate * 0.06)); // 60 ms max
        _lines = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++) _lines[c] = new float[_lineLen];
        _pos = 0;
        _phase = 0;
    }

    public override void ResetState()
    {
        foreach (var line in _lines) Array.Clear(line);
        _pos = 0;
        _phase = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_lines.Length == 0) return;
        double rate = GetParam("rate");
        double depthSamples = GetParam("depth") / 1000.0 * SampleRate;
        double baseSamples = BaseDelayMs / 1000.0 * SampleRate;
        float mix = (float)GetParam("mix");
        float dry = 1 - mix;
        double phaseInc = 2 * Math.PI * rate / SampleRate;

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            for (int c = 0; c < ChannelCount; c++)
            {
                _lines[c][_pos] = buffer[idx + c];
                double lfo = Math.Sin(_phase + c * Math.PI / 2);
                double delay = baseSamples + depthSamples * (0.5 + 0.5 * lfo);
                delay = Math.Min(delay, _lineLen - 2);
                double readPos = _pos - delay;
                while (readPos < 0) readPos += _lineLen;
                int i0 = (int)readPos;
                double frac = readPos - i0;
                int i1 = (i0 + 1) % _lineLen;
                float delayed = (float)(_lines[c][i0] * (1 - frac) + _lines[c][i1] * frac);
                buffer[idx + c] = buffer[idx + c] * dry + delayed * mix;
            }
            _phase += phaseInc;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            if (++_pos >= _lineLen) _pos = 0;
        }
    }
}
