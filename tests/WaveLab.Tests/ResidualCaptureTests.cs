using WaveLab.Audio.Dsp;
using WaveLab.Util;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// What a restoration pass removed, kept as audio. The claim the whole feature rests on is that
/// the residual is the exact difference — not a rendering of it, not a boosted copy — so that
/// mixing it back onto the restored audio returns the original. These tests hold that claim.
/// </summary>
public sealed class ResidualCaptureTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    private static float[][] Programme(int length, int channels = 2, int seed = 7)
    {
        var random = new Random(seed);
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[length];
            for (int i = 0; i < length; i++)
            {
                // Tone plus a noise bed, so the residual has something to be a small part of.
                data[c][i] = (float)(0.4 * Math.Sin(2 * Math.PI * (220 + 40 * c) * i / Rate)
                                     + 0.02 * (random.NextDouble() * 2 - 1));
            }
        }
        return data;
    }

    [Fact]
    public void TheResidualIsTheDifferenceSampleForSample()
    {
        var dry = Programme(4096);
        var processed = RestorationPreview.Clone(dry);
        processed[0][100] = 0.9f;
        processed[1][2000] = -0.3f;

        float[][] removed = RestorationPreview.Difference(dry, processed);

        for (int c = 0; c < dry.Length; c++)
        {
            for (int i = 0; i < dry[c].Length; i++)
                Assert.Equal(dry[c][i] - processed[c][i], removed[c][i]);
        }
    }

    /// <summary>
    /// The load-bearing property. Asserted as a bound rather than as bit equality: subtracting
    /// two floats and adding one back is exact only where the two are close enough for Sterbenz,
    /// which a repair usually but not always satisfies.
    /// </summary>
    [Fact]
    public void MixingTheResidualBackOntoTheRestoredAudioReturnsTheOriginal()
    {
        var dry = Programme(1 << 16);
        var processed = RestorationPreview.Clone(dry);
        var random = new Random(11);
        // Something removal-shaped: isolated impulses replaced, plus a broadband trim everywhere.
        for (int c = 0; c < processed.Length; c++)
        {
            for (int i = 0; i < processed[c].Length; i++) processed[c][i] *= 0.97f;
            for (int n = 0; n < 200; n++)
            {
                int at = random.Next(processed[c].Length);
                processed[c][at] = (float)(random.NextDouble() * 2 - 1);
            }
        }

        float[][] removed = RestorationPreview.Difference(dry, processed);

        double worst = 0;
        for (int c = 0; c < dry.Length; c++)
        {
            for (int i = 0; i < dry[c].Length; i++)
                worst = Math.Max(worst, Math.Abs(processed[c][i] + removed[c][i] - dry[c][i]));
        }
        output.WriteLine($"worst reconstruction error: {worst:0.###e+00}");
        Assert.True(worst <= 1e-6, $"residual + restored drifted from the original by {worst}");
    }

    [Fact]
    public void AToolThatChangedNothingLeavesASilentResidual()
    {
        var dry = Programme(2048);
        float[][] removed = RestorationPreview.Difference(dry, RestorationPreview.Clone(dry));

        Assert.Equal(0f, RestorationPreview.PeakOf(removed));
        Assert.True(RestorationPreview.PeakOf(removed) <= ResidualSummary.SilenceThreshold);
    }

    /// <summary>
    /// The workbench blends dry and restored before committing, so what it removed is only the
    /// part of the difference the blend let through. That falls out of subtracting the committed
    /// audio and needs no special case — which is the reason the residual is taken there rather
    /// than from inside each tool.
    /// </summary>
    [Fact]
    public void ADryWetBlendScalesTheResidualByTheWetAmount()
    {
        var dry = Programme(4096, channels: 1);
        var fullyProcessed = RestorationPreview.Clone(dry);
        for (int i = 0; i < fullyProcessed[0].Length; i++) fullyProcessed[0][i] *= 0.5f;

        const double wet = 0.4;
        float[][] blended = RestorationPreview.Mix(dry, fullyProcessed, wet);
        float[][] removed = RestorationPreview.Difference(dry, blended);
        float[][] full = RestorationPreview.Difference(dry, fullyProcessed);

        // A tolerance rather than decimal places: both sides are single precision and the
        // comparison sits at the rounding boundary, where "six places" fails on a difference
        // of 1.8e-8 that is simply what a float is.
        double worst = 0;
        for (int i = 0; i < removed[0].Length; i++)
            worst = Math.Max(worst, Math.Abs(full[0][i] * wet - removed[0][i]));
        output.WriteLine($"worst departure from wet x difference: {worst:0.###e+00}");
        Assert.True(worst <= 1e-6, $"the blended residual is not the scaled difference: {worst}");
    }

    /// <summary>
    /// A selection restores a range, and the dry reference is the whole document — the channel
    /// snapshot the tool took before the splice. The offset is what lines the two up.
    /// </summary>
    [Fact]
    public void ARangeIsDifferencedAgainstItsOwnPartOfTheSource()
    {
        var dry = Programme(8192);
        const int start = 3000, count = 1024;
        var processed = new float[dry.Length][];
        for (int c = 0; c < dry.Length; c++)
        {
            processed[c] = dry[c].AsSpan(start, count).ToArray();
            for (int i = 0; i < count; i++) processed[c][i] -= 0.01f;
        }

        float[][] removed = RestorationPreview.Difference(dry, processed, start);

        Assert.Equal(count, removed[0].Length);
        for (int c = 0; c < dry.Length; c++)
        {
            for (int i = 0; i < count; i++)
                Assert.Equal(0.01f, removed[c][i], 5);
        }
    }

    [Fact]
    public void ADryReferenceTooShortForTheRangeIsRefusedRatherThanRead()
    {
        var dry = Programme(1024);
        var processed = Programme(1024);
        Assert.Throws<ArgumentException>(() => RestorationPreview.Difference(dry, processed, dryOffset: 1));
    }

    /// <summary>
    /// One pass rather than two, because both numbers are wanted together and only together, and
    /// the caller is holding a buffer the size of the range.
    /// </summary>
    [Fact]
    public void MeasuringBothLevelsAtOnceAgreesWithMeasuringThemSeparately()
    {
        var dry = Programme(1 << 15);
        var processed = RestorationPreview.Clone(dry);
        for (int c = 0; c < processed.Length; c++)
        {
            for (int i = 0; i < processed[c].Length; i++) processed[c][i] *= 0.9f;
            processed[c][77] = 0.8f;
        }
        float[][] removed = RestorationPreview.Difference(dry, processed);

        var levels = RestorationPreview.MeasureLevels(removed);
        Assert.Equal(RestorationPreview.PeakOf(removed), levels.Peak);
        Assert.Equal(RestorationPreview.RmsOf(removed), levels.Rms);
    }

    [Fact]
    public void MeasuringEmptyOrSilentAudioIsZeroRatherThanUndefined()
    {
        var levels = RestorationPreview.MeasureLevels([[], []]);
        Assert.Equal(0f, levels.Peak);
        Assert.Equal(0f, levels.Rms);

        var silent = RestorationPreview.MeasureLevels([new float[128]]);
        Assert.Equal(0f, silent.Peak);
        Assert.Equal(0f, silent.Rms);
    }

    [Fact]
    public void PeakIsTheLargestMagnitudeAcrossEveryChannel()
    {
        float[][] channels = [[0.1f, -0.6f, 0.2f], [0.3f, 0.05f, -0.4f]];
        Assert.Equal(0.6f, RestorationPreview.PeakOf(channels));
        Assert.Equal(0f, RestorationPreview.PeakOf([[], []]));
    }
}
