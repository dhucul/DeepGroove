namespace WaveLab.Audio.Dsp;

/// <summary>
/// Plans the bounded lead-in used by restoration previews. Stateful restoration
/// still starts on the same global STFT hop grid as a full render, while old IIR
/// and spectral-gate state is allowed to decay below the float noise floor before
/// the audible preview begins.
/// </summary>
internal static class RestorationPreviewPlanning
{
    internal const double StateResidualDb = -180.0;
    internal const double MaximumWarmupSeconds = 12.0;

    internal readonly record struct Plan(int StartSample, int WarmupSamples, bool StartsAtRangeOrigin);

    internal static Plan Create(int previewStart, int sampleRate,
        bool removeHum, double humFrequency, double humQ, bool reduceNoise)
    {
        if (previewStart < 0) throw new ArgumentOutOfRangeException(nameof(previewStart));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        int fftSize = Restoration.NrFftSize;
        int hopSize = fftSize / 4;
        double residual = Math.Pow(10.0, StateResidualDb / 20.0);
        long warmup = 0;

        if (removeHum)
        {
            // RBJ notch poles have radius sqrt(a2). Calculate enough samples for
            // zero-state startup error to decay beneath our residual target.
            double frequency = Math.Clamp(humFrequency, 1.0, sampleRate * 0.48);
            double q = Math.Clamp(humQ, 0.1, 1_000.0);
            double alpha = Math.Sin(2.0 * Math.PI * frequency / sampleRate) / (2.0 * q);
            double denominator = 1.0 + alpha;
            double radius = denominator > 0
                ? Math.Sqrt(Math.Abs((1.0 - alpha) / denominator))
                : 0.0;
            if (radius > 0.0 && radius < 1.0)
                warmup = Math.Max(warmup, (long)Math.Ceiling(Math.Log(residual) / Math.Log(radius)));
        }

        if (reduceNoise)
        {
            // The spectral gate's slowest state coefficient is 0.85 per hop.
            // Add one FFT window so hum has settled before any frame that can
            // overlap the audible preview is analyzed.
            long smoothingFrames = (long)Math.Ceiling(Math.Log(residual) / Math.Log(0.85));
            long spectralWarmup = smoothingFrames * hopSize + fftSize;
            warmup = Math.Max(warmup + (removeHum ? fftSize : 0), spectralWarmup);
        }

        long maximum = (long)Math.Ceiling(sampleRate * MaximumWarmupSeconds);
        int boundedWarmup = (int)Math.Min(Math.Min(warmup, maximum), previewStart);
        int start = previewStart - boundedWarmup;

        // ReduceNoise starts frames at local sample zero. Align the bounded
        // buffer to the full render's global hop grid to prevent phase shifts.
        if (reduceNoise && start > 0)
            start -= start % hopSize;

        return new Plan(start, previewStart - start, start == 0);
    }
}
