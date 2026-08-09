namespace WaveLab.Audio.Effects;

/// <summary>
/// Real-time, stereo-linked broadband noise expansion plus signal-dependent hiss reduction.
/// Fast opening preserves transients; slow, adjustable closing avoids pumping on musical tails.
/// </summary>
public sealed class NoiseReductionEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("threshold", "NOISE FLOOR", -90, -30, -60, EffectParam.Db),
        new("reduction", "NOISE REDUCE", 0, 30, 10, EffectParam.Db),
        new("hiss", "HISS REDUCE", 0, 24, 8, EffectParam.Db),
        new("hissFreq", "HISS ABOVE", 3000, 12000, 5500, EffectParam.Hz),
        new("release", "RELEASE", 50, 1000, 280, EffectParam.Ms),
    ];

    private double[] _hissLowPass = [];
    private double _envelope;
    private double _noiseGain = 1;
    private double _hissGain = 1;
    private double _reductionReadout;

    public override string TypeId => "denoise";
    public override string DisplayName => "Noise & Hiss Reduction";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"NR {_reductionReadout:0.0} dB";

    protected override void OnConfigure() => _hissLowPass = new double[ChannelCount];

    public override void ResetState()
    {
        Array.Clear(_hissLowPass);
        _envelope = 0;
        _noiseGain = 1;
        _hissGain = 1;
        _reductionReadout = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_hissLowPass.Length != ChannelCount) return;

        double threshold = GetParam("threshold");
        double noiseReduction = GetParam("reduction");
        double hissReduction = GetParam("hiss");
        double hissFrequency = Math.Min(GetParam("hissFreq"), SampleRate * 0.45);
        double splitAlpha = 1 - Math.Exp(-2 * Math.PI * hissFrequency / SampleRate);
        double detectorAttack = Math.Exp(-1.0 / (SampleRate * 0.002));
        double detectorRelease = Math.Exp(-1.0 / (SampleRate * 0.08));
        double openCoefficient = Math.Exp(-1.0 / (SampleRate * 0.004));
        double closeCoefficient = Math.Exp(-1.0 / (SampleRate * GetParam("release") / 1000.0));

        int frames = count / ChannelCount;
        double maximumReduction = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double peak = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
                peak = Math.Max(peak, Math.Abs(buffer[index + channel]));

            double detectorCoefficient = peak > _envelope ? detectorAttack : detectorRelease;
            _envelope = detectorCoefficient * _envelope + (1 - detectorCoefficient) * peak;
            double levelDb = 20 * Math.Log10(Math.Max(1e-9, _envelope));
            double depth = Math.Clamp((threshold - levelDb) / 24.0, 0, 1);
            double targetNoiseGain = Math.Pow(10, -noiseReduction * depth / 20.0);
            double targetHissGain = Math.Pow(10, -hissReduction * depth / 20.0);

            double noiseCoefficient = targetNoiseGain > _noiseGain ? openCoefficient : closeCoefficient;
            double hissCoefficient = targetHissGain > _hissGain ? openCoefficient : closeCoefficient;
            _noiseGain = noiseCoefficient * _noiseGain + (1 - noiseCoefficient) * targetNoiseGain;
            _hissGain = hissCoefficient * _hissGain + (1 - hissCoefficient) * targetHissGain;

            for (int channel = 0; channel < ChannelCount; channel++)
            {
                double input = buffer[index + channel];
                _hissLowPass[channel] += splitAlpha * (input - _hissLowPass[channel]);
                double high = input - _hissLowPass[channel];
                buffer[index + channel] = (float)((_hissLowPass[channel] + high * _hissGain) * _noiseGain);
            }

            double currentReduction = -20 * Math.Log10(Math.Max(1e-9, _noiseGain));
            if (currentReduction > maximumReduction) maximumReduction = currentReduction;
        }
        _reductionReadout = maximumReduction;
    }
}
