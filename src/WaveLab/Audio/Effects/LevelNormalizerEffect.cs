namespace WaveLab.Audio.Effects;

/// <summary>
/// Conservative linked-channel automatic level control. It follows programme RMS
/// slowly, leaves material below the noise gate unboosted, and applies one gain to
/// every channel so the stereo image and inter-channel phase relationship are kept.
/// This is a real-time leveller; exact whole-file peak normalization remains an
/// offline operation.
/// </summary>
public sealed class LevelNormalizerEffect : EffectBase
{
    private const int ControlIntervalFrames = 32;

    private static readonly EffectParam[] P =
    [
        new("target", "TARGET RMS", -30, -10, -18, EffectParam.Db),
        new("maxBoost", "MAX BOOST", 0, 18, 6, EffectParam.Db),
        new("maxCut", "MAX CUT", 0, 18, 12, EffectParam.Db),
        new("gate", "NOISE FLOOR", -80, -35, -55, EffectParam.Db),
        new("response", "RESPONSE", 250, 5000, 1500, EffectParam.Ms),
    ];

    private double _meanSquare;
    private double _currentGain = 1;
    private double _targetGain = 1;
    private double _gainReadoutDb;
    private int _controlCountdown;

    public override string TypeId => "normalizer";
    public override string DisplayName => "Level Normalizer";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"GAIN {_gainReadoutDb:+0.0;-0.0;0.0} dB";

    public override void ResetState()
    {
        _meanSquare = 0;
        _currentGain = 1;
        _targetGain = 1;
        _gainReadoutDb = 0;
        _controlCountdown = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        int frames = count / ChannelCount;
        if (frames <= 0) return;

        // RMS detector reacts in musical time rather than to individual peaks.
        double detectorCoefficient = Math.Exp(-1.0 / (SampleRate * 0.10));
        double responseSeconds = GetParam("response") / 1000.0;
        double increaseCoefficient = Math.Exp(-1.0 / (SampleRate * responseSeconds));
        double reduceCoefficient = Math.Exp(-1.0 / (SampleRate * Math.Max(0.05, responseSeconds * 0.20)));
        double targetLevelDb = GetParam("target");
        double maximumBoostDb = GetParam("maxBoost");
        double maximumCutDb = GetParam("maxCut");
        double noiseFloorDb = GetParam("gate");

        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double power = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                double sample = buffer[index + channel];
                power += sample * sample;
            }
            power /= ChannelCount;

            _meanSquare = detectorCoefficient * _meanSquare + (1 - detectorCoefficient) * power;

            if (_controlCountdown-- <= 0)
            {
                _controlCountdown = ControlIntervalFrames - 1;
                double levelDb = 10 * Math.Log10(Math.Max(1e-12, _meanSquare));
                double desiredGainDb = levelDb <= noiseFloorDb
                    ? 0
                    : Math.Clamp(targetLevelDb - levelDb, -maximumCutDb, maximumBoostDb);
                _targetGain = Math.Pow(10, desiredGainDb / 20.0);
            }

            double smoothing = _targetGain < _currentGain ? reduceCoefficient : increaseCoefficient;
            _currentGain = smoothing * _currentGain + (1 - smoothing) * _targetGain;
            float gain = (float)_currentGain;
            for (int channel = 0; channel < ChannelCount; channel++)
                buffer[index + channel] *= gain;
        }

        _gainReadoutDb = 20 * Math.Log10(Math.Max(1e-12, _currentGain));
    }
}
