using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class InterpolationTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 8_192;

    private static float[] Tone(double frequency, double phase = 0)
    {
        var signal = new float[Length];
        for (int i = 0; i < Length; i++)
            signal[i] = (float)Math.Sin(2 * Math.PI * frequency * i / Rate + phase);
        return signal;
    }

    /// <summary>
    /// The measurement that says whether the interpolator is worth its cost: reading a tone between
    /// samples has to give what the tone actually does there.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(10_000)]
    [InlineData(15_000)]
    public void ReadingBetweenSamplesMatchesTheAnalyticSignal(double frequency)
    {
        float[] signal = Tone(frequency);
        double worst = 0;

        // Well inside the signal, so the kernel is never truncated by an edge.
        for (int i = 1_000; i < 2_000; i++)
        {
            for (double fraction = 0.05; fraction < 1; fraction += 0.05)
            {
                double read = Interpolation.At(signal, i + fraction);
                double exact = Math.Sin(2 * Math.PI * frequency * (i + fraction) / Rate);
                worst = Math.Max(worst, Math.Abs(read - exact));
            }
        }

        output.WriteLine($"{frequency,6} Hz: worst error {worst:0.0e+0} " +
                         $"({20 * Math.Log10(Math.Max(worst, 1e-12)):0.0} dB below full scale)");
        Assert.True(worst < 1e-4, $"the interpolator is out by {worst:0.0e+0} at {frequency} Hz");
    }

    /// <summary>
    /// The comparison that justifies the kernel. Linear interpolation is a comb whose response
    /// depends on the fraction, and on a drifting correction that is heard as the top breathing.
    /// </summary>
    [Fact]
    public void ItIsFarBetterThanLinearInterpolation()
    {
        float[] signal = Tone(12_000);
        double sinc = 0, linear = 0;

        for (int i = 1_000; i < 1_500; i++)
        {
            for (double fraction = 0.1; fraction < 1; fraction += 0.1)
            {
                double exact = Math.Sin(2 * Math.PI * 12_000 * (i + fraction) / Rate);
                sinc = Math.Max(sinc, Math.Abs(Interpolation.At(signal, i + fraction) - exact));
                double straight = signal[i] * (1 - fraction) + signal[i + 1] * fraction;
                linear = Math.Max(linear, Math.Abs(straight - exact));
            }
        }

        output.WriteLine($"at 12 kHz: windowed sinc {sinc:0.0e+0}, linear {linear:0.0e+0} " +
                         $"— a factor of {linear / sinc:0}");
        Assert.True(sinc < linear / 100, "the kernel should be orders of magnitude better than linear");
    }

    [Fact]
    public void ReadingExactlyOnASampleReturnsItUntouched()
    {
        float[] signal = Tone(3_000);
        for (int i = 100; i < 200; i++)
            Assert.Equal(signal[i], Interpolation.At(signal, i), 12);
    }

    /// <summary>A constant must read back as itself at every fraction, or the kernel has DC ripple.</summary>
    [Fact]
    public void AConstantReadsBackAsItselfAtEveryFraction()
    {
        var flat = new float[Length];
        Array.Fill(flat, 0.75f);

        double worst = 0;
        for (double fraction = 0; fraction < 1; fraction += 0.01)
            worst = Math.Max(worst, Math.Abs(Interpolation.At(flat, 2_000 + fraction) - 0.75));

        output.WriteLine($"worst deviation from a constant: {worst:0.0e+0}");
        Assert.True(worst < 1e-9, $"the kernel has {worst:0.0e+0} of DC ripple");
    }

    [Fact]
    public void ReadingOutsideTheSignalIsSilenceRatherThanASmear()
    {
        float[] signal = Tone(1_000);

        Assert.Equal(0, Interpolation.At(signal, -500.5), 9);
        Assert.Equal(0, Interpolation.At(signal, Length + 500.5), 9);
        Assert.Equal(0, Interpolation.At([], 0.5), 12);
    }

    // ── shifting ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0.25)]
    [InlineData(-0.25)]
    [InlineData(3.75)]
    public void ShiftingMovesTheSignalByExactlyThatMuch(double delay)
    {
        float[] signal = Tone(2_000);
        float[] shifted = Interpolation.Shift(signal, delay);

        double worst = 0;
        for (int i = 1_000; i < 2_000; i++)
        {
            double exact = Math.Sin(2 * Math.PI * 2_000 * (i - delay) / Rate);
            worst = Math.Max(worst, Math.Abs(shifted[i] - exact));
        }

        output.WriteLine($"delay {delay}: worst error {worst:0.0e+0}");
        Assert.True(worst < 1e-4, $"shifting by {delay} was out by {worst:0.0e+0}");
    }

    [Fact]
    public void ShiftingByNothingIsExactlyTheOriginal()
    {
        float[] signal = Tone(4_000);
        Assert.Equal(signal, Interpolation.Shift(signal, 0));
    }

    [Fact]
    public void ShiftingThereAndBackReturnsTheSignal()
    {
        float[] signal = Tone(6_000);
        float[] round = Interpolation.Shift(Interpolation.Shift(signal, 2.4), -2.4);

        double worst = 0;
        for (int i = 500; i < Length - 500; i++) worst = Math.Max(worst, Math.Abs(round[i] - signal[i]));

        output.WriteLine($"there and back: worst error {worst:0.0e+0}");
        Assert.True(worst < 1e-3, $"a round trip lost {worst:0.0e+0}");
    }

    // ── time-varying resampling ──────────────────────────────────

    /// <summary>
    /// The primitive wow-and-flutter correction is built on: reading along a position that drifts.
    /// A constant rate change has to come out as a clean pitch change with nothing added.
    /// </summary>
    /// <remarks>
    /// The two frequencies have to be far enough apart for the measurement to tell them apart, and
    /// the measurement window has to be tapered. A first version of this compared 1000 Hz against
    /// 1020 Hz over four thousand rectangular-windowed samples — under two bins apart — and read the
    /// leakage of the new tone as a residue of the old one.
    /// </remarks>
    [Fact]
    public void ResamplingAlongADriftingPositionChangesPitchCleanly()
    {
        float[] signal = Tone(1_000);
        const double ratio = 1.1;

        float[] stretched = Interpolation.Resample(signal, i => i * ratio);

        double atTarget = Level(stretched, 1_000 * ratio);
        double atSource = Level(stretched, 1_000);
        output.WriteLine($"resampled by {ratio}: the original frequency is " +
                         $"{20 * Math.Log10(atSource / atTarget):0.0} dB down");
        Assert.True(atSource < atTarget * 0.01, "the original frequency should be gone");

        // Hann-windowed, so the tone being measured against does not leak into the answer.
        static double Level(float[] s, double f)
        {
            const int from = 500, to = 7_000;
            double re = 0, im = 0;
            for (int i = from; i < to; i++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - from) / (double)(to - from));
                double omega = 2 * Math.PI * f * i / Rate;
                re += s[i] * window * Math.Cos(omega);
                im -= s[i] * window * Math.Sin(omega);
            }
            return Math.Sqrt(re * re + im * im);
        }
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var signal = new float[2_000_000];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Interpolation.Resample(signal, i => i, Interpolation.DefaultHalfTaps, cancellation.Token));
    }
}
