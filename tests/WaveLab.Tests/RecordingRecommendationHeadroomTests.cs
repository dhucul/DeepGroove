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
    public void ARecommendedReductionPlacesTheMeasuredPassageAtTheTarget(double ceiling, bool ignoreClicks)
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

        Assert.Equal(0, advice.ReserveDb);
        // Lower the physical signal and replay the same observed passage.
        check.Reset();
        Feed(check, Tone(-0.25 + advice.SuggestedGainDb), 15);
        RecordingLevelSnapshot recording = check.GetFreshSnapshot();
        Assert.Equal(0, recording.ClippedSamples);
        Assert.True(recording.TruePeakDb < 0);
        Assert.InRange(recording.TruePeakDb, ceiling - 0.5, ceiling + 1e-6);
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
        // The observed rise is included once, without any additional margin.
        Assert.Equal(0, result.ReserveDb);
        Assert.Equal(result.TruePeakDb, result.ProjectedPeakDb, 6);
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
        Assert.Equal(strict.SuggestedGainDb - 5, lowerCeiling.SuggestedGainDb, 6);

        analyzer.IgnorePopsAndClicks = true;
        RecordingLevelSnapshot ignoredAgain = analyzer.Snapshot;
        Assert.True(ignoredAgain.HasOnlyClickClipping);
        Assert.NotEqual(RecordingLevelStatus.Clipping, ignoredAgain.Status);
        Assert.Equal(ignoring.SuggestedGainDb - 5, ignoredAgain.SuggestedGainDb, 6);

        analyzer.Reset();
        Assert.Equal(-6, analyzer.TargetCeilingDb);
        Assert.True(analyzer.IgnorePopsAndClicks);
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, analyzer.Snapshot.Status);
        Assert.Equal(0, analyzer.Snapshot.ClippedSamples);
        Assert.Equal(0, analyzer.Snapshot.SuggestedGainDb);
    }

    [Fact]
    public void BothLiveAndFinishedWholeSongAdviceUseTheMeasuredPeak()
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
        Assert.Equal(0, provisional.ReserveDb);
        Assert.False(provisional.IsCompletedFullScan);

        RecordingLevelSnapshot completed = analyzer.GetCompletedScanSnapshot();
        Assert.True(completed.IsCompletedFullScan);
        Assert.Equal(0, completed.ReserveDb);
        Assert.Equal(provisional.ActiveSeconds, completed.ActiveSeconds);
        Assert.Equal(provisional.TruePeakDb, completed.TruePeakDb, 6);
        Assert.Equal(provisional.SuggestedGainDb, completed.SuggestedGainDb);
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
    public void FinishingAShortCheckDoesNotAddAReserve()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1) { TargetCeilingDb = -1 };
        Feed(analyzer, Tone(-7), 30);
        RecordingLevelSnapshot completed = analyzer.GetCompletedScanSnapshot();
        Assert.False(completed.IsCompletedFullScan);
        Assert.Equal(0, completed.ReserveDb);
    }

    [Fact]
    public void AnIncompleteHistoryCannotBeFinalizedAsACompletePass()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1);
        Feed(analyzer, Tone(-7), 121); // the first second has left the rolling window
        analyzer.FullDurationScanEnabled = true;
        RecordingLevelSnapshot incomplete = analyzer.GetCompletedScanSnapshot();
        Assert.False(incomplete.IsCompletedFullScan);
        Assert.Equal(0, incomplete.ReserveDb);

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

    [Fact]
    public void ActualConverterClippingNeverProducesAnIncreaseAfterFineTrim()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1);
        const double FineGain = 0.1;
        float[] source = Tone(6);
        long clipped = source.LongCount(value => Math.Abs(value) >= 0.999969);
        var attenuated = source.Select(value => (float)(Math.Clamp(value, -1, 1) * FineGain)).ToArray();
        for (int i = 0; i < 12; i++)
            analyzer.Process(attenuated, 0, attenuated.Length, clipped, FineGain);

        var result = analyzer.GetFreshSnapshot();
        Assert.Equal(RecordingLevelStatus.Clipping, result.Status);
        Assert.True(result.TruePeakDb < -10);
        Assert.True(result.ClippedSamples > 0);
        Assert.True(result.SuggestedGainDb <= -1);
        Assert.Equal(0, result.ReserveDb);
    }

    [Fact]
    public void ApplyingTheMeasuredAdjustmentDoesNotAskForAnotherReductionOnTheSameSignal()
    {
        var analyzer = new RecordingLevelAnalyzer(Rate, 1) { TargetCeilingDb = -1 };
        float[] signal = Tone(-7);
        Feed(analyzer, signal, 12);
        var advice = analyzer.GetFreshSnapshot();
        double gain = Math.Pow(10, advice.SuggestedGainDb / 20);
        var adjusted = signal.Select(value => (float)(value * gain)).ToArray();
        analyzer.Reset();
        Feed(analyzer, adjusted, 12);
        var checkedAgain = analyzer.GetFreshSnapshot();
        Assert.Equal(0, checkedAgain.SuggestedGainDb);
        Assert.Equal(RecordingLevelStatus.Good, checkedAgain.Status);
        Assert.Equal(0, checkedAgain.ClippedSamples);
        Assert.InRange(checkedAgain.TruePeakDb, -1.5, -1 + 1e-6);
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
