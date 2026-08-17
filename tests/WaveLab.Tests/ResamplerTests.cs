using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Sample-rate conversion, measured rather than eyeballed.
/// </summary>
/// <remarks>
/// A resampler that is merely "not obviously broken" passes a length check and a finiteness check
/// and still sounds wrong. What actually matters is three numbers: how much of a tone survives
/// unchanged, how much of it comes back as an alias somewhere it does not belong, and whether a
/// signal that goes out and comes back is the signal that left. Each has a test here.
/// </remarks>
public sealed class ResamplerTests(ITestOutputHelper output)
{
    private static float[] Tone(int length, double hz, int rate, double amplitude = 0.5)
    {
        var signal = new float[length];
        for (int i = 0; i < length; i++)
            signal[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return signal;
    }

    /// <summary>Power in a narrow band around a frequency, by direct correlation with that frequency.</summary>
    private static double PowerAt(float[] signal, double hz, int rate, int from, int count)
    {
        double re = 0, im = 0;
        for (int i = from; i < from + count && i < signal.Length; i++)
        {
            double phase = 2 * Math.PI * hz * i / rate;
            re += signal[i] * Math.Cos(phase);
            im -= signal[i] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(re * re + im * im) / count;
    }

    // ── shape and bookkeeping ────────────────────────────────────

    [Theory]
    [InlineData(44_100, 48_000)]
    [InlineData(48_000, 44_100)]
    [InlineData(44_100, 88_200)]
    [InlineData(96_000, 44_100)]
    [InlineData(44_100, 44_100)]
    public void TheOutputLengthFollowsTheRateRatio(int from, int to)
    {
        const int length = 44_100;
        float[][] result = Resampler.Resample([Tone(length, 1_000, from)], from, to);

        var expected = (int)Math.Round(length * (double)to / from);
        output.WriteLine($"{from} -> {to}: {result[0].Length} samples, expected about {expected}");

        Assert.Single(result);
        Assert.InRange(result[0].Length, expected - 2, expected + 2);
    }

    [Fact]
    public void ConvertingToTheSameRateReturnsTheSameSamples()
    {
        float[] signal = Tone(4_096, 997, 44_100);
        float[][] result = Resampler.Resample([signal], 44_100, 44_100);

        Assert.Equal(signal.Length, result[0].Length);
        for (int i = 0; i < signal.Length; i++) Assert.Equal(signal[i], result[0][i], 6);
    }

    [Fact]
    public void EveryChannelIsConvertedAndTheyStayInStep()
    {
        float[] left = Tone(20_000, 500, 44_100);
        float[] right = Tone(20_000, 500, 44_100, amplitude: 0.25);

        float[][] result = Resampler.Resample([left, right], 44_100, 48_000);

        Assert.Equal(2, result.Length);
        Assert.Equal(result[0].Length, result[1].Length);

        // The right channel is the left at half the level, and conversion must not disturb that.
        for (int i = 5_000; i < 15_000; i++)
            Assert.Equal(result[0][i] * 0.5f, result[1][i], 4);
    }

    [Fact]
    public void NoChannelsIsNoWork()
    {
        Assert.Empty(Resampler.Resample([], 44_100, 48_000));
    }

    [Theory]
    [InlineData(0, 48_000)]
    [InlineData(44_100, 0)]
    [InlineData(-1, 48_000)]
    public void AnImpossibleRateIsRejected(int from, int to)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Resampler.Resample([new float[16]], from, to));
    }

    // ── what it does to the signal ───────────────────────────────

    /// <summary>
    /// A tone well inside the passband must come out at the level it went in. This is the property a
    /// resampler exists to have, and the one a naive linear interpolator fails worst at the top of
    /// the band.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(10_000)]
    [InlineData(15_000)]
    public void ATonePassesThroughAtItsOwnLevel(double hz)
    {
        const int rate = 44_100, target = 48_000, length = rate;
        float[] input = Tone(length, hz, rate);
        float[] output_ = Resampler.Resample([input], rate, target)[0];

        // Measured away from the ends, where the kernel is still filling.
        double before = PowerAt(input, hz, rate, 4_000, 32_768);
        double after = PowerAt(output_, hz, target, 4_000, 32_768);
        double errorDb = 20 * Math.Log10(after / before);

        output.WriteLine($"{hz,6:0} Hz: {before:0.0000} -> {after:0.0000} ({errorDb:+0.000;-0.000} dB)");
        Assert.True(Math.Abs(errorDb) < 0.1, $"{hz} Hz moved by {errorDb:0.00} dB");
    }

