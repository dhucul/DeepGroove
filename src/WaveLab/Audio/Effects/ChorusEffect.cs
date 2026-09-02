namespace WaveLab.Audio.Effects;

/// <summary>
/// Multi-voice chorus: up to 4 independent delay voices with detuned LFOs,
/// selectable sine/triangle waveform, feedback path, and stereo spread.
/// </summary>
public sealed class ChorusEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("rate", "RATE", 0.05, 5, 0.8, v => $"{v:0.00} Hz"),
        new("depth", "DEPTH", 1, 15, 6, EffectParam.Ms),
        new("mix", "MIX", 0, 1, 0.35, EffectParam.Pct),
        new("voices", "VOICES", 1, 4, 2, v => $"{Math.Round(v):0}"),
        new("feedback", "FEEDBK", 0, 0.7, 0, EffectParam.Pct),
        new("spread", "SPREAD", 0, 1, 0.6, EffectParam.Pct),
        new("waveform", "LFO WAVE", 0, 1, 0, v => v > 0.5 ? "TRI" : "SINE"),
    ];

    private const double BaseDelayMs = 20;
    private const int MaxVoices = 4;

    /// <summary>Per-voice detune amounts (in Hz) — constant, so never per-buffer.</summary>
    private static readonly double[] Detunes = [0, 0.15, -0.12, 0.18];

    private float[][] _lines = [];
    private int _lineLen;
    private int _pos;
    private double[] _phases = [];
    private float[] _prevFeedback = []; // per channel: wet feedback must not bleed across L/R
    private double[] _voiceSin = [];    // one LFO evaluation per voice per frame
    private double[] _voiceCos = [];
    private double[] _spreadSin = [];   // per-channel spread offset (block constant)
    private double[] _spreadCos = [];

    public override string TypeId => "chorus";
    public override string DisplayName => "Chorus";
    public override IReadOnlyList<EffectParam> Params => P;
    public override int TailSamples
    {
        get
        {
            const double silence = 0.001;
            double wet = GetParam("mix");
            if (wet <= silence) return 0;
            double feedback = GetParam("feedback");
            int cycles = feedback <= 1e-9
                ? 1
                : Math.Max(1, 1 + (int)Math.Ceiling(Math.Max(0,
                    Math.Log(silence / wet) / Math.Log(feedback))));
            double longestDelay = (BaseDelayMs + GetParam("depth")) * SampleRate / 1000.0;
            return (int)Math.Min(Math.Ceiling(longestDelay) * cycles, SampleRate * 10.0);
        }
    }

    protected override void OnConfigure()
    {
        _lineLen = Math.Max(16, (int)(SampleRate * 0.06)); // 60 ms max
        _lines = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++) _lines[c] = new float[_lineLen];
        _phases = new double[MaxVoices];
        for (int v = 0; v < MaxVoices; v++)
            _phases[v] = v * Math.PI / MaxVoices; // spread initial phases
        _pos = 0;
        _prevFeedback = new float[ChannelCount];
        _voiceSin = new double[MaxVoices];
        _voiceCos = new double[MaxVoices];
        _spreadSin = new double[ChannelCount];
        _spreadCos = new double[ChannelCount];
    }

    public override void ResetState()
    {
        foreach (var line in _lines) Array.Clear(line);
        _pos = 0;
        for (int v = 0; v < MaxVoices; v++)
            _phases[v] = v * Math.PI / MaxVoices;
        Array.Clear(_prevFeedback);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_lines.Length == 0 || _prevFeedback.Length != ChannelCount
            || _spreadSin.Length != ChannelCount) return;
        double rate = GetParam("rate");
        double depthSamples = GetParam("depth") / 1000.0 * SampleRate;
        double baseSamples = BaseDelayMs / 1000.0 * SampleRate;
        float mix = (float)GetParam("mix");
        float dry = 1 - mix;
        int voices = (int)Math.Round(GetParam("voices"));
        float feedback = (float)GetParam("feedback");
        float spread = (float)GetParam("spread");
        bool useTriangle = GetParam("waveform") > 0.5;
        double phaseInc = 2 * Math.PI * rate / SampleRate;

        // The per-channel spread offset is block-constant, so its sine and cosine
        // are computed once here and combined with the per-voice LFO below.
        for (int c = 0; c < ChannelCount; c++)
            (_spreadSin[c], _spreadCos[c]) = Math.SinCos(c * Math.PI / 2 * spread);

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;

            // One LFO evaluation per voice per frame instead of one per voice per
            // channel per sample; the channel offset is applied by angle-sum below.
            if (!useTriangle)
                for (int v = 0; v < voices; v++)
                    (_voiceSin[v], _voiceCos[v]) = Math.SinCos(_phases[v]);

            for (int c = 0; c < ChannelCount; c++)
            {
                float input = buffer[idx + c] + _prevFeedback[c] * feedback;
                _lines[c][_pos] = input;
                double spreadOffset = c * Math.PI / 2 * spread;

                float wetSum = 0;
                for (int v = 0; v < voices; v++)
                {
                    double lfo;
                    if (useTriangle)
                    {
                        // Triangle wave
                        double phase = _phases[v] + spreadOffset;
                        double tri = (phase % (2 * Math.PI)) / (2 * Math.PI);
                        if (tri < 0) tri += 1;
                        lfo = tri < 0.5 ? tri * 4 - 1 : 3 - tri * 4;
                    }
                    else
                    {
                        // sin(phase + offset) = sin·cos + cos·sin
                        lfo = _voiceSin[v] * _spreadCos[c] + _voiceCos[v] * _spreadSin[c];
                    }

                    double delay = baseSamples + depthSamples * (0.5 + 0.5 * lfo);
                    delay = Math.Min(delay, _lineLen - 2);
                    double readPos = _pos - delay;
                    while (readPos < 0) readPos += _lineLen;
                    int i0 = (int)readPos;
                    double frac = readPos - i0;
                    int i1 = (i0 + 1) % _lineLen;
                    float delayed = (float)(_lines[c][i0] * (1 - frac) + _lines[c][i1] * frac);
                    wetSum += delayed;
                }
                wetSum /= voices;

                buffer[idx + c] = buffer[idx + c] * dry + wetSum * mix;
                _prevFeedback[c] = wetSum;
            }

            for (int v = 0; v < MaxVoices; v++)
            {
                _phases[v] += phaseInc * (1 + Detunes[v]);
                if (_phases[v] > 2 * Math.PI) _phases[v] -= 2 * Math.PI;
            }

            if (++_pos >= _lineLen) _pos = 0;
        }
    }
}
