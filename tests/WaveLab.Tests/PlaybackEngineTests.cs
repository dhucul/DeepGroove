using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class PlaybackEngineTests
{
    [Fact]
    public void PresentedPositionStaysAtStartDuringPreRollThenAdvancesContinuously()
    {
        Assert.Equal(1_000, PlaybackEngine.CalculatePresentedPosition(
            start: 1_000, end: 100_000, preRollFrames: 960,
            outputFrames: 480, outputSampleRate: 48_000, sourceSampleRate: 48_000,
            loop: false));

        Assert.Equal(5_800, PlaybackEngine.CalculatePresentedPosition(
            start: 1_000, end: 100_000, preRollFrames: 960,
            outputFrames: 5_760, outputSampleRate: 48_000, sourceSampleRate: 48_000,
            loop: false));
    }

    [Fact]
    public void PresentedPositionConvertsTheOutputClockToTheSourceSampleRate()
    {
        Assert.Equal(45_100, PlaybackEngine.CalculatePresentedPosition(
            start: 1_000, end: 100_000, preRollFrames: 882,
            outputFrames: 48_960, outputSampleRate: 48_000, sourceSampleRate: 44_100,
            loop: false));
    }

    [Fact]
    public void PresentedPositionClampsAtEndOrWrapsInsideALoop()
    {
        Assert.Equal(2_000, PlaybackEngine.CalculatePresentedPosition(
            start: 1_000, end: 2_000, preRollFrames: 0,
            outputFrames: 1_125, outputSampleRate: 48_000, sourceSampleRate: 48_000,
            loop: false));

        Assert.Equal(1_125, PlaybackEngine.CalculatePresentedPosition(
            start: 1_000, end: 2_000, preRollFrames: 0,
            outputFrames: 1_125, outputSampleRate: 48_000, sourceSampleRate: 48_000,
            loop: true));
    }
}
