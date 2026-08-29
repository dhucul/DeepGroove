using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced automatic level control with K-weighted (ITU-R BS.1770) loudness
/// measurement, true-peak awareness, dual-stage (short-term + integrated) loudness
/// targets, and gain change limiting to prevent audible pumping.
/// </summary>
public sealed class LevelNormalizerEffect : EffectBase
{
    private const double ControlIntervalSeconds = 0.08;
    private const double LoudnessHistorySeconds = 3.2;
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
    private int _controlIntervalFrames;
    private double _intervalPeak; // highest true-peak estimate since the last control update
    private double[] _lufsHistory = [];
    private int _lufsHistoryPos;
    private double _integratedLoudness;
    private bool _hasIntegratedMeasurement;
    private float[][] _truePeakDelay = [];
    private int[] _truePeakHistory = [];
    private Biquad[] _kStage1 = [];   // K-weighting pre-filter (shelf)
    private Biquad[] _kStage2 = [];   // K-weighting RLB high-pass
    private readonly Limiter _ceilingLimiter = new();

    public override string TypeId => "normalizer";
    public override string DisplayName => "Level Normalizer";
    public override IReadOnlyList<EffectParam> Params => P;
    public override int LatencySamples => Math.Max(1, SampleRate * 5 / 1000);
    public override string? Readout => $"GAIN {_gainReadoutDb:+0.0;-0.0;0.0} dB";

    protected override void OnConfigure()
    {
        _controlIntervalFrames = Math.Max(1, (int)Math.Round(SampleRate * ControlIntervalSeconds));
        _lufsHistory = new double[Math.Max(4,
            (int)Math.Ceiling(LoudnessHistorySeconds / ControlIntervalSeconds))];
        // Empty slots must read as silence: 0 would pass the noise-floor gate as
        // 0 LUFS and drag the integrated measurement to full scale at startup.
        Array.Fill(_lufsHistory, -100.0);
        _lufsHistoryPos = 0;
        _integratedLoudness = -18;

        _truePeakDelay = new float[ChannelCount][];
        _truePeakHistory = new int[ChannelCount];
        _kStage1 = new Biquad[ChannelCount];
        _kStage2 = new Biquad[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            _truePeakDelay[c] = new float[LoudnessMeter.TruePeakTapsPerPhase];
            // Same K-weighting as the EBU R128 loudness meter:
            // high shelf +4 dB @ ~1.68 kHz, then high-pass @ ~38 Hz.
            _kStage1[c] = Biquad.HighShelf(SampleRate, 1681.97, 3.99982, 1.0);
            _kStage2[c] = Biquad.HighPass(SampleRate, 38.13, 0.5);
        }
        _ceilingLimiter.Configure(SampleRate, ChannelCount);
    }

    protected override void OnParamsChanged()
    {
        _ceilingLimiter.ThresholdDb = 0;
        _ceilingLimiter.CeilingDb = GetParam("truePeakLimit");
        _ceilingLimiter.Oversample = true;
        _ceilingLimiter.Enabled = true;
    }

    public override void ResetState()
    {
        _meanSquare = 0;
        _currentGain = 1;
        _targetGain = 1;
        _gainReadoutDb = 0;
        _controlCountdown = Math.Max(0, _controlIntervalFrames - 1);
        _intervalPeak = 0;
        _hasIntegratedMeasurement = false;
        foreach (float[] history in _truePeakDelay) Array.Clear(history);
        Array.Clear(_truePeakHistory);
        Array.Fill(_lufsHistory, -100.0); // silence, not 0 LUFS (see OnConfigure)
        _lufsHistoryPos = 0;

        _integratedLoudness = -18;
        // Indexed, not foreach: Biquad is a struct, so foreach would reset copies
        // and the K-weighting memory would carry over into the next render.
        for (int c = 0; c < _kStage1.Length; c++) _kStage1[c].Reset();
        for (int c = 0; c < _kStage2.Length; c++) _kStage2[c].Reset();
        _ceilingLimiter.Reset();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        int frames = count / ChannelCount;
        if (frames <= 0 || _truePeakDelay.Length != ChannelCount) return;

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

                // A midpoint is a convex combination and can never exceed either endpoint, so it
                // cannot detect an inter-sample peak. Use the same four-phase BS.1770 FIR as the
                // meters and limiter.
                float[] history = _truePeakDelay[channel];
                for (int tap = history.Length - 1; tap > 0; tap--) history[tap] = history[tap - 1];
                history[0] = (float)sample;
                if (_truePeakHistory[channel] < history.Length) _truePeakHistory[channel]++;
                if (_truePeakHistory[channel] == history.Length)
                    framePeak = Math.Max(framePeak, LoudnessMeter.InterpolatedTruePeak(history));
            }
            power /= ChannelCount;

            _meanSquare = detectorCoefficient * _meanSquare + (1 - detectorCoefficient) * power;

            // Accumulate across the control interval: reading framePeak only inside
            // the update below would discard almost every peak, including the
            // transients the ceiling exists to catch.
            if (framePeak > _intervalPeak) _intervalPeak = framePeak;

            if (_controlCountdown-- <= 0)
            {
                _controlCountdown = _controlIntervalFrames - 1;

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
                if (!_hasIntegratedMeasurement)
                {
                    _integratedLoudness = shortTermLoudness;
                    _hasIntegratedMeasurement = true;
                }
                else
                {
                    // One update every 80 ms: this gives the running estimate a several-second
                    // memory without making startup depend on the arbitrary -18 LUFS seed.
                    _integratedLoudness = 0.98 * _integratedLoudness + 0.02 * shortTermLoudness;
                }

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
            double smoothing = _targetGain < _currentGain ? reduceCoefficient : increaseCoefficient;
            double smoothedGain = smoothing * _currentGain + (1 - smoothing) * _targetGain;
            // Smooth toward the target first, then cap the movement. Capping the target to one
            // frame and smoothing that tiny step again made the gain effectively immobile.
            double stepRatio = Math.Clamp(
                smoothedGain / Math.Max(1e-9, _currentGain), minRatio, maxRatio);
            _currentGain *= stepRatio;
            float gain = (float)_currentGain;
            for (int channel = 0; channel < ChannelCount; channel++)
                buffer[index + channel] *= gain;
        }

        _gainReadoutDb = 20 * Math.Log10(Math.Max(1e-12, _currentGain));
        // The control loop above anticipates peak pressure, but only look-ahead can
        // enforce a ceiling on audio that has not left the effect yet.
        _ceilingLimiter.Process(buffer, offset, count);
    }
}
