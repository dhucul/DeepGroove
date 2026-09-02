using WaveLab.Util;

namespace WaveLab.ViewModels;

/// <summary>
/// The one place long operations report from. Blocking work — anything that mutates a document —
/// takes the overlay; background work takes the status strip and leaves the editor usable.
/// </summary>
/// <remarks>
/// <para>
/// The DSP layer already accepts a <see cref="CancellationToken"/> and an <see cref="IProgress{T}"/>
/// almost everywhere. What was missing was anywhere for them to go: the main-window commands
/// discarded both and set a wait cursor, so open, save, render and apply-chain froze the window with
/// no indication and no way out.
/// </para>
/// <para>
/// Nothing is shown for the first <see cref="OperationProgress.ShowDelaySeconds"/>, so trimming a
/// selection or saving a short file stays silent rather than flashing a dialog. That delay is
/// evaluated inside <see cref="Tick"/> against an injectable clock rather than by waiting, which
/// keeps the whole policy testable without real time passing.
/// </para>
/// </remarks>
public sealed class ProgressHost : ObservableObject
{
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly List<Entry> _running = [];

    private OperationProgress? _blocking;
    private OperationProgress? _background;

    private sealed class Entry(OperationProgress operation, bool isBlocking)
    {
        public OperationProgress Operation { get; } = operation;
        public bool IsBlocking { get; } = isBlocking;
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// The oldest blocking operation whether or not it has crossed the visual show delay. Lifecycle
    /// decisions must use this rather than <see cref="Blocking"/>, which is intentionally delayed.
    /// </summary>
    public OperationProgress? ActiveBlockingOperation
    {
        get
        {
            lock (_gate)
                return _running.FirstOrDefault(entry => entry.IsBlocking)?.Operation;
        }
    }

    public bool HasActiveOperations
    {
        get { lock (_gate) return _running.Count > 0; }
    }

    public ProgressHost(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>The operation owning the modal overlay, or null.</summary>
    public OperationProgress? Blocking
    {
        get => _blocking;
        private set { if (Set(ref _blocking, value)) Raise(nameof(IsBlockingVisible)); }
    }

    /// <summary>The operation shown in the status strip, or null.</summary>
    public OperationProgress? Background
    {
        get => _background;
        private set { if (Set(ref _background, value)) Raise(nameof(IsBackgroundVisible)); }
    }

    public bool IsBlockingVisible => _blocking != null;
    public bool IsBackgroundVisible => _background != null;

    /// <summary>Runs work that mutates a document, behind the overlay.</summary>
    public Task RunBlockingAsync(string title, string? detail,
        Func<IProgress<double>, CancellationToken, Task> work) =>
        RunAsync(title, detail, isBlocking: true, work);

    /// <summary>Runs work that leaves the editor usable, reported in the status strip.</summary>
    public Task RunBackgroundAsync(string title, string? detail,
        Func<IProgress<double>, CancellationToken, Task> work) =>
        RunAsync(title, detail, isBlocking: false, work);

    private async Task RunAsync(string title, string? detail, bool isBlocking,
        Func<IProgress<double>, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var operation = new OperationProgress(title, detail, _clock());
        var entry = new Entry(operation, isBlocking);
        lock (_gate) _running.Add(entry);

        try
        {
            await work(operation, operation.Token);
        }
        finally
        {
            lock (_gate) _running.Remove(entry);
            if (ReferenceEquals(Blocking, operation)) Blocking = Promote(isBlocking: true);
            if (ReferenceEquals(Background, operation)) Background = Promote(isBlocking: false);
            operation.DisposeToken();
            entry.Completion.TrySetResult();
        }
    }

    /// <summary>Requests cancellation for every operation currently owned by the host.</summary>
    public void CancelAll()
    {
        OperationProgress[] operations;
        lock (_gate) operations = [.. _running.Select(entry => entry.Operation)];
        foreach (OperationProgress operation in operations) operation.Cancel();
    }

    /// <summary>
    /// Joins the operations present when called. New operations are refused by the view model once
    /// shutdown begins; the loop also covers a completion that queued its successor just before it
    /// observed that state.
    /// </summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate) pending = [.. _running.Select(entry => entry.Completion.Task)];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// The oldest still-running operation of the given kind that has outlived the show delay, so a
    /// nested or queued operation takes over the slot rather than leaving it blank.
    /// </summary>
    private OperationProgress? Promote(bool isBlocking)
    {
        DateTime now = _clock();
        lock (_gate)
        {
            foreach (Entry entry in _running)
            {
                if (entry.IsBlocking != isBlocking) continue;
                if ((now - entry.Operation.StartedUtc).TotalSeconds >= OperationProgress.ShowDelaySeconds)
                    return entry.Operation;
            }
        }
        return null;
    }

    /// <summary>
    /// Publishes operations that have outlived the show delay and refreshes the visible ones. Drive
    /// this from a UI timer; tests call it directly with a stepped clock.
    /// </summary>
    public void Tick()
    {
        DateTime now = _clock();

        if (Blocking == null) Blocking = Promote(isBlocking: true);
        if (Background == null) Background = Promote(isBlocking: false);

        Blocking?.Refresh(now);
        Background?.Refresh(now);
    }
}
