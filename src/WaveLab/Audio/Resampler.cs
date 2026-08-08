namespace WaveLab.Audio;

/// <summary>High-quality sample-rate conversion: windowed-sinc interpolation (Blackman-Harris, 64 taps).</summary>
public static class Resampler
{
    private const int HalfTaps = 32;

    public static float[][] Resample(IReadOnlyList<float[]> channels, int srcRate, int dstRate)
    {
        if (srcRate == dstRate)
        {
            var copy = new float[channels.Count][];
            for (int c = 0; c < channels.Count; c++) copy[c] = (float[])channels[c].Clone();
            return copy;
        }

        double ratio = (double)dstRate / srcRate;
        // when downsampling, lower the filter cutoff to the new Nyquist
        double cutoff = Math.Min(1.0, ratio) * 0.945;
        int srcLen = channels[0].Length;
        int dstLen = (int)Math.Round((long)srcLen * dstRate / (double)srcRate);
        var result = new float[channels.Count][];

        for (int c = 0; c < channels.Count; c++)
        {
            var src = channels[c];
            var dst = new float[dstLen];
            for (int i = 0; i < dstLen; i++)
            {
                double srcPos = i / ratio;
                int center = (int)Math.Floor(srcPos);
                double frac = srcPos - center;
                double sum = 0, wsum = 0;
                for (int t = -HalfTaps + 1; t <= HalfTaps; t++)
                {
                    double x = (t - frac) * cutoff;
                    double sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    double wpos = (t - frac) / HalfTaps; // -1..1
                    if (wpos < -1 || wpos > 1) continue;
                    double wt = 0.35875 + 0.48829 * Math.Cos(Math.PI * wpos)
                              + 0.14128 * Math.Cos(2 * Math.PI * wpos) + 0.01168 * Math.Cos(3 * Math.PI * wpos);
                    double w = sinc * wt;
                    wsum += w;
                    int s = center + t;
                    if ((uint)s < (uint)srcLen) sum += src[s] * w;
                }
                dst[i] = wsum != 0 ? (float)(sum / wsum) : 0f;
            }
            result[c] = dst;
        }
        return result;
    }
}
