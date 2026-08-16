using WaveLab.Util;

namespace WaveLab.ViewModels;

/// <summary>
/// One long-running operation's bindable state: what it is, how far along, how much longer, and the
/// token that stops it.
/// </summary>
/// <remarks>
/// <para>
/// Progress arrives from a worker thread and is only <em>stored</em> here — the bound text is
/// recomputed by <see cref="Refresh"/> on the UI thread at a fixed rate. That indirection is the
/// point: the DSP loops report progress every few thousand samples, and marshalling each of those
/// through <c>Progress&lt;T&gt;</c> would post tens of thousands of dispatcher callbacks per render
/// and starve the meters and the playhead, which is exactly the failure `CLAUDE.md` warns about for
/// the waveform's invalidation path.
/// </para>
/// <para>
/// Cancellation is a request. Asking for it latches <see cref="IsCancelling"/> and cancels the token,
/// but the operation goes on until it reaches its next cancellation point, and the UI says so rather
/// than pretending it has already stopped.
/// </para>
/// </remarks>
public sealed class OperationProgress : ObservableObject, IProgress<double>
{
    /// <summary>How long an operation must run before it is worth interrupting the user for.</summary>
    public const double ShowDelaySeconds = 0.4;

    // An estimate made from the first one percent of a render is worse than no estimate at all, so
    // none is offered until there is enough of both progress and elapsed time to divide by.
    private const double MinimumFractionForEstimate = 0.05;
    private const double MinimumSecondsForEstimate = 3.0;
    private const double EstimateSmoothing = 0.25;

    private readonly CancellationTokenSource _cancellation = new();
    private double _reported = -1;          // written by the worker, read by Refresh
    private double _smoothedRemaining = -1;

    private double _fraction;
    private bool _isIndeterminate = true;
    private string _percentText = "Working…";
    private string _remainingText = "0 s elapsed";
    private bool _isCancelling;

    public OperationProgress(string title, string? detail, DateTime startedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title;
        Detail = detail;
        StartedUtc = startedUtc;
        CancelCommand = new RelayCommand(Cancel, () => !_isCancelling);
    }

    public string Title { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    internal DateTime StartedUtc { get; }
    public CancellationToken Token => _cancellation.Token;
    public RelayCommand CancelCommand { get; }

    /// <summary>0..1 once known; meaningless while <see cref="IsIndeterminate"/>.</summary>
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    /// <summary>True until the operation reports a figure. Several paths never do.</summary>
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }

    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }
    public bool IsCancelling { get => _isCancelling; private set => Set(ref _isCancelling, value); }

    /// <summary>Called from the worker thread; stores the value without touching the dispatcher.</summary>
    public void Report(double value) => Volatile.Write(ref _reported, value);

    public void Cancel()
    {
        if (_isCancelling) return;
        IsCancelling = true;
        CancelCommand.RaiseCanExecuteChanged();
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* the operation finished first */ }
    }

    /// <summary>Recomputes the bound text from whatever the worker has reported. UI thread only.</summary>
    internal void Refresh(DateTime nowUtc)
    {
        double elapsed = Math.Max(0, (nowUtc - StartedUtc).TotalSeconds);
        double reported = Volatile.Read(ref _reported);

        if (reported >= 0)
        {
            double fraction = Math.Clamp(reported, 0, 1);
            IsIndeterminate = false;
            Fraction = fraction;
            PercentText = $"{fraction * 100:0}%";
        }
        else
        {
            IsIndeterminate = true;
            Fraction = 0;
            PercentText = "Working…";
        }

        if (IsCancelling)
        {
            RemainingText = "cancelling…";
            return;
        }

        if (!IsIndeterminate && Fraction >= MinimumFractionForEstimate && elapsed >= MinimumSecondsForEstimate)
        {
            double remaining = elapsed * (1 - Fraction) / Fraction;
            _smoothedRemaining = _smoothedRemaining < 0
                ? remaining
                : _smoothedRemaining + EstimateSmoothing * (remaining - _smoothedRemaining);
            RemainingText = $"about {Describe(_smoothedRemaining)} left";
        }
        else
        {
            RemainingText = $"{Describe(elapsed)} elapsed";
        }
    }

    private static string Describe(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        int total = (int)Math.Round(seconds);
        return total >= 60 ? $"{total / 60} m {total % 60:00} s" : $"{total} s";
    }

    internal void DisposeToken()
    {
        try { _cancellation.Dispose(); }
        catch (ObjectDisposedException) { }
    }
}
