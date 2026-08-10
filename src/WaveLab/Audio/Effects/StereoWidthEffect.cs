using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced stereo width: mid/side width scaling, Haas-effect delay option,
/// per-band width control (low/high split), mono bass, and correlation-aware safety.
/// </summary>
public sealed class StereoWidthEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("width", "WIDTH", 0, 2, 1, EffectParam.Pct),
        new("monoBass", "MONO BASS", 0, 400, 0, v => v < 1 ? "Off" : EffectParam.Hz(v)),
        new("safety", "PHASE SAFE", 0, 1, 1, EffectParam.Pct),
        new("mode", "MODE", 0, 1, 0, v => v > 0.5 ? "HAAS" : "M/S"),
        new("haasDelay", "HAAS DELAY", 0.5, 15, 5, EffectParam.Ms),
        new("lowWidth", "LOW WIDTH", 0, 2, 1, EffectParam.Pct),
        new("splitFreq", "SPLIT FREQ", 100, 2000, 300, EffectParam.Hz),
    ];

    private double _lowSide;
    private double _midEnergy;
    private double _sideEnergy;
    private double _safetyGain = 1;
    private double _correlation = 1;
    private Biquad[] _splitLowPass = [];
    private Biquad[] _splitHighPass = [];
    private float[] _haasDelayLine = [];
    private int _haasWritePos;

    public override string TypeId => "stereo-width";
    public override string DisplayName => "Stereo Width";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => ChannelCount < 2 ? "STEREO ONLY" : $"CORR {_correlation:+0.00;-0.00;0.00}";

    protected override void OnConfigure()
    {
        _splitLowPass = new Biquad[ChannelCount];
        _splitHighPass = new Biquad[ChannelCount];
        _haasDelayLine = new float[Math.Max(32, (int)(SampleRate * 0.02) + 2)];
        RebuildSplitFilters();
    }

    private void RebuildSplitFilters()
    {
        if (_splitLowPass.Length != ChannelCount || _splitHighPass.Length != ChannelCount) return;

        double splitFreq = GetParam("splitFreq");
        for (int c = 0; c < ChannelCount; c++)
        {
            _splitLowPass[c] = Biquad.LowPass12Db(SampleRate, splitFreq);
            _splitHighPass[c] = Biquad.HighPass12Db(SampleRate, splitFreq);
        }
    }

    protected override void OnParamsChanged() => RebuildSplitFilters();

    public override void ResetState()
    {
        _lowSide = 0;
        _midEnergy = 0;
        _sideEnergy = 0;
        _safetyGain = 1;
        _correlation = 1;
        _haasWritePos = 0;
        Array.Clear(_haasDelayLine);
        foreach (var f in _splitLowPass) f.Reset();
        foreach (var f in _splitHighPass) f.Reset();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        if (ChannelCount < 2) return;

        double width = GetParam("width");
        double lowWidth = GetParam("lowWidth");
        double monoBass = GetParam("monoBass");
        double bassAlpha = monoBass > 0 ? 1 - Math.Exp(-2 * Math.PI * monoBass / SampleRate) : 1;
        double safety = GetParam("safety");
        double energyCoefficient = Math.Exp(-1.0 / (SampleRate * 0.05));
        double reduceCoefficient = Math.Exp(-1.0 / (SampleRate * 0.004));
        double recoverCoefficient = Math.Exp(-1.0 / (SampleRate * 0.12));
        bool haasMode = GetParam("mode") > 0.5;
        double haasDelaySamples = GetParam("haasDelay") / 1000.0 * SampleRate;

        int frames = count / ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = offset + frame * ChannelCount;
            double left = buffer[index];
            double right = buffer[index + 1];
            double mid = (left + right) * 0.5;
            double side = (left - right) * 0.5;

            if (haasMode)
            {
                // Haas effect: delay one channel slightly
                _haasDelayLine[_haasWritePos] = (float)mid;
                double readPos = _haasWritePos - haasDelaySamples;
                if (readPos < 0) readPos += _haasDelayLine.Length;
                int r0 = (int)readPos;
                int r1 = (r0 + 1) % _haasDelayLine.Length;
                double frac = readPos - r0;
                double delayed = _haasDelayLine[r0] * (1 - frac) + _haasDelayLine[r1] * frac;

                double haasSide = (mid - delayed) * width;
                buffer[index] = (float)(mid + haasSide);
                buffer[index + 1] = (float)(mid - haasSide);
                if (++_haasWritePos >= _haasDelayLine.Length) _haasWritePos = 0;
                continue;
            }

            // Per-band M/S processing
            double lowSidePart, highSidePart;
            if (monoBass > 0)
            {
                _lowSide += bassAlpha * (side - _lowSide);
                lowSidePart = _lowSide * lowWidth;
                highSidePart = (side - _lowSide) * width;
            }
            else
            {
                _lowSide = side;
                lowSidePart = side * lowWidth;
                highSidePart = side * width;
            }

            double candidateSide = lowSidePart + highSidePart;

            _midEnergy = energyCoefficient * _midEnergy + (1 - energyCoefficient) * mid * mid;
            _sideEnergy = energyCoefficient * _sideEnergy + (1 - energyCoefficient) * candidateSide * candidateSide;

            double targetSafetyGain = 1;
            bool processingCanIncreaseSide = width > 1 || lowWidth > 1 || monoBass > 0;
            if (processingCanIncreaseSide && safety > 0 && _sideEnergy > 1e-12)
            {
                double maxEnergyRatio = 0.95 + (1 - safety) * 7.05;
                double actualRatio = _sideEnergy / Math.Max(1e-12, _midEnergy);
                if (actualRatio > maxEnergyRatio)
                    targetSafetyGain = Math.Sqrt(maxEnergyRatio / actualRatio);
            }

            double smoothing = targetSafetyGain < _safetyGain ? reduceCoefficient : recoverCoefficient;
            _safetyGain = smoothing * _safetyGain + (1 - smoothing) * targetSafetyGain;
            double safeSide = candidateSide * _safetyGain;

            if (Math.Abs(width - 1) > 1e-12 || Math.Abs(lowWidth - 1) > 1e-12 || monoBass > 0)
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
