using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class HumTrackerTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 441_000;      // 10 s

    private static HumTrackOptions Options => HumTrackOptions.Default;

    /// <summary>Programme deliberately clear of the hum's partials, except where a test wants otherwise.</summary>
    private static float[] Programme(int seed = 41)
    {
        var signal = new float[Length];
        var random = new Random(seed);
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)Rate;
            signal[i] = (float)(
                  0.26 * Math.Sin(2 * Math.PI * 233 * t)
                + 0.18 * Math.Sin(2 * Math.PI * 349 * t + 0.6)
                + 0.12 * Math.Sin(2 * Math.PI * 587 * t - 0.4)
                + (random.NextDouble() - 0.5) * 0.003);
        }
        return signal;
    }

    /// <summary>
    /// Hum with a fundamental that drifts, as mains does over minutes and as a mechanically coupled
    /// hum does with the turntable.
    /// </summary>
    private static float[] AddHum(float[] clean, double startHz, double endHz,
        double amplitude = 0.05, int harmonics = 6)
    {
        var damaged = (float[])clean.Clone();
        double phase = 0;
        for (int i = 0; i < Length; i++)
        {
            double fraction = i / (double)Length;
            double frequency = startHz + (endHz - startHz) * fraction;
            phase += 2 * Math.PI * frequency / Rate;

            double value = 0;
            for (int n = 1; n <= harmonics; n++) value += amplitude / n * Math.Sin(phase * n + n * 0.3);
            damaged[i] += (float)value;
        }
        return damaged;
    }

    private static double Level(float[] signal, double frequency, int from, int to)
    {
        double re = 0, im = 0, weight = 0;
        for (int i = from; i < to; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - from) / (double)(to - from));
            double omega = 2 * Math.PI * frequency * i / Rate;
            re += signal[i] * window * Math.Cos(omega);
            im -= signal[i] * window * Math.Sin(omega);
            weight += window;
        }
        return Math.Sqrt(re * re + im * im) / Math.Max(1, weight);
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-15));

    // ── tracking ─────────────────────────────────────────────────

    [Theory]
    [InlineData(50.0)]
    [InlineData(60.0)]
    [InlineData(49.8)]
    [InlineData(60.4)]
    public void TheFundamentalIsFoundWhereverItSits(double hz)
    {
        float[] damaged = AddHum(Programme(), hz, hz);
        HumReport report = HumTracker.Measure(damaged, Rate, Options);

        output.WriteLine($"planted {hz} Hz: measured {report.MeanHz:0.000} Hz, " +
                         $"drift {report.DriftHz:0.000} Hz, level {report.LevelDb:0.0} dB");
        Assert.True(report.Found);
        Assert.Equal(hz, report.MeanHz, 0.05);
    }

    /// <summary>
    /// The reason for tracking at all. A fixed notch a tenth of a hertz off leaves the hum audible
    /// while still digging its hole in the music.
    /// </summary>
    [Fact]
    public void TheTrackerFollowsADriftingFundamental()
    {
        float[] damaged = AddHum(Programme(), 49.7, 50.3);
        HumReport report = HumTracker.Measure(damaged, Rate, Options);

        output.WriteLine($"planted a drift from 49.7 to 50.3 Hz: " +
                         $"mean {report.MeanHz:0.000}, drift measured {report.DriftHz:0.000} Hz, " +
                         $"ending at {report.StartHz:0.000}");

        Assert.InRange(report.MeanHz, 49.7, 50.3);
        Assert.True(report.DriftHz > 0.2, $"the tracker only moved {report.DriftHz:0.000} Hz");
    }

    [Fact]
    public void MaterialWithNoHumIsReportedAsQuiet()
    {
        HumReport report = HumTracker.Measure(Programme(), Rate, Options);
        output.WriteLine($"clean material: {report.MeanHz:0.00} Hz at {report.LevelDb:0.0} dB");
        Assert.True(report.LevelDb < -40, $"clean material reported hum at {report.LevelDb:0.0} dB");
    }

    [Fact]
    public void StereoMeasurementDoesNotLetAQuietLeftChannelVetoRightChannelHum()
    {
        float[] right = AddHum(Programme(), 60.0, 60.0);

        HumReport report = HumTracker.Measure([new float[Length], right], Rate, Options);

        output.WriteLine($"right-channel hum measured at {report.MeanHz:0.000} Hz, " +
                         $"{report.LevelDb:0.0} dB against programme");
        Assert.True(report.Found);
        Assert.Equal(60.0, report.MeanHz, 0.05);
    }

    // ── removal ──────────────────────────────────────────────────

    /// <summary>The headline: the hum and its partials come off, at a steady frequency.</summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(60.0)]
    public void HumIsRemovedIncludingItsPartials(double hz)
    {
        float[] clean = Programme();
        float[] damaged = AddHum(clean, hz, hz);
        var working = (float[])damaged.Clone();

        HumReport report = HumTracker.Remove(working, Rate, Options);
        Assert.True(report.Found);

        const int from = 120_000, to = 320_000;
        foreach (int n in new[] { 1, 2, 3 })
        {
            double before = Level(damaged, hz * n, from, to);
            double after = Level(working, hz * n, from, to);
            output.WriteLine($"partial {n} at {hz * n:0.0} Hz: {Db(after) - Db(before):0.0} dB");
            Assert.True(Db(before) - Db(after) > 12,
                $"partial {n} only came down {Db(before) - Db(after):0.0} dB");
        }
    }

    /// <summary>
    /// The case tracking exists for. Measured as a residual against the clean signal rather than as
    /// a level at one frequency: the hum sweeps across whatever frequency a fixed measurement would
    /// pick, so it is already spread out before anything is removed and the reading means nothing.
    /// A first version of this measured at 50.0 Hz and read the sweep itself as a 9 dB reduction.
    /// </summary>
    /// <remarks>
    /// The expected gain is stated per drift rate rather than as one figure, because the falloff is
    /// real and worth recording. Mains drifts by about a tenth of a hertz over <em>minutes</em>; a
    /// hertz across ten seconds, as the last case does, is six hertz a minute and far beyond
    /// anything a transfer will show. Stating what each rate achieves records the limit instead of
    /// hiding it behind an average, and still fails if any of them regresses.
    /// </remarks>
    [Theory]
    [InlineData(50.0, 50.0, 40.0)]
    [InlineData(49.9, 50.1, 20.0)]
    [InlineData(49.7, 50.3, 12.0)]
    [InlineData(59.5, 60.5, 8.0)]
    public void ADriftingHumIsFollowedAndRemoved(double startHz, double endHz, double expectedGainDb)
    {
        float[] clean = Programme();
        float[] damaged = AddHum(clean, startHz, endHz);
        var working = (float[])damaged.Clone();

        HumReport report = HumTracker.Remove(working, Rate, Options);
        Assert.True(report.Found);

        double before = Residual(clean, damaged);
        double after = Residual(clean, working);
        output.WriteLine($"{startHz}→{endHz} Hz: {before:0.0} → {after:0.0} dB " +
                         $"(tracked {report.DriftHz:0.000} Hz of drift)");


        Assert.True(after > before + expectedGainDb,
            $"the drifting hum only came down {after - before:0.0} dB");

        static double Residual(float[] clean, float[] candidate)
        {
            double signal = 0, error = 0;
            for (int i = 40_000; i < Length - 40_000; i++)
            {
                double difference = clean[i] - candidate[i];
                signal += (double)clean[i] * clean[i];
                error += difference * difference;
            }
            return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
        }
    }

    /// <summary>
    /// Subtraction rather than notching: music at a frequency near the hum must survive, where a
    /// notch bank would take it out along with the hum.
    /// </summary>
    [Fact]
    public void MusicNearTheHumSurvives()
    {
        float[] clean = Programme();

        // A sustained note at 100 Hz, right on the second partial of a 50 Hz hum.
        for (int i = 0; i < Length; i++)
            clean[i] += (float)(0.15 * Math.Sin(2 * Math.PI * 100.4 * i / Rate + 1.1));

        float[] damaged = AddHum(clean, 50, 50);
        var working = (float[])damaged.Clone();
        HumTracker.Remove(working, Rate, Options);

        const int from = 120_000, to = 320_000;
        double noteBefore = Level(clean, 100.4, from, to);
        double noteAfter = Level(working, 100.4, from, to);
        double humAfter = Level(working, 100.0, from, to);
        double humBefore = Level(damaged, 100.0, from, to);

        output.WriteLine($"the note at 100.4 Hz moved {Db(noteAfter) - Db(noteBefore):+0.0;-0.0;0.0} dB");
        output.WriteLine($"the hum at 100.0 Hz moved {Db(humAfter) - Db(humBefore):+0.0;-0.0;0.0} dB");

        Assert.True(Math.Abs(Db(noteAfter) - Db(noteBefore)) < 3,
            "the music beside the hum was taken with it");
    }

    [Fact]
    public void RemovingFromMaterialWithNoHumBarelyChangesIt()
    {
        float[] clean = Programme();
        var working = (float[])clean.Clone();

        HumTracker.Remove(working, Rate, Options);

        double signal = 0, error = 0;
        for (int i = 20_000; i < Length - 20_000; i++)
        {
            double difference = clean[i] - working[i];
            signal += (double)clean[i] * clean[i];
            error += difference * difference;
        }
        double snr = 10 * Math.Log10(signal / Math.Max(error, 1e-30));

        output.WriteLine($"clean material after a removal pass: {snr:0.0} dB");
        Assert.True(snr > 30, $"clean material was disturbed down to {snr:0.0} dB");
    }

    // ── degenerate input ─────────────────────────────────────────

    [Fact]
    public void SilenceIsLeftAlone()
    {
        var silence = new float[Length];
        HumTracker.Remove(silence, Rate, Options);
        Assert.All(silence, sample => Assert.Equal(0f, sample, 6));
    }

    [Fact]
    public void MaterialShorterThanTwoBlocksIsNotProcessed()
    {
        var brief = new float[4_000];
        Assert.False(HumTracker.Measure(brief, Rate, Options).Found);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        float[] damaged = AddHum(Programme(), 50, 50);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            HumTracker.Remove(damaged, Rate, Options, cancellation.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        float[] damaged = AddHum(Programme(), 50, 50);
        var reports = new List<double>();

        HumTracker.Remove(damaged, Rate, Options, CancellationToken.None, new DirectProgress(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
    }

    private sealed class DirectProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
