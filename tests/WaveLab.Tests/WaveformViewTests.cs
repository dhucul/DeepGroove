using System.Windows;
using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

public sealed class WaveformViewTests
{
    private sealed record BuildRequest(string Name, long Epoch, WaveformView.GeometryWindow Window);

    [Fact]
    public void GeometryWindowOverscansAndCoversSmallPlaybackMoves()
    {
        WaveformView.GeometryWindow window = WaveformView.CalculateGeometryWindow(
            documentLength: 100_000,
            viewStart: 20_000,
            samplesPerPixel: 10,
            viewWidth: 100);

        Assert.Equal(19_500, window.StartSample);
        Assert.Equal(21_500, window.EndSample);
        Assert.Equal(200, window.PixelWidth);
        Assert.True(WaveformView.GeometryWindowCovers(window, 20_500, 21_500));
        Assert.False(WaveformView.GeometryWindowCovers(window, 20_501, 21_501));
    }

    [Fact]
    public void GeometryWindowShiftsBackAtDocumentEnd()
    {
        WaveformView.GeometryWindow window = WaveformView.CalculateGeometryWindow(
            documentLength: 100_000,
            viewStart: 99_000,
            samplesPerPixel: 10,
            viewWidth: 100);

        Assert.Equal(98_000, window.StartSample);
        Assert.Equal(100_000, window.EndSample);
        Assert.True(WaveformView.GeometryWindowCovers(window, 99_000, 100_000));
    }

    [Fact]
    public void ForwardGeometryWindowProvidesPlaybackLookahead()
    {
        WaveformView.GeometryWindow window = WaveformView.CalculateForwardGeometryWindow(
            documentLength: 100_000,
            viewStart: 20_000,
            samplesPerPixel: 10,
            viewWidth: 100);

        Assert.Equal(19_750, window.StartSample);
        Assert.Equal(21_750, window.EndSample);
        Assert.Equal(200, window.PixelWidth);
        Assert.True(WaveformView.GeometryWindowCovers(window, 20_750, 21_750));
    }

    [Fact]
    public void BuildQueueRunsOneRequestAndKeepsOnlyTheLatestPendingRequest()
    {
        var queue = new WaveformView.LatestBuildQueue<BuildRequest>();
        var first = new BuildRequest("first", 1, new(0, 100, 100));
        var superseded = new BuildRequest("superseded", 1, new(100, 200, 100));
        var latest = new BuildRequest("latest", 1, new(200, 300, 100));

        Assert.True(queue.Enqueue(first));
        Assert.Same(first, queue.TryStartNext());
        Assert.Null(queue.TryStartNext());

        Assert.True(queue.Enqueue(superseded));
        Assert.True(queue.Enqueue(latest));
        Assert.Same(first, queue.Active);
        Assert.Same(latest, queue.Pending);

        queue.Complete(first);
        Assert.Same(latest, queue.TryStartNext());
        Assert.Null(queue.Pending);
    }

    [Theory]
    [InlineData(4, 5, true)]
    [InlineData(5, 5, false)]
    public void StaleEpochOrKeyIsRejected(long requestEpoch, long currentEpoch, bool keyMatches)
    {
        var disposition = WaveformView.ClassifyGeometryBuild(
            requestEpoch,
            currentEpoch,
            keyMatches,
            new WaveformView.GeometryWindow(100, 300, 200),
            viewStart: 150,
            viewEnd: 250);

        Assert.Equal(WaveformView.GeometryBuildDisposition.Reject, disposition);
    }

    [Fact]
    public void ReturningToAnOldZoomQueuesAFreshEpochInsteadOfWaitingOnStaleWork()
    {
        var queue = new WaveformView.LatestBuildQueue<BuildRequest>();
        var oldZoom = new BuildRequest("zoom B, old epoch", 1, new(100, 300, 200));
        var otherZoom = new BuildRequest("zoom C", 2, new(100, 300, 200));
        var freshZoom = new BuildRequest("zoom B, fresh epoch", 3, new(100, 300, 200));

        queue.Enqueue(oldZoom);
        Assert.Same(oldZoom, queue.TryStartNext());
        queue.Enqueue(otherZoom);

        queue.ClearPending();
        queue.Enqueue(freshZoom);

        Assert.Equal(
            WaveformView.GeometryBuildDisposition.Reject,
            WaveformView.ClassifyGeometryBuild(
                oldZoom.Epoch, 3, keyMatches: true, resultWindow: oldZoom.Window,
                viewStart: 150, viewEnd: 250));
        Assert.Same(freshZoom, queue.Pending);
    }

    [Fact]
    public async Task GeometryBuiltOnAWorkerIsFrozenForSafeUiThreadUse()
    {
        var top = new[] { new Point(0, 1), new Point(1, 2) };
        var bottom = new[] { new Point(0, 3), new Point(1, 4) };

        var geometry = await Task.Run(() => WaveformView.BuildBand(top, bottom));

        Assert.True(geometry.IsFrozen);
        Assert.True(geometry.CanFreeze);
    }

    [Fact]
    public void UncoveredResultRetriesAndFinalViewResultCanBeApplied()
    {
        var queue = new WaveformView.LatestBuildQueue<BuildRequest>();
        var oldWindow = new WaveformView.GeometryWindow(0, 200, 200);
        var finalWindow = new WaveformView.GeometryWindow(300, 600, 300);
        var oldRequest = new BuildRequest("old view", 7, oldWindow);
        var finalRequest = new BuildRequest("final view", 7, finalWindow);
        var processed = new List<BuildRequest>();
        BuildRequest? applied = null;

        queue.Enqueue(oldRequest);
        while (queue.TryStartNext() is { } request)
        {
            processed.Add(request);
            var disposition = WaveformView.ClassifyGeometryBuild(
                request.Epoch, 7, true, request.Window,
                viewStart: 350, viewEnd: 550);
            if (disposition == WaveformView.GeometryBuildDisposition.RetryForCurrentView)
                queue.Enqueue(finalRequest);
            else if (disposition == WaveformView.GeometryBuildDisposition.Apply)
                applied = request;
            queue.Complete(request);
        }

        Assert.Equal([oldRequest, finalRequest], processed);
        Assert.Same(finalRequest, applied);
        Assert.Null(queue.Active);
        Assert.Null(queue.Pending);
    }
}
