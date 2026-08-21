using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Pins the output of the offline spectral gate so that refactoring the overlap-add machinery
/// underneath it cannot quietly change how it sounds.
/// </summary>
/// <remarks>
/// <para>
/// The figures below were captured from the implementations as they stood before the shared
/// <see cref="Stft"/> framework replaced their three hand-rolled overlap-add loops. The tolerance is
/// 1e-6, which is about thirty times finer than a 16-bit LSB — tight enough that only floating-point
/// reassociation can pass, and far too tight for any change in windowing, frame layout or
/// normalization to slip through.
/// </para>
/// <para>
/// <b>Re-pinned once.</b> The pass looped to <c>NrFftSize / 2</c> where the processor is handed
/// <c>NrFftSize / 2 + 1</c> bins, so the Nyquist bin was passing through at unity gain while every
/// other bin was gated. Correcting the bound moved these numbers, and the size of the move is the
/// evidence that it was the only thing that changed: the learned profile is bit-identical, the RMS
/// figures move in the seventh decimal, and the largest probe delta is 8.5e-6 — one bin at 22.05 kHz.
/// Anything that moves them further than that is a real change in behaviour, not this.
/// </para>
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

        AssertMatches(data[0], 0.229499961, 0.510410964,
            [0.017721303, 0.001224617, -0.016192438, -0.010751238, 0.009722359, -0.084036380, 0.325935423, 0.028876577]);
        AssertMatches(data[1], 0.206558046, 0.459632069,
            [0.019493433, 0.001093167, -0.018105946, -0.012146194, 0.010408811, -0.075237699, 0.292047948, 0.025766347]);
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
        Restoration.ReduceNoise(simple, profile, 12.0, 3.0);

        Assert.Equal(source[0][0], simple[0][0], Tolerance);
    }
}
