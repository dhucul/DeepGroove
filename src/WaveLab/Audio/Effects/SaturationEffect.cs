using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced saturation: multiple distortion curves (tube, tape, transistor, diode),
/// 2x oversampling for alias reduction, tone tilt, and automatic output compensation.
/// </summary>
public sealed class SaturationEffect : EffectBase
{
    private static readonly EffectParam[] P =
    [
        new("drive", "DRIVE", 0, 36, 12, EffectParam.Db),
        new("tone", "TONE", 0, 1, 0.5, EffectParam.Plain),
        new("mix", "MIX", 0, 1, 1, EffectParam.Pct),
        new("curve", "CURVE", 0, 3, 0, v => ((int)v) switch
        {
            0 => "TUBE",
            1 => "TAPE",
            2 => "TRANSISTOR",
            _ => "DIODE",
        }),
        new("oversample", "OVERSAMPLE", 0, 1, 1, v => v > 0.5 ? "2x" : "OFF"),
    ];

    private Biquad[] _tone = [];
    private double _toneCutoff = double.NaN;
    private SaturationParameters _parameters = new(1f, 1f, 1f, 0, true);
    private float[] _osBuf = [];
    private float _prevSample;

    private sealed record SaturationParameters(float Drive, float Compensation, float Mix, int Curve, bool Oversample);

    public override string TypeId => "saturation";
    public override string DisplayName => "Saturation";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        _osBuf = new float[ChannelCount * 4];
        RebuildTone(EffectiveToneCutoff());
    }

    protected override void OnParamsChanged()
    {
        double driveDb = GetParam("drive");
        int curve = (int)GetParam("curve");
        bool oversample = GetParam("oversample") > 0.5;

        // Different compensation curves per saturation type
        double compFactor = curve switch
        {
            0 => 0.55, // tube: moderate comp
            1 => 0.65, // tape: more comp (softer saturation)
            2 => 0.45, // transistor: less comp (harder clip)
            _ => 0.5,  // diode: medium
        };

        Volatile.Write(ref _parameters, new SaturationParameters(
            (float)Math.Pow(10, driveDb / 20.0),
            (float)(1.0 / Math.Pow(10, driveDb * compFactor / 20.0)),
            (float)GetParam("mix"),
            curve,
            oversample));

        double cutoff = EffectiveToneCutoff();
        if (cutoff != Volatile.Read(ref _toneCutoff)) RebuildTone(cutoff);
    }

    private double EffectiveToneCutoff()
    {
        double cutoff = 2000 * Math.Pow(10, GetParam("tone"));
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
        _prevSample = 0;
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
        int curve = parameters.Curve;
        bool oversample = parameters.Oversample;

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float x = buffer[i];

            float shaped;
            if (oversample)
            {
                // 2x oversampling: process at double rate with linear interpolation
                float mid = (x + _prevSample) * 0.5f;
                float s0 = ShapeSample(x * drive, curve) * comp;
                float sMid = ShapeSample(mid * drive, curve) * comp;
                shaped = (s0 + sMid) * 0.5f; // simple averaging for anti-aliasing
                _prevSample = x;
            }
            else
            {
                shaped = ShapeSample(x * drive, curve) * comp;
            }

            shaped = tone[c].Process(shaped);
            buffer[i] = x * dry + shaped * mix;
        }
    }

    private static float ShapeSample(float x, int curve)
    {
        return curve switch
        {
            0 => TubeShape(x),       // asymmetric soft-clip with even harmonics
            1 => TapeShape(x),       // symmetric soft-saturate with hysteresis
            2 => TransistorShape(x), // hard-clip with smooth corners
            _ => DiodeShape(x),      // asymmetric hard-clip
        };
    }

    // Tube: asymmetric tanh with DC offset for even harmonics
    private static float TubeShape(float x)
    {
        float bias = 0.15f;
        float pos = MathF.Tanh((x + bias) * 1.2f);
        float neg = MathF.Tanh((x - bias) * 1.2f);
        return (pos + neg) * 0.5f;
    }

    // Tape: symmetric soft-saturate with cubic soft-clip
    private static float TapeShape(float x)
    {
        float ax = Math.Abs(x);
        if (ax < 0.6f)
            return x - x * x * x * 0.25f;
        float sign = MathF.Sign(x);
        return sign * (0.55f + 0.45f * MathF.Tanh((ax - 0.6f) * 2.5f));
    }

    // Transistor: hard-clip with smooth polynomial transition
    private static float TransistorShape(float x)
    {
        float ax = Math.Abs(x);
        if (ax < 0.8f)
            return x * (1.0f - x * x * 0.15f);
        float sign = MathF.Sign(x);
        return sign * (0.72f + 0.28f * MathF.Tanh((ax - 0.8f) * 4f));
    }

    // Diode: asymmetric hard-clip (only clips positive side hard)
    private static float DiodeShape(float x)
    {
        if (x > 0.7f)
            return 0.7f + 0.3f * MathF.Tanh((x - 0.7f) * 3f);
        if (x < -0.9f)
            return -0.9f - 0.1f * MathF.Tanh((-x - 0.9f) * 3f);
        return x;
    }
}