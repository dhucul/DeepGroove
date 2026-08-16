using System.Collections.Concurrent;
using System.Numerics;

namespace WaveLab.Audio.Dsp;

/// <summary>
/// Fast Fourier transform: Stockham autosort radix-4/2 over a cached double-precision twiddle table,
/// with real-input transforms and arbitrary (non-power-of-two) sizes via Bluestein.
/// </summary>
/// <remarks>
/// <para>
/// Convention: the forward transform is unnormalized and the inverse carries the full 1/N, which is
/// what the hand-rolled conjugate-trick inversions in the restoration and noise-reduction code
/// already assumed. Round-tripping forward then inverse therefore returns the original signal.
/// </para>
/// <para>
/// The arithmetic is done in double even though every caller hands in float. That is deliberate: a
/// float FFT loses roughly half its significant bits by the last stage of a large transform, and
/// spectral subtraction, inpainting and linear-phase filtering all difference nearly-equal
/// magnitudes, which is exactly where that loss shows up. The conversion is a pass over the buffer;
/// the transform is O(N log N) on top of it.
/// </para>
/// </remarks>
public static class Fft
{
    private static readonly ConcurrentDictionary<int, FftPlan> PlanCache = new();

    // Scratch is per-thread rather than per-plan so that plans stay immutable and shareable: the
    // spectrum analyzer renders on the UI thread while offline restoration runs on the pool, and
    // both will ask for the same size.
    [ThreadStatic] private static double[]? _workRe;
    [ThreadStatic] private static double[]? _workIm;
    [ThreadStatic] private static double[]? _swapRe;
    [ThreadStatic] private static double[]? _swapIm;

    internal static FftPlan GetPlan(int size) => PlanCache.GetOrAdd(size, static n => new FftPlan(n));

