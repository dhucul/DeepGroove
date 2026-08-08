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

    private static Biquad FromCoefficients(double b0, double b1, double b2, double a0, double a1, double a2) => new()
    {
        _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0,
    };

    public static Biquad Identity() => FromCoefficients(1, 0, 0, 1, 0, 0);

    public static Biquad LowShelf(double fs, double f, double gainDb, double slope = 1.0)
    {
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
        double a = Math.Pow(10, gainDb / 40);
        double w = 2 * Math.PI * f / fs;
        double alpha = Math.Sin(w) / (2 * q);
        return FromCoefficients(
            1 + alpha * a, -2 * Math.Cos(w), 1 - alpha * a,
            1 + alpha / a, -2 * Math.Cos(w), 1 - alpha / a);
    }

    public static Biquad LowPass(double fs, double f, double q)
    {
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return FromCoefficients(
            (1 - cw) / 2, 1 - cw, (1 - cw) / 2,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad Notch(double fs, double f, double q)
    {
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return FromCoefficients(
            1, -2 * cw, 1,
            1 + alpha, -2 * cw, 1 - alpha);
    }

    public static Biquad HighPass(double fs, double f, double q)
    {
        double w = 2 * Math.PI * f / fs;
        double cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return FromCoefficients(
            (1 + cw) / 2, -(1 + cw), (1 + cw) / 2,
            1 + alpha, -2 * cw, 1 - alpha);
    }
}
