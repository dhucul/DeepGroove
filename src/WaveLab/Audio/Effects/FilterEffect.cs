using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>Resonant 12 dB/oct filter — registered twice as Low-Pass and High-Pass.</summary>
public sealed class FilterEffect(bool highPass) : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("cutoff", "CUTOFF", 20, 20000, 1000, EffectParam.Hz),
        new("q", "RES", 0.5, 8, 0.707, EffectParam.Plain),
    ];

    private Biquad[] _filters = [];

    public override string TypeId => highPass ? "hpf" : "lpf";
    public override string DisplayName => highPass ? "High-Pass Filter" : "Low-Pass Filter";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure() => Rebuild();
    protected override void OnParamsChanged() => Rebuild();

    private void Rebuild()
    {
        double cutoff = Math.Min(GetParam("cutoff"), SampleRate * 0.45);
        double q = GetParam("q");
        var rebuilt = new Biquad[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
            rebuilt[c] = highPass ? Biquad.HighPass(SampleRate, cutoff, q) : Biquad.LowPass(SampleRate, cutoff, q);
        Volatile.Write(ref _filters, rebuilt);
    }

    public override void ResetState() => Rebuild();

    public override void Process(float[] buffer, int offset, int count)
    {
        var filters = Volatile.Read(ref _filters);
        if (filters.Length != ChannelCount) return;
        for (int i = offset; i < offset + count; i++)
            buffer[i] = filters[(i - offset) % ChannelCount].Process(buffer[i]);
    }
}
