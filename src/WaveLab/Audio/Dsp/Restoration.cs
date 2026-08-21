namespace WaveLab.Audio.Dsp;

/// <summary>Restoration DSP: spectral noise reduction, click repair, hum removal, silence detection.</summary>
public static partial class Restoration
{
    public const int NrFftSize = 2048;
    private const int NrHop = NrFftSize / 4;

    /// <summary>Average magnitude spectrum of a region — the "noise print".</summary>
    public static float[] LearnNoiseProfile(IReadOnlyList<float[]> channels, int start, int count,
        CancellationToken cancellationToken = default)
    {
        int sampleCount = ValidateRestorationChannels(channels);
        if (channels.Count == 0)
            throw new ArgumentException("At least one audio channel is required.", nameof(channels));
        if (start < 0 || start > sampleCount)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (count <= 0 || count > sampleCount - start)
            throw new ArgumentOutOfRangeException(nameof(count));

        var window = Fft.HannWindow(NrFftSize);
        var profile = new double[NrFftSize / 2];
        var re = new float[NrFftSize];
        var im = new float[NrFftSize];
        int frames = 0;

        for (int pos = start; pos + NrFftSize <= start + count; pos += NrHop)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        if (frames == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(re);
            Array.Clear(im);
            int windowOffset = (NrFftSize - count) / 2;
            for (int i = 0; i < count; i++)
            {
                float mono = 0;
                foreach (var channel in channels) mono += channel[start + i];
                int windowIndex = windowOffset + i;
                re[windowIndex] = mono / channels.Count * window[windowIndex];
            }
            Fft.Forward(re, im);
            for (int bin = 0; bin < profile.Length; bin++)
                profile[bin] = Math.Sqrt(re[bin] * re[bin] + im[bin] * im[bin]);
            frames = 1;
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
    public static void ReduceNoise(float[][] data, float[] profile, double reductionDb,
        double sensitivityDb, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(profile);
        ValidateRestorationChannels(data);
        if (profile.Length == 0)
            throw new ArgumentException("A noise profile must contain at least one frequency bin.", nameof(profile));

        var window = Fft.HannWindow(NrFftSize);
        float floorGain = (float)Math.Pow(10, -Math.Abs(reductionDb) / 20.0);
        double thresholdMul = Math.Pow(10, sensitivityDb / 20.0);
        // DC through Nyquist inclusive, which is what the processor is handed. Stopping
        // at NrFftSize / 2 left the Nyquist bin passing through at unity while every
        // other bin was gated.
        int bins = NrFftSize / 2 + 1;

        var stft = NoiseReductionStft(window);

        foreach (var channel in data)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var smooth = new float[bins];
            for (int b = 0; b < bins; b++) smooth[b] = 1f;

            stft.Process(channel, channel, (_, _, re, im) =>
            {
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

                // Median of three across frequency, which removes isolated gain spikes — the
                // "musical noise" a per-bin gate otherwise leaves behind.
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
                }
            }, cancellationToken);
        }
    }

    /// <summary>
    /// The overlap-add configuration both offline noise-reduction passes run on. It is deliberately
    /// not the framework default: these use the symmetric Hann they were tuned around, start the
    /// first frame at sample zero, and normalize by the weight actually accumulated on each output
    /// sample — which is what lets the opening samples, sitting under a window value of zero, pass
    /// through untouched instead of being divided by nothing.
    /// </summary>
    private static Stft NoiseReductionStft(float[] window) =>
        new(NrFftSize, NrHop, window, window, StftLeadIn.None, StftNormalization.RunningSum);

    /// <summary>
    /// Compatibility entry point for automatic click/pop analysis and repair. New callers
    /// should pass the real sample rate to the overload in Restoration.Advanced.
    /// </summary>
    public static int RemoveClicks(float[][] data, double sensitivity /*1 lax .. 10 aggressive*/)
    {
        return RemoveClicks(data, DefaultLegacySampleRate, sensitivity);
    }

    /// <summary>Remove mains hum: cascaded notches at the base frequency and its harmonics.</summary>
    public static void RemoveHum(float[][] data, int sampleRate, double baseFreq, int harmonics,
        double q, CancellationToken cancellationToken = default)
    {
        RemoveHum(data, sampleRate, baseFreq, harmonics, q, 1.0, cancellationToken);
    }

