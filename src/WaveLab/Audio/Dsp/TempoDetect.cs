namespace WaveLab.Audio.Dsp;

/// <summary>Tempo estimation: onset-energy envelope autocorrelation, 60–200 BPM.</summary>
public static class TempoDetect
{
    public static (double Bpm, double Confidence) Detect(IReadOnlyList<float[]> channels, int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels.Count == 0) return (0, 0);
        ArgumentNullException.ThrowIfNull(channels[0]);
        int n = channels[0].Length;
        for (int channel = 1; channel < channels.Count; channel++)
        {
            ArgumentNullException.ThrowIfNull(channels[channel]);
            if (channels[channel].Length != n)
                throw new ArgumentException("All channel buffers must have the same length.", nameof(channels));
        }
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
            if ((f & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
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

        double bestScore = 0, bestCorrelation = 0;
        int bestLag = 0;

        double Correlation(int lag)
        {
            double sum = 0, leftEnergy = 0, rightEnergy = 0;
            for (int f = 0; f + lag < maxFrames; f++)
            {
                double left = onset[f], right = onset[f + lag];
                sum += left * right;
                leftEnergy += left * left;
                rightEnergy += right * right;
            }
            double normalizer = Math.Sqrt(leftEnergy * rightEnergy);
            return normalizer > 1e-12 ? sum / normalizer : 0;
        }

        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            if ((lag & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
            double correlation = Correlation(lag);
            // slight bias toward the 90–150 BPM octave
            double bpm = 60 * frameRate / lag;
            double weight = bpm is >= 90 and <= 150 ? 1.1 : 1.0;
            if (correlation * weight > bestScore)
            {
                bestScore = correlation * weight;
                bestCorrelation = correlation;
                bestLag = lag;
            }
        }
        if (bestLag == 0) return (0, 0);

        // parabolic refinement
        double refined = bestLag;
        if (bestLag > lagMin && bestLag < lagMax)
        {
            double s0 = Correlation(bestLag - 1), s1 = Correlation(bestLag),
                s2 = Correlation(bestLag + 1);
            double denom = 2 * (2 * s1 - s0 - s2);
            if (Math.Abs(denom) > 1e-12) refined = bestLag + (s0 - s2) / denom;
        }

        double resultBpm = 60 * frameRate / refined;
        double confidence = Math.Clamp(bestCorrelation, 0, 1);
        return (Math.Round(resultBpm, 1), confidence);
    }
}
