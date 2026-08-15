using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class PeakStoreTests
{
    [Fact]
    public void Query_UsesPyramidForIntermediatePixelRanges()
    {
        // A ramp that is easy to verify: sample i has value i / length.
        int length = 50_000;
        var samples = new float[length];
        for (int i = 0; i < length; i++) samples[i] = i / (float)length;
        var doc = new AudioDocument([samples], 48_000, 32);
        var peaks = new PeakStore();
        peaks.Rebuild(doc);

        // Intermediate zoom: 128 samples/pixel — previously fell into the raw
        // scan path when the base bin was 256 and stalled playhead-follow.
        int s0 = 10_000;
        int s1 = s0 + 128;
        peaks.Query(0, s0, s1, out float min, out float max, out float rms);

        Assert.True(min <= samples[s0] + 1e-3f);
        Assert.True(max >= samples[s1 - 1] - 1e-3f);
        Assert.InRange(rms, 0f, 1f);
        // Pyramid bins can slightly overshoot the exact range edges; they must
        // still be in the document amplitude domain.
        Assert.InRange(min, 0f, 1f);
        Assert.InRange(max, 0f, 1f);
        Assert.True(max >= min);
    }

    [Fact]
    public void Query_RawPathMatchesExactSamplesAtCloseZoom()
    {
        var samples = new float[1_024];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (i % 17 == 0) ? 0.75f : -0.25f;
        var doc = new AudioDocument([samples], 48_000, 32);
        var peaks = new PeakStore();
        peaks.Rebuild(doc);

        int s0 = 100;
        int s1 = 116; // 16 samples — well under the raw-scan limit
        peaks.Query(0, s0, s1, out float min, out float max, out float rms);

        float expectedMin = float.MaxValue, expectedMax = float.MinValue;
        double sq = 0;
        for (int s = s0; s < s1; s++)
        {
            float v = samples[s];
            if (v < expectedMin) expectedMin = v;
            if (v > expectedMax) expectedMax = v;
            sq += v * v;
        }
        float expectedRms = (float)Math.Sqrt(sq / (s1 - s0));

        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
        Assert.Equal(expectedRms, rms, precision: 5);
    }

    [Fact]
    public void Query_IsFastEnoughForContinuousGeometryRebuilds()
    {
        // Simulate one stereo geometry build at intermediate zoom: ~3600 pixels
        // (3× a 1200 px view) × 2 channels × 200 samples/pixel ranges.
        int length = 2_000_000;
        var left = new float[length];
        var right = new float[length];
        var rng = new Random(1);
        for (int i = 0; i < length; i++)
        {
            left[i] = (float)(rng.NextDouble() * 2 - 1);
            right[i] = (float)(rng.NextDouble() * 2 - 1);
        }
        var doc = new AudioDocument([left, right], 48_000, 32);
        var peaks = new PeakStore();
        peaks.Rebuild(doc);

        const int pixels = 3600;
        const double spp = 200;
        double start = 100_000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int c = 0; c < 2; c++)
        {
            for (int x = 0; x < pixels; x++)
            {
                int s0 = (int)(start + x * spp);
                int s1 = Math.Max(s0 + 1, (int)(start + (x + 1) * spp));
                peaks.Query(c, s0, s1, out _, out _, out _);
            }
        }
        sw.Stop();

        // Pyramid lookups should finish a full intermediate-zoom rebuild well
        // under a frame. The old raw-scan path routinely exceeded this budget.
        Assert.True(sw.ElapsedMilliseconds < 40,
            $"Intermediate-zoom geometry query took {sw.ElapsedMilliseconds} ms");
    }
}
