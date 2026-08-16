namespace WaveLab.Audio.Dsp;

/// <summary>Window shape for spectral analysis and overlap-add synthesis.</summary>
public enum WindowKind
{
    Rectangular,
    Hann,
    Hamming,
    Blackman,
    BlackmanHarris,
    Nuttall,
    FlatTop,
    Kaiser,
    Gaussian,
    Tukey,
    DolphChebyshev,
}

/// <summary>
/// Window functions, in both the periodic form analysis and overlap-add want and the symmetric form
/// filter design wants.
/// </summary>
/// <remarks>
/// <para>
/// The distinction matters more than it looks. A periodic (DFT-even) window of length N is the first
/// N samples of a length-N+1 symmetric window; it is the one whose shifted copies sum to a constant,
/// so it is the only correct choice for overlap-add. The symmetric form is right for designing an
/// FIR kernel, where you want the taps themselves to be symmetric. The app's original
/// <see cref="Fft.HannWindow"/> is the symmetric form, which is why its overlap-add callers each had
/// to carry a running normalization to undo the error.
/// </para>
/// <para>
/// All builders return the window in float because that is what every consumer stores, but the
/// coefficients are evaluated in double so the shape does not inherit a rounding bias.
/// </para>
/// </remarks>
public static class WindowFunctions
{
    /// <summary>Builds a window of the requested kind.</summary>
    /// <param name="kind">Window shape.</param>
    /// <param name="length">Number of taps.</param>
    /// <param name="periodic">True for the overlap-add form, false for the symmetric filter-design form.</param>
    /// <param name="parameter">
    /// Kaiser β, Gaussian σ, Tukey α, or Dolph-Chebyshev sidelobe attenuation in dB. Ignored otherwise.
    /// </param>
    public static float[] Create(WindowKind kind, int length, bool periodic = true, double parameter = double.NaN) => kind switch
    {
        WindowKind.Rectangular => Filled(length, 1f),
        WindowKind.Hann => Hann(length, periodic),
        WindowKind.Hamming => Hamming(length, periodic),
        WindowKind.Blackman => Blackman(length, periodic),
        WindowKind.BlackmanHarris => BlackmanHarris(length, periodic),
        WindowKind.Nuttall => Nuttall(length, periodic),
        WindowKind.FlatTop => FlatTop(length, periodic),
        WindowKind.Kaiser => Kaiser(length, double.IsNaN(parameter) ? 8.6 : parameter, periodic),
        WindowKind.Gaussian => Gaussian(length, double.IsNaN(parameter) ? 0.4 : parameter, periodic),
        WindowKind.Tukey => Tukey(length, double.IsNaN(parameter) ? 0.5 : parameter, periodic),
        WindowKind.DolphChebyshev => DolphChebyshev(length, double.IsNaN(parameter) ? 100 : parameter),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Hann. Periodic by default — this is the COLA-correct form.</summary>
    public static float[] Hann(int length, bool periodic = true) =>
        CosineSum(length, periodic, 0.5, 0.5);

    /// <summary>Hamming, classic 0.54/0.46 coefficients.</summary>
    public static float[] Hamming(int length, bool periodic = true) =>
        CosineSum(length, periodic, 0.54, 0.46);

    /// <summary>Blackman, classic (unexact) coefficients.</summary>
    public static float[] Blackman(int length, bool periodic = true) =>
        CosineSum(length, periodic, 0.42, 0.5, 0.08);

    /// <summary>4-term Blackman-Harris; about -92 dB peak sidelobe.</summary>
    public static float[] BlackmanHarris(int length, bool periodic = true) =>
        CosineSum(length, periodic, 0.35875, 0.48829, 0.14128, 0.01168);

    /// <summary>4-term Nuttall, continuous first derivative; about -93 dB peak sidelobe.</summary>
    public static float[] Nuttall(int length, bool periodic = true) =>
        CosineSum(length, periodic, 0.355768, 0.487396, 0.144232, 0.012604);

    /// <summary>Flat-top: poor resolution, near-exact amplitude — the window for level calibration.</summary>
    public static float[] FlatTop(int length, bool periodic = true) =>
        CosineSum(length, periodic, 0.21557895, 0.41663158, 0.277263158, 0.083578947, 0.006947368);

    /// <summary>
    /// Kaiser window. β trades mainlobe width against sidelobe level: β≈4 puts the peak sidelobe near
    /// -30 dB, β≈8.6 near -65 dB and β≈12 near -90 dB. Note these are the window's own sidelobes; the
    /// familiar β = 0.1102(A - 8.7) rule quotes the stopband attenuation of the *filter* that results
    /// from windowing, which is a good deal deeper.
    /// </summary>
    public static float[] Kaiser(int length, double beta, bool periodic = true)
    {
        if (length <= 0) return [];
        if (length == 1) return [1f];

        var window = new float[length];
        double denominator = length - (periodic ? 0 : 1);
        double normalization = BesselI0(beta);
        double half = denominator / 2.0;

        for (int i = 0; i < length; i++)
        {
            double ratio = (i - half) / half;
            double inner = 1.0 - ratio * ratio;
            window[i] = inner <= 0 ? 0f : (float)(BesselI0(beta * Math.Sqrt(inner)) / normalization);
        }
        return window;
    }

    /// <summary>Gaussian window; σ is a fraction of half the window length.</summary>
    public static float[] Gaussian(int length, double sigma, bool periodic = true)
    {
        if (length <= 0) return [];
        if (length == 1) return [1f];

        var window = new float[length];
        double denominator = length - (periodic ? 0 : 1);
        double half = denominator / 2.0;
        double spread = Math.Max(1e-9, sigma) * half;

        for (int i = 0; i < length; i++)
        {
            double offset = (i - half) / spread;
            window[i] = (float)Math.Exp(-0.5 * offset * offset);
        }
        return window;
    }

    /// <summary>Tukey (cosine-tapered) window; α = 0 is rectangular, α = 1 is Hann.</summary>
    public static float[] Tukey(int length, double alpha, bool periodic = true)
    {
        if (length <= 0) return [];
        if (length == 1) return [1f];

        alpha = Math.Clamp(alpha, 0, 1);
        if (alpha <= 0) return Filled(length, 1f);
        if (alpha >= 1) return Hann(length, periodic);

        var window = new float[length];
        double denominator = length - (periodic ? 0 : 1);
        double taper = alpha * denominator / 2.0;

        for (int i = 0; i < length; i++)
        {
            double value;
            if (i < taper) value = 0.5 * (1 - Math.Cos(Math.PI * i / taper));
            else if (i > denominator - taper) value = 0.5 * (1 - Math.Cos(Math.PI * (denominator - i) / taper));
            else value = 1.0;
            window[i] = (float)value;
        }
        return window;
    }

    /// <summary>
    /// Dolph-Chebyshev window: the shape with the narrowest mainlobe for a given sidelobe level, with
    /// every sidelobe at exactly that level. Built from its own transform, so it is always symmetric.
    /// </summary>
    public static float[] DolphChebyshev(int length, double sidelobeAttenuationDb)
    {
        if (length <= 0) return [];
        if (length == 1) return [1f];

        int n = length;
        int order = n - 1;
        double ripple = Math.Pow(10, Math.Abs(sidelobeAttenuationDb) / 20.0);
        double beta = Math.Cosh(Math.Acosh(ripple) / order);

        var re = new double[n];
        var im = new double[n];
        for (int k = 0; k < n; k++)
        {
            double amplitude = Chebyshev(order, beta * Math.Cos(Math.PI * k / n));

            // Centring the window is a shift of (n-1)/2 samples, applied here as a phase ramp. For
            // odd n that happens to be a whole number of samples and reduces to an alternating sign;
            // for even n it is half a sample, and using the alternating sign anyway makes the
            // spectrum antisymmetric and the resulting "window" a flat line.
            (double sin, double cos) = Math.SinCos(-Math.PI * k * order / n);
            re[k] = amplitude * cos;
            im[k] = amplitude * sin;
        }

        Fft.Inverse(re, im);

        double peak = 0;
        for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(re[i]));
        if (!(peak > 0)) return Filled(n, 1f);

        var window = new float[n];
        for (int i = 0; i < n; i++) window[i] = (float)(re[i] / peak);
        return window;
    }

