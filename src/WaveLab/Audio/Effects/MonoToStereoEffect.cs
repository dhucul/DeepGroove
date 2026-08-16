using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced mono-to-stereo enhancer with multiple decorrelation algorithms:
/// delay-based, all-pass filter network, and micro-pitch shift.
/// Includes mono bass keep, correlation safety limiter, and frequency-dependent spread.
/// </summary>
public sealed class MonoToStereoEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("amount", "AMOUNT", 0, 1, 0.45, EffectParam.Pct),
        new("delay", "SPACE", 3, 25, 12, v => $"{v:0.0} ms"),
        new("bass", "MONO BASS", 40, 400, 140, EffectParam.Hz),
        new("safety", "MONO SAFE", 0, 1, 0.85, EffectParam.Pct),
        new("algorithm", "ALGORITHM", 0, 2, 0, v => ((int)v) switch
        {
            0 => "DELAY",
            1 => "ALL-PASS",
            _ => "PITCH",
        }),
    ];

    private float[] _delayLine = [];
    private int _writePosition;
    private double _decorrelationLowPass;
    private double _midEnergy;
    private double _decorrelatedEnergy;
    private double _safetyGain = 1;
    private double _spread;
    private Biquad[] _allPassFilters = [];
    private double _pitchPhase;
    private float[] _pitchDelayLine = [];
    private int _pitchWritePos;

    public override string TypeId => "mono-stereo";
    public override string DisplayName => "Mono-to-Stereo Enhancer";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => ChannelCount < 2
        ? "STEREO OUTPUT REQUIRED"
        : $"SPREAD {_spread * 100:0}%";

    protected override void OnConfigure()
    {
        _delayLine = new float[Math.Max(32, (int)Math.Ceiling(SampleRate * 0.03) + 2)];
        _pitchDelayLine = new float[Math.Max(32, (int)Math.Ceiling(SampleRate * 0.05) + 2)];
        _allPassFilters = new Biquad[4];
        RebuildAllPasses();
    }

    private void RebuildAllPasses()
    {
        // All-pass network at different frequencies for decorrelation
        double[] apFreqs = [250, 700, 1800, 4500];
        for (int i = 0; i < 4; i++)
            _allPassFilters[i] = Biquad.AllPass(SampleRate, apFreqs[i], 0.707);
    }

    public override void ResetState()
    {
        Array.Clear(_delayLine);
        Array.Clear(_pitchDelayLine);
        _writePosition = 0;
        _pitchWritePos = 0;
        _decorrelationLowPass = 0;
        _midEnergy = 0;
        _decorrelatedEnergy = 0;
        _safetyGain = 1;
        _spread = 0;
        _pitchPhase = 0;
        // Indexed, not foreach: Biquad is a struct, so foreach would reset copies
        // and the previous take's tail would leak into the synthetic side signal.
        for (int i = 0; i < _allPassFilters.Length; i++) _allPassFilters[i].Reset();
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
        int algorithm = (int)GetParam("algorithm");

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            float left = buffer[index];
            float right = buffer[index + 1];
            double mid = (left + right) * 0.5;
            double originalSide = (left - right) * 0.5;

            _delayLine[_writePosition] = (float)mid;

            double decorrelated;
            switch (algorithm)
            {
                case 1: // All-pass network
                    decorrelated = AllPassDecorrelation(mid);
                    break;
                case 2: // Micro-pitch shift
                    decorrelated = PitchShiftDecorrelation(mid);
                    break;
                default: // Delay-based (original)
                    decorrelated = DelayBasedDecorrelation(mid, delaySamples, highPassAlpha);
                    break;
            }

            _midEnergy = energyCoefficient * _midEnergy + (1 - energyCoefficient) * mid * mid;
            _decorrelatedEnergy = energyCoefficient * _decorrelatedEnergy
                                  + (1 - energyCoefficient) * decorrelated * decorrelated;

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

            if (amount > 1e-9)
            {
                double side = originalSide + syntheticSide;
                buffer[index] = (float)(mid + side);
                buffer[index + 1] = (float)(mid - side);
            }

            if (++_writePosition == _delayLine.Length) _writePosition = 0;
        }
    }

    private double DelayBasedDecorrelation(double mid, double delaySamples, double highPassAlpha)
    {
        double readPosition = _writePosition - delaySamples;
        if (readPosition < 0) readPosition += _delayLine.Length;
        int read0 = (int)readPosition;
        int read1 = read0 + 1;
        if (read1 == _delayLine.Length) read1 = 0;
        double fraction = readPosition - read0;
        double delayed = _delayLine[read0] * (1 - fraction) + _delayLine[read1] * fraction;

        double difference = mid - delayed;
        _decorrelationLowPass += highPassAlpha * (difference - _decorrelationLowPass);
        return difference - _decorrelationLowPass;
    }

    private double AllPassDecorrelation(double mid)
    {
        float x = (float)mid;
        for (int i = 0; i < 4; i++)
            x = _allPassFilters[i].Process(x);
        return x - mid; // difference signal
    }

    private double PitchShiftDecorrelation(double mid)
    {
        // Micro pitch-shift via modulated delay (very subtle, ~5 cents)
        _pitchPhase += 2 * Math.PI * 0.3 / SampleRate; // 0.3 Hz modulation
        if (_pitchPhase > 2 * Math.PI) _pitchPhase -= 2 * Math.PI;

        double modDelay = 2.0 + Math.Sin(_pitchPhase) * 1.5; // 0.5-3.5 sample modulation
        _pitchDelayLine[_pitchWritePos] = (float)mid;
        double readPos = _pitchWritePos - modDelay;
        if (readPos < 0) readPos += _pitchDelayLine.Length;
        int r0 = (int)readPos;
        int r1 = (r0 + 1) % _pitchDelayLine.Length;
        double frac = readPos - r0;
        double shifted = _pitchDelayLine[r0] * (1 - frac) + _pitchDelayLine[r1] * frac;

        if (++_pitchWritePos >= _pitchDelayLine.Length) _pitchWritePos = 0;
        return shifted - mid;
    }
}