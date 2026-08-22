using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class RecordingEngineBoundaryTests
{
    [Fact]
    public void AdvancingBoundaryDropsExactlyTheFirstPacketInTheNewEpoch()
    {
        var boundary = new RecordingEngine.CaptureDataBoundary(retainAudio: false);
        long monitorState = boundary.DataState;
        Assert.False(boundary.RetainAudio);
        Assert.False(boundary.TryConsumeBoundaryDiscard(monitorState));

        boundary.Advance(retainAudio: true);
        long recordingState = boundary.DataState;

        Assert.True(boundary.RetainAudio);
        Assert.NotEqual(monitorState, recordingState);
        Assert.False(boundary.TryConsumeBoundaryDiscard(monitorState));
        Assert.True(boundary.TryConsumeBoundaryDiscard(recordingState));
        Assert.False(boundary.TryConsumeBoundaryDiscard(recordingState));
    }

    [Fact]
    public void CallbackOwnedTransitionKeepsTheFollowingPacket()
    {
        var boundary = new RecordingEngine.CaptureDataBoundary(retainAudio: false);

        boundary.Advance(retainAudio: true, discardFirstPacket: false);
        long recordingState = boundary.DataState;

        Assert.True(boundary.RetainAudio);
        Assert.False(boundary.TryConsumeBoundaryDiscard(recordingState));
    }

    /// <summary>
    /// The audio promoted ahead of a needle drop is the head of the take, so
    /// the run-out detector has to hear it: on that path it is the only lead-in
    /// groove the noise floor is ever offered.
    /// </summary>
    [Fact]
    public void PreRollReachesTheRunOutDetector()
    {
        var detector = new RunOutDetector(PreRollRate, PreRollChannels, holdSeconds: 12);
        RunOutDetector? primed = RecordingEngine.PrimeWithPreRoll(
            detector, PreRollBlocks(Tone, seconds: 2), PreRollChannels);

        Assert.Same(detector, primed);
        Assert.True(detector.HasHeardProgram, "the pre-roll never reached the detector");
    }

    /// <summary>
    /// A detector handed to a take already triggered would end it on the first
    /// evaluation. The real pre-roll is a quarter of a second and cannot
    /// complete the shortest hold, so this is unreachable today and pinned here
    /// so that it stays a local invariant rather than one resting on two
    /// constants declared far apart.
    /// </summary>
    [Fact]
    public void APreRollThatCompletedTheHoldIsNotHandedOver()
    {
        var detector = new RunOutDetector(PreRollRate, PreRollChannels, holdSeconds: 5);
        var preRoll = new List<float[]>();
        preRoll.AddRange(PreRollBlocks(Tone, seconds: 2));
        preRoll.AddRange(PreRollBlocks(_ => 0, seconds: 12));

        Assert.Null(RecordingEngine.PrimeWithPreRoll(detector, preRoll, PreRollChannels));
        Assert.True(detector.IsTriggered);
    }

    private const int PreRollRate = 48_000;
    private const int PreRollChannels = 2;

    private static double Tone(int frame)
    {
        double t = frame / (double)PreRollRate;
        return 0.25 * (Math.Sin(2 * Math.PI * 220 * t)
                     + 0.7 * Math.Sin(2 * Math.PI * 660 * t)
                     + 0.4 * Math.Sin(2 * Math.PI * 1480 * t)) / 2.1;
    }

    /// <summary>Capture-sized blocks, as the pre-roll queue holds them.</summary>
    private static List<float[]> PreRollBlocks(Func<int, double> generator, double seconds)
    {
        const int BlockFrames = 480;
        var blocks = new List<float[]>();
        int frame = 0;
        for (int index = 0; index < seconds * PreRollRate / BlockFrames; index++)
        {
            var block = new float[BlockFrames * PreRollChannels];
            for (int inBlock = 0; inBlock < BlockFrames; inBlock++, frame++)
            {
                var value = (float)generator(frame);
                block[inBlock * PreRollChannels] = value;
                block[inBlock * PreRollChannels + 1] = value;
            }
            blocks.Add(block);
        }
        return blocks;
    }

    [Fact]
    public void CompletedOrInactiveMonitorCannotTransition()
    {
        Assert.True(RecordingEngine.CanTransitionLevelCheck(
            isCurrentSession: true,
            acceptsCallbacks: true,
            stopCompleted: false,
            engineRunning: true,
            retainsAudio: false));

        Assert.False(RecordingEngine.CanTransitionLevelCheck(true, true, true, true, false));
        Assert.False(RecordingEngine.CanTransitionLevelCheck(true, true, false, false, false));
        Assert.False(RecordingEngine.CanTransitionLevelCheck(true, true, false, true, true));
        Assert.False(RecordingEngine.CanTransitionLevelCheck(true, false, false, true, false));
        Assert.False(RecordingEngine.CanTransitionLevelCheck(false, true, false, true, false));
    }
}
