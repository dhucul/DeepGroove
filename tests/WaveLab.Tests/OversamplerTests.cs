using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class OversamplerTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 32_768;

    /// <summary>Hann-windowed level at a frequency, well clear of the filter's settling.</summary>
    private static double Level(float[] signal, double frequency, int from = 4_000, int to = 28_000)
    {
        double re = 0, im = 0, weight = 0;
        for (int i = from; i < to && i < signal.Length; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - from) / (double)(to - from));
            double omega = 2 * Math.PI * frequency * i / Rate;
            re += signal[i] * window * Math.Cos(omega);
            im -= signal[i] * window * Math.Sin(omega);
            weight += window;
        }
        return Math.Sqrt(re * re + im * im) / Math.Max(1, weight) * 2;
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-15));

    /// <summary>A hard non-linearity, which is what oversampling exists to make safe.</summary>
    private static float Saturate(float x) => (float)Math.Tanh(x * 4);

    /// <summary>Runs a tone through a non-linearity at the given oversampling factor.</summary>
    private static float[] Distort(double frequency, int factor)
    {
        var oversampler = new Oversampler(factor, channels: 1);
        var result = new float[Length];
        Span<float> high = stackalloc float[factor];

        for (int i = 0; i < Length; i++)
        {
            var input = (float)(0.8 * Math.Sin(2 * Math.PI * frequency * i / Rate));
            oversampler.Upsample(0, input, high);
            for (int p = 0; p < factor; p++) high[p] = Saturate(high[p]);
            result[i] = oversampler.Downsample(0, high);
        }
        return result;
    }

    // ── the round trip ───────────────────────────────────────────

    /// <summary>
    /// With nothing done in between, up and back down has to return the signal. Anything else means
    /// the filter is wrong before a non-linearity has had a chance to be blamed for it.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void UpAndBackDownReturnsTheSignal(int factor)
    {
        var oversampler = new Oversampler(factor, channels: 1);
        var input = new float[Length];
        var recovered = new float[Length];
        var random = new Random(19);

        // Genuinely band-limited, which means steeply so. A single one-pole is only 6 dB per octave
        // and still has real energy at 20 kHz; the anti-imaging filter has to remove that, and the
        // round trip then measures the filter doing its job rather than failing at it.
        var poles = new double[4];
        for (int i = 0; i < Length; i++)
        {
            double value = random.NextDouble() - 0.5;
            for (int p = 0; p < poles.Length; p++)
            {
                poles[p] = poles[p] * 0.85 + value * 0.15;
                value = poles[p];
            }
            input[i] = (float)(value * 400
                             + 0.3 * Math.Sin(2 * Math.PI * 440 * i / Rate)
                             + 0.2 * Math.Sin(2 * Math.PI * 3_000 * i / Rate)
                             + 0.1 * Math.Sin(2 * Math.PI * 9_000 * i / Rate));
        }

        Span<float> high = stackalloc float[factor];
        for (int i = 0; i < Length; i++)
        {
            oversampler.Upsample(0, input[i], high);
            recovered[i] = oversampler.Downsample(0, high);
        }

        // The delay the reported latency claims, and the one that actually lines the signal up.
        // They have to be the same number: everything downstream compensates by the reported one.
        int measured = 0;
        double best = double.NegativeInfinity;
        for (int delay = 0; delay <= 128; delay++)
        {
            double dot = 0;
            for (int i = 2_000; i < Length - 2_000; i += 3)
                if (i + delay < Length) dot += input[i] * recovered[i + delay];
            if (dot > best) { best = dot; measured = delay; }
        }

        int reported = oversampler.LatencySamples;
        double signal = 0, error = 0;
        for (int i = 2_000; i < Length - 2_000; i++)
        {
            double difference = input[i] - recovered[i + reported];
            signal += (double)input[i] * input[i];
            error += difference * difference;
        }
        double snr = 10 * Math.Log10(signal / Math.Max(error, 1e-30));

        output.WriteLine($"{factor}×: round trip {snr:0.0} dB; latency reported {reported}, " +
                         $"measured {measured}");
        Assert.Equal(reported, measured);
        Assert.True(snr > 60, $"{factor}× round trip only reached {snr:0.0} dB");
    }

    [Fact]
    public void AConstantSurvivesTheRoundTrip()
    {
        var oversampler = new Oversampler(4, channels: 1);
        Span<float> high = stackalloc float[4];

        float last = 0;
        for (int i = 0; i < 2_000; i++)
        {
            oversampler.Upsample(0, 0.5f, high);
            last = oversampler.Downsample(0, high);
        }

        output.WriteLine($"a constant 0.5 came back as {last:0.00000}");
        Assert.Equal(0.5, last, 0.002);
    }

    // ── the reason it exists ─────────────────────────────────────

    /// <summary>
    /// The headline. A non-linearity makes harmonics, and every harmonic above Nyquist folds back as
    /// a tone that was never in the music. Running it faster moves the fold-back point up.
    /// </summary>
    /// <remarks>
    /// Measured on the third harmonic of 7 kHz, which is 21 kHz — below Nyquist at 44.1 but only just
    /// — and on the fifth at 35 kHz, which is not, and folds to 9.1 kHz. That folded tone is the
    /// thing being counted.
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void OversamplingRemovesTheAliasAPlainNonLinearityCreates(int factor)
    {
        const double tone = 7_000;
        const double aliasOfFifth = Rate - 5 * tone;      // 35 kHz folds to 9.1 kHz

        float[] plain = Distort(tone, 1);
        float[] oversampled = Distort(tone, factor);

        double before = Level(plain, aliasOfFifth);
        double after = Level(oversampled, aliasOfFifth);
        double fundamentalBefore = Level(plain, tone);
        double fundamentalAfter = Level(oversampled, tone);

        output.WriteLine($"{factor}×: the fold-back at {aliasOfFifth:0} Hz went from " +
                         $"{Db(before):0.0} to {Db(after):0.0} dB; " +
                         $"the tone itself {Db(fundamentalBefore):0.0} → {Db(fundamentalAfter):0.0} dB");

        Assert.True(Db(before) - Db(after) > 20,
            $"{factor}× only removed {Db(before) - Db(after):0.0} dB of the alias");

        // And the wanted signal is still there.
        Assert.Equal(Db(fundamentalBefore), Db(fundamentalAfter), 1.0);
    }

    /// <summary>
    /// Every factor must leave the fold-back far below where no oversampling leaves it.
    /// </summary>
    /// <remarks>
    /// Not asserted as monotonic across factors. Past 2× the residue is already around −130 dB,
    /// which is the arithmetic's own floor rather than anything the filter is doing, and comparing
    /// two numbers down there measures rounding.
    /// </remarks>
    [Fact]
    public void EveryFactorRemovesTheFoldBack()
    {
        const double tone = 7_000;
        double plain = Db(Level(Distort(tone, 1), Rate - 5 * tone));

        foreach (int factor in new[] { 2, 4, 8 })
        {
            double alias = Db(Level(Distort(tone, factor), Rate - 5 * tone));
            output.WriteLine($"{factor}×: fold-back {alias:0.0} dB against {plain:0.0} with none");
            Assert.True(alias < plain - 60, $"{factor}× left the fold-back at {alias:0.0} dB");
        }
    }

    /// <summary>
    /// The comparison that justifies the kernel over what was there before: linear interpolation is
    /// not band-limited, so it leaves images for the non-linearity to fold back down.
    /// </summary>
    [Fact]
    public void ItIsFarBetterThanDoublingByLinearInterpolation()
    {
        const double tone = 7_000;
        const double alias = Rate - 5 * tone;

        // What SaturationEffect did: an interpolated midpoint, then saturate, then average back.
        var naive = new float[Length];
        float previous = 0;
        for (int i = 0; i < Length; i++)
        {
            var input = (float)(0.8 * Math.Sin(2 * Math.PI * tone * i / Rate));
            float middle = (input + previous) * 0.5f;
            naive[i] = (Saturate(middle) + Saturate(input)) * 0.5f;
            previous = input;
        }

        double naiveAlias = Db(Level(naive, alias));
        double properAlias = Db(Level(Distort(tone, 2), alias));

        output.WriteLine($"at 2×: linear interpolation leaves {naiveAlias:0.0} dB, " +
                         $"a band-limited kernel {properAlias:0.0} dB");
        Assert.True(properAlias < naiveAlias - 15,
            "the band-limited kernel should be far cleaner than linear interpolation");
    }

    // ── housekeeping ─────────────────────────────────────────────

    [Fact]
    public void AFactorOfOneIsAPassThrough()
    {
        var oversampler = new Oversampler(1, channels: 1);
        Span<float> high = stackalloc float[1];

        oversampler.Upsample(0, 0.25f, high);
        Assert.Equal(0.25f, high[0], 5);
        Assert.Equal(0.25f, oversampler.Downsample(0, high), 5);
    }

    [Fact]
    public void ChannelsAreFilteredIndependently()
    {
        var oversampler = new Oversampler(4, channels: 2);
        Span<float> high = stackalloc float[4];

        for (int i = 0; i < 500; i++)
        {
            oversampler.Upsample(0, 1f, high);
            oversampler.Downsample(0, high);
            oversampler.Upsample(1, 0f, high);
            float quiet = oversampler.Downsample(1, high);
            Assert.Equal(0f, quiet, 9);
        }
    }

    [Fact]
    public void ResetClearsTheHistory()
    {
        var oversampler = new Oversampler(4, channels: 1);
        Span<float> high = stackalloc float[4];

        for (int i = 0; i < 500; i++) { oversampler.Upsample(0, 1f, high); oversampler.Downsample(0, high); }
        oversampler.Reset();
        oversampler.Upsample(0, 0f, high);

        Assert.Equal(0f, oversampler.Downsample(0, high), 9);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(16)]
    public void AnUnsupportedFactorIsRejected(int factor) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Oversampler(factor, 1));

    [Fact]
    public void AnOddTapCountIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Oversampler(2, 1, tapsPerPhase: 31));
}


