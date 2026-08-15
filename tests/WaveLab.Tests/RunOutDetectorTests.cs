using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class RunOutDetectorTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int PacketFrames = 480;
    private const double Hold = 12;

    [Fact]
    public void ProgrammeAloneNeverTriggers()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 40);

        Assert.False(detector.IsTriggered);
        Assert.True(detector.HasHeardProgram);
    }

    [Fact]
    public void RunOutGrooveTriggersAfterTheHold()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 20);
        long musicEnd = feed.TotalSamples;

        feed.Play(RunOut, seconds: 40);

        Assert.True(detector.IsTriggered);
        double held = SecondsBetween(musicEnd, feed.TotalSamples);
        Assert.InRange(held, Hold - 1, Hold + 1.5);
    }

    [Fact]
    public void TrimKeepsTwoSecondsPastTheLastProgramme()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 20);
        long musicEnd = feed.TotalSamples;
        feed.Play(RunOut, seconds: 40);

        long kept = feed.TotalSamples - detector.TrimBackoffSamples;

        Assert.True(kept >= musicEnd, "the trim must never cut into the programme");
        Assert.InRange(SecondsBetween(musicEnd, kept), 1.4, 2.6);
    }

    [Fact]
    public void GapBetweenTracksDoesNotEndTheTake()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 15);
        feed.Play(RunOut, seconds: 6);
        Assert.False(detector.IsTriggered);

        feed.Play(Music, seconds: 10);
        Assert.False(detector.IsTriggered);

        feed.Play(RunOut, seconds: 25);
        Assert.True(detector.IsTriggered);
    }

    [Fact]
    public void SurfaceNoiseWithoutProgrammeNeverTriggers()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);

        feed.Play(RunOut, seconds: 120);

        Assert.False(detector.IsTriggered);
        Assert.False(detector.HasHeardProgram);
    }

    [Fact]
    public void LoudHumDoesNotPassForProgramme()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 15);

        feed.Play(frame => 0.03 * Math.Sin(2 * Math.PI * 50 * frame / SampleRate), seconds: 30);

        Assert.True(detector.IsTriggered);
    }

    [Fact]
    public void FrequentClicksDoNotPassForProgramme()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 15);

        // A click every 200 ms over an almost silent groove: the block level is
        // the median of its sub-blocks, so the clicks cannot lift it.
        feed.Play(frame => frame % (SampleRate / 5) < 12 ? 0.3 : 0.0001, seconds: 30);

        Assert.True(detector.IsTriggered);
    }

    [Fact]
    public void QuietPassageIsStillProgramme()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 12);
        feed.Play(RunOut, seconds: 8);

        feed.Play(frame => Music(frame) * 0.05, seconds: 25);

        Assert.False(detector.IsTriggered);
    }

    [Fact]
    public void CountdownReportsTheRemainingHold()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 12);
        Assert.True(double.IsNaN(detector.CountdownSeconds));

        feed.Play(RunOut, seconds: 5);
        Assert.InRange(detector.CountdownSeconds, 6, 8);

        feed.Play(RunOut, seconds: 10);
        Assert.True(double.IsNaN(detector.CountdownSeconds));
    }

    [Theory]
    [InlineData(1, RunOutDetector.MinimumHoldSeconds)]
    [InlineData(999, RunOutDetector.MaximumHoldSeconds)]
    [InlineData(double.NaN, RunOutDetector.DefaultHoldSeconds)]
    [InlineData(20, 20)]
    public void HoldSecondsAreClamped(double requested, double expected) =>
        Assert.Equal(expected, new RunOutDetector(SampleRate, Channels, requested).HoldSeconds);

    [Fact]
    public void MonoCaptureWithPartialFramesStillTriggers()
    {
        var detector = new RunOutDetector(SampleRate, 1, 5);
        var packet = new float[1001]; // deliberately not a whole number of packets
        for (int index = 0; index < 2000; index++)
        {
            for (int sample = 0; sample < packet.Length; sample++)
            {
                int frame = index * 1000 + sample;
                packet[sample] = (float)(index < 1000 ? Music(frame) : RunOut(frame));
            }
            detector.Process(packet, packet.Length, 1);
        }

        Assert.True(detector.IsTriggered);
    }

    [Fact]
    public void NonFiniteSamplesDoNotWedgeTheDetector()
    {
        var detector = new RunOutDetector(SampleRate, Channels, 5);
        var feed = new Feeder(detector);
        feed.Play(Music, seconds: 8);

        var broken = new float[PacketFrames * Channels];
        Array.Fill(broken, float.NaN);
        detector.Process(broken, broken.Length, Channels);
        feed.Play(RunOut, seconds: 12);

        Assert.True(detector.IsTriggered);
    }

    [Fact]
    public void ChannelCountMustNotChangeMidSession()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var packet = new float[PacketFrames];

        Assert.Throws<ArgumentException>(() => detector.Process(packet, packet.Length, 1));
    }

    private static double SecondsBetween(long fromSamples, long toSamples) =>
        (toSamples - fromSamples) / (double)Channels / SampleRate;

    private static double Music(int frame)
    {
        double t = frame / (double)SampleRate;
        return 0.25 * (Math.Sin(2 * Math.PI * 220 * t)
                     + 0.7 * Math.Sin(2 * Math.PI * 660 * t)
                     + 0.4 * Math.Sin(2 * Math.PI * 1480 * t)) / 2.1;
    }

    /// <summary>Surface hiss, turntable rumble below the 150 Hz activity filter, and a rare click.</summary>
    private static double RunOut(int frame)
    {
        double t = frame / (double)SampleRate;
        double hiss = Hash(frame) * 0.0012;
        double rumble = 0.010 * Math.Sin(2 * Math.PI * 33 * t);
        double click = Hash(frame * 7919) > 0.99996 ? 0.25 : 0;
        return hiss + rumble + click;
    }

    /// <summary>Deterministic pseudo-noise in [-1, 1), so these tests never flake.</summary>
    private static double Hash(int value)
    {
        unchecked
        {
            uint x = (uint)value * 2654435761u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return x / (double)uint.MaxValue * 2 - 1;
        }
    }

    private sealed class Feeder(RunOutDetector detector)
    {
        private int _frame;

        public long TotalSamples { get; private set; }

        public void Play(Func<int, double> generator, double seconds)
        {
            int packets = (int)(seconds * SampleRate / PacketFrames);
            var packet = new float[PacketFrames * Channels];
            for (int index = 0; index < packets; index++)
            {
                for (int frame = 0; frame < PacketFrames; frame++)
                {
                    float value = (float)generator(_frame + frame);
                    packet[frame * Channels] = value;
                    packet[frame * Channels + 1] = (float)(value * 0.97);
                }
                _frame += PacketFrames;
                TotalSamples += packet.Length;
                if (detector.Process(packet, packet.Length, Channels)) return;
            }
        }
    }
}
