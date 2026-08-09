using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>Adjustable, phase-matched harmonic notch bank for 50/60 Hz mains hum.</summary>
public sealed class HumRemovalEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("frequency", "MAINS", 45, 65, 60, v => $"{v:0.0} Hz"),
        new("harmonics", "HARMONICS", 1, 12, 6, v => $"{Math.Round(v):0}"),
        new("q", "PRECISION", 10, 80, 35, v => $"Q {v:0}"),
        new("amount", "AMOUNT", 0, 1, 0.85, EffectParam.Pct),
    ];

    private Biquad[][] _notches = [];
    private int _activeHarmonics;

    public override string TypeId => "dehum";
    public override string DisplayName => "Hum Removal";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string? Readout => $"{_activeHarmonics} NOTCHES";

    protected override void OnConfigure() => Rebuild();
    protected override void OnParamsChanged() => Rebuild();

    private void Rebuild()
    {
        double frequency = GetParam("frequency");
        int requested = (int)Math.Round(GetParam("harmonics"));
        double q = GetParam("q");
        int active = 0;
        for (int harmonic = 1; harmonic <= requested; harmonic++)
            if (frequency * harmonic < SampleRate * 0.475) active++;

        var rebuilt = new Biquad[ChannelCount][];
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            rebuilt[channel] = new Biquad[active];
            for (int harmonic = 1; harmonic <= active; harmonic++)
                rebuilt[channel][harmonic - 1] = Biquad.Notch(SampleRate, frequency * harmonic, q);
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
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var notches = Volatile.Read(ref _notches);
        if (notches.Length != ChannelCount) return;
        float amount = (float)GetParam("amount");
        float dry = 1 - amount;

        for (int i = 0; i < count; i++)
        {
            int channel = i % ChannelCount;
            int index = offset + i;
            float input = buffer[index];
            float filtered = input;
            for (int harmonic = 0; harmonic < notches[channel].Length; harmonic++)
                filtered = notches[channel][harmonic].Process(filtered);
            buffer[index] = input * dry + filtered * amount;
        }
    }
}
