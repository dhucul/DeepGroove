using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

/// <summary>Regression coverage for the algorithm review's cross-cutting failure modes.</summary>
public sealed class AlgorithmReviewRegressionTests
{
    private const int Rate = 44_100;

    [Fact]
    public void AntiPhaseStereoNoiseCannotDisappearFromTheLearnedProfile()
    {
        var left = new float[Restoration.NrFftSize * 2];
        var right = new float[left.Length];
        var random = new Random(17);
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = (float)((random.NextDouble() * 2 - 1) * 0.01);
            right[i] = -left[i];
        }

        float[] profile = Restoration.LearnNoiseProfile([left, right], 0, left.Length);

        Assert.True(profile.Average(value => value) > 1e-4,
            "anti-phase noise was cancelled before its spectrum was measured");
    }

    [Fact]
    public void StereoNoiseReductionUsesOneMaskForBothChannels()
    {
        const int length = 16_384;
        var left = new float[length];
        var right = new float[length];
        for (int i = 0; i < length; i++)
        {
            right[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 1000 * i / Rate));
            left[i] = right[i] * 0.1f;
        }
        var profile = Enumerable.Repeat(30f, Restoration.NrFftSize / 2).ToArray();

        Restoration.ReduceNoise([left, right], profile, reductionDb: 20, sensitivityDb: 5);

        double mismatch = 0, reference = 0;
        for (int i = 0; i < length; i++)
        {
            mismatch += Math.Pow(left[i] - right[i] * 0.1, 2);
            reference += Math.Pow(right[i] * 0.1, 2);
        }
        Assert.True(mismatch / Math.Max(reference, 1e-30) < 1e-10,
            "independent masks changed the stereo channel ratio");
    }

    [Fact]
    public void HumRemovalLeavesUnsupportedIntermediateHarmonicsAlone()
    {
        var signal = new float[Rate * 3];
        for (int i = 0; i < signal.Length; i++)
        {
            double t = i / (double)Rate;
            signal[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 50 * t)
                              + 0.2 * Math.Sin(2 * Math.PI * 100 * t)
                              + 0.2 * Math.Sin(2 * Math.PI * 150 * t));
        }
        double before100 = PowerAt(signal, 100);

        Restoration.RemoveHum([signal], Rate, 50, harmonics: 3,
            harmonicMask: 0b101, q: 35, strength: 1);

        Assert.True(Db(PowerAt(signal, 50)) < Db(before100) - 15);
        Assert.InRange(Db(PowerAt(signal, 100)) - Db(before100), -1.0, 1.0);
        Assert.True(Db(PowerAt(signal, 150)) < Db(before100) - 15);
    }

    [Fact]
    public void AutomaticClippingFindsMoreThanOneRailInAChannel()
    {
        var signal = new float[2_000];
        AddPlateau(signal, 300, 0.60f);
        AddPlateau(signal, 1_300, 0.90f);

        ClippingAnalysisResult result = Restoration.AnalyzeClipping([signal], Rate,
            new ClippingAnalysisOptions { MinimumConfidence = 0 });

        Assert.Contains(result.Events, item => Math.Abs(item.AbsoluteClipLevel - 0.60f) < 0.001f);
        Assert.Contains(result.Events, item => Math.Abs(item.AbsoluteClipLevel - 0.90f) < 0.001f);
    }

    [Fact]
    public void CdDeEmphasisHasUnityDcAndTheSpecifiedHighFrequencyShelf()
    {
        var dc = Enumerable.Repeat(0.25f, 20_000).ToArray();
        var nyquist = new float[dc.Length];
        for (int i = 0; i < nyquist.Length; i++) nyquist[i] = (i & 1) == 0 ? 0.25f : -0.25f;

        CdAudioService.ApplyCdDeEmphasis([dc, nyquist], Rate);

        Assert.All(dc, value => Assert.InRange(value, 0.24999f, 0.25001f));
        double highFrequencyGain = Rms(nyquist.AsSpan(10_000)) / 0.25;
        Assert.InRange(highFrequencyGain, 0.295, 0.305);
        Assert.True(new CdAudioTrack(1, 0, 75, CdTrackKind.Audio, 0x01).PreEmphasis);
    }

    private static void AddPlateau(float[] signal, int start, float level)
    {
        signal[start - 2] = level * 0.25f;
        signal[start - 1] = level * 0.65f;
        for (int i = start; i < start + 7; i++) signal[i] = level;
        signal[start + 7] = level * 0.65f;
        signal[start + 8] = level * 0.25f;
    }

    private static double PowerAt(float[] signal, double frequency)
    {
        int start = Rate;
        double real = 0, imaginary = 0;
        for (int i = start; i < signal.Length; i++)
        {
            double angle = 2 * Math.PI * frequency * i / Rate;
            real += signal[i] * Math.Cos(angle);
            imaginary -= signal[i] * Math.Sin(angle);
        }
        return real * real + imaginary * imaginary;
    }

    private static double Db(double power) => 10 * Math.Log10(Math.Max(power, 1e-30));

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float sample in samples) sum += sample * (double)sample;
        return Math.Sqrt(sum / samples.Length);
    }
}
