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

    public override string TypeId => "saturation";
    public override string DisplayName => "Saturation";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure() => RebuildTone();

    protected override void OnParamsChanged() => RebuildTone();

    private void RebuildTone()
    {
        // tone 0 = dark (LP at 2 kHz) … 1 = bright (LP at 20 kHz, effectively open)
        double cutoff = 2000 * Math.Pow(10, GetParam("tone")); // 2k .. 20k
        cutoff = Math.Min(cutoff, SampleRate * 0.45);
        _tone = new Biquad[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
            _tone[c] = Biquad.LowPass(SampleRate, cutoff, 0.707);
    }

    public override void ResetState() => RebuildTone();

    public override void Process(float[] buffer, int offset, int count)
    {
        if (_tone.Length == 0) return;
        double driveDb = GetParam("drive");
        float drive = (float)Math.Pow(10, driveDb / 20.0);
        // keep loudness roughly constant as drive goes up
        float comp = (float)(1.0 / Math.Pow(10, driveDb * 0.6 / 20.0));
        float mix = (float)GetParam("mix");
        float dry = 1 - mix;

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float x = buffer[i];
            float shaped = MathF.Tanh(x * drive) * comp;
            shaped = _tone[c].Process(shaped);
            buffer[i] = x * dry + shaped * mix;
        }
    }
}
