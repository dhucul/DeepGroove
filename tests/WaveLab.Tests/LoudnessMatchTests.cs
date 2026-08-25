using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The arithmetic behind Match Loudness: what each mode levels to, and what the true-peak ceiling
/// does to it.
/// </summary>
/// <remarks>
/// The defect this file stands in for is the batch converter's LUFS branch, which applies the gain
/// loudness asks for and nothing else — it will push a track past 0 dBTP and report nothing. Every
/// ceiling assertion here exists so that cannot happen again on this path.
/// </remarks>
public sealed class LoudnessMatchTests
{
    private static LoudnessMeasurement Track(string name, double lufs, double truePeak = -6.0) =>
        new(name, lufs, truePeak, 6.0, 44_100, 44_100 * 30);

    private static LoudnessTarget Streaming => LoudnessTarget.Streaming;

    [Fact]
    public void EveryTrackEndsAtThePresetTargetWhenNothingIsTruePeakLimited()
    {
        LoudnessMeasurement[] tracks =
        [
            Track("A", -20, -12),
            Track("B", -12, -4),
            Track("C", -14.5, -6),
        ];

        var plan = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Target, Streaming);

        Assert.Equal(Streaming.IntegratedLufs, plan.TargetLufs, 6);
        Assert.Equal(Streaming.TruePeakDbtp, plan.CeilingDbtp, 6);
        foreach (var step in plan.Steps)
        {
            Assert.Equal(0, step.ShortfallDb, 6);
            Assert.Equal(Streaming.IntegratedLufs, step.ResultingLufs, 6);
        }
    }

    [Fact]
    public void MatchingToTheQuietestNeverAsksForABoost()
    {
        LoudnessMeasurement[] tracks = [Track("A", -20), Track("B", -12), Track("C", -16)];

        var plan = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Quietest, Streaming);

        Assert.Equal(-20, plan.TargetLufs, 6);
        foreach (var step in plan.Steps)
            Assert.True(step.GainDb <= 0, $"{step.Measurement.Name} was boosted by {step.GainDb:0.00} dB.");
    }

    /// <summary>
    /// The mean is taken of the LUFS figures, not of their power. LUFS is already a perceptual
    /// scale; a power mean is dominated by the loudest track and lands several LU above where a
    /// listener puts the average of a record.
    /// </summary>
    [Fact]
    public void MatchingToTheAverageIsTheArithmeticMeanOfTheLoudnessFigures()
    {
        LoudnessMeasurement[] tracks = [Track("A", -20, -14), Track("B", -12, -8), Track("C", -16, -10)];

        var plan = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Average, Streaming);

        Assert.Equal(-16, plan.TargetLufs, 6);
        Assert.True(plan.Steps[0].GainDb > 0, "the quiet track was not brought up.");
        Assert.True(plan.Steps[1].GainDb < 0, "the loud track was not brought down.");
        Assert.Equal(-16, plan.Steps.Average(s => s.ResultingLufs), 6);
    }

    [Fact]
    public void TheReferenceTrackIsLeftAloneAndSaysSo()
    {
        LoudnessMeasurement[] tracks = [Track("A", -20, -14), Track("B", -12, -9), Track("C", -16, -11)];

        var plan = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Reference, Streaming, referenceIndex: 2);

        Assert.Equal(-16, plan.TargetLufs, 6);
        Assert.Equal(0, plan.Steps[2].GainDb, 6);
        Assert.False(plan.Steps[2].CanApply);
        Assert.Equal("reference", plan.Steps[2].Note);
        Assert.Equal(-16, plan.Steps[0].ResultingLufs, 6);
    }

    /// <summary>
    /// Gain alone cannot reach a loudness target without eventually running out of headroom. The
    /// gain is cut back to the ceiling and what is left over is reported as the limiting the master
    /// would need — never applied, and never silently dropped.
    /// </summary>
    [Fact]
    public void AGainThatWouldBreakTheCeilingIsCutBackAndTheShortfallIsReported()
    {
        LoudnessMeasurement[] tracks = [Track("Quiet side", -28, -3)];

        var plan = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Target, Streaming);
        var step = plan.Steps[0];

        Assert.Equal(14, step.RequestedGainDb, 6);
        Assert.Equal(2, step.GainDb, 6);
        Assert.Equal(12, step.ShortfallDb, 6);
        Assert.True(step.ResultingTruePeakDbtp <= plan.CeilingDbtp + 1e-9);
        Assert.Contains("true-peak limited", step.Note);

        string report = LoudnessMatch.Format(plan);
        Assert.Contains("12.0 dB of limiting", report);
        Assert.Contains("Quiet side", report);
    }

    /// <summary>
    /// Loudness asks for a boost, the ceiling says the track is already over: it gets cut. Reporting
    /// that as "true-peak limited" alone would read as "left alone", which is the opposite.
    /// </summary>
    [Fact]
    public void ATrackAlreadyOverTheCeilingIsBroughtDownEvenWhereLoudnessAsksForABoost()
    {
        LoudnessMeasurement[] tracks = [Track("Hot transfer", -18, 0.4)];

        var step = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Target, Streaming).Steps[0];

        Assert.True(step.RequestedGainDb > 0);
        Assert.True(step.GainDb < 0, "a track above the ceiling was not brought down.");
        Assert.Contains("over the ceiling", step.Note);
    }

    [Fact]
    public void ASilentTrackIsSkippedRatherThanGivenInfiniteGain()
    {
        LoudnessMeasurement[] tracks =
        [
            Track("Programme", -18, -6),
            Track("Lead-out", double.NegativeInfinity, double.NegativeInfinity),
        ];

        var plan = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Target, Streaming);

        Assert.False(plan.Steps[1].CanApply);
        Assert.Equal(0, plan.Steps[1].GainDb);
        Assert.Contains("−70 LUFS", plan.Steps[1].Note);
        Assert.Contains("1 silent", plan.Summary);
    }

    [Fact]
    public void ASelectionOfNothingButSilenceProducesAPlanThatChangesNothing()
    {
        LoudnessMeasurement[] tracks =
        [
            Track("A", double.NegativeInfinity, double.NegativeInfinity),
            Track("B", double.NegativeInfinity, double.NegativeInfinity),
        ];

        foreach (var mode in Enum.GetValues<LoudnessMatchMode>())
        {
            var plan = LoudnessMatch.Plan(tracks, mode, Streaming, referenceIndex: 0);
            foreach (var step in plan.Steps)
            {
                Assert.False(step.CanApply);
                Assert.Equal(0, step.GainDb);
                Assert.False(double.IsNaN(step.GainDb));
            }
        }
    }

    [Fact]
    public void AnEmptySelectionIsAPlanThatSaysSoRatherThanAThrow()
    {
        var plan = LoudnessMatch.Plan([], LoudnessMatchMode.Average, Streaming);

        Assert.Empty(plan.Steps);
        Assert.Contains("nothing selected", plan.Summary);
    }

    [Fact]
    public void AReferenceModeWithNoReferenceChosenAppliesNothingAndSaysWhy()
    {
        LoudnessMeasurement[] tracks = [Track("A", -18), Track("B", -14)];

        var plan = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Reference, Streaming, referenceIndex: -1);

        Assert.All(plan.Steps, step => Assert.False(step.CanApply));
        Assert.Contains("no reference track chosen", plan.Summary);
    }

    [Fact]
    public void TheRelativeModesUseTheirOwnCeilingWhicheverPresetIsSelected()
    {
        LoudnessMeasurement[] tracks = [Track("A", -20, -2), Track("B", -12, -8)];

        foreach (var target in LoudnessTarget.All)
        {
            foreach (var mode in new[] { LoudnessMatchMode.Quietest, LoudnessMatchMode.Average })
            {
                var plan = LoudnessMatch.Plan(tracks, mode, target);
                Assert.Equal(LoudnessMatch.RelativeCeilingDbtp, plan.CeilingDbtp, 6);
                foreach (var step in plan.Steps)
                {
                    Assert.True(
                        step.ResultingTruePeakDbtp <= LoudnessMatch.RelativeCeilingDbtp + 1e-9,
                        $"{target.Name}/{mode} left {step.Measurement.Name} at "
                        + $"{step.ResultingTruePeakDbtp:0.00} dBTP.");
                }
            }
        }
    }

    /// <summary>
    /// Normalize Loudness and the batch converter both drive <see cref="LoudnessMatch.Plan"/> with
    /// a single measurement. That is not a special case in the code, but it is the only case those
    /// two ever exercise, so it is pinned apart from the multi-track modes above.
    /// </summary>
    [Fact]
    public void OneTrackWithHeadroomToSpareReachesTheTargetExactly()
    {
        var plan = LoudnessMatch.Plan([Track("only", -24, -18)], LoudnessMatchMode.Target, Streaming);

        LoudnessMatchStep step = Assert.Single(plan.Steps);
        Assert.True(step.CanApply);
        Assert.Equal(10, step.GainDb, 6);                    // −24 LUFS up to −14
        Assert.Equal(0, step.ShortfallDb, 6);
        Assert.Equal(Streaming.IntegratedLufs, step.ResultingLufs, 6);
        Assert.Equal(-8, step.ResultingTruePeakDbtp, 6);     // −18 + 10, still well under the ceiling
    }

    /// <summary>
    /// The batch converter's defect written as an assertion. A file whose peaks will not let it
    /// reach the target has to stop <b>at</b> the ceiling rather than above it, and has to say how
    /// far short that left it — the unattended path did neither.
    /// </summary>
    [Fact]
    public void OneTrackHeldBackByItsPeaksStopsAtTheCeilingAndSaysHowFarShort()
    {
        var plan = LoudnessMatch.Plan([Track("only", -24, -3)], LoudnessMatchMode.Target, Streaming);

        LoudnessMatchStep step = Assert.Single(plan.Steps);
        Assert.True(step.CanApply);
        Assert.Equal(2, step.GainDb, 6);                     // −3 dBTP plus 2 is exactly the ceiling
        Assert.Equal(8, step.ShortfallDb, 6);                // of the 10 dB loudness asked for
        Assert.Equal(Streaming.TruePeakDbtp, step.ResultingTruePeakDbtp, 6);
        Assert.Equal(-22, step.ResultingLufs, 6);            // short of −14, and not pretending otherwise
        Assert.Contains("true-peak limited", step.Note);
    }

    /// <summary>A file already where it was asked to be earns no gain, and so no undo entry.</summary>
    [Fact]
    public void OneTrackAlreadyAtTheTargetIsLeftAlone()
    {
        var plan = LoudnessMatch.Plan(
            [Track("only", Streaming.IntegratedLufs, -6)], LoudnessMatchMode.Target, Streaming);

        LoudnessMatchStep step = Assert.Single(plan.Steps);
        Assert.False(step.CanApply);
        Assert.Equal("already there", step.Note);
        Assert.Equal(0, step.GainDb, 6);
    }

    /// <summary>
    /// Every LUFS mode the batch converter offers is one of these presets, taken whole so the
    /// ceiling travels with the target. If one of them ever moved off −1 dBTP the unattended path
    /// would quietly start writing over the ceiling again, which is how it behaved before.
    /// </summary>
    [Theory]
    [InlineData(-16)]   // Apple Music, the converter's "−16 LUFS (streaming)"
    [InlineData(-14)]   // Spotify / YouTube, its "−14 LUFS (loud)"
    [InlineData(-23)]   // EBU R128, its "−23 LUFS (broadcast)"
    public void EveryTargetTheBatchConverterOffersReservesAnInterSamplePeak(double lufs)
    {
        LoudnessTarget target = LoudnessTarget.All.Single(t => t.IntegratedLufs == lufs);

        Assert.Equal(LoudnessMatch.RelativeCeilingDbtp, target.TruePeakDbtp, 6);

        // Given headroom, the preset is reached exactly; the ceiling is reserved, not spent.
        LoudnessMatchStep step = Assert.Single(
            LoudnessMatch.Plan([Track("t", -30, -20)], LoudnessMatchMode.Target, target).Steps);
        Assert.Equal(lufs, step.ResultingLufs, 6);
        Assert.True(step.ResultingTruePeakDbtp <= target.TruePeakDbtp + 1e-9);
    }

    [Fact]
    public void ThePlanIsAPureFunctionOfItsInputs()
    {
        LoudnessMeasurement[] tracks = [Track("A", -20), Track("B", -12)];

        var first = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Average, Streaming);
        var second = LoudnessMatch.Plan(tracks, LoudnessMatchMode.Average, Streaming);

        Assert.Equal(first.TargetLufs, second.TargetLufs, 12);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Steps, second.Steps);
    }

    /// <summary>
    /// The measurement is the compliance meter, not a second implementation of it. If the two ever
    /// drift apart, the report and the compliance dialog will disagree about the same file.
    /// </summary>
    [Fact]
    public void MeasuringAgreesWithTheComplianceMeterOnTheSameBuffer()
    {
        const int rate = 44_100;
        var channels = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            channels[c] = new float[rate * 4];
            for (int i = 0; i < channels[c].Length; i++)
                channels[c][i] = 0.25f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        }

        var report = LoudnessCompliance.Measure(channels, rate, Streaming);
        var measurement = LoudnessMatch.Measure("Tone", channels, rate, Streaming);

        Assert.Equal(report.IntegratedLufs, measurement.IntegratedLufs, 9);
        Assert.Equal(report.TruePeakDbtp, measurement.TruePeakDbtp, 9);
        Assert.Equal(report.LoudnessRangeLu, measurement.LoudnessRangeLu, 9);
        Assert.Equal(rate, measurement.SampleRate);
        Assert.Equal(rate * 4, measurement.Frames);
    }

    [Fact]
    public void TheReportNamesEveryTrackAndTheGainItGot()
    {
        LoudnessMeasurement[] tracks = [Track("Side one", -20, -12), Track("Side two", -11, -5)];

        string report = LoudnessMatch.Format(LoudnessMatch.Plan(tracks, LoudnessMatchMode.Target, Streaming));

        Assert.Contains("Side one", report);
        Assert.Contains("Side two", report);
        Assert.Contains("+6.0", report);
        Assert.Contains("-3.0", report);
    }
}

