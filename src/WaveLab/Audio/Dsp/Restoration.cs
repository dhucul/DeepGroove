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
        int bins = NrFftSize / 2;

        foreach (var channel in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = channel.Length;
            var output = new float[NrFftSize];
            var norm = new float[NrFftSize];
            var re = new float[NrFftSize];
            var im = new float[NrFftSize];
            var smooth = new float[bins];
            for (int b = 0; b < bins; b++) smooth[b] = 1f;
            int nextOutput = 0;

            for (int pos = 0; pos < n; pos += NrHop)
            {
                if ((pos & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                Array.Clear(im);
                for (int i = 0; i < NrFftSize; i++)
                    re[i] = (pos + i < n ? channel[pos + i] : 0f) * window[i];
                Fft.Forward(re, im);

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
                    if (b > 0)
                    {
                        re[NrFftSize - b] *= smooth[b];
                        im[NrFftSize - b] *= smooth[b];
                    }
                }

                for (int i = 0; i < NrFftSize; i++) im[i] = -im[i];
                Fft.Forward(re, im);
                for (int i = 0; i < NrFftSize; i++)
                {
                    int oi = pos + i;
                    if (oi >= n) break;
                    int slot = oi % NrFftSize;
                    output[slot] += re[i] / NrFftSize * window[i];
                    norm[slot] += window[i] * window[i];
                }

                int finalizedThrough = Math.Min(n, pos + NrHop);
                while (nextOutput < finalizedThrough)
                {
                    int slot = nextOutput % NrFftSize;
                    if (norm[slot] > 1e-6f)
                        channel[nextOutput] = output[slot] / norm[slot];
                    output[slot] = 0f;
                    norm[slot] = 0f;
                    nextOutput++;
                }
            }
        }
    }

    /// <summary>
    /// Advanced Ephraim-Malah MMSE noise reduction with decision-directed a priori SNR
    /// estimation. Produces significantly fewer musical noise artifacts than the simple
    /// spectral gate. Uses the same learned noise profile.
    /// </summary>
    public static void ReduceNoiseAdvanced(float[][] data, float[] profile, double reductionDb,
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
        int bins = NrFftSize / 2;
        double alpha = 0.98; // decision-directed smoothing

        foreach (var channel in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = channel.Length;
            var output = new float[NrFftSize];
            var norm = new float[NrFftSize];
            var re = new float[NrFftSize];
            var im = new float[NrFftSize];
            var prevGain = new float[bins];
            var prevPower = new double[bins];
            for (int b = 0; b < bins; b++) { prevGain[b] = 1f; prevPower[b] = 0; }
            int nextOutput = 0;

            for (int pos = 0; pos < n; pos += NrHop)
            {
                if ((pos & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                Array.Clear(im);
                for (int i = 0; i < NrFftSize; i++)
                    re[i] = (pos + i < n ? channel[pos + i] : 0f) * window[i];
                Fft.Forward(re, im);

                for (int b = 0; b < bins; b++)
                {
                    double noisyPower = (re[b] * re[b] + im[b] * im[b]);
                    double noisePower = profile[Math.Min(b, profile.Length - 1)] * thresholdMul;
                    noisePower *= noisePower;

                    // A posteriori SNR
                    double gamma = noisyPower / Math.Max(1e-12, noisePower);

                    // Decision-directed a priori SNR estimate
                    double snrPrior = alpha * prevGain[b] * prevGain[b] * prevPower[b] / Math.Max(1e-12, noisePower)
                                    + (1 - alpha) * Math.Max(0, gamma - 1);
                    snrPrior = Math.Max(floorGain * floorGain, snrPrior);

                    // Ephraim-Malah MMSE-STSA gain:
                    //   G = (sqrt(pi)/2) * (sqrt(v)/gamma) * e^(-v/2)
                    //       * [(1 + v) I0(v/2) + v I1(v/2)]
                    // which rises monotonically with SNR and tends to the Wiener
                    // gain v/gamma for large v.
                    double v = snrPrior / (1 + snrPrior) * gamma;
                    double gain;
                    if (!double.IsFinite(v) || v <= 0)
                        gain = floorGain;
                    else if (v > 5000)
                    {
                        // Wiener asymptote; the bracket below agrees with it to
                        // within 1/(2v) here, so the join is continuous.
                        gain = v / Math.Max(1e-12, gamma);
                    }
                    else
                    {
                        double half = v / 2;
                        double bracket = (1 + v) * ScaledBesselI0(half) + v * ScaledBesselI1(half);
                        gain = Math.Sqrt(Math.PI) / 2 * Math.Sqrt(v) / Math.Max(1e-12, gamma) * bracket;
                    }

                    // Clamp gain (Math.Clamp would propagate a NaN, so screen first)
                    if (!double.IsFinite(gain)) gain = floorGain;
                    gain = Math.Clamp(gain, floorGain, 1.0);

                    // Smooth gain across time
                    float targetGain = (float)gain;
                    prevGain[b] = targetGain < prevGain[b]
                        ? 0.7f * prevGain[b] + 0.3f * targetGain
                        : 0.9f * prevGain[b] + 0.1f * targetGain;

                    prevPower[b] = noisyPower;

                    re[b] *= prevGain[b];
                    im[b] *= prevGain[b];
                    if (b > 0)
                    {
                        re[NrFftSize - b] *= prevGain[b];
                        im[NrFftSize - b] *= prevGain[b];
                    }
                }

                for (int i = 0; i < NrFftSize; i++) im[i] = -im[i];
                Fft.Forward(re, im);
                for (int i = 0; i < NrFftSize; i++)
                {
                    int oi = pos + i;
                    if (oi >= n) break;
                    int slot = oi % NrFftSize;
                    output[slot] += re[i] / NrFftSize * window[i];
                    norm[slot] += window[i] * window[i];
                }

                int finalizedThrough = Math.Min(n, pos + NrHop);
                while (nextOutput < finalizedThrough)
                {
                    int slot = nextOutput % NrFftSize;
                    if (norm[slot] > 1e-6f)
                        channel[nextOutput] = output[slot] / norm[slot];
                    output[slot] = 0f;
                    norm[slot] = 0f;
                    nextOutput++;
                }
            }
        }
    }

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
    /// Uses zero-crossing analysis to detect the actual mains frequency and reduces
    /// notch depth at harmonics where sustained musical energy is present.
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
            effectiveFreq = DetectMainsFrequency(data, sampleRate);
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
                        double energy = Math.Abs(dry);
                        harmonicEnergy[hIdx] = 0.95 * harmonicEnergy[hIdx] + 0.05 * energy;
                        harmonicSmoothing[hIdx] = 0.9 * harmonicSmoothing[hIdx] + 0.1 * harmonicEnergy[hIdx];

                        double dynamicAmount = amount;
                        if (harmonicSmoothing[hIdx] > 0.01)
                        {
                            double reduction = Math.Clamp(harmonicSmoothing[hIdx] * 20, 0, 1);
                            dynamicAmount *= (1 - reduction * 0.7);
                        }
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

    // Modified Bessel functions scaled by e^-x (Abramowitz & Stegun 9.8.1-9.8.4).
    // Returning the scaled value keeps the Ephraim-Malah bracket finite at every v:
    // I_n(v/2) overflows long before e^(-v/2) * I_n(v/2) stops being representable.

    /// <summary>e^-x * I0(x); the underlying I0 approximation is accurate to ~2e-7.</summary>
    private static double ScaledBesselI0(double x)
    {
        x = Math.Abs(x);
        if (x < 3.75)
        {
            double t = x / 3.75, t2 = t * t;
            double i0 = 1 + t2 * (3.5156229 + t2 * (3.0899424 + t2 * (1.2067492
                + t2 * (0.2659732 + t2 * (0.0360768 + t2 * 0.0045813)))));
            return i0 * Math.Exp(-x);
        }

        double u = 3.75 / x;
        double poly = 0.39894228 + u * (0.01328592 + u * (0.00225319 + u * (-0.00157565
            + u * (0.00916281 + u * (-0.02057706 + u * (0.02635537 + u * (-0.01647633
            + u * 0.00392377)))))));
        return poly / Math.Sqrt(x);
    }

    /// <summary>e^-x * I1(x); the underlying I1 approximation is accurate to ~2e-7.</summary>
    private static double ScaledBesselI1(double x)
    {
        double magnitude = Math.Abs(x);
        double result;
        if (magnitude < 3.75)
        {
            double t = magnitude / 3.75, t2 = t * t;
            result = magnitude * (0.5 + t2 * (0.87890594 + t2 * (0.51498869 + t2 * (0.15084934
                + t2 * (0.02658733 + t2 * (0.00301532 + t2 * 0.00032411))))))
                * Math.Exp(-magnitude);
        }
        else
        {
            double u = 3.75 / magnitude;
            double poly = 0.39894228 + u * (-0.03988024 + u * (-0.00362018 + u * (0.00163801
                + u * (-0.01031555 + u * (0.02282967 + u * (-0.02895312 + u * (0.01787654
                + u * -0.00420059)))))));
            result = poly / Math.Sqrt(magnitude);
        }
        return x < 0 ? -result : result;
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

    private static double DetectMainsFrequency(float[][] data, int sampleRate)
    {
        if (data.Length == 0 || sampleRate <= 0 || data[0].Length < sampleRate / 4) return 60;

        // Use first channel, analyze middle portion
        var samples = data[0];
        int start = samples.Length / 4;
        int count = Math.Min(samples.Length / 2, sampleRate * 2);

        // Whole 100 ms blocks hold an exact number of 50 Hz and 60 Hz cycles, so
        // both bins land on complete cycles and the two powers stay comparable.
        int block = Math.Max(1, sampleRate / 10);
        count -= count % block;
        if (count < block) return 60;

        // Compare the energy actually present at each candidate rather than
        // inferring a frequency from broadband zero crossings, which measures the
        // programme, not the hum.
        double power50 = GoertzelPower(samples, start, count, 50, sampleRate);
        double power60 = GoertzelPower(samples, start, count, 60, sampleRate);

        return power50 > power60 ? 50 : 60;
    }

    /// <summary>Find silent stretches: returns (start, end) sample ranges below threshold lasting at least minLength.</summary>
    public static List<(int Start, int End)> DetectSilences(IReadOnlyList<float[]> channels, int sampleRate,
        double thresholdDb, double minLengthMs, CancellationToken cancellationToken = default)
    {
        double thresholdLin = Math.Pow(10, thresholdDb / 20.0);
        int minLen = Math.Max(1, (int)(minLengthMs / 1000.0 * sampleRate));
        int n = channels[0].Length;
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
        double thresholdLin = Math.Pow(10, thresholdDb / 20.0);
        double openThreshold = Math.Pow(10, (thresholdDb + hysteresisDb) / 20.0);
        int minLen = Math.Max(1, (int)(minLengthMs / 1000.0 * sampleRate));
        int n = channels[0].Length;
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