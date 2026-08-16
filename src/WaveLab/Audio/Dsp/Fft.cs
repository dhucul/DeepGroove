namespace WaveLab.Audio.Dsp;

/// <summary>In-place iterative radix-2 FFT with a Hann window helper.</summary>
public static class Fft
{
    public static void Forward(float[] re, float[] im)
    {
        int n = re.Length;
        if ((n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two.");

        // bit reversal
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float ur = re[a], ui = im[a];
                    float vr = re[b] * cr - im[b] * ci;
                    float vi = re[b] * ci + im[b] * cr;
                    re[a] = ur + vr; im[a] = ui + vi;
                    re[b] = ur - vr; im[b] = ui - vi;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    public static float[] HannWindow(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
            w[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    // Per-thread scratch for MagnitudeDb: Forward() derives the transform size from
    // re.Length, so the buffers must match the request exactly rather than merely be
    // large enough. Callers keep a constant size, so this reallocates ~never.
    [ThreadStatic] private static float[]? _scratchRe;
    [ThreadStatic] private static float[]? _scratchIm;
    [ThreadStatic] private static float[]? _windowSumSource;
    [ThreadStatic] private static int _windowSumCount;
    [ThreadStatic] private static double _windowSumValue;

    /// <summary>Windowed magnitude spectrum in dBFS. Input length = FFT size; output = size/2 bins.</summary>
    public static void MagnitudeDb(float[] samples, float[] window, float[] outDb)
    {
        int n = samples.Length;
        float[] re = _scratchRe is { } cachedRe && cachedRe.Length == n ? cachedRe : (_scratchRe = new float[n]);
        float[] im = _scratchIm is { } cachedIm && cachedIm.Length == n ? cachedIm : (_scratchIm = new float[n]);
        Array.Clear(im);

        double windowSum;
        if (ReferenceEquals(_windowSumSource, window) && _windowSumCount == n)
        {
            windowSum = _windowSumValue;
            for (int i = 0; i < n; i++) re[i] = samples[i] * window[i];
        }
        else
        {
            windowSum = 0;
            for (int i = 0; i < n; i++) { re[i] = samples[i] * window[i]; windowSum += window[i]; }
            _windowSumSource = window;
            _windowSumCount = n;
            _windowSumValue = windowSum;
        }

        Forward(re, im);
        double norm = 2.0 / Math.Max(1e-9, windowSum);
        for (int i = 0; i < outDb.Length && i < n / 2; i++)
        {
            double mag = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) * norm;
            outDb[i] = (float)(20 * Math.Log10(Math.Max(1e-9, mag)));
        }
    }
}
