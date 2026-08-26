using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Finding the auto-split setting instead of leaving the user to hunt for it.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "it is hard to determine how to fix the problem with the slider". The window was
/// asking for the answer to an inverse problem — which level produces the right tracks — and the
/// only way to answer it was guess, count the rows, guess again.
/// </para>
/// <para>
/// It is answerable because <b>a real gap structure is robust to the threshold and a spurious one
/// is not</b>. Measured on a real three-track side, every setting from −55 to −40 dB proposes the
/// same three tracks with the splits steady within 0.07 s; past −40 they slide, by 7.6 s at −30.
/// So the setting to use is the middle of the widest run of thresholds that agree.
/// </para>
/// </remarks>
public sealed class CdSplitSweepTests(ITestOutputHelper output)
{
    /// <summary>8 kHz: the rules here are all in seconds, so duration is what the test needs.</summary>
    private const int Rate = 8_000;

    /// <summary>
    /// A 200 s side with two deep gaps and one shallow one, and a three-second fade into each.
    /// </summary>
    /// <remarks>
    /// <b>Both details are load-bearing.</b> A gap holding digital silence is quieter than every
    /// setting, so they would all agree and there would be no plateau structure to find. And
    /// without a fade the edge of a gap is a step, so the split lands in the same place at every
    /// threshold — which is exactly the drift the sweep exists to measure.
    /// <para>
    /// The shallow gap is what makes the answer ambiguous, on purpose: it is quiet enough to count
    /// only at the loud end of the sweep, so the side genuinely has two defensible readings.
    /// </para>
    /// </remarks>
    private static float[][] Side()
    {
        const double fade = 3;
        (double Start, double End, double Floor)[] gaps =
        [
            (40, 45, 0.001),    // -60 dBFS
            (85, 90, 0.001),    // -60 dBFS
            (130, 133, 0.02),   // -34 dBFS, only quiet enough near the top of the sweep
        ];

        int frames = 200 * Rate;
        var left = new float[frames];
        var right = new float[frames];
        var noise = new Random(7);

        for (int i = 0; i < frames; i++)
        {
            double at = i / (double)Rate;
            double level = 0.3;
            foreach ((double start, double end, double floor) in gaps)
            {
                if (at >= start && at < end) { level = floor; break; }
                // The three seconds before a gap fall to that gap's own floor.
                if (at >= start - fade && at < start)
                {
                    level = floor + (0.3 - floor) * ((start - at) / fade);
                    break;
                }
            }
            float value = (float)((noise.NextDouble() * 2 - 1) * level);
            left[i] = value;
            right[i] = value;
        }
        return [left, right];
    }

    private static CdSplitSweep Sweep(int? target = null) =>
        CdTransfer.SweepTracks(Side(), Rate, target);

    private void Report(CdSplitSweep sweep)
    {
        foreach (CdSplitCandidate c in sweep.Candidates)
            output.WriteLine($"{c.Tracks} track(s) from {c.LowestDb:0} to {c.HighestDb:0} dB " +
                $"(chose {c.ChosenDb:0}), splits at " +
                string.Join(", ", c.Boundaries.Select(b => $"{b / (double)Rate:0.00}")));
        output.WriteLine($"best: {sweep.Best?.Tracks.ToString() ?? "none"}");
    }

    /// <summary>
    /// The whole idea in one assertion: the answer that holds over the most settings is the one
    /// taken, and it is taken from the middle of that run rather than from an edge.
    /// </summary>
    [Fact]
    public void TheAnswerThatHoldsOverTheMostSettingsIsTheOneTaken()
    {
        CdSplitSweep sweep = Sweep();
        Report(sweep);

        Assert.NotNull(sweep.Best);
        Assert.Equal(3, sweep.Best!.Tracks);
        Assert.False(sweep.GapRelaxed);

        // Widest first, and the chosen setting is the middle of its own run.
        Assert.Same(sweep.Best, sweep.Candidates[0]);
        Assert.InRange(sweep.Best.ChosenDb, sweep.Best.LowestDb, sweep.Best.HighestDb);
        Assert.Equal(Math.Round((sweep.Best.LowestDb + sweep.Best.HighestDb) / 2,
            MidpointRounding.AwayFromZero), sweep.Best.ChosenDb);

        // Wide enough to be evidence rather than a coincidence.
        Assert.True(sweep.Best.HighestDb - sweep.Best.LowestDb >= 10,
            $"the plateau is only {sweep.Best.HighestDb - sweep.Best.LowestDb:0} dB wide");
    }

