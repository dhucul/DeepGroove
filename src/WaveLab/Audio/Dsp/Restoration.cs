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
    /// How far the programme sits above its own quietest passage, in dB, from the audio alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The quietest two-second window is where a tool looks for a noise profile, so its level is the
    /// best available estimate of the noise floor; the whole signal's level is the programme. A large
    /// number means the noise is far below the music and there is little to remove; a small one means
    /// the noise is most of what is there.
    /// </para>
    /// <para>
    /// <b>Where a recording has no genuinely quiet passage the window contains music, so the floor
    /// reads high and this number reads low.</b> The suppressor then sees less headroom than there
    /// is and reduces more, which is the safe direction: it errs towards doing what the user asked.
    /// </para>
    /// <para>
    /// <b>Where there is nothing to measure it returns <see cref="NoiseDepthCeilingDb"/> - nothing to
    /// remove - and not zero</b>, which would mean the opposite. Zero is a real reading, of a
    /// programme no louder than its own quietest passage, and the rule takes it as a reason to
    /// reduce at full depth; handing that same value back for digital silence made an empty buffer
    /// ask for maximum reduction.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b><paramref name="ceilingDb"/> is here because this method's "no reading" answer is
    /// expressed in it, not because a measurement depends on a policy.</b> Returning the ceiling is
    /// how it says "nothing to remove", and once the ceiling is a setting the two have to move
    /// together: a fixed 10 handed to a rule running at 30 no longer means "nothing", it means
    /// two thirds of the requested depth — so an empty buffer would ask for heavy reduction, which
    /// is the exact defect the remarks above record being fixed once already.
    /// </remarks>
    public static double EstimateNoiseToProgrammeDb(float[][] data, int sampleRate,
        double ceilingDb = NoiseDepthCeilingDb)
    {
        ArgumentNullException.ThrowIfNull(data);
        double noReading = EffectiveCeilingDb(ceilingDb);
        if (data.Length == 0 || sampleRate <= 0) return noReading;

        double best = double.MaxValue;
        foreach (float[] channel in data)
        {
            if (channel is not { Length: > 0 }) continue;
            double estimate = EstimateChannelNoiseToProgrammeDb(channel, sampleRate, noReading);
            if (estimate < best) best = estimate;      // the noisiest channel decides
        }
        return best == double.MaxValue ? noReading : best;
    }

    /// <summary>Lowest ceiling the rule will run at — the shipped default and its measured optimum.</summary>
    /// <remarks>
    /// Measured, every ceiling between 8 and 10 dB scores within 0.01 dB of the best over the 108
    /// cells, so there is nothing below 10 to offer: going lower only protects material that did not
    /// need protecting.
    /// </remarks>
    public const double MinimumNoiseDepthCeilingDb = 10.0;

    /// <summary>Highest ceiling the rule will run at.</summary>
    /// <remarks>
    /// Past about 40 the scaling is doing nothing a fixed depth would not do, and a fixed depth is
    /// what this rule was measured against and beat.
    /// </remarks>
    public const double MaximumNoiseDepthCeilingDb = 40.0;

    private static double EffectiveCeilingDb(double ceilingDb) =>
        double.IsFinite(ceilingDb)
            ? Math.Clamp(ceilingDb, MinimumNoiseDepthCeilingDb, MaximumNoiseDepthCeilingDb)
            : NoiseDepthCeilingDb;

    /// <summary>Below this a window is digital silence rather than a noise floor.</summary>
    /// <remarks>
    /// −200 dBFS, far under any real dither, so only a window that is genuinely zero is rejected.
    /// </remarks>
    private const double SilentWindowRms = 1e-10;

    private static double EstimateChannelNoiseToProgrammeDb(float[] channel, int sampleRate,
        double noReading)
    {
        int window = Math.Min(channel.Length, Math.Max(NrFftSize, sampleRate * 2));
        int hop = Math.Min(window, 4096);
        if (window <= 0) return noReading;

        double total = 0;
        foreach (float sample in channel) total += (double)sample * sample;
        double programme = Math.Sqrt(total / Math.Max(1, channel.Length));
        if (!(programme > 0)) return noReading;

        // The quietest window that is not digital silence.
        //
        // Silence is not a noise floor, it is the absence of a measurement, and taking it as one
        // reports an infinite ratio and switches the suppressor off on a file that may be full of
        // hiss - which an already-gated recording, or one trimmed to start on a clean edit, would
        // otherwise do.
        //
        // <b>A low percentile of the windows was measured here instead and is worse</b>, which is
        // worth stating because it is the more defensible-looking choice: the tenth percentile
        // window carries more music than the quietest one, so every reading compresses towards zero
        // - the six planted severities separate 8.7/8.5/7.8/6.4/4.3/2.0 against 15.9/14.8/12.7/9.5/
        // 5.9/2.7 - and the rule built on it protects far less, taking cells that come out worse
        // than doing nothing from 46 of 108 to 29 where the minimum takes them to 15.
        //
        // <b>What the minimum costs is a known limitation rather than a fixed one.</b> A window
        // lying across the edge of a spliced-in silence is almost all silence and a little
        // programme, so it passes the test above without being anywhere near the real floor, and
        // the estimate then reads high and reduces less than it should. That case is constructed;
        // the corpus is measured, and it decides.
        double floorEnergy = double.MaxValue;
        double rolling = 0;
        for (int i = 0; i < window; i++) rolling += (double)channel[i] * channel[i];
        double silenceEnergy = SilentWindowRms * SilentWindowRms * window;
        if (rolling > silenceEnergy) floorEnergy = rolling;

        int previous = 0;
        for (int start = hop; start + window <= channel.Length; start += hop)
        {
            for (int i = previous; i < start; i++) rolling -= (double)channel[i] * channel[i];
            for (int i = previous + window; i < start + window; i++)
                rolling += (double)channel[i] * channel[i];
            if (rolling > silenceEnergy && rolling < floorEnergy) floorEnergy = rolling;
            previous = start;
        }
        if (floorEnergy == double.MaxValue) return noReading;

        double floor = Math.Sqrt(Math.Max(floorEnergy, 0) / window);
        return floor > 0 ? 20 * Math.Log10(programme / floor) : noReading;
    }
    public const double NoiseDepthCeilingDb = 10.0;

    /// <summary>At or below this, the user's full requested depth is used.</summary>
    public const double NoiseDepthFloorDb = 0.0;

    /// <summary>
    /// Scales a requested reduction depth by how much noise there actually is to remove.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is downside protection, and the mean is not where its value is.</b> Measured over 108
    /// cells - 9 record transfers and 45 Windows Media files, six hiss severities - a fixed 10 dB
    /// scores <b>−0.85 dB segmental, which is worse than leaving the audio alone</b>, because a fixed
    /// depth applied to hiss already 30 dB under the programme costs more music than it saves noise:
    /// −8.13 dB at that severity against 0.00 for doing nothing. This rule scores <b>+0.04 dB fitted
    /// and −0.37 held out by recording, −0.40 held out by corpus</b> - so about half a decibel of
    /// mean. What it actually buys is the tail: <b>cells that come out worse than doing nothing fall
    /// from 46 of 108 to 15, and cells worse than −3 dB from 31 to 8</b>, with the worst cell going
    /// −21.13 to −17.26. It improves 52 cells and worsens 56, and still gains overall, because what
    /// it gives up is small and what it prevents is large.
    /// </para>
    /// <para>
    /// <b>The parameters sit on a plateau rather than a peak</b>, which is why they are trusted: the
    /// whole fitted surface spans about half a decibel, and every ceiling between 8 and 10 dB scores
    /// within 0.01 of the best. Held-out folds disagree about the argmax - 2 of 18 by recording, 0 of
    /// 2 by corpus - and that is near-ties moving under tiny corpus differences, not the parameter
    /// instability that killed five declip calibrations, where variants swung by tens of decibels.
    /// </para>
    /// <para>
    /// <b>What it cannot do is worth stating.</b> The best depth chosen per cell in hindsight scores
    /// +1.79 dB, so <b>+2.64 dB is the ceiling for any rule of this kind</b> and this one collects
    /// about a fifth of it. The oracle Wiener mask - a perfect per-bin estimator - reaches +7.15 dB
    /// over the better of the gate and doing nothing. Adapting the depth is the cheap part of that
    /// gap and not a substitute for estimating the mask better.
    /// </para>
    /// </remarks>
    public static double SuggestReductionDepthDb(float[][] data, int sampleRate, double requestedDb,
        double ceilingDb = NoiseDepthCeilingDb)
    {
        // Nothing asked for, so the measurement is not worth taking - it walks every sample.
        if (Math.Abs(requestedDb) <= 0) return 0;
        return SuggestReductionDepthDb(
            EstimateNoiseToProgrammeDb(data, sampleRate, ceilingDb), requestedDb, ceilingDb);
    }

    /// <summary>
    /// The same rule, applied to an estimate already taken.
    /// </summary>
    /// <remarks>
    /// <b>The split exists because the measurement is expensive and the rule is not.</b>
    /// <see cref="EstimateNoiseToProgrammeDb"/> walks every sample of every channel and then slides
    /// a two-second window over them: measured, <b>388 ms for a 22-minute stereo side</b>, 88 ms for
    /// five minutes. The rule itself is three comparisons. A caller that shows the depth on screen
    /// re-evaluates it on every movement of a slider and must not re-measure to do it - the
    /// restoration workbench did exactly that for one commit, which put a third of a second of
    /// whole-file scanning on the dispatcher per tick of any of its ten sliders.
    /// </remarks>
    public static double SuggestReductionDepthDb(double estimateDb, double requestedDb,
        double ceilingDb = NoiseDepthCeilingDb)
    {
        double requested = Math.Abs(requestedDb);
        if (requested <= 0) return 0;
        double ceiling = EffectiveCeilingDb(ceilingDb);
        if (double.IsNaN(estimateDb) || estimateDb >= ceiling) return 0;
        if (estimateDb <= NoiseDepthFloorDb) return requested;
        return requested * (ceiling - estimateDb) / (ceiling - NoiseDepthFloorDb);
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
    /// Butterworth section Qs for a 24 dB/octave high-pass pair. These are pole positions, not a
    /// tuning choice — the same two numbers <see cref="WaveLab.Audio.Effects.FilterEffect"/> cascades.
    /// </summary>
    internal static readonly double[] SubsonicSectionQs = [0.5412, 1.3066];

    /// <summary>
    /// Removes subsonic rumble: a 24 dB/octave Butterworth high-pass. A strength of zero is a
    /// bit-exact no-op; one applies the complete filter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On a record transfer this is not a trim, it is most of the noise.</b> Measured on the
    /// run-out of a real transfer, <b>48% of the total energy sits below 40 Hz</b>, peaking at 10.8
    /// and 16.1 Hz — a tonearm/cartridge resonance excited by warp, not anything the disc was cut
    /// with. Its ⅓-octave level is −48.6 dBFS at 25 Hz against −65 dBFS across the midrange, and a
    /// 25 Hz filter of this shape takes <b>8.1 dB</b> off the total while leaving the ⅓-octave bands
    /// from 100 Hz up unmoved.
    /// </para>
    /// <para>
    /// <b>What it does not do is worth stating, because the obvious claims are both false here.</b>
    /// It does not help the de-crackler: measured on the same material, high-pass then collapse then
    /// de-crackle leaves 15 ticks above −45 dBFS having repaired 4.2% of samples, and collapse then
    /// de-crackle then high-pass leaves 14 having repaired 4.4% — the same answer either way round.
    /// Nor does it rescue <c>AnalyzeClipping</c> from rumble-inflated plateaus: removing it moved
    /// that transfer's peak by 0.09 dB, −1.09 to −1.18 dBFS. It runs first so that every downstream
    /// <em>measurement</em> — the automatic noise profile, the per-block robust scales, the levels
    /// on the cards — is taken on the audible band rather than on the rumble. That is a reason about
    /// the readouts and not about the audio.
    /// </para>
    /// <para>
    /// The cascade is blended <b>once</b> rather than per section. Blending each section against its
    /// own input is a different filter at any strength below one, and not the one the cutoff names.
    /// The cutoff is passed to <see cref="Biquad"/> unclamped on purpose: <c>Biquad.Corner</c>
    /// already bounds it, and clamping a second time at the call site is what made
    /// <c>MonoToStereoEffect</c> diverge from the rest of the app.
    /// </para>
    /// </remarks>
    public static void RemoveSubsonic(float[][] data, int sampleRate, double cutoffHz,
        double strength = 1.0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        float amount = (float)Math.Clamp(strength, 0.0, 1.0);
        if (amount <= 0f) return;

        foreach (var channel in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (channel is not { Length: > 0 }) continue;

            // One pair per channel, declared here so no state ever crosses a channel boundary.
            var first = Biquad.HighPass(sampleRate, cutoffHz, SubsonicSectionQs[0]);
            var second = Biquad.HighPass(sampleRate, cutoffHz, SubsonicSectionQs[1]);
            for (int i = 0; i < channel.Length; i++)
            {
                if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                float dry = channel[i];
                float filtered = second.Process(first.Process(dry));
                channel[i] = amount >= 1f
                    ? filtered
                    : dry + (filtered - dry) * amount;
            }
        }
    }

    /// <summary>
    /// Scales the side (L−R) signal of a stereo pair. A level of one is a bit-exact no-op; zero
    /// collapses the pair to mono.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Surface noise on a record is vertical, and vertical is the side signal.</b> Measured on a
    /// real transfer's run-out, <b>78% of crackle events are anti-phase</b> — L·R below zero across
    /// the event — and 77% of them appear in both channels, so they are one defect in the difference
    /// signal rather than two defects in two channels. Across five transfers from one collection,
    /// side-to-mid rises from the programme to the quietest window by <b>11.0, 13.1, 16.0, 19.1 and
    /// 24.1 dB</b>: every one of them, so this is a property of records rather than of one record.
    /// </para>
    /// <para>
    /// <b>This is what makes the de-crackler work, and it is not a subtlety.</b> Run on the stereo
    /// file, the shipped de-crackler takes that run-out from 462 ticks above −45 dBFS to 408 — a 12%
    /// improvement for 5.2% of samples repaired, which is close to nothing. Collapse the side first
    /// and the same detector, unchanged, takes 88 to <b>15</b>. Each channel otherwise gets its own
    /// autoregressive model and its own robust threshold, so it sees a <em>different</em> realisation
    /// of the same vertical tick, repairs that, and summing the channels back reconstitutes what the
    /// other channel still holds.
    /// </para>
    /// <para>
    /// <b>How far the side may be pulled down is a property of the pressing, not of the noise</b>,
    /// and the two must not be confused. The rise above is present on stereo pressings too — the
    /// widest record in that set of five still showed +11.0 dB — so it says the noise is vertical and
    /// says nothing about whether music is there with it. That is the programme side-to-mid ratio's
    /// job, and it split the same five into −16.5/−15.2/−12.3 dB and −9.8/−6.0.
    /// </para>
    /// <para>
    /// The no-op at one is an early return rather than the arithmetic, because
    /// <c>mid + side</c> reconstructs a float sample to within a rounding error and not to the
    /// sample. Only the first two channels of a stereo pair mean anything here, so anything that is
    /// not a pair is left alone rather than guessed at.
    /// </para>
    /// </remarks>
    public static void ScaleSide(float[][] data, double sideLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length != 2) return;

        float level = (float)Math.Clamp(sideLevel, 0.0, 1.0);
        if (level >= 1f) return;

        float[] left = data[0], right = data[1];
        if (left is null || right is null) return;
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            float mid = (left[i] + right[i]) * 0.5f;
            float side = (left[i] - right[i]) * 0.5f * level;
            left[i] = mid + side;
            right[i] = mid - side;
        }
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

}