    /// <summary>Smallest power of two greater than or equal to <paramref name="value"/>.</summary>
    public static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        return (int)BitOperations.RoundUpToPowerOf2((uint)value);
    }

    // ── complex transforms ───────────────────────────────────────

    /// <summary>Forward transform in place. Length may be any positive size.</summary>
    public static void Forward(float[] re, float[] im) => TransformFloat(re, im, inverse: false);

    /// <summary>Inverse transform in place, normalized by 1/N. Length may be any positive size.</summary>
    public static void Inverse(float[] re, float[] im) => TransformFloat(re, im, inverse: true);

    /// <summary>Forward transform in place over double-precision data.</summary>
    public static void Forward(double[] re, double[] im) => Transform(re, im, re.Length, inverse: false);

    /// <summary>Inverse transform in place over double-precision data, normalized by 1/N.</summary>
    public static void Inverse(double[] re, double[] im) => Transform(re, im, re.Length, inverse: true);

    private static void TransformFloat(float[] re, float[] im, bool inverse)
    {
        ArgumentNullException.ThrowIfNull(re);
        ArgumentNullException.ThrowIfNull(im);
        int n = re.Length;
        if (im.Length != n)
            throw new ArgumentException("Real and imaginary buffers must be the same length.", nameof(im));
        if (n <= 1) return;

        double[] workRe = Rent(ref _workRe, n), workIm = Rent(ref _workIm, n);
        for (int i = 0; i < n; i++) { workRe[i] = re[i]; workIm[i] = im[i]; }

        Transform(workRe, workIm, n, inverse);

        for (int i = 0; i < n; i++) { re[i] = (float)workRe[i]; im[i] = (float)workIm[i]; }
    }

    /// <summary>
    /// Transforms the first <paramref name="n"/> entries in place, choosing the power-of-two kernel
    /// or Bluestein as the size requires.
    /// </summary>
    internal static void Transform(double[] re, double[] im, int n, bool inverse)
    {
        if (n <= 1) return;

        if (inverse)
        {
            // IDFT(x) = conj(DFT(conj(x))) / N.
            for (int i = 0; i < n; i++) im[i] = -im[i];
        }

        if (BitOperations.IsPow2((uint)n)) TransformPowerOfTwo(re, im, n);
        else Bluestein(re, im, n);

        if (inverse)
        {
            double scale = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= scale;
                im[i] = -im[i] * scale;
            }
        }
    }

    private static void TransformPowerOfTwo(double[] re, double[] im, int n)
    {
        FftPlan plan = GetPlan(n);
        double[] swapRe = Rent(ref _swapRe, n), swapIm = Rent(ref _swapIm, n);

        if (plan.Transform(re, im, swapRe, swapIm))
        {
            // An odd number of Stockham stages left the result in the scratch pair.
            Array.Copy(swapRe, re, n);
            Array.Copy(swapIm, im, n);
        }
    }

    // ── real-input transforms ────────────────────────────────────

    /// <summary>
    /// Forward transform of <paramref name="samples"/> (even length N), producing the N/2+1 unique
    /// bins. Half the work of a complex transform of the same length, and it removes the mirrored-bin
    /// bookkeeping each caller used to repeat by hand.
    /// </summary>
    public static void RealForward(ReadOnlySpan<float> samples, Span<float> binRe, Span<float> binIm)
    {
        int n = samples.Length;
        if (n < 2 || (n & 1) != 0)
            throw new ArgumentException("Real transform length must be even and at least 2.", nameof(samples));
        int half = n / 2, bins = half + 1;
        if (binRe.Length < bins || binIm.Length < bins)
            throw new ArgumentException($"Bin buffers must hold at least {bins} entries.", nameof(binRe));

        // Pack the even samples into the real part and the odd samples into the imaginary part, then
        // transform at half length and untangle the two interleaved spectra.
        double[] workRe = Rent(ref _workRe, half), workIm = Rent(ref _workIm, half);
        for (int j = 0; j < half; j++)
        {
            workRe[j] = samples[2 * j];
            workIm[j] = samples[2 * j + 1];
        }
        Transform(workRe, workIm, half, inverse: false);

        // DC and Nyquist are both real and fall out of Z[0] alone.
        binRe[0] = (float)(workRe[0] + workIm[0]); binIm[0] = 0;
        binRe[half] = (float)(workRe[0] - workIm[0]); binIm[half] = 0;

        FftPlan? plan = BitOperations.IsPow2((uint)n) ? GetPlan(n) : null;

        for (int k = 1; k < half; k++)
        {
            int mirror = half - k;
            double zr = workRe[k], zi = workIm[k];
            double mr = workRe[mirror], mi = workIm[mirror];

            // Even part: (Z[k] + conj(Z[m-k]))/2. Odd part: (Z[k] - conj(Z[m-k]))/2i.
            double evenR = 0.5 * (zr + mr), evenI = 0.5 * (zi - mi);
            double oddR = 0.5 * (zi + mi), oddI = -0.5 * (zr - mr);

            (double wr, double wi) = UnpackTwiddle(plan, k, n);
            binRe[k] = (float)(evenR + (oddR * wr - oddI * wi));
            binIm[k] = (float)(evenI + (oddR * wi + oddI * wr));
        }
    }

    /// <summary>
    /// Inverse of <see cref="RealForward"/>: N/2+1 bins back to N real samples, normalized so that a
    /// forward/inverse round trip is the identity.
    /// </summary>
    public static void RealInverse(ReadOnlySpan<float> binRe, ReadOnlySpan<float> binIm, Span<float> samples)
    {
        int n = samples.Length;
        if (n < 2 || (n & 1) != 0)
            throw new ArgumentException("Real transform length must be even and at least 2.", nameof(samples));
        int half = n / 2, bins = half + 1;
        if (binRe.Length < bins || binIm.Length < bins)
            throw new ArgumentException($"Bin buffers must hold at least {bins} entries.", nameof(binRe));

        double[] workRe = Rent(ref _workRe, half), workIm = Rent(ref _workIm, half);
        FftPlan? plan = BitOperations.IsPow2((uint)n) ? GetPlan(n) : null;

        double dc = binRe[0], nyquist = binRe[half];
        workRe[0] = 0.5 * (dc + nyquist);
        workIm[0] = 0.5 * (dc - nyquist);

        for (int k = 1; k < half; k++)
        {
            int mirror = half - k;
            double xr = binRe[k], xi = binIm[k];
            double mr = binRe[mirror], mi = binIm[mirror];

            double evenR = 0.5 * (xr + mr), evenI = 0.5 * (xi - mi);
            double diffR = 0.5 * (xr - mr), diffI = 0.5 * (xi + mi);

            // Odd part needs the conjugate twiddle: W^-k.
            (double tabulatedRe, double tabulatedIm) = UnpackTwiddle(plan, k, n);
            double wr = tabulatedRe, wi = -tabulatedIm;
            double oddR = diffR * wr - diffI * wi;
            double oddI = diffR * wi + diffI * wr;

            // Z[k] = Xe[k] + i·Xo[k].
            workRe[k] = evenR - oddI;
            workIm[k] = evenI + oddR;
        }

        Transform(workRe, workIm, half, inverse: true);

        for (int j = 0; j < half; j++)
        {
            samples[2 * j] = (float)workRe[j];
            samples[2 * j + 1] = (float)workIm[j];
        }
    }

    /// <summary>
    /// exp(-2πik/n) for the real-transform untangling step: straight off the table when the length is
    /// a power of two, evaluated directly otherwise. A table built for a *different* size cannot be
    /// indexed by a scaled stride here, because the scale is only an integer when n divides it.
    /// </summary>
    private static (double Re, double Im) UnpackTwiddle(FftPlan? plan, int k, int n)
    {
        if (plan is not null) return (plan.TwiddleRe[k], plan.TwiddleIm[k]);
        (double sin, double cos) = Math.SinCos(-2.0 * Math.PI * k / n);
        return (cos, sin);
    }

    // ── Bluestein (arbitrary size) ───────────────────────────────

    /// <summary>
    /// Chirp-Z transform for sizes that are not powers of two, so analysis can use the length the
    /// signal actually has instead of zero-padding to the next power of two and smearing the result.
    /// </summary>
    private static void Bluestein(double[] re, double[] im, int n)
    {
        int m = NextPowerOfTwo(2 * n - 1);

        var chirpRe = new double[n];
        var chirpIm = new double[n];
        for (int j = 0; j < n; j++)
        {
            // j² mod 2n keeps the angle small and exact for large j, where j² alone would lose bits.
            long squared = (long)j * j % (2L * n);
            (double sin, double cos) = Math.SinCos(-Math.PI * squared / n);
            chirpRe[j] = cos;
            chirpIm[j] = sin;
        }

        var aRe = new double[m];
        var aIm = new double[m];
        for (int j = 0; j < n; j++)
        {
            aRe[j] = re[j] * chirpRe[j] - im[j] * chirpIm[j];
            aIm[j] = re[j] * chirpIm[j] + im[j] * chirpRe[j];
        }

        var bRe = new double[m];
        var bIm = new double[m];
        bRe[0] = chirpRe[0];
        bIm[0] = -chirpIm[0];
        for (int j = 1; j < n; j++)
        {
            bRe[j] = bRe[m - j] = chirpRe[j];
            bIm[j] = bIm[m - j] = -chirpIm[j];
        }

        TransformPowerOfTwo(aRe, aIm, m);
        TransformPowerOfTwo(bRe, bIm, m);

        for (int j = 0; j < m; j++)
        {
            double pr = aRe[j] * bRe[j] - aIm[j] * bIm[j];
            double pi = aRe[j] * bIm[j] + aIm[j] * bRe[j];
            aRe[j] = pr;
            aIm[j] = pi;
        }

        // Inverse of the cyclic convolution, then undo the chirp.
        for (int j = 0; j < m; j++) aIm[j] = -aIm[j];
        TransformPowerOfTwo(aRe, aIm, m);
        double scale = 1.0 / m;

        for (int k = 0; k < n; k++)
        {
            double cr = aRe[k] * scale;
            double ci = -aIm[k] * scale;
            re[k] = cr * chirpRe[k] - ci * chirpIm[k];
            im[k] = cr * chirpIm[k] + ci * chirpRe[k];
        }
    }

    // ── windows and magnitude ────────────────────────────────────

    /// <summary>
    /// Symmetric Hann window. Retained with its original definition because the restoration and
    /// analysis code is tuned around it; new overlap-add code should use the periodic form from
    /// <see cref="WindowFunctions.Hann"/>, which is the one that satisfies COLA.
    /// </summary>
    public static float[] HannWindow(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
            w[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    [ThreadStatic] private static float[]? _magnitudeWindowSource;
    [ThreadStatic] private static int _magnitudeWindowCount;
    [ThreadStatic] private static double _magnitudeWindowSum;
    [ThreadStatic] private static float[]? _magnitudeBinRe;
    [ThreadStatic] private static float[]? _magnitudeBinIm;
    [ThreadStatic] private static float[]? _magnitudeWindowed;

    /// <summary>Windowed magnitude spectrum in dBFS. Input length = FFT size; output = size/2 bins.</summary>
    public static void MagnitudeDb(float[] samples, float[] window, float[] outDb)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(outDb);

        int n = samples.Length;
        int bins = n / 2 + 1;
        float[] binRe = RentFloat(ref _magnitudeBinRe, bins);
        float[] binIm = RentFloat(ref _magnitudeBinIm, bins);
        float[] windowed = RentFloat(ref _magnitudeWindowed, n);

        double windowSum;
        if (ReferenceEquals(_magnitudeWindowSource, window) && _magnitudeWindowCount == n)
        {
            windowSum = _magnitudeWindowSum;
            for (int i = 0; i < n; i++) windowed[i] = samples[i] * window[i];
        }
        else
        {
            windowSum = 0;
            for (int i = 0; i < n; i++) { windowed[i] = samples[i] * window[i]; windowSum += window[i]; }
            _magnitudeWindowSource = window;
            _magnitudeWindowCount = n;
            _magnitudeWindowSum = windowSum;
        }

        RealForward(windowed.AsSpan(0, n), binRe, binIm);

        double norm = 2.0 / Math.Max(1e-9, windowSum);
        for (int i = 0; i < outDb.Length && i < n / 2; i++)
        {
            double mag = Math.Sqrt(binRe[i] * binRe[i] + binIm[i] * binIm[i]) * norm;
            outDb[i] = (float)(20 * Math.Log10(Math.Max(1e-9, mag)));
        }
    }

    // ── scratch ──────────────────────────────────────────────────

    private static double[] Rent(ref double[]? slot, int size)
    {
        double[]? buffer = slot;
        if (buffer is null || buffer.Length < size) slot = buffer = new double[size];
        return buffer;
    }

    private static float[] RentFloat(ref float[]? slot, int size)
    {
        float[]? buffer = slot;
        if (buffer is null || buffer.Length < size) slot = buffer = new float[size];
        return buffer;
    }
}
