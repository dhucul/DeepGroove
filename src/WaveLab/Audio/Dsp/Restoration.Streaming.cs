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
        bool removeHum, double humFrequency, double humQ, bool reduceNoise,
        bool removeSubsonic = false, double subsonicCutoff = 30.0, int decrackleBlock = 0)
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

        if (removeSubsonic)
        {
            // The same pole calculation as the notch, over both sections of the Butterworth
            // pair - the higher-Q section has the smaller alpha and therefore the slower decay,
            // so the worst of the two is what the lead-in has to cover.
            //
            // <b>This is the one stage whose warm-up the flat fallback pad does not cover.</b>
            // Where hum and noise are both off, the preview used to fall back to a fixed
            // 0.1 s pad; a 30 Hz corner at 44.1 kHz needs about <b>12,700 samples, 0.29 s</b>,
            // and 4,410 of them buy roughly -116 dB rather than the -180 asked for. An IIR
            // high-pass started cold at a preview boundary thumps, and it would have thumped
            // only when the other two stages happened to be switched off.
            double cutoff = Math.Clamp(subsonicCutoff, 1.0, sampleRate * 0.48);
            double w = 2.0 * Math.PI * cutoff / sampleRate;
            foreach (double sectionQ in Restoration.SubsonicSectionQs)
            {
                double alpha = Math.Sin(w) / (2.0 * Math.Clamp(sectionQ, 0.1, 1_000.0));
                double denominator = 1.0 + alpha;
                double radius = denominator > 0
                    ? Math.Sqrt(Math.Abs((1.0 - alpha) / denominator))
                    : 0.0;
                if (radius > 0.0 && radius < 1.0)
                    warmup = Math.Max(warmup, (long)Math.Ceiling(Math.Log(residual) / Math.Log(radius)));
            }
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
        //
        // De-crackle needs the same treatment for a different reason and it is easy to miss,
        // because it carries no IIR state at all. It fits one autoregressive model per block
        // and starts that grid at index zero of whatever array it is handed - so a preview
        // whose buffer begins somewhere else fits its predictors to <em>different audio</em>
        // than the full render does, and the two disagree about what is crackle. The block is
        // a multiple of the hop, so aligning to the larger of the two satisfies both.
        int alignment = 1;
        if (reduceNoise) alignment = hopSize;
        if (decrackleBlock > 0) alignment = LeastCommonMultiple(alignment, decrackleBlock);
        if (alignment > 1 && start > 0)
            start -= start % alignment;

        return new Plan(start, previewStart - start, start == 0);
    }

    /// <remarks>
    /// The default de-crackle block is a whole number of hops, so this is <c>Math.Max</c> in every
    /// case the workbench produces - but the block is <c>max(order * 8, BlockLength)</c> and
    /// neither of those is required to be one, and a maximum would then align to a grid that is
    /// not the other stage's.
    /// </remarks>
    private static int LeastCommonMultiple(int a, int b)
    {
        if (a <= 0 || b <= 0) return Math.Max(Math.Max(a, b), 1);
        int x = a, y = b;
        while (y != 0) (x, y) = (y, x % y);
        long multiple = (long)a / x * b;
        return multiple > int.MaxValue ? Math.Max(a, b) : (int)multiple;
    }
}
