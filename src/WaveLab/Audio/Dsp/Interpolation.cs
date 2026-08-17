namespace WaveLab.Audio.Dsp;

/// <summary>
/// Band-limited interpolation: reading a signal at a position between samples.
/// </summary>
/// <remarks>
/// <para>
/// The primitive underneath azimuth correction and wow-and-flutter correction alike. Both need to
/// read a signal at a fractional position — one by a constant offset, the other by an offset that
/// drifts — and both are ruined by an interpolator that is not band-limited. Linear interpolation
/// between neighbouring samples is a comb filter whose response depends on the fraction: it takes
/// several dB off the top octave and moves as the fraction moves, which on a drifting correction is
/// heard as the top end breathing.
/// </para>
/// <para>
/// A windowed sinc is the honest answer. The sinc is the ideal interpolator and the window is what
/// makes it finite; the length sets how close to ideal it gets, and is measured rather than assumed
/// in <c>InterpolationTests</c>.
/// </para>
/// </remarks>
public static class Interpolation
{
    /// <summary>Taps either side of the read position. Sixteen is the default half-length.</summary>
    public const int DefaultHalfTaps = 16;

    /// <summary>
    /// The signal at <paramref name="position"/>, in samples. Positions outside the signal read as
    /// silence rather than wrapping or clamping, so a correction that runs off either end fades
    /// instead of smearing the first or last sample across the gap.
    /// </summary>
    public static double At(ReadOnlySpan<float> signal, double position, int halfTaps = DefaultHalfTaps)
    {
        if (signal.Length == 0) return 0;

        int centre = (int)Math.Floor(position);
        double fraction = position - centre;

        // On a sample exactly, no interpolation is needed and none should be done: running the
        // kernel would only add its own truncation error to an answer already known exactly.
        if (fraction < 1e-12 && (uint)centre < (uint)signal.Length) return signal[centre];

        double sum = 0, weight = 0;
        for (int k = -halfTaps + 1; k <= halfTaps; k++)
        {
            int index = centre + k;
            double x = fraction - k;
            double tap = Sinc(x) * Window(x, halfTaps);
            weight += tap;
            if ((uint)index < (uint)signal.Length) sum += tap * signal[index];
        }

        // Normalised by the kernel's own sum, so a constant reads back as itself whatever the
        // fraction. Without this the interpolator has a small ripple in its DC response.
        return Math.Abs(weight) > 1e-12 ? sum / weight : 0;
    }

    /// <summary>Resamples <paramref name="signal"/> shifted later by <paramref name="delay"/> samples.</summary>
    public static float[] Shift(float[] signal, double delay, int halfTaps = DefaultHalfTaps)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var result = new float[signal.Length];
        if (Math.Abs(delay) < 1e-12)
        {
            signal.CopyTo(result, 0);
            return result;
        }

        for (int i = 0; i < signal.Length; i++)
            result[i] = (float)At(signal, i - delay, halfTaps);
        return result;
    }

    /// <summary>
    /// Reads a signal along a time-varying position, given by <paramref name="positionAt"/>.
    /// </summary>
    public static float[] Resample(float[] signal, Func<int, double> positionAt,
        int halfTaps = DefaultHalfTaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(positionAt);

        var result = new float[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            result[i] = (float)At(signal, positionAt(i), halfTaps);
        }
        return result;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12) return 1;
        double pi = Math.PI * x;
        return Math.Sin(pi) / pi;
    }

    /// <summary>Blackman-Harris over the kernel's span; zero outside it.</summary>
    private static double Window(double x, int halfTaps)
    {
        double t = (x + halfTaps) / (2.0 * halfTaps);
        if (t <= 0 || t >= 1) return 0;
        return 0.35875
             - 0.48829 * Math.Cos(2 * Math.PI * t)
             + 0.14128 * Math.Cos(4 * Math.PI * t)
             - 0.01168 * Math.Cos(6 * Math.PI * t);
    }
}
