namespace WaveLab.Audio.Effects;

/// <summary>
/// Creates a complementary, decorrelated side signal from mono-compatible stereo input.
/// The generated side is added with opposite polarity, so a mono fold-down always returns
/// the original mid signal instead of producing Haas-delay comb filtering.
/// </summary>
public sealed class MonoToStereoEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("amount", "AMOUNT", 0, 1, 0.45, EffectParam.Pct),
        new("delay", "SPACE", 3, 25, 12, v => $"{v:0.0} ms"),
        new("bass", "MONO BASS", 40, 400, 140, EffectParam.Hz),
        new("safety", "MONO SAFE", 0, 1, 0.85, EffectParam.Pct),
    ];

    private float[] _delayLine = [];
    private int _writePosition;
    private double _decorrelationLowPass;
    private double _midEnergy;
    private double _decorrelatedEnergy;
    private double _safetyGain = 1;
    private double _spread;

    public override string TypeId => "mono-stereo";
    public override string DisplayName => "Mono-to-Stereo Enhancer";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => ChannelCount < 2
        ? "STEREO OUTPUT REQUIRED"
        : $"SPREAD {_spread * 100:0}%";

    protected override void OnConfigure()
    {
        // Parameters never resize this line, keeping slider moves allocation-free.
        _delayLine = new float[Math.Max(32, (int)Math.Ceiling(SampleRate * 0.03) + 2)];
    }

    public override void ResetState()
    {
        Array.Clear(_delayLine);
        _writePosition = 0;
        _decorrelationLowPass = 0;
        _midEnergy = 0;
        _decorrelatedEnergy = 0;
        _safetyGain = 1;
        _spread = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (ChannelCount < 2 || _delayLine.Length == 0) return;

        double amount = GetParam("amount");
        double delaySamples = Math.Clamp(GetParam("delay") * SampleRate / 1000.0, 1, _delayLine.Length - 2);
        double highPassAlpha = 1 - Math.Exp(-2 * Math.PI * GetParam("bass") / SampleRate);
        double energyCoefficient = Math.Exp(-1.0 / (SampleRate * 0.08));
        double safety = GetParam("safety");
        double reduceCoefficient = Math.Exp(-1.0 / (SampleRate * 0.005));
        double recoverCoefficient = Math.Exp(-1.0 / (SampleRate * 0.12));

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            float left = buffer[index];
            float right = buffer[index + 1];
            double mid = (left + right) * 0.5;
            double originalSide = (left - right) * 0.5;

            _delayLine[_writePosition] = (float)mid;
            double readPosition = _writePosition - delaySamples;
            if (readPosition < 0) readPosition += _delayLine.Length;
            int read0 = (int)readPosition;
            int read1 = read0 + 1;
            if (read1 == _delayLine.Length) read1 = 0;
            double fraction = readPosition - read0;
            double delayed = _delayLine[read0] * (1 - fraction) + _delayLine[read1] * fraction;
            if (++_writePosition == _delayLine.Length) _writePosition = 0;

            // Removing low-frequency content from the decorrelator keeps bass centered.
            double difference = mid - delayed;
            _decorrelationLowPass += highPassAlpha * (difference - _decorrelationLowPass);
            double decorrelated = difference - _decorrelationLowPass;

            _midEnergy = energyCoefficient * _midEnergy + (1 - energyCoefficient) * mid * mid;
            _decorrelatedEnergy = energyCoefficient * _decorrelatedEnergy
                                  + (1 - energyCoefficient) * decorrelated * decorrelated;

            // At full safety, synthetic side RMS stays well below mid RMS. This keeps
            // newly widened mono sources positively correlated while retaining width.
            double targetSafetyGain = 1;
            if (safety > 0 && amount > 0 && _decorrelatedEnergy > 1e-12)
            {
                double maxSideRatio = 0.85 - 0.5 * safety;
                double requestedRatio = amount * Math.Sqrt(_decorrelatedEnergy / Math.Max(1e-12, _midEnergy));
                if (requestedRatio > maxSideRatio)
                    targetSafetyGain = maxSideRatio / requestedRatio;
            }

            double smoothing = targetSafetyGain < _safetyGain ? reduceCoefficient : recoverCoefficient;
            _safetyGain = smoothing * _safetyGain + (1 - smoothing) * targetSafetyGain;
            double syntheticSide = decorrelated * amount * _safetyGain;
            _spread = amount * _safetyGain;

            if (amount <= 1e-9) continue;
            double side = originalSide + syntheticSide;
            buffer[index] = (float)(mid + side);
            buffer[index + 1] = (float)(mid - side);
        }
    }
}
