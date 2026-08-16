using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class WindowsTests
{
    /// <summary>
    /// Peak sidelobe level in dB relative to the mainlobe, measured from a heavily zero-padded
    /// transform of the window itself. This is the number that characterises a window, so it is the
    /// honest way to check the coefficients are the ones claimed rather than merely plausible.
    /// </summary>
    private static double PeakSidelobeDb(float[] window)
    {
        int padded = Fft.NextPowerOfTwo(window.Length * 16);
        var re = new double[padded];
        var im = new double[padded];
        for (int i = 0; i < window.Length; i++) re[i] = window[i];

        Fft.Forward(re, im);

        var magnitude = new double[padded / 2];
        for (int k = 0; k < magnitude.Length; k++)
            magnitude[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);

        // Find the first true null. "Walk down until the response turns back up" is not enough: a
        // flat-top's mainlobe is deliberately rippled, so it contains local maxima at roughly 0 dB
        // that would be mistaken for sidelobes. A null has to be both a local minimum and far below
        // the peak.
        double peak = magnitude[0];
        int firstNull = 1;
        while (firstNull < magnitude.Length - 1 &&
               !(magnitude[firstNull] < magnitude[firstNull - 1] &&
                 magnitude[firstNull] <= magnitude[firstNull + 1] &&
                 magnitude[firstNull] < peak * 0.1))
            firstNull++;

        double worst = 0;
        for (int k = firstNull; k < magnitude.Length; k++) worst = Math.Max(worst, magnitude[k]);
        return 20 * Math.Log10(worst / peak);
    }

    [Theory]
    [InlineData(WindowKind.Hann, -34, -28)]
    [InlineData(WindowKind.Hamming, -46, -39)]
    [InlineData(WindowKind.Blackman, -61, -55)]
    [InlineData(WindowKind.BlackmanHarris, -96, -88)]
    [InlineData(WindowKind.Nuttall, -97, -89)]
    [InlineData(WindowKind.FlatTop, -95, -80)]
    public void WindowsHitTheirPublishedSidelobeLevels(WindowKind kind, double floorDb, double ceilingDb)
    {
        double sidelobe = PeakSidelobeDb(WindowFunctions.Create(kind, 512, periodic: false));
        Assert.InRange(sidelobe, floorDb, ceilingDb);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    public void DolphChebyshevPlacesEverySidelobeAtTheRequestedLevel(double attenuationDb)
    {
        double sidelobe = PeakSidelobeDb(WindowFunctions.DolphChebyshev(512, attenuationDb));

        // Every sidelobe sits at the same height by construction, so the measured peak should land
        // on the requested attenuation rather than merely below it.
        Assert.InRange(sidelobe, -attenuationDb - 6, -attenuationDb + 6);
    }

    /// <summary>
    /// These are the window's own peak sidelobe levels, which are not the same thing as the
    /// stopband attenuation the usual β = 0.1102(A - 8.7) design rule refers to — that rule describes
    /// the filter you get after windowing, and is roughly 20 dB more optimistic.
    /// </summary>
    [Theory]
    [InlineData(4.0, -34, -26)]
    [InlineData(8.6, -70, -60)]
    [InlineData(12.0, -95, -85)]
    public void KaiserBetaTradesMainlobeWidthForSidelobeLevel(double beta, double floorDb, double ceilingDb)
    {
        double sidelobe = PeakSidelobeDb(WindowFunctions.Kaiser(512, beta, periodic: false));
        Assert.InRange(sidelobe, floorDb, ceilingDb);
    }

    // ── the periodic/symmetric distinction ───────────────────────

    [Fact]
    public void SymmetricWindowsAreSymmetric()
    {
        float[] window = WindowFunctions.Hann(9, periodic: false);

        for (int i = 0; i < window.Length; i++)
            Assert.Equal(window[i], window[^(i + 1)], 6);
        Assert.Equal(0f, window[0], 6);
        Assert.Equal(1f, window[4], 6);
    }

    [Fact]
    public void PeriodicWindowsAreTheOnesThatTile()
    {
        // The periodic form starts at zero but does not end there — its last sample is the one that
        // would overlap the next frame's first, which is exactly why it sums flat and the symmetric
        // form does not.
        float[] periodic = WindowFunctions.Hann(8, periodic: true);
        Assert.Equal(0f, periodic[0], 6);
        Assert.NotEqual(0f, periodic[7], 3);

        Assert.True(WindowFunctions.SatisfiesCola(periodic, null, hop: 4, out double constant));
        Assert.Equal(1.0, constant, 6);
        Assert.False(WindowFunctions.SatisfiesCola(WindowFunctions.Hann(8, periodic: false), null, hop: 4, out _));
    }

    [Theory]
    [InlineData(1024, 512)]   // 50 %
    [InlineData(1024, 256)]   // 75 %
    [InlineData(1024, 128)]   // 87.5 %
    public void PeriodicHannSumsFlatAtEveryUsefulOverlap(int length, int hop)
    {
        float[] window = WindowFunctions.Hann(length, periodic: true);

        Assert.True(WindowFunctions.SatisfiesCola(window, null, hop, out double constant));
        Assert.Equal(length / (2.0 * hop), constant, 6);
    }

    /// <summary>
    /// The pair the STFT framework defaults to: half the window on the way in and half on the way
    /// out, which tapers the frame that gets modified while still reconstructing exactly.
    /// </summary>
    [Theory]
    [InlineData(1024, 256)]
    [InlineData(2048, 512)]
    [InlineData(512, 128)]
    public void SqrtHannPairsReconstructExactlyAtSeventyFivePercent(int length, int hop)
    {
        float[] root = WindowFunctions.Sqrt(WindowFunctions.Hann(length, periodic: true));

        Assert.True(WindowFunctions.SatisfiesCola(root, root, hop, out double constant));
        Assert.Equal(length / (2.0 * hop), constant, 6);
    }

    [Fact]
    public void OverlapSumReportsThePerPositionTotals()
    {
        float[] window = WindowFunctions.Hann(8, periodic: true);
        double[] sums = WindowFunctions.OverlapSum(window, null, hop: 4);

        Assert.Equal(4, sums.Length);
        foreach (double sum in sums) Assert.Equal(1.0, sum, 6);
    }

    [Fact]
    public void ColaFailsForAnOverlapTheWindowCannotTile()
    {
        // A hop equal to the window length is no overlap at all, and Hann tapers to zero.
        Assert.False(WindowFunctions.SatisfiesCola(WindowFunctions.Hann(64, periodic: true), null, hop: 64, out _));
    }

    // ── parameterised shapes ─────────────────────────────────────

    [Fact]
    public void TukeyDegeneratesToRectangularAndHannAtItsExtremes()
    {
        float[] rectangular = WindowFunctions.Tukey(64, 0.0);
        foreach (float value in rectangular) Assert.Equal(1f, value, 6);

        float[] hann = WindowFunctions.Hann(64, periodic: true);
        float[] tukeyFull = WindowFunctions.Tukey(64, 1.0);
        for (int i = 0; i < 64; i++) Assert.Equal(hann[i], tukeyFull[i], 5);
    }

    [Fact]
    public void TukeyKeepsAFlatCentreAndTaperedEdges()
    {
        float[] window = WindowFunctions.Tukey(100, 0.5, periodic: false);

        Assert.Equal(0f, window[0], 5);
        Assert.Equal(1f, window[50], 5);
        Assert.True(window[10] < window[25]);
    }

    [Fact]
    public void GaussianPeaksAtTheCentreAndDecaysMonotonically()
    {
        float[] window = WindowFunctions.Gaussian(101, 0.4, periodic: false);

        Assert.Equal(1f, window[50], 5);
        for (int i = 1; i <= 50; i++) Assert.True(window[i] >= window[i - 1]);
        for (int i = 51; i < 101; i++) Assert.True(window[i] <= window[i - 1]);
    }

    /// <summary>Flat-top exists to measure amplitude, so that is what it is checked on.</summary>
    [Fact]
    public void FlatTopReadsAnOffBinToneAtTheRightLevel()
    {
        const int n = 4096;
        float[] window = WindowFunctions.FlatTop(n);
        var samples = new float[n];

        // Deliberately half a bin off centre — the worst case for scalloping loss.
        double bin = 200.5;
        for (int i = 0; i < n; i++) samples[i] = (float)Math.Sin(2 * Math.PI * bin * i / n);

        var magnitudeDb = new float[n / 2];
        Fft.MagnitudeDb(samples, window, magnitudeDb);

        double peak = double.MinValue;
        for (int k = 190; k < 212; k++) peak = Math.Max(peak, magnitudeDb[k]);

        // A Hann window loses about 1.4 dB here; flat-top is built to lose under a hundredth of one.
        Assert.InRange(peak, -0.1, 0.1);
    }

    [Fact]
    public void RectangularIsAllOnes()
    {
        foreach (float value in WindowFunctions.Create(WindowKind.Rectangular, 16)) Assert.Equal(1f, value, 6);
    }

    [Theory]
    [InlineData(WindowKind.Hann)]
    [InlineData(WindowKind.Kaiser)]
    [InlineData(WindowKind.Gaussian)]
    [InlineData(WindowKind.Tukey)]
    [InlineData(WindowKind.DolphChebyshev)]
    [InlineData(WindowKind.FlatTop)]
    public void EveryWindowKindBuildsWithoutAnExplicitParameter(WindowKind kind)
    {
        float[] window = WindowFunctions.Create(kind, 256);

        Assert.Equal(256, window.Length);
        Assert.All(window, value => Assert.True(float.IsFinite(value)));
        Assert.True(Array.Exists(window, value => value > 0.5f), "window should reach a usable peak");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DegenerateLengthsAreHandled(int length)
    {
        Assert.Equal(length, WindowFunctions.Hann(length).Length);
        Assert.Equal(length, WindowFunctions.Kaiser(length, 8.6).Length);
        Assert.Equal(length, WindowFunctions.DolphChebyshev(length, 80).Length);
    }
}
