using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class DecrackleTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 220_500;      // 5 s

    private static DecrackleOptions Options => DecrackleOptions.Default;

    /// <summary>Tonal programme with a light noise floor — predictable, as music is.</summary>
    private static float[] Programme(int seed = 13)
    {
        var signal = new float[Length];
        var random = new Random(seed);
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)Rate;
            signal[i] = (float)(
                  0.30 * Math.Sin(2 * Math.PI * 196 * t)
                + 0.20 * Math.Sin(2 * Math.PI * 294 * t + 0.5)
                + 0.14 * Math.Sin(2 * Math.PI * 587 * t - 0.9)
                + 0.08 * Math.Sin(2 * Math.PI * 1_175 * t + 1.3)
                + (random.NextDouble() - 0.5) * 0.004);
        }
        return signal;
    }

    /// <summary>
    /// Surface crackle: many very short defects at low level, which is what makes it different from
    /// the discrete clicks the de-click already finds.
    /// </summary>
    private static (float[] Damaged, int[] Positions) AddCrackle(float[] clean, int count = 400,
        double amplitude = 0.05, int seed = 29)
    {
        var damaged = (float[])clean.Clone();
        var random = new Random(seed);
        var positions = new int[count];

        for (int c = 0; c < count; c++)
        {
            int at = 2_000 + random.Next(Length - 6_000);
            positions[c] = at;
            int width = 1 + random.Next(3);
            double sign = random.NextDouble() < 0.5 ? -1 : 1;
            for (int i = 0; i < width; i++)
                damaged[at + i] += (float)(sign * amplitude * (0.6 + random.NextDouble() * 0.8));
        }
        Array.Sort(positions);
        return (damaged, positions);
    }

    private static double SnrDb(float[] clean, float[] candidate)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            double difference = clean[i] - candidate[i];
            signal += (double)clean[i] * clean[i];
            error += difference * difference;
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    // ── the headline ─────────────────────────────────────────────

    /// <summary>
    /// Crackle comes off and the music stays. Measured over the whole file, because crackle is
    /// spread across all of it — this is not a defect with a span to measure inside.
    /// </summary>
    [Theory]
    [InlineData(0.05, 8.0)]
    [InlineData(0.02, 5.0)]
    [InlineData(0.10, 12.0)]
    public void CrackleIsRemovedAndTheMusicSurvives(double amplitude, double expectedGainDb)
    {
        float[] clean = Programme();
        var (damaged, _) = AddCrackle(clean, amplitude: amplitude);

        var working = (float[])damaged.Clone();
        DecrackleReport report = Decrackle.Process(working, Options);

        double before = SnrDb(clean, damaged);
        double after = SnrDb(clean, working);
        output.WriteLine($"amplitude {amplitude}: {before:0.0} -> {after:0.0} dB " +
                         $"({report.Events} events, {report.SamplesReplaced} samples, " +
                         $"{report.Density(Rate, Length):0.0}/s, {report.Fallbacks} fallbacks)");

        Assert.True(after > before + expectedGainDb,
            $"de-crackling gained only {after - before:0.0} dB");
    }

    /// <summary>
    /// Clean music must come through essentially untouched. A de-crackler that fires on programme
    /// is worse than none: it takes the life out of everything it is run over.
    /// </summary>
    [Fact]
    public void CleanMusicIsLeftAlone()
    {
        float[] clean = Programme();
        var working = (float[])clean.Clone();

        DecrackleReport report = Decrackle.Process(working, Options);
        double snr = SnrDb(clean, working);

        output.WriteLine($"clean material: {report.Events} events " +
                         $"({report.Density(Rate, Length):0.0}/s), residual {snr:0.0} dB");
        Assert.True(snr > 45, $"clean music was disturbed down to {snr:0.0} dB");
    }

    /// <summary>
    /// The point of working on the prediction residual rather than the waveform. Crackle sits at or
    /// below the level of the music, so a detector looking at the waveform cannot separate them —
    /// but the music is predictable and the crackle is not.
    /// </summary>
    [Fact]
    public void CrackleBelowTheLevelOfTheMusicIsStillFound()
    {
        float[] clean = Programme();
        var (damaged, positions) = AddCrackle(clean, count: 200, amplitude: 0.03);

        double peak = 0;
        foreach (float sample in clean) peak = Math.Max(peak, Math.Abs(sample));
        output.WriteLine($"crackle at 0.03 against programme peaking at {peak:0.00} — " +
                         $"{20 * Math.Log10(0.03 / peak):0.0} dB below it");

        List<(int Start, int End)> found = Decrackle.Detect(damaged, Options);

        int hits = 0;
        foreach (int at in positions)
            if (found.Exists(span => at >= span.Start - 4 && at < span.End + 4)) hits++;

        output.WriteLine($"found {hits} of {positions.Length} ({hits / (double)positions.Length:P0}), " +
                         $"{found.Count} spans in total");
        Assert.True(hits > positions.Length * 0.6, $"only {hits} of {positions.Length} were found");
    }

    /// <summary>
    /// A transient is not a defect. A sustained departure from the model is a rimshot or the start
    /// of a note, and replacing it with what the model expected is what makes an aggressive
    /// de-crackler sound dull.
    /// </summary>
    [Fact]
    public void ATransientIsNotMistakenForCrackle()
    {
        float[] clean = Programme();

        // A struck note: fast attack, long decay. Nothing about it is a speck of dirt.
        const int strike = 100_000;
        for (int i = 0; i < 20_000 && strike + i < Length; i++)
        {
            double t = i / (double)Rate;
            clean[strike + i] += (float)(0.5 * Math.Exp(-i / 3_000.0) * Math.Sin(2 * Math.PI * 880 * t));
        }

        var working = (float[])clean.Clone();
        Decrackle.Process(working, Options);

        // Measure only across the attack, where a curvature-based detector would fire hardest.
        double signal = 0, error = 0;
        for (int i = strike - 200; i < strike + 2_000; i++)
        {
            double difference = clean[i] - working[i];
            signal += (double)clean[i] * clean[i];
            error += difference * difference;
        }
        double snr = 10 * Math.Log10(signal / Math.Max(error, 1e-30));

        output.WriteLine($"across the attack: {snr:0.0} dB");
        Assert.True(snr > 35, $"the transient was eaten, down to {snr:0.0} dB");
    }

    [Fact]
    public void ADenserThresholdFindsMore()
    {
        float[] clean = Programme();
        var (damaged, _) = AddCrackle(clean, amplitude: 0.02);

        int strict = Decrackle.Detect(damaged, Options with { Threshold = 6 }).Count;
        int loose = Decrackle.Detect(damaged, Options with { Threshold = 2.5 }).Count;

        output.WriteLine($"threshold 6 found {strict}, threshold 2.5 found {loose}");
        Assert.True(loose > strict, "the threshold did nothing");
    }

    // ── the robust scale ─────────────────────────────────────────

    /// <summary>
    /// The outliers must not inflate the scale used to find them, which is exactly what a standard
    /// deviation would let them do.
    /// </summary>
    [Fact]
    public void TheScaleIsNotDraggedByTheOutliersItIsUsedToFind()
    {
        var quiet = new double[1_000];
        var random = new Random(7);
        for (int i = 0; i < quiet.Length; i++) quiet[i] = Math.Abs(random.NextDouble() - 0.5) * 0.02;

        double before = Decrackle.RobustScale(quiet);
        for (int i = 0; i < 30; i++) quiet[i * 33] = 5.0;      // 3% wild outliers
        double after = Decrackle.RobustScale(quiet);

        output.WriteLine($"robust scale {before:0.00000} -> {after:0.00000} with 3% outliers at 5.0");
        Assert.Equal(before, after, 0.001);
    }

    // ── degenerate input ─────────────────────────────────────────

    [Fact]
    public void SilenceIsLeftAlone()
    {
        var silence = new float[Length];
        DecrackleReport report = Decrackle.Process(silence, Options);

        Assert.Equal(0, report.Events);
        Assert.All(silence, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void MaterialShorterThanABlockIsNotProcessed()
    {
        var brief = new float[500];
        Assert.Equal(0, Decrackle.Process(brief, Options).Events);
    }

    [Fact]
    public void AnEmptySignalIsANoOp()
    {
        Assert.Equal(DecrackleReport.None, Decrackle.Process([], Options));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        float[] clean = Programme();
        var (damaged, _) = AddCrackle(clean);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Decrackle.Process(damaged, Options, cancellation.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        float[] clean = Programme();
        var (damaged, _) = AddCrackle(clean);
        var reports = new List<double>();

        Decrackle.Process(damaged, Options, CancellationToken.None, new DirectProgress(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
        Assert.All(reports, value => Assert.InRange(value, 0, 1));
    }

    private sealed class DirectProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
