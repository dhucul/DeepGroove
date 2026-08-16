using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class JanssenTests(ITestOutputHelper output)
{
    private const int SampleRate = 44_100;

    private static float[] Harmonic(int length, params (double Frequency, double Amplitude)[] partials)
    {
        var signal = new float[length];
        for (int i = 0; i < length; i++)
        {
            double value = 0;
            foreach (var (frequency, amplitude) in partials)
                value += amplitude * Math.Sin(2 * Math.PI * frequency * i / SampleRate);
            signal[i] = (float)value;
        }
        return signal;
    }

    /// <summary>Signal-to-error ratio over the repaired span, in dB. Higher is better.</summary>
    private static double ReconstructionSnrDb(float[] truth, double[] estimate, int gapStart)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < estimate.Length; i++)
        {
            double reference = truth[gapStart + i];
            double difference = reference - estimate[i];
            signal += reference * reference;
            error += difference * difference;
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    /// <summary>The obvious baseline: a straight line across the hole.</summary>
    private static double[] LinearBridge(float[] samples, int gapStart, int gapEnd)
    {
        int gap = gapEnd - gapStart;
        var bridge = new double[gap];
        double left = samples[gapStart - 1], right = samples[gapEnd];
        for (int i = 0; i < gap; i++) bridge[i] = left + (right - left) * (i + 1.0) / (gap + 1.0);
        return bridge;
    }

    private static float[] WithGap(float[] source, int gapStart, int gapEnd)
    {
        var damaged = (float[])source.Clone();
        for (int i = gapStart; i < gapEnd; i++) damaged[i] = 0;
        return damaged;
    }

    // ── reconstruction quality ───────────────────────────────────

    /// <summary>
    /// A single undamped sinusoid is, counter-intuitively, the least favourable case: its poles sit
    /// exactly on the unit circle, where the stability clamp on the reflection coefficients bites,
    /// and the default order is far more model than it needs. It still clears 30 dB — a 3% error on
    /// a signal real audio never actually consists of.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(220)]   // 5 ms — the longest defect the pop repairer treats
    public void ASustainedToneIsReconstructedWell(int gapLength)
    {
        float[] truth = Harmonic(20_000, (440, 0.5));
        int gapStart = 9_000;
        float[] damaged = WithGap(truth, gapStart, gapStart + gapLength);

        Assert.True(Janssen.TryInterpolate(damaged, gapStart, gapStart + gapLength,
            JanssenOptions.For(gapLength, 1.0), out double[] repaired));

        double snr = ReconstructionSnrDb(truth, repaired, gapStart);
        output.WriteLine($"sine gap {gapLength}: {snr:0.0} dB");
        Assert.True(snr > 25, $"reconstruction SNR was only {snr:0.0} dB");
    }

    /// <summary>
    /// The case that matters, and the one the defaults are tuned for: several partials moving across
    /// a gap long enough that nothing local can bridge it.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(220)]
    public void HarmonicallyRichAudioIsReconstructedAtEveryGapLength(int gapLength)
    {
        float[] truth = Harmonic(20_000,
            (220, 0.4), (440, 0.25), (660, 0.15), (1320, 0.08), (2640, 0.04));
        const int gapStart = 9_000;
        float[] damaged = WithGap(truth, gapStart, gapStart + gapLength);

        Assert.True(Janssen.TryInterpolate(damaged, gapStart, gapStart + gapLength,
            JanssenOptions.For(gapLength, 1.0), out double[] repaired));

        double snr = ReconstructionSnrDb(truth, repaired, gapStart);
        double linear = ReconstructionSnrDb(truth,
            LinearBridge(damaged, gapStart, gapStart + gapLength), gapStart);
        output.WriteLine($"rich gap {gapLength}: Janssen {snr:0.0} dB vs linear {linear:0.0} dB");
        // Bounded well below the measured figures on purpose: the exact number moves with how many
        // whole periods land in the analysis window, so the assertion is on the property that
        // matters — a large, unambiguous margin over the obvious alternative.
        Assert.True(snr > 25, $"reconstruction SNR was only {snr:0.0} dB");
        Assert.True(snr > linear + 18, $"only {snr - linear:0.0} dB better than a straight line");
    }

    /// <summary>
    /// The case the method exists for: several partials moving at once, where extrapolating from one
    /// side or bridging with a curve loses the upper harmonics.
    /// </summary>
    [Fact]
    public void HarmonicallyRichAudioBeatsALinearBridgeByAWideMargin()
    {
        float[] truth = Harmonic(20_000,
            (220, 0.4), (440, 0.25), (660, 0.15), (1320, 0.08), (2640, 0.04));
        const int gapStart = 9_000, gapLength = 48;
        float[] damaged = WithGap(truth, gapStart, gapStart + gapLength);

        Assert.True(Janssen.TryInterpolate(damaged, gapStart, gapStart + gapLength,
            JanssenOptions.For(gapLength, 1.0), out double[] repaired));

        double janssen = ReconstructionSnrDb(truth, repaired, gapStart);
        double linear = ReconstructionSnrDb(truth, LinearBridge(damaged, gapStart, gapStart + gapLength), gapStart);
        output.WriteLine($"Janssen {janssen:0.0} dB vs linear {linear:0.0} dB");

        Assert.True(janssen > 25, $"Janssen managed only {janssen:0.0} dB");
        Assert.True(janssen > linear + 20, $"only {janssen - linear:0.0} dB better than a straight line");
    }

    [Fact]
    public void AmplitudeModulatedMaterialKeepsItsEnvelope()
    {
        var truth = new float[20_000];
        for (int i = 0; i < truth.Length; i++)
        {
            double envelope = 0.5 * (1 + 0.8 * Math.Sin(2 * Math.PI * 3 * i / SampleRate));
            truth[i] = (float)(envelope * Math.Sin(2 * Math.PI * 500 * i / SampleRate));
        }
        const int gapStart = 9_000, gapLength = 40;
        float[] damaged = WithGap(truth, gapStart, gapStart + gapLength);

        Assert.True(Janssen.TryInterpolate(damaged, gapStart, gapStart + gapLength,
            JanssenOptions.For(gapLength, 1.0), out double[] repaired));

        double snr = ReconstructionSnrDb(truth, repaired, gapStart);
        output.WriteLine($"modulated: {snr:0.0} dB");
        Assert.True(snr > 20, $"reconstruction SNR was only {snr:0.0} dB");
    }

    [Fact]
    public void TheRepairJoinsTheAudioEitherSideWithoutAStep()
    {
        float[] truth = Harmonic(20_000, (330, 0.5), (990, 0.2));
        const int gapStart = 9_000, gapLength = 32;
        float[] damaged = WithGap(truth, gapStart, gapStart + gapLength);

        Assert.True(Janssen.TryInterpolate(damaged, gapStart, gapStart + gapLength,
            JanssenOptions.For(gapLength, 1.0), out double[] repaired));

        // A discontinuity at either boundary is what a click sounds like, so the step across each
        // join must be no larger than the steps the signal takes on its own.
        double typicalStep = 0;
        for (int i = gapStart - 200; i < gapStart - 1; i++)
            typicalStep = Math.Max(typicalStep, Math.Abs(truth[i + 1] - truth[i]));

        double leadingStep = Math.Abs(repaired[0] - damaged[gapStart - 1]);
        double trailingStep = Math.Abs(damaged[gapStart + gapLength] - repaired[^1]);

        Assert.True(leadingStep < typicalStep * 1.5, $"step into the repair was {leadingStep:0.0000}");
        Assert.True(trailingStep < typicalStep * 1.5, $"step out of the repair was {trailingStep:0.0000}");
    }

    /// <summary>
    /// Closer to real material than the pure tones above: harmonics over a noise floor. The added
    /// noise breaks the exact periodicity that makes those measurements swing, so this is the most
    /// trustworthy of the quality figures even though its ceiling is lower.
    /// </summary>
    [Theory]
    [InlineData(24)]
    [InlineData(96)]
    public void HarmonicsOverANoiseFloorAreStillReconstructed(int gapLength)
    {
        var random = new Random(7);
        float[] truth = Harmonic(20_000, (196, 0.35), (392, 0.22), (588, 0.12), (784, 0.07));
        for (int i = 0; i < truth.Length; i++) truth[i] += (float)((random.NextDouble() - 0.5) * 0.01);

        const int gapStart = 9_000;
        float[] damaged = WithGap(truth, gapStart, gapStart + gapLength);

        Assert.True(Janssen.TryInterpolate(damaged, gapStart, gapStart + gapLength,
            JanssenOptions.For(gapLength, 1.0), out double[] repaired));

        double snr = ReconstructionSnrDb(truth, repaired, gapStart);
        double linear = ReconstructionSnrDb(truth,
            LinearBridge(damaged, gapStart, gapStart + gapLength), gapStart);
        output.WriteLine($"noisy gap {gapLength}: Janssen {snr:0.0} dB vs linear {linear:0.0} dB");

        Assert.True(snr > 20, $"reconstruction SNR was only {snr:0.0} dB");
        Assert.True(snr > linear + 15, $"only {snr - linear:0.0} dB better than a straight line");
    }

    // ── robustness ───────────────────────────────────────────────

    [Fact]
    public void NoiseIsNotAmplified()
    {
        var random = new Random(11);
        var damaged = new float[20_000];
        for (int i = 0; i < damaged.Length; i++) damaged[i] = (float)(random.NextDouble() * 2 - 1) * 0.3f;
        const int gapStart = 9_000, gapLength = 40;
        for (int i = gapStart; i < gapStart + gapLength; i++) damaged[i] = 0;

        // Noise is not predictable, so the answer will be poor — but it must stay bounded and finite
        // rather than ringing away on an unstable model.
        if (Janssen.TryInterpolate(damaged, gapStart, gapStart + gapLength,
                JanssenOptions.For(gapLength, 0.3), out double[] repaired))
        {
            Assert.All(repaired, value => Assert.True(double.IsFinite(value)));
            Assert.All(repaired, value => Assert.True(Math.Abs(value) < 1.5));
        }
    }

    [Fact]
    public void SilenceIsFilledWithSilence()
    {
        var damaged = new float[8_000];

        // Silence may or may not support a model fit; either answer is legitimate. What is not
        // legitimate is anything audible coming out of it.
        Janssen.TryInterpolate(damaged, 4_000, 4_040, JanssenOptions.For(40, 1.0), out double[] repaired);

        foreach (double value in repaired) Assert.True(Math.Abs(value) < 1e-6);
    }

    [Theory]
    [InlineData(0, 40)]          // gap touching the start: no left context
    [InlineData(7_990, 8_000)]   // gap touching the end: no right context
    [InlineData(4_000, 4_000)]   // empty gap
    [InlineData(4_000, 3_990)]   // inverted
    public void ImpossibleGeometryIsDeclinedRatherThanGuessed(int gapStart, int gapEnd)
    {
        float[] damaged = Harmonic(8_000, (440, 0.5));

        Assert.False(Janssen.TryInterpolate(damaged, gapStart, gapEnd,
            JanssenOptions.For(Math.Max(1, gapEnd - gapStart), 1.0), out _));
    }

    [Fact]
    public void GapsBeyondTheSupportedLengthAreDeclined()
    {
        float[] damaged = Harmonic(40_000, (440, 0.5));

        Assert.False(Janssen.TryInterpolate(damaged, 10_000, 10_000 + Janssen.MaximumGapLength + 1,
            JanssenOptions.For(64, 1.0), out _));
    }

    [Fact]
    public void NonFiniteInputIsDeclined()
    {
        float[] damaged = Harmonic(20_000, (440, 0.5));
        damaged[8_900] = float.NaN;   // inside the analysis window for a gap at 9 000

        Assert.False(Janssen.TryInterpolate(damaged, 9_000, 9_040,
            JanssenOptions.For(40, 1.0), out _));
    }

    [Fact]
    public void TheSameInputAlwaysGivesTheSameAnswer()
    {
        float[] truth = Harmonic(20_000, (440, 0.4), (880, 0.2));
        const int gapStart = 9_000, gapLength = 48;

        Janssen.TryInterpolate(WithGap(truth, gapStart, gapStart + gapLength), gapStart,
            gapStart + gapLength, JanssenOptions.For(gapLength, 1.0), out double[] first);
        Janssen.TryInterpolate(WithGap(truth, gapStart, gapStart + gapLength), gapStart,
            gapStart + gapLength, JanssenOptions.For(gapLength, 1.0), out double[] second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheInputBufferIsLeftAlone()
    {
        float[] truth = Harmonic(20_000, (440, 0.5));
        float[] damaged = WithGap(truth, 9_000, 9_040);
        var before = (float[])damaged.Clone();

        Janssen.TryInterpolate(damaged, 9_000, 9_040, JanssenOptions.For(40, 1.0), out _);

        Assert.Equal(before, damaged);
    }

    [Fact]
    public void DefaultsScaleTheModelToTheGap()
    {
        JanssenOptions small = JanssenOptions.For(10, 1.0);
        JanssenOptions large = JanssenOptions.For(400, 1.0);

        Assert.True(large.Order > small.Order);
        Assert.True(large.ContextLength >= small.ContextLength);
        Assert.InRange(small.Order, 48, 256);
        Assert.InRange(large.Order, 48, 256);
    }
}
