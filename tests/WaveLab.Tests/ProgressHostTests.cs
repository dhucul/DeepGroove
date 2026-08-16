using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class ProgressHostTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private ProgressHost NewHost() => new(() => _now);
    private void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    // ── the show delay ───────────────────────────────────────────

    [Fact]
    public async Task FastWorkNeverShowsAnything()
    {
        var host = NewHost();
        bool ranWhileHidden = false;

        await host.RunBlockingAsync("Trimming", null, (_, _) =>
        {
            host.Tick();
            ranWhileHidden = host.Blocking == null;
            return Task.CompletedTask;
        });

        Assert.True(ranWhileHidden, "an operation shorter than the show delay must stay silent");
        Assert.Null(host.Blocking);
        Assert.False(host.IsBlockingVisible);
    }

    [Fact]
    public async Task WorkThatOutlivesTheDelayBecomesVisible()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        Task running = host.RunBlockingAsync("Rendering master chain", "3 effects", (_, _) => gate.Task);

        host.Tick();
        Assert.Null(host.Blocking);

        Advance(0.5);
        host.Tick();

        Assert.NotNull(host.Blocking);
        Assert.True(host.IsBlockingVisible);
        Assert.Equal("Rendering master chain", host.Blocking!.Title);
        Assert.Equal("3 effects", host.Blocking.Detail);
        Assert.True(host.Blocking.HasDetail);

        gate.SetResult();
        await running;
        Assert.Null(host.Blocking);
        Assert.False(host.IsBlockingVisible);
    }

    // ── determinate / indeterminate ──────────────────────────────

    [Fact]
    public async Task ProgressStaysIndeterminateUntilSomethingIsReported()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        IProgress<double>? reporter = null;
        Task running = host.RunBlockingAsync("Opening file", null, (progress, _) =>
        {
            reporter = progress;
            return gate.Task;
        });

        Advance(1);
        host.Tick();
        Assert.True(host.Blocking!.IsIndeterminate);
        Assert.Equal("Working…", host.Blocking.PercentText);

        reporter!.Report(0.42);
        host.Tick();

        Assert.False(host.Blocking.IsIndeterminate);
        Assert.Equal(0.42, host.Blocking.Fraction, 6);
        Assert.Equal("42%", host.Blocking.PercentText);

        gate.SetResult();
        await running;
    }

    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(1.7, 1.0)]
    public async Task ReportedValuesOutsideTheUnitRangeAreClamped(double reported, double expected)
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        IProgress<double>? reporter = null;
        Task running = host.RunBlockingAsync("Working", null, (progress, _) => { reporter = progress; return gate.Task; });

        Advance(1);
        reporter!.Report(reported);
        host.Tick();

        Assert.Equal(expected, host.Blocking!.Fraction, 6);

        gate.SetResult();
        await running;
    }

    // ── time remaining ───────────────────────────────────────────

    [Fact]
    public async Task NoEstimateIsOfferedBeforeItWouldBeWorthTrusting()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        IProgress<double>? reporter = null;
        Task running = host.RunBlockingAsync("Converting", null, (progress, _) => { reporter = progress; return gate.Task; });

        // Plenty of elapsed time but almost no progress: dividing here would produce a wild number.
        Advance(10);
        reporter!.Report(0.01);
        host.Tick();
        Assert.Contains("elapsed", host.Blocking!.RemainingText);

        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task AnEstimateAppearsOnceProgressAndTimeBothQualify()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        IProgress<double>? reporter = null;
        Task running = host.RunBlockingAsync("Converting", null, (progress, _) => { reporter = progress; return gate.Task; });

        Advance(10);
        reporter!.Report(0.5);
        host.Tick();

        // Half done after ten seconds, so about ten seconds to go.
        Assert.Equal("about 10 s left", host.Blocking!.RemainingText);

        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task LongEstimatesAreGivenInMinutesAndSeconds()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        IProgress<double>? reporter = null;
        Task running = host.RunBlockingAsync("Converting", null, (progress, _) => { reporter = progress; return gate.Task; });

        Advance(10);
        reporter!.Report(0.1);      // 10 % in ten seconds → ninety seconds left
        host.Tick();

        Assert.Equal("about 1 m 30 s left", host.Blocking!.RemainingText);

        gate.SetResult();
        await running;
    }

    // ── cancellation ─────────────────────────────────────────────

    [Fact]
    public async Task CancellingLatchesAndSaysSoWithoutClaimingToHaveStopped()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        CancellationToken observed = default;
        Task running = host.RunBlockingAsync("Applying effect chain", null, (_, token) =>
        {
            observed = token;
            return gate.Task;
        });

        Advance(1);
        host.Tick();
        OperationProgress operation = host.Blocking!;
        Assert.True(operation.CancelCommand.CanExecute(null));

        operation.Cancel();
        host.Tick();

        Assert.True(operation.IsCancelling);
        Assert.True(observed.IsCancellationRequested);
        Assert.Equal("cancelling…", operation.RemainingText);
        Assert.False(operation.CancelCommand.CanExecute(null));

        // The operation is still on screen: it has not actually stopped yet.
        Assert.NotNull(host.Blocking);

        gate.SetResult();
        await running;
        Assert.Null(host.Blocking);
    }

    [Fact]
    public async Task CancellingTwiceIsHarmless()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        Task running = host.RunBlockingAsync("Working", null, (_, _) => gate.Task);

        Advance(1);
        host.Tick();
        host.Blocking!.Cancel();
        host.Blocking.Cancel();

        Assert.True(host.Blocking.IsCancelling);
        gate.SetResult();
        await running;
    }

    // ── slots ────────────────────────────────────────────────────

    [Fact]
    public async Task BlockingAndBackgroundAreSeparateSlots()
    {
        var host = NewHost();
        var blockingGate = new TaskCompletionSource();
        var backgroundGate = new TaskCompletionSource();

        Task blocking = host.RunBlockingAsync("Rendering", null, (_, _) => blockingGate.Task);
        Task background = host.RunBackgroundAsync("Rebuilding peaks", null, (_, _) => backgroundGate.Task);

        Advance(1);
        host.Tick();

        Assert.Equal("Rendering", host.Blocking!.Title);
        Assert.Equal("Rebuilding peaks", host.Background!.Title);
        Assert.True(host.IsBackgroundVisible);

        backgroundGate.SetResult();
        await background;
        Assert.Null(host.Background);
        Assert.NotNull(host.Blocking);

        blockingGate.SetResult();
        await blocking;
        Assert.Null(host.Blocking);
    }

    [Fact]
    public async Task AStillRunningOperationTakesOverTheSlotWhenTheVisibleOneFinishes()
    {
        var host = NewHost();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();

        Task a = host.RunBlockingAsync("First", null, (_, _) => first.Task);
        Task b = host.RunBlockingAsync("Second", null, (_, _) => second.Task);

        Advance(1);
        host.Tick();
        Assert.Equal("First", host.Blocking!.Title);

        first.SetResult();
        await a;

        Assert.NotNull(host.Blocking);
        Assert.Equal("Second", host.Blocking!.Title);

        second.SetResult();
        await b;
        Assert.Null(host.Blocking);
    }

    [Fact]
    public async Task AFailingOperationStillClearsTheSlot()
    {
        var host = NewHost();
        var gate = new TaskCompletionSource();
        Task running = host.RunBlockingAsync("Rendering", null, (_, _) => gate.Task);

        Advance(1);
        host.Tick();
        Assert.NotNull(host.Blocking);

        gate.SetException(new InvalidOperationException("render failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        Assert.Null(host.Blocking);
        Assert.False(host.IsBlockingVisible);
    }

    [Fact]
    public void TickWithNothingRunningIsHarmless()
    {
        var host = NewHost();
        host.Tick();
        Assert.Null(host.Blocking);
        Assert.Null(host.Background);
    }

    [Fact]
    public async Task VisibilityChangesRaiseNotifications()
    {
        var host = NewHost();
        var raised = new List<string>();
        host.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        var gate = new TaskCompletionSource();
        Task running = host.RunBlockingAsync("Rendering", null, (_, _) => gate.Task);
        Advance(1);
        host.Tick();

        Assert.Contains(nameof(ProgressHost.Blocking), raised);
        Assert.Contains(nameof(ProgressHost.IsBlockingVisible), raised);

        gate.SetResult();
        await running;
    }
}
