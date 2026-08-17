using WaveLab.Audio.Montage;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The fade shapes, and the claim the crossfade law rests on: that the level through an overlap is
/// flat whatever the two clips contain.
/// </summary>
public sealed class FadeCurveTests(ITestOutputHelper output)
{
    private static readonly FadeShape[] AllShapes =
        [FadeShape.Linear, FadeShape.EqualPower, FadeShape.SCurve, FadeShape.DecibelLinear];

    // ── shapes ───────────────────────────────────────────────────

    [Theory]
    [InlineData(FadeShape.Linear)]
    [InlineData(FadeShape.EqualPower)]
    [InlineData(FadeShape.SCurve)]
    [InlineData(FadeShape.DecibelLinear)]
    public void EveryShapeReachesSilenceAndUnityExactly(FadeShape shape)
    {
        Assert.Equal(0, Fades.In(shape, 0), 12);
        Assert.Equal(1, Fades.In(shape, 1), 12);
        Assert.Equal(1, Fades.Out(shape, 0), 12);
        Assert.Equal(0, Fades.Out(shape, 1), 12);
    }

    [Theory]
    [InlineData(FadeShape.Linear)]
    [InlineData(FadeShape.EqualPower)]
    [InlineData(FadeShape.SCurve)]
    [InlineData(FadeShape.DecibelLinear)]
    public void EveryShapeRisesWithoutTurningBack(FadeShape shape)
    {
        double previous = -1;
        for (int i = 0; i <= 1000; i++)
        {
            double gain = Fades.In(shape, i / 1000.0);
            Assert.True(gain >= previous - 1e-12, $"fell at t={i / 1000.0}: {gain} < {previous}");
            previous = gain;
        }
    }

    /// <summary>
    /// The point of the shape: a constant rate of decay. The usual form never reaches silence and
    /// has to be clamped, which puts a step at the start of every fade; this one arrives exactly.
    /// </summary>
    [Fact]
    public void ADecibelLinearFadeIsAStraightLineWhereItIsStillAudible()
    {
        double worstAudible = 0, deepest = 0;
        for (int i = 1; i <= 1000; i++)
        {
            double t = i / 1000.0;
            double db = 20 * Math.Log10(Fades.In(FadeShape.DecibelLinear, t));
            double ideal = (t - 1) * Fades.DecibelRange;
            if (ideal >= Fades.DecibelLinearTo) worstAudible = Math.Max(worstAudible, Math.Abs(db - ideal));
            else deepest = Math.Max(deepest, ideal - db);
        }

        output.WriteLine($"above {Fades.DecibelLinearTo:0} dB: within {worstAudible:0.000} dB of the line");
        output.WriteLine($"below it: dives up to {deepest:0.0} dB faster, which is how it reaches silence");

        Assert.True(worstAudible < 0.5, $"worst {worstAudible:0.000} dB above {Fades.DecibelLinearTo} dB");

        // The trade this shape makes: exact silence at the cost of straightness in the last 25 dB.
        Assert.True(deepest > 1, "the tail should accelerate rather than track the line to nothing");
        Assert.Equal(0, Fades.In(FadeShape.DecibelLinear, 0), 12);
    }

    [Fact]
    public void AnEnvelopeSpansTheWholeFadeRatherThanStoppingShort()
    {
        double[] gains = Fades.Envelope(FadeShape.Linear, 5);
        Assert.Equal([0, 0.25, 0.5, 0.75, 1], gains);

        double[] outgoing = Fades.Envelope(FadeShape.Linear, 5, fadeIn: false);
        Assert.Equal([1, 0.75, 0.5, 0.25, 0], outgoing);
        Assert.Empty(Fades.Envelope(FadeShape.Linear, 0));
    }

    // ── the two familiar laws fall out of the general one ────────

    [Fact]
    public void UncorrelatedMaterialGetsTheEqualPowerLaw()
    {
        var (outgoing, incoming) = Crossfade.Law(FadeShape.EqualPower, 256, correlation: 0);

        for (int i = 0; i < incoming.Length; i++)
        {
            double power = incoming[i] * incoming[i] + outgoing[i] * outgoing[i];
            Assert.Equal(1, power, 10);
        }
    }

