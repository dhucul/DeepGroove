using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class RecordingLevelAnalyzerTests
{
    private const int SampleRate = 8_000;

    [Fact]
    public void SilenceRemainsWaitingWithoutInventingARecommendation()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 2);

        FeedRepeated(analyzer, new float[SampleRate * 2], seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, result.Status);
        Assert.Equal(12, result.ElapsedSeconds, 8);
        Assert.Equal(0, result.ActiveSeconds);
        Assert.Equal(0, result.Confidence);
        Assert.True(double.IsNegativeInfinity(result.PeakLeftDb));
        Assert.True(double.IsNegativeInfinity(result.PeakRightDb));
        Assert.True(double.IsNegativeInfinity(result.TruePeakDb));
        Assert.True(double.IsNegativeInfinity(result.ProjectedPeakDb));
        Assert.True(double.IsNegativeInfinity(result.ProgramRmsDb));
        Assert.True(double.IsNegativeInfinity(result.NoiseFloorDb));
        Assert.True(double.IsNaN(result.CrestFactorDb));
        Assert.Equal(0, result.ReserveDb);
        Assert.Equal(0, result.SuggestedGainDb);
    }

    [Fact]
    public void SteadyProgramIsNotMistakenForItsOwnNoiseFloor()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 2);
        float[] second = SineSecond(leftPeakDb: -12, rightPeakDb: -12);

        FeedRepeated(analyzer, second, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.TooLow, result.Status);
        Assert.Equal(12, result.ActiveSeconds, 8);
        Assert.True(double.IsNaN(result.NoiseFloorDb));
        Assert.InRange(result.ProgramRmsDb, -15.02, -15.00);
        Assert.InRange(result.CrestFactorDb, 2.99, 3.03);
        Assert.Equal(0, result.FlatTopCount);
        Assert.True(result.Confidence > 0);
        Assert.True(result.SuggestedGainDb > 1);
    }

    [Fact]
    public void ExactDigitalSilenceBeforeProgramStillProducesActiveProgrammeTime()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        FeedRepeated(analyzer, new float[SampleRate], seconds: 5);

        FeedRepeated(analyzer, SineSecond(-9), seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(17, result.ElapsedSeconds, 8);
        Assert.Equal(12, result.ActiveSeconds, 8);
        Assert.True(double.IsNegativeInfinity(result.NoiseFloorDb));
        Assert.NotEqual(RecordingLevelStatus.WaitingForSignal, result.Status);
    }

    [Fact]
    public void AShortValidPassageIsAnalyzingButHasNoSettledGainAdvice()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);

        FeedRepeated(analyzer, SineSecond(-9), seconds: 9);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.Analyzing, result.Status);
        Assert.Equal(9, result.ActiveSeconds, 8);
        Assert.Equal(0, result.ReserveDb);
        Assert.Equal(0, result.SuggestedGainDb);
        Assert.Equal(1 - Math.Exp(-9 / 30.0), result.Confidence, 10);
    }

    [Theory]
    [InlineData(10, 6)]
    [InlineData(30, 4)]
    [InlineData(60, 3)]
    [InlineData(120, 2)]
    public void ReserveFallsAsRepresentativeActiveTimeGrows(int seconds, double expectedReserve)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);

        FeedRepeated(analyzer, SineSecond(-6), seconds);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(seconds, result.ActiveSeconds, 8);
        Assert.Equal(expectedReserve, result.ReserveDb, 8);
        Assert.Equal(Math.Min(0.95, 1 - Math.Exp(-seconds / 30.0)), result.Confidence, 10);
    }

    [Fact]
    public void LongSessionsKeepOnlyTheMostRecentTwoMinutesOfProgrammeStatistics()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);

        FeedRepeated(analyzer, SineSecond(-6), seconds: 130);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(130, result.ElapsedSeconds, 8);
        Assert.Equal(120, result.ActiveSeconds, 8);
        Assert.Equal(2, result.ReserveDb, 8);
        Assert.Equal(0.95, result.Confidence, 10);
    }

    [Theory]
    [InlineData(-12, RecordingLevelStatus.TooLow, 6)]
    [InlineData(-6, RecordingLevelStatus.Good, 0)]
    [InlineData(-1, RecordingLevelStatus.Hot, -2)]
    public void SixtySecondScanKeepsTheMeasuredProgrammeInASafeRange(
        double peakDb,
        RecordingLevelStatus expectedStatus,
        double expectedGain)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);

        FeedRepeated(analyzer, SineSecond(peakDb), seconds: 60);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(expectedStatus, result.Status);
        Assert.InRange(result.TruePeakDb, peakDb - 0.02, peakDb + 0.02);
        Assert.Equal(result.TruePeakDb + 3, result.ProjectedPeakDb, 10);
        Assert.Equal(expectedGain, result.SuggestedGainDb);
    }

    [Fact]
    public void SafetyReserveDoesNotForceAnAlreadySafeProgrammeLower()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);

        FeedRepeated(analyzer, SineSecond(-6), seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.True(result.ReserveDb > 5);
        Assert.True(result.ProjectedPeakDb > -1);
        Assert.Equal(RecordingLevelStatus.Good, result.Status);
        Assert.Equal(0, result.SuggestedGainDb);
    }

    [Fact]
    public void FullDurationScanRetainsAnEarlyLoudPassageBeyondTheRollingWindow()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1)
        {
            FullDurationScanEnabled = true,
        };

        analyzer.Process(SineSecond(-3));
        FeedRepeated(analyzer, SineSecond(-12), seconds: 121);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(122, result.ElapsedSeconds, 8);
        Assert.InRange(result.ProgramPeakDb, -3.4, -2.9);
        Assert.Equal(RecordingLevelStatus.Good, result.Status);
    }

    [Fact]
    public void FreshFullDurationSnapshotIncludesASubSecondTail()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1)
        {
            FullDurationScanEnabled = true,
        };
        FeedRepeated(analyzer, SineSecond(-12), seconds: 12);
        RecordingLevelSnapshot cached = analyzer.Snapshot;

        float[] loudTail = SineSecond(-1)[..(SampleRate / 2)];
        analyzer.Process(loudTail);

        Assert.Same(cached, analyzer.Snapshot);
        RecordingLevelSnapshot final = analyzer.GetFreshSnapshot();
        Assert.Equal(12.5, final.ElapsedSeconds, 8);
        Assert.True(final.ProgramPeakDb > -2,
            $"Expected the final loud tail in the programme peak, got {final.ProgramPeakDb:0.0} dBTP");
        Assert.Equal(RecordingLevelStatus.Hot, final.Status);
    }

    [Fact]
    public void BroadDigitalFullScaleEventLatchesClippingImmediately()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] data = SineSecond(-12);
        for (int frame = 400; frame < 900; frame++)
            data[frame] = frame % 2 == 0 ? 1.0f : -1.0f;

        analyzer.Process(data);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.Clipping, result.Status);
        Assert.Equal(500, result.ClippedSamples);
        Assert.True(result.TruePeakDb >= 0);
    }

    [Fact]
    public void SourceClippingRemainsLatchedAfterFineAttenuation()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] attenuated = [0.5f, -0.5f];

        analyzer.Process(attenuated, 0, attenuated.Length, sourceClippedSamples: 2);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.Clipping, result.Status);
        Assert.Equal(2, result.ClippedSamples);
        Assert.InRange(result.TruePeakDb, -6.03, -6.01);
    }

    [Fact]
    public void IntersampleOverIsHotButNotDigitalClipping()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        var signal = new float[64];
        for (int frame = 0; frame < signal.Length; frame++)
        {
            // The sample points stay below full scale while the underlying
            // quarter-rate sine reaches 1.05 between them.
            signal[frame] = (float)(1.05 * Math.Sin(
                2 * Math.PI * 2_000 * frame / SampleRate + Math.PI / 4));
        }

        analyzer.Process(signal);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.Hot, result.Status);
        Assert.Equal(0, result.ClippedSamples);
        Assert.True(result.TruePeakDb > 0);
    }

    [Fact]
    public void CaptureStartingOnHotSamplesDoesNotInterpolateAgainstResetPadding()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);

        analyzer.Process([0.95f, 0.95f, 0.95f, 0.95f]);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        double expectedPeakDb = 20 * Math.Log10(0.95);
        Assert.Equal(0, result.ClippedSamples);
        Assert.NotEqual(RecordingLevelStatus.Clipping, result.Status);
        Assert.InRange(result.TruePeakDb, expectedPeakDb - 0.001, expectedPeakDb + 0.001);
        Assert.True(result.TruePeakDb < 0);
    }

    [Fact]
    public void SubFullScaleFlatTopsAreReportedAsUpstreamClipping()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] second = SineSecond(-20);
        for (int start = 40; start + 5 < second.Length; start += 80)
        {
            second[start] = 0.20f;
            second[start + 1] = 0.50f;
            second[start + 2] = 0.50f;
            second[start + 3] = 0.50f;
            second[start + 4] = 0.20f;
        }

        FeedRepeated(analyzer, second, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.UpstreamClipping, result.Status);
        Assert.Equal(0, result.ClippedSamples);
        Assert.True(result.FlatTopCount > 0);
        Assert.True(result.PeakLeftDb < -5.9);
    }

    [Fact]
    public void OneIsolatedPlateauDoesNotLatchUpstreamClipping()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] firstSecond = SineSecond(-12);
        firstSecond[400] = 0.20f;
        firstSecond[401] = 0.50f;
        firstSecond[402] = 0.50f;
        firstSecond[403] = 0.50f;
        firstSecond[404] = 0.20f;

        analyzer.Process(firstSecond);
        FeedRepeated(analyzer, SineSecond(-12), seconds: 11);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(1, result.FlatTopCount);
        Assert.Equal(0, result.ClippedSamples);
        Assert.NotEqual(RecordingLevelStatus.UpstreamClipping, result.Status);
    }

    [Fact]
    public void BalanceIsLeftMinusRightActiveRms()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 2);

        FeedRepeated(analyzer, SineSecond(leftPeakDb: -7, rightPeakDb: -11), seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.InRange(result.BalanceDb, 3.98, 4.02);
        Assert.InRange(result.PeakLeftDb, -7.01, -6.99);
        Assert.InRange(result.PeakRightDb, -11.01, -10.99);
    }

    [Theory]
    [InlineData(true, 60)]
    [InlineData(false, -60)]
    public void DeadChannelBalanceSignIdentifiesTheLiveSide(bool leftIsLive, double expectedBalanceDb)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 2);
        float[] live = SineSecond(-12);
        var stereo = new float[SampleRate * 2];
        for (int frame = 0; frame < SampleRate; frame++)
            stereo[frame * 2 + (leftIsLive ? 0 : 1)] = live[frame];

        FeedRepeated(analyzer, stereo, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(12, result.ActiveSeconds, 8);
        Assert.Equal(expectedBalanceDb, result.BalanceDb);
        Assert.True(double.IsNegativeInfinity(leftIsLive ? result.PeakRightDb : result.PeakLeftDb));
        Assert.InRange(leftIsLive ? result.PeakLeftDb : result.PeakRightDb, -12.01, -11.99);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoudSteadyDcOrHumRemainsWaitingWithoutPositiveGainAdvice(bool hum)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        var second = new float[SampleRate];
        const double amplitude = 0.05; // about -26 dBFS peak: above the raw program gate
        for (int frame = 0; frame < second.Length; frame++)
        {
            second[frame] = hum
                ? (float)(amplitude * Math.Sin(2 * Math.PI * 60 * frame / SampleRate))
                : (float)amplitude;
        }

        FeedRepeated(analyzer, second, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, result.Status);
        Assert.Equal(0, result.ActiveSeconds);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(0, result.ReserveDb);
        Assert.Equal(0, result.SuggestedGainDb);
        Assert.Equal(0, result.FlatTopCount);
    }

    [Fact]
    public void SteadyBroadbandNoiseDoesNotBecomeProgramOrInviteGain()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        var random = new Random(7342);
        var second = new float[SampleRate];
        for (int frame = 0; frame < second.Length; frame++)
            second[frame] = (float)((random.NextDouble() * 2 - 1) * 0.05);

        FeedRepeated(analyzer, second, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, result.Status);
        Assert.Equal(0, result.ActiveSeconds);
        Assert.Equal(0, result.SuggestedGainDb);
    }

    [Fact]
    public void FlatTopsOutsideTheEvidenceWindowDoNotAccumulateIntoAWarning()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        for (int occurrence = 0; occurrence < 3; occurrence++)
        {
            float[] eventSecond = SineSecond(-12);
            eventSecond[400] = 0.20f;
            eventSecond[401] = 0.50f;
            eventSecond[402] = 0.50f;
            eventSecond[403] = 0.50f;
            eventSecond[404] = 0.20f;
            analyzer.Process(eventSecond);
            FeedRepeated(analyzer, SineSecond(-12), seconds: 11);
        }

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(3, result.FlatTopCount);
        Assert.NotEqual(RecordingLevelStatus.UpstreamClipping, result.Status);
    }

    [Fact]
    public void Bs1770FourPhaseFilterUsesThePublishedDcGain()
    {
        var meter = new LoudnessMeter();
        meter.Configure(48_000, 1);
        meter.Process(Enumerable.Repeat(0.5f, 64).ToArray(), 0, 64);

        const double maximumPhaseGain = 1.0015869140625;
        double expected = 20 * Math.Log10(0.5 * maximumPhaseGain);
        Assert.Equal(expected, meter.TruePeakDb, 10);
    }

    [Fact]
    public void TruePeakAndAllStreamingResultsAreIndependentOfCallbackChunking()
    {
        float[] signal = StereoSignal(seconds: 12, frequency: 3_517, amplitude: 0.72, phase: 0.37);
        var whole = new RecordingLevelAnalyzer(SampleRate, 2);
        var chunked = new RecordingLevelAnalyzer(SampleRate, 2);

        whole.Process(signal);
        int[] frameChunks = [1, 7, 64, 511, 3, 127, 997];
        int frame = 0;
        int chunkIndex = 0;
        int totalFrames = signal.Length / 2;
        while (frame < totalFrames)
        {
            int frames = Math.Min(frameChunks[chunkIndex++ % frameChunks.Length], totalFrames - frame);
            chunked.Process(signal, frame * 2, frames * 2);
            frame += frames;
        }

        Assert.Equal(whole.Snapshot, chunked.Snapshot);
    }

    [Fact]
    public void InvalidSamplesAreCountedAndOtherwiseBehaveAsSilence()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 2);
        var signal = new float[SampleRate * 2 / 10];
        signal[10] = float.NaN;
        signal[33] = float.PositiveInfinity;
        signal[94] = float.NegativeInfinity;

        analyzer.Process(signal);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(3, result.InvalidSamples);
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, result.Status);
        Assert.True(double.IsNegativeInfinity(result.PeakLeftDb));
        Assert.True(double.IsNegativeInfinity(result.PeakRightDb));
        Assert.True(double.IsNegativeInfinity(result.TruePeakDb));
        Assert.Equal(0, result.ClippedSamples);
        Assert.Equal(0, result.FlatTopCount);
    }

    [Fact]
    public void ResetClearsEveryLatchedAndAccumulatedMeasurement()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] data = SineSecond(-9);
        data[100] = 1;
        data[200] = float.NaN;
        analyzer.Process(data);

        analyzer.Reset();

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, result.Status);
        Assert.Equal(0, result.ElapsedSeconds);
        Assert.Equal(0, result.ActiveSeconds);
        Assert.Equal(0, result.Confidence);
        Assert.True(double.IsNegativeInfinity(result.PeakLeftDb));
        Assert.True(double.IsNegativeInfinity(result.TruePeakDb));
        Assert.True(double.IsNegativeInfinity(result.ProjectedPeakDb));
        Assert.True(double.IsNegativeInfinity(result.ProgramRmsDb));
        Assert.True(double.IsNaN(result.NoiseFloorDb));
        Assert.True(double.IsNaN(result.CrestFactorDb));
        Assert.Equal(0, result.BalanceDb);
        Assert.Equal(0, result.ReserveDb);
        Assert.Equal(0, result.SuggestedGainDb);
        Assert.Equal(0, result.ClippedSamples);
        Assert.Equal(0, result.InvalidSamples);
        Assert.Equal(0, result.FlatTopCount);
    }

    [Fact]
    public void InvalidFormatsAndIncompleteFramesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingLevelAnalyzer(7_999, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingLevelAnalyzer(SampleRate, 0));

        var analyzer = new RecordingLevelAnalyzer(SampleRate, 2);
        Assert.Throws<ArgumentNullException>(() => analyzer.Process(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.Process(new float[4], -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.Process(new float[4], 0, 5));
        Assert.Throws<ArgumentException>(() => analyzer.Process(new float[3]));
    }

    [Fact]
    public void IsolatedClicksDoNotDictateTheGainRecommendation()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] second = SineSecond(-12);
        // A narrow click once per second: three samples cannot fill a block's
        // trimmed-peak rank, unlike a musical transient. Kept below the level
        // whose interpolator ringing would legitimately latch the Hot warning.
        second[400] = 0.7f;
        second[401] = -0.7f;
        second[402] = 0.7f;

        FeedRepeated(analyzer, second, seconds: 60);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.TooLow, result.Status);
        // The absolute meter still shows the click…
        Assert.InRange(result.TruePeakDb, -6, 0);
        // …but the recommendation protects the programme.
        Assert.InRange(result.ProgramPeakDb, -12.8, -11.3);
        Assert.InRange(result.SuggestedGainDb, 5, 6);
    }

    [Fact]
    public void IsolatedFullScaleClicksDoNotTellUserToLowerTheProgramme()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] second = SineSecond(-12);
        second[399] = 0.2f;
        second[400] = 1.0f;
        second[401] = 1.0f;
        second[402] = 1.0f;
        second[403] = 0.2f;

        FeedRepeated(analyzer, second, seconds: 60);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.TooLow, result.Status);
        Assert.True(result.TruePeakDb >= 0);
        Assert.Equal(180, result.ClippedSamples);
        Assert.InRange(result.ProgramPeakDb, -12.8, -11.3);
        Assert.InRange(result.SuggestedGainDb, 5, 6);
    }

    [Fact]
    public void RepeatedNarrowPlateausDoNotMasqueradeAsProgrammeClipping()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] second = SineSecond(-12);
        second[399] = 0.2f;
        second[400] = 0.8f;
        second[401] = 0.8f;
        second[402] = 0.8f;
        second[403] = 0.2f;

        FeedRepeated(analyzer, second, seconds: 60);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.True(result.FlatTopCount >= 60);
        Assert.Equal(RecordingLevelStatus.TooLow, result.Status);
        Assert.InRange(result.ProgramPeakDb, -12.8, -11.3);
        Assert.InRange(result.SuggestedGainDb, 5, 6);
    }

    [Fact]
    public void GenuineTransientsSurviveClickRejection()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        // 5 ms of 1 kHz at -6 dB over a quiet bed, identical every 100 ms block:
        // wide enough to be music, not a click.
        var block = new float[SampleRate / 10];
        for (int frame = 0; frame < block.Length; frame++)
        {
            bool burst = frame < SampleRate / 200;
            double amplitude = Math.Pow(10, (burst ? -6 : -30) / 20.0);
            block[frame] = (float)(amplitude * Math.Sin(2 * Math.PI * 1_000 * frame / SampleRate));
        }
        for (int repeat = 0; repeat < 600; repeat++) analyzer.Process(block);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.InRange(result.ProgramPeakDb, -6.3, -5.7);
    }

    [Fact]
    public void DynamicProgrammeEarnsExtraSafetyReserve()
    {
        var steady = new RecordingLevelAnalyzer(SampleRate, 1);
        FeedRepeated(steady, SineSecond(-6), seconds: 60);

        var dynamic = new RecordingLevelAnalyzer(SampleRate, 1);
        var block = new float[SampleRate / 10];
        for (int frame = 0; frame < block.Length; frame++)
        {
            bool burst = frame < SampleRate / 200;
            double amplitude = Math.Pow(10, (burst ? -6 : -30) / 20.0);
            block[frame] = (float)(amplitude * Math.Sin(2 * Math.PI * 1_000 * frame / SampleRate));
        }
        for (int repeat = 0; repeat < 600; repeat++) dynamic.Process(block);

        RecordingLevelSnapshot steadyResult = steady.Snapshot;
        RecordingLevelSnapshot dynamicResult = dynamic.Snapshot;
        Assert.Equal(3, steadyResult.ReserveDb, 8);
        Assert.True(dynamicResult.CrestFactorDb > 12,
            $"Expected a high programme crest, was {dynamicResult.CrestFactorDb:0.0}");
        Assert.True(dynamicResult.ReserveDb > steadyResult.ReserveDb,
            $"Dynamic reserve {dynamicResult.ReserveDb:0.00} should exceed steady {steadyResult.ReserveDb:0.00}");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(60)]
    public void MainsHumDominatingQuietPassagesIsIdentifiedByFrequency(int humHz)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        var second = new float[SampleRate];
        for (int frame = 0; frame < second.Length; frame++)
            second[frame] = (float)(Math.Pow(10, -30 / 20.0) * Math.Sin(2 * Math.PI * humHz * frame / SampleRate));

        FeedRepeated(analyzer, second, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        // Hum alone is not programme…
        Assert.Equal(RecordingLevelStatus.WaitingForSignal, result.Status);
        // …but it is named, with its RMS level (about -33 dBFS for a -30 dB peak sine).
        Assert.Equal(humHz, result.HumFrequencyHz);
        Assert.InRange(result.HumLevelDb, -33.5, -32.5);
    }

    [Fact]
    public void BroadbandNoiseFloorIsNotReportedAsHum()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        var random = new Random(7342);
        var second = new float[SampleRate];
        for (int frame = 0; frame < second.Length; frame++)
            second[frame] = (float)((random.NextDouble() * 2 - 1) * 0.05);

        FeedRepeated(analyzer, second, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(0, result.HumFrequencyHz);
        Assert.True(double.IsNegativeInfinity(result.HumLevelDb));
    }

    [Fact]
    public void DcOffsetIsMeasuredAcrossTheActiveProgramme()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] second = SineSecond(-12);
        for (int frame = 0; frame < second.Length; frame++) second[frame] += 0.01f;

        FeedRepeated(analyzer, second, seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.InRange(result.DcOffsetDb, -40.3, -39.7);
    }

    [Fact]
    public void CleanProgrammeReportsNoDcOffsetOrHum()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 2);

        FeedRepeated(analyzer, SineSecond(-12, -12), seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.True(result.DcOffsetDb < -60, $"Expected no DC, got {result.DcOffsetDb:0.0} dB");
        Assert.Equal(0, result.HumFrequencyHz);
    }

    [Fact]
    public void ProgramLoudnessIsReportedInLufs()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);

        FeedRepeated(analyzer, SineSecond(-12), seconds: 12);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.True(double.IsFinite(result.IntegratedLufs));
        // LUFS sits 0.691 below RMS plus the K-weighting loss at 100 Hz
        // (the 38 Hz high-pass with Q=0.5 costs about 1.2 dB there).
        Assert.InRange(result.IntegratedLufs, result.ProgramRmsDb - 2.5, result.ProgramRmsDb + 0.5);
    }

    [Fact]
    public void SnapshotIsReusedUntilNewSamplesArrive()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        analyzer.Process(SineSecond(-9));

        RecordingLevelSnapshot first = analyzer.Snapshot;
        RecordingLevelSnapshot second = analyzer.Snapshot;
        Assert.Same(first, second);

        analyzer.Process(SineSecond(-9));
        RecordingLevelSnapshot third = analyzer.Snapshot;
        Assert.NotSame(first, third);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void ResetClearsTheNewDiagnosticsToo()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] second = SineSecond(-12);
        for (int frame = 0; frame < second.Length; frame++) second[frame] += 0.01f;
        FeedRepeated(analyzer, second, seconds: 12);

        analyzer.Reset();

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.True(double.IsNegativeInfinity(result.ProgramPeakDb));
        Assert.True(double.IsNegativeInfinity(result.IntegratedLufs));
        Assert.True(double.IsNegativeInfinity(result.DcOffsetDb));
        Assert.True(double.IsNegativeInfinity(result.HumLevelDb));
        Assert.Equal(0, result.HumFrequencyHz);
    }

    [Fact]
    public void FinalSnapshotFlushesTheTruePeakInterpolatorTail()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] tail = SineSecond(-6);
        // Alternating near-full-scale samples at the very end: the interpolator
        // phases that straddle them are only evaluated once the tail is rung out.
        for (int frame = tail.Length - 12; frame < tail.Length; frame++)
            tail[frame] = frame % 2 == 0 ? 0.95f : -0.95f;

        FeedRepeated(analyzer, SineSecond(-12), seconds: 12);
        analyzer.Process(tail);

        var reference = new LoudnessMeter();
        reference.Configure(SampleRate, 1);
        float[] quiet = SineSecond(-12);
        for (int second = 0; second < 12; second++) reference.Process(quiet, 0, quiet.Length);
        reference.Process(tail, 0, tail.Length);
        double beforeFlush = reference.TruePeakDb;
        reference.FlushTruePeak();
        double afterFlush = reference.TruePeakDb;

        double live = analyzer.Snapshot.TruePeakDb;
        double final = analyzer.GetFreshSnapshot().TruePeakDb;

        // Guard against a vacuous test: the flush must actually find something.
        Assert.True(afterFlush > beforeFlush);
        Assert.True(final >= live);
        Assert.Equal(afterFlush, final, 9);
    }

    [Fact]
    public void FinalSnapshotIncludesAPartialTrailingBlockCountedInRealFrames()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        FeedRepeated(analyzer, SineSecond(-12), seconds: 12);
        Assert.Equal(12, analyzer.Snapshot.ActiveSeconds, 8);

        analyzer.Process(SineSecond(-12), 0, SampleRate * 6 / 100); // 60 ms

        // The live snapshot only advances when a block completes.
        Assert.Equal(12, analyzer.Snapshot.ActiveSeconds, 8);
        // 12.06, not the 12.1 that counting whole blocks would report.
        Assert.Equal(12.06, analyzer.GetFreshSnapshot().ActiveSeconds, 8);
    }

    [Fact]
    public void PartialTailShorterThanAQuarterBlockIsIgnored()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        FeedRepeated(analyzer, SineSecond(-12), seconds: 12);

        analyzer.Process(SineSecond(-12), 0, SampleRate / 100); // 10 ms

        Assert.Equal(12, analyzer.GetFreshSnapshot().ActiveSeconds, 8);
    }

    [Fact]
    public void SustainedConverterOverloadIsNotHiddenByALouderIsolatedClick()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] clicked = SineSecond(-12);
        for (int frame = 100; frame < 103; frame++) clicked[frame] = 1f;

        float[] overloaded = SineSecond(-12);
        for (int frame = 0; frame < SampleRate * 15 / 100; frame++) // 150 ms
        {
            double wave = Math.Sin(2 * Math.PI * 100 * frame / SampleRate) * 2;
            overloaded[frame] = (float)Math.Clamp(wave, -1, 1);
        }

        for (int second = 0; second < 60; second++)
            analyzer.Process(second == 30 ? overloaded : clicked);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        // The clicks still own the absolute peak by a wide margin over the
        // programme, so the narrow-artifact amnesty would otherwise apply.
        Assert.True(result.TruePeakDb - result.ProgramPeakDb > 6);
        Assert.Equal(RecordingLevelStatus.Clipping, result.Status);
    }

    [Fact]
    public void IsolatedClicksStillDoNotLatchClippingAfterTheEvidenceRule()
    {
        const int HighRate = 48_000;
        var analyzer = new RecordingLevelAnalyzer(HighRate, 2);
        var second = new float[HighRate * 2];
        double amplitude = Math.Pow(10, -12 / 20.0);
        for (int frame = 0; frame < HighRate; frame++)
        {
            var wave = (float)(Math.Sin(2 * Math.PI * 100 * frame / HighRate) * amplitude);
            second[frame * 2] = wave;
            second[frame * 2 + 1] = wave;
        }
        for (int frame = 600; frame < 603; frame++)
        {
            second[frame * 2] = 1f;
            second[frame * 2 + 1] = 1f;
        }

        for (int index = 0; index < 12; index++) analyzer.Process(second);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        // Six clipped samples in a 4800-frame stereo block is far below the
        // trimmed-peak fraction, so this stays a stylus click, not overload.
        Assert.True(result.ClippedSamples > 0);
        Assert.NotEqual(RecordingLevelStatus.Clipping, result.Status);
        Assert.Equal(RecordingLevelStatus.TooLow, result.Status);
    }

    [Fact]
    public void ClicksSpreadAcrossALargeCaptureBufferAreNotReadAsSustainedOverload()
    {
        // A 500 ms capture buffer spans five blocks. The caller reports one clip
        // total for the whole packet, so the per-block split has to be measured
        // here rather than credited to whichever block happened to be open.
        const int HighRate = 48_000;
        const int PacketFrames = HighRate / 2;
        var analyzer = new RecordingLevelAnalyzer(HighRate, 2);

        var packet = new float[PacketFrames * 2];
        double amplitude = Math.Pow(10, -12 / 20.0);
        for (int frame = 0; frame < PacketFrames; frame++)
        {
            var wave = (float)(Math.Sin(2 * Math.PI * 100 * frame / HighRate) * amplitude);
            packet[frame * 2] = wave;
            packet[frame * 2 + 1] = wave;
        }
        long clipped = 0;
        for (int click = 0; click < 20; click++)
        {
            for (int frame = click * 1200; frame < click * 1200 + 3; frame++)
            {
                packet[frame * 2] = 1f;
                packet[frame * 2 + 1] = 1f;
                clipped += 2;
            }
        }

        for (int index = 0; index < 24; index++)
            analyzer.Process(packet, 0, packet.Length, clipped);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.True(result.ClippedSamples > 0);
        Assert.NotEqual(RecordingLevelStatus.Clipping, result.Status);
    }

    [Fact]
    public void SustainedOverloadIsStillDetectedThroughSoftwareAttenuation()
    {
        // The engine trims before the analyzer sees the audio, so the rail moves
        // down with the gain. Detecting at full scale would miss it entirely.
        const double Gain = 0.5;
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] quiet = SineSecond(-12);
        for (int frame = 0; frame < quiet.Length; frame++) quiet[frame] *= (float)Gain;

        float[] overloaded = (float[])quiet.Clone();
        for (int frame = 0; frame < SampleRate * 15 / 100; frame++)
        {
            double wave = Math.Sin(2 * Math.PI * 100 * frame / SampleRate) * 2;
            overloaded[frame] = (float)(Math.Clamp(wave, -1, 1) * Gain);
        }

        for (int second = 0; second < 20; second++)
        {
            float[] buffer = second == 10 ? overloaded : quiet;
            // 3 clipped samples per second upstream, as an isolated stylus click.
            analyzer.Process(buffer, 0, buffer.Length, sourceClippedSamples: 3, sourceGain: Gain);
        }

        Assert.Equal(RecordingLevelStatus.Clipping, analyzer.Snapshot.Status);
    }

    [Fact]
    public void TargetCeilingIsClampedToASensibleRange()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        Assert.Equal(RecordingLevelAnalyzer.DefaultTargetCeilingDb, analyzer.TargetCeilingDb);

        analyzer.TargetCeilingDb = 5;
        Assert.Equal(-1, analyzer.TargetCeilingDb);
        analyzer.TargetCeilingDb = -100;
        Assert.Equal(-24, analyzer.TargetCeilingDb);
        analyzer.TargetCeilingDb = double.NaN;
        Assert.Equal(RecordingLevelAnalyzer.DefaultTargetCeilingDb, analyzer.TargetCeilingDb);
    }

    [Theory]
    [InlineData(-3, 3.0)]
    [InlineData(-6, 0.0)]
    [InlineData(-10, -1.0)]
    public void RecommendationTracksTheConfiguredTargetCeiling(
        double ceilingDb,
        double expectedSuggestionDb)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1) { TargetCeilingDb = ceilingDb };

        FeedRepeated(analyzer, SineSecond(-9), seconds: 60);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(3, result.ReserveDb, 8);
        Assert.Equal(expectedSuggestionDb, result.SuggestedGainDb, 8);
    }

    /// <summary>
    /// The ceiling is a continuous setting, not three presets wearing a slider: a
    /// value between two landmarks has to land the recommendation between their two
    /// recommendations rather than snapping to either.
    /// </summary>
    [Fact]
    public void ARecommendationFollowsACeilingBetweenTheLandmarks()
    {
        double SuggestionAt(double ceilingDb)
        {
            var analyzer = new RecordingLevelAnalyzer(SampleRate, 1) { TargetCeilingDb = ceilingDb };
            FeedRepeated(analyzer, SineSecond(-9), seconds: 60);
            return analyzer.Snapshot.SuggestedGainDb;
        }

        double lower = SuggestionAt(-6);
        double between = SuggestionAt(-4.5);
        double upper = SuggestionAt(-3);

        Assert.True(
            lower < between && between < upper,
            $"expected a −4.5 dBTP ceiling to sit between the landmarks, got "
            + $"{lower:0.###} / {between:0.###} / {upper:0.###}");
    }

    /// <summary>
    /// Changing the ceiling mid-scan must re-derive from blocks already measured. The
    /// analyzer's own cache is what makes the level card's slider cheap, so a stale
    /// snapshot here would show as a readout that ignores the control.
    /// </summary>
    [Fact]
    public void ChangingTheCeilingReDerivesWithoutFurtherAudio()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1) { TargetCeilingDb = -6 };
        FeedRepeated(analyzer, SineSecond(-9), seconds: 60);
        double before = analyzer.Snapshot.SuggestedGainDb;

        analyzer.TargetCeilingDb = -4.5;
        double after = analyzer.Snapshot.SuggestedGainDb;

        Assert.NotEqual(before, after, 6);
    }

    [Fact]
    public void SparseLoudPassagesEarnMoreReserveThanSteadyMaterial()
    {
        var steady = new RecordingLevelAnalyzer(SampleRate, 1);
        FeedRepeated(steady, SineSecond(-12), seconds: 60);

        var peaky = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] quiet = SineSecond(-12);
        float[] burst = SineSecond(-12);
        double loud = Math.Pow(10, -4 / 20.0);
        for (int frame = 0; frame < SampleRate / 100; frame++) // 10 ms, far wider than a click
            burst[frame] = (float)(Math.Sin(2 * Math.PI * 100 * frame / SampleRate) * loud);
        for (int second = 0; second < 60; second++)
            peaky.Process(second % 20 == 0 ? burst : quiet);

        // The recommendation is built on the 99th percentile, which the three
        // bursts never reach — so the reserve has to cover the gap to the maximum.
        Assert.Equal(3, steady.Snapshot.ReserveDb, 8);
        Assert.InRange(peaky.Snapshot.ReserveDb, 7.5, 8.5);
    }

    [Theory]
    [InlineData(10, 6)]
    [InlineData(30, 4)]
    [InlineData(60, 3)]
    [InlineData(120, 2)]
    public void ReserveNeverFallsBelowTheTimeSchedule(int seconds, double scheduledReserve)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] quiet = SineSecond(-12);
        float[] burst = SineSecond(-4);

        for (int second = 0; second < seconds; second++)
            analyzer.Process(second % 20 == 0 ? burst : quiet);

        Assert.True(analyzer.Snapshot.ReserveDb >= scheduledReserve);
    }

    [Fact]
    public void AStillRisingProgrammeMaximumHoldsExtraReserveAndLowersConfidence()
    {
        float[] quiet = SineSecond(-18);
        float[] loud = SineSecond(-6);

        var rising = new RecordingLevelAnalyzer(SampleRate, 1);
        for (int second = 0; second < 120; second++)
            rising.Process(second < 108 ? quiet : loud);

        var settledEarly = new RecordingLevelAnalyzer(SampleRate, 1);
        for (int second = 0; second < 120; second++)
            settledEarly.Process(second < 12 ? loud : quiet);

        RecordingLevelSnapshot risingResult = rising.Snapshot;
        RecordingLevelSnapshot settledResult = settledEarly.Snapshot;

        // Identical block populations, opposite order: only the novelty term moves.
        Assert.Equal(risingResult.ProgramPeakDb, settledResult.ProgramPeakDb, 8);
        Assert.Equal(1.0, risingResult.ReserveDb - settledResult.ReserveDb, 6);
        Assert.True(risingResult.Confidence < settledResult.Confidence);
    }

    private static float[] SineSecond(double leftPeakDb, double? rightPeakDb = null)
    {
        double left = Math.Pow(10, leftPeakDb / 20);
        double right = Math.Pow(10, (rightPeakDb ?? leftPeakDb) / 20);
        bool stereo = rightPeakDb.HasValue;
        int channels = stereo ? 2 : 1;
        var result = new float[SampleRate * channels];
        for (int frame = 0; frame < SampleRate; frame++)
        {
            double wave = Math.Sin(2 * Math.PI * 100 * frame / SampleRate);
            result[frame * channels] = (float)(wave * left);
            if (stereo) result[frame * channels + 1] = (float)(wave * right);
        }
        return result;
    }

    private static float[] StereoSignal(int seconds, double frequency, double amplitude, double phase)
    {
        int frames = SampleRate * seconds;
        var result = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            double left = Math.Sin(2 * Math.PI * frequency * frame / SampleRate + phase) * amplitude;
            double right = Math.Sin(2 * Math.PI * frequency * frame / SampleRate + phase + 0.41) * amplitude * 0.83;
            result[frame * 2] = (float)left;
            result[frame * 2 + 1] = (float)right;
        }
        return result;
    }

    private static void FeedRepeated(RecordingLevelAnalyzer analyzer, float[] oneSecond, int seconds)
    {
        for (int second = 0; second < seconds; second++) analyzer.Process(oneSecond);
    }
}
