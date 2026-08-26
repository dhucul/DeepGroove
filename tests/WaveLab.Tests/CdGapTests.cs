using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// An even silence between every pair of tracks, and a player that starts on the music.
/// </summary>
/// <remarks>
/// <para>
/// Asked for as "can we have an option to put in some pregaps — silence between tracks". Two
/// decisions behind it, both taken deliberately.
/// </para>
/// <para>
/// <b>Every gap is made the same length rather than lengthened by the same amount.</b> The splits
/// land at the middle of the quiet between two songs, so each track already carries half of
/// whatever the record left there — which differs from gap to gap. Adding a fixed silence on top
/// would preserve that unevenness and make it worse. Taking the quiet off both ends and putting
/// back exactly what was asked for is what makes the disc even.
/// </para>
/// <para>
/// <b>The silence is the incoming track's pregap, not its opening.</b> It sits between INDEX 00 and
/// INDEX 01, so a player counts it down while the disc plays through and skips it when the track is
/// chosen — which is what a shop-bought CD does, and what selecting track 2 and getting two seconds
/// of dead air does not.
/// </para>
/// </remarks>
public sealed class CdGapTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;

    /// <summary>
    /// Two songs with an uneven amount of the record's own quiet around each split: 4 s after the
    /// first and 8 s after the second. Nothing here is digital silence — it is groove noise at
    /// −60 dBFS, which is what a transfer actually holds.
    /// </summary>
    private static float[][] Side()
    {
        int frames = 180 * Rate;
        var left = new float[frames];
        var right = new float[frames];
        var noise = new Random(13);
        for (int i = 0; i < frames; i++)
        {
            double at = i / (double)Rate;
            bool quiet = (at >= 60 && at < 64) || (at >= 124 && at < 132);
            float value = (float)((noise.NextDouble() * 2 - 1) * (quiet ? 0.001 : 0.3));
            left[i] = value;
            right[i] = value;
        }
        return [left, right];
    }

    /// <summary>Splits at the middle of each quiet stretch, which is where the analysis puts them.</summary>
    private static List<CdTrackPlan> Tiled() =>
    [
        new(0, 62 * Rate, "One"),
        new(62 * Rate, 128 * Rate, "Two"),
        new(128 * Rate, 180 * Rate, "Three"),
    ];

    /// <summary>
    /// What is actually heard between two tracks on the finished disc, which is the incoming
    /// track's pregap and nothing else: each track is cut from the programme at its own range, so
    /// source material lying between two ranges is never written at all.
    /// </summary>
    private static double GapOnTheDisc(IReadOnlyList<CdTrackPlan> plans, int index) =>
        plans[index + 1].PregapSeconds;

    /// <summary>The record's own quiet at a split, which is what gets thrown away.</summary>
    private static double QuietDiscarded(IReadOnlyList<CdTrackPlan> plans, int index) =>
        (plans[index + 1].SourceStart - plans[index].SourceEnd) / (double)Rate;

    /// <summary>
    /// The whole point: two gaps the record left at four and eight seconds come out the same.
    /// </summary>
    [Fact]
    public void EveryGapComesOutTheLengthThatWasAskedFor()
    {
        List<CdTrackPlan> before = Tiled();
        // As arranged the tracks tile the recording, so the quiet is inside them and every gap is
        // whatever the record happened to leave: four seconds after one song and eight after the
        // next. That unevenness is the thing being fixed.
        Assert.Equal(0, GapOnTheDisc(before, 0));
        Assert.Equal(0, GapOnTheDisc(before, 1));

        var after = CdTransfer.ApplyGaps(Side(), Rate, before, gapSeconds: 2, quietBelowDb: -45);
        foreach (CdTrackPlan p in after)
            output.WriteLine($"{p.Title}: {p.SourceStart / (double)Rate:0.00} to " +
                $"{p.SourceEnd / (double)Rate:0.00}, pregap {p.PregapSeconds:0.#} s");

        // Both come out at exactly what was asked for.
        Assert.Equal(2, GapOnTheDisc(after, 0));
        Assert.Equal(2, GapOnTheDisc(after, 1));

        // And the uneven quiet the record left is what was taken out to make that true: all four
        // seconds at the first split and all eight at the second.
        Assert.Equal(4, QuietDiscarded(after, 0), 2);
        Assert.Equal(8, QuietDiscarded(after, 1), 2);

        // Track 01 never carries one: the disc's own two-second lead-in already is its pregap.
        Assert.Equal(0, after[0].PregapSeconds);
        Assert.Equal(2, after[1].PregapSeconds);
        Assert.Equal(2, after[2].PregapSeconds);
    }

    /// <summary>
    /// The lead-in and the run-out are not between anything, so they are left exactly as they are.
    /// </summary>
    [Fact]
    public void TheHeadOfTheFirstTrackAndTheTailOfTheLastAreNotTouched()
    {
        List<CdTrackPlan> before = Tiled();
        var after = CdTransfer.ApplyGaps(Side(), Rate, before, 2, -45);

        Assert.Equal(before[0].SourceStart, after[0].SourceStart);
        Assert.Equal(before[^1].SourceEnd, after[^1].SourceEnd);
        // The ends that face another track did move.
        Assert.True(after[0].SourceEnd < before[0].SourceEnd);
        Assert.True(after[^1].SourceStart > before[^1].SourceStart);
    }

    /// <summary>
    /// Setting the gap again must not eat another slice each time, because the window re-applies it
    /// whenever the list changes underneath it.
    /// </summary>
    [Fact]
    public void ApplyingTheSameGapTwiceChangesNothingTheSecondTime()
    {
        var once = CdTransfer.ApplyGaps(Side(), Rate, Tiled(), 2, -45);
        var twice = CdTransfer.ApplyGaps(Side(), Rate, once, 2, -45);
        Assert.Equal(once, twice);

        // And changing the figure re-trims from where it already is rather than compounding.
        var wider = CdTransfer.ApplyGaps(Side(), Rate, once, 4, -45);
        Assert.Equal(once[1].SourceStart, wider[1].SourceStart);
        Assert.Equal(4, wider[1].PregapSeconds);
    }

    /// <summary>
    /// Nothing above the level the user called quiet is ever cut, so a fade only loses the part of
    /// itself that had already fallen below it — inaudible by that definition.
    /// </summary>
    [Fact]
    public void NothingAboveTheQuietLevelIsTrimmed()
    {
        float[][] side = Side();
        var after = CdTransfer.ApplyGaps(side, Rate, Tiled(), 2, -45);
        double threshold = Math.Pow(10, -45 / 20.0);

        foreach (CdTrackPlan track in after)
        {
            // The sample just outside each trimmed end has to be quiet, or music was thrown away.
            if (track.SourceStart > 0)
                Assert.True(Math.Abs(side[0][track.SourceStart - 1]) < threshold,
                    $"{track.Title} was trimmed into audio above the threshold at its start");
            if (track.SourceEnd < side[0].Length)
                Assert.True(Math.Abs(side[0][track.SourceEnd]) < threshold,
                    $"{track.Title} was trimmed into audio above the threshold at its end");
        }
    }

    /// <summary>A track holding nothing above the threshold is left alone rather than collapsed.</summary>
    [Fact]
    public void ATrackWithNoMusicInItSurvives()
    {
        int frames = 120 * Rate;
        var quiet = new float[frames];
        var noise = new Random(2);
        for (int i = 0; i < frames; i++) quiet[i] = (float)(noise.NextDouble() * 2 - 1) * 0.001f;

        List<CdTrackPlan> plans = [new(0, 60 * Rate, "One"), new(60 * Rate, frames, "Two")];
        var after = CdTransfer.ApplyGaps([quiet, quiet], Rate, plans, 2, -45);

        Assert.Equal(plans[0].SourceStart, after[0].SourceStart);
        Assert.Equal(plans[0].SourceEnd, after[0].SourceEnd);
        Assert.Equal(plans[1].SourceEnd, after[1].SourceEnd);
    }

    [Fact]
    public void AGapOfZeroLeavesTheRecordsOwnQuietExactlyAsItIs()
    {
        var after = CdTransfer.ApplyGaps(Side(), Rate, Tiled(), 0, -45);
        Assert.Equal(Tiled().Select(t => (t.SourceStart, t.SourceEnd)),
            after.Select(t => (t.SourceStart, t.SourceEnd)));
        Assert.All(after, t => Assert.Equal(0, t.PregapSeconds));
    }

    /// <summary>Two seconds is a hundred and fifty CD frames exactly, so nothing is left over.</summary>
    [Fact]
    public void AGapIsAWholeNumberOfCdFrames()
    {
        Assert.Equal(150, new CdTrackPlan(0, 1, "x", PregapSeconds: 2).PregapSectors);
        Assert.Equal(75, new CdTrackPlan(0, 1, "x", PregapSeconds: 1).PregapSectors);
        Assert.Equal(0, new CdTrackPlan(0, 1, "x").PregapSectors);
    }

    /// <summary>The silence is counted into what the disc holds, or a full disc would overrun.</summary>
    [Fact]
    public void TheGapsCountTowardsWhatWillFitOnTheDisc()
    {
        var plans = CdTransfer.ApplyGaps(Side(), Rate, Tiled(), 4, -45);
        string without = CdTransfer.Validate(Tiled(), Rate, 180 * Rate)
            .First(i => i.Severity == CdPlanIssueSeverity.Information).Message;
        string with = CdTransfer.Validate(plans, Rate, 180 * Rate)
            .First(i => i.Severity == CdPlanIssueSeverity.Information).Message;

        output.WriteLine($"without gaps: {without}");
        output.WriteLine($"with 4 s gaps: {with}");
        Assert.NotEqual(without, with);
        Assert.Equal(8, CdTransfer.TotalGapSeconds(plans));

        // The disc gets shorter, not longer: twelve seconds of the record's own quiet came out and
        // eight seconds of gap went back. A gap setting is not simply time added.
        Assert.Equal("3 tracks, 3:00 on the disc.", without);
        Assert.Equal("3 tracks, 2:56 on the disc.", with);
    }

    [Fact]
    public void TheGapLineSaysWhatItDidAndWhatItCost()
    {
        string set = CdTransfer.DescribeGap(2, tracks: 3, trimmed: 2);
        output.WriteLine(set);
        Assert.Contains("2 s between every pair of tracks", set, StringComparison.Ordinal);
        Assert.Contains("2 tracks trimmed back to their music", set, StringComparison.Ordinal);
        Assert.Contains("Choosing a track still starts on the music", set, StringComparison.Ordinal);

        Assert.Contains("One track trimmed", CdTransfer.DescribeGap(2, 3, 1), StringComparison.Ordinal);
        Assert.Contains("Nothing needed trimming", CdTransfer.DescribeGap(2, 3, 0), StringComparison.Ordinal);
        Assert.Contains("Gap removed", CdTransfer.DescribeGap(0, 3, 0), StringComparison.Ordinal);
    }
}
