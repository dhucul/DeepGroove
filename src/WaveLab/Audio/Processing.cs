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

    public static void FadeIn(AudioDocument doc, int start, int count) =>
        Fade(doc, start, count, "Fade In", from: 0f, to: 1f);

    public static void FadeOut(AudioDocument doc, int start, int count) =>
        Fade(doc, start, count, "Fade Out", from: 1f, to: 0f);

    private static void Fade(AudioDocument doc, int start, int count, string name, float from, float to)
    {
        Apply(doc, start, count, name, data =>
        {
            foreach (var ch in data)
                for (int i = 0; i < ch.Length; i++)
                {
                    double t = ch.Length <= 1 ? 1 : (double)i / (ch.Length - 1);
                    // equal-power style curve, smoother than linear
                    double g = from + (to - from) * t;
                    ch[i] *= (float)(Math.Sin(g * Math.PI / 2) * Math.Sin(g * Math.PI / 2));
                }
        });
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