    /// <summary>
    /// The shallow gap gives the side a second defensible reading, and the runner-up is what the
    /// status line offers when the count is not the one the user expected.
    /// </summary>
    [Fact]
    public void ASideWithAShallowGapCarriesASecondAnswer()
    {
        CdSplitSweep sweep = Sweep();
        CdSplitCandidate four = Assert.Single(sweep.Candidates, c => c.Tracks == 4);

        // It exists only at the loud end, where -34 dBFS finally counts as quiet.
        Assert.True(four.LowestDb > sweep.Best!.HighestDb,
            $"the 4-track answer starts at {four.LowestDb:0} dB, not above the 3-track run");
        Assert.True(four.HighestDb - four.LowestDb < sweep.Best.HighestDb - sweep.Best.LowestDb,
            "the shallow answer should not hold over more settings than the deep one");
    }

    /// <summary>A count the user has off the record label is a fact the audio cannot supply.</summary>
    [Fact]
    public void ATrackCountThatIsReachableIsTakenOverTheSteadiestAnswer()
    {
        CdSplitSweep sweep = Sweep(target: 4);
        Assert.Equal(4, sweep.Best?.Tracks);
        // Still ranked by width, so the 3-track answer is still listed first.
        Assert.Equal(3, sweep.Candidates[0].Tracks);
    }

