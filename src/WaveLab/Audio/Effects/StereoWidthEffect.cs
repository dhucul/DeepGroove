namespace WaveLab.Audio.Effects;

/// <summary>
/// Mid/side stereo width with optional mono bass and correlation-aware limiting.
/// Scaling only the side channel guarantees that mono fold-down remains unchanged.
/// </summary>
public sealed class StereoWidthEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("width", "WIDTH", 0, 2, 1, EffectParam.Pct),
        new("monoBass", "MONO BASS", 0, 400, 0, v => v < 1 ? "Off" : EffectParam.Hz(v)),
        new("safety", "PHASE SAFE", 0, 1, 1, EffectParam.Pct),
    ];

    private double _lowSide;
    private double _midEnergy;
    private double _sideEnergy;
    private double _safetyGain = 1;
    private double _correlation = 1;

    public override string TypeId => "stereo-width";
    public override string DisplayName => "Stereo Width";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => ChannelCount < 2 ? "STEREO ONLY" : $"CORR {_correlation:+0.00;-0.00;0.00}";

    public override void ResetState()
    {
        _lowSide = 0;
        _midEnergy = 0;
        _sideEnergy = 0;
        _safetyGain = 1;
        _correlation = 1;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (ChannelCount < 2) return;

        double width = GetParam("width");
        double monoBass = GetParam("monoBass");
        double bassAlpha = monoBass > 0 ? 1 - Math.Exp(-2 * Math.PI * monoBass / SampleRate) : 1;
        double safety = GetParam("safety");
        double energyCoefficient = Math.Exp(-1.0 / (SampleRate * 0.05));
        double reduceCoefficient = Math.Exp(-1.0 / (SampleRate * 0.004));
        double recoverCoefficient = Math.Exp(-1.0 / (SampleRate * 0.12));

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double left = buffer[index];
            double right = buffer[index + 1];
            double mid = (left + right) * 0.5;
            double side = (left - right) * 0.5;

            double candidateSide;
            if (monoBass > 0)
            {
                _lowSide += bassAlpha * (side - _lowSide);
                candidateSide = (side - _lowSide) * width;
            }
            else
            {
                _lowSide = side;
                candidateSide = side * width;
            }

            _midEnergy = energyCoefficient * _midEnergy + (1 - energyCoefficient) * mid * mid;
            _sideEnergy = energyCoefficient * _sideEnergy + (1 - energyCoefficient) * candidateSide * candidateSide;

            double targetSafetyGain = 1;
            // Width <= 100% cannot introduce a phase problem. Keeping the safety
            // limiter out of that range also makes the default 100% / mono-bass-off
            // setting an exact pass-through, as users expect from a width control.
            bool processingCanIncreaseSide = width > 1 || monoBass > 0;
            if (processingCanIncreaseSide && safety > 0 && _sideEnergy > 1e-12)
            {
                // Full safety limits side energy just below mid energy (correlation >= 0).
                // Lower settings progressively relax the constraint for creative widening.
                double maxEnergyRatio = 0.95 + (1 - safety) * 7.05;
                double actualRatio = _sideEnergy / Math.Max(1e-12, _midEnergy);
                if (actualRatio > maxEnergyRatio)
                    targetSafetyGain = Math.Sqrt(maxEnergyRatio / actualRatio);
            }

            double smoothing = targetSafetyGain < _safetyGain ? reduceCoefficient : recoverCoefficient;
            _safetyGain = smoothing * _safetyGain + (1 - smoothing) * targetSafetyGain;
            double safeSide = candidateSide * _safetyGain;
            if (Math.Abs(width - 1) > 1e-12 || monoBass > 0)
            {
                buffer[index] = (float)(mid + safeSide);
                buffer[index + 1] = (float)(mid - safeSide);
            }

            double safeSideEnergy = _sideEnergy * _safetyGain * _safetyGain;
            double denominator = _midEnergy + safeSideEnergy;
            _correlation = denominator > 1e-12 ? (_midEnergy - safeSideEnergy) / denominator : 1;
        }
    }
}
