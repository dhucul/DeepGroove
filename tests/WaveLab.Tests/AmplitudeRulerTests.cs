using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

public sealed class AmplitudeRulerTests
{
    // The renderer's own geometry: the band is half a channel tall and full scale sits at 46% of it.
    private const double TickGapPx = 4;
    private const double LabelGapPx = 11;
    private const double Tolerance = 1e-9;

    private static (double Amplitude, double MaxOffset) Band(double channelHeight, double ampZoom) =>
        (channelHeight * 0.46 * ampZoom, channelHeight / 2 - 2);

    private static List<AmplitudeRuler.ScaleTick> Scale(double channelHeight, double ampZoom)
    {
        var (amplitude, maxOffset) = Band(channelHeight, ampZoom);
        return AmplitudeRuler.BuildScale(amplitude, maxOffset);
    }

    private static int[] Levels(IEnumerable<AmplitudeRuler.ScaleTick> ticks) =>
        ticks.Select(t => t.LevelDb).ToArray();

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-6.020599913, 50)]
    [InlineData(-20, 10)]
    public void DbMarkersUseTheWaveformsLinearAmplitudeScale(double levelDb, double expectedOffset)
    {
        Assert.Equal(expectedOffset, AmplitudeRuler.MarkerOffset(levelDb, 100), 6);
    }

    [Fact]
    public void RulerIncludesCommonPeakReferenceLevels()
    {
        Assert.Equal([0, -3, -6, -12, -24], AmplitudeRuler.MarkerLevelsDb);
    }

    [Fact]
    public void LadderFillsATallBandDownToTheCentreLine()
    {
        var ticks = Scale(channelHeight: 398, ampZoom: 1);

        Assert.Equal(
            [0, -1, -2, -3, -4, -5, -6, -7, -8, -9, -10, -11, -12, -13, -15, -18, -21, -24],
            Levels(ticks));
        Assert.Equal(
            [0, -1, -2, -3, -4, -5, -6, -9, -12, -18, -24],
            Levels(ticks.Where(t => t.Labeled)));
    }

    /// <summary>
    /// The old fixed list was the whole scale, so winding the amplitude zoom up pushed 0/-3/-6 off
    /// the band and left the ruler with two numbers on it. The ladder re-solves for what is visible.
    /// </summary>
    [Fact]
    public void LadderStaysPopulatedWhenZoomPushesFullScaleOffTheBand()
    {
        var ticks = Scale(channelHeight: 398, ampZoom: 4);
        int[] labelled = Levels(ticks.Where(t => t.Labeled));

        Assert.Equal([-12, -13, -14, -15, -16, -17, -18, -21, -24, -30, -36], labelled);
        Assert.DoesNotContain(0, Levels(ticks));    // full scale really is off the top
        Assert.True(labelled.Length > AmplitudeRuler.MarkerLevelsDb.Length);
    }

    /// <summary>
    /// Every band height and amplitude zoom the editor can actually produce (AmpZoom is clamped to
    /// 1..8), checked against the invariants the renderer draws on. Cheap enough to sweep, and the
    /// interesting failures live at the boundaries between step sizes rather than at round numbers.
    /// </summary>
    [Fact]
    public void EveryReachableGeometryProducesADrawableLadder()
    {
        var failures = new List<string>();

        for (double channelHeight = 4; channelHeight <= 1400; channelHeight += 2.5)
        {
            for (double ampZoom = 1; ampZoom <= 8.0001; ampZoom += 0.125)
            {
                var (amplitude, maxOffset) = Band(channelHeight, ampZoom);
                var ticks = AmplitudeRuler.BuildScale(amplitude, maxOffset);
                string where = $"chH={channelHeight:0.0} zoom={ampZoom:0.000}";

                int coarsestStep = 0;
                for (int i = 0; i < ticks.Count; i++)
                {
                    var tick = ticks[i];
                    if (tick.Offset > maxOffset + Tolerance || tick.Offset < TickGapPx - Tolerance)
                        failures.Add($"{where}: {tick.LevelDb} dB sits at {tick.Offset:0.00} px, outside the band");
                    if (Math.Abs(tick.Offset - AmplitudeRuler.MarkerOffset(tick.LevelDb, amplitude)) > Tolerance)
                        failures.Add($"{where}: {tick.LevelDb} dB offset disagrees with MarkerOffset");
                    if (i == 0) continue;

                    var above = ticks[i - 1];
                    if (tick.LevelDb >= above.LevelDb || tick.Offset >= above.Offset)
                        failures.Add($"{where}: ladder not descending at {tick.LevelDb} dB");
                    if (above.Offset - tick.Offset < TickGapPx - Tolerance)
                        failures.Add($"{where}: {above.LevelDb}/{tick.LevelDb} dB only {above.Offset - tick.Offset:0.00} px apart");

                    // The ladder may only coarsen on the way in, never sharpen up again.
                    int step = above.LevelDb - tick.LevelDb;
                    if (step < coarsestStep)
                        failures.Add($"{where}: step narrowed {coarsestStep}->{step} dB at {tick.LevelDb} dB");
                    coarsestStep = step;
                }

                var labelled = ticks.Where(t => t.Labeled).ToList();
                for (int i = 0; i < labelled.Count; i++)
                {
                    // The centre line carries -inf, so no number may come within a label height of it.
                    if (labelled[i].Offset < LabelGapPx - Tolerance)
                        failures.Add($"{where}: {labelled[i].LevelDb} dB label {labelled[i].Offset:0.00} px from the centre");
                    if (i > 0 && labelled[i - 1].Offset - labelled[i].Offset < LabelGapPx - Tolerance)
                        failures.Add($"{where}: {labelled[i - 1].LevelDb}/{labelled[i].LevelDb} dB labels overlap");
                }

                // The renderer solves into one reused list, so a rebuild must not accumulate.
                List<AmplitudeRuler.ScaleTick> reused = [];
                AmplitudeRuler.BuildScale(reused, amplitude, maxOffset);
                AmplitudeRuler.BuildScale(reused, amplitude, maxOffset);
                if (!reused.SequenceEqual(ticks)) failures.Add($"{where}: rebuilding into a reused list changed the ladder");
            }
        }

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(398, 1)]
    [InlineData(199, 8)]
    [InlineData(40, 1)]
    public void LabelsOnlyEverSitOnTicks(double channelHeight, double ampZoom)
    {
        var ticks = Scale(channelHeight, ampZoom);

        Assert.Subset(Levels(ticks).ToHashSet(), Levels(ticks.Where(t => t.Labeled)).ToHashSet());
    }

    /// <summary>The waveform's own guide lines still land on rungs of the ruler at ordinary zoom.</summary>
    [Theory]
    [InlineData(398)]
    [InlineData(500)]
    public void WaveformGuideLevelsAreOnTheLadder(double channelHeight)
    {
        var levels = Levels(Scale(channelHeight, ampZoom: 1)).ToHashSet();

        Assert.All(AmplitudeRuler.MarkerLevelsDb, db => Assert.Contains((int)db, levels));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(120, 0)]
    [InlineData(120, 3)]
    [InlineData(-5, 100)]
    public void DegenerateGeometryProducesAnEmptyLadder(double amplitudeHeight, double maxOffset)
    {
        Assert.Empty(AmplitudeRuler.BuildScale(amplitudeHeight, maxOffset));
    }
}
