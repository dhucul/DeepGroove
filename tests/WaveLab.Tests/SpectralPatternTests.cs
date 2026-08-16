using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class SpectralPatternTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 220_500;      // 5 s
    private const int Fft = 2048, Hop = 512;

    private static SpectralPatternOptions Options => SpectralPatternOptions.Default;

    // ── the exponential integral ─────────────────────────────────

    /// <summary>
    /// Reference values for E₁, which the whole log-spectral gain rests on, to twelve significant
    /// figures. The abridged values in Abramowitz and Stegun's table 5.1 carry only seven, which is
    /// not enough to tell a correct implementation from one that is merely close.
    /// </summary>
    [Theory]
    [InlineData(0.01, 4.03792957653801)]
    [InlineData(0.1, 1.82292395841939)]
    [InlineData(0.5, 0.559773594776160)]
    [InlineData(1.0, 0.219383934395520)]
    [InlineData(2.0, 0.0489005107080611)]
    [InlineData(5.0, 0.00114829559127533)]
    [InlineData(10.0, 4.15696892968532e-6)]
    [InlineData(20.0, 9.83552529093482e-11)]
    public void TheExponentialIntegralMatchesItsReferenceValues(double x, double expected)
    {
        double actual = SpectralPattern.ExponentialIntegral(x);
        double relative = Math.Abs(actual - expected) / expected;

        output.WriteLine($"E1({x}) = {actual:0.############e+0}, expected {expected:0.############e+0}, " +
                         $"relative error {relative:0.0e+0}");
        Assert.True(relative < 1e-8, $"E1({x}) was out by {relative:0.0e+0}");
    }

    /// <summary>
    /// The series is used below one and a continued fraction above it, and they are computed by
    /// completely different routes — so agreeing at the join is a check on both of them at once.
    /// </summary>
    [Fact]
    public void TheExponentialIntegralIsContinuousAcrossItsTwoBranches()
    {
        double below = SpectralPattern.ExponentialIntegral(1 - 1e-9);
        double above = SpectralPattern.ExponentialIntegral(1 + 1e-9);

        output.WriteLine($"{below:0.############} against {above:0.############}, " +
                         $"differing by {Math.Abs(below - above) / below:0.0e+0}");
        Assert.Equal(below, above, 8);
    }

    [Fact]
    public void TheExponentialIntegralFallsMonotonically()
    {
        double previous = double.MaxValue;
        for (double x = 0.05; x < 30; x *= 1.3)
        {
            double value = SpectralPattern.ExponentialIntegral(x);
            Assert.True(value < previous, $"E1 rose at {x}");
            Assert.True(value >= 0, $"E1({x}) went negative");
            previous = value;
        }
    }

    // ── the gain rule ────────────────────────────────────────────

    /// <summary>
    /// At high SNR the log-spectral gain has to tend to the Wiener gain, because the correction term
    /// tends to one. That is the check that the exponential integral is wired in the right way up.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void AtHighSignalToNoiseTheGainApproachesWiener(double snr)
    {
        double gain = SpectralPattern.LogSpectralGain(snr, snr + 1, floorGain: 1e-3, oddsAbsent: 1);
        double wiener = snr / (1 + snr);

        output.WriteLine($"snr {snr}: gain {gain:0.00000}, Wiener {wiener:0.00000}");
        Assert.Equal(wiener, gain, 0.02);
    }

    [Fact]
    public void AGainIsNeverOutsideTheFloorAndUnity()
    {
        const double floor = 0.05;
        foreach (double prior in new[] { 1e-6, 1e-3, 0.1, 1, 10, 1e4, 1e8 })
        {
            foreach (double posterior in new[] { 0.0, 1e-3, 0.5, 1, 10, 1e6 })
            {
                double gain = SpectralPattern.LogSpectralGain(prior, posterior, floor, 1);
                Assert.InRange(gain, floor, 1.0);
            }
        }
    }

    [Fact]
    public void NonsenseInputsFallBackToTheFloorRatherThanPropagating()
    {
        const double floor = 0.02;
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -1.0, 0.0 })
        {
            Assert.Equal(floor, SpectralPattern.LogSpectralGain(bad, 4, floor, 1));
            Assert.Equal(floor, SpectralPattern.LogSpectralGain(4, bad, floor, 1), 12);
        }
    }

    /// <summary>The gain must rise with signal-to-noise; anything else inverts the whole point.</summary>
    [Fact]
    public void TheGainRisesWithSignalToNoise()
    {
        double previous = -1;
        for (double snr = 1e-4; snr < 1e5; snr *= 2)
        {
            double gain = SpectralPattern.LogSpectralGain(snr, snr + 1, 1e-4, 1);
            Assert.True(gain >= previous - 1e-9, $"the gain fell at an SNR of {snr}");
            previous = gain;
        }
    }

    /// <summary>
    /// Presence gating is the difference between OM-LSA and plain LSA: a bin that almost certainly
    /// holds nothing but the pattern is pushed to the floor rather than left hovering above it.
    /// </summary>
    [Fact]
    public void PresenceGatingPushesAnEmptyBinToTheFloor()
    {
        const double floor = 0.01;
        double ungated = SpectralPattern.LogSpectralGain(0.02, 1.0, floor, oddsAbsent: 1e-9);
        double gated = SpectralPattern.LogSpectralGain(0.02, 1.0, floor, oddsAbsent: 20);

        output.WriteLine($"ungated {ungated:0.0000}, gated {gated:0.0000}, floor {floor}");
        Assert.True(gated < ungated, "gating did not lower the gain of a bin holding only the pattern");
        Assert.True(gated < ungated * 0.75, "gating barely moved it");
    }

    // ── learning and removing ────────────────────────────────────

    /// <summary>
    /// Music, plus a steady buzz on the first six harmonics of 120 Hz.
    /// </summary>
    /// <remarks>
    /// The music sits above the comb rather than between its teeth. At 2048 points a bin is 21.5 Hz
    /// and a tone's main lobe spans about four of them, so a partial within roughly 40 Hz of a
    /// learned band cannot be separated from it by any gain rule — that is the transform's
    /// resolution, not the suppressor's doing, and
    /// <see cref="APartialWithinTheAnalysisResolutionOfTheCombCannotBeSpared"/> measures it on
    /// purpose. Mixing the two cases into one signal would let a genuine defect hide behind a limit.
    /// </remarks>
    private static (float[] Damaged, float[] Clean) Buzzing(double buzzLevel = 0.06,
        double extraMusicHz = 0)
    {
        var clean = new float[Length];
        var damaged = new float[Length];
        var random = new Random(21);
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)Rate;
            double music = 0.32 * Math.Sin(2 * Math.PI * 900 * t)
                         + 0.22 * Math.Sin(2 * Math.PI * 1_650 * t + 0.6)
                         + 0.14 * Math.Sin(2 * Math.PI * 2_400 * t - 0.4)
                         + (random.NextDouble() - 0.5) * 0.002;
            if (extraMusicHz > 0) music += 0.30 * Math.Sin(2 * Math.PI * extraMusicHz * t + 1.4);

            double buzz = 0;
            for (int n = 1; n <= 6; n++) buzz += buzzLevel / n * Math.Sin(2 * Math.PI * 120 * n * t + n);

            clean[i] = (float)music;
            damaged[i] = (float)(music + buzz);
        }
        return (damaged, clean);
    }

    /// <summary>The harmonic comb of the buzz, as the harmonic tool would select it.</summary>
    private static SpectralMask BuzzMask() =>
        SpectralMask.Harmonic(Fft / 2 + 1, Fft, Rate, frameFrom: 20, frameTo: 80,
            fundamentalHz: 120, partials: 6);

    private static double Level(float[] signal, double frequency, int from, int to)
    {
        // Goertzel over a span well inside the processed range.
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / Rate);
        double s1 = 0, s2 = 0;
        for (int i = from; i < to; i++)
        {
            double s = signal[i] + coefficient * s1 - s2;
            s2 = s1; s1 = s;
        }
        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coefficient * s1 * s2)) / (to - from);
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-15));

    [Fact]
    public void LearningCoversOnlyTheBinsTheSelectionTouched()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);

        Assert.False(pattern.IsEmpty);
        output.WriteLine($"{pattern.LearnedBins} of {pattern.Bins} bins learned, " +
                         $"band {pattern.Band.Low:0}-{pattern.Band.High:0} Hz");

        // A comb of six partials, not a slab: most of the spectrum must carry no signature at all.
        Assert.True(pattern.LearnedBins < pattern.Bins / 8,
            $"{pattern.LearnedBins} bins were learned from a six-partial comb");

        double perBin = Rate / (double)Fft;
        Assert.True(pattern.Power[(int)Math.Round(240 / perBin)] > 0, "the second partial was not learned");
        Assert.Equal(0, pattern.Power[(int)Math.Round(1_650 / perBin)]);   // the music above it
    }

    /// <summary>
    /// The headline: the buzz comes down and the music does not. Measured on partials of each,
    /// because an overall level would be dominated by whichever is louder.
    /// </summary>
    [Fact]
    public void RemovingTheLearnedPatternTakesTheBuzzAndLeavesTheMusic()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);

        float[] cleaned = pattern.Remove(damaged, 0, Length, Options);
        Assert.Equal(Length, cleaned.Length);

        const int from = 80_000, to = 140_000;
        double buzzBefore = Level(damaged, 240, from, to), buzzAfter = Level(cleaned, 240, from, to);
        double musicBefore = Level(damaged, 900, from, to), musicAfter = Level(cleaned, 900, from, to);

        output.WriteLine($"buzz  240 Hz: {Db(buzzAfter) - Db(buzzBefore):0.0} dB");
        output.WriteLine($"music 900 Hz: {Db(musicAfter) - Db(musicBefore):0.0} dB");

        Assert.True(Db(buzzBefore) - Db(buzzAfter) > 10, "the buzz was not removed");
        Assert.True(Math.Abs(Db(musicAfter) - Db(musicBefore)) < 1.5, "the music was disturbed");
    }

    /// <summary>
    /// A bin the selection never covered has no signature, so nothing may happen to it however long
    /// the removal runs. This is what makes the tool safe to run over a whole side.
    /// </summary>
    [Fact]
    public void BinsOutsideTheLearnedCombAreUntouched()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);
        float[] cleaned = pattern.Remove(damaged, 0, Length, Options);

        const int from = 80_000, to = 140_000;
        foreach (double frequency in new[] { 900.0, 1_650, 2_400 })
        {
            double change = Db(Level(cleaned, frequency, from, to)) - Db(Level(damaged, frequency, from, to));
            output.WriteLine($"{frequency} Hz changed by {change:+0.00;−0.00} dB");
            Assert.InRange(change, -1.5, 1.5);
        }
    }

    /// <summary>
    /// The honest limit, measured rather than left for someone to discover. At 2048 points a bin is
    /// 21.5 Hz and a tone's main lobe covers about four of them, so a musical partial sitting within
    /// roughly two bins of a learned band shares those bins with it and loses some of itself when
    /// they are turned down. No gain rule can separate them; only a longer transform can, and that
    /// trades away the time resolution the comb needs.
    /// </summary>
    [Fact]
    public void APartialWithinTheAnalysisResolutionOfTheCombCannotBeSpared()
    {
        // 330 Hz sits 30 Hz - well under two bins - from the buzz's third partial at 360 Hz.
        var (damaged, _) = Buzzing(extraMusicHz: 330);
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);
        float[] cleaned = pattern.Remove(damaged, 0, Length, Options);

        const int from = 80_000, to = 140_000;
        double crowded = Db(Level(cleaned, 330, from, to)) - Db(Level(damaged, 330, from, to));
        double clear = Db(Level(cleaned, 900, from, to)) - Db(Level(damaged, 900, from, to));

        output.WriteLine($"330 Hz, beside a learned partial: {crowded:+0.0;−0.0;0.0} dB");
        output.WriteLine($"900 Hz, clear of the comb:        {clear:+0.0;−0.0;0.0} dB");

        Assert.True(crowded < clear - 1,
            "a partial inside the analysis resolution of the comb was somehow spared");
        Assert.True(crowded > -12, $"it lost {crowded:0.0} dB, far more than the overlap explains");
    }

    [Fact]
    public void AStrongerReductionRemovesMoreOfThePattern()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);

        const int from = 80_000, to = 140_000;
        double before = Level(damaged, 240, from, to);
        double gentle = Level(pattern.Remove(damaged, 0, Length, Options with { ReductionDb = 6 }), 240, from, to);
        double firm = Level(pattern.Remove(damaged, 0, Length, Options with { ReductionDb = 30 }), 240, from, to);

        output.WriteLine($"6 dB setting: {Db(gentle) - Db(before):0.0} dB, " +
                         $"30 dB setting: {Db(firm) - Db(before):0.0} dB");
        Assert.True(firm < gentle, "the reduction setting did nothing");
        Assert.True(Db(before) - Db(gentle) <= 7.5, "a 6 dB setting removed more than 6 dB");
    }

    [Fact]
    public void RemovingOverASubRangeReturnsExactlyThatRange()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);

        float[] part = pattern.Remove(damaged, 60_000, 40_000, Options);

        Assert.Equal(40_000, part.Length);
        Assert.All(part, sample => Assert.True(float.IsFinite(sample)));

        // The run-up means the opening of the range is already settled rather than swelling in.
        double head = Level(part, 240, 200, 4_000);
        double body = Level(part, 240, 20_000, 24_000);
        output.WriteLine($"head {Db(head):0.0} dB, body {Db(body):0.0} dB");
        Assert.True(Math.Abs(Db(head) - Db(body)) < 9, "the start of the range had not settled");
    }

    // ── degenerate input ─────────────────────────────────────────

    [Fact]
    public void AnEmptyMaskLearnsNothingAndRemovesNothing()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(
            damaged, 0, SpectralMask.Rectangle(10, 10, 20, 40), Rate, Options);

        Assert.True(pattern.IsEmpty);
        Assert.Empty(pattern.Remove(damaged, 0, Length, Options));
    }

    [Fact]
    public void APatternFromAnotherGridIsRejected()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);

        Assert.Throws<ArgumentException>(() =>
            pattern.Remove(damaged, 0, Length, Options with { Hop = 256 }));
    }

    [Fact]
    public void ARangeRunningPastTheEndIsClamped()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);

        Assert.Equal(1_000, pattern.Remove(damaged, Length - 1_000, 50_000, Options).Length);
        Assert.Empty(pattern.Remove(damaged, Length, 1_000, Options));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            pattern.Remove(damaged, 0, Length, Options, cancellation.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        var (damaged, _) = Buzzing();
        SpectralPattern pattern = SpectralPattern.Learn(damaged, 0, BuzzMask(), Rate, Options);
        var reports = new List<double>();

        pattern.Remove(damaged, 0, Length, Options, CancellationToken.None, new DirectProgress(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
        Assert.All(reports, value => Assert.InRange(value, 0, 1));
    }

    private sealed class DirectProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