    /// <summary>
    /// Element-wise square root, for the √Hann analysis/synthesis pair that weighted overlap-add
    /// wants: applying half the window on the way in and half on the way out keeps the round trip
    /// exact while still tapering the frame that gets modified in between.
    /// </summary>
    public static float[] Sqrt(float[] window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var result = new float[window.Length];
        for (int i = 0; i < window.Length; i++)
            result[i] = window[i] <= 0 ? 0f : (float)Math.Sqrt(window[i]);
        return result;
    }

    // ── overlap-add verification ─────────────────────────────────

    /// <summary>
    /// Sum of the window's shifted copies at each position within one hop. For a COLA-satisfying
    /// pair every entry is the same, and that value is what synthesis must divide out.
    /// </summary>
    public static double[] OverlapSum(float[] analysis, float[]? synthesis, int hop)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (hop <= 0) throw new ArgumentOutOfRangeException(nameof(hop));
        if (synthesis is not null && synthesis.Length != analysis.Length)
            throw new ArgumentException("Analysis and synthesis windows must be the same length.", nameof(synthesis));

        var sums = new double[hop];
        for (int i = 0; i < analysis.Length; i++)
        {
            double contribution = synthesis is null ? analysis[i] : (double)analysis[i] * synthesis[i];
            sums[i % hop] += contribution;
        }
        return sums;
    }

    /// <summary>
    /// True when the (window, hop) pair sums to a constant to within <paramref name="tolerance"/>,
    /// relative to the mean. <paramref name="constant"/> receives that mean.
    /// </summary>
    public static bool SatisfiesCola(float[] analysis, float[]? synthesis, int hop,
        out double constant, double tolerance = 1e-6)
    {
        double[] sums = OverlapSum(analysis, synthesis, hop);
        double minimum = double.MaxValue, maximum = double.MinValue, total = 0;
        foreach (double sum in sums)
        {
            minimum = Math.Min(minimum, sum);
            maximum = Math.Max(maximum, sum);
            total += sum;
        }

        constant = total / sums.Length;
        if (Math.Abs(constant) < 1e-12) return false;
        return (maximum - minimum) / Math.Abs(constant) <= tolerance;
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Generalized cosine-sum window: w[i] = Σ (-1)^k a[k] cos(2πki/D), with D = N for the periodic
    /// form and N-1 for the symmetric one.
    /// </summary>
    private static float[] CosineSum(int length, bool periodic, params double[] coefficients)
    {
        if (length <= 0) return [];
        if (length == 1) return [1f];

        var window = new float[length];
        double denominator = length - (periodic ? 0 : 1);

        for (int i = 0; i < length; i++)
        {
            double value = 0;
            double angle = 2.0 * Math.PI * i / denominator;
            for (int k = 0; k < coefficients.Length; k++)
            {
                double term = coefficients[k] * Math.Cos(k * angle);
                value += (k & 1) == 0 ? term : -term;
            }
            window[i] = (float)value;
        }
        return window;
    }

    private static float[] Filled(int length, float value)
    {
        if (length <= 0) return [];
        var window = new float[length];
        Array.Fill(window, value);
        return window;
    }

    /// <summary>
    /// Modified Bessel function of the first kind, order zero, by its defining series. Summed until
    /// the terms stop contributing rather than truncated at a fixed order, so accuracy does not decay
    /// as β rises.
    /// </summary>
    private static double BesselI0(double x)
    {
        double halfX = x / 2.0;
        double term = 1.0, sum = 1.0;
        for (int k = 1; k < 200; k++)
        {
            term *= halfX / k;
            double squared = term * term;
            sum += squared;
            if (squared < sum * 1e-17) break;
        }
        return sum;
    }

    /// <summary>Chebyshev polynomial of the first kind, valid inside and outside [-1, 1].</summary>
    private static double Chebyshev(int order, double x)
    {
        if (x >= 1) return Math.Cosh(order * Math.Acosh(x));
        if (x <= -1)
        {
            double magnitude = Math.Cosh(order * Math.Acosh(-x));
            return (order & 1) == 0 ? magnitude : -magnitude;
        }
        return Math.Cos(order * Math.Acos(x));
    }
}
