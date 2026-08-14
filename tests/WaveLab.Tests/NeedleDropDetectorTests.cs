using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class NeedleDropDetectorTests
{
    private const int SampleRate = 48_000;

    [Fact]
    public void SharpContactAfterQuietTriggers()
    {
        var detector = new NeedleDropDetector();
        FeedQuiet(detector, seconds: 0.25);
        var contact = new float[480 * 2];
        contact[200] = 0.12f;
        contact[201] = 0.10f;
        contact[202] = -0.07f;
        contact[203] = -0.06f;

        Assert.True(detector.Process(contact, contact.Length, 2, SampleRate));
        Assert.True(detector.IsTriggered);
    }

    [Fact]
    public void InterfaceNoiseAndHumDoNotTrigger()
    {
        var detector = new NeedleDropDetector();
        int frames = SampleRate * 2;
        var buffer = new float[480 * 2];
        int frame = 0;
        while (frame < frames)
        {
            for (int index = 0; index < buffer.Length; index += 2)
            {
                float sample = (float)(0.0015 * Math.Sin(2 * Math.PI * 60 * frame / SampleRate));
                buffer[index] = sample;
                buffer[index + 1] = sample;
                frame++;
            }
            Assert.False(detector.Process(buffer, buffer.Length, 2, SampleRate));
        }
    }

    [Fact]
    public void DetectorWaitsForNoiseFloorWarmup()
    {
        var detector = new NeedleDropDetector();
        var earlyHandlingNoise = new float[480 * 2];
        earlyHandlingNoise[40] = 0.1f;
        earlyHandlingNoise[41] = 0.1f;

        Assert.False(detector.Process(earlyHandlingNoise, earlyHandlingNoise.Length, 2, SampleRate));
    }

    private static void FeedQuiet(NeedleDropDetector detector, double seconds)
    {
        int remainingFrames = (int)(SampleRate * seconds);
        var buffer = new float[480 * 2];
        while (remainingFrames > 0)
        {
            int frames = Math.Min(480, remainingFrames);
            int count = frames * 2;
            Assert.False(detector.Process(buffer, count, 2, SampleRate));
            remainingFrames -= frames;
        }
    }
}
