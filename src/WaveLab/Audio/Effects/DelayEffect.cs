using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced stereo delay: true ping-pong (cross-fed feedback), feedback filter (LP/HP),
/// ducking, and slew-smoothed delay-time changes that glide instead of clicking.
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
    private Biquad[] _fbFilters = [];   // audio-thread state only
    private BiquadCoefficients _fbCoefficients = BiquadCoefficients.Identity;
    private double _duckEnv;
    private double _duckGain = 1;
    private double _delaySmoothed;
    private float[] _delayedScratch = [];
    private float[] _fbScratch = [];

    public override string TypeId => "delay";
    public override string DisplayName => "Stereo Delay";
    public override IReadOnlyList<EffectParam> Params => P;
    public override int TailSamples => FeedbackTailSamples(
        GetParam("time") * SampleRate / 1000.0,
        GetParam("feedback"),
        GetParam("mix"));

    /// <summary>A practical -60 dB decay point for an explicit repeat generator.</summary>
    private int FeedbackTailSamples(double cycleSamples, double feedback, double wetLevel)
    {
        const double silence = 0.001;
        if (wetLevel <= silence || cycleSamples <= 0) return 0;

        int repeats = 1;
        if (feedback > 1e-9)
        {
            double decayCycles = Math.Log(silence / wetLevel) / Math.Log(feedback);
            repeats = Math.Max(1, 1 + (int)Math.Ceiling(Math.Max(0, decayCycles)));
        }

        long samples = (long)Math.Ceiling(cycleSamples) * repeats;
        return (int)Math.Min(samples, (long)SampleRate * 120);
    }

    protected override void OnConfigure()
    {
        _lineLen = Math.Max(8, SampleRate * 2); // up to 2 s
        _lines = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++) _lines[c] = new float[_lineLen];
        _fbFilters = new Biquad[ChannelCount];
        _delayedScratch = new float[ChannelCount];
        _fbScratch = new float[ChannelCount];
        _pos = 0;
        RebuildFeedbackFilters();
    }

    private void RebuildFeedbackFilters()
    {
        int filterType = (int)GetParam("fbFilter");
        double freq = GetParam("fbFreq");
        // Publish the coefficients rather than writing them into the live filters: this runs on
        // whichever thread moved the knob, and this filter sits *inside* the feedback loop, so
        // Process copies them in — moving MIX or TIME leaves running repeats continuous.
        Volatile.Write(ref _fbCoefficients, new BiquadCoefficients(filterType switch
        {
            1 => Biquad.LowPass12Db(SampleRate, freq),
            2 => Biquad.HighPass12Db(SampleRate, freq),
            _ => Biquad.Identity(),
        }));
    }

    protected override void OnParamsChanged() => RebuildFeedbackFilters();

    public override void ResetState()
    {
        foreach (var line in _lines) Array.Clear(line);
        // Indexed, not foreach: Biquad is a struct, so foreach would reset copies.
        for (int c = 0; c < _fbFilters.Length; c++) _fbFilters[c].Reset();
        _pos = 0;
        _duckEnv = 0;
        _duckGain = 1;
        _delaySmoothed = GetParam("time") / 1000.0 * SampleRate;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_lines.Length == 0) return;
        if (_fbFilters.Length != ChannelCount) return;
        Volatile.Read(ref _fbCoefficients).ApplyTo(_fbFilters);

        double targetDelay = Math.Clamp(GetParam("time") / 1000.0 * SampleRate, 1, _lineLen - 2);
        float feedback = (float)GetParam("feedback");
        float mix = (float)GetParam("mix");
        float dry = 1 - mix;
        // c ^ 1 pairs channels 0↔1, 2↔3… — an odd channel count would index past
        // the delay lines, so ping-pong requires an even channel count.
        bool pingPong = GetParam("pingPong") > 0.5 && ChannelCount >= 2 && ChannelCount % 2 == 0;

        float duckAmount = (float)GetParam("duckAmount");
        double duckAttack = Math.Exp(-1.0 / (SampleRate * 0.005));
        double duckRelease = Math.Exp(-1.0 / (SampleRate * 0.15));
        // ~40 ms slew: delay-time moves glide like a tape deck instead of clicking
        double slew = 1.0 / (SampleRate * 0.04);

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;

            // Smooth the delay time, then read with linear interpolation
            _delaySmoothed += (targetDelay - _delaySmoothed) * slew;
            double readOffset = _pos - _delaySmoothed;
            readOffset -= Math.Floor(readOffset / _lineLen) * _lineLen;
            int i0 = (int)readOffset;
            double frac = readOffset - i0;
            int i1 = (i0 + 1) % _lineLen;

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
                float delayed = (float)(_lines[c][i0] * (1 - frac) + _lines[c][i1] * frac);
                _delayedScratch[c] = delayed;

                // The filter belongs inside the feedback loop. Filtering the sum of the dry
                // injection and the return coloured the very first repeat too, even though the
                // control is explicitly a feedback filter. It also meant a high-pass setting
                // could turn a clean first echo into a thin click before feedback had occurred.
                float filteredReturn = _fbFilters[c].Process(delayed);
                _fbScratch[c] = buffer[idx + c] + filteredReturn * feedback;
            }

            // Ping-pong: write each channel's feedback into the opposite delay
            // line so the repeats genuinely bounce between left and right.
            for (int c = 0; c < ChannelCount; c++)
            {
                int dest = pingPong ? c ^ 1 : c;
                _lines[dest][_pos] = _fbScratch[c];
            }

            for (int c = 0; c < ChannelCount; c++)
                buffer[idx + c] = buffer[idx + c] * dry + _delayedScratch[c] * mix * (float)_duckGain;

            if (++_pos >= _lineLen) _pos = 0;
        }
    }
}