    [Fact]
    public void IdenticalMaterialGetsTheEqualGainLaw()
    {
        var (outgoing, incoming) = Crossfade.Law(FadeShape.EqualPower, 256, correlation: 1);

        for (int i = 0; i < incoming.Length; i++)
            Assert.Equal(1, incoming[i] + outgoing[i], 10);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void TheLawIsAFadeAtEveryCorrelation(double correlation)
    {
        var (outgoing, incoming) = Crossfade.Law(FadeShape.EqualPower, 512, correlation);

        Assert.Equal(1, outgoing[0], 10);
        Assert.Equal(0, outgoing[^1], 10);
        Assert.Equal(0, incoming[0], 10);
        Assert.Equal(1, incoming[^1], 10);

        for (int i = 1; i < outgoing.Length; i++)
            Assert.True(outgoing[i] <= outgoing[i - 1] + 1e-12,
                $"the outgoing gain rose at {i}: {outgoing[i]} > {outgoing[i - 1]}");
    }

    /// <summary>
    /// The claim the whole design rests on, checked on the arithmetic rather than the audio: the
    /// summed power is one at every point of the fade, for every correlation.
    /// </summary>
    [Fact]
    public void TheSummedPowerIsFlatAtEveryCorrelation()
    {
        double worst = 0;
        for (int step = 0; step <= 20; step++)
        {
            double rho = step / 20.0;
            var (outgoing, incoming) = Crossfade.Law(FadeShape.EqualPower, 400, rho);

            for (int i = 0; i < incoming.Length; i++)
            {
                double a = incoming[i], b = outgoing[i];
                double power = a * a + b * b + 2 * a * b * rho;
                worst = Math.Max(worst, Math.Abs(power - 1));
            }
        }

        output.WriteLine($"worst departure from unit power across ρ = 0…1: {worst:0.###e+0}");
        Assert.True(worst < 1e-9, $"worst {worst}");
    }

    // ── measured on real signals ─────────────────────────────────

    private static float[][] Tone(int count, double frequency, double amplitude = 0.5, double phase = 0)
    {
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[count];
            for (int i = 0; i < count; i++)
                data[c][i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / 44100.0 + phase));
        }
        return data;
    }

    private static float[][] Noise(int count, int seed, double amplitude = 0.3)
    {
        var random = new Random(seed);
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[count];
            for (int i = 0; i < count; i++)
                data[c][i] = (float)(amplitude * (random.NextDouble() * 2 - 1));
        }
        return data;
    }

    /// <summary>
    /// Peak-to-trough spread of the short-term level through the crossfade, in dB.
    /// </summary>
    /// <remarks>
    /// <b>Measured on tones, not on noise.</b> A 512-sample window of noise has an RMS that wanders
    /// by about 0.2 dB on its own, and across a hundred-odd windows the peak-to-trough of that
    /// wander is close to a dB — which is the same size as the error being hunted. The first version
    /// of this test measured the noise and reported it as the law's.
    /// </remarks>
    private static double LevelRippleDb(float[][] first, float[][] second, double correlation,
        FadeShape shape = FadeShape.EqualPower)
    {
        int count = first[0].Length;
        var (outgoing, incoming) = Crossfade.Law(shape, count, correlation);

        const int window = 4_096;
        double lowest = double.MaxValue, highest = 0;

        // The ends are skipped: there only one clip is playing, so the level is that clip's own and
        // says nothing about the law.
        for (int start = window; start + window * 2 <= count; start += window / 2)
        {
            double energy = 0;
            for (int c = 0; c < first.Length; c++)
                for (int i = start; i < start + window; i++)
                {
                    double sample = first[c][i] * outgoing[i] + second[c][i] * incoming[i];
                    energy += sample * sample;
                }

            double rms = Math.Sqrt(energy / (window * first.Length));
            if (rms <= 1e-12) continue;
            lowest = Math.Min(lowest, rms);
            highest = Math.Max(highest, rms);
        }

        return lowest == double.MaxValue ? 0 : 20 * Math.Log10(highest / lowest);
    }

    /// <summary>
    /// Level at the exact middle of the crossfade against the level of either clip alone, in dB —
    /// the textbook claim, where the two familiar laws are wrong by their famous 3 dB.
    /// </summary>
    private static double MidpointErrorDb(float[][] first, float[][] second, double correlation,
        FadeShape shape = FadeShape.EqualPower)
    {
        int count = first[0].Length;
        var (outgoing, incoming) = Crossfade.Law(shape, count, correlation);

        const int window = 4_096;
        int start = count / 2 - window / 2;
        double summed = 0, alone = 0;

        for (int c = 0; c < first.Length; c++)
            for (int i = start; i < start + window; i++)
            {
                double mixed = first[c][i] * outgoing[i] + second[c][i] * incoming[i];
                summed += mixed * mixed;
                alone += first[c][i] * (double)first[c][i];
            }

        return 10 * Math.Log10(summed / alone);
    }

    [Fact]
    public void TheMeasuredLevelIsFlatThroughACrossfadeOfUnrelatedMaterial()
    {
        // A whole number of cycles of each in the span, so the two are exactly orthogonal and the
        // only thing left for the metric to see is the law.
        const int count = 44_100;
        float[][] first = Tone(count, 440);
        float[][] second = Tone(count, 1_100);

        double rho = Crossfade.MeasureCorrelation(first, 0, second, 0, count);
        double ripple = LevelRippleDb(first, second, rho);
        double measured = MidpointErrorDb(first, second, rho);
        double equalGain = MidpointErrorDb(first, second, correlation: 1);

        output.WriteLine($"measured ρ = {rho:0.0000}, ripple {ripple:0.00} dB");
        output.WriteLine($"at the midpoint: measured law {measured:+0.00;-0.00} dB, " +
                         $"equal-gain law {equalGain:+0.00;-0.00} dB");

        Assert.True(rho < 0.02, $"unrelated tones correlated at {rho}");
        Assert.True(ripple < 0.1, $"ripple {ripple:0.00} dB");
        Assert.True(Math.Abs(measured) < 0.05, $"midpoint off by {measured:0.00} dB");

        // The dip the equal-gain law puts in an unrelated join. It is 2.3 dB rather than the
        // textbook 3.0 because only the *partner* is solved here — the incoming curve keeps the
        // shape the user chose, so the pair is not the symmetric one the 3 dB figure assumes. With
        // a linear incoming shape the pair is symmetric and the dip is the familiar 3.01.
        Assert.InRange(equalGain, -2.6, -2.1);
        Assert.InRange(MidpointErrorDb(first, second, 1, FadeShape.Linear), -3.2, -2.9);
    }

    [Fact]
    public void TheMeasuredLevelIsFlatThroughACrossfadeOfTheSameMaterial()
    {
        const int count = 44_100;
        float[][] first = Tone(count, 440);
        float[][] second = Tone(count, 440);   // the same take on both sides

        double rho = Crossfade.MeasureCorrelation(first, 0, second, 0, count);
        double ripple = LevelRippleDb(first, second, rho);
        double measured = MidpointErrorDb(first, second, rho);
        double equalPower = MidpointErrorDb(first, second, correlation: 0);

        output.WriteLine($"measured ρ = {rho:0.0000}, ripple {ripple:0.00} dB");
        output.WriteLine($"at the midpoint: measured law {measured:+0.00;-0.00} dB, " +
                         $"equal-power law {equalPower:+0.00;-0.00} dB");

        Assert.True(rho > 0.999, $"identical material correlated at only {rho}");
        Assert.True(ripple < 0.1, $"ripple {ripple:0.00} dB");
        Assert.True(Math.Abs(measured) < 0.05, $"midpoint off by {measured:0.00} dB");

        // The bump the equal-power law puts in a join between two takes of the same thing.
        Assert.InRange(equalPower, 2.7, 3.3);
    }

    /// <summary>
    /// The case neither familiar law handles: two takes that share a lot but not everything. Both
    /// fixed laws are wrong here, in opposite directions, and the measured one is right.
    /// </summary>
    [Fact]
    public void PartlyRelatedMaterialIsHandledByNeitherFixedLaw()
    {
        const int count = 44_100;
        float[][] first = Tone(count, 440, amplitude: 0.28);
        float[][] second = Tone(count, 440, amplitude: 0.28);
        float[][] distinctA = Tone(count, 700, amplitude: 0.15);
        float[][] distinctB = Tone(count, 1_300, amplitude: 0.15);
        for (int c = 0; c < 2; c++)
            for (int i = 0; i < count; i++)
            {
                first[c][i] += distinctA[c][i];
                second[c][i] += distinctB[c][i];
            }

        double rho = Crossfade.MeasureCorrelation(first, 0, second, 0, count);
        double measured = MidpointErrorDb(first, second, rho);
        double equalPower = MidpointErrorDb(first, second, correlation: 0);
        double equalGain = MidpointErrorDb(first, second, correlation: 1);

        output.WriteLine($"measured ρ = {rho:0.000}, ripple {LevelRippleDb(first, second, rho):0.00} dB");
        output.WriteLine($"at the midpoint: measured {measured:+0.00;-0.00} dB · " +
                         $"equal power {equalPower:+0.00;-0.00} dB · equal gain {equalGain:+0.00;-0.00} dB");

        Assert.InRange(rho, 0.5, 0.95);
        Assert.True(Math.Abs(measured) < 0.05, $"midpoint off by {measured:0.00} dB");

        // Both fixed laws miss, in opposite directions, and by different amounts — which is exactly
        // why neither can be the one law for a montage.
        Assert.True(equalPower > 2.0, $"equal power should overshoot here, was {equalPower:0.00} dB");
        Assert.True(equalGain < -0.3, $"equal gain should undershoot here, was {equalGain:0.00} dB");
    }

    // ── correlation ──────────────────────────────────────────────

    [Fact]
    public void CorrelationIsOneForTheSameSpanAndZeroForSilence()
    {
        float[][] tone = Tone(4_096, 440);
        Assert.Equal(1, Crossfade.MeasureCorrelation(tone, 0, tone, 0, 4_096), 6);

        float[][] silence = [new float[4_096], new float[4_096]];
        Assert.Equal(0, Crossfade.MeasureCorrelation(tone, 0, silence, 0, 4_096));
        Assert.Equal(0, Crossfade.MeasureCorrelation(silence, 0, silence, 0, 4_096));
    }

    /// <summary>
    /// A polarity-inverted copy cancels rather than adds. No monotone pair of fades holds the level
    /// through that — the compensation would not reach zero at the end of the overlap — so the
    /// measurement is floored at zero and the fix belongs upstream, in the polarity.
    /// </summary>
    [Fact]
    public void AnInvertedCopyIsFlooredAtZeroRatherThanDrivingTheLawNegative()
    {
        float[][] tone = Tone(4_096, 440);
        float[][] inverted = Tone(4_096, 440, phase: Math.PI);

        Assert.Equal(0, Crossfade.MeasureCorrelation(tone, 0, inverted, 0, 4_096));

        // And the law stays a fade whatever it is handed.
        var (outgoing, incoming) = Crossfade.Law(FadeShape.EqualPower, 128, correlation: -1);
        Assert.Equal(1, outgoing[0], 10);
        Assert.Equal(0, outgoing[^1], 10);
        Assert.Equal(1, incoming[^1], 10);
    }

    [Fact]
    public void ReadingPastTheEndOfAClipCountsAsSilenceRatherThanThrowing()
    {
        float[][] tone = Tone(1_000, 440);
        double rho = Crossfade.MeasureCorrelation(tone, 900, tone, 0, 400);

        output.WriteLine($"ρ across the end of the buffer: {rho:0.000}");
        Assert.InRange(rho, 0, 1);
    }

    [Fact]
    public void ANonFiniteGainOrCorrelationDoesNotEscapeTheLaw()
    {
        Assert.Equal(0, Fades.In(FadeShape.EqualPower, double.NaN));
        Assert.InRange(Crossfade.Partner(double.NaN, 0.5), 0, 1);
        Assert.InRange(Crossfade.Partner(0.5, double.NaN), 0, 1);
        Assert.InRange(Crossfade.Partner(2, 0.5), 0, 1);
    }
}
