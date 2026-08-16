using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class FftTests
{
    private static (double[] Re, double[] Im) Noise(int n, int seed)
    {
        var random = new Random(seed);
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = random.NextDouble() * 2 - 1;
            im[i] = random.NextDouble() * 2 - 1;
        }
        return (re, im);
    }

    /// <summary>Direct O(N²) transform in double precision — the ground truth for every size test.</summary>
    private static (double[] Re, double[] Im) ReferenceDft(double[] re, double[] im, bool inverse = false)
    {
        int n = re.Length;
        var outRe = new double[n];
        var outIm = new double[n];
        double sign = inverse ? 2.0 : -2.0;

        for (int k = 0; k < n; k++)
        {
            double sumRe = 0, sumIm = 0;
            for (int j = 0; j < n; j++)
            {
                (double sin, double cos) = Math.SinCos(sign * Math.PI * ((long)j * k % n) / n);
                sumRe += re[j] * cos - im[j] * sin;
                sumIm += re[j] * sin + im[j] * cos;
            }
            double scale = inverse ? 1.0 / n : 1.0;
            outRe[k] = sumRe * scale;
            outIm[k] = sumIm * scale;
        }
        return (outRe, outIm);
    }

    private static double MaxError(double[] aRe, double[] aIm, double[] bRe, double[] bIm)
    {
        double worst = 0, scale = 0;
        for (int i = 0; i < aRe.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(aRe[i] - bRe[i]));
            worst = Math.Max(worst, Math.Abs(aIm[i] - bIm[i]));
            scale = Math.Max(scale, Math.Abs(bRe[i]));
            scale = Math.Max(scale, Math.Abs(bIm[i]));
        }
        return worst / Math.Max(1e-30, scale);
    }

    // ── correctness against a direct transform ───────────────────

    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)] [InlineData(32)]
    [InlineData(64)] [InlineData(128)] [InlineData(256)] [InlineData(512)] [InlineData(1024)]
    public void ForwardMatchesADirectTransformAtEveryPowerOfTwo(int n)
    {
        var (re, im) = Noise(n, seed: n);
        var (expectedRe, expectedIm) = ReferenceDft(re, im);

        Fft.Forward(re, im);

        Assert.True(MaxError(re, im, expectedRe, expectedIm) < 1e-12);
    }

    /// <summary>Bluestein path: sizes with odd, prime and composite factors.</summary>
    [Theory]
    [InlineData(3)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(9)]
    [InlineData(10)] [InlineData(12)] [InlineData(15)] [InlineData(100)] [InlineData(384)]
    [InlineData(1000)]
    public void ForwardMatchesADirectTransformAtNonPowerOfTwoSizes(int n)
    {
        var (re, im) = Noise(n, seed: n * 7);
        var (expectedRe, expectedIm) = ReferenceDft(re, im);

        Fft.Forward(re, im);

        Assert.True(MaxError(re, im, expectedRe, expectedIm) < 1e-11);
    }

    [Theory]
    [InlineData(8)] [InlineData(64)] [InlineData(1024)] [InlineData(12)] [InlineData(100)]
    public void InverseUndoesForward(int n)
    {
        var (re, im) = Noise(n, seed: n + 1);
        var originalRe = (double[])re.Clone();
        var originalIm = (double[])im.Clone();

        Fft.Forward(re, im);
        Fft.Inverse(re, im);

        Assert.True(MaxError(re, im, originalRe, originalIm) < 1e-12);
    }

    /// <summary>
    /// The accuracy gate. A direct transform at this size is far too slow to use as a reference, but
    /// a single complex exponential has an exact answer: all the energy in one bin, magnitude N.
    /// This is precisely the case the old float-recurrence twiddles degraded, because the rotation
    /// error compounded across every one of the sixteen stages.
    /// </summary>
    [Fact]
    public void LargeTransformsStayAccurate()
    {
        const int n = 65536;
        const int tone = 9001;
        var re = new double[n];
        var im = new double[n];
        for (int j = 0; j < n; j++)
        {
            (double sin, double cos) = Math.SinCos(2.0 * Math.PI * ((long)tone * j % n) / n);
            re[j] = cos;
            im[j] = sin;
        }

        Fft.Forward(re, im);

        double peak = Math.Sqrt(re[tone] * re[tone] + im[tone] * im[tone]);
        Assert.True(Math.Abs(peak - n) / n < 1e-12, $"peak was {peak}, expected {n}");

        double leakage = 0;
        for (int k = 0; k < n; k++)
        {
            if (k == tone) continue;
            leakage = Math.Max(leakage, Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
        }
        Assert.True(leakage / n < 1e-12, $"leakage was {leakage / n:E3} of full scale");
    }

    [Fact]
    public void ImpulseTransformsToAFlatSpectrum()
    {
        var re = new double[64];
        var im = new double[64];
        re[0] = 1;

        Fft.Forward(re, im);

        for (int k = 0; k < 64; k++)
        {
            Assert.Equal(1.0, re[k], 12);
            Assert.Equal(0.0, im[k], 12);
        }
    }

    [Fact]
    public void ConstantTransformsToADcOnlySpectrum()
    {
        var re = new double[64];
        var im = new double[64];
        Array.Fill(re, 1.0);

        Fft.Forward(re, im);

        Assert.Equal(64.0, re[0], 10);
        for (int k = 1; k < 64; k++)
            Assert.True(Math.Sqrt(re[k] * re[k] + im[k] * im[k]) < 1e-11);
    }

    [Fact]
    public void EnergyIsConservedAcrossTheTransform()
    {
        var (re, im) = Noise(256, seed: 99);
        double before = 0;
        for (int i = 0; i < re.Length; i++) before += re[i] * re[i] + im[i] * im[i];

        Fft.Forward(re, im);

        double after = 0;
        for (int i = 0; i < re.Length; i++) after += re[i] * re[i] + im[i] * im[i];
        Assert.Equal(before, after / re.Length, 9);
    }

    // ── real-input transforms ────────────────────────────────────

    [Theory]
    [InlineData(8)] [InlineData(64)] [InlineData(256)] [InlineData(2048)]
    public void RealForwardMatchesTheComplexTransform(int n)
    {
        var random = new Random(n * 3);
        var samples = new float[n];
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = (float)(random.NextDouble() * 2 - 1);
            re[i] = samples[i];
        }

        Fft.Forward(re, im);

        var binRe = new float[n / 2 + 1];
        var binIm = new float[n / 2 + 1];
        Fft.RealForward(samples, binRe, binIm);

        // Compared against the spectrum's own scale: the bins come back as float, so the agreement
        // to look for is float precision, not a fixed number of decimal places.
        double peak = 0;
        for (int k = 0; k <= n / 2; k++)
            peak = Math.Max(peak, Math.Max(Math.Abs(re[k]), Math.Abs(im[k])));

        double worst = 0;
        for (int k = 0; k <= n / 2; k++)
        {
            worst = Math.Max(worst, Math.Abs(re[k] - binRe[k]));
            worst = Math.Max(worst, Math.Abs(im[k] - binIm[k]));
        }
        Assert.True(worst / peak < 1e-6, $"worst divergence {worst / peak:E3} of peak");
    }

    [Theory]
    [InlineData(8)] [InlineData(64)] [InlineData(1024)] [InlineData(4096)]
    public void RealInverseUndoesRealForward(int n)
    {
        var random = new Random(n * 11);
        var samples = new float[n];
        for (int i = 0; i < n; i++) samples[i] = (float)(random.NextDouble() * 2 - 1);
        var original = (float[])samples.Clone();

        var binRe = new float[n / 2 + 1];
        var binIm = new float[n / 2 + 1];
        Fft.RealForward(samples, binRe, binIm);
        Fft.RealInverse(binRe, binIm, samples);

        double worst = 0;
        for (int i = 0; i < n; i++) worst = Math.Max(worst, Math.Abs(original[i] - samples[i]));
        Assert.True(worst < 1e-6, $"round trip drifted by {worst:E3}");
    }

    [Fact]
    public void RealForwardPutsAToneInItsOwnBin()
    {
        const int n = 1024, bin = 64;
        var samples = new float[n];
        for (int i = 0; i < n; i++) samples[i] = (float)Math.Cos(2 * Math.PI * bin * i / n);

        var binRe = new float[n / 2 + 1];
        var binIm = new float[n / 2 + 1];
        Fft.RealForward(samples, binRe, binIm);

        double magnitude = Math.Sqrt(binRe[bin] * binRe[bin] + binIm[bin] * binIm[bin]);
        Assert.Equal(n / 2.0, magnitude, 1);
        for (int k = 0; k <= n / 2; k++)
        {
            if (Math.Abs(k - bin) <= 1) continue;
            Assert.True(Math.Sqrt(binRe[k] * binRe[k] + binIm[k] * binIm[k]) < 1e-2);
        }
    }

    // ── the float API the existing callers use ───────────────────

    /// <summary>
    /// The transform this replaced, kept verbatim as a reference. Ten call sites across the
    /// restoration, cleanup and analyzer code were tuned against its output, so the replacement has
    /// to agree with it to well within what any of them can perceive — the difference should be the
    /// old one's accumulated twiddle error and nothing else.
    /// </summary>
    private static void LegacyForward(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float ur = re[a], ui = im[a];
                    float vr = re[b] * cr - im[b] * ci;
                    float vi = re[b] * ci + im[b] * cr;
                    re[a] = ur + vr; im[a] = ui + vi;
                    re[b] = ur - vr; im[b] = ui - vi;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    [Theory]
    [InlineData(256)] [InlineData(1024)] [InlineData(2048)] [InlineData(4096)]
    public void FloatForwardAgreesWithTheTransformItReplaced(int n)
    {
        var random = new Random(n);
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++) re[i] = (float)(random.NextDouble() * 2 - 1);
        var legacyRe = (float[])re.Clone();
        var legacyIm = (float[])im.Clone();

        Fft.Forward(re, im);
        LegacyForward(legacyRe, legacyIm);

        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            worst = Math.Max(worst, Math.Abs(re[i] - legacyRe[i]));
            worst = Math.Max(worst, Math.Abs(im[i] - legacyIm[i]));
        }
        // Relative to a spectrum whose peak is on the order of sqrt(n) for noise input.
        Assert.True(worst / Math.Sqrt(n) < 1e-4, $"worst divergence {worst}");
    }

    [Fact]
    public void FloatInverseUndoesFloatForward()
    {
        const int n = 512;
        var random = new Random(5);
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++) re[i] = (float)(random.NextDouble() * 2 - 1);
        var original = (float[])re.Clone();

        Fft.Forward(re, im);
        Fft.Inverse(re, im);

        double worst = 0;
        for (int i = 0; i < n; i++) worst = Math.Max(worst, Math.Abs(original[i] - re[i]));
        Assert.True(worst < 1e-6, $"round trip drifted by {worst:E3}");
    }

    [Fact]
    public void MagnitudeDbReadsAFullScaleToneAtZeroDb()
    {
        const int n = 4096, bin = 100;
        var samples = new float[n];
        float[] window = Fft.HannWindow(n);
        for (int i = 0; i < n; i++) samples[i] = (float)Math.Sin(2 * Math.PI * bin * i / n);

        var magnitudeDb = new float[n / 2];
        Fft.MagnitudeDb(samples, window, magnitudeDb);

        Assert.Equal(0.0, magnitudeDb[bin], 1);
        Assert.True(magnitudeDb[bin / 2] < -60);
    }

    [Fact]
    public void HannWindowKeepsItsOriginalSymmetricDefinition()
    {
        // Deliberately unchanged: the restoration and analysis code is tuned around this exact shape.
        float[] window = Fft.HannWindow(8);

        Assert.Equal(0f, window[0], 6);
        Assert.Equal(0f, window[7], 6);
        for (int i = 0; i < 8; i++)
            Assert.Equal(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 7), window[i], 6);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(1023)]
    [InlineData(1024)] [InlineData(1025)]
    public void NextPowerOfTwoRoundsUp(int value)
    {
        int result = Fft.NextPowerOfTwo(value);
        Assert.True(result >= value);
        Assert.True((result & (result - 1)) == 0);
        Assert.True(result / 2 < value);
    }
}
