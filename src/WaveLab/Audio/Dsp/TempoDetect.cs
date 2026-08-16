namespace WaveLab.Audio.Dsp;

/// <summary>Tempo estimation: onset-energy envelope autocorrelation, 60–200 BPM.</summary>
public static class TempoDetect
{
    public static (double Bpm, double Confidence) Detect(IReadOnlyList<float[]> channels, int sampleRate)
    {
        int n = channels[0].Length;
        const int hop = 512;

        // analyze up to 60 s from the middle of the material
        int maxFrames = Math.Min(n / hop, 60 * sampleRate / hop);
        // Shorter than a few hops there is no envelope to analyse; bail out before
        // the mean below would run over an empty array.
        if (maxFrames < 4) return (0, 0);
        int startFrame = Math.Max(0, (n / hop - maxFrames) / 2);

        // energy envelope
        var energy = new double[maxFrames];
        for (int f = 0; f < maxFrames; f++)
        {
            int s0 = (startFrame + f) * hop;
            int s1 = Math.Min(s0 + hop, n);
            double sum = 0;
            foreach (var ch in channels)
                for (int i = s0; i < s1; i++)
                    sum += ch[i] * ch[i];
            energy[f] = Math.Sqrt(sum / Math.Max(1, s1 - s0));
        }

        // half-wave rectified difference (onsets), lightly smoothed
        var onset = new double[maxFrames];
        for (int f = 1; f < maxFrames; f++)
            onset[f] = Math.Max(0, energy[f] - energy[f - 1]);
        for (int f = 1; f < maxFrames; f++)
            onset[f] = 0.7 * onset[f] + 0.3 * onset[f - 1];

        double mean = onset.Average();
        for (int f = 0; f < maxFrames; f++) onset[f] -= mean;

        double frameRate = (double)sampleRate / hop;
        int lagMin = (int)(frameRate * 60 / 200); // 200 BPM
        int lagMax = (int)(frameRate * 60 / 60);  // 60 BPM
        if (lagMax >= maxFrames / 2) return (0, 0);

        double bestScore = 0, zeroLag = 0;
        int bestLag = 0;
        for (int f = 0; f < maxFrames; f++) zeroLag += onset[f] * onset[f];
        if (zeroLag < 1e-12) return (0, 0);

        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            double sum = 0;
            for (int f = 0; f + lag < maxFrames; f++)
                sum += onset[f] * onset[f + lag];
            // slight bias toward the 90–150 BPM octave
            double bpm = 60 * frameRate / lag;
            double weight = bpm is >= 90 and <= 150 ? 1.1 : 1.0;
            if (sum * weight > bestScore) { bestScore = sum * weight; bestLag = lag; }
        }
        if (bestLag == 0) return (0, 0);

        // parabolic refinement
        double refined = bestLag;
        if (bestLag > lagMin && bestLag < lagMax)
        {
            double S(int lag)
            {
                double sum = 0;
                for (int f = 0; f + lag < maxFrames; f++) sum += onset[f] * onset[f + lag];
                return sum;
            }
            double s0 = S(bestLag - 1), s1 = S(bestLag), s2 = S(bestLag + 1);
            double denom = 2 * (2 * s1 - s0 - s2);
            if (Math.Abs(denom) > 1e-12) refined = bestLag + (s0 - s2) / denom;
        }

        double resultBpm = 60 * frameRate / refined;
        double confidence = Math.Clamp(bestScore / zeroLag * 4, 0, 1);
        return (Math.Round(resultBpm, 1), confidence);
    }
}
