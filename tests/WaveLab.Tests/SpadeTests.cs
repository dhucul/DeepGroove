using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class SpadeTests(ITestOutputHelper output)
{
    private const int SampleRate = 44_100;
    private const int Length = 40_000;

    /// <summary>Programme with several partials and a light noise floor, peaking near full scale.</summary>
    private static float[] Programme(int seed = 3, double peak = 0.95)
    {
        var random = new Random(seed);
        var signal = new float[Length];
        double maximum = 0;
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)SampleRate;
            double value =
                  0.50 * Math.Sin(2 * Math.PI * 147 * t)
                + 0.28 * Math.Sin(2 * Math.PI * 294 * t + 0.4)
                + 0.16 * Math.Sin(2 * Math.PI * 441 * t - 0.8)
                + 0.09 * Math.Sin(2 * Math.PI * 882 * t)
                + (random.NextDouble() - 0.5) * 0.004;
            signal[i] = (float)value;
            maximum = Math.Max(maximum, Math.Abs(value));
        }
        for (int i = 0; i < Length; i++) signal[i] = (float)(signal[i] / maximum * peak);
        return signal;
    }

    private static (float[] Clipped, bool[] Mask, int Count) Clip(float[] clean, double level)
    {
        var clipped = (float[])clean.Clone();
        var mask = new bool[clean.Length];
        int count = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            if (clipped[i] > level) { clipped[i] = (float)level; mask[i] = true; count++; }
            else if (clipped[i] < -level) { clipped[i] = (float)-level; mask[i] = true; count++; }
        }
        return (clipped, mask, count);
    }

    /// <summary>Error against the clean original, restricted to the samples that were destroyed.</summary>
    private static double ClippedSnrDb(float[] clean, float[] candidate, bool[] mask)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            if (!mask[i]) continue;
            double difference = clean[i] - candidate[i];
            signal += (double)clean[i] * clean[i];
            error += difference * difference;
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    // ── reconstruction quality ───────────────────────────────────

    /// <summary>
    /// The headline measurement across increasing severity, with the gain that severity actually
    /// affords. At 0.70 roughly a quarter of the samples are gone and the recovery is dramatic; by
    /// 0.25 three quarters are gone, and there is correspondingly less left to infer from. Stating
    /// the expected gain per level records that falloff rather than pretending to uniform
    /// performance — and still fails if any level regresses.
    /// </summary>
    [Theory]
    [InlineData(0.70, 10.0)]
    [InlineData(0.50, 5.0)]
    [InlineData(0.35, 2.0)]
    [InlineData(0.25, 1.0)]
    public void DeclippingMovesCrushedSamplesMuchCloserToTheOriginal(double level, double expectedGainDb)
    {
        float[] clean = Programme();
        var (clipped, mask, count) = Clip(clean, level);

        double before = ClippedSnrDb(clean, clipped, mask);
        var candidate = (float[])clipped.Clone();
        SpadeResult result = Spade.Declip(candidate, level);
        double after = ClippedSnrDb(clean, candidate, mask);

        output.WriteLine($"level {level:0.00}: {count} clipped ({100.0 * count / Length:0.0}%) · " +
                         $"{before:0.0} dB → {after:0.0} dB (+{after - before:0.0}) · " +
                         $"{result.FramesRepaired}/{result.FramesProcessed} frames, " +
                         $"{result.MeanIterations:0.0} iterations");

        Assert.True(count > 0, "the test signal was not actually clipped");
        Assert.True(after > before + expectedGainDb,
            $"declipping gained only {after - before:0.0} dB, expected more than {expectedGainDb:0.0}");
    }

    [Fact]
    public void ReliableSamplesAreLeftWhereTheyWere()
    {
        float[] clean = Programme();
        var (clipped, mask, _) = Clip(clean, 0.5);
        var candidate = (float[])clipped.Clone();

        Spade.Declip(candidate, 0.5);

        double worst = 0;
        for (int i = 0; i < Length; i++)
            if (!mask[i]) worst = Math.Max(worst, Math.Abs(candidate[i] - clipped[i]));

        output.WriteLine($"largest change to a reliable sample: {worst:0.000000}");
        Assert.True(worst < 1e-3, $"a reliable sample moved by {worst:0.000000}");
    }

    /// <summary>
    /// A reconstruction that stayed inside the rail would be admitting the flat top was real. Every
    /// repaired sample must end up at least at the clipping level, on the side it railed.
    /// </summary>
    [Fact]
    public void RepairedSamplesArePushedBackPastTheRail()
    {
        float[] clean = Programme();
        var (clipped, mask, _) = Clip(clean, 0.5);
        var candidate = (float[])clipped.Clone();

        Spade.Declip(candidate, 0.5);

        int violations = 0, extended = 0;
        for (int i = 0; i < Length; i++)
        {
            if (!mask[i]) continue;
            bool positive = clipped[i] > 0;
            if (positive && candidate[i] < 0.5 - 1e-3) violations++;
            if (!positive && candidate[i] > -0.5 + 1e-3) violations++;
            if (Math.Abs(candidate[i]) > 0.5 + 1e-3) extended++;
        }

        output.WriteLine($"{extended} samples reconstructed beyond the rail, {violations} violations");
        Assert.Equal(0, violations);
        Assert.True(extended > 0, "nothing was actually reconstructed above the clipping level");
    }

    [Fact]
    public void UnclippedAudioIsPassedThroughUnchanged()
    {
        float[] clean = Programme(peak: 0.4);
        var candidate = (float[])clean.Clone();

        SpadeResult result = Spade.Declip(candidate, 0.9);

        Assert.Equal(0, result.FramesRepaired);
        Assert.Equal(0L, result.ClippedSamples);

        double worst = 0;
        for (int i = 0; i < Length; i++) worst = Math.Max(worst, Math.Abs(candidate[i] - clean[i]));
        output.WriteLine($"largest change to unclipped audio: {worst:0.000000}");
        Assert.True(worst < 1e-4, $"unclipped audio moved by {worst:0.000000}");
    }

    // ── robustness ───────────────────────────────────────────────

    [Fact]
    public void ResultIsDeterministic()
    {
        float[] clean = Programme();
        var (clipped, _, _) = Clip(clean, 0.5);
        var first = (float[])clipped.Clone();
        var second = (float[])clipped.Clone();

        Spade.Declip(first, 0.5);
        Spade.Declip(second, 0.5);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EntirelyRailedAudioStaysFinite()
    {
        var square = new float[Length];
        for (int i = 0; i < Length; i++) square[i] = (i / 50) % 2 == 0 ? 0.9f : -0.9f;

        Spade.Declip(square, 0.9);

        Assert.All(square, value => Assert.True(float.IsFinite(value)));
        Assert.All(square, value => Assert.True(Math.Abs(value) < 8));
    }

    [Fact]
    public void SilenceIsUntouched()
    {
        var silence = new float[8_192];
        Spade.Declip(silence, 0.5);
        Assert.All(silence, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void EmptyInputIsHandled()
    {
        SpadeResult result = Spade.Declip([], 0.5);
        Assert.Equal(0, result.FramesProcessed);
    }

    [Fact]
    public void CancellationStopsTheSolver()
    {
        float[] clean = Programme();
        var (clipped, _, _) = Clip(clean, 0.4);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Spade.Declip(clipped, 0.4, SpadeOptions.Default, cancellation.Token));
    }

    [Theory]
    [InlineData(1000, 256)]   // not a power of two
    [InlineData(1024, 300)]   // hop does not divide the frame
    [InlineData(16, 4)]       // frame too short
    public void InvalidConfigurationIsRejected(int frameSize, int hop)
    {
        var options = SpadeOptions.Default with { FrameSize = frameSize, Hop = hop };
        Assert.Throws<ArgumentException>(() => Spade.Declip(new float[4_096], 0.5, options));
    }

    [Fact]
    public void AZeroClipLevelIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Spade.Declip(new float[1_024], 0));
    }
}