    /// <summary>
    /// Converting down must not fold what will not fit back into the audible band. A tone above the
    /// new Nyquist has to disappear, not reappear somewhere else — which is exactly what an
    /// interpolator without a proper anti-alias filter does.
    /// </summary>
    [Fact]
    public void ContentAboveTheNewNyquistDoesNotFoldBack()
    {
        const int rate = 96_000, target = 44_100, length = 96_000;

        // 30 kHz fits at 96 kHz and cannot fit at 44.1 kHz. Its alias would land at 44.1 − 30 =
        // 14.1 kHz, in the middle of everything.
        float[] input = Tone(length, 30_000, rate);
        float[] output_ = Resampler.Resample([input], rate, target)[0];

        double alias = PowerAt(output_, 14_100, target, 2_000, 32_768);
        double reference = PowerAt(input, 30_000, rate, 2_000, 32_768);
        double rejectionDb = 20 * Math.Log10(Math.Max(1e-12, alias) / reference);

        output.WriteLine($"alias at 14.1 kHz is {rejectionDb:0.0} dB below the tone that caused it");
        Assert.True(rejectionDb < -80, $"the alias came back only {rejectionDb:0.0} dB down");
    }

    /// <summary>
    /// Out and back to the same rate must return what went in. Any real conversion loses the top of
    /// the band on the way down, so this is measured on programme that fits inside the narrower of
    /// the two.
    /// </summary>
    [Fact]
    public void AConversionThereAndBackReturnsTheSignal()
    {
        const int rate = 48_000, via = 44_100, length = 48_000;

        // Built from sinusoids rather than filtered noise, and every one of them well below the
        // narrower of the two Nyquists. A one-pole lowpass is not band-limiting in any useful sense
        // — at 6 dB an octave it still has real energy above 22 kHz, which the 44.1 kHz stage
        // correctly discards, and the measurement then reads a resampler doing its job as a fault.
        var random = new Random(4);
        var input = new float[length];
        for (int p = 0; p < 40; p++)
        {
            double hz = 40 + random.NextDouble() * 15_000;
            double phase = random.NextDouble() * 2 * Math.PI;
            for (int i = 0; i < length; i++)
                input[i] += (float)(0.08 * Math.Sin(2 * Math.PI * hz * i / rate + phase));
        }

        float[] there = Resampler.Resample([input], rate, via)[0];
        float[] back = Resampler.Resample([there], via, rate)[0];

        int count = Math.Min(input.Length, back.Length) - 8_000;

        double Snr(int lag)
        {
            double signal = 0, error = 0;
            for (int i = 4_000; i < 4_000 + count; i++)
            {
                signal += (double)input[i] * input[i];
                double d = back[i + lag] - input[i];
                error += d * d;
            }
            return 10 * Math.Log10(signal / error);
        }

        int bestLag = 0;
        double best = double.NegativeInfinity;
        for (int lag = -80; lag <= 80; lag++)
        {
            double snr = Snr(lag);
            if (snr > best) { best = snr; bestLag = lag; }
        }

        output.WriteLine($"48k -> 44.1k -> 48k round trip: {Snr(0):0.0} dB aligned as returned, "
                         + $"{best:0.0} dB at a lag of {bestLag} samples");

        // Zero, and it matters: the kernel is 64 taps, so a converter that did not account for its
        // own group delay would hand back audio a fraction of a millisecond late. That is inaudible
        // on its own and is not inaudible when the result is spliced against something that was not
        // converted, which is exactly what a montage and a CD transfer do.
        Assert.Equal(0, bestLag);
        Assert.True(best > 100, $"the round trip lost more than it should: {best:0.0} dB");
    }

    /// <summary>Silence in, silence out — and no impulse at the joins where the kernel starts.</summary>
    [Fact]
    public void SilenceStaysSilent()
    {
        float[][] result = Resampler.Resample([new float[10_000]], 44_100, 48_000);
        Assert.All(result[0], sample => Assert.Equal(0f, sample, 7));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Resampler.Resample([Tone(200_000, 1_000, 44_100)], 44_100, 48_000, cancelled.Token));
    }

    [Fact]
    public void ProgressRunsToCompletion()
    {
        var reports = new List<double>();
        Resampler.Resample([Tone(50_000, 1_000, 44_100)], 44_100, 48_000, CancellationToken.None,
            new Sink(reports));

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1], 6);
        Assert.All(reports, value => Assert.InRange(value, 0, 1));
    }

    private sealed class Sink(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
