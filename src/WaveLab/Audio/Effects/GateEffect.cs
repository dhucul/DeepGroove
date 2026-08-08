namespace WaveLab.Audio.Effects;

/// <summary>Noise gate with 3 dB hysteresis and attack/release envelope.</summary>
public sealed class GateEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("thresh", "THRESH", -80, -20, -50, EffectParam.Db),
        new("attack", "ATTACK", 0.1, 50, 1, v => $"{v:0.0} ms"),
        new("release", "RELEASE", 20, 2000, 200, EffectParam.Ms),
    ];

    private double _env;      // linear input envelope
    private double _gain = 1; // smoothed gate gain
    private bool _open = true;

    public override string TypeId => "gate";
    public override string DisplayName => "Noise Gate";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => _open ? "OPEN" : "CLOSED";

    public override void ResetState() { _env = 0; _gain = 1; _open = true; }

    public override void Process(float[] buffer, int offset, int count)
    {
        double openLin = Math.Pow(10, GetParam("thresh") / 20.0);
        double closeLin = Math.Pow(10, (GetParam("thresh") - 3) / 20.0);
        double attCoeff = Math.Exp(-1.0 / (SampleRate * GetParam("attack") / 1000.0));
        double relCoeff = Math.Exp(-1.0 / (SampleRate * GetParam("release") / 1000.0));
        double envCoeff = Math.Exp(-1.0 / (SampleRate * 0.002));

        int frames = count / ChannelCount;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * ChannelCount;
            double peak = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                double a = Math.Abs(buffer[idx + c]);
                if (a > peak) peak = a;
            }
            _env = Math.Max(peak, envCoeff * _env);

            if (_open && _env < closeLin) _open = false;
            else if (!_open && _env > openLin) _open = true;

            double target = _open ? 1 : 0;
            _gain = target > _gain
                ? attCoeff * _gain + (1 - attCoeff) * target
                : relCoeff * _gain + (1 - relCoeff) * target;

            float g = (float)_gain;
            for (int c = 0; c < ChannelCount; c++)
                buffer[idx + c] *= g;
        }
    }
}
