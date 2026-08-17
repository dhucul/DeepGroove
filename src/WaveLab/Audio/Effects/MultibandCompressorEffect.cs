using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// A four-band compressor on a Linkwitz-Riley crossover, with program-dependent release.
/// </summary>
/// <remarks>
/// <para>
/// The point of splitting before compressing is that a full-band compressor is steered by whatever
/// is loudest, which on most music is the bass: a kick drum then ducks the vocal, the cymbals and
/// everything else, once per beat. Compressing each range against its own level stops one part of
/// the spectrum controlling the rest.
/// </para>
/// <para>
/// The bands must recombine to the signal they came from, which is the whole reason
/// <see cref="Crossover"/> exists and is not a plain pair of filters: with three or more bands a
/// naive tree leaves a hole at the upper crossings. Recombination is by addition, and the sum is
/// all-pass rather than the identity — flat in level, with phase rotating through each crossing.
/// </para>
/// <para>
/// Release is <b>program-dependent</b>, set by the crest factor of the band rather than by the
/// knob alone. A steady passage has a low crest factor and gets the slow release that keeps the
/// compression inaudible; a percussive one has a high crest factor and gets a fast release, so the
/// gain recovers between hits instead of pumping. A single release time cannot serve both, and
/// choosing one is what makes fixed-release compressors sound either sluggish or busy.
/// </para>
/// </remarks>
public sealed class MultibandCompressorEffect : EffectBase
{
    /// <summary>Bands, fixed: the parameter set is per-band and a variable count would not fit the rack UI.</summary>
    public const int BandCount = 4;

    private static readonly EffectParam[] P =
    [
        new("x1", "SPLIT 1", 40, 500, 120, EffectParam.Hz),
        new("x2", "SPLIT 2", 200, 3_000, 800, EffectParam.Hz),
        new("x3", "SPLIT 3", 1_500, 12_000, 5_000, EffectParam.Hz),
        new("threshold", "THRESHOLD", -48, 0, -18, EffectParam.Db),
        new("ratio", "RATIO", 1, 12, 3, EffectParam.Ratio),
        new("attack", "ATTACK", 0.5, 100, 10, EffectParam.Ms),
        new("release", "RELEASE", 20, 800, 180, EffectParam.Ms),
        new("knee", "KNEE", 0, 18, 6, EffectParam.Db),
        new("lowgain", "LOW", -12, 12, 0, EffectParam.Db1),
        new("lowmidgain", "LOW MID", -12, 12, 0, EffectParam.Db1),
        new("highmidgain", "HIGH MID", -12, 12, 0, EffectParam.Db1),
        new("highgain", "HIGH", -12, 12, 0, EffectParam.Db1),
        new("slope", "SLOPE", 0, 1, 0, v => v > 0.5 ? "48dB" : "24dB"),
    ];

    private Crossover? _crossover;
    private double[][] _envelope = [];       // [band][channel]
    private double[][] _crest = [];          // [band][channel] — slow mean square, for the release rule
    private double[][] _peak = [];           // [band][channel] — fast peak, ditto
    private BandParameters _parameters = BandParameters.Silent;
    private double _reduction;

    private sealed record BandParameters(
        double Threshold, double Ratio, double Attack, double Release, double KneeDb,
        float[] MakeUp, double[] Splits, CrossoverSlope Slope)
    {
        public static BandParameters Silent { get; } =
            new(1, 1, 0.99, 0.999, 0, [1, 1, 1, 1], [120, 800, 5_000], CrossoverSlope.Lr24);
    }

    public override string TypeId => "multiband";
    public override string DisplayName => "Multiband Compressor";
    public override IReadOnlyList<EffectParam> Params => P;

    public override string Readout
    {
        get
        {
            double reduction = Volatile.Read(ref _reduction);
            return reduction < 0.05 ? "—" : $"GR {reduction:0.0} dB";
        }
    }

    protected override void OnConfigure()
    {
        _envelope = Allocate();
        _crest = Allocate();
        _peak = Allocate();
        OnParamsChanged();
        RebuildCrossover();
    }

    private double[][] Allocate()
    {
        var result = new double[BandCount][];
        for (int b = 0; b < BandCount; b++) result[b] = new double[ChannelCount];
        return result;
    }

    private void RebuildCrossover()
    {
        var parameters = Volatile.Read(ref _parameters);
        Volatile.Write(ref _crossover,
            new Crossover(SampleRate, parameters.Splits, ChannelCount, parameters.Slope));
    }

