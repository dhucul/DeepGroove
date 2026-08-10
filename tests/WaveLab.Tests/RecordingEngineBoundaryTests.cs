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
