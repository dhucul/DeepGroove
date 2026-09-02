using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Standards-based playback de-emphasis and its reciprocal production emphasis.
/// </summary>
/// <remarks>
/// <para>
/// FM follows the 50 µs and 75 µs parallel-RC characteristics in ITU-R BS.450. The ideal analogue
/// emphasis rises without limit, which cannot be inverted safely at digital Nyquist, so the curve
/// is represented by a minimum-phase high shelf fitted to the specified RC response and its gain at
/// Nyquist. This keeps both directions stable while tracking the broadcast curve through its 15 kHz
/// programme band.
/// </para>
/// <para>
/// The CD/IEC curve is the bounded 50/15 µs pole/zero pair. Its analogue pole and zero are mapped
/// directly into the z-plane; emphasis swaps numerator and denominator, making the two directions
/// reciprocal.
/// </para>
/// </remarks>
public sealed class EmphasisEffect : EffectBase
{
    private const double Fm50Seconds = 50e-6;
    private const double Fm75Seconds = 75e-6;
    private const double CdPoleSeconds = 50e-6;
    private const double CdZeroSeconds = 15e-6;

    private static readonly EffectParam[] P =
    [
        new("direction", "DIRECTION", 0, 1, 0,
            value => value > 0.5 ? "EMPHASIS" : "DE-EMPH", Step: 1),
        new("standard", "STANDARD", 0, 2, 2, value => ((int)Math.Round(value)) switch
        {
            0 => "FM 50 µs",
            1 => "FM 75 µs",
            _ => "CD/IEC 50/15 µs",
        }, Step: 1),
    ];

    private sealed record FilterSnapshot(BiquadCoefficients Coefficients, long Revision);

    private readonly object _rebuildLock = new();
    private Biquad[] _filters = [];
    private FilterSnapshot _published = new(BiquadCoefficients.Identity, 0);
    private long _nextRevision;
    private long _appliedRevision = -1;
    private int _resetRequested;
    private int _fittedSampleRate;
    private double _fm50Q = double.NaN;
    private double _fm75Q = double.NaN;

    public override string TypeId => "deemphasis";
    public override string DisplayName => "Emphasis / De-Emphasis";
    public override IReadOnlyList<EffectParam> Params => P;
    public override string Readout =>
        $"{(IsEmphasis ? "EMPH" : "DE-EMPH")} · {StandardName(Standard)}";

    private bool IsEmphasis => GetParam("direction") > 0.5;
    private int Standard => Math.Clamp((int)Math.Round(GetParam("standard")), 0, 2);

    protected override void OnConfigure()
    {
        _filters = new Biquad[ChannelCount];
        _appliedRevision = -1;
        if (_fittedSampleRate == SampleRate) return;
        _fittedSampleRate = SampleRate;
        _fm50Q = _fm75Q = double.NaN;
    }

    protected override void OnParamsChanged() => Rebuild();

    private void Rebuild()
    {
        lock (_rebuildLock)
        {
            bool emphasis = IsEmphasis;
            Biquad filter = Standard switch
            {
                0 => BuildFm(Fm50Seconds, emphasis),
                1 => BuildFm(Fm75Seconds, emphasis),
                _ => BuildCd(emphasis),
            };

            // Coefficients and their revision cross the thread boundary as one object. Process
            // resets before applying any revision it has not seen, so there is no interval in
            // which new coefficients can run against the preceding curve's delay-line state.
            long revision = Interlocked.Increment(ref _nextRevision);
            Volatile.Write(ref _published,
                new FilterSnapshot(new BiquadCoefficients(filter), revision));
        }
    }

    private Biquad BuildFm(double timeConstant, bool emphasis)
    {
        double corner = 1.0 / (2.0 * Math.PI * timeConstant);
        double nyquist = SampleRate * 0.5;
        double shelfGain = Math.Sqrt(1.0 + nyquist * nyquist / (corner * corner));
        double centre = corner * Math.Sqrt(shelfGain - 1.0);

        // Fix the shelf's endpoints, then choose its shape against the actual RC curve through the
        // 15 kHz FM programme band. The small deterministic fit runs only on configuration or a
        // parameter move, never in the audio callback.
        ref double cachedQ = ref timeConstant == Fm75Seconds ? ref _fm75Q : ref _fm50Q;
        if (double.IsNaN(cachedQ)) cachedQ = FitFmQ(timeConstant, centre, shelfGain);
        double q = cachedQ;
        return BuildFmShelf(centre, shelfGain, q, emphasis);
    }

