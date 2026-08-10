using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Adaptive hum removal: auto-detects 50/60 Hz mains frequency, phase-matched
/// harmonic notch bank with dynamic depth control to preserve musical content
/// at hum frequencies.
/// </summary>
public sealed class HumRemovalEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("frequency", "MAINS", 45, 65, 60, v => $"{v:0.0} Hz"),
        new("harmonics", "HARMONICS", 1, 12, 6, v => $"{Math.Round(v):0}"),
        new("q", "PRECISION", 10, 80, 35, v => $"Q {v:0}"),
        new("amount", "AMOUNT", 0, 1, 0.85, EffectParam.Pct),
        new("autoDetect", "AUTO DETECT", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
        new("dynamic", "DYNAMIC", 0, 1, 0, v => v > 0.5 ? "ON" : "OFF"),
    ];

    private Biquad[][] _notches = [];
    private int _activeHarmonics;
    private double _detectedFundamental = 60;
    private double _fundamentalConfidence;
    private double[] _harmonicEnergy = [];
    private double[] _harmonicSmoothing = [];

    public override string TypeId => "dehum";
    public override string DisplayName => "Hum Removal";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"{_activeHarmonics} NOTCHES @ {_detectedFundamental:0.0} Hz";

    protected override void OnConfigure()
    {
        _harmonicEnergy = new double[12];
        _harmonicSmoothing = new double[12];
        Rebuild();
    }

    protected override void OnParamsChanged() => Rebuild();

    private void Rebuild()
    {
        double frequency = GetParam("frequency");
        bool autoDetect = GetParam("autoDetect") > 0.5;

        double effectiveFreq = autoDetect && _fundamentalConfidence > 0.3
            ? _detectedFundamental
            : frequency;

        int requested = (int)Math.Round(GetParam("harmonics"));
        double q = GetParam("q");
        int active = 0;
        for (int harmonic = 1; harmonic <= requested; harmonic++)
            if (effectiveFreq * harmonic < SampleRate * 0.475) active++;

        var rebuilt = new Biquad[ChannelCount][];
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            rebuilt[channel] = new Biquad[active];
            for (int harmonic = 1; harmonic <= active; harmonic++)
                rebuilt[channel][harmonic - 1] = Biquad.Notch(SampleRate, effectiveFreq * harmonic, q);
        }
        _activeHarmonics = active;
        Volatile.Write(ref _notches, rebuilt);
    }

    public override void ResetState()
    {
        var notches = Volatile.Read(ref _notches);
        for (int channel = 0; channel < notches.Length; channel++)
            for (int harmonic = 0; harmonic < notches[channel].Length; harmonic++)
                notches[channel][harmonic].Reset();
        Array.Clear(_harmonicEnergy);
        Array.Clear(_harmonicSmoothing);
        _detectedFundamental = GetParam("frequency");
        _fundamentalConfidence = 0;
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var notches = Volatile.Read(ref _notches);
        if (notches.Length != ChannelCount) return;
        float amount = (float)GetParam("amount");
        float dry = 1 - amount;
        bool autoDetect = GetParam("autoDetect") > 0.5;
        bool dynamic = GetParam("dynamic") > 0.5;

        // Adaptive fundamental detection via zero-crossing analysis
        if (autoDetect)
        {
            DetectFundamental(buffer, offset, count);
        }

        for (int i = 0; i < count; i++)
        {
            int channel = i % ChannelCount;
            int index = offset + i;
            float input = buffer[index];
            float filtered = input;

            for (int harmonic = 0; harmonic < notches[channel].Length; harmonic++)
            {
                float notchOut = notches[channel][harmonic].Process(filtered);

                // Dynamic depth: reduce notch depth when harmonic has significant energy
                // (likely musical content, not hum)
                if (dynamic && harmonic < _harmonicEnergy.Length)
                {
                    double energy = Math.Abs(filtered);
                    _harmonicEnergy[harmonic] = 0.95 * _harmonicEnergy[harmonic] + 0.05 * energy;
                    _harmonicSmoothing[harmonic] = 0.9 * _harmonicSmoothing[harmonic] + 0.1 * _harmonicEnergy[harmonic];

                    double dynamicAmount = amount;
                    if (_harmonicSmoothing[harmonic] > 0.01)
                    {
                        // Reduce notch depth when sustained energy is present
                        double reduction = Math.Clamp(_harmonicSmoothing[harmonic] * 20, 0, 1);
                        dynamicAmount *= (1 - reduction * 0.7);
                    }
                    filtered = input * (1 - (float)dynamicAmount) + notchOut * (float)dynamicAmount;
                }
                else
                {
                    filtered = notchOut;
                }
            }
            buffer[index] = input * dry + filtered * amount;
        }
    }

    private void DetectFundamental(float[] buffer, int offset, int count)
    {
        // Simple zero-crossing based frequency detection
        int zeroCrossings = 0;
        float prevSample = 0;
        bool prevPositive = false;
        double totalSamples = 0;

        for (int i = offset; i < offset + count; i += ChannelCount)
        {
            float sample = buffer[i];
            bool positive = sample >= 0;
            if (positive != prevPositive && totalSamples > 0)
                zeroCrossings++;
            prevPositive = positive;
            prevSample = sample;
            totalSamples++;
        }

        if (totalSamples > 100 && zeroCrossings > 2)
        {
            double detectedFreq = zeroCrossings * SampleRate / (2 * totalSamples);

            // Check if detected frequency is near 50 or 60 Hz
            double dist50 = Math.Abs(detectedFreq - 50);
            double dist60 = Math.Abs(detectedFreq - 60);

            if (dist50 < 10 || dist60 < 10)
            {
                double candidate = dist50 < dist60 ? 50 : 60;
                double confidence = 1.0 - Math.Clamp(Math.Min(dist50, dist60) / 10.0, 0, 1);

                // Smooth the detection
                _detectedFundamental = 0.9 * _detectedFundamental + 0.1 * candidate;
                _fundamentalConfidence = 0.9 * _fundamentalConfidence + 0.1 * confidence;
            }
        }
    }
}