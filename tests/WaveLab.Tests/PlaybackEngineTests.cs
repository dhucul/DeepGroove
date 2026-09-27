using System.Diagnostics;
using System.Reflection;
using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class PlaybackEngineTests
{
    [Fact]
    public async Task AsynchronousCleanupWaitKeepsPendingTasksAfterCancellationAndDrainsThemWhenReady()
    {
        using var engine = new PlaybackEngine();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingCleanups(engine).Add(gate.Task);
        using var cancellation = new CancellationTokenSource();
        Task waiting = engine.WaitForOutputReleaseAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Contains(gate.Task, PendingCleanups(engine));
        gate.TrySetResult();
        await engine.WaitForOutputReleaseAsync(default);
        Assert.Empty(PendingCleanups(engine));
    }

    [Fact]
    public async Task AsynchronousCleanupWaitIncludesNewlyQueuedWorkAndToleratesCleanupFaults()
    {
        using var engine = new PlaybackEngine();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingCleanups(engine).Add(first.Task);
        Task waiting = engine.WaitForOutputReleaseAsync(default);
        PendingCleanups(engine).Add(second.Task);
        first.SetException(new IOException("Cleanup failed"));
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);
        second.TrySetResult();
        await waiting;
        Assert.Empty(PendingCleanups(engine));
    }

    [Fact]
    public void PlaybackDoesNotOpenAnotherStreamWhenCleanupTimesOut()
    {
        using var engine = new PlaybackEngine();
        var cleanupGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PendingCleanups(engine).Add(cleanupGate.Task);
        var document = new AudioDocument([[0f]], 48_000, 32);

        try
        {
            var watch = Stopwatch.StartNew();
            var error = Assert.Throws<PlaybackDeviceBusyException>(
                () => engine.Play(document, 0, document.Length));
            watch.Stop();

            Assert.Equal(
                "The previous output stream is still releasing its device. Try Play again in a moment.",
                error.Message);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(3_500),
                $"Play spent more than one cleanup budget ({watch.Elapsed.TotalMilliseconds:0} ms).");
        }
        finally
        {
            cleanupGate.TrySetResult();
        }
    }

    [Fact]
    public void StopDoesNotWaitForPendingEndpointCleanup()
    {
        using var engine = new PlaybackEngine();
        var cleanupGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PendingCleanups(engine).Add(cleanupGate.Task);

        try
        {
            var watch = Stopwatch.StartNew();
            engine.Stop();
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Stop waited {watch.Elapsed.TotalMilliseconds:0} ms for endpoint cleanup.");
        }
        finally
        {
            cleanupGate.TrySetResult();
        }
    }

    private static List<Task> PendingCleanups(PlaybackEngine engine) =>
        Assert.IsType<List<Task>>(
            typeof(PlaybackEngine)
                .GetField("_pendingCleanupTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine));

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
