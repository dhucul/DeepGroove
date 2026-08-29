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
/// <para>
/// <b>Re-pinned twice.</b> <see cref="Restoration.ScrubTonalPeaks"/> now runs at the end of
/// <see cref="Restoration.LearnNoiseProfile"/>: a 5-bin median that strips narrow spikes, so music
/// left in a passage learned as "noise" is not then gated away as noise. This signal is built to
/// contain exactly that — the profile is learned from samples 0–2800, the deliberately quiet head,
/// which still carries the 440 Hz and 1970 Hz tones at 2% — so it is the case the filter exists for,
/// and the move is large rather than numerical.
/// </para>
/// <para>
/// Three things say the median filter is the whole of it. <b>Only one of the three pinned profile
/// bins moved</b>: bin 10 fell 0.6547 → 0.4708, while bin 0 and bin 100 are unchanged to every digit
/// — and bin 0 is the one the new code documents as left alone, being DC. <b>The output RMS rose</b>,
/// 0.2295 → 0.2434, which is the direction the scrub is for: a lower profile means the gate calls
/// less of the signal noise and removes less of it. And <b>the probes move where the tones are</b>,
/// the largest at index 5000 (−0.0840 → −0.1256), not uniformly across the file the way a windowing
/// or normalization change would.
/// </para>
/// <para>
/// <b>Re-pinned three times.</b> Stereo reduction now derives one shared mask from both channels,
/// and the temporal gate opens quickly while closing slowly. This intentionally preserves image
/// position and stops the previous noise-floor mask from dulling the first frames of an attack.
/// The separate regression in <c>AlgorithmReviewRegressionTests</c> pins the stereo-ratio invariant;
/// these figures pin the resulting signal path.
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
        // Bin 0 is DC, which ScrubTonalPeaks leaves alone by design, so this one is unchanged from
        // the original pinning. Bin 10 is a narrow peak the median filter removes. Bin 100 sits on
        // the broadband hiss shelf, which is what the filter is chosen to preserve — a smoothing
        // filter would have blurred it, and that this figure did not move is how we know it did not.
        Assert.Equal(0.423183680, profile[0], Tolerance);
        Assert.Equal(0.470803560, profile[10], Tolerance);
        Assert.Equal(0.701537967, profile[100], Tolerance);
    }

    [Fact]
    public void SpectralGateOutputIsUnchanged()
    {
        float[][] source = Signal();
        float[] profile = Restoration.LearnNoiseProfile(source, 0, 2800);
        float[][] data = [(float[])source[0].Clone(), (float[])source[1].Clone()];

        Restoration.ReduceNoise(data, profile, 12.0, 3.0);

        AssertMatches(data[0], 0.247777649, 0.514013112,
            [0.017721303, 0.001224617, -0.020409824, -0.013289983, 0.016345974, -0.107935830, 0.330877960, 0.040992476]);
        AssertMatches(data[1], 0.223048479, 0.465499371,
            [0.019493433, 0.001093167, -0.022780024, -0.015105492, 0.017256634, -0.096524395, 0.296273470, 0.036765087]);
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
