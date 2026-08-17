using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class CrossoverTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    /// <summary>Level of a band's response at a frequency, by running a tone through the splitter.</summary>
    private static double[] BandLevels(Crossover crossover, double frequency, int settle = 8_000,
        int measure = 8_000)
    {
        crossover.Reset();
        var bands = new float[crossover.Bands];
        var sums = new double[crossover.Bands];

        for (int i = 0; i < settle + measure; i++)
        {
            var input = (float)Math.Sin(2 * Math.PI * frequency * i / Rate);
            crossover.ProcessSample(0, input, bands);
            if (i < settle) continue;
            for (int b = 0; b < bands.Length; b++) sums[b] += (double)bands[b] * bands[b];
        }

        for (int b = 0; b < sums.Length; b++) sums[b] = Math.Sqrt(sums[b] / measure) * Math.Sqrt(2);
        return sums;
    }

    /// <summary>Level of the recombined bands at a frequency: the property that matters.</summary>
    private static double SumLevel(Crossover crossover, double frequency, int settle = 8_000,
        int measure = 8_000)
    {
        crossover.Reset();
        var bands = new float[crossover.Bands];
        double total = 0;

        for (int i = 0; i < settle + measure; i++)
        {
            var input = (float)Math.Sin(2 * Math.PI * frequency * i / Rate);
            crossover.ProcessSample(0, input, bands);
            if (i < settle) continue;

            double sum = 0;
            foreach (float band in bands) sum += band;
            total += sum * sum;
        }
        return Math.Sqrt(total / measure) * Math.Sqrt(2);
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-12));

    // ── the property that makes it usable ────────────────────────

    /// <summary>
    /// The whole point: the bands add back up to a flat magnitude. A splitter whose bands do not sum
    /// flat puts a hole or a bump at every crossing, and no amount of care in the processors between
    /// them can put it back.
    /// </summary>
    [Theory]
    [InlineData(2, CrossoverSlope.Lr24)]
    [InlineData(3, CrossoverSlope.Lr24)]
    [InlineData(4, CrossoverSlope.Lr24)]
    [InlineData(5, CrossoverSlope.Lr24)]
    [InlineData(3, CrossoverSlope.Lr48)]
    [InlineData(4, CrossoverSlope.Lr48)]
    public void TheBandsSumBackToAFlatResponse(int bands, CrossoverSlope slope)
    {
        double[] points = Frequencies(bands);
        var crossover = new Crossover(Rate, points, channels: 1, slope);

        double worst = 0, worstAt = 0;
        for (double f = 40; f < 18_000; f *= 1.08)
        {
            double error = Math.Abs(Db(SumLevel(crossover, f)));
            if (error > worst) { worst = error; worstAt = f; }
        }

        output.WriteLine($"{bands} bands, {slope}: worst deviation {worst:0.000} dB at {worstAt:0} Hz " +
                         $"(crossings at {string.Join(", ", points.Select(p => $"{p:0}"))} Hz)");
        Assert.True(worst < 0.15, $"the bands sum {worst:0.00} dB off flat at {worstAt:0} Hz");
    }

    /// <summary>
    /// Exactly at a crossing, each side must be 6 dB down — that is what makes a Linkwitz-Riley pair
    /// sum to unity where a Butterworth pair sums to +3.
    /// </summary>
    [Theory]
    [InlineData(CrossoverSlope.Lr24)]
    [InlineData(CrossoverSlope.Lr48)]
    public void EachHalfIsSixDecibelsDownAtTheCrossing(CrossoverSlope slope)
    {
        var crossover = new Crossover(Rate, [1_000.0], channels: 1, slope);
        double[] levels = BandLevels(crossover, 1_000);

        output.WriteLine($"{slope} at the crossing: low {Db(levels[0]):0.00} dB, high {Db(levels[1]):0.00} dB");
        Assert.Equal(-6.0, Db(levels[0]), 0.35);
        Assert.Equal(-6.0, Db(levels[1]), 0.35);
    }

    /// <summary>
    /// The correction that is easy to leave out. With three or more bands the low band skips the
    /// upper crossings, so without an all-pass to match them the sum has a hole at the top crossing.
    /// </summary>
    [Fact]
    public void ThreeBandsWouldNotSumFlatWithoutTheAllPassCorrection()
    {
        var crossover = new Crossover(Rate, [300.0, 3_000], channels: 1);

        // Right at the upper crossing is where the missing correction shows.
        double atUpper = Db(SumLevel(crossover, 3_000));
        double atLower = Db(SumLevel(crossover, 300));
        output.WriteLine($"three bands: {atLower:0.000} dB at 300 Hz, {atUpper:0.000} dB at 3 kHz");

        Assert.Equal(0, atUpper, 0.15);
        Assert.Equal(0, atLower, 0.15);
    }

    // ── separation ───────────────────────────────────────────────

    [Theory]
    [InlineData(CrossoverSlope.Lr24, 22)]
    [InlineData(CrossoverSlope.Lr48, 45)]
    public void EachBandRejectsWhatBelongsToTheOthers(CrossoverSlope slope, double expectedDb)
    {
        var crossover = new Crossover(Rate, [1_000.0], channels: 1, slope);

        // An octave either side of the crossing.
        double lowAtTop = Db(BandLevels(crossover, 2_000)[0]);
        double highAtBottom = Db(BandLevels(crossover, 500)[1]);

        output.WriteLine($"{slope}: low band {lowAtTop:0.0} dB at 2 kHz, " +
                         $"high band {highAtBottom:0.0} dB at 500 Hz");
        Assert.True(lowAtTop < -expectedDb, $"the low band leaks at {lowAtTop:0.0} dB an octave up");
        Assert.True(highAtBottom < -expectedDb, $"the high band leaks at {highAtBottom:0.0} dB an octave down");
    }

    [Fact]
    public void ABandCarriesWhatBelongsToIt()
    {
        var crossover = new Crossover(Rate, [200.0, 2_000], channels: 1);

        Assert.Equal(0, Db(BandLevels(crossover, 60)[0]), 0.6);
        Assert.Equal(0, Db(BandLevels(crossover, 700)[1]), 0.6);
        Assert.Equal(0, Db(BandLevels(crossover, 8_000)[2]), 0.6);
    }

    // ── housekeeping ─────────────────────────────────────────────

    [Fact]
    public void CrossingPointsAreSortedAndDeduplicated()
    {
        var crossover = new Crossover(Rate, [3_000.0, 300, 3_000, 1_000], channels: 1);

        Assert.Equal(3, crossover.Frequencies.Count);
        Assert.Equal(4, crossover.Bands);
        Assert.Equal([300.0, 1_000, 3_000], crossover.Frequencies);
    }

    [Fact]
    public void APointAboveNyquistIsBroughtInside()
    {
        var crossover = new Crossover(Rate, [40_000.0], channels: 1);
        Assert.True(crossover.Frequencies[0] < Rate / 2.0);
    }

    [Fact]
    public void WithNoCrossingsEverythingIsOneBand()
    {
        var crossover = new Crossover(Rate, [], channels: 1);
        Assert.Equal(1, crossover.Bands);

        var bands = new float[1];
        crossover.ProcessSample(0, 0.5f, bands);
        Assert.Equal(0.5f, bands[0], 6);
    }

    [Fact]
    public void ChannelsAreFilteredIndependently()
    {
        var crossover = new Crossover(Rate, [1_000.0], channels: 2);
        var left = new float[2];
        var right = new float[2];

        // Only the left channel is driven; the right must stay silent.
        for (int i = 0; i < 500; i++)
        {
            crossover.ProcessSample(0, (float)Math.Sin(2 * Math.PI * 300 * i / Rate), left);
            crossover.ProcessSample(1, 0f, right);
        }

        Assert.Equal(0f, right[0], 9);
        Assert.Equal(0f, right[1], 9);
        Assert.True(Math.Abs(left[0]) > 1e-3);
    }

    [Fact]
    public void ResetClearsTheFilterState()
    {
        var crossover = new Crossover(Rate, [1_000.0], channels: 1);
        var bands = new float[2];

        for (int i = 0; i < 200; i++) crossover.ProcessSample(0, 1f, bands);
        crossover.Reset();
        crossover.ProcessSample(0, 0f, bands);

        Assert.Equal(0f, bands[0], 9);
        Assert.Equal(0f, bands[1], 9);
    }

    [Fact]
    public void TooFewOutputsIsRejected()
    {
        var crossover = new Crossover(Rate, [1_000.0], channels: 1);
        Assert.Throws<ArgumentException>(() => crossover.ProcessSample(0, 0f, new float[1]));
    }

    private static double[] Frequencies(int bands) => bands switch
    {
        2 => [1_000.0],
        3 => [300.0, 3_000],
        4 => [150.0, 900, 5_000],
        _ => [120.0, 500, 2_000, 8_000],
    };
}
