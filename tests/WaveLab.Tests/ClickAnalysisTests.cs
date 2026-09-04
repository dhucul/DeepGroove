using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class ClickAnalysisTests
{
    private const int SampleRate = 48_000;

    [Fact]
    public void MaximumSensitivityFindsAnObviousSingleSampleClickInMusic()
    {
        float[][] audio = Program(seconds: 2);
        int defect = SampleRate;
        audio[0][defect] += 0.85f;

        ClickAnalysisResult result = Analyze(audio);

        Assert.Contains(result.Events, item => Covers(item, defect));
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(7.0)]
    [InlineData(10.0)]
    public void RestorationSensitivitiesFindAShortFlatPopInMusic(double sensitivity)
    {
        float[][] audio = Program(seconds: 2);
        int start = SampleRate;
        const int length = 24; // 0.5 ms: a conspicuous short impulse, not a musical attack
        for (int index = start; index < start + length; index++)
            audio[0][index] += 0.65f;

        ClickAnalysisResult result = Analyze(audio, sensitivity);

        Assert.Contains(result.Events,
            item => item.StartSample <= start + 1 && item.EndSample >= start + length - 1);
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(7.0)]
    [InlineData(10.0)]
    public void RestorationSensitivitiesFindAShortDecayingPopInMusic(double sensitivity)
    {
        float[][] audio = Program(seconds: 2);
        int start = SampleRate;
        int length = (int)Math.Round(SampleRate * 0.002);
        for (int offset = 0; offset < length; offset++)
        {
            double envelope = Math.Exp(-offset / 24.0);
            audio[0][start + offset] += (float)(0.75 * envelope);
        }

        ClickAnalysisResult result = Analyze(audio, sensitivity);

        Assert.Contains(result.Events,
            item => item.StartSample <= start + 1 && item.EndSample > start + 8);
    }

    [Fact]
    public void AnalysisKeepsTheCompleteEnvelopeOfAShortRingingPop()
    {
        float[][] audio = ComplexProgram(seconds: 2);
        int start = SampleRate;
        const int secondLobe = 150; // ringing lobe within the five-millisecond pop contract
        const int lobeLength = 18;
        for (int offset = 0; offset < lobeLength; offset++)
        {
            double decay = Math.Exp(-offset / 7.0);
            audio[0][start + offset] += (float)(0.78 * decay * (offset % 2 == 0 ? 1 : -1));
            audio[0][start + secondLobe + offset] +=
                (float)(0.58 * decay * (offset % 2 == 0 ? -1 : 1));
        }

        ClickAnalysisResult result = Analyze(audio, sensitivity: 7);

        Assert.Contains(result.Events, item =>
            item.StartSample <= start + 1 &&
            item.EndSample >= start + secondLobe + lobeLength - 1);
    }

    [Fact]
    public void CompactCoreDoesNotRestoreAnEnvelopeRejectedForPoorRecovery()
    {
        float[][] audio = Program(seconds: 2);
        int click = SampleRate;
        int attack = click + 70;
        audio[0][click] += 0.82f;
        for (int index = attack; index < attack + 500; index++)
            audio[0][index] += 0.40f;

        ClickAnalysisResult result = Analyze(audio, sensitivity: 7);

        Assert.DoesNotContain(result.Events, item =>
            item.StartSample <= click + 1 && item.EndSample >= attack);
    }

    [Fact]
    public void RepairRemovesMostOfAnObviousShortPop()
    {
        float[][] clean = Program(seconds: 2);
        float[][] damaged = [clean[0].ToArray()];
        int start = SampleRate;
        const int length = 24;
        for (int index = start; index < start + length; index++)
            damaged[0][index] += 0.65f;

        ClickAnalysisResult analysis = Analyze(damaged, sensitivity: 7);
        float[][] repaired = Restoration.RepairClicks(damaged, analysis.Events,
            new ClickRepairOptions { Strength = 1 });

        double damagedError = Error(damaged[0], clean[0], start, length);
        double repairedError = Error(repaired[0], clean[0], start, length);
        Assert.True(repairedError < damagedError * 0.2,
            $"Expected repair error below 20% of the damaged signal, got {repairedError / damagedError:P1}.");
    }

    [Fact]
    public void RepairReconstructsOscillatoryAudioAcrossAWideClick()
    {
        float[][] clean = ComplexProgram(seconds: 2);
        float[][] damaged = [clean[0].ToArray()];
        int start = SampleRate;
        int length = SampleRate / 500; // 2 ms / 96 samples
        for (int index = start; index < start + length; index++)
            damaged[0][index] += 0.72f;

        ClickAnalysisResult analysis = Analyze(damaged, sensitivity: 7);
        float[][] repaired = Restoration.RepairClicks(damaged, analysis.Events,
            new ClickRepairOptions { Strength = 1 });

        double damagedError = Error(damaged[0], clean[0], start, length);
        double repairedError = Error(repaired[0], clean[0], start, length);
        Assert.True(repairedError < damagedError * 0.15,
            $"Expected model-based repair error below 15%, got {repairedError / damagedError:P1}.");
    }

    [Fact]
    public void StereoRepairUsesTheCompleteDetectedSpanInBothAffectedChannels()
    {
        float[] cleanLeft = ComplexProgram(seconds: 2)[0];
        float[] cleanRight = Program(seconds: 2)[0];
        float[][] clean = [cleanLeft, cleanRight];
        float[][] damaged = [cleanLeft.ToArray(), cleanRight.ToArray()];
        int start = SampleRate;
        int length = SampleRate / 500; // 2 ms
        for (int index = start; index < start + length; index++)
        {
            damaged[0][index] += 0.72f;
            damaged[1][index] -= 0.64f;
        }

        ClickEvent[] channelLocalDetections =
        [
            Event(channel: 0, start, start + length),
            Event(channel: 1, start + 2, start + length / 2),
        ];

        float[][] repaired = Restoration.RepairClicks(damaged, channelLocalDetections,
            new ClickRepairOptions { Strength = 1 });

        double leftDamagedError = Error(damaged[0], clean[0], start, length);
        double rightDamagedError = Error(damaged[1], clean[1], start, length);
        double leftRepairedError = Error(repaired[0], clean[0], start, length);
        double rightRepairedError = Error(repaired[1], clean[1], start, length);
        Assert.True(leftRepairedError < leftDamagedError * 0.2);
        Assert.True(rightRepairedError < rightDamagedError * 0.2,
            $"Expected linked stereo repair to remove the residual right-channel click, " +
            $"got {rightRepairedError / rightDamagedError:P1} error.");
    }

    [Fact]
    public void StereoRepairRepairsTheSameSpanWhenOnlyOneChannelDetectedTheClick()
    {
        float[] cleanLeft = ComplexProgram(seconds: 2)[0];
        float[] cleanRight = Program(seconds: 2)[0];
        float[][] damaged = [cleanLeft.ToArray(), cleanRight.ToArray()];
        int start = SampleRate;
        const int detectedLength = 3;
        const int oppositePolarityTailLength = 8;
        int completeLength = detectedLength + oppositePolarityTailLength;
        for (int index = start; index < start + detectedLength; index++)
        {
            damaged[0][index] += 0.72f;
            damaged[1][index] += 0.40f;
        }
        for (int index = start + detectedLength;
             index < start + completeLength; index++)
            damaged[1][index] -= 0.28f; // quieter opposite-polarity remnant

        float[][] repaired = Restoration.RepairClicks(damaged,
            [Event(channel: 0, start, start + detectedLength)],
            new ClickRepairOptions { Strength = 1 });

        double damagedError = Error(damaged[1], cleanRight, start, completeLength);
        double repairedError = Error(repaired[1], cleanRight, start, completeLength);
        Assert.True(repairedError < damagedError * 0.2,
            $"Expected cross-channel repair below 20% error, got " +
            $"{repairedError / damagedError:P1}.");
    }

    [Fact]
    public void PopRepairIncludesAQuieterLobeOutsideTheDetectedCore()
    {
        float[][] clean = ComplexProgram(seconds: 2);
        float[][] damaged = [clean[0].ToArray()];
        int coreStart = SampleRate;
        const int leadingLobe = 28;
        const int coreLength = 20;
        for (int offset = -leadingLobe; offset < coreLength; offset++)
        {
            double distance = offset < 0 ? -offset : offset;
            double amplitude = offset < 0 ? 0.30 : 0.75;
            damaged[0][coreStart + offset] +=
                (float)(amplitude * Math.Exp(-distance / 10.0) * (offset % 2 == 0 ? 1 : -1));
        }

        float[][] repaired = Restoration.RepairClicks(damaged,
            [Event(channel: 0, coreStart, coreStart + coreLength)],
            new ClickRepairOptions { Strength = 1 });

        int completeStart = coreStart - leadingLobe;
        int completeLength = leadingLobe + coreLength;
        double damagedError = Error(damaged[0], clean[0], completeStart, completeLength);
        double repairedError = Error(repaired[0], clean[0], completeStart, completeLength);
        Assert.True(repairedError < damagedError * 0.2,
            $"Expected the quiet lobe to be included in repair, got " +
            $"{repairedError / damagedError:P1} error.");
    }

    [Fact]
    public void ChannelLinkingCanBeDisabledForUnrelatedMultichannelAudio()
    {
        float[] cleanLeft = ComplexProgram(seconds: 2)[0];
        float[] cleanRight = Program(seconds: 2)[0];
        float[][] damaged = [cleanLeft.ToArray(), cleanRight.ToArray()];
        int start = SampleRate;
        int length = SampleRate / 500;
        for (int index = start; index < start + length; index++)
            damaged[0][index] += 0.72f;

        float[][] repaired = Restoration.RepairClicks(damaged,
            [Event(channel: 0, start, start + length)],
            new ClickRepairOptions { Strength = 1, LinkChannels = false });

        Assert.Equal(cleanRight, repaired[1]);
    }

    [Fact]
    public void UnlinkedChannelsBuildIndependentRepairEnvelopes()
    {
        ClickEvent[] events =
        [
            Event(channel: 0, start: 1_000, end: 1_040,
                kind: ImpulseDefectKind.Pop, sampleRate: SampleRate),
            Event(channel: 1, start: 1_010, end: 1_014,
                kind: ImpulseDefectKind.Click, sampleRate: SampleRate),
        ];

        ClickEvent[] plan = Restoration.CreateClickRepairPlan(events,
            channelCount: 2, sampleCount: 4_000, linkChannels: false);

        ClickEvent left = Assert.Single(plan, item => item.Channel == 0);
        ClickEvent right = Assert.Single(plan, item => item.Channel == 1);
        Assert.Equal((964, 1_076), (left.StartSample, left.EndSample));
        Assert.Equal((1_001, 1_023), (right.StartSample, right.EndSample));
    }

    [Theory]
    [InlineData(44_100, 33)]
    [InlineData(96_000, 72)]
    [InlineData(192_000, 144)]
    public void PopRepairGuardRepresentsTheSameDurationAtEverySampleRate(
        int sampleRate, int expectedGuard)
    {
        const int start = 1_000;
        const int end = 1_020;
        ClickEvent[] plan = Restoration.CreateClickRepairPlan(
            [Event(0, start, end, ImpulseDefectKind.Pop, sampleRate)],
            channelCount: 1, sampleCount: 4_000, linkChannels: true);

        ClickEvent item = Assert.Single(plan);
        Assert.Equal(start - expectedGuard, item.StartSample);
        Assert.Equal(end + expectedGuard, item.EndSample);
    }

    [Fact]
    public void CandidateMergeOrdersAnEarlierAmplitudeEventBeforeComparingOverlap()
    {
        var events = new List<ClickEvent>
        {
            Event(0, 1_200, 1_210, ImpulseDefectKind.Click, SampleRate),
        };

        Restoration.AddOrMergeClickEvent(events,
            Event(0, 1_000, 1_010, ImpulseDefectKind.Click, SampleRate),
            maximumClickSamples: 17, maximumPopSamples: 240);

        Assert.Collection(events,
            first => Assert.Equal((1_000, 1_010), (first.StartSample, first.EndSample)),
            second => Assert.Equal((1_200, 1_210), (second.StartSample, second.EndSample)));
    }

    [Fact]
    public void AutoModeDetectsAndRepairsAFourMillisecondPop()
    {
        float[][] clean = ComplexProgram(seconds: 2);
        float[][] damaged = [clean[0].ToArray()];
        int start = SampleRate;
        int length = SampleRate / 250; // 4 ms / 192 samples
        for (int index = start; index < start + length; index++)
            damaged[0][index] += 0.78f;

        ClickAnalysisResult analysis = Analyze(damaged, sensitivity: 7);
        Assert.Contains(analysis.Events,
            item => item.StartSample <= start + 1 && item.EndSample >= start + length - 1);

        float[][] repaired = Restoration.RepairClicks(damaged, analysis.Events,
            new ClickRepairOptions { Strength = 1 });
        double damagedError = Error(damaged[0], clean[0], start, length);
        double repairedError = Error(repaired[0], clean[0], start, length);
        Assert.True(repairedError < damagedError * 0.25,
            $"Expected four-millisecond repair error below 25%, got {repairedError / damagedError:P1}.");
    }

    [Fact]
    public void AnalysisNeverMergesCandidatesPastTheMaximumRepairablePopLength()
    {
        float[][] audio = Program(seconds: 2);
        int start = SampleRate;
        int individualLength = SampleRate / 1000;
        for (int eventIndex = 0; eventIndex < 8; eventIndex++)
        {
            int eventStart = start + eventIndex * (individualLength - 1);
            for (int index = eventStart; index < eventStart + individualLength; index++)
                audio[0][index] += eventIndex % 2 == 0 ? 0.72f : -0.72f;
        }

        const double maximumPopLengthMs = 5.0;
        ClickAnalysisResult result = Restoration.AnalyzeClicks(audio, SampleRate,
            new ClickAnalysisOptions
            {
                Sensitivity = 10,
                MaximumPopLengthMs = maximumPopLengthMs,
                PreserveTransients = true,
            });
        int maximumSamples = (int)Math.Round(SampleRate * maximumPopLengthMs / 1000.0);

        Assert.NotEmpty(result.Events);
        Assert.All(result.Events, item => Assert.InRange(item.Length, 1, maximumSamples));
    }

    [Fact]
    public void MaximumSensitivityProtectsASmoothPercussiveAttack()
    {
        float[][] audio = Program(seconds: 2);
        int start = SampleRate;
        int length = SampleRate / 20;
        int attack = SampleRate / 250;
        for (int offset = 0; offset < length; offset++)
        {
            double rise = Math.Sin(Math.PI * 0.5 * Math.Min(1, offset / (double)attack));
            double decay = Math.Exp(-offset / (SampleRate * 0.018));
            audio[0][start + offset] += (float)(
                0.55 * rise * decay * Math.Sin(2 * Math.PI * 1_200 * offset / SampleRate));
        }

        ClickAnalysisResult result = Analyze(audio);

        Assert.DoesNotContain(result.Events,
            item => item.EndSample > start && item.StartSample < start + length);
    }

    private static ClickAnalysisResult Analyze(float[][] audio, double sensitivity = 10) =>
        Restoration.AnalyzeClicks(audio, SampleRate,
            new ClickAnalysisOptions
            {
                Sensitivity = sensitivity,
                PreserveTransients = true,
            });

    private static float[][] Program(int seconds)
    {
        var channel = new float[SampleRate * seconds];
        for (int index = 0; index < channel.Length; index++)
        {
            double time = index / (double)SampleRate;
            channel[index] = (float)(
                0.16 * Math.Sin(2 * Math.PI * 233 * time) +
                0.07 * Math.Sin(2 * Math.PI * 997 * time));
        }
        return [channel];
    }

    private static float[][] ComplexProgram(int seconds)
    {
        var channel = new float[SampleRate * seconds];
        for (int index = 0; index < channel.Length; index++)
        {
            double time = index / (double)SampleRate;
            channel[index] = (float)(
                0.17 * Math.Sin(2 * Math.PI * 277 * time + 0.2) +
                0.09 * Math.Sin(2 * Math.PI * 3_411 * time - 0.4) +
                0.05 * Math.Sin(2 * Math.PI * 7_103 * time + 0.7));
        }
        return [channel];
    }

    private static bool Covers(ClickEvent item, int sample) =>
        item.StartSample <= sample && item.EndSample > sample;

    private static ClickEvent Event(int channel, int start, int end,
        ImpulseDefectKind kind = ImpulseDefectKind.Pop, int sampleRate = 0) =>
        new(channel, start, end, start, kind,
            Confidence: 0.9f, Severity: 0.9f, PeakAmplitude: 0.8f,
            DetectionThreshold: 0.05f, SampleRate: sampleRate);

    private static double Error(float[] actual, float[] expected, int start, int count)
    {
        double error = 0;
        for (int index = start; index < start + count; index++)
            error += Math.Abs(actual[index] - expected[index]);
        return error;
    }
}
