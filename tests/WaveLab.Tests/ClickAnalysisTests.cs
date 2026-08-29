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

    private static ClickEvent Event(int channel, int start, int end) =>
        new(channel, start, end, start, ImpulseDefectKind.Pop,
            Confidence: 0.9f, Severity: 0.9f, PeakAmplitude: 0.8f,
            DetectionThreshold: 0.05f);

    private static double Error(float[] actual, float[] expected, int start, int count)
    {
        double error = 0;
        for (int index = start; index < start + count; index++)
            error += Math.Abs(actual[index] - expected[index]);
        return error;
    }
}