    private double FitFmQ(double timeConstant, double centre, double shelfGain)
    {
        const int FrequencyPoints = 48;
        const double MinimumQ = 0.25;
        const double QStep = 0.0025;
        const int QSteps = 200;

        double upperFrequency = Math.Min(15_000.0, SampleRate * 0.45);

        double bestQ = 0.5;
        double bestError = double.PositiveInfinity;
        for (int qIndex = 0; qIndex <= QSteps; qIndex++)
        {
            double q = MinimumQ + qIndex * QStep;
            Biquad candidate = BuildFmShelf(centre, shelfGain, q, emphasis: false);
            double worstError = 0;
            for (int point = 0; point < FrequencyPoints; point++)
            {
                double fraction = point / (double)(FrequencyPoints - 1);
                double frequency = 20.0 * Math.Pow(upperFrequency / 20.0, fraction);
                double expected = -10.0 * Math.Log10(
                    1.0 + Math.Pow(2.0 * Math.PI * frequency * timeConstant, 2));
                double error = Math.Abs(candidate.MagnitudeDb(frequency, SampleRate) - expected);
                worstError = Math.Max(worstError, error);
                if (worstError >= bestError) break;
            }

            if (worstError >= bestError) continue;
            bestError = worstError;
            bestQ = q;
        }
        return bestQ;
    }

    private Biquad BuildFmShelf(double centre, double shelfGain, double q, bool emphasis)
    {
        // Convert the Q form of the RBJ high shelf to Biquad.HighShelf's equivalent slope form.
        // Because A + 1/A is unchanged when the gain is inverted, the same shape is an exact,
        // stable reciprocal for production emphasis.
        double a = Math.Sqrt(shelfGain);
        double slope = 1.0 /
            (1.0 + (1.0 / (q * q) - 2.0) / (a + 1.0 / a));
        double gainDb = 20.0 * Math.Log10(shelfGain) * (emphasis ? 1.0 : -1.0);
        return Biquad.HighShelf(SampleRate, centre, gainDb, slope);
    }

    private Biquad BuildCd(bool emphasis)
    {
        // Hde(s) = (1 + s*15us) / (1 + s*50us). Matched pole/zero mapping avoids the
        // severe top-octave frequency warping of an ordinary bilinear transform at 44.1/48 kHz.
        // The gain is normalized to 0 dB at DC, and both mapped roots are strictly inside the unit
        // circle, so the reciprocal production curve is causal and stable too.
        double pole = Math.Exp(-1.0 / (SampleRate * CdPoleSeconds));
        double zero = Math.Exp(-1.0 / (SampleRate * CdZeroSeconds));
        double gain = (1.0 - pole) / (1.0 - zero);
        double b0 = gain;
        double b1 = -gain * zero;
        double a1 = -pole;

        if (!emphasis)
            return Biquad.FromNormalized(b0, b1, 0, a1, 0);

        // Reciprocal transfer: (1 + a1*z^-1) / (b0 + b1*z^-1).
        return Biquad.FromNormalized(1.0 / b0, a1 / b0, 0, b1 / b0, 0);
    }

    private static string StandardName(int standard) => standard switch
    {
        0 => "FM 50 µs",
        1 => "FM 75 µs",
        _ => "CD/IEC 50/15 µs",
    };

    public override void ResetState()
    {
        // Delay lines belong exclusively to Process. Control-thread resets are requests, never
        // writes into state that the audio callback may be updating at the same time.
        Interlocked.Exchange(ref _resetRequested, 1);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        Biquad[] filters = _filters;
        if (filters.Length != ChannelCount) return;

        FilterSnapshot snapshot = Volatile.Read(ref _published);
        bool reset = snapshot.Revision != _appliedRevision;
        if (Interlocked.Exchange(ref _resetRequested, 0) != 0) reset = true;
        if (reset)
            for (int channel = 0; channel < filters.Length; channel++)
                filters[channel].Reset();

        snapshot.Coefficients.ApplyTo(filters);
        _appliedRevision = snapshot.Revision;
        int end = offset + count;
        for (int index = offset; index < end; index++)
        {
            int channel = (index - offset) % ChannelCount;
            float output = filters[channel].Process(buffer[index]);
            if (!float.IsFinite(output))
            {
                filters[channel].Reset();
                output = 0;
            }
            buffer[index] = output;
        }
    }
}
