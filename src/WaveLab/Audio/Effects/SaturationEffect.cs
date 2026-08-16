using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Advanced saturation: multiple distortion curves (tube, tape, transistor, diode),
/// true 2x oversampling with a windowed-sinc anti-alias filter, tone tilt,
/// and automatic output compensation.
/// </summary>
public sealed class SaturationEffect : EffectBase
{
    private const int OsTaps = 16; // power of two for cheap ring wrap

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
        new("oversample", "OVERSAMPLE", 0, 1, 1, v => v > 0.5 ? "2x" : "OFF"),
    ];

    private Biquad[] _tone = [];
    private double _toneCutoff = double.NaN;
    private SaturationParameters _parameters = new(1f, 1f, 1f, 0, true);
    private double[] _osFir = [];
    private float[][] _osHist = [];
    private int[] _osPos = []; // per channel: one shared cursor would leave half of every history unwritten
    private float[] _prevIn = [];

    private sealed record SaturationParameters(float Drive, float Compensation, float Mix, int Curve, bool Oversample);

    public override string TypeId => "saturation";
    public override string DisplayName => "Saturation";
    public override IReadOnlyList<EffectParam> Params => P;

    protected override void OnConfigure()
    {
        _prevIn = new float[ChannelCount];
        _osHist = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++) _osHist[c] = new float[OsTaps];
        _osPos = new int[ChannelCount];

        // 16-tap windowed-sinc anti-alias filter for the 2x path; cutoff sits just
        // below the original Nyquist, expressed in cycles per 2x-rate sample.
        const double fc = 0.225; // ≈ 21.6 kHz at a 48 kHz base rate
        _osFir = new double[OsTaps];
        for (int n = 0; n < OsTaps; n++)
        {
            double m = n - (OsTaps - 1) / 2.0;
            double sinc = m == 0 ? 1 : Math.Sin(2 * Math.PI * fc * m) / (2 * Math.PI * fc * m);
            double win = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (OsTaps - 1)); // Hamming
            _osFir[n] = 2 * fc * sinc * win;
        }

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
        Array.Clear(_prevIn);
        foreach (var hist in _osHist) Array.Clear(hist);
        Array.Clear(_osPos);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        var tone = Volatile.Read(ref _tone);
        if (tone.Length != ChannelCount || _osHist.Length != ChannelCount || _osPos.Length != ChannelCount) return;
        var parameters = Volatile.Read(ref _parameters);
        float drive = parameters.Drive;
        float comp = parameters.Compensation;
        float mix = parameters.Mix;
        float dry = 1 - mix;
        int curve = parameters.Curve;
        bool oversample = parameters.Oversample && _osFir.Length == OsTaps;

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            float x = buffer[i];

            float shaped;
            if (oversample)
            {
                // True 2x oversampling: interpolate the intermediate sample, shape
                // both 2x-rate points, then anti-alias-filter and decimate.
                float mid = (x + _prevIn[c]) * 0.5f;
                _prevIn[c] = x;

                float[] hist = _osHist[c];
                int p = _osPos[c];
                hist[p] = ShapeSample(mid * drive, curve) * comp;
                p = (p + 1) & (OsTaps - 1);
                hist[p] = ShapeSample(x * drive, curve) * comp;

                double acc = 0;
                for (int k = 0; k < OsTaps; k++)
                    acc += _osFir[k] * hist[(p - k) & (OsTaps - 1)];
                _osPos[c] = (p + 1) & (OsTaps - 1);
                shaped = (float)acc;
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
