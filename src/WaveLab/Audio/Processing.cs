using WaveLab.Audio.Dsp;

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

            // BS.1770 integrated loudness: K-weighted, gated, with the standard
            // offset. An unweighted RMS lands several dB from the stated target.
            double currentLufs = MeasureIntegratedLufs(data, doc.SampleRate);
            // No gated block survived (silence, or a range shorter than 400 ms):
            // there is no measurable loudness to normalize to.
            if (!double.IsFinite(currentLufs)) return;
            double gainDb = targetLufs - currentLufs;
            float g = (float)Math.Pow(10, gainDb / 20.0);

            // Check true-peak after gain
            float truePeak = 0;
            foreach (var ch in data)
            {
                // Each channel is its own signal: carrying prev across the channel
                // boundary invents a peak that exists in neither channel.
                float prev = 0;
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

    /// <summary>
    /// Integrated loudness (LUFS) of an offline block, measured by driving the same
    /// BS.1770 meter the master section uses. Returns negative infinity when no
    /// block passes the absolute gate.
    /// </summary>
    private static double MeasureIntegratedLufs(float[][] data, int sampleRate)
    {
        int channels = data.Length;
        if (channels == 0 || sampleRate <= 0) return double.NegativeInfinity;
        int frames = data[0].Length;
        if (frames == 0) return double.NegativeInfinity;

        var meter = new LoudnessMeter();
        meter.Configure(sampleRate, channels);
        // The meter consumes interleaved audio; feed it in bounded chunks so the
        // measurement never allocates a second copy of the whole range.
        int chunkFrames = Math.Min(frames, 8192);
        var interleaved = new float[chunkFrames * channels];
        for (int offset = 0; offset < frames; offset += chunkFrames)
        {
            int count = Math.Min(chunkFrames, frames - offset);
            for (int f = 0; f < count; f++)
                for (int c = 0; c < channels; c++)
                    interleaved[f * channels + c] = data[c][offset + f];
            meter.Process(interleaved, 0, count * channels);
        }
        return meter.IntegratedLufs;
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
                    // The pair sums to unity in *power*, and one buffer holds both
                    // sides of the join. Summing the amplitudes instead would put a
                    // +3.01 dB bulge at the centre of the window and clip hot material.
                    ch[i] *= (float)Math.Sqrt(fadeOut * fadeOut + fadeIn * fadeIn);
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
        ArgumentNullException.ThrowIfNull(doc);
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds, "The silence duration must be a finite, non-negative number of seconds.");
        }

        // Rounded as a double and bounded before it is cast: a duration long enough to
        // overflow int reached `new float[n]` as a negative length.
        double exact = Math.Round(seconds * doc.SampleRate);
        long limit = Math.Min(Array.MaxLength, Array.MaxLength - (long)doc.Length);
        if (exact > limit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds, "The silence is longer than this file can grow to hold.");
        }

        var n = (int)exact;
        if (n == 0) return;
        int channels = doc.ChannelCount;
        var data = new float[channels][];
        for (int c = 0; c < channels; c++) data[c] = new float[n];
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
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / (n - 1);
                    float t2 = t * t, t3 = t2 * t;
                    // Smoothstep between the window's endpoints: monotone, and always
                    // bounded by [min(y0, y1), max(y0, y1)]. Scaling Hermite tangents
                    // by the window length turns a one-sample slope into a full-interval
                    // derivative and overshoots full scale by an order of magnitude.
                    float bridge = (2 * t3 - 3 * t2 + 1) * y0 + (-2 * t3 + 3 * t2) * y1;
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