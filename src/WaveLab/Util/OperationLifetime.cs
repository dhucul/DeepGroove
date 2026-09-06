namespace WaveLab.Util;

/// <summary>Owns modeless work until its cancellation and cleanup have both finished.</summary>
public sealed class OperationLifetime
{
    private readonly object _gate = new();
    private readonly HashSet<Lease> _running = [];
    private bool _stopping;
    public bool IsStopping { get { lock (_gate) return _stopping; } }

    public IDisposable Register(CancellationTokenSource cancellation)
    {
        var lease = new Lease(this, cancellation);
        bool stopping;
        lock (_gate)
        {
            stopping = _stopping;
            if (!stopping) _running.Add(lease);
        }
        // Late UI requests receive an already-cancelled operation and cannot launch workers.
        if (stopping) cancellation.Cancel();
        return lease;
    }

    public void CancelAll()
    {
        Lease[] running;
        lock (_gate) { _stopping = true; running = [.. _running]; }
        foreach (var lease in running)
            try { lease.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate) pending = [.. _running.Select(l => l.Completion.Task)];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    private sealed class Lease(OperationLifetime owner, CancellationTokenSource cancellation) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            lock (owner._gate) owner._running.Remove(this);
            Completion.TrySetResult();
        }
    }
}
