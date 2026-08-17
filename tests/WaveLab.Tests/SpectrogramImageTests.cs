using WaveLab.Audio.Dsp;
using WaveLab.Views.Controls;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class SpectrogramImageTests(ITestOutputHelper output)
{
    private const int SampleRate = 48_000;
    private const double Floor = -96, Ceiling = 0;

    private static double Luminance(uint bgra)
    {
        double r = (bgra >> 16) & 0xFF, g = (bgra >> 8) & 0xFF, b = bgra & 0xFF;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>A grid at the floor except for one bin held at full level across every frame.</summary>
    private static SpectrogramData WithPartial(int frames, int bins, int loudBin, float loudDb = 0)
    {
        var image = new float[frames * bins];
        Array.Fill(image, (float)Floor);
        for (int f = 0; f < frames; f++) image[f * bins + loudBin] = loudDb;
        return Analysed(frames, bins, image);
    }

    /// <summary>
    /// Built by calling the constructor, not by reflecting onto it.
    /// </summary>
    /// <remarks>
    /// It was reflection, and it did not need to be — this project already sees the assembly's
    /// internals. The cost showed up the first time the constructor gained an optional parameter:
    /// reflection wants every argument whether or not the language does, so thirteen tests failed
    /// with a parameter count mismatch over a change that could not break a direct call.
    /// </remarks>
    private static SpectrogramData Analysed(int frames, int bins, float[] image)
    {
        int fftSize = (bins - 1) * 2;
        return new SpectrogramData(frames, bins, image, SampleRate, fftSize, fftSize / 4, 0);
    }

    // ── palettes ─────────────────────────────────────────────────

    /// <summary>
    /// The property that makes a ramp usable for judging faint detail: lightness must rise with
    /// level, so nothing looks like a boundary that is not one.
    /// </summary>
    [Theory]
    [InlineData(SpectrogramPalette.Viridis)]
    [InlineData(SpectrogramPalette.Magma)]
    [InlineData(SpectrogramPalette.Teal)]
    [InlineData(SpectrogramPalette.Grey)]
    public void EveryRampGetsLighterAsItGoesUp(SpectrogramPalette palette)
    {
        double previous = -1;
        for (int i = 0; i <= 100; i++)
        {
            double luminance = Luminance(SpectrogramImage.Sample(palette, i / 100.0));
            Assert.True(luminance >= previous - 0.75,
                $"{palette} dipped in lightness at {i}%: {luminance:0.0} after {previous:0.0}");
            previous = luminance;
        }

        double bottom = Luminance(SpectrogramImage.Sample(palette, 0));
        double top = Luminance(SpectrogramImage.Sample(palette, 1));
        output.WriteLine($"{palette}: {bottom:0} → {top:0}");
        Assert.True(top - bottom > 120, $"{palette} uses too little of the range");
    }

    [Fact]
    public void RampsAreClampedOutsideTheUnitRange()
    {
        Assert.Equal(SpectrogramImage.Sample(SpectrogramPalette.Viridis, 0),
                     SpectrogramImage.Sample(SpectrogramPalette.Viridis, -5));
        Assert.Equal(SpectrogramImage.Sample(SpectrogramPalette.Viridis, 1),
                     SpectrogramImage.Sample(SpectrogramPalette.Viridis, 9));
    }

    [Fact]
    public void RampsAreFullyOpaque()
    {
        for (int i = 0; i <= 10; i++)
            Assert.Equal(0xFFu, SpectrogramImage.Sample(SpectrogramPalette.Magma, i / 10.0) >> 24);
    }

    // ── geometry ─────────────────────────────────────────────────

    [Fact]
    public void HighFrequenciesAreDrawnAtTheTop()
    {
        // 15 kHz: high, and inside the 20 kHz the display shows. A partial above the ceiling is
        // correctly not drawn at all, which makes it useless for testing where things land.
        const int fftSize = 1024;
        int loudBin = (int)Math.Round(15_000.0 * fftSize / SampleRate);
        SpectrogramData data = WithPartial(frames: 32, bins: fftSize / 2 + 1, loudBin: loudBin);
        var pixels = new uint[200 * 120];

        SpectrogramImage.Render(data, pixels, 200, 120, Floor, Ceiling);

        int brightest = 0;
        double best = -1;
        for (int y = 0; y < 120; y++)
        {
            double luminance = Luminance(pixels[y * 200 + 100]);
            if (luminance > best) { best = luminance; brightest = y; }
        }

        output.WriteLine($"15 kHz landed on row {brightest} of 120");
        Assert.True(brightest < 40, $"a 15 kHz partial should sit near the top, not row {brightest}");
    }

    [Fact]
    public void ContentAboveTheDisplayCeilingIsNotDrawn()
    {
        // Bin 500 of 513 is 23.4 kHz — real, but above the 20 kHz the view shows.
        SpectrogramData data = WithPartial(frames: 32, bins: 513, loudBin: 500);
        var pixels = new uint[200 * 120];

        SpectrogramImage.Render(data, pixels, 200, 120, Floor, Ceiling);

        double brightest = 0;
        foreach (uint pixel in pixels) brightest = Math.Max(brightest, Luminance(pixel));
        Assert.Equal(Luminance(SpectrogramImage.Sample(SpectrogramPalette.Viridis, 0)), brightest, 1);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(15_000)]
    public void AToneIsDrawnOnTheRowItsFrequencyMapsTo(double frequency)
    {
        var settings = SpectrogramImageSettings.Default;
        const int height = 240;
        int fftSize = 1024, bins = fftSize / 2 + 1;
        int loudBin = (int)Math.Round(frequency * fftSize / SampleRate);

        SpectrogramData data = WithPartial(frames: 16, bins: bins, loudBin: loudBin);
        var pixels = new uint[120 * height];
        SpectrogramImage.Render(data, pixels, 120, height, Floor, Ceiling, settings);

        int brightest = 0;
        double best = -1;
        for (int y = 0; y < height; y++)
        {
            double luminance = Luminance(pixels[y * 120 + 60]);
            if (luminance > best) { best = luminance; brightest = y; }
        }

        double expected = SpectrogramImage.RowForFrequency(frequency, height, settings, SampleRate / 2.0);
        output.WriteLine($"{frequency} Hz: brightest row {brightest}, expected about {expected:0}");
        Assert.InRange(brightest, expected - 8, expected + 8);
    }

    /// <summary>
    /// The reduction has to be a maximum. On a log axis most rows cover many bins, and averaging
    /// would bury a one-bin partial in the floor either side of it — which is precisely the detail
    /// this view exists to show.
    /// </summary>
    [Fact]
    public void AOneBinPartialSurvivesHeavyDownsampling()
    {
        SpectrogramData data = WithPartial(frames: 40, bins: 2049, loudBin: 1600);
        var pixels = new uint[160 * 90];   // 2049 bins into 90 rows

        SpectrogramImage.Render(data, pixels, 160, 90, Floor, Ceiling);

        double brightest = 0;
        foreach (uint pixel in pixels) brightest = Math.Max(brightest, Luminance(pixel));
        double floorLuminance = Luminance(SpectrogramImage.Sample(SpectrogramPalette.Viridis, 0));

        output.WriteLine($"brightest {brightest:0} against a floor of {floorLuminance:0}");
        Assert.True(brightest > floorLuminance + 100,
            "the partial was averaged away instead of being kept");
    }

    [Fact]
    public void FrequencyAndRowAreInversesOfEachOther()
    {
        var settings = SpectrogramImageSettings.Default;
        const int height = 300;

        foreach (double frequency in new[] { 25.0, 100.0, 440.0, 3_000.0, 19_000.0 })
        {
            double row = SpectrogramImage.RowForFrequency(frequency, height, settings, 24_000);
            double back = SpectrogramImage.FrequencyForRow(row, height, settings, 24_000);
            Assert.Equal(frequency, back, frequency * 0.001);
        }
    }

    [Fact]
    public void ALinearAxisSpacesFrequenciesEvenly()
    {
        var settings = SpectrogramImageSettings.Default with { Logarithmic = false };
        const int height = 200;

        double quarter = SpectrogramImage.FrequencyForRow(height * 0.75, height, settings, 24_000);
        double half = SpectrogramImage.FrequencyForRow(height * 0.5, height, settings, 24_000);
        double threeQuarters = SpectrogramImage.FrequencyForRow(height * 0.25, height, settings, 24_000);

        // Evenly spaced rows should give evenly spaced frequencies.
        Assert.Equal(half - quarter, threeQuarters - half, 1);
    }

    // ── levels ───────────────────────────────────────────────────

    [Fact]
    public void TheFloorAndCeilingMapToTheEndsOfTheRamp()
    {
        var atFloor = new float[8 * 33];
        Array.Fill(atFloor, -60f);
        var pixels = new uint[40 * 24];

        SpectrogramImage.Render(Analysed(8, 33, atFloor), pixels, 40, 24, -60, 0);
        Assert.All(pixels.ToArray(), p =>
            Assert.Equal(SpectrogramImage.Sample(SpectrogramPalette.Viridis, 0), p));

        Array.Fill(atFloor, 0f);
        SpectrogramImage.Render(Analysed(8, 33, atFloor), pixels, 40, 24, -60, 0);
        Assert.All(pixels.ToArray(), p =>
            Assert.Equal(SpectrogramImage.Sample(SpectrogramPalette.Viridis, 1), p));
    }

    [Fact]
    public void GammaDarkensTheQuietEndWithoutMovingTheEnds()
    {
        var mid = new float[4 * 17];
        Array.Fill(mid, -48f);           // exactly halfway between -96 and 0
        var plain = new uint[20 * 12];
        var shaped = new uint[20 * 12];

        SpectrogramImage.Render(Analysed(4, 17, mid), plain, 20, 12, Floor, Ceiling,
            SpectrogramImageSettings.Default with { Gamma = 1.0 });
        SpectrogramImage.Render(Analysed(4, 17, mid), shaped, 20, 12, Floor, Ceiling,
            SpectrogramImageSettings.Default with { Gamma = 2.0 });

        Assert.True(Luminance(shaped[0]) < Luminance(plain[0]),
            "a gamma above one should push mid levels down the ramp");
    }

    // ── degenerate input ─────────────────────────────────────────

    [Fact]
    public void AnEmptyAnalysisPaintsTheBackground()
    {
        SpectrogramData empty = Spectrogram.Analyze(new float[100], 0, 0, SampleRate);
        var pixels = new uint[30 * 20];

        SpectrogramImage.Render(empty, pixels, 30, 20, Floor, Ceiling);

        Assert.All(pixels.ToArray(), p =>
            Assert.Equal(SpectrogramImage.Sample(SpectrogramPalette.Viridis, 0), p));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-4, 10)]
    public void ADegenerateSizeIsANoOp(int width, int height)
    {
        SpectrogramData data = WithPartial(8, 33, 10);
        var pixels = new uint[400];
        SpectrogramImage.Render(data, pixels, width, height, Floor, Ceiling);
        Assert.All(pixels.ToArray(), p => Assert.Equal(0u, p));
    }

    [Fact]
    public void ABufferTooSmallIsRejectedRatherThanOverrun()
    {
        SpectrogramData data = WithPartial(8, 33, 10);
        var pixels = new uint[10];
        Assert.Throws<ArgumentException>(() =>
        {
            SpectrogramImage.Render(data, pixels, 40, 40, Floor, Ceiling);
        });
    }

    [Fact]
    public void MoreImageColumnsThanFramesStillFillsEveryColumn()
    {
        SpectrogramData data = WithPartial(frames: 3, bins: 33, loudBin: 16);
        var pixels = new uint[300 * 40];

        SpectrogramImage.Render(data, pixels, 300, 40, Floor, Ceiling);

        // Every column must have been written; none may be left as the zero it started at.
        for (int x = 0; x < 300; x++)
            Assert.NotEqual(0u, pixels[20 * 300 + x]);
    }
}
