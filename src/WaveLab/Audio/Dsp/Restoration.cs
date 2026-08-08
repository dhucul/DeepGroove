namespace WaveLab.Audio.Dsp;

/// <summary>Restoration DSP: spectral noise reduction, click repair, hum removal, silence detection.</summary>
public static class Restoration
{
    public const int NrFftSize = 2048;
    private const int NrHop = NrFftSize / 4;

    /// <summary>Average magnitude spectrum of a region — the "noise print".</summary>
    public static float[] LearnNoiseProfile(IReadOnlyList<float[]> channels, int start, int count)
    {
        var window = Fft.HannWindow(NrFftSize);
        var profile = new double[NrFftSize / 2];
        var re = new float[NrFftSize];
        var im = new float[NrFftSize];
        int frames = 0;

        for (int pos = start; pos + NrFftSize <= start + count; pos += NrHop)
        {
            Array.Clear(im);
            for (int i = 0; i < NrFftSize; i++)
            {
                float mono = 0;
                foreach (var ch in channels) mono += ch[pos + i];
                re[i] = mono / channels.Count * window[i];
            }
            Fft.Forward(re, im);
            for (int b = 0; b < profile.Length; b++)
                profile[b] += Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
            frames++;
        }
        var result = new float[profile.Length];
        for (int b = 0; b < profile.Length; b++)
            result[b] = frames > 0 ? (float)(profile[b] / frames) : 0f;
        return result;
    }

    /// <summary>
    /// Spectral-gate noise reduction with a learned profile. reductionDb = max attenuation,
    /// sensitivityDb raises the gate threshold above the profile. STFT overlap-add, per-bin smoothing.
    /// </summary>
    public static void ReduceNoise(float[][] data, float[] profile, double reductionDb, double sensitivityDb)
    {
        var window = Fft.HannWindow(NrFftSize);
        float floorGain = (float)Math.Pow(10, -Math.Abs(reductionDb) / 20.0);
        double thresholdMul = Math.Pow(10, sensitivityDb / 20.0);
        int bins = NrFftSize / 2;

        foreach (var channel in data)
        {
            int n = channel.Length;
            var output = new float[n];
            var norm = new float[n];
            var re = new float[NrFftSize];
            var im = new float[NrFftSize];
            var smooth = new float[bins];
            for (int b = 0; b < bins; b++) smooth[b] = 1f;

            for (int pos = 0; pos < n; pos += NrHop)
            {
                Array.Clear(im);
                for (int i = 0; i < NrFftSize; i++)
                    re[i] = (pos + i < n ? channel[pos + i] : 0f) * window[i];
                Fft.Forward(re, im);

                // per-bin gate with time smoothing (fast attack, slow release against musical noise)
                for (int b = 0; b < bins; b++)
                {
                    double mag = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
                    double gate = profile[Math.Min(b, profile.Length - 1)] * thresholdMul;
                    float target = mag > gate * 2 ? 1f
                        : mag > gate ? (float)(floorGain + (1 - floorGain) * ((mag - gate) / gate))
                        : floorGain;
                    smooth[b] = target < smooth[b]
                        ? 0.6f * smooth[b] + 0.4f * target
                        : 0.85f * smooth[b] + 0.15f * target;
                }

                // light median-of-3 across frequency to suppress isolated bins
                float prev = smooth[0];
                for (int b = 1; b < bins - 1; b++)
                {
                    float a = prev, mid = smooth[b], c = smooth[b + 1];
                    prev = smooth[b];
                    float lo = Math.Min(a, Math.Min(mid, c));
                    float hi = Math.Max(a, Math.Max(mid, c));
                    smooth[b] = a + mid + c - lo - hi;
                }

                for (int b = 0; b < bins; b++)
                {
                    re[b] *= smooth[b];
                    im[b] *= smooth[b];
                    if (b > 0)
                    {
                        re[NrFftSize - b] *= smooth[b];
                        im[NrFftSize - b] *= smooth[b];
                    }
                }

                // inverse FFT via conjugate trick
                for (int i = 0; i < NrFftSize; i++) im[i] = -im[i];
                Fft.Forward(re, im);
                for (int i = 0; i < NrFftSize; i++)
                {
                    int oi = pos + i;
                    if (oi >= n) break;
                    output[oi] += re[i] / NrFftSize * window[i];
                    norm[oi] += window[i] * window[i];
                }
            }

            for (int i = 0; i < n; i++)
                channel[i] = norm[i] > 1e-6f ? output[i] / norm[i] : 0f;
        }
    }

