namespace WaveLab.Audio.Dsp;

/// <summary>WSOLA time stretching and (via resampling) pitch shifting.</summary>
public static class TimeStretch
{
    /// <summary>Stretch duration by <paramref name="factor"/> (2.0 = twice as long) without changing pitch.</summary>
    public static float[][] Stretch(IReadOnlyList<float[]> channels, int sampleRate, double factor)
    {
        factor = Math.Clamp(factor, 0.25, 4.0);
        int n = channels[0].Length;
        int chCount = channels.Count;

        int segment = Math.Max(256, (int)(sampleRate * 0.050));   // 50 ms
        int overlap = segment / 2;
        int synHop = segment - overlap;
        double anaHop = synHop / factor;
        int search = Math.Max(16, (int)(sampleRate * 0.008));     // ±8 ms

        // mono guide for correlation, zero-padded so the loop can consume the input tail
        int pad = segment + search;
        int paddedN = n + pad;
        var guide = new float[paddedN];
        for (int i = 0; i < n; i++)
        {
            float v = 0;
            for (int c = 0; c < chCount; c++) v += channels[c][i];
            guide[i] = v / chCount;
        }

        int targetLen = Math.Max(1, (int)Math.Round(n * factor));
        int outCapacity = targetLen + segment * 2;
        var output = new float[chCount][];
        for (int c = 0; c < chCount; c++) output[c] = new float[outCapacity];
        var outGuide = new float[outCapacity];

        var fadeIn = new float[overlap];
        var fadeOut = new float[overlap];
        for (int i = 0; i < overlap; i++)
        {
            double t = (double)i / overlap;
            fadeIn[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * t));
            fadeOut[i] = 1 - fadeIn[i];
        }

        int outPos = 0;
        double srcPos = 0;
        bool first = true;

        while (outPos + segment < outCapacity && (int)srcPos + segment + search < paddedN)
        {
            int ideal = (int)srcPos;
            int best = ideal;

            if (!first)
            {
                double bestScore = double.NegativeInfinity;
                int lo = Math.Max(0, ideal - search);
                int hi = Math.Min(paddedN - segment - 1, ideal + search);
                for (int cand = lo; cand <= hi; cand += 4)
                {
                    double score = 0;
                    for (int i = 0; i < overlap; i += 8)
                        score += outGuide[outPos + i] * guide[cand + i];
                    if (score > bestScore) { bestScore = score; best = cand; }
                }
            }

            for (int c = 0; c < chCount; c++)
            {
                var src = channels[c];
                var dst = output[c];
                for (int i = 0; i < segment; i++)
                {
                    int si = best + i;
                    int oi = outPos + i;
                    float s = si < n ? src[si] : 0f; // zero-padded tail
                    if (i < overlap && !first)
                        dst[oi] = dst[oi] * fadeOut[i] + s * fadeIn[i];
                    else
                        dst[oi] = s;
                }
            }
            for (int i = 0; i < segment; i++)
            {
                int si = best + i;
                int oi = outPos + i;
                float s = si < paddedN ? guide[si] : 0f;
                if (i < overlap && !first)
                    outGuide[oi] = outGuide[oi] * fadeOut[i] + s * fadeIn[i];
                else
                    outGuide[oi] = s;
            }

            first = false;
            outPos += synHop;
            srcPos += anaHop;
        }

        int finalLen = Math.Max(1, Math.Min(outPos + overlap, targetLen));
        var trimmed = new float[chCount][];
        for (int c = 0; c < chCount; c++)
        {
            trimmed[c] = new float[finalLen];
            Array.Copy(output[c], trimmed[c], finalLen);
        }
        return trimmed;
    }

    /// <summary>Shift pitch by semitones (+cents) keeping duration: WSOLA stretch then windowed-sinc resample.</summary>
    public static float[][] PitchShift(IReadOnlyList<float[]> channels, int sampleRate, double semitones)
    {
        double pitchFactor = Math.Pow(2, semitones / 12.0);
        if (Math.Abs(pitchFactor - 1) < 1e-4)
        {
            var copy = new float[channels.Count][];
            for (int c = 0; c < channels.Count; c++) copy[c] = (float[])channels[c].Clone();
            return copy;
        }
        var stretched = Stretch(channels, sampleRate, pitchFactor);
        // play the stretched audio "faster" by pitchFactor: resample from rate*factor back to rate
        int virtualRate = Math.Max(4000, (int)Math.Round(sampleRate * pitchFactor));
        return Resampler.Resample(stretched, virtualRate, sampleRate);
    }
}
