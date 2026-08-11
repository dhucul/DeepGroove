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
    [InlineData(-1, RecordingLevelStatus.Hot, -5)]
    public void SixtySecondScanTargetsAProjectedMinusThreeDbtp(
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
    public void DigitalFullScaleSamplesLatchClippingImmediately()
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        float[] data = SineSecond(-12);
        data[401] = 1.0f;
        data[402] = -1.0f;

        analyzer.Process(data);

        RecordingLevelSnapshot result = analyzer.Snapshot;
        Assert.Equal(RecordingLevelStatus.Clipping, result.Status);
        Assert.Equal(2, result.ClippedSamples);
        Assert.True(result.TruePeakDb >= 0);
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
        Assert.True(double.IsFinite(result.ProgramLoudnessLufs));
        // LUFS sits 0.691 below RMS plus the K-weighting loss at 100 Hz
        // (the 38 Hz high-pass with Q=0.5 costs about 1.2 dB there).
        Assert.InRange(result.ProgramLoudnessLufs, result.ProgramRmsDb - 2.5, result.ProgramRmsDb + 0.5);
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
        Assert.True(double.IsNegativeInfinity(result.ProgramLoudnessLufs));
        Assert.True(double.IsNegativeInfinity(result.DcOffsetDb));
        Assert.True(double.IsNegativeInfinity(result.HumLevelDb));
        Assert.Equal(0, result.HumFrequencyHz);
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
