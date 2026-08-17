using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced noise gate: sidechain filter for frequency-conscious gating,
/// range control (downward expansion instead of full mute), hold time,
/// and adjustable hysteresis with attack/release envelope.
/// </summary>
public sealed class GateEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("thresh", "THRESH", -80, -20, -50, EffectParam.Db),
        new("hyst", "HYSTERESIS", 0, 12, 3, EffectParam.Db),
        new("attack", "ATTACK", 0.1, 50, 1, v => $"{v:0.0} ms"),
        new("release", "RELEASE", 20, 2000, 200, EffectParam.Ms),
        new("hold", "HOLD", 0, 500, 20, EffectParam.Ms),
        new("range", "RANGE", -60, 0, -60, EffectParam.Db),
        new("scFilter", "SC FILTER", 0, 2, 0, v => ((int)v) switch { 0 => "OFF", 1 => "LP", _ => "HP" }),
        new("scFreq", "SC FREQ", 40, 8000, 1000, EffectParam.Hz),
    ];

    private double _env;      // linear input envelope
    private double _gain = 1; // smoothed gate gain
    private bool _open = true;
    private int _holdSamples;
    private int _holdCounter;
    private Biquad[] _sidechainFilters = [];   // audio-thread state only
    private BiquadCoefficients _sidechainCoefficients = BiquadCoefficients.Identity;

    public override string TypeId => "gate";
    public override string DisplayName => "Noise Gate";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => _open ? "OPEN" : $"CLOSED ({20 * Math.Log10(Math.Max(1e-9, _gain)):0.0} dB)";

    protected override void OnConfigure()
    {
        _sidechainFilters = new Biquad[ChannelCount];
        RebuildSidechain();
    }

    private void RebuildSidechain()
    {
        int filterType = (int)GetParam("scFilter");
        double freq = GetParam("scFreq");
        // Publish the coefficients rather than writing them into the live filters: this runs on
        // whichever thread moved the knob. Process copies them in, so the sidechain's delay line
        // survives an unrelated knob move and the detector never sees a step.
        Volatile.Write(ref _sidechainCoefficients, new BiquadCoefficients(filterType switch
        {
            1 => Biquad.LowPass12Db(SampleRate, freq),
            2 => Biquad.HighPass12Db(SampleRate, freq),
            _ => Biquad.Identity(),
        }));
    }

    protected override void OnParamsChanged() => RebuildSidechain();

    public override void ResetState()
    {
        _env = 0;
        _gain = 1;
        _open = true;
        _holdCounter = 0;
        // Indexed, not foreach: Biquad is a struct, so foreach would reset copies.
        for (int c = 0; c < _sidechainFilters.Length; c++) _sidechainFilters[c].Reset();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_sidechainFilters.Length != ChannelCount) return;
        Volatile.Read(ref _sidechainCoefficients).ApplyTo(_sidechainFilters);

        double openLin = Math.Pow(10, GetParam("thresh") / 20.0);
        double closeLin = Math.Pow(10, (GetParam("thresh") - GetParam("hyst")) / 20.0);
        double attCoeff = Math.Exp(-1.0 / (SampleRate * GetParam("attack") / 1000.0));
        double relCoeff = Math.Exp(-1.0 / (SampleRate * GetParam("release") / 1000.0));
        double envCoeff = Math.Exp(-1.0 / (SampleRate * 0.002));
        double rangeDb = GetParam("range");
        double rangeGain = Math.Pow(10, rangeDb / 20.0); // minimum gain when closed
        _holdSamples = (int)(GetParam("hold") / 1000.0 * SampleRate);

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            double peak = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                float sc = _sidechainFilters[c].Process(buffer[idx + c]);
                double a = Math.Abs(sc);
                if (a > peak) peak = a;
            }
            _env = Math.Max(peak, envCoeff * _env);

            if (_open && _env < closeLin)
            {
                _open = false;
                _holdCounter = _holdSamples;
            }
            else if (!_open && _env > openLin)
            {
                _open = true;
                _holdCounter = 0;
            }

            // Hold time: keep gate closed for minimum duration
            if (!_open && _holdCounter > 0)
            {
                _holdCounter--;
            }

            bool effectiveOpen = _open || _holdCounter > 0;
            double target = effectiveOpen ? 1 : rangeGain;
            _gain = target > _gain
                ? attCoeff * _gain + (1 - attCoeff) * target
                : relCoeff * _gain + (1 - relCoeff) * target;

            float g = (float)_gain;
            for (int c = 0; c < ChannelCount; c++)
                buffer[idx + c] *= g;
        }
    }
}
