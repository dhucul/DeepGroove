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
        Assert.InRange(result.TruePeakDb, peakDb - 0.01, peakDb + 0.01);
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

        analyzer.Process([-0.9f, 0.9f, 0.9f, -0.9f]);

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
    public void LowSteadyDcOrHumRemainsWaitingWithoutPositiveGainAdvice(bool hum)
    {
        var analyzer = new RecordingLevelAnalyzer(SampleRate, 1);
        var second = new float[SampleRate];
        const double amplitude = 0.01; // -40 dBFS peak
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
