namespace WaveLab.Util;

/// <summary>
/// Forwards progress synchronously into a slice of an outer range — for work split into stages or
/// across channels, where each part reports 0 to 1 of itself.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="Progress{T}"/>. That posts every report to the captured
/// synchronization context, so on a worker thread the values arrive late, out of order, and on
/// somebody else's thread — and where the report is only being stored for a timer to read, as it is
/// throughout this app, the whole round trip is waste. It also makes progress untestable without
/// pumping a dispatcher, which is how this was found.
/// </remarks>
public sealed class SubProgress(IProgress<double>? outer, double offset, double span) : IProgress<double>
{
    public void Report(double value)
    {
        if (outer is null) return;
        outer.Report(Math.Clamp(offset + value * span, 0, 1));
    }

    /// <summary>The slice belonging to <paramref name="index"/> of <paramref name="count"/> parts.</summary>
    public static SubProgress Slice(IProgress<double>? outer, int index, int count)
    {
        double span = 1.0 / Math.Max(1, count);
        return new SubProgress(outer, index * span, span);
    }
}
