using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class RecordingRecommendationHeadroomTests
{
    private const int Rate = 8_000;

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(-3, false)]
    [InlineData(-3, true)]
    public void ARecommendedReductionRetainsReserveForALouderPassage(double ceiling, bool ignoreClicks)
    {
        var check = new RecordingLevelAnalyzer(Rate, 1)
        {
            TargetCeilingDb = ceiling,
            IgnorePopsAndClicks = ignoreClicks,
        };
        Feed(check, Tone(-0.25), 60);
        RecordingLevelSnapshot advice = check.GetFreshSnapshot();
        Assert.True(advice.SuggestedGainDb < 0);
        Assert.True(advice.ProjectedPeakDb + advice.SuggestedGainDb <= ceiling + 1e-6,
            $"Reduction {advice.SuggestedGainDb:0.0} dB leaves projected peak "
            + $"{advice.ProjectedPeakDb + advice.SuggestedGainDb:0.00}, above ceiling {ceiling}");

        // Lower the physical signal, then record. The middle passage is 2 dB
        // louder than the scan, within its advertised 3 dB reserve.
        check.Reset();
        Feed(check, Tone(-0.25 + advice.SuggestedGainDb), 15);
        Feed(check, Tone(-0.25 + 2 + advice.SuggestedGainDb), 1);
        RecordingLevelSnapshot recording = check.GetFreshSnapshot();
        Assert.Equal(0, recording.ClippedSamples);
        Assert.True(recording.TruePeakDb < 0);
        Assert.NotEqual(RecordingLevelStatus.Clipping, recording.Status);
        Assert.NotEqual(RecordingLevelStatus.UpstreamClipping, recording.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdviceCoversTheAbsolutePeakWheneverClicksAreNotExempt(bool wholeSide)
    {
        var check = new RecordingLevelAnalyzer(Rate, 1)
        {
            TargetCeilingDb = -1,
            FullDurationScanEnabled = wholeSide,
        };
        float[] quiet = Tone(-3);
        float[] signal = Tone(-3);
        // A rare peak falls outside the rolling 99th percentile but is less
        // than 6 dB above the programme, so it is not exempt from warnings.
        for (int i = 600; i < 603; i++) signal[i] = 0.999f;
        for (int i = 0; i < 60; i++) check.Process(i == 30 ? signal : quiet);
        RecordingLevelSnapshot advice = check.GetFreshSnapshot();
        Assert.False(advice.HasOnlyClickClipping);
        Assert.True(advice.TruePeakDb > 0);
        if (!wholeSide) Assert.True(advice.TruePeakDb - advice.ProgramPeakDb > 2);
        Assert.True(advice.TruePeakDb + advice.ReserveDb + advice.SuggestedGainDb <= -1 + 1e-6);

        double gain = Math.Pow(10, advice.SuggestedGainDb / 20);
        float[] lowered = signal.Select(value => (float)(value * gain)).ToArray();
        float[] loweredQuiet = quiet.Select(value => (float)(value * gain)).ToArray();
        check.Reset();
        for (int i = 0; i < 60; i++) check.Process(i == 30 ? lowered : loweredQuiet);
        RecordingLevelSnapshot recording = check.GetFreshSnapshot();
        Assert.True(recording.TruePeakDb < 0);
        Assert.Equal(0, recording.ClippedSamples);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnAbsolutePeakAlreadyIncludesTheRarePassage(bool ignoreClicks, bool wholeSide)
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1)
        {
            IgnorePopsAndClicks = ignoreClicks,
            FullDurationScanEnabled = wholeSide,
        };
        float[] quiet = Tone(-8);
        float[] loud = Tone(-4);
        var burst = (float[])quiet.Clone();
        Array.Copy(loud, burst, Rate / 100); // 10 ms of music, not a narrow click
        analyzer.Process(burst);
        Feed(analyzer, quiet, 59);

        RecordingLevelSnapshot result = analyzer.GetFreshSnapshot();
        Assert.False(result.HasOnlyClickClipping);
        if (!wholeSide) Assert.True(result.TruePeakDb - result.ProgramPeakDb > 3.5);
        // The absolute peak already covers the 4 dB rise. Add only the 3 dB
        // time reserve, rather than adding that same observed rise again.
        Assert.Equal(3, result.ReserveDb, 6);
        Assert.Equal(result.TruePeakDb + 3, result.ProjectedPeakDb, 6);
        Assert.True(result.ProjectedPeakDb + result.SuggestedGainDb <= analyzer.TargetCeilingDb + 1e-6);
    }

    [Fact]
    public void PolicyAndCeilingChangesRecalculateTheExistingScanBeforeAReset()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1) { FullDurationScanEnabled = true };
        float[] clicked = Tone(-12);
        clicked[600] = 1;
        Feed(analyzer, clicked, 60);
        RecordingLevelSnapshot ignoring = analyzer.GetFreshSnapshot();
        Assert.True(ignoring.HasOnlyClickClipping);
        Assert.NotEqual(RecordingLevelStatus.Clipping, ignoring.Status);

        analyzer.IgnorePopsAndClicks = false;
        RecordingLevelSnapshot strict = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.Clipping, strict.Status);
        Assert.True(strict.SuggestedGainDb < ignoring.SuggestedGainDb);
        Assert.Equal(ignoring.ActiveSeconds, strict.ActiveSeconds);
        Assert.Equal(ignoring.ClippedSamples, strict.ClippedSamples);

        analyzer.TargetCeilingDb = -6;
        RecordingLevelSnapshot lowerCeiling = analyzer.Snapshot;
        Assert.Equal(strict.SuggestedGainDb - 3, lowerCeiling.SuggestedGainDb, 6);

        analyzer.IgnorePopsAndClicks = true;
        RecordingLevelSnapshot ignoredAgain = analyzer.Snapshot;
        Assert.True(ignoredAgain.HasOnlyClickClipping);
        Assert.NotEqual(RecordingLevelStatus.Clipping, ignoredAgain.Status);
        Assert.Equal(ignoring.SuggestedGainDb - 3, ignoredAgain.SuggestedGainDb, 6);

        analyzer.Reset();
        Assert.Equal(-6, analyzer.TargetCeilingDb);
        Assert.True(analyzer.IgnorePopsAndClicks);
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, analyzer.Snapshot.Status);
        Assert.Equal(0, analyzer.Snapshot.ClippedSamples);
        Assert.Equal(0, analyzer.Snapshot.SuggestedGainDb);
    }

    [Fact]
    public void FinishedWholeSongUsesTheMeasuredPeakInsteadOfUnheardPassageReserve()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1)
        {
            FullDurationScanEnabled = true,
            TargetCeilingDb = -1,
        };
        Feed(analyzer, Tone(-18), 30);
        Feed(analyzer, Tone(-7), 2);
        Feed(analyzer, Tone(-18), 30);
        RecordingLevelSnapshot provisional = analyzer.GetFreshSnapshot();
        Assert.True(provisional.ReserveDb >= 2);
        Assert.False(provisional.IsCompletedFullScan);

        RecordingLevelSnapshot completed = analyzer.GetCompletedScanSnapshot();
        Assert.True(completed.IsCompletedFullScan);
        Assert.Equal(0, completed.ReserveDb);
        Assert.Equal(provisional.ActiveSeconds, completed.ActiveSeconds);
        Assert.Equal(provisional.TruePeakDb, completed.TruePeakDb, 6);
        Assert.True(completed.SuggestedGainDb > provisional.SuggestedGainDb);
        Assert.InRange(completed.TruePeakDb + completed.SuggestedGainDb, -1.5, -1 + 1e-6);

        // Rewind and record the same whole song at the final recommended gain.
        analyzer.Reset();
        Assert.False(analyzer.Snapshot.IsCompletedFullScan);
        Feed(analyzer, Tone(-18 + completed.SuggestedGainDb), 30);
        Feed(analyzer, Tone(-7 + completed.SuggestedGainDb), 2);
        Feed(analyzer, Tone(-18 + completed.SuggestedGainDb), 30);
        RecordingLevelSnapshot recorded = analyzer.GetFreshSnapshot();
        Assert.InRange(recorded.TruePeakDb, -1.5, -1 + 1e-6);
        Assert.Equal(0, recorded.ClippedSamples);
    }

    [Fact]
    public void FinishingAShortCheckDoesNotRemoveItsReserve()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1) { TargetCeilingDb = -1 };
        Feed(analyzer, Tone(-7), 30);
        RecordingLevelSnapshot completed = analyzer.GetCompletedScanSnapshot();
        Assert.False(completed.IsCompletedFullScan);
        Assert.Equal(4, completed.ReserveDb, 6);
    }

    [Fact]
    public void AnIncompleteHistoryCannotBeFinalizedAsACompletePass()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1);
        Feed(analyzer, Tone(-7), 121); // the first second has left the rolling window
        analyzer.FullDurationScanEnabled = true;
        RecordingLevelSnapshot incomplete = analyzer.GetCompletedScanSnapshot();
        Assert.False(incomplete.IsCompletedFullScan);
        Assert.True(incomplete.ReserveDb >= 2);

        analyzer.Reset();
        Feed(analyzer, Tone(-7), 12);
        Assert.True(analyzer.GetCompletedScanSnapshot().IsCompletedFullScan);
    }

    [Fact]
    public void AFinishedPassWithTooLittleMusicRemainsProvisional()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1) { FullDurationScanEnabled = true };
        Feed(analyzer, Tone(-7), 9);
        RecordingLevelSnapshot result = analyzer.GetCompletedScanSnapshot();
        Assert.False(result.IsCompletedFullScan);
        Assert.Equal(RecordingLevelStatus.Analyzing, result.Status);
        Assert.Equal(0, result.SuggestedGainDb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinishedPassStillHonorsClickPolicyAndSustainedOverload(bool ignoreClicks)
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1)
        {
            FullDurationScanEnabled = true,
            IgnorePopsAndClicks = ignoreClicks,
        };
        float[] clicked = Tone(-12);
        clicked[600] = 1;
        Feed(analyzer, clicked, 12);
        RecordingLevelSnapshot clicks = analyzer.GetCompletedScanSnapshot();
        Assert.True(clicks.IsCompletedFullScan);
        Assert.True(clicks.HasOnlyClickClipping);
        Assert.Equal(ignoreClicks, clicks.Status != RecordingLevelStatus.Clipping);

        analyzer.Reset();
        float[] overloaded = Tone(-12);
        for (int i = 600; i < 640; i++) overloaded[i] = 1;
        Feed(analyzer, overloaded, 12);
        RecordingLevelSnapshot overload = analyzer.GetCompletedScanSnapshot();
        Assert.True(overload.IsCompletedFullScan);
        Assert.False(overload.HasOnlyClickClipping);
        Assert.Equal(RecordingLevelStatus.Clipping, overload.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinishedPassProtectsALoudTailTooShortForBlockClassification(bool ignoreClicks)
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1)
        {
            FullDurationScanEnabled = true,
            IgnorePopsAndClicks = ignoreClicks,
            TargetCeilingDb = -1,
        };
        Feed(analyzer, Tone(-18), 12);
        analyzer.Process(Tone(-3)[..(Rate / 50)]); // 20 ms: below the 25 ms block cutoff
        RecordingLevelSnapshot completed = analyzer.GetCompletedScanSnapshot();
        Assert.True(completed.IsCompletedFullScan);
        Assert.InRange(completed.TruePeakDb, -3.1, -2.8);
        Assert.True(completed.TruePeakDb + completed.SuggestedGainDb <= -1 + 1e-6,
            $"Final advice adds {completed.SuggestedGainDb} dB to a {completed.TruePeakDb:0.00} dBTP tail");

        analyzer.Reset();
        Feed(analyzer, Tone(-18 + completed.SuggestedGainDb), 12);
        analyzer.Process(Tone(-3 + completed.SuggestedGainDb)[..(Rate / 50)]);
        var recording = analyzer.GetFreshSnapshot();
        Assert.True(recording.TruePeakDb < 0);
        Assert.Equal(0, recording.ClippedSamples);
    }

    private static float[] Tone(double peakDb)
    {
        double amplitude = Math.Pow(10, peakDb / 20);
        return Enumerable.Range(0, Rate)
            .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 100 * i / Rate))).ToArray();
    }

    private static void Feed(RecordingLevelAnalyzer analyzer, float[] signal, int seconds)
    {
        for (int i = 0; i < seconds; i++) analyzer.Process(signal);
    }
}
