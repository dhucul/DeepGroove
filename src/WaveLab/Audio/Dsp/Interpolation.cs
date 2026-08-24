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
    /// <remarks>
    /// <b>The kernel is built once, not per sample.</b> The read position is <c>i - delay</c> for
    /// integer <c>i</c> and a delay that does not move, so the fraction is the same at every output
    /// sample and so is the kernel. Calling <see cref="At"/> in a loop rebuilds it regardless — a
    /// sine and three cosines per tap, thirty-two taps, for an answer identical to the last one.
    /// Measured on thirty seconds of mono that is 651 ms against 38 ms, a factor of seventeen, with
    /// the outputs agreeing to 1e-7; on a five-minute stereo transfer it is the difference between
    /// thirteen seconds of azimuth correction and under one.
    /// </remarks>
    public static float[] Shift(float[] signal, double delay, int halfTaps = DefaultHalfTaps)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var result = new float[signal.Length];
        if (Math.Abs(delay) < 1e-12)
        {
            signal.CopyTo(result, 0);
            return result;
        }

        int baseCentre = (int)Math.Floor(-delay);
        double fraction = -delay - baseCentre;

        var kernel = new double[halfTaps * 2];
        double weight = 0;
        for (int k = -halfTaps + 1, t = 0; k <= halfTaps; k++, t++)
        {
            double x = fraction - k;
            kernel[t] = Sinc(x) * Window(x, halfTaps);
            weight += kernel[t];
        }

        // Normalised once, for the reason At normalises every time: without it the interpolator has
        // a small ripple in its DC response and a constant does not read back as itself.
        if (Math.Abs(weight) > 1e-12)
            for (int t = 0; t < kernel.Length; t++) kernel[t] /= weight;

        for (int i = 0; i < signal.Length; i++)
        {
            int centre = i + baseCentre;
            double sum = 0;
            for (int k = -halfTaps + 1, t = 0; k <= halfTaps; k++, t++)
            {
                int index = centre + k;
                // Outside the signal reads as silence, as in At: a correction that runs off either
                // end fades rather than smearing the first or last sample across the gap.
                if ((uint)index < (uint)signal.Length) sum += kernel[t] * signal[index];
            }
            result[i] = (float)sum;
        }
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

    /// <summary>
    /// The signal at <paramref name="position"/>, read through a kernel table rather than a kernel
    /// built on the spot. Same interpolator, same edge behaviour, for callers that read every sample
    /// of a side at a fraction that moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Shift"/> can hoist its kernel because a constant delay has a constant fraction.
    /// A drifting correction cannot: the fraction moves with the position, so the kernel is rebuilt
    /// at every one of tens of millions of samples — a sine and three cosines per tap, thirty-two
    /// taps — and that, not the transform, is what makes a wow correction take minutes.
    /// </para>
    /// <para>
    /// So the kernel is tabulated at <see cref="Phases"/> fractions and <b>interpolated between the
    /// two straddling it</b> rather than snapped to the nearer. Snapping is the obvious version and
    /// it is a timing error of up to half a phase — small, but it moves as the correction drifts,
    /// which is the one kind of error this interpolator exists to avoid. Blending is a convex
    /// combination of two kernels that each sum to one, so it sums to one, keeps the DC response
    /// that <see cref="At"/> normalises for, and leaves the residual second order in the phase step.
    /// </para>
    /// <para>
    /// <see cref="At"/> stays exact and is what the tests measure the interpolator against. This is
    /// the one to reach for in a loop over a whole file.
    /// </para>
    /// </remarks>
    public static double AtTabulated(ReadOnlySpan<float> signal, double position)
    {
        if (signal.Length == 0) return 0;

        int centre = (int)Math.Floor(position);
        double fraction = position - centre;

        // On a sample exactly, no interpolation is needed and none should be done.
        if (fraction < 1e-12 && (uint)centre < (uint)signal.Length) return signal[centre];

        double exact = fraction * Phases;
        int phase = (int)exact;
        if (phase >= Phases) phase = Phases - 1;
        double blend = exact - phase;

        double[] kernels = Kernels;
        int low = phase * Taps;
        int high = low + Taps;

        double sum = 0;
        for (int k = -DefaultHalfTaps + 1, t = 0; k <= DefaultHalfTaps; k++, t++)
        {
            int index = centre + k;
            if ((uint)index < (uint)signal.Length)
            {
                double tap = kernels[low + t];
                sum += (tap + (kernels[high + t] - tap) * blend) * signal[index];
            }
        }
        return sum;
    }

    /// <summary>
    /// Fractions the kernel is tabulated at. Half a megabyte at the default tap count, built once.
    /// </summary>
    private const int Phases = 2048;

    private const int Taps = DefaultHalfTaps * 2;

    /// <remarks>
    /// One row past <see cref="Phases"/>, holding the kernel for a fraction of exactly one, so the
    /// blend above can always read the row after the one it landed on without a bounds test.
    /// </remarks>
    private static readonly double[] Kernels = BuildKernels();

    private static double[] BuildKernels()
    {
        var table = new double[(Phases + 1) * Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double fraction = p / (double)Phases;
            int at = p * Taps;
            double weight = 0;

            for (int k = -DefaultHalfTaps + 1, t = 0; k <= DefaultHalfTaps; k++, t++)
            {
                double x = fraction - k;
                table[at + t] = Sinc(x) * Window(x, DefaultHalfTaps);
                weight += table[at + t];
            }

            // Normalised here rather than per read, which is the other half of the saving.
            if (Math.Abs(weight) > 1e-12)
                for (int t = 0; t < Taps; t++) table[at + t] /= weight;
        }
        return table;
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
