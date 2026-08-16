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
    public void Query_AnswersIntermediateZoomFromThePyramidInsteadOfRawSamples()
    {
        // Simulate one stereo geometry build at intermediate zoom: ~3600 pixels
        // (3x a 1200 px view) x 2 channels x 200 samples/pixel ranges. This used to
        // assert a wall-clock budget, which measures how loaded the machine is rather
        // than what the code does. The property that actually matters is that Query
        // answers from the precomputed pyramid, so assert the pyramid signature
        // instead: results snapped out to whole base bins, which a per-sample scan of
        // the exact range cannot produce.
        int length = 2_000_000;
        var left = new float[length];
        var right = new float[length];
        var rng = new Random(1);
        for (int i = 0; i < length; i++)
        {
            left[i] = (float)(rng.NextDouble() * 2 - 1);
            right[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        const int pixels = 3600;
        const int spp = 200;
        const int start = 100_000;

        // A marker planted outside the first queried range but inside the base bin that
        // overlaps it: only a pyramid lookup can see it, because a raw scan of
        // [start, start + spp) never reads that sample. Its value sits outside the
        // [-1, 1] programme domain so no random sample can be confused for it.
        int markerIndex = start / PeakStore.BaseBin * PeakStore.BaseBin;
        Assert.True(markerIndex < start, "The marker must fall before the queried range.");
        left[markerIndex] = 2f;

        float[][] channels = [left, right];
        var doc = new AudioDocument(channels, 48_000, 32);
        var peaks = new PeakStore();
        peaks.Rebuild(doc);

        for (int c = 0; c < channels.Length; c++)
        {
            for (int x = 0; x < pixels; x++)
            {
                int s0 = start + x * spp;
                int s1 = s0 + spp;
                peaks.Query(c, s0, s1, out float min, out float max, out float rms);

                // Every one of these ranges is wider than the raw-scan limit, so the
                // answer must cover the bin-aligned superset of [s0, s1) exactly.
                int b0 = s0 / PeakStore.BaseBin * PeakStore.BaseBin;
                int b1 = (s1 + PeakStore.BaseBin - 1) / PeakStore.BaseBin * PeakStore.BaseBin;
                (float binMin, float binMax, double binRms) = Scan(channels[c], b0, b1);

                Assert.Equal(binMin, min);
                Assert.Equal(binMax, max);
                Assert.True(Math.Abs(binRms - rms) < 1e-3,
                    $"Channel {c} pixel {x}: rms {rms} was not the bin-aligned {binRms}.");
            }
        }

        // The decisive case: the marker is unreachable from a scan of the queried range,
        // so reporting it proves the pyramid answered.
        peaks.Query(0, start, start + spp, out _, out float firstMax, out _);
        Assert.Equal(2f, firstMax);
    }

    private static (float Min, float Max, double Rms) Scan(float[] samples, int s0, int s1)
    {
        float min = float.MaxValue, max = float.MinValue;
        double sq = 0;
        for (int s = s0; s < s1; s++)
        {
            float v = samples[s];
            if (v < min) min = v;
            if (v > max) max = v;
            sq += v * v;
        }
        return (min, max, Math.Sqrt(sq / (s1 - s0)));
    }
}
