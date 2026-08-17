using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class DitherTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 1 << 16;

    /// <summary>
    /// The noise a dither adds, as a spectrum: quantise a signal, subtract it, and transform what is
    /// left. This is the only way to judge noise shaping — a wideband figure says a shaped dither is
    /// louder, which is true and beside the point.
    /// </summary>
    private static double[] NoiseSpectrum(DitherKind kind, int bits = 16, double toneLevel = 0.2)
    {
        var dither = new Dither(kind, bits, channels: 1, Rate, autoBlank: false, seed: 12345);
        var error = new float[Length];

        for (int i = 0; i < Length; i++)
        {
            // A tone well away from the frequencies being examined, so what is measured is the
            // dither and not the signal.
            double sample = toneLevel * Math.Sin(2 * Math.PI * 997 * i / Rate);
            error[i] = (float)(dither.Process(0, sample) - sample);
        }

        var window = new float[Length];
        for (int i = 0; i < Length; i++)
            window[i] = (float)(error[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Length)));

        int bins = Length / 2 + 1;
        var re = new float[bins];
        var im = new float[bins];
        Fft.RealForward(window, re, im);

        var magnitude = new double[bins];
        for (int b = 0; b < bins; b++)
            magnitude[b] = Math.Sqrt((double)re[b] * re[b] + (double)im[b] * im[b]);
        return magnitude;
    }

    /// <summary>Average level over a band, in dB relative to an arbitrary but shared reference.</summary>
    private static double BandDb(double[] spectrum, double lowHz, double highHz)
    {
        double resolution = (double)Rate / Length;
        int from = Math.Max(1, (int)(lowHz / resolution));
        int to = Math.Min(spectrum.Length - 1, (int)(highHz / resolution));
        double sum = 0;
        for (int b = from; b <= to; b++) sum += spectrum[b] * spectrum[b];
        return 10 * Math.Log10(Math.Max(sum / Math.Max(1, to - from + 1), 1e-30));
    }

    // ── the shaping curve ────────────────────────────────────────

    /// <summary>
    /// The measurement that says whether the shaping coefficients are what they claim to be. Both
    /// curves must take noise <em>out</em> of the band where hearing is keenest and put it above
    /// 15 kHz, where it is not.
    /// </summary>
    [Theory]
    [InlineData(DitherKind.NoiseShapedE)]
    [InlineData(DitherKind.NoiseShapedF)]
    public void NoiseShapingMovesNoiseOutOfTheEarsBestBand(DitherKind kind)
    {
        double[] flat = NoiseSpectrum(DitherKind.FlatTpdf);
        double[] shaped = NoiseSpectrum(kind);

        double flatMid = BandDb(flat, 1_000, 5_000);
        double shapedMid = BandDb(shaped, 1_000, 5_000);
        double flatTop = BandDb(flat, 16_000, 21_000);
        double shapedTop = BandDb(shaped, 16_000, 21_000);

        output.WriteLine($"{kind}, against flat TPDF:");
        foreach ((double low, double high) in new[]
                 {
                     (20.0, 200.0), (200.0, 1_000.0), (1_000.0, 2_000.0), (2_000.0, 4_000.0),
                     (4_000.0, 6_000.0), (6_000.0, 10_000.0), (10_000.0, 15_000.0), (15_000.0, 21_000.0),
                 })
        {
            output.WriteLine($"   {low / 1000,5:0.0}–{high / 1000,4:0.0} kHz: " +
                             $"{BandDb(shaped, low, high) - BandDb(flat, low, high),6:+0.0;-0.0;0.0} dB");
        }

        Assert.True(shapedMid < flatMid - 1.5,
            $"{kind} only took {flatMid - shapedMid:0.0} dB out of the sensitive band");
        Assert.True(shapedTop > flatTop + 3,
            $"{kind} did not put the noise anywhere: the top only rose {shapedTop - flatTop:0.0} dB");
    }

    /// <summary>
    /// Noise power can only be moved, never removed. A shaped dither has to measure <em>louder</em>
    /// wideband than a flat one, and a claim that it is quieter would mean the measurement is wrong.
    /// </summary>
    [Theory]
    [InlineData(DitherKind.NoiseShapedE)]
    [InlineData(DitherKind.NoiseShapedF)]
    public void ShapingCostsWidebandNoiseToBuyAudibleQuiet(DitherKind kind)
    {
        double flat = BandDb(NoiseSpectrum(DitherKind.FlatTpdf), 20, 22_000);
        double shaped = BandDb(NoiseSpectrum(kind), 20, 22_000);

        output.WriteLine($"{kind}: wideband {flat:0.0} → {shaped:0.0} dB ({shaped - flat:+0.0;-0.0} dB)");
        Assert.True(shaped > flat, "shaping should raise the total noise while lowering the audible part");
    }

    [Fact]
    public void TheHighPassVariantTiltsItsOwnNoiseUpward()
    {
        double[] flat = NoiseSpectrum(DitherKind.FlatTpdf);
        double[] tilted = NoiseSpectrum(DitherKind.HighPassTpdf);

        double lowChange = BandDb(tilted, 100, 2_000) - BandDb(flat, 100, 2_000);
        double highChange = BandDb(tilted, 15_000, 21_000) - BandDb(flat, 15_000, 21_000);

        output.WriteLine($"high-pass TPDF: 100 Hz–2 kHz {lowChange:+0.0;-0.0} dB, " +
                         $"15–21 kHz {highChange:+0.0;-0.0} dB");
        Assert.True(lowChange < -2, "the low end should be quieter than flat TPDF");
        Assert.True(highChange > lowChange + 4, "the noise should be tilted, not merely quieter");
    }

    // ── what dither is for ───────────────────────────────────────

    /// <summary>
    /// The reason dither exists. Undithered truncation leaves error correlated with the signal,
    /// which appears as harmonic distortion; dither replaces it with steady noise.
    /// </summary>
    [Fact]
    public void DitherReplacesDistortionWithNoise()
    {
        // A quiet tone, where truncation error is a large fraction of the signal.
        const double tone = 1_000;
        const double level = 0.00025;

        double Harmonic(DitherKind kind, double frequency)
        {
            var dither = new Dither(kind, 16, channels: 1, Rate, autoBlank: false, seed: 999);
            var samples = new float[Length];
            for (int i = 0; i < Length; i++)
                samples[i] = (float)dither.Process(0, level * Math.Sin(2 * Math.PI * tone * i / Rate));

            double re = 0, im = 0, weight = 0;
            for (int i = 0; i < Length; i++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Length);
                double omega = 2 * Math.PI * frequency * i / Rate;
                re += samples[i] * window * Math.Cos(omega);
                im -= samples[i] * window * Math.Sin(omega);
                weight += window;
            }
            return 20 * Math.Log10(Math.Max(Math.Sqrt(re * re + im * im) / weight * 2, 1e-15));
        }

        double truncatedThird = Harmonic(DitherKind.None, tone * 3);
        double ditheredThird = Harmonic(DitherKind.FlatTpdf, tone * 3);

        output.WriteLine($"third harmonic of a quiet tone: truncated {truncatedThird:0.0} dB, " +
                         $"dithered {ditheredThird:0.0} dB");
        Assert.True(ditheredThird < truncatedThird - 10,
            "dither should bury the distortion product truncation leaves");
    }

    [Fact]
    public void EveryOutputLandsExactlyOnAQuantisationStep()
    {
        foreach (DitherKind kind in Enum.GetValues<DitherKind>())
        {
            var dither = new Dither(kind, 16, channels: 1, Rate, autoBlank: false);
            for (int i = 0; i < 5_000; i++)
            {
                double value = dither.Process(0, 0.31 * Math.Sin(i * 0.017));
                double steps = value / dither.Lsb;
                Assert.Equal(Math.Round(steps), steps, 6);
            }
        }
    }

    [Fact]
    public void NothingEverExceedsFullScale()
    {
        foreach (DitherKind kind in Enum.GetValues<DitherKind>())
        {
            var dither = new Dither(kind, 16, channels: 1, Rate, autoBlank: false);
            for (int i = 0; i < 20_000; i++)
            {
                // Driven right at the rails, where the shaper's feedback could push it over.
                double value = dither.Process(0, i % 2 == 0 ? 0.99999 : -0.99999);
                Assert.InRange(value, -1.0, 1.0);
            }
        }
    }

    // ── auto-blanking ────────────────────────────────────────────

    /// <summary>
    /// Digital silence should stay silent. Hiss in a run-out or between tracks is audible precisely
    /// because there is nothing else there.
    /// </summary>
    [Fact]
    public void DigitalSilenceStaysSilent()
    {
        var dither = new Dither(DitherKind.NoiseShapedE, 16, channels: 1, Rate, autoBlank: true);

        double sum = 0;
        for (int i = 0; i < 40_000; i++)
        {
            double value = dither.Process(0, 0);
            if (i > 20_000) sum += Math.Abs(value);
        }

        output.WriteLine($"after blanking, {sum:0.000000} of residual across 20 000 samples");
        Assert.Equal(0, sum, 12);
    }

    [Fact]
    public void BlankingLiftsAsSoonAsThereIsSignalAgain()
    {
        var dither = new Dither(DitherKind.FlatTpdf, 16, channels: 1, Rate, autoBlank: true);
        for (int i = 0; i < 40_000; i++) dither.Process(0, 0);

        bool moved = false;
        for (int i = 0; i < 200 && !moved; i++)
            if (dither.Process(0, 0.001 * Math.Sin(i * 0.3)) != 0) moved = true;

        Assert.True(moved, "dither should resume once there is signal again");
    }

    [Fact]
    public void WithoutBlankingSilenceStillGetsDither()
    {
        var dither = new Dither(DitherKind.FlatTpdf, 16, channels: 1, Rate, autoBlank: false);

        double sum = 0;
        for (int i = 0; i < 20_000; i++) sum += Math.Abs(dither.Process(0, 0));
        Assert.True(sum > 0, "with blanking off, silence should still be dithered");
    }

    // ── housekeeping ─────────────────────────────────────────────

    [Theory]
    [InlineData(16, 32768.0)]
    [InlineData(24, 8388608.0)]
    [InlineData(8, 128.0)]
    public void TheStepSizeFollowsTheTargetDepth(int bits, double expectedFullScale)
    {
        var dither = new Dither(DitherKind.FlatTpdf, bits, channels: 1, Rate);
        Assert.Equal(expectedFullScale, dither.FullScale);
        Assert.Equal(1 / expectedFullScale, dither.Lsb, 12);
    }

    [Fact]
    public void NoneIsPlainQuantisation()
    {
        var dither = new Dither(DitherKind.None, 16, channels: 1, Rate);
        Assert.Equal(Math.Round(0.5 * 32768) / 32768, dither.Process(0, 0.5), 12);
    }

    [Fact]
    public void ChannelsAreShapedIndependently()
    {
        var dither = new Dither(DitherKind.NoiseShapedE, 16, channels: 2, Rate, autoBlank: false);

        // Driving one channel hard must not colour the other.
        for (int i = 0; i < 5_000; i++)
        {
            dither.Process(0, 0.8 * Math.Sin(i * 0.05));
            double quiet = dither.Process(1, 0);
            Assert.InRange(quiet, -0.01, 0.01);
        }
    }

    [Fact]
    public void ResetClearsTheShaperState()
    {
        var dither = new Dither(DitherKind.NoiseShapedE, 16, channels: 1, Rate, autoBlank: false);
        for (int i = 0; i < 1_000; i++) dither.Process(0, 0.5 * Math.Sin(i * 0.1));

        dither.Reset();
        Assert.InRange(dither.Process(0, 0), -4 * dither.Lsb, 4 * dither.Lsb);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public void AnImpossibleDepthIsRejected(int bits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Dither(DitherKind.FlatTpdf, bits, 1, Rate));
}


