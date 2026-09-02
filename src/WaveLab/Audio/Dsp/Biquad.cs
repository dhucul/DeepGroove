namespace WaveLab.Audio.Dsp;

/// <summary>RBJ cookbook biquad, transposed direct form II.</summary>
public struct Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    public float Process(float x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return (float)y;
    }

    public void Reset() { _z1 = 0; _z2 = 0; }

    /// <summary>
    /// Copy another biquad's coefficients while preserving the delay-line state,
    /// so parameter changes don't click or zipper.
    /// </summary>
    public void CopyCoefficientsFrom(in Biquad source)
    {
        _b0 = source._b0; _b1 = source._b1; _b2 = source._b2;
        _a1 = source._a1; _a2 = source._a2;
    }


    /// <summary>Magnitude response at frequency f (Hz), for drawing curves.</summary>
    public readonly double MagnitudeDb(double f, double fs)
    {
        double w = 2 * Math.PI * f / fs;
        var (cr, ci) = (Math.Cos(w), -Math.Sin(w));
        var (c2r, c2i) = (Math.Cos(2 * w), -Math.Sin(2 * w));
        double nr = _b0 + _b1 * cr + _b2 * c2r, ni = _b1 * ci + _b2 * c2i;
        double dr = 1 + _a1 * cr + _a2 * c2r, di = _a1 * ci + _a2 * c2i;
        double num = Math.Sqrt(nr * nr + ni * ni), den = Math.Sqrt(dr * dr + di * di);
        return 20 * Math.Log10(Math.Max(1e-12, num / Math.Max(1e-12, den)));
    }

    /// <summary>
    /// The corner frequency a factory may actually use, clamped inside the band the
    /// bilinear transform can express.
    /// </summary>
    /// <remarks>
    /// At exactly fs/2 the RBJ low- and high-pass forms put a double pole on the unit
    /// circle; past it sin(w) changes sign and the pole pair can leave the circle
    /// altogether. MonoToStereoEffect's fixed 4500 Hz all-pass did precisely that at an
    /// 8 kHz project rate — pole radius 1.32, so the filter diverged. Four of the five
    /// effects with a frequency parameter clamped for themselves and two did not, which
    /// is the wrong place to be making the decision.
    /// </remarks>
    private static double Corner(double fs, double f) =>
        double.IsFinite(f) && double.IsFinite(fs) && fs > 0
            ? Math.Clamp(f, 1.0, fs * 0.49)
            : 1.0;

    /// <summary>Q clamped away from zero, which would make alpha infinite.</summary>
    private static double Resonance(double q) =>
        double.IsFinite(q) ? Math.Max(q, 1e-3) : 0.70710678118654752;

    private static Biquad FromCoefficients(double b0, double b1, double b2, double a0, double a1, double a2) => new()
    {
        _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0,
    };

    /// <summary>
    /// Builds a filter from an already-normalized transfer function. Kept internal so effects with
    /// standard-defined pole/zero curves can use the shared state-safe processor without exposing
    /// arbitrary (and potentially unstable) coefficients as public API.
    /// </summary>
    internal static Biquad FromNormalized(
        double b0, double b1, double b2, double a1, double a2) =>
        FromCoefficients(b0, b1, b2, 1, a1, a2);

    public static Biquad Identity() => FromCoefficients(1, 0, 0, 1, 0, 0);

    public static Biquad LowShelf(double fs, double f, double gainDb, double slope = 1.0)
    {
        f = Corner(fs, f);
        double a = Math.Pow(10, gainDb / 40);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), sw = Math.Sin(w);
        double alpha = sw / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
        double sq = 2 * Math.Sqrt(a) * alpha;
        return FromCoefficients(
            a * ((a + 1) - (a - 1) * cw + sq),
            2 * a * ((a - 1) - (a + 1) * cw),
            a * ((a + 1) - (a - 1) * cw - sq),
            (a + 1) + (a - 1) * cw + sq,
            -2 * ((a - 1) + (a + 1) * cw),
            (a + 1) + (a - 1) * cw - sq);
    }

    public static Biquad HighShelf(double fs, double f, double gainDb, double slope = 1.0)
    {
        f = Corner(fs, f);
        double a = Math.Pow(10, gainDb / 40);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), sw = Math.Sin(w);
        double alpha = sw / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
        double sq = 2 * Math.Sqrt(a) * alpha;
        return FromCoefficients(
            a * ((a + 1) + (a - 1) * cw + sq),
            -2 * a * ((a - 1) + (a + 1) * cw),
            a * ((a + 1) + (a - 1) * cw - sq),
            (a + 1) - (a - 1) * cw + sq,
            2 * ((a - 1) - (a + 1) * cw),
            (a + 1) - (a - 1) * cw - sq);
    }

    public static Biquad Peaking(double fs, double f, double q, double gainDb)
    {
        f = Corner(fs, f);
        double a = Math.Pow(10, gainDb / 40);
        double w = 2 * Math.PI * f / fs;
        double alpha = Math.Sin(w) / (2 * Resonance(q));
        return FromCoefficients(
            1 + alpha * a, -2 * Math.Cos(w), 1 - alpha * a,
            1 + alpha / a, -2 * Math.Cos(w), 1 - alpha / a);
    }

    public static Biquad LowPass(double fs, double f, double q)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * Resonance(q));
        return FromCoefficients(
            (1 - cw) / 2, 1 - cw, (1 - cw) / 2,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad Notch(double fs, double f, double q)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * Resonance(q));
        return FromCoefficients(
            1, -2 * cw, 1,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad HighPass(double fs, double f, double q)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * Resonance(q));
        return FromCoefficients(
            (1 + cw) / 2, -(1 + cw), (1 + cw) / 2,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad BandPass(double fs, double f, double q)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * Resonance(q));
        return FromCoefficients(
            alpha, 0, -alpha,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad BandStop(double fs, double f, double q)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * Resonance(q));
        return FromCoefficients(
            1, -2 * cw, 1,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad AllPass(double fs, double f, double q)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * Resonance(q));
        return FromCoefficients(
            1 - alpha, -2 * cw, 1 + alpha,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    /// <summary>
    /// A genuine one-pole, one-zero high-pass: -6 dB/octave.
    /// </summary>
    /// <remarks>
    /// It was not one. The old form used the second-order high-pass numerator with an
    /// alpha of sin(w), which is Q = 0.5 — critically damped, and -12 dB/octave
    /// asymptotically. Its one caller is the compressor's sidechain filter, where the
    /// extra six decibels an octave measurably changes how much low end reaches the
    /// detector.
    /// </remarks>
    public static Biquad FirstOrderHighPass(double fs, double f)
    {
        double k = Math.Tan(Math.PI * Corner(fs, f) / fs);
        return FromCoefficients(1, -1, 0, 1 + k, k - 1, 0);
    }

    public static Biquad LowPass12Db(double fs, double f)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), sw = Math.Sin(w);
        double alpha = sw / Math.Sqrt(2);
        return FromCoefficients(
            (1 - cw) / 2, 1 - cw, (1 - cw) / 2,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad HighPass12Db(double fs, double f)
    {
        f = Corner(fs, f);
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), sw = Math.Sin(w);
        double alpha = sw / Math.Sqrt(2);
        return FromCoefficients(
            (1 + cw) / 2, -(1 + cw), (1 + cw) / 2,
            1 + alpha, -2 * cw, 1 - alpha);
    }
}
