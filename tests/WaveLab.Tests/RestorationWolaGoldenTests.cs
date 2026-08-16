using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Pins the output of the two offline spectral-restoration passes so that refactoring the
/// overlap-add machinery underneath them cannot quietly change how they sound.
/// </summary>
/// <remarks>
/// The figures below were captured from the implementations as they stood before the shared
/// <see cref="Stft"/> framework replaced their three hand-rolled overlap-add loops. The tolerance is
/// 1e-6, which is about thirty times finer than a 16-bit LSB — tight enough that only floating-point
/// reassociation can pass, and far too tight for any change in windowing, frame layout or
/// normalization to slip through.
/// </remarks>
public sealed class RestorationWolaGoldenTests
{
    private const double Tolerance = 1e-6;
    private const int Length = 20_000;
    private static readonly int[] Probes = [0, 1, 511, 512, 2047, 5000, 12345, 19999];

    /// <summary>Tone plus deterministic hiss, with a quiet head a noise profile can be learned from.</summary>
    private static float[][] Signal()
    {
        var random = new Random(4242);
        var left = new float[Length];
        var right = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            double tone = 0.35 * Math.Sin(2 * Math.PI * 440 * i / 44100.0)
                        + 0.15 * Math.Sin(2 * Math.PI * 1970 * i / 44100.0);
            double hiss = (random.NextDouble() - 0.5) * 0.05;
            double envelope = i < 3000 ? 0.02 : 1.0;
            left[i] = (float)(tone * envelope + hiss);
            right[i] = (float)(tone * envelope * 0.9 + hiss * 1.1);
        }
        return [left, right];
    }

    private static void AssertMatches(float[] channel, double rms, double peak, double[] probes)
    {
        double sumSquares = 0, measuredPeak = 0;
        foreach (float value in channel)
        {
            sumSquares += (double)value * value;
            measuredPeak = Math.Max(measuredPeak, Math.Abs(value));
        }

        Assert.Equal(rms, Math.Sqrt(sumSquares / channel.Length), Tolerance);
        Assert.Equal(peak, measuredPeak, Tolerance);
        for (int i = 0; i < Probes.Length; i++)
            Assert.Equal(probes[i], channel[Probes[i]], Tolerance);
    }

    [Fact]
    public void NoiseProfileIsUnchanged()
    {
        float[] profile = Restoration.LearnNoiseProfile(Signal(), 0, 2800);

        Assert.Equal(1024, profile.Length);
        Assert.Equal(0.423183680, profile[0], Tolerance);
        Assert.Equal(0.654717803, profile[10], Tolerance);
        Assert.Equal(0.701537967, profile[100], Tolerance);
    }

    [Fact]
    public void SpectralGateOutputIsUnchanged()
    {
        float[][] source = Signal();
        float[] profile = Restoration.LearnNoiseProfile(source, 0, 2800);
        float[][] data = [(float[])source[0].Clone(), (float[])source[1].Clone()];

        Restoration.ReduceNoise(data, profile, 12.0, 3.0);

        AssertMatches(data[0], 0.229500270, 0.510382831,
            [0.017721303, 0.001224617, -0.016183918, -0.010759586, 0.009715128, -0.084052995, 0.325987458, 0.028860917]);
        AssertMatches(data[1], 0.206558544, 0.459600478,
            [0.019493433, 0.001093167, -0.018092612, -0.012159288, 0.010397199, -0.075259246, 0.292104095, 0.025749445]);
    }

    [Fact]
    public void EphraimMalahOutputIsUnchanged()
    {
        float[][] source = Signal();
        float[] profile = Restoration.LearnNoiseProfile(source, 0, 2800);
        float[][] data = [(float[])source[0].Clone(), (float[])source[1].Clone()];

        Restoration.ReduceNoiseAdvanced(data, profile, 12.0, 3.0);

        AssertMatches(data[0], 0.226708815, 0.502017915,
            [0.017721303, 0.001224617, -0.018343234, -0.012479023, 0.014126949, -0.086156577, 0.312515616, 0.025733428]);
        AssertMatches(data[1], 0.203592408, 0.453732550,
            [0.019493433, 0.001093167, -0.020225322, -0.013962405, 0.014699615, -0.076666936, 0.278798372, 0.022755167]);
    }

    /// <summary>
    /// The very first samples sit under a window value of zero, so no overlap weight accumulates
    /// there and the original audio is left in place rather than divided by nothing. Both passes must
    /// keep doing that.
    /// </summary>
    [Fact]
    public void SamplesWithNoOverlapWeightArePassedThrough()
    {
        float[][] source = Signal();
        float[] profile = Restoration.LearnNoiseProfile(source, 0, 2800);

        float[][] simple = [(float[])source[0].Clone(), (float[])source[1].Clone()];
        float[][] advanced = [(float[])source[0].Clone(), (float[])source[1].Clone()];
        Restoration.ReduceNoise(simple, profile, 12.0, 3.0);
        Restoration.ReduceNoiseAdvanced(advanced, profile, 12.0, 3.0);

        Assert.Equal(source[0][0], simple[0][0], Tolerance);
        Assert.Equal(source[0][0], advanced[0][0], Tolerance);
    }
}
