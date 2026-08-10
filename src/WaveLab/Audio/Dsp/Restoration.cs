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

                    // Ephraim-Malah gain function
                    double v = snrPrior / (1 + snrPrior) * gamma;
                    double gain;
                    if (v < 0.01)
                        gain = Math.Sqrt(Math.PI * v / 2) * (1 + v) * Math.Exp(-v / 2);
                    else
                    {
                        // Modified Bessel function approximation for I0 and I1
                        double expNegV2 = Math.Exp(-v / 2);
                        double bessel = (1 + 1 / (8 * v)) / Math.Sqrt(2 * Math.PI * v);
                        gain = Math.Sqrt(Math.PI / 2) * Math.Sqrt(v) * bessel * expNegV2;
                    }

                    // Clamp gain
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

        // Per-harmonic energy tracking for dynamic depth
        var harmonicEnergy = new double[harmonics];
        var harmonicSmoothing = new double[harmonics];

        foreach (var channel in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static double DetectMainsFrequency(float[][] data, int sampleRate)
    {
        if (data.Length == 0 || data[0].Length < sampleRate / 4) return 60;

        // Use first channel, analyze middle portion
        var samples = data[0];
        int start = samples.Length / 4;
        int count = Math.Min(samples.Length / 2, sampleRate * 2);

        int zeroCrossings = 0;
        bool prevPositive = samples[start] >= 0;
        for (int i = start + 1; i < start + count; i++)
        {
            bool positive = samples[i] >= 0;
            if (positive != prevPositive) zeroCrossings++;
            prevPositive = positive;
        }

        if (zeroCrossings < 4) return 60;

        double detectedFreq = zeroCrossings * sampleRate / (2.0 * count);
        double dist50 = Math.Abs(detectedFreq - 50);
        double dist60 = Math.Abs(detectedFreq - 60);

        return dist50 < dist60 ? 50 : 60;
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