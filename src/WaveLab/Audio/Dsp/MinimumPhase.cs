namespace WaveLab.Audio.Dsp;

/// <summary>
/// Turns a magnitude response into the unique minimum-phase impulse response that has it.
/// </summary>
/// <remarks>
/// <para>
/// A magnitude response does not determine an impulse response — infinitely many share the same
/// magnitude and differ only in phase. Exactly one of them is <b>minimum phase</b>: causal, with all
/// its energy as early as possible and a stable inverse. That is usually the one wanted, either
/// because the thing being matched was itself a minimum-phase analog network, or because a noise
/// shaper's feedback filter has to be causal to exist at all.
/// </para>
/// <para>
/// The construction is the real cepstrum. Taking the logarithm of the magnitude turns the
/// factorisation into an addition, in which the minimum-phase part is exactly the causal half; so
/// transform the log magnitude to the quefrency domain, discard the anticausal half, come back, and
/// exponentiate. What returns has the magnitude asked for and the only phase consistent with being
/// minimum phase.
/// </para>
/// </remarks>
public static class MinimumPhase
{
    /// <summary>
    /// The minimum-phase impulse response whose magnitude spectrum is <paramref name="magnitude"/>.
    /// </summary>
    /// <param name="magnitude">
    /// Magnitudes on a full, conjugate-symmetric frequency grid of power-of-two length.
    /// </param>
    public static double[] FromMagnitude(double[] magnitude)
    {
        ArgumentNullException.ThrowIfNull(magnitude);
        int size = magnitude.Length;
        if (size < 4 || (size & (size - 1)) != 0)
            throw new ArgumentException("The grid must be a power of two of at least four.", nameof(magnitude));

        var re = new double[size];
        var im = new double[size];
        for (int i = 0; i < size; i++) re[i] = Math.Log(Math.Max(magnitude[i], 1e-20));

        // Into the quefrency domain, where a minimum-phase signal is causal.
        Fft.Inverse(re, im);

        var foldRe = new double[size];
        var foldIm = new double[size];
        foldRe[0] = re[0];
        foldIm[0] = im[0];
        for (int i = 1; i < size / 2; i++)
        {
            foldRe[i] = 2 * re[i];
            foldIm[i] = 2 * im[i];
        }
        foldRe[size / 2] = re[size / 2];
        foldIm[size / 2] = im[size / 2];

        Fft.Forward(foldRe, foldIm);

        // The exponential of the complex log is the minimum-phase spectrum.
        for (int i = 0; i < size; i++)
        {
            double scale = Math.Exp(foldRe[i]);
            double angle = foldIm[i];
            foldRe[i] = scale * Math.Cos(angle);
            foldIm[i] = scale * Math.Sin(angle);
        }

        Fft.Inverse(foldRe, foldIm);
        return foldRe;
    }

    /// <summary>
    /// A zero-phase impulse response with the given magnitude, centred on index zero.
    /// </summary>
    /// <remarks>
    /// Centred on index <b>zero</b>, not in the middle of the buffer: the inverse
    /// transform of a real symmetric magnitude is circularly symmetric, so the response
    /// runs forward from index 0 and its negative half wraps to the end. A caller that
    /// wants it centred has to rotate it — <c>LinearPhaseEqEffect</c> does, with
    /// <c>(i - half + size) % size</c>. The doc used to claim otherwise, which is the
    /// kind of thing that only shows up as a filter that sounds wrong.
    /// </remarks>
    public static double[] ZeroPhase(double[] magnitude)
    {
        ArgumentNullException.ThrowIfNull(magnitude);
        var re = (double[])magnitude.Clone();
        var im = new double[magnitude.Length];
        Fft.Inverse(re, im);
        return re;
    }
}
