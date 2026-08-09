using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>Tape-style saturation: drive into tanh with tone tilt and automatic output compensation.</summary>
public sealed class SaturationEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("drive", "DRIVE", 0, 36, 12, EffectParam.Db),
        new("tone", "TONE", 0, 1, 0.5, EffectParam.Plain),
        new("mix", "MIX", 0, 1, 1, EffectParam.Pct),
    ];

    private Biquad[] _tone = [];
    private double _toneCutoff = double.NaN;
    private SaturationParameters _parameters = new(1f, 1f, 1f);

    private sealed record SaturationParameters(float Drive, float Compensation, float Mix);

    public override string TypeId => "saturation";
    public override string DisplayName => "Saturation";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure() => RebuildTone(EffectiveToneCutoff());

    protected override void OnParamsChanged()
    {
        double driveDb = GetParam("drive");
        Volatile.Write(ref _parameters, new SaturationParameters(
            (float)Math.Pow(10, driveDb / 20.0),
            (float)(1.0 / Math.Pow(10, driveDb * 0.6 / 20.0)),
            (float)GetParam("mix")));

        double cutoff = EffectiveToneCutoff();
        if (cutoff != Volatile.Read(ref _toneCutoff)) RebuildTone(cutoff);
    }

    private double EffectiveToneCutoff()
    {
        // tone 0 = dark (LP at 2 kHz) … 1 = bright (LP at 20 kHz, effectively open)
        double cutoff = 2000 * Math.Pow(10, GetParam("tone")); // 2k .. 20k
        return Math.Min(cutoff, SampleRate * 0.45);
    }

    private void RebuildTone(double cutoff)
    {
        var rebuilt = new Biquad[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
            rebuilt[c] = Biquad.LowPass(SampleRate, cutoff, 0.707);
        Volatile.Write(ref _tone, rebuilt);
        Volatile.Write(ref _toneCutoff, cutoff);
    }

    public override void ResetState()
    {
        var tone = Volatile.Read(ref _tone);
        for (int c = 0; c < tone.Length; c++) tone[c].Reset();
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var tone = Volatile.Read(ref _tone);
        if (tone.Length != ChannelCount) return;
        var parameters = Volatile.Read(ref _parameters);
        float drive = parameters.Drive;
        float comp = parameters.Compensation;
        float mix = parameters.Mix;
        float dry = 1 - mix;

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float x = buffer[i];
            float shaped = MathF.Tanh(x * drive) * comp;
            shaped = tone[c].Process(shaped);
            buffer[i] = x * dry + shaped * mix;
        }
    }
}
