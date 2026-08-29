using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class AzimuthTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 441_000;      // 10 s

    private static AzimuthOptions Options => AzimuthOptions.Default;

    /// <summary>Broadband programme — the material azimuth is actually measured on.</summary>
    private static float[] Programme(int seed = 5)
    {
        var signal = new float[Length + 4_096];
        var random = new Random(seed);
        double low = 0, high = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            double t = i / (double)Rate;
            double noise = random.NextDouble() - 0.5;
            low = low * 0.85 + noise * 0.15;
            high = high * 0.2 + noise * 0.8;
            signal[i] = (float)(0.22 * Math.Sin(2 * Math.PI * 220 * t)
                              + 0.14 * Math.Sin(2 * Math.PI * 1_320 * t + 0.7)
                              + 0.10 * Math.Sin(2 * Math.PI * 5_500 * t - 0.3)
                              + low * 0.25 + high * 0.12);
        }
        return signal;
    }

    /// <summary>A stereo pair from one source, with the right channel delayed by a known amount.</summary>
    private static (float[] Left, float[] Right) Pair(double delaySamples, int seed = 5,
        double decorrelation = 0)
    {
        float[] source = Programme(seed);
        var left = new float[Length];
        var right = new float[Length];
        var random = new Random(seed + 100);

        for (int i = 0; i < Length; i++)
        {
            left[i] = source[i + 2_048];
            right[i] = (float)Interpolation.At(source, i + 2_048 - delaySamples);
            if (decorrelation > 0)
            {
                left[i] += (float)((random.NextDouble() - 0.5) * decorrelation);
                right[i] += (float)((random.NextDouble() - 0.5) * decorrelation);
            }
        }
        return (left, right);
    }

    // ── measurement ──────────────────────────────────────────────

    /// <summary>
    /// The headline: a known delay is recovered to well under a sample. Whole-sample accuracy would
    /// be useless here — an azimuth error is typically a fraction of a sample at 44.1 kHz.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(-0.4)]
    [InlineData(1.5)]
    [InlineData(-3.0)]
    [InlineData(12.75)]
    public void AKnownDelayIsRecoveredToAFractionOfASample(double delay)
    {
        var (left, right) = Pair(delay);

        AzimuthEstimate estimate = Azimuth.Estimate(left, right, Rate, Options);

        output.WriteLine($"planted {delay,7:0.00}, measured {estimate.DelaySamples,7:0.000} " +
                         $"({estimate.Microseconds(Rate),7:0.0} µs) over {estimate.Windows} windows, " +
                         $"spread {estimate.SpreadSamples:0.000}, confidence {estimate.Confidence:0.00}");

        Assert.Equal(delay, estimate.DelaySamples, 0.05);
        Assert.True(estimate.Confidence > 0.8, $"confidence was only {estimate.Confidence:0.00}");
    }

    /// <summary>
    /// Whitening is the whole point of the phase transform. On bass-heavy material a plain
    /// correlation measures the part of the spectrum with the least timing information; this checks
    /// the estimate survives material that would defeat it.
    /// </summary>
    [Fact]
    public void ADelayIsFoundEvenWhenTheEnergyIsAllInTheBass()
    {
        var source = new float[Length + 4_096];
        var random = new Random(11);
        double state = 0;
        for (int i = 0; i < source.Length; i++)
        {
            double t = i / (double)Rate;
            state = state * 0.995 + (random.NextDouble() - 0.5) * 0.005;
            // 40 dB of tilt toward the bottom, plus a whisper of top.
            source[i] = (float)(state * 4 + 0.004 * Math.Sin(2 * Math.PI * 6_000 * t));
        }

        var left = new float[Length];
        var right = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            left[i] = source[i + 2_048];
            right[i] = (float)Interpolation.At(source, i + 2_048 - 0.6);
        }

        AzimuthEstimate estimate = Azimuth.Estimate(left, right, Rate, Options);
        output.WriteLine($"bass-heavy: measured {estimate.DelaySamples:0.000} samples, " +
                         $"confidence {estimate.Confidence:0.00}");
        Assert.Equal(0.6, estimate.DelaySamples, 0.15);
    }

    /// <summary>
    /// Reduced by the median, so a few windows that measure something else entirely cannot move the
    /// answer. A mean would be dragged by exactly the passages a real side is full of.
    /// </summary>
    [Fact]
    public void AFewWildWindowsDoNotMoveTheAnswer()
    {
        var (left, right) = Pair(0.75);

        // Three stretches where the right channel is unrelated to the left: a dropout, a hard-panned
        // passage, a splice.
        var random = new Random(3);
        foreach (int at in new[] { 60_000, 180_000, 300_000 })
        {
            for (int i = at; i < at + 20_000 && i < Length; i++)
                right[i] = (float)((random.NextDouble() - 0.5) * 0.5);
        }

        AzimuthEstimate estimate = Azimuth.Estimate(left, right, Rate, Options);
        output.WriteLine($"with three corrupt stretches: {estimate.DelaySamples:0.000} samples, " +
                         $"spread {estimate.SpreadSamples:0.000}");
        Assert.Equal(0.75, estimate.DelaySamples, 0.1);
    }

    [Fact]
    public void UncorrelatedChannelsAreReportedAsUncertain()
    {
        var random = new Random(17);
        var left = new float[Length];
        var right = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            left[i] = (float)((random.NextDouble() - 0.5) * 0.4);
            right[i] = (float)((random.NextDouble() - 0.5) * 0.4);
        }

        AzimuthEstimate estimate = Azimuth.Estimate(left, right, Rate, Options);
        output.WriteLine($"unrelated channels: {estimate.DelaySamples:0.00} samples, " +
                         $"spread {estimate.SpreadSamples:0.0}, confidence {estimate.Confidence:0.00}");
        Assert.True(estimate.Confidence < 0.5,
            $"unrelated channels reported {estimate.Confidence:0.00} confidence");
    }

    [Fact]
    public void SilenceIsMeasuredAsNothingRatherThanGuessed()
    {
        AzimuthEstimate estimate = Azimuth.Estimate(new float[Length], new float[Length], Rate, Options);

        Assert.Equal(0, estimate.Windows);
        Assert.Equal(0, estimate.DelaySamples);
        Assert.Equal(0, estimate.Confidence);
    }

    [Fact]
    public void MaterialShorterThanAWindowIsNotMeasured()
    {
        var (left, right) = Pair(1.0);
        AzimuthEstimate estimate = Azimuth.Estimate(
            left.AsSpan(0, 4_000).ToArray(), right.AsSpan(0, 4_000).ToArray(), Rate, Options);

        Assert.Equal(0, estimate.Windows);
    }

    [Fact]
    public void OneUsableWindowDoesNotClaimMatureConfidence()
    {
        var (left, right) = Pair(0.75);
        int size = Options.WindowSize;

        AzimuthEstimate estimate = Azimuth.Estimate(
            left.AsSpan(0, size).ToArray(), right.AsSpan(0, size).ToArray(), Rate, Options);

        Assert.Equal(1, estimate.Windows);
        Assert.InRange(estimate.Confidence, 0, 0.2);
    }

    [Fact]
    public void DefaultSearchStaysInsideAPlausibleStylusOffset()
    {
        Assert.Equal(0.5, Options.MaximumDelayMs);
    }

    // ── correction ───────────────────────────────────────────────

    /// <summary>
    /// Correcting has to remove the delay it measured. Re-running the estimator afterwards is the
    /// only check that the measurement and the correction share a sign convention — getting it the
    /// wrong way round doubles the error rather than removing it, and nothing else here would say so.
    /// </summary>
    /// <remarks>
    /// The bar is 0.08 samples, which at 44.1 kHz is 1.8 µs. The estimator's own residual bias is
    /// about 0.03 samples and the correction leaves around 0.05; a real azimuth error is tens to
    /// hundreds of microseconds, so what survives here is a fortieth of the smallest error worth
    /// correcting. Chasing it further buys precision nobody can hear.
    /// </remarks>
    [Theory]
    [InlineData(0.35)]
    [InlineData(-0.8)]
    [InlineData(2.4)]
    public void CorrectingRemovesTheDelayItMeasured(double delay)
    {
        var (left, right) = Pair(delay);
        AzimuthEstimate before = Azimuth.Estimate(left, right, Rate, Options);

        var channels = new[] { left, right };
        Azimuth.Align(channels, before.DelaySamples);

        AzimuthEstimate after = Azimuth.Estimate(channels[0], channels[1], Rate, Options);
        output.WriteLine($"planted {delay:0.00}: measured {before.DelaySamples:0.000}, " +
                         $"left over {after.DelaySamples:0.000} " +
                         $"({after.Microseconds(Rate):0.0} µs)");

        Assert.True(Math.Abs(after.DelaySamples) < 0.08,
            $"{after.DelaySamples:0.000} samples of delay survived the correction");
    }

    /// <summary>
    /// The correction is split between the channels, so the programme does not slide in time. A
    /// transfer may already be cut to length or carry markers.
    /// </summary>
    [Fact]
    public void CorrectingDoesNotSlideTheProgrammeInTime()
    {
        var (left, right) = Pair(4.0);
        float[] originalMid = new float[Length];
        for (int i = 0; i < Length; i++) originalMid[i] = (left[i] + right[i]) * 0.5f;

        var channels = new[] { left, right };
        Azimuth.Align(channels, 4.0);

        // The mid signal must stay put: correlate it against where it was.
        double best = 0;
        int bestLag = 0;
        for (int lag = -8; lag <= 8; lag++)
        {
            double sum = 0;
            for (int i = 20_000; i < 60_000; i++)
            {
                int j = i + lag;
                if ((uint)j >= (uint)Length) continue;
                sum += originalMid[i] * (channels[0][j] + channels[1][j]) * 0.5;
            }
            if (sum > best) { best = sum; bestLag = lag; }
        }

        output.WriteLine($"programme moved by {bestLag} samples");
        Assert.Equal(0, bestLag);
    }

    [Fact]
    public void AMonoDocumentIsLeftAlone()
    {
        float[] only = Programme();
        var channels = new[] { (float[])only.Clone() };

        Azimuth.Align(channels, 3.0);
        Assert.Equal(only, channels[0]);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var (left, right) = Pair(1.0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Azimuth.Estimate(left, right, Rate, Options, cancellation.Token));
    }

    // ── the parabolic refinement ─────────────────────────────────

    [Fact]
    public void TheParabolicFitFindsThePeakBetweenSamples()
    {
        // A parabola peaking a quarter of a sample to the right of the middle point.
        double Peak(double x) => -(x - 0.25) * (x - 0.25) + 4;
        double offset = Azimuth.ParabolicOffset(Peak(-1), Peak(0), Peak(1));

        output.WriteLine($"offset {offset:0.0000}, expected 0.25");
        Assert.Equal(0.25, offset, 6);
    }

    [Fact]
    public void AFlatOrDegenerateFitReturnsNoOffset()
    {
        Assert.Equal(0, Azimuth.ParabolicOffset(1, 1, 1));
        Assert.Equal(0, Azimuth.ParabolicOffset(0, 0, 0));
    }
}