    /// <summary>
    /// The message that stops the hunt: no setting produces six, and saying which ones it does
    /// produce is the difference between an answer and being sent back to the slider.
    /// </summary>
    [Fact]
    public void ATrackCountNoSettingProducesIsRefusedWithTheOnesThatAreReachable()
    {
        CdSplitSweep sweep = Sweep(target: 6);
        Assert.Null(sweep.Best);

        string line = CdTransfer.DescribeSweep(sweep, 6);
        output.WriteLine(line);
        Assert.Contains("never 6", line, StringComparison.Ordinal);
        Assert.Contains("1, 3 or 4 tracks", line, StringComparison.Ordinal);
        Assert.Contains("Split", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// One track is the absence of a finding, so a side with nothing quiet in it says which way to
    /// go rather than reporting a track it did not find.
    /// </summary>
    [Fact]
    public void ASideWithNoQuietAtAllSaysSoRatherThanReportingOneTrack()
    {
        int frames = 200 * Rate;
        var noise = new Random(3);
        var channel = new float[frames];
        for (int i = 0; i < frames; i++) channel[i] = (float)(noise.NextDouble() * 2 - 1) * 0.3f;

        CdSplitSweep sweep = CdTransfer.SweepTracks([channel, channel], Rate);
        Assert.Equal(1, sweep.Best?.Tracks);

        string line = CdTransfer.DescribeSweep(sweep, null);
        output.WriteLine(line);
        Assert.Contains("No gaps found at any setting", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The invariant that stops the two buttons disagreeing.</b> Find Tracks applies a
    /// candidate's splits and leaves the slider at that candidate's chosen setting; pressing
    /// Analyze then re-derives from the slider. If those two did not match to the sample, the user
    /// would be told the splits had moved by pressing a button that did nothing.
    /// </summary>
    /// <remarks>
    /// This is also the first direct test <c>SuggestTracks</c> has ever had.
    /// </remarks>
    [Fact]
    public void AnalyzeAtTheChosenSettingReproducesWhatFindTracksApplied()
    {
        float[][] side = Side();
        CdSplitSweep sweep = CdTransfer.SweepTracks(side, Rate);

        foreach (CdSplitCandidate candidate in sweep.Candidates)
        {
            var byHand = CdTransfer.SuggestTracks(side, Rate, candidate.ChosenDb,
                CdTransfer.DefaultMinimumGapSeconds, CdTransfer.AutoSplitMinimumTrackSeconds);
            var swept = CdTransfer.PlansFor(candidate);

            Assert.Equal(swept.Count, byHand.Count);
            for (int i = 0; i < swept.Count; i++)
            {
                Assert.Equal(swept[i].SourceStart, byHand[i].SourceStart);
                Assert.Equal(swept[i].SourceEnd, byHand[i].SourceEnd);
            }
        }
    }

    /// <summary>
    /// The sweep is affordable only because the envelope is measured once, so the two ways of
    /// asking for silences have to give the same answer.
    /// </summary>
    [Fact]
    public void MeasuringTheEnvelopeOnceGivesTheSameSilencesAsMeasuringItEveryTime()
    {
        float[][] side = Side();
        float[] envelope = Restoration.BlockPeaks(side, Rate);
        Assert.Equal((side[0].Length + Restoration.SilenceBlock - 1) / Restoration.SilenceBlock, envelope.Length);

        for (double db = -70; db <= -25; db++)
        {
            var direct = Restoration.DetectSilences(side, Rate, db, 1250);
            var reused = Restoration.DetectSilences(envelope, side[0].Length, Rate, db, 1250);
            Assert.Equal(direct, reused);
        }
    }

    /// <summary>
    /// A quiet passage is not a gap, and a one-decibel answer is not evidence of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test of <c>MinimumPlateauDb</c>, and it exists because <b>the corpus does not
    /// exercise that constant at all</b>: every side built from real transfers produces a plateau
    /// about twenty decibels wide, so the guard never binds there and the corpus result is no
    /// evidence about it either way.
    /// </para>
    /// <para>
    /// It binds at the top of the sweep, and only there. Once a gap is quiet enough to register it
    /// goes on registering at every louder setting, so a multi-track answer always runs to the end
    /// of the sweep — which makes it narrow only when it first appears near the end. A stretch that
    /// is a mere 26 dB below the programme is a soft passage in a song rather than the space
    /// between two, and splitting a song there is the failure the guard is for.
    /// </para>
    /// </remarks>
    [Fact]
    public void AQuietPassageIsNotAGapAndOneDecibelIsNotEvidence()
    {
        int frames = 200 * Rate;
        var noise = new Random(5);
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double at = i / (double)Rate;
            // Two seconds only 26 dB under the programme: long enough to be a gap, nowhere near
            // quiet enough to be one.
            double level = at >= 100 && at < 102 ? 0.048 : 0.3;
            float value = (float)((noise.NextDouble() * 2 - 1) * level);
            left[i] = value;
            right[i] = value;
        }

        CdSplitSweep sweep = CdTransfer.SweepTracks([left, right], Rate);
        foreach (CdSplitCandidate c in sweep.Candidates)
            output.WriteLine($"{c.Tracks} track(s) from {c.LowestDb:0} to {c.HighestDb:0} dB");

        // The two-track reading exists — and is refused, because it holds over almost nothing.
        CdSplitCandidate two = Assert.Single(sweep.Candidates, c => c.Tracks == 2);
        Assert.True(two.HighestDb - two.LowestDb < CdTransfer.MinimumPlateauDb,
            $"the spurious answer holds over {two.HighestDb - two.LowestDb:0} dB, so this no longer tests the guard");
        Assert.Equal(1, sweep.Best?.Tracks);

        // Asking for two by name still gets it: the guard is about what to choose unprompted, not
        // about overruling somebody who knows their own record.
        Assert.Equal(2, CdTransfer.SweepTracks([left, right], Rate, targetTracks: 2).Best?.Tracks);
    }

    [Fact]
    public void AnEmptyRecordingSweepsToNothingRatherThanThrowing()
    {
        CdSplitSweep sweep = CdTransfer.SweepTracks([[], []], Rate);
        Assert.Empty(sweep.Candidates);
        Assert.Null(sweep.Best);
        Assert.Equal("No gaps found at any setting. The songs may run together, or the quiet " +
            "between them may be too short.", CdTransfer.DescribeSweep(sweep, null));
    }
}