    /// <summary>Detect impulsive clicks via 2nd-derivative outliers and repair with cubic interpolation. Returns repairs made.</summary>
    public static int RemoveClicks(float[][] data, double sensitivity /*1 lax .. 10 aggressive*/)
    {
        const int repairHalf = 12;
        double threshMul = 30.0 / Math.Clamp(sensitivity, 1, 10); // lower = more sensitive
        int total = 0;

        foreach (var x in data)
        {
            int n = x.Length;
            if (n < 64) continue;

            // running RMS of 2nd derivative over ~1024-sample windows
            const int win = 1024;
            int idx = 2;
            while (idx < n - 2)
            {
                int end = Math.Min(idx + win, n - 2);
                double sumSq = 0;
                int m = 0;
                for (int i = idx; i < end; i++)
                {
                    double d2 = x[i + 1] - 2 * x[i] + x[i - 1];
                    sumSq += d2 * d2;
                    m++;
                }
                double rms = Math.Sqrt(sumSq / Math.Max(1, m)) + 1e-9;
                double threshold = rms * threshMul;

                for (int i = idx; i < end; i++)
                {
                    double d2 = Math.Abs(x[i + 1] - 2 * x[i] + x[i - 1]);
                    if (d2 > threshold)
                    {
                        int a = Math.Max(1, i - repairHalf);
                        int b = Math.Min(n - 2, i + repairHalf);
                        // cubic Hermite across the gap using clean edge samples
                        float y0 = x[Math.Max(0, a - 1)], y1 = x[a], y2 = x[b], y3 = x[Math.Min(n - 1, b + 1)];
                        for (int k = a; k <= b; k++)
                        {
                            float t = (float)(k - a) / (b - a);
                            float t2 = t * t, t3 = t2 * t;
                            x[k] = 0.5f * ((2 * y1) + (-y0 + y2) * t +
                                   (2 * y0 - 5 * y1 + 4 * y2 - y3) * t2 +
                                   (-y0 + 3 * y1 - 3 * y2 + y3) * t3);
                        }
                        total++;
                        i = b + repairHalf;
                    }
                }
                idx = end;
            }
        }
        return total;
    }

    /// <summary>Remove mains hum: cascaded notches at the base frequency and its harmonics.</summary>
    public static void RemoveHum(float[][] data, int sampleRate, double baseFreq, int harmonics, double q)
    {
        foreach (var channel in data)
        {
            for (int h = 1; h <= harmonics; h++)
            {
                double f = baseFreq * h;
                if (f >= sampleRate * 0.48) break;
                var notch = Biquad.Notch(sampleRate, f, q);
                for (int i = 0; i < channel.Length; i++)
                    channel[i] = notch.Process(channel[i]);
            }
        }
    }

    /// <summary>Find silent stretches: returns (start, end) sample ranges below threshold lasting at least minLength.</summary>
    public static List<(int Start, int End)> DetectSilences(IReadOnlyList<float[]> channels, int sampleRate,
        double thresholdDb, double minLengthMs)
    {
        double thresholdLin = Math.Pow(10, thresholdDb / 20.0);
        int minLen = Math.Max(1, (int)(minLengthMs / 1000.0 * sampleRate));
        int n = channels[0].Length;
        const int hop = 256;

        var result = new List<(int, int)>();
        int silentStart = -1;
        for (int pos = 0; pos < n; pos += hop)
        {
            int end = Math.Min(pos + hop, n);
            float peak = 0;
            foreach (var ch in channels)
                for (int i = pos; i < end; i++)
                {
                    float a = Math.Abs(ch[i]);
                    if (a > peak) peak = a;
                }
            bool silent = peak < thresholdLin;
            if (silent && silentStart < 0) silentStart = pos;
            else if (!silent && silentStart >= 0)
            {
                if (pos - silentStart >= minLen) result.Add((silentStart, pos));
                silentStart = -1;
            }
        }
        if (silentStart >= 0 && n - silentStart >= minLen) result.Add((silentStart, n));
        return result;
    }
}
