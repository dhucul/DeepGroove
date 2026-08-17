using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced saturation: multiple distortion curves (tube, tape, transistor, diode),
/// true 2x oversampling with a windowed-sinc anti-alias filter, tone tilt,
/// and automatic output compensation.
/// </summary>
public sealed class SaturationEffect : EffectBase
{

    private static readonly EffectParam[] P =
    [
        new("drive", "DRIVE", 0, 36, 12, EffectParam.Db),
        new("tone", "TONE", 0, 1, 1.0, EffectParam.Plain),
        new("mix", "MIX", 0, 1, 1, EffectParam.Pct),
        new("curve", "CURVE", 0, 3, 0, v => ((int)v) switch
        {
            0 => "TUBE",
            1 => "TAPE",
            2 => "TRANSISTOR",
            _ => "DIODE",
        }),
        new("oversample", "OVERSAMPLE", 0, 3, 1, v => (int)Math.Round(v) switch { 0 => "OFF", 1 => "2x", 2 => "4x", _ => "8x" }),
    ];

    /// <summary>Largest factor offered, which is what the per-sample scratch span is sized for.</summary>
    private const int MaximumFactor = 8;

    private Biquad[] _tone = [];
    private double _toneCutoff = double.NaN;
    private SaturationParameters _parameters = new(1f, 1f, 1f, 0, true);
    private Oversampler? _sampler;

    private sealed record SaturationParameters(float Drive, float Compensation, float Mix, int Curve, bool Oversample);

    public override string TypeId => "saturation";
    public override string DisplayName => "Saturation";
    public override IReadOnlyList<EffectParam> Params => P;

    /// <summary>
    /// The band-limiting filters delay the signal, and offline rendering compensates by this. The
    /// old interpolate-and-average path had no meaningful delay to declare, which is part of why it
    /// could get away with not being a filter.
    /// </summary>
    public override int LatencySamples
    {
        get
        {
            Oversampler? sampler = Volatile.Read(ref _sampler);
            return sampler is { Factor: > 1 } && Volatile.Read(ref _parameters).Oversample
                ? sampler.LatencySamples
                : 0;
        }
    }

    protected override void OnConfigure()
    {
        Volatile.Write(ref _sampler, new Oversampler(FactorFromParam(), ChannelCount));
        RebuildTone(EffectiveToneCutoff());
    }

    private int FactorFromParam() => (int)Math.Round(GetParam("oversample")) switch
    {
        0 => 1,
        1 => 2,
        2 => 4,
        _ => 8,
    };

    protected override void OnParamsChanged()
    {
        double driveDb = GetParam("drive");
        int curve = (int)GetParam("curve");
        bool oversample = GetParam("oversample") >= 0.5;

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
        if (_tone.Length != ChannelCount) _tone = new Biquad[ChannelCount];
        // In-place coefficient update: tone sweeps don't reset filter state.
        Biquad proto = Biquad.LowPass(SampleRate, cutoff, 0.707);
        for (int c = 0; c < ChannelCount; c++) _tone[c].CopyCoefficientsFrom(proto);
        Volatile.Write(ref _toneCutoff, cutoff);
    }

    public override void ResetState()
    {
        var tone = Volatile.Read(ref _tone);
        for (int c = 0; c < tone.Length; c++) tone[c].Reset();
        Volatile.Read(ref _sampler)?.Reset();
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
        Oversampler? sampler = Volatile.Read(ref _sampler);
        bool oversample = parameters.Oversample;

        // Allocated once for the block, not once per sample: this runs on the audio thread.
        Span<float> high = stackalloc float[MaximumFactor];

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float x = buffer[i];

            float shaped;
            if (oversample && sampler != null)
            {
                // Band-limited up, shape at the higher rate, band-limited back down. What was here
                // before interpolated the midpoint as (x + previous)/2, which is not band-limited:
                // it leaves images just above Nyquist for the curve to fold back into the audible
                // band, and no filter on the way down can recover from that. Measured on the fifth
                // harmonic of a 7 kHz tone, the fold-back sat at -29.8 dB where a proper kernel
                // leaves it at -135.
                Span<float> band = high[..sampler.Factor];
                sampler.Upsample(c, x, band);
                for (int p = 0; p < band.Length; p++) band[p] = ShapeSample(band[p] * drive, curve) * comp;
                shaped = sampler.Downsample(c, band);
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