    protected override void OnParamsChanged()
    {
        // Kept in order: a split dragged past its neighbour would otherwise produce a band with no
        // width, which the crossover would de-duplicate into a different band count than the gains
        // are written for.
        double x1 = GetParam("x1");
        double x2 = Math.Max(GetParam("x2"), x1 * 1.2);
        double x3 = Math.Max(GetParam("x3"), x2 * 1.2);

        var previous = Volatile.Read(ref _parameters);
        var slope = GetParam("slope") > 0.5 ? CrossoverSlope.Lr48 : CrossoverSlope.Lr24;
        double[] splits = [x1, x2, x3];

        Volatile.Write(ref _parameters, new BandParameters(
            Math.Pow(10, GetParam("threshold") / 20.0),
            Math.Max(1, GetParam("ratio")),
            Coefficient(GetParam("attack")),
            Coefficient(GetParam("release")),
            GetParam("knee"),
            [
                (float)Math.Pow(10, GetParam("lowgain") / 20.0),
                (float)Math.Pow(10, GetParam("lowmidgain") / 20.0),
                (float)Math.Pow(10, GetParam("highmidgain") / 20.0),
                (float)Math.Pow(10, GetParam("highgain") / 20.0),
            ],
            splits,
            slope));

        // Only rebuilt when the geometry actually moved: a new crossover starts with empty filter
        // state, so doing it on every parameter touch would click on a gain change.
        bool moved = slope != previous.Slope;
        for (int i = 0; i < splits.Length && !moved; i++)
            moved = Math.Abs(splits[i] - previous.Splits[i]) > 1e-6;
        if (moved && _envelope.Length == BandCount) RebuildCrossover();
    }

    private double Coefficient(double milliseconds) =>
        Math.Exp(-1.0 / Math.Max(1, milliseconds * 0.001 * SampleRate));

    public override void ResetState()
    {
        foreach (double[] band in _envelope) Array.Clear(band);
        foreach (double[] band in _crest) Array.Clear(band);
        foreach (double[] band in _peak) Array.Clear(band);
        Volatile.Read(ref _crossover)?.Reset();
        Volatile.Write(ref _reduction, 0);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        Crossover? crossover = Volatile.Read(ref _crossover);
        var parameters = Volatile.Read(ref _parameters);
        if (crossover == null || crossover.Bands != BandCount) return;
        if (_envelope.Length != BandCount || _envelope[0].Length != ChannelCount) return;

        Span<float> bands = stackalloc float[BandCount];
        double worst = 0;

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % ChannelCount;
            crossover.ProcessSample(c, buffer[i], bands);

            float sum = 0;
            for (int b = 0; b < BandCount; b++)
            {
                double magnitude = Math.Abs(bands[b]);

                // Two more followers per band, purely to measure how peaky it is.
                _peak[b][c] = Math.Max(magnitude, magnitude + 0.999 * (_peak[b][c] - magnitude));
                _crest[b][c] = magnitude * magnitude + 0.9999 * (_crest[b][c] - magnitude * magnitude);

                double crest = _peak[b][c] / Math.Sqrt(Math.Max(_crest[b][c], 1e-12));
                double release = ProgramRelease(parameters.Release, crest);

                double coefficient = magnitude > _envelope[b][c] ? parameters.Attack : release;
                _envelope[b][c] = magnitude + coefficient * (_envelope[b][c] - magnitude);

                double gain = GainFor(_envelope[b][c], parameters);
                double reductionDb = gain < 1 ? -20 * Math.Log10(gain) : 0;
                if (reductionDb > worst) worst = reductionDb;

                sum += (float)(bands[b] * gain) * parameters.MakeUp[b];
            }
            buffer[i] = sum;
        }

        Volatile.Write(ref _reduction, worst);
    }

    /// <summary>
    /// Release scaled by how peaky the band is: fast for percussive material, slow for steady.
    /// </summary>
    /// <remarks>
    /// A crest factor near one is a sine or a steady wash, which wants the slow release that keeps
    /// the compression from being heard. A high crest factor is transients with gaps between them,
    /// which wants a fast release so the gain is back up before the next hit — a slow one there is
    /// heard as the whole mix breathing at the rhythm of the drums.
    /// </remarks>
    private static double ProgramRelease(double release, double crest)
    {
        // Converted back to a time constant, scaled, and converted again: scaling the coefficient
        // directly is not the same thing and gets the fast end badly wrong.
        double samples = -1.0 / Math.Log(Math.Clamp(release, 1e-9, 0.999999));
        double factor = Math.Clamp(4.0 / Math.Clamp(crest, 1, 8), 0.25, 1.0);
        return Math.Exp(-1.0 / Math.Max(1, samples * factor));
    }

    /// <summary>Gain for an envelope, with a soft knee around the threshold.</summary>
    private static double GainFor(double envelope, BandParameters parameters)
    {
        if (envelope <= 1e-9) return 1;

        double overDb = 20 * Math.Log10(envelope / parameters.Threshold);
        double knee = parameters.KneeDb;

        double reduction;
        if (overDb <= -knee / 2)
        {
            reduction = 0;
        }
        else if (overDb >= knee / 2 || knee <= 0)
        {
            reduction = overDb * (1 - 1 / parameters.Ratio);
        }
        else
        {
            // Quadratic through the knee, so the ratio arrives gradually rather than switching on.
            double t = overDb + knee / 2;
            reduction = (1 - 1 / parameters.Ratio) * t * t / (2 * knee);
        }

        return reduction <= 0 ? 1 : Math.Pow(10, -reduction / 20.0);
    }
}
