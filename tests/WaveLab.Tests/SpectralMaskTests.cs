using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class SpectralMaskTests(ITestOutputHelper output)
{
    private const int SampleRate = 48_000;

    /// <summary>Sustained partials plus one loud blob, so the wand has something to grow through.</summary>
    private static SpectrogramData Analysed(bool withBlob = true)
    {
        const int length = 60_000;
        var signal = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)SampleRate;
            // Well below the burst: at a comparable level the wand legitimately walks down to a
            // partial and runs along it for the whole file, and the test would be measuring the
            // signal rather than the tool.
            signal[i] = (float)(0.0005 * Math.Sin(2 * Math.PI * 500 * t)
                              + 0.0003 * Math.Sin(2 * Math.PI * 1500 * t));
        }
        if (withBlob)
        {
            // A short broadband burst in the middle.
            var random = new Random(4);
            for (int i = 28_000; i < 32_000; i++)
                signal[i] += (float)((random.NextDouble() - 0.5) * 1.4);
        }
        return Spectrogram.Analyze(signal, 0, length, SampleRate,
            SpectrogramSettings.Default with { Reassign = false });
    }

    // ── rectangle ────────────────────────────────────────────────

    [Fact]
    public void ARectangleCoversTheCellsItWasDrawnOver()
    {
        SpectralMask mask = SpectralMask.Rectangle(10, 30, 100, 160, feather: 0);

        Assert.Equal(10, mask.FrameOffset);
        Assert.Equal(100, mask.BinOffset);
        Assert.Equal(20, mask.Frames);
        Assert.Equal(60, mask.Bins);
        Assert.Equal(1f, mask.At(20, 130));
        Assert.Equal(0f, mask.At(9, 130));
        Assert.Equal(0f, mask.At(30, 130));
        Assert.Equal(0f, mask.At(20, 99));
        Assert.Equal(0f, mask.At(20, 160));
    }

    [Fact]
    public void CornersAreOrderedSoDraggingInAnyDirectionWorks()
    {
        SpectralMask forward = SpectralMask.Rectangle(10, 30, 100, 160, feather: 0);
        SpectralMask backward = SpectralMask.Rectangle(30, 10, 160, 100, feather: 0);

        Assert.Equal(forward.FrameOffset, backward.FrameOffset);
        Assert.Equal(forward.BinOffset, backward.BinOffset);
        Assert.Equal(forward.Frames, backward.Frames);
        Assert.Equal(forward.Bins, backward.Bins);
    }

    [Theory]
    [InlineData(10, 10, 100, 160)]   // no width
    [InlineData(10, 30, 100, 100)]   // no height
    public void ADegenerateRectangleIsEmpty(int f0, int f1, int b0, int b1)
    {
        Assert.True(SpectralMask.Rectangle(f0, f1, b0, b1).IsEmpty);
    }

    // ── feathering ───────────────────────────────────────────────

    /// <summary>
    /// The property that matters: the taper must run inward. A weight anywhere outside the drawn
    /// outline would mean the edit reaches audio the user did not select.
    /// </summary>
    [Fact]
    public void TheTaperNeverReachesOutsideTheOutline()
    {
        SpectralMask mask = SpectralMask.Rectangle(20, 60, 100, 200, feather: 4);

        for (int f = 10; f < 70; f++)
            for (int b = 90; b < 210; b++)
            {
                bool inside = f >= 20 && f < 60 && b >= 100 && b < 200;
                if (!inside) Assert.Equal(0f, mask.At(f, b));
            }
    }

    [Fact]
    public void TheTaperRisesSmoothlyToFullStrengthInside()
    {
        SpectralMask mask = SpectralMask.Rectangle(0, 40, 0, 40, feather: 4);

        // Along a line into the middle: zero at the edge, one in the centre, monotone between.
        var profile = new List<float>();
        for (int b = 0; b < 20; b++) profile.Add(mask.At(20, b));

        output.WriteLine("edge → centre: " + string.Join(" ", profile.ConvertAll(v => v.ToString("0.00"))));
        // Near zero rather than exactly zero: the outermost cell inside the outline still catches
        // the tail of the smoothing kernel, which is what a taper is.
        Assert.True(profile[0] < 0.05f, $"the outermost cell weighed {profile[0]:0.000}");
        Assert.Equal(1f, profile[^1], 3);
        for (int i = 1; i < profile.Count; i++)
            Assert.True(profile[i] >= profile[i - 1] - 1e-6, "the taper must not dip on the way in");
    }

    [Fact]
    public void AnUnfeatheredMaskHasHardEdges()
    {
        SpectralMask mask = SpectralMask.Rectangle(0, 20, 0, 20, feather: 0);
        Assert.Equal(1f, mask.At(0, 0));
        Assert.Equal(1f, mask.At(19, 19));
    }

    /// <summary>
    /// A feather wider than the region gives way to the region rather than erasing it. Eroding a
    /// four-cell selection by eight cells leaves nothing at all, which turned a small repair into a
    /// silent no-op — the tool doing nothing reads as the tool being broken, and the taper is a
    /// detail of how the edit is applied, not a reason not to apply it.
    /// </summary>
    [Theory]
    [InlineData(4, 8)]
    [InlineData(2, 2)]
    [InlineData(1, 4)]
    public void AFeatherWiderThanTheRegionGivesWayToIt(int size, int feather)
    {
        SpectralMask mask = SpectralMask.Rectangle(0, size, 0, size, feather);

        Assert.False(mask.IsEmpty);
        Assert.True(mask.Coverage() > 0, $"a {size}×{size} selection vanished under a {feather}-cell feather");
    }

    [Fact]
    public void AFeatherStillTapersWhenTheRegionIsLargeEnoughToCarryIt()
    {
        SpectralMask mask = SpectralMask.Rectangle(0, 40, 0, 40, feather: 4);

        Assert.Equal(1f, mask.At(20, 20), 3);
        Assert.True(mask.At(0, 0) < 0.2f, "the outline itself must stay near zero");
    }

    // ── lasso ────────────────────────────────────────────────────

    [Fact]
    public void ALassoCoversItsInteriorAndNothingElse()
    {
        // A triangle with a right angle at (10,10).
        var outline = new[] { (10.0, 10.0), (40.0, 10.0), (10.0, 40.0) };
        SpectralMask mask = SpectralMask.Lasso(outline, feather: 0);

        Assert.Equal(1f, mask.At(15, 15));           // well inside
        Assert.Equal(0f, mask.At(35, 35));           // beyond the hypotenuse
        Assert.Equal(0f, mask.At(5, 20));            // outside entirely
    }

    [Fact]
    public void AConcaveOutlineIsFollowedRatherThanFilledIn()
    {
        // A C shape: the notch must stay unselected.
        var outline = new[]
        {
            (10.0, 10.0), (40.0, 10.0), (40.0, 18.0), (20.0, 18.0),
            (20.0, 32.0), (40.0, 32.0), (40.0, 40.0), (10.0, 40.0),
        };
        SpectralMask mask = SpectralMask.Lasso(outline, feather: 0);

        Assert.Equal(1f, mask.At(15, 25));   // the spine of the C
        Assert.Equal(0f, mask.At(35, 25));   // the notch
    }

    [Fact]
    public void TooFewPointsIsAnEmptySelection()
    {
        Assert.True(SpectralMask.Lasso([(1.0, 1.0), (2.0, 2.0)]).IsEmpty);
    }

    // ── magic wand ───────────────────────────────────────────────

    [Fact]
    public void TheWandGrowsThroughConnectedEnergyAndStopsAtTheFloor()
    {
        SpectrogramData data = Analysed();

        // Seed inside the burst.
        int seedFrame = 30_000 / data.Hop;
        SpectralMask mask = SpectralMask.MagicWand(data, seedFrame, data.Bins / 2, toleranceDb: 18, feather: 0);

        double share = mask.Coverage() / ((double)data.Frames * data.Bins);
        output.WriteLine($"wand took {mask.Coverage():0} cells over {mask.Frames} frames × {mask.Bins} bins " +
                         $"({share:P1} of the grid)");
        Assert.False(mask.IsEmpty);
        Assert.True(mask.Coverage() > 50, "the burst should be more than a handful of cells");
        Assert.True(share < 0.25, $"the wand took {share:P0} of the whole grid");
    }

    [Fact]
    public void ATighterToleranceTakesLess()
    {
        SpectrogramData data = Analysed();
        int seedFrame = 30_000 / data.Hop;

        double loose = SpectralMask.MagicWand(data, seedFrame, data.Bins / 2, 24, feather: 0).Coverage();
        double tight = SpectralMask.MagicWand(data, seedFrame, data.Bins / 2, 6, feather: 0).Coverage();

        output.WriteLine($"tolerance 24 dB → {loose:0} cells, 6 dB → {tight:0} cells");
        Assert.True(tight <= loose);
    }

    [Fact]
    public void TheWandIsBoundedSoItCannotRunAway()
    {
        SpectrogramData data = Analysed();
        SpectralMask mask = SpectralMask.MagicWand(data, data.Frames / 2, data.Bins / 2,
            toleranceDb: 200, maximumCells: 500, feather: 0);

        Assert.True(mask.Coverage() <= 500);
    }

    [Fact]
    public void ASeedOutsideTheGridIsAnEmptySelection()
    {
        SpectrogramData data = Analysed(withBlob: false);
        Assert.True(SpectralMask.MagicWand(data, -1, 0).IsEmpty);
        Assert.True(SpectralMask.MagicWand(data, 0, data.Bins).IsEmpty);
    }

    // ── harmonic ─────────────────────────────────────────────────

    [Fact]
    public void HarmonicSelectionTakesThePartialsAndLeavesTheGapsBetweenThem()
    {
        SpectrogramData data = Analysed(withBlob: false);
        SpectralMask mask = SpectralMask.Harmonic(data, 10, 40, fundamentalHz: 500,
            partials: 6, relativeBandwidth: 0.02, feather: 0);

        double binsPerHz = data.FftSize / (double)data.SampleRate;
        int atFundamental = (int)Math.Round(500 * binsPerHz);
        int atSecond = (int)Math.Round(1000 * binsPerHz);
        int between = (int)Math.Round(750 * binsPerHz);

        Assert.Equal(1f, mask.At(20, atFundamental));
        Assert.Equal(1f, mask.At(20, atSecond));
        Assert.Equal(0f, mask.At(20, between));
    }

    [Fact]
    public void HarmonicSelectionStopsAtNyquist()
    {
        SpectrogramData data = Analysed(withBlob: false);

        // Ninety partials of 500 Hz would run to 45 kHz; the grid stops at 24.
        SpectralMask mask = SpectralMask.Harmonic(data, 0, 20, 500, partials: 90, feather: 0);

        Assert.False(mask.IsEmpty);
        Assert.Equal(data.Bins, mask.Bins);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    public void ANonsenseFundamentalIsAnEmptySelection(double fundamental)
    {
        SpectrogramData data = Analysed(withBlob: false);
        Assert.True(SpectralMask.Harmonic(data, 0, 20, fundamental).IsEmpty);
    }

    // ── shared behaviour ─────────────────────────────────────────

    [Fact]
    public void EveryBuilderRecordsHowItWasDrawn()
    {
        SpectrogramData data = Analysed(withBlob: false);

        Assert.Equal(SpectralSelectionKind.Rectangle, SpectralMask.Rectangle(0, 10, 0, 10).Kind);
        Assert.Equal(SpectralSelectionKind.Lasso,
            SpectralMask.Lasso([(0.0, 0.0), (10.0, 0.0), (5.0, 10.0)]).Kind);
        Assert.Equal(SpectralSelectionKind.MagicWand, SpectralMask.MagicWand(data, 5, 5).Kind);
        Assert.Equal(SpectralSelectionKind.Harmonic, SpectralMask.Harmonic(data, 0, 10, 500).Kind);
    }

    // ── full band, for a plain time selection ────────────────────

    private const int Fft = 2048, Hop = 512;

    /// <summary>
    /// The whole point of the separate builder: the frequency edges of a full-band mask are the ends
    /// of the spectrum, not edges in the signal, and the general feather erodes inward from both —
    /// so routing this through <see cref="SpectralMask.ForRegion"/> fades out the very bins a
    /// selection across everything asked for.
    /// </summary>
    [Fact]
    public void AFullBandMaskDoesNotFadeOutDcAndNyquist()
    {
        SpectralMask full = SpectralMask.FullBand(20_000, 40_000, Fft, Hop);
        SpectralMask viaRectangle = SpectralMask.ForRegion(
            20_000, 40_000, 0, SampleRate / 2.0, SampleRate, Fft, Hop);

        int middle = full.FrameOffset + full.Frames / 2;
        int top = Fft / 2;
        output.WriteLine($"full: DC {full.At(middle, 0):0.00}, Nyquist {full.At(middle, top):0.00}");
        output.WriteLine($"rect: DC {viaRectangle.At(middle, 0):0.00}, " +
                         $"Nyquist {viaRectangle.At(middle, top):0.00}");

        Assert.Equal(1f, full.At(middle, 0));
        Assert.Equal(1f, full.At(middle, top));
        Assert.True(viaRectangle.At(middle, 0) < 1f, "the general feather is expected to erode here");
    }

    [Fact]
    public void AFullBandMaskCoversEveryBinAndOnlyTheFramesAskedFor()
    {
        SpectralMask mask = SpectralMask.FullBand(20_000, 40_000, Fft, Hop);

        Assert.Equal(0, mask.BinOffset);
        Assert.Equal(Fft / 2 + 1, mask.Bins);
        Assert.Equal(20_000 / Hop, mask.FrameOffset);

        int middle = mask.FrameOffset + mask.Frames / 2;
        for (int b = 0; b < mask.Bins; b++) Assert.Equal(1f, mask.At(middle, b));

        Assert.Equal(0f, mask.At(mask.FrameOffset - 1, mask.Bins / 2));
        Assert.Equal(0f, mask.At(mask.FrameOffset + mask.Frames, mask.Bins / 2));
    }

    /// <summary>The ends of the span are edges the audio has, so they are the ones that ring.</summary>
    [Fact]
    public void AFullBandMaskStillTapersTheEndsOfItsSpan()
    {
        SpectralMask mask = SpectralMask.FullBand(20_000, 40_000, Fft, Hop);
        int bin = mask.Bins / 2;

        float first = mask.At(mask.FrameOffset, bin);
        float last = mask.At(mask.FrameOffset + mask.Frames - 1, bin);
        output.WriteLine($"first frame {first:0.00}, last frame {last:0.00}");

        Assert.InRange(first, 0f, 0.99f);
        Assert.InRange(last, 0f, 0.99f);
    }

    [Fact]
    public void AFullBandMaskOfNoExtentIsNothingSelected()
    {
        Assert.True(SpectralMask.FullBand(5_000, 5_000, Fft, Hop).IsEmpty);
        Assert.False(SpectralMask.FullBandFits(5_000, 5_000, Fft, Hop));
    }

    /// <summary>
    /// A repair holds four planes the size of the mask per channel, so the ceiling is what stops a
    /// select-all from asking for gigabytes. It is refused before it is built, not partway through.
    /// </summary>
    [Fact]
    public void AFullBandMaskPastTheCeilingIsRefusedRatherThanAllocated()
    {
        int bins = Fft / 2 + 1;
        int frames = (int)(SpectralMask.MaximumFullBandCells / bins) + 2 * Hop;
        int end = frames * Hop;

        Assert.False(SpectralMask.FullBandFits(0, end, Fft, Hop));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpectralMask.FullBand(0, end, Fft, Hop));

        int within = (int)(SpectralMask.MaximumFullBandCells / bins) / 2 * Hop;
        Assert.True(SpectralMask.FullBandFits(0, within, Fft, Hop));
    }

    /// <summary>
    /// The fit test and the builder have to agree, and an int frame count is what could part them.
    /// </summary>
    /// <remarks>
    /// At the hop spectral edits use, a frame count cannot overflow an int. At a hop of one it can,
    /// and the wrap is <em>negative</em> — which reads as "no frames", so the fit test would answer
    /// yes to a span the builder then declines to build, leaving the actions lit and doing nothing.
    /// Counted in long, both refuse it.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(Hop)]
    public void AFrameCountTooLargeForAnIntIsRefusedRatherThanWrappingNegative(int hop)
    {
        bool fits = SpectralMask.FullBandFits(0, int.MaxValue, Fft, hop);
        output.WriteLine($"hop {hop}: fits={fits}");

        Assert.False(fits);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpectralMask.FullBand(0, int.MaxValue, Fft, hop));
    }

    [Fact]
    public void ReversedEndsOfAFullBandSpanAreOrderedRatherThanRejected()
    {
        SpectralMask forward = SpectralMask.FullBand(10_000, 30_000, Fft, Hop);
        SpectralMask backward = SpectralMask.FullBand(30_000, 10_000, Fft, Hop);

        Assert.Equal(forward.FrameOffset, backward.FrameOffset);
        Assert.Equal(forward.Frames, backward.Frames);
        Assert.Equal(forward.Bins, backward.Bins);
    }

    [Fact]
    public void WeightsNeverLeaveTheUnitRange()
    {
        SpectrogramData data = Analysed();
        SpectralMask[] masks =
        [
            SpectralMask.Rectangle(5, 50, 10, 200, feather: 3),
            SpectralMask.Lasso([(5.0, 10.0), (50.0, 10.0), (30.0, 90.0)], feather: 3),
            SpectralMask.MagicWand(data, 30_000 / data.Hop, data.Bins / 2, feather: 3),
            SpectralMask.Harmonic(data, 5, 40, 500, feather: 2),
        ];

        foreach (SpectralMask mask in masks)
            Assert.All(mask.Weight, value => Assert.InRange(value, 0f, 1f));
    }
}
