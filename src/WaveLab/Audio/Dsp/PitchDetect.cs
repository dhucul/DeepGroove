namespace WaveLab.Audio.Dsp;

/// <summary>YIN pitch detection (median over frames) and note naming.</summary>
public static class PitchDetect
{
    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static (double Frequency, double Confidence) Detect(float[] mono, int sampleRate)
    {
        const int window = 4096;
        int tauMax = Math.Min(window / 2, sampleRate / 50);   // down to 50 Hz
        int tauMin = Math.Max(2, sampleRate / 1500);          // up to 1.5 kHz
        var results = new List<(double f, double conf)>();

        for (int start = 0; start + window * 2 <= mono.Length; start += window)
        {
            var diff = new double[tauMax];
            for (int tau = tauMin; tau < tauMax; tau++)
            {
                double sum = 0;
                for (int i = 0; i < window; i += 2) // stride 2 for speed
                {
                    double d = mono[start + i] - mono[start + i + tau];
                    sum += d * d;
                }
                diff[tau] = sum;
            }

            // cumulative mean normalized difference
            var cmnd = new double[tauMax];
            double running = 0;
            cmnd[0] = 1;
            for (int tau = 1; tau < tauMax; tau++)
            {
                running += diff[tau];
                cmnd[tau] = running > 1e-12 ? diff[tau] * tau / running : 1;
            }

            int found = -1;
            for (int tau = tauMin + 1; tau < tauMax - 1; tau++)
            {
                if (cmnd[tau] < 0.15 && cmnd[tau] <= cmnd[tau + 1])
                {
                    found = tau;
                    break;
                }
            }
            if (found < 0) continue;

            // parabolic interpolation
            double better = found;
            if (found > 1 && found < tauMax - 1)
            {
                double s0 = cmnd[found - 1], s1 = cmnd[found], s2 = cmnd[found + 1];
                double denom = 2 * (2 * s1 - s2 - s0);
                if (Math.Abs(denom) > 1e-12) better = found + (s2 - s0) / denom;
            }
            results.Add((sampleRate / better, 1 - cmnd[found]));
        }

        if (results.Count == 0) return (0, 0);
        var ordered = results.OrderBy(r => r.f).ToList();
        var median = ordered[ordered.Count / 2];
        return (median.f, results.Average(r => r.conf));
    }

    /// <summary>"A4 +12 ¢ (440.0 Hz)" style description.</summary>
    public static string Describe(double frequency)
    {
        if (frequency <= 0) return "No stable pitch detected.";
        double midi = 69 + 12 * Math.Log2(frequency / 440.0);
        int nearest = (int)Math.Round(midi);
        double cents = (midi - nearest) * 100;
        string note = NoteNames[((nearest % 12) + 12) % 12];
        int octave = nearest / 12 - 1;
        return $"{note}{octave} {cents:+0;-0;±0} ¢  ({frequency:0.0} Hz)";
    }
}
