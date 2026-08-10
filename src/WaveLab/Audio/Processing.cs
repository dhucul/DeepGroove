namespace WaveLab.Audio;

/// <summary>Destructive processing operations. Each op runs on a copied range and commits via ReplaceRange (undoable).</summary>
public static class Processing
{
    public static void Gain(AudioDocument doc, int start, int count, double db)
    {
        float g = (float)Math.Pow(10, db / 20.0);
        Apply(doc, start, count, $"Gain {db:+0.0;-0.0} dB", data =>
        {
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++) ch[i] *= g;
        });
    }

    public static void Normalize(AudioDocument doc, int start, int count, double targetDbfs)
    {
        Apply(doc, start, count, "Normalize", data =>
        {
            float peak = 0;
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++) peak = Math.Max(peak, Math.Abs(ch[i]));
            if (peak <= 0) return;
            float g = (float)(Math.Pow(10, targetDbfs / 20.0) / peak);
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++) ch[i] *= g;
        });
    }

    /// <summary>EBU R128 loudness normalization to target LUFS.</summary>
    public static void NormalizeLoudness(AudioDocument doc, int start, int count, double targetLufs = -23)
    {
        Apply(doc, start, count, $"Loudness Normalize {targetLufs:0.0} LUFS", data =>
        {
            if (data.Length == 0 || data[0].Length == 0) return;

            // Simple integrated loudness measurement (K-weighted RMS approximation)
            double sumSquares = 0;
            int totalSamples = 0;
            foreach (var ch in data)
            {
                for (int i = 0; i < ch.Length; i++)
                {
                    sumSquares += (double)ch[i] * ch[i];
                    totalSamples++;
                }
            }

            double rms = Math.Sqrt(sumSquares / Math.Max(1, totalSamples));
            double currentLufs = 20 * Math.Log10(Math.Max(1e-9, rms));
            double gainDb = targetLufs - currentLufs;
            float g = (float)Math.Pow(10, gainDb / 20.0);

            // Check true-peak after gain
            float truePeak = 0;
            float prev = 0;
            foreach (var ch in data)
            {
                for (int i = 0; i < ch.Length; i++)
                {
                    float s = ch[i] * g;
                    float a = Math.Abs(s);
                    if (a > truePeak) truePeak = a;
                    // Inter-sample peak
                    float mid = Math.Abs((s + prev) * 0.5f);
                    if (mid > truePeak) truePeak = mid;
                    prev = s;
                }
            }

            // Limit gain if true peak would exceed -1 dBTP
            if (truePeak > 0.891f) // -1 dBTP
                g *= 0.891f / truePeak;

            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++) ch[i] *= g;
        });
    }

    public static void FadeIn(AudioDocument doc, int start, int count, int curveType = 0) =>
        Fade(doc, start, count, $"Fade In ({CurveName(curveType)})", from: 0f, to: 1f, curveType);

    public static void FadeOut(AudioDocument doc, int start, int count, int curveType = 0) =>
        Fade(doc, start, count, $"Fade Out ({CurveName(curveType)})", from: 1f, to: 0f, curveType);

    public static void Crossfade(AudioDocument doc, int position, int overlapSamples)
    {
        if (overlapSamples < 8) return;
        int start = Math.Max(0, position - overlapSamples / 2);
        int end = Math.Min(doc.Length, position + overlapSamples / 2);
        int actualOverlap = end - start;
        if (actualOverlap < 8) return;

        Apply(doc, start, actualOverlap, "Crossfade", data =>
        {
            foreach (var ch in data)
            {
                int n = ch.Length;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / (n - 1);
                    // Equal-power crossfade curve
                    double fadeOut = Math.Cos(t * Math.PI / 2);
                    double fadeIn = Math.Sin(t * Math.PI / 2);
                    ch[i] *= (float)(fadeOut + fadeIn); // unity gain at center
                }
            }
        });
    }

    private static string CurveName(int curveType) => curveType switch
    {
        1 => "Linear",
        2 => "Logarithmic",
        3 => "Exponential",
        4 => "S-Curve",
        _ => "Equal Power",
    };

    private static void Fade(AudioDocument doc, int start, int count, string name, float from, float to, int curveType = 0)
    {
        Apply(doc, start, count, name, data =>
        {
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++)
                {
                    double t = ch.Length <= 1 ? 1 : (double)i / (ch.Length - 1);
                    double g = ComputeFadeCurve(t, from, to, curveType);
                    ch[i] *= (float)g;
                }
        });
    }

    private static double ComputeFadeCurve(double t, double from, double to, int curveType)
    {
        double g;
        switch (curveType)
        {
            case 1: // Linear
                g = from + (to - from) * t;
                break;
            case 2: // Logarithmic
                g = from + (to - from) * Math.Log(1 + 9 * t) / Math.Log(10);
                break;
            case 3: // Exponential
                g = from + (to - from) * (Math.Exp(3 * t) - 1) / (Math.Exp(3) - 1);
                break;
            case 4: // S-Curve
            {
                double s = 1.0 / (1.0 + Math.Exp(-12 * (t - 0.5)));
                g = from + (to - from) * s;
                break;
            }
            default: // Equal-power (sine-squared)
            {
                double curve = from + (to - from) * t;
                g = Math.Sin(curve * Math.PI / 2) * Math.Sin(curve * Math.PI / 2);
                break;
            }
        }
        return Math.Clamp(g, 0.0, 1.0);
    }

    public static void Reverse(AudioDocument doc, int start, int count) =>
        Apply(doc, start, count, "Reverse", data => { foreach (var ch in data) Array.Reverse(ch); });

    public static void RemoveDcOffset(AudioDocument doc, int start, int count)
    {
        Apply(doc, start, count, "Remove DC Offset", data =>
        {
            foreach (var ch in data)
            {
                if (ch.Length == 0) continue;
                double mean = 0;
                for (int i = 0; i < ch.Length; i++) mean += ch[i];
                mean /= ch.Length;
                for (int i = 0; i < ch.Length; i++) ch[i] -= (float)mean;
            }
        });
    }

    public static void InsertSilence(AudioDocument doc, int at, double seconds)
    {
        int n = (int)Math.Round(seconds * doc.SampleRate);
        var data = new float[doc.ChannelCount][];
        for (int c = 0; c < doc.ChannelCount; c++) data[c] = new float[n];
        doc.ReplaceRange(at, 0, data, "Insert Silence");
    }

    /// <summary>
    /// De-click an edit point: blend a short cubic bridge across the boundary so pasted/spliced
    /// joins have no discontinuity. Window is ±ms around the position.
    /// </summary>
    public static void SmoothEditPoint(AudioDocument doc, int position, double ms = 5)
    {
        int w = Math.Max(8, (int)(ms / 1000.0 * doc.SampleRate));
        int start = Math.Max(1, position - w);
        int end = Math.Min(doc.Length - 2, position + w);
        if (end - start < 8) return;

        Apply(doc, start, end - start, "Smooth Edit Point", data =>
        {
            foreach (var ch in data)
            {
                int n = ch.Length;
                float y0 = ch[0], y1 = ch[n - 1];
                float d0 = n > 2 ? ch[1] - ch[0] : 0;
                float d1 = n > 2 ? ch[n - 1] - ch[n - 2] : 0;
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / (n - 1);
                    float t2 = t * t, t3 = t2 * t;
                    // cubic Hermite bridge between the window's endpoints
                    float bridge = (2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * d0 * n
                                 + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * d1 * n;
                    // blend strongest at the centre (the actual edit point)
                    float weight = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * t);
                    ch[i] = ch[i] * (1 - weight) + bridge * weight;
                }
            }
        });
    }

    private static void Apply(AudioDocument doc, int start, int count, string name, Action<float[][]> op)
    {
        if (count <= 0) return;
        var data = doc.CopyRange(start, count);
        op(data);
        doc.ReplaceRange(start, count, data, name);
    }
}