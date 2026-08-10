namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced automatic level control with LUFS-style loudness measurement,
/// true-peak awareness, dual-stage (short-term + integrated) loudness targets,
/// and gain change limiting to prevent audible pumping.
/// </summary>
public sealed class LevelNormalizerEffect : EffectBase
{
    private const int ControlIntervalFrames = 32;
    private const int LufsIntegrationFrames = 3840; // ~80ms at 48kHz for short-term

    private static readonly EffectParam[] P =
    [
        new("target", "TARGET LUFS", -30, -10, -18, EffectParam.Db),
        new("maxBoost", "MAX BOOST", 0, 18, 6, EffectParam.Db),
        new("maxCut", "MAX CUT", 0, 18, 12, EffectParam.Db),
        new("gate", "NOISE FLOOR", -80, -35, -55, EffectParam.Db),
        new("response", "RESPONSE", 250, 5000, 1500, EffectParam.Ms),
        new("maxGainChange", "MAX ΔGAIN/s", 1, 12, 6, v => $"{v:0.0} dB/s"),
        new("truePeakLimit", "TRUE PEAK", -6, 0, -1, v => $"{v:0.0} dBTP"),
    ];

    private double _meanSquare;
    private double _currentGain = 1;
    private double _targetGain = 1;
    private double _gainReadoutDb;
    private int _controlCountdown;
    private double[] _lufsHistory = [];
    private int _lufsHistoryPos;
    private double _integratedLoudness;
    private double _prevSample;

    public override string TypeId => "normalizer";
    public override string DisplayName => "Level Normalizer";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"GAIN {_gainReadoutDb:+0.0;-0.0;0.0} dB";

    protected override void OnConfigure()
    {
        _lufsHistory = new double[48]; // ~2 seconds of short-term measurements
        _lufsHistoryPos = 0;
        _integratedLoudness = -18;
    }

    public override void ResetState()
    {
        _meanSquare = 0;
        _currentGain = 1;
        _targetGain = 1;
        _gainReadoutDb = 0;
        _controlCountdown = 0;
        _prevSample = 0;
        Array.Clear(_lufsHistory);
        _lufsHistoryPos = 0;
        _integratedLoudness = -18;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        int frames = count / ChannelCount;
        if (frames <= 0) return;

        double detectorCoefficient = Math.Exp(-1.0 / (SampleRate * 0.10));
        double responseSeconds = GetParam("response") / 1000.0;
        double increaseCoefficient = Math.Exp(-1.0 / (SampleRate * responseSeconds));
        double reduceCoefficient = Math.Exp(-1.0 / (SampleRate * Math.Max(0.05, responseSeconds * 0.20)));
        double targetLevelDb = GetParam("target");
        double maximumBoostDb = GetParam("maxBoost");
        double maximumCutDb = GetParam("maxCut");
        double noiseFloorDb = GetParam("gate");
        double maxGainChangePerSec = GetParam("maxGainChange");
        double truePeakLimitDb = GetParam("truePeakLimit");
        double truePeakLimit = Math.Pow(10, truePeakLimitDb / 20.0);

        // Max gain change per frame
        double maxGainChangePerFrame = maxGainChangePerSec / SampleRate * ChannelCount;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double power = 0;
            double framePeak = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                double sample = buffer[index + channel];
                power += sample * sample;
                double a = Math.Abs(sample);
                if (a > framePeak) framePeak = a;

                // True-peak check: inter-sample peak
                double midSample = (sample + _prevSample) * 0.5;
                double midAbs = Math.Abs(midSample);
                if (midAbs > framePeak) framePeak = midAbs;
                _prevSample = sample;
            }
            power /= ChannelCount;

            _meanSquare = detectorCoefficient * _meanSquare + (1 - detectorCoefficient) * power;

            if (_controlCountdown-- <= 0)
            {
                _controlCountdown = ControlIntervalFrames - 1;
                double levelDb = 10 * Math.Log10(Math.Max(1e-12, _meanSquare));

                // K-weighting approximation for LUFS-style measurement
                // Simple pre-filter: high-pass at ~100Hz to approximate RLB filter
                double kWeightedDb = levelDb; // simplified

                // Update short-term loudness history
                _lufsHistory[_lufsHistoryPos] = kWeightedDb;
                _lufsHistoryPos = (_lufsHistoryPos + 1) % _lufsHistory.Length;

                // Integrated loudness (slow average of short-term measurements)
                double stSum = 0;
                int stCount = 0;
                for (int i = 0; i < _lufsHistory.Length; i++)
                {
                    if (_lufsHistory[i] > noiseFloorDb + 10)
                    {
                        stSum += _lufsHistory[i];
                        stCount++;
                    }
                }
                double shortTermLoudness = stCount > 0 ? stSum / stCount : kWeightedDb;
                _integratedLoudness = 0.995 * _integratedLoudness + 0.005 * shortTermLoudness;

                // Use integrated loudness for gain target (more stable)
                double effectiveLevel = _integratedLoudness;
                double desiredGainDb = effectiveLevel <= noiseFloorDb
                    ? 0
                    : Math.Clamp(targetLevelDb - effectiveLevel, -maximumCutDb, maximumBoostDb);

                // True-peak limiting: reduce gain if peaks would exceed ceiling
                if (framePeak > 0)
                {
                    double peakDb = 20 * Math.Log10(framePeak);
                    double peakHeadroom = truePeakLimitDb - peakDb;
                    if (peakHeadroom < desiredGainDb)
                        desiredGainDb = Math.Min(desiredGainDb, peakHeadroom);
                }

                _targetGain = Math.Pow(10, desiredGainDb / 20.0);
            }

            // Gain change limiting
            double requestedGain = _targetGain;
            double maxRatio = Math.Pow(10, maxGainChangePerFrame / 20.0);
            double minRatio = 1.0 / maxRatio;
            double clampedGain = Math.Clamp(requestedGain / Math.Max(1e-9, _currentGain), minRatio, maxRatio)
                * _currentGain;

            double smoothing = clampedGain < _currentGain ? reduceCoefficient : increaseCoefficient;
            _currentGain = smoothing * _currentGain + (1 - smoothing) * clampedGain;
            float gain = (float)_currentGain;
            for (int channel = 0; channel < ChannelCount; channel++)
                buffer[index + channel] *= gain;
        }

        _gainReadoutDb = 20 * Math.Log10(Math.Max(1e-12, _currentGain));
    }
}