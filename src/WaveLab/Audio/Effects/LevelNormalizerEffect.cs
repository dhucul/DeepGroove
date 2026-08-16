using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced automatic level control with K-weighted (ITU-R BS.1770) loudness
/// measurement, true-peak awareness, dual-stage (short-term + integrated) loudness
/// targets, and gain change limiting to prevent audible pumping.
/// </summary>
public sealed class LevelNormalizerEffect : EffectBase
{
    private const int ControlIntervalFrames = 32;
    private const int LufsIntegrationFrames = 3840; // ~80ms at 48kHz for short-term
    private const double LufsCalibrationOffset = -0.691; // ITU-R BS.1770 absolute constant

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
    private double _intervalPeak; // highest true-peak estimate since the last control update
    private double[] _lufsHistory = [];
    private int _lufsHistoryPos;
    private double _integratedLoudness;
    private float[] _prevSample = []; // per channel: inter-sample estimates must not bleed across L/R
    private Biquad[] _kStage1 = [];   // K-weighting pre-filter (shelf)
    private Biquad[] _kStage2 = [];   // K-weighting RLB high-pass

    public override string TypeId => "normalizer";
    public override string DisplayName => "Level Normalizer";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"GAIN {_gainReadoutDb:+0.0;-0.0;0.0} dB";

    protected override void OnConfigure()
    {
        _lufsHistory = new double[48]; // ~2 seconds of short-term measurements
        // Empty slots must read as silence: 0 would pass the noise-floor gate as
        // 0 LUFS and drag the integrated measurement to full scale at startup.
        Array.Fill(_lufsHistory, -100.0);
        _lufsHistoryPos = 0;
        _integratedLoudness = -18;

        _prevSample = new float[ChannelCount];
        _kStage1 = new Biquad[ChannelCount];
        _kStage2 = new Biquad[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            // Same K-weighting as the EBU R128 loudness meter:
            // high shelf +4 dB @ ~1.68 kHz, then high-pass @ ~38 Hz.
            _kStage1[c] = Biquad.HighShelf(SampleRate, 1681.97, 3.99982, 1.0);
            _kStage2[c] = Biquad.HighPass(SampleRate, 38.13, 0.5);
        }
    }

    public override void ResetState()
    {
        _meanSquare = 0;
        _currentGain = 1;
        _targetGain = 1;
        _gainReadoutDb = 0;
        _controlCountdown = 0;
        _intervalPeak = 0;
        Array.Clear(_prevSample);
        Array.Fill(_lufsHistory, -100.0); // silence, not 0 LUFS (see OnConfigure)
        _lufsHistoryPos = 0;

        _integratedLoudness = -18;
        // Indexed, not foreach: Biquad is a struct, so foreach would reset copies
        // and the K-weighting memory would carry over into the next render.
        for (int c = 0; c < _kStage1.Length; c++) _kStage1[c].Reset();
        for (int c = 0; c < _kStage2.Length; c++) _kStage2[c].Reset();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        int frames = count / ChannelCount;
        if (frames <= 0 || _prevSample.Length != ChannelCount) return;

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

        // Max gain change per frame (the gain is updated once per frame,
        // and there are SampleRate frames per second — independent of channel count).
        double maxGainChangePerFrame = maxGainChangePerSec / SampleRate;
        // Loop-invariant: the per-frame gain-change ceiling, in linear ratio.
        double maxRatio = Math.Pow(10, maxGainChangePerFrame / 20.0);
        double minRatio = 1.0 / maxRatio;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double power = 0;
            double framePeak = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                double sample = buffer[index + channel];

                // K-weighted measurement power
                float w = _kStage2[channel].Process(_kStage1[channel].Process((float)sample));
                power += w * w;

                double a = Math.Abs(sample);
                if (a > framePeak) framePeak = a;

                // True-peak check: inter-sample peak against this channel's previous sample
                double midSample = (sample + _prevSample[channel]) * 0.5;
                double midAbs = Math.Abs(midSample);
                if (midAbs > framePeak) framePeak = midAbs;
                _prevSample[channel] = (float)sample;
            }
            power /= ChannelCount;

            _meanSquare = detectorCoefficient * _meanSquare + (1 - detectorCoefficient) * power;

            // Accumulate across the control interval: reading framePeak only inside
            // the update below would discard 31 of every 32 peaks, including the
            // transients the ceiling exists to catch.
            if (framePeak > _intervalPeak) _intervalPeak = framePeak;

            if (_controlCountdown-- <= 0)
            {
                _controlCountdown = ControlIntervalFrames - 1;

                // K-weighted loudness in LUFS (absolute-gated scale)
                double kWeightedDb = 10 * Math.Log10(Math.Max(1e-12, _meanSquare)) + LufsCalibrationOffset;

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
                if (_intervalPeak > 0)
                {
                    double peakDb = 20 * Math.Log10(_intervalPeak);
                    double peakHeadroom = truePeakLimitDb - peakDb;
                    if (peakHeadroom < desiredGainDb)
                        desiredGainDb = Math.Min(desiredGainDb, peakHeadroom);
                }
                _intervalPeak = 0;

                _targetGain = Math.Pow(10, desiredGainDb / 20.0);
            }

            // Gain change limiting
            double requestedGain = _targetGain;
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