/// <summary>
/// The undo entry Match Loudness leaves behind, which is the durable half of "show what has been
/// applied": the panel reads it back long after the dialog has gone.
/// </summary>
public sealed class MatchLoudnessEditTests
{
    private static AudioDocument Document()
    {
        var channels = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            channels[c] = new float[1_000];
            Array.Fill(channels[c], 0.25f);
        }
        return new AudioDocument(channels, 44_100, 32);
    }

    [Fact]
    public void MatchLoudnessLeavesOneUndoEntryNamingWhatItMatchedTo()
    {
        var document = Document();

        Commit(document, 2.3, -14);

        Assert.Equal(1, document.HistoryCount);
        Assert.Equal("Match Loudness -14.0 LUFS (+2.3 dB)", document.NextUndoName);
    }

    [Fact]
    public void MatchLoudnessAppliesExactlyTheGainItWasGivenAndUndoesCleanly()
    {
        var document = Document();
        float original = document.Channels[0][0];

        Commit(document, -6, -14);
        Assert.Equal(original * MathF.Pow(10, -6f / 20f), document.Channels[0][0], 5);

        document.Undo();
        Assert.Equal(original, document.Channels[0][0], 6);
    }

    /// <summary>
    /// Scaling on the way into the new buffer, and committing by taking ownership, is what keeps the
    /// whole edit to one copy of the document. The old route through <c>Apply</c> made three, on the
    /// dispatcher, for every open tab at once — the same defect the channel tools were moved off the
    /// UI thread to fix.
    /// </summary>
    [Fact]
    public void CommittingAMatchRetainsTheOutgoingSamplesRatherThanCopyingThem()
    {
        var document = Document();
        var before = document.Channels[0];

        var scaled = Processing.MatchLoudnessData(document.Channels, -6);
        Assert.NotSame(before, scaled[0]);
        // The source is read, never written: the caller still holds valid audio if the commit is
        // abandoned.
        Assert.Equal(0.25f, before[0], 6);

        document.ReplaceAllOwned(scaled, Processing.MatchLoudnessName(-6, -14));
        Assert.Same(scaled[0], document.Channels[0]);

        document.Undo();
        Assert.Same(before, document.Channels[0]);
    }

    [Fact]
    public void ScalingAWholeDocumentCanBeCancelled()
    {
        var channels = new float[2][];
        for (int c = 0; c < 2; c++) channels[c] = new float[500_000];
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Processing.MatchLoudnessData(channels, -6, cancelled.Token));
    }

    private static void Commit(AudioDocument document, double gainDb, double targetLufs) =>
        document.ReplaceAllOwned(
            Processing.MatchLoudnessData(document.Channels, gainDb),
            Processing.MatchLoudnessName(gainDb, targetLufs));

    /// <summary>
    /// Guards the extraction of the shared gain loop: the status line and
    /// <see cref="GuiActionStatusTests"/> both depend on this exact string.
    /// </summary>
    [Fact]
    public void TheOrdinaryGainCommandStillNamesItselfTheWayItAlwaysDid()
    {
        var document = Document();

        Processing.Gain(document, 0, document.Length, 3);

        Assert.Equal("Gain +3.0 dB", document.NextUndoName);
    }
}
