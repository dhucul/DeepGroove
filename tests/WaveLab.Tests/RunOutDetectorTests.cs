using WaveLab.Audio;
using WaveLab.Audio.Dsp;
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

        Assert.False(detector.PreservedFadingTail);
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

    /// <summary>
    /// The failure this detector was rebuilt for: a side whose last track fades
    /// out. The fade is programme all the way down to the groove noise, and a
    /// floor read from the last half minute of music puts the threshold inside
    /// the music's own dynamic range, so the fade is trimmed off where it drops
    /// below the song's average level.
    /// </summary>
    [Fact]
    public void AFadeOutIsNotARunOut()
    {
        const double FadeSeconds = 15;
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);

        feed.Play(RunOut, seconds: 5);
        feed.Play(DynamicMusic, seconds: 60); // long enough that any window has lost the lead-in

        long fadeStart = feed.Frames;
        feed.Play(frame =>
        {
            double through = Math.Clamp((frame - fadeStart) / (FadeSeconds * SampleRate), 0, 1);
            return DynamicMusic(frame) * Math.Pow(10, -30 * through / 20);
        }, seconds: FadeSeconds);

        feed.Play(RunOut, seconds: 40);

        Assert.True(detector.IsTriggered);
        long kept = feed.TotalSamples - detector.TrimBackoffSamples;
        double keptPastFadeStart = SecondsBetween(fadeStart * Channels, kept);
        Assert.True(
            keptPastFadeStart >= FadeSeconds,
            $"the fade was cut {FadeSeconds - keptPastFadeStart:0.0} s early");
    }

    [Fact]
    public void AFlatBassHeavyFadeIsKeptUntilItEnds()
    {
        const double ProgramSeconds = 20;
        const double FadeSeconds = 30;
        int frames = (int)((ProgramSeconds + FadeSeconds) * SampleRate);
        var left = new float[frames];
        var right = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            double t = frame / (double)SampleRate;
            double through = Math.Clamp((t - ProgramSeconds) / FadeSeconds, 0, 1);
            double fade = 1 - through;
            double music = 0.20 * (Math.Sin(2 * Math.PI * 80 * t)
                                   + 0.55 * Math.Sin(2 * Math.PI * 160 * t)
                                   + 0.30 * Math.Sin(2 * Math.PI * 320 * t)) / 1.85;
            left[frame] = (float)(music * fade);
            right[frame] = (float)(music * fade * 0.97);
        }

        float[][] flat = [left, right];
        RecordingCurves.Apply(flat, RecordingCurves.Spec(RecordingCurve.Riaa), SampleRate,
            CurveDirection.Record, CurvePhase.Minimum);
        for (int frame = 0; frame < frames; frame++)
        {
            left[frame] += (float)RunOut(frame);
            right[frame] += (float)(RunOut(frame + 17) * 0.97);
        }

        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(RunOut, seconds: 5);
        long fadeEnd = feed.TotalSamples + (long)frames * Channels;
        bool consumedCompleteFade = feed.Play(flat);

        Assert.True(consumedCompleteFade,
            $"recording stopped {SecondsBetween(feed.TotalSamples, fadeEnd):0.0} s before the flat fade ended");
        Assert.Equal(fadeEnd, feed.TotalSamples);
        feed.Play(RunOut, seconds: 40);

        Assert.True(detector.IsTriggered);
        Assert.True(detector.PreservedFadingTail);
        long kept = feed.TotalSamples - detector.TrimBackoffSamples;
        Assert.True(detector.TrimBackoffSamples > 0,
            "a protected fade must still discard the safety hold after its confirmed endpoint");
        Assert.InRange(SecondsBetween(fadeEnd, kept), 1.0, 3.0);
    }

    /// <summary>
    /// The mechanism behind that failure on its own: ordinary programme has
    /// enough spread between its loud and quiet blocks to look like a floor and
    /// a programme, so a quiet passage must not be measured against the loud
    /// parts of the same music.
    /// </summary>
    [Fact]
    public void MusicsOwnDynamicsAreNotItsNoiseFloor()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(RunOut, seconds: 5);
        feed.Play(DynamicMusic, seconds: 60);

        feed.Play(frame => DynamicMusic(frame) * 0.05, seconds: 25);

        Assert.False(detector.IsTriggered);
    }

    /// <summary>
    /// The floor is learned from the take rather than from a window, so it has
    /// to survive a track longer than any window it was once read from.
    /// </summary>
    [Fact]
    public void TheRunOutIsStillFoundAfterATrackLongerThanAnyWindow()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);
        feed.Play(RunOut, seconds: 5);
        feed.Play(DynamicMusic, seconds: 90);
        long musicEnd = feed.TotalSamples;

        feed.Play(RunOut, seconds: 40);

        Assert.True(detector.IsTriggered);
        Assert.InRange(SecondsBetween(musicEnd, feed.TotalSamples), Hold - 1, Hold + 1.5);
    }

    /// <summary>
    /// Arming the recorder and then cueing the stylus by hand leaves dead input
    /// at the head of the take. The floor only ratchets down, so admitting that
    /// silence would latch it below anything a disc can produce and pin the gate
    /// at the absolute minimum for the whole side — which a transfer whose
    /// groove noise is louder than that minimum never stops against.
    /// <para>The levels here are chosen so that only the learned floor can
    /// separate them: the run-out clears the absolute minimum, so the fallback
    /// gate would call it programme, and it carries a musical zero-crossing rate
    /// and a peak well above the minimum, so neither of the other two tests
    /// rejects it either.</para>
    /// </summary>
    [Fact]
    public void SilenceBeforeTheStylusLandsIsNotTheFloor()
    {
        var detector = new RunOutDetector(SampleRate, Channels, Hold);
        var feed = new Feeder(detector);

        feed.Play(_ => 0, seconds: 5);                            // armed, nothing playing yet
        feed.Play(frame => Music(frame) * 0.01, seconds: 8);      // lead-in groove, about -60 dB
        feed.Play(Music, seconds: 20);
        long musicEnd = feed.TotalSamples;

        feed.Play(frame => Music(frame) * 0.022, seconds: 40);    // groove floor, about -53 dB

        Assert.True(detector.IsTriggered);
        Assert.InRange(SecondsBetween(musicEnd, feed.TotalSamples), Hold - 1, Hold + 1.5);
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

    /// <summary>
    /// The same programme with about 12 dB of block-to-block dynamics — enough
    /// that a low percentile of it looks like a noise floor to anything that
    /// only measures the spread between its quiet and loud blocks.
    /// </summary>
    private static double DynamicMusic(int frame)
    {
        double t = frame / (double)SampleRate;
        return Music(frame) * (0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 4)));
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

        /// <summary>Frames played, which is where the next generator call starts.</summary>
        public long Frames => TotalSamples / Channels;

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

        public bool Play(float[][] channels)
        {
            Assert.Equal(Channels, channels.Length);
            Assert.Equal(channels[0].Length, channels[1].Length);
            var packet = new float[PacketFrames * Channels];
            for (int offset = 0; offset < channels[0].Length; offset += PacketFrames)
            {
                int packetFrames = Math.Min(PacketFrames, channels[0].Length - offset);
                for (int frame = 0; frame < packetFrames; frame++)
                {
                    packet[frame * Channels] = channels[0][offset + frame];
                    packet[frame * Channels + 1] = channels[1][offset + frame];
                }
                int samples = packetFrames * Channels;
                _frame += packetFrames;
                TotalSamples += samples;
                if (detector.Process(packet, samples, Channels)) return false;
            }
            return true;
        }
    }
}
