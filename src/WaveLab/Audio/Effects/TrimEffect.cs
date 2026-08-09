namespace WaveLab.Audio.Effects;

/// <summary>Click-free gain staging trim for matching levels between rack processors.</summary>
public sealed class TrimEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("gain", "GAIN", -24, 24, 0, EffectParam.Db1),
    ];

    private double _currentGain = 1;
    private double _targetGain = 1;

    public override string TypeId => "trim";
    public override string DisplayName => "Gain & Trim";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"{GetParam("gain"):+0.0;-0.0;0.0} dB";

    protected override void OnParamsChanged() => _targetGain = Math.Pow(10, GetParam("gain") / 20.0);

    public override void ResetState() => _currentGain = _targetGain;

    public override void Process(float[] buffer, int offset, int count)
    {
        double smoothing = 1 - Math.Exp(-1.0 / (SampleRate * 0.005));
        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            _currentGain += smoothing * (_targetGain - _currentGain);
            float gain = (float)_currentGain;
            int index = offset + frame * ChannelCount;
            for (int channel = 0; channel < ChannelCount; channel++)
                buffer[index + channel] *= gain;
        }
    }
}
