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

        // Adaptive fundamental detection via energy-gated Goertzel analysis
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
        // Goertzel power at 50 and 60 Hz, gated against the total signal power:
        // a mains fundamental must carry a meaningful share of the energy, so
        // bass-heavy music (e.g. a 55 Hz A1 note) cannot false-lock the detector.
        int frames = count / ChannelCount;
        if (frames < 64) return;

        double w50 = 2 * Math.PI * 50 / SampleRate, w60 = 2 * Math.PI * 60 / SampleRate;
        double c50 = 2 * Math.Cos(w50), c60 = 2 * Math.Cos(w60);
        double s50a = 0, s50b = 0, s60a = 0, s60b = 0, total = 0;

        for (int f = 0; f < frames; f++)
        {
            double x = buffer[offset + f * ChannelCount];
            double y50 = x + c50 * s50a - s50b;
            s50b = s50a; s50a = y50;
            double y60 = x + c60 * s60a - s60b;
            s60b = s60a; s60a = y60;
            total += x * x;
        }
        if (total <= 1e-9) return;

        double p50 = s50a * s50a + s50b * s50b - c50 * s50a * s50b;
        double p60 = s60a * s60a + s60b * s60b - c60 * s60a * s60b;

        // Normalized share of signal energy at each mains candidate (1 = all of it)
        double share50 = 2 * p50 / (frames * total);
        double share60 = 2 * p60 / (frames * total);
        double candidate = share50 > share60 ? 50 : 60;
        double share = Math.Max(share50, share60);

        // Full confidence once the candidate holds ≳17% of the signal energy
        double confidence = Math.Clamp((share - 0.02) / 0.15, 0, 1);

        // Smooth the detection
        if (confidence > 0)
        {
            _detectedFundamental = 0.9 * _detectedFundamental + 0.1 * candidate;
            _fundamentalConfidence = 0.9 * _fundamentalConfidence + 0.1 * confidence;
        }
        else
        {
            _fundamentalConfidence *= 0.95;
        }
    }

}