    /// <summary>
    /// Remove mains hum with an adjustable notch amount. A strength of zero is a
    /// bit-exact no-op; one applies the complete notch bank.
    /// </summary>
    public static void RemoveHum(float[][] data, int sampleRate, double baseFreq, int harmonics,
        double q, double strength, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float amount = (float)Math.Clamp(strength, 0.0, 1.0);
        if (amount <= 0f) return;

        foreach (var channel in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int h = 1; h <= harmonics; h++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double f = baseFreq * h;
                if (f >= sampleRate * 0.48) break;
                var notch = Biquad.Notch(sampleRate, f, q);
                for (int i = 0; i < channel.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    float dry = channel[i];
                    float filtered = notch.Process(dry);
                    channel[i] = amount >= 1f
                        ? filtered
                        : dry + (filtered - dry) * amount;
                }
            }
        }
    }

    /// <summary>
    /// Advanced hum removal with adaptive fundamental tracking and dynamic notch depth.
    /// Compares the energy actually present at each mains candidate to decide the
    /// fundamental, and reduces notch depth at harmonics where the energy the notch is
    /// removing behaves like music rather than hum.
    /// </summary>
    public static void RemoveHumAdvanced(float[][] data, int sampleRate, double baseFreq, int harmonics,
        double q, double strength, bool adaptiveFrequency = true, bool dynamicDepth = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float amount = (float)Math.Clamp(strength, 0.0, 1.0);
        if (amount <= 0f) return;

        // Adaptive frequency detection via zero-crossing analysis
        double effectiveFreq = baseFreq;
        if (adaptiveFrequency)
        {
            effectiveFreq = DetectMainsFrequency(data, sampleRate, baseFreq);
        }

        foreach (var channel in data)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per-channel harmonic energy tracking for dynamic depth
            var harmonicEnergy = new double[harmonics];
            var harmonicSmoothing = new double[harmonics];

            for (int h = 1; h <= harmonics; h++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double f = effectiveFreq * h;
                if (f >= sampleRate * 0.48) break;
                var notch = Biquad.Notch(sampleRate, f, q);

                int hIdx = h - 1;
                for (int i = 0; i < channel.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    float dry = channel[i];
                    float filtered = notch.Process(dry);

                    if (dynamicDepth)
                    {
                        // What the notch removes is the energy at this harmonic; |dry| was the
                        // whole programme, so anything above about -40 dBFS pinned every notch
                        // at 30% strength for the length of the file whatever the hum was doing.
                        double harmonic = Math.Abs(dry - filtered);

                        // Hum is steady, so a fast and a slow envelope of what the notch takes
                        // out agree with each other. Musical energy sitting on the harmonic
                        // moves, and the two diverge — which is the actual discriminator, and
                        // the one the two arrays here were always named for.
                        harmonicEnergy[hIdx] = 0.95 * harmonicEnergy[hIdx] + 0.05 * harmonic;
                        harmonicSmoothing[hIdx] = 0.9995 * harmonicSmoothing[hIdx] + 0.0005 * harmonic;

                        double slow = Math.Max(1e-9, harmonicSmoothing[hIdx]);
                        double variation = Math.Abs(harmonicEnergy[hIdx] - slow) / slow;
                        double dynamicAmount =
                            amount * (1 - 0.7 * Math.Clamp(variation - 0.5, 0.0, 1.0));
                        channel[i] = dry + (filtered - dry) * (float)dynamicAmount;
                    }
                    else
                    {
                        channel[i] = amount >= 1f
                            ? filtered
                            : dry + (filtered - dry) * amount;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Mean Goertzel power of one frequency bin over a sample range. Returns 0 for an
    /// empty or non-finite range so callers can compare bins without further guards.
    /// </summary>
    private static double GoertzelPower(float[] samples, int start, int count,
        double frequency, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0 || count <= 0) return 0;
        if (frequency <= 0 || frequency >= sampleRate / 2.0) return 0;
        int first = Math.Max(0, start);
        int end = Math.Min(samples.Length, first + count);
        int length = end - first;
        if (length <= 0) return 0;

        double coeff = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
        double s1 = 0, s2 = 0;
        for (int i = first; i < end; i++)
        {
            double sample = samples[i];
            if (!double.IsFinite(sample)) sample = 0;
            double s0 = sample + coeff * s1 - s2;
            if (!double.IsFinite(s0)) return 0;
            s2 = s1;
            s1 = s0;
        }

        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return double.IsFinite(power) && power > 0 ? power / length : 0;
    }

    /// <summary>
    /// Which mains frequency the transfer actually carries, or <paramref name="fallback"/>
    /// when neither candidate stands out far enough to be believed.
    /// </summary>
    /// <remarks>
    /// The gate is the point. Comparing the two bins and taking the larger always returns
    /// an answer, so on a record with bass energy near 50 or 60 Hz it was a coin flip that
    /// silently overrode whatever the user had chosen. A 55 Hz probe sits between the
    /// candidates and carries no mains component, so it measures the local floor: neither
    /// candidate is hum unless it stands well above that, and above the other.
    /// </remarks>
    private static double DetectMainsFrequency(float[][] data, int sampleRate, double fallback)
    {
        if (data.Length == 0 || sampleRate <= 0 || data[0].Length < sampleRate / 4) return fallback;

        // Use first channel, analyze middle portion
        var samples = data[0];
        int start = samples.Length / 4;
        int count = Math.Min(samples.Length / 2, sampleRate * 2);

        // Whole 100 ms blocks hold an exact number of 50 Hz and 60 Hz cycles, so
        // both bins land on complete cycles and the two powers stay comparable.
        int block = Math.Max(1, sampleRate / 10);
        count -= count % block;
        if (count < block) return fallback;

        // Compare the energy actually present at each candidate rather than
        // inferring a frequency from broadband zero crossings, which measures the
        // programme, not the hum.
        double power50 = GoertzelPower(samples, start, count, 50, sampleRate);
        double power60 = GoertzelPower(samples, start, count, 60, sampleRate);
        double floor = GoertzelPower(samples, start, count, 55, sampleRate);

        double winner = Math.Max(power50, power60);
        double loser = Math.Min(power50, power60);
        if (!(winner > floor * 4) || !(winner > loser * 2)) return fallback;
        return power50 > power60 ? 50 : 60;
    }

    /// <summary>Find silent stretches: returns (start, end) sample ranges below threshold lasting at least minLength.</summary>
    public static List<(int Start, int End)> DetectSilences(IReadOnlyList<float[]> channels, int sampleRate,
        double thresholdDb, double minLengthMs, CancellationToken cancellationToken = default)
    {
        int n = ValidateRestorationChannels(channels, sampleRate);
        if (n == 0) return [];
        double thresholdLin = Math.Pow(10, thresholdDb / 20.0);
        int minLen = Math.Max(1, (int)(minLengthMs / 1000.0 * sampleRate));
        const int hop = 256;

        var result = new List<(int, int)>();
        int silentStart = -1;
        for (int pos = 0; pos < n; pos += hop)
        {
            if ((pos & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Advanced silence detection using RMS with hysteresis and adaptive threshold.
    /// More robust against isolated sample spikes and subsonic rumble.
    /// </summary>
    public static List<(int Start, int End)> DetectSilencesAdvanced(IReadOnlyList<float[]> channels, int sampleRate,
        double thresholdDb, double minLengthMs, double hysteresisDb = 6,
        CancellationToken cancellationToken = default)
    {
        int n = ValidateRestorationChannels(channels, sampleRate);
        if (n == 0) return [];
        double thresholdLin = Math.Pow(10, thresholdDb / 20.0);
        double openThreshold = Math.Pow(10, (thresholdDb + hysteresisDb) / 20.0);
        int minLen = Math.Max(1, (int)(minLengthMs / 1000.0 * sampleRate));
        int hop = Math.Max(64, sampleRate / 100); // ~10ms hops

        // Simple high-pass to ignore subsonic rumble
        double hpfCoeff = Math.Exp(-2 * Math.PI * 20 / sampleRate);
        var hpfState = new double[channels.Count];

        var result = new List<(int, int)>();
        int silentStart = -1;
        bool isSilent = false;

        for (int pos = 0; pos < n; pos += hop)
        {
            if ((pos & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(pos + hop, n);
            double rmsSum = 0;
            int count = 0;

            for (int c = 0; c < channels.Count; c++)
            {
                for (int i = pos; i < end; i++)
                {
                    double sample = channels[c][i];
                    // High-pass filter to remove subsonic content
                    hpfState[c] += (1 - hpfCoeff) * (sample - hpfState[c]);
                    double filtered = sample - hpfState[c];
                    rmsSum += filtered * filtered;
                    count++;
                }
            }

            double rms = Math.Sqrt(rmsSum / Math.Max(1, count));

            if (isSilent)
            {
                if (rms > openThreshold)
                {
                    isSilent = false;
                    if (pos - silentStart >= minLen)
                        result.Add((silentStart, pos));
                    silentStart = -1;
                }
            }
            else
            {
                if (rms < thresholdLin)
                {
                    isSilent = true;
                    silentStart = pos;
                }
            }
        }

        if (isSilent && silentStart >= 0 && n - silentStart >= minLen)
            result.Add((silentStart, n));

        return result;
    }
}