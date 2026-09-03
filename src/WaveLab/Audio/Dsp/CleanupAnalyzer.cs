using WaveLab.Audio.Effects;

namespace WaveLab.Audio.Dsp;

/// <summary>
/// Deterministic, bounded whole-program analysis for the two analog-transfer rack
/// profiles. It measures rather than processes the supplied channel arrays.
/// </summary>
public static class CleanupAnalyzer
{
    private sealed record GlobalStats(
        int Frames,
        double[] ChannelRms,
        double Peak,
        double NoiseFloorDb,
        double ActiveMedianDb,
        double DynamicRangeDb,
        double IntegratedLufs,
        double TruePeakDb,
        double StereoCorrelation,
        double SideToMidDb);

    private sealed record SpectralWindow(double[] Power, double RmsDb, double ZeroRatio);

    private sealed record SpectralStats(
        int FftSize,
        int WindowsAnalyzed,
        int QuietWindows,
        double[] ProgramPower,
        double[] NoisePower,
        double[] RumbleRatiosDb);

    private readonly record struct HumEstimate(
        double Frequency,
        int Harmonics,
        int HarmonicMask,
        double Q,
        double Amount,
        double AverageExcessDb,
        double Confidence,
        bool Detected);

    private readonly record struct RumbleEstimate(
        double RatioDb,
        double Persistence,
        double Cutoff,
        double Confidence,
        bool Detected);

    private readonly record struct NoiseEstimate(
        double ThresholdDb,
        double ReductionDb,
        double HissDb,
        double HissFrequency,
        double ReleaseMs,
        double HissExcessDb,
        double NoiseToProgrammeDb,
        bool BenefitWithheld,
        double Confidence,
        bool Detected);

    public static CleanupAnalysisResult Analyze(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        CleanupProfile profile,
        CancellationToken cancellationToken = default,
        IProgress<CleanupAnalysisProgress>? progress = null,
        double noiseDepthCeilingDb = Restoration.NoiseDepthCeilingDb)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInput(channels, sampleRate, out int frames);
        if (profile is not (CleanupProfile.VinylCleanup or CleanupProfile.CleanTransfer))
            throw new ArgumentOutOfRangeException(nameof(profile));

        string factoryName = profile == CleanupProfile.VinylCleanup
            ? "Vinyl Cleanup"
            : "Clean Transfer";
        EffectFactory.ChainPreset baseline = EffectFactory.CreateFactoryPreset(factoryName);
        EffectFactory.ChainPreset recommended = CleanupAnalysisResult.ClonePreset(
            baseline, $"{factoryName} · Analyzed");

        progress?.Report(new CleanupAnalysisProgress("Measuring levels and program dynamics…", 0.03));
        GlobalStats global = MeasureGlobal(channels, sampleRate, frames, cancellationToken);

        progress?.Report(new CleanupAnalysisProgress("Sampling representative spectra…", 0.24));
        SpectralStats spectral = MeasureSpectra(channels, sampleRate, frames, cancellationToken,
            fraction => progress?.Report(new CleanupAnalysisProgress(
                "Sampling representative spectra…", 0.24 + fraction * 0.45)));

        progress?.Report(new CleanupAnalysisProgress("Identifying rumble, hum, and surface noise…", 0.72));
        HumEstimate hum = EstimateHum(spectral, channels, sampleRate, cancellationToken);
        RumbleEstimate rumble = EstimateRumble(spectral, sampleRate);
        NoiseEstimate noise = EstimateNoise(global, spectral, sampleRate);
        // The rack and restoration workbench must make the same minimum-benefit decision. The
        // spectral evidence alone can nominate a quiet passage that is actually music; compare it
        // with the whole programme before either UI offers reduction. This also moves the costly
        // scan off the workbench dispatcher and lets both consumers reuse one result.
        float[][] noiseChannels = channels as float[][] ?? channels.ToArray();
        double noiseToProgrammeDb = Restoration.EstimateNoiseToProgrammeDb(
            noiseChannels, sampleRate, noiseDepthCeilingDb, cancellationToken);
        if (noise.Detected && Restoration.SuggestReductionDepthDb(
                noiseToProgrammeDb, noise.ReductionDb, noiseDepthCeilingDb) <= 0)
        {
            noise = noise with
            {
                NoiseToProgrammeDb = noiseToProgrammeDb,
                BenefitWithheld = true,
                Confidence = Math.Min(noise.Confidence, 0.54),
                Detected = false,
            };
        }
        else
        {
            noise = noise with { NoiseToProgrammeDb = noiseToProgrammeDb };
        }

        var metrics = new List<CleanupMetric>();
        var recommendations = new List<CleanupRecommendation>();
        if (profile == CleanupProfile.VinylCleanup)
        {
            BuildVinylRecommendations(baseline, recommended, global, spectral, rumble, hum, noise,
                sampleRate, metrics, recommendations);
        }
        else
        {
            BuildCleanTransferRecommendations(channels, baseline, recommended, global, hum, noise,
                sampleRate, cancellationToken, metrics, recommendations);
        }

        progress?.Report(new CleanupAnalysisProgress("Checking conservative parameter limits…", 0.94));
        NormalizePreset(recommended);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CleanupAnalysisProgress("Analysis complete.", 1));

        return new CleanupAnalysisResult
        {
            Profile = profile,
            Metrics = metrics,
            Recommendations = recommendations,
            BaselinePreset = CleanupAnalysisResult.ClonePreset(baseline),
            RecommendedPreset = recommended,
            WindowsAnalyzed = spectral.WindowsAnalyzed,
            SideToMidDb = global.SideToMidDb,
            NoiseToProgrammeDb = noiseToProgrammeDb,
        };
    }

    private static void BuildVinylRecommendations(
        EffectFactory.ChainPreset baseline,
        EffectFactory.ChainPreset recommended,
        GlobalStats global,
        SpectralStats spectral,
        RumbleEstimate rumble,
        HumEstimate hum,
        NoiseEstimate noise,
        int sampleRate,
        List<CleanupMetric> metrics,
        List<CleanupRecommendation> recommendations)
    {
        EffectFactory.EffectState hpf = State(recommended, "filter");
        hpf.Enabled = rumble.Detected;
        Set(hpf, "mode", 1.0); // HP mode
        Set(hpf, "cutoff", rumble.Cutoff);
        Set(hpf, "q", 0.707);
        Set(hpf, "slope", 1.0); // fourth-order magnitude
        Set(hpf, "phase", 1.0); // shared restoration kernel; offline latency is compensated
        recommendations.Add(Recommendation(
            baseline, hpf, "High-Pass Filter",
            rumble.Detected
                ? $"Subsonic energy is {rumble.RatioDb:+0.0;-0.0;0.0} dB relative to bass fundamentals in the typical passage ({rumble.Persistence:P0} persistent)."
                : $"Subsonic energy was not persistent enough to separate from musical bass ({rumble.Persistence:P0} of passages).",
            $"{Param(State(baseline, "filter"), "cutoff"):0} Hz",
            rumble.Detected ? $"{rumble.Cutoff:0} Hz · Q 0.71" : "Bypass",
            rumble.Confidence, true));

        EffectFactory.EffectState dehum = State(recommended, "dehum");
        ApplyHum(dehum, hum);
        recommendations.Add(Recommendation(
            baseline, dehum, "Hum Removal",
            hum.Detected
                ? $"A stable {hum.Frequency:0.0} Hz harmonic family rises {hum.AverageExcessDb:0.0} dB above its local floor."
                : "No persistent 45–65 Hz harmonic family was found; unnecessary notches are bypassed.",
            HumText(State(baseline, "dehum")),
            hum.Detected ? HumText(dehum) : "Bypass",
            hum.Confidence, true));

        EffectFactory.EffectState denoise = State(recommended, "denoise");
        ApplyNoise(denoise, noise);
        recommendations.Add(Recommendation(
            baseline, denoise, "Noise & Hiss Reduction",
            NoiseEvidence(noise, global,
                $"Stationary quiet passages support a {global.NoiseFloorDb:0.0} dBFS floor and {noise.HissExcessDb:+0.0;-0.0;0.0} dB high-band excess.",
                "The file does not contain enough distinct, stationary quiet audio for safe automatic reduction."),
            NoiseText(State(baseline, "denoise")),
            noise.Detected ? NoiseText(denoise) : "Bypass",
            noise.Confidence, true));

        EffectFactory.EffectState eq = State(recommended, "eq");
        double lowBand = BandDb(spectral.ProgramPower, 70, 240, sampleRate, spectral.FftSize);
        double midBand = BandDb(spectral.ProgramPower, 450, 2200, sampleRate, spectral.FftSize);
        double highEnd = Math.Min(12000, sampleRate * 0.45);
        bool hasHighBand = highEnd > 6000;
        double highBand = hasHighBand
            ? BandDb(spectral.ProgramPower, 6000, highEnd, sampleRate, spectral.FftSize)
            : double.NegativeInfinity;
        double highNoise = hasHighBand
            ? BandDb(spectral.NoisePower, 6000, highEnd, sampleRate, spectral.FftSize)
            : double.NegativeInfinity;
        double lowGain = Quantize(Math.Clamp(((midBand + 3) - lowBand) * 0.18, -2, 2), 0.25);
        double highGain = hasHighBand && double.IsFinite(highBand)
            ? Quantize(Math.Clamp(((midBand - 6) - highBand) * 0.18, -1.5, 1.5), 0.25)
            : 0;
        if (!double.IsFinite(highNoise) || highBand - highNoise < 18 && highGain > 0) highGain = 0;
        double eqConfidence = spectral.WindowsAnalyzed >= 4 && double.IsFinite(lowBand) &&
                              double.IsFinite(midBand) ? 0.68 : 0.42;
        bool eqSupported = eqConfidence >= 0.65;
        if (!eqSupported)
        {
            lowGain = 0;
            highGain = 0;
        }
        Set(eq, "lowGain", lowGain);
        Set(eq, "midGain", 0);
        Set(eq, "highGain", highGain);
        eq.Enabled = eqSupported;
        recommendations.Add(Recommendation(
            baseline, eq, "Studio EQ",
            "Only broad transfer tilt is corrected; the analyzer does not try to redesign the recording's artistic balance.",
            EqText(State(baseline, "eq")), eqSupported ? EqText(eq) : "Bypass", eqConfidence, true));

        EffectFactory.EffectState limiter = State(recommended, "limiter");
        ConfigureLimiter(limiter, global, targetLufs: -18, maximumDriveDb: 4);
        limiter.Enabled = false;
        double limiterConfidence = global.Frames >= sampleRate * 3 ? 0.9 : 0.45;
        recommendations.Add(Recommendation(
            baseline, limiter, "Precision Limiter",
            $"Measured {FormatLufs(global.IntegratedLufs)} with {FormatDb(global.TruePeakDb, "dBTP")} true peak. Loudness and limiting belong to the delivery stage after restoration.",
            LimiterText(State(baseline, "limiter")), "Deferred to Normalize Loudness",
            limiterConfidence, false));

        metrics.Add(new CleanupMetric("LOW-FREQUENCY RUMBLE",
            rumble.Detected ? $"Strong below {rumble.Cutoff:0} Hz" : "No distinct rumble",
            rumble.Detected ? "Separated from sustained musical bass" : "High-pass processing can remain bypassed"));
        metrics.Add(new CleanupMetric("MAINS HUM",
            hum.Detected ? $"{hum.Frequency:0.0} Hz + {hum.Harmonics - 1} harmonics" : "No stable hum family",
            hum.Detected ? "Persistent narrow spectral peaks" : "No safe automatic notch target"));
        metrics.Add(new CleanupMetric("SURFACE NOISE FLOOR",
            double.IsFinite(global.NoiseFloorDb) ? $"{global.NoiseFloorDb:0.0} dBFS" : "Not measurable",
            noise.Detected ? $"{spectral.QuietWindows} stationary quiet passages" : "Insufficient clean quiet material"));
        metrics.Add(new CleanupMetric("HIGH-FREQUENCY HISS",
            noise.Detected ? $"Above {noise.HissFrequency / 1000:0.0} kHz" : "No confident setting",
            noise.Detected ? $"{noise.HissExcessDb:+0.0;-0.0;0.0} dB relative high-band energy" : "Reduction remains bypassed"));
    }

    private static void BuildCleanTransferRecommendations(
        IReadOnlyList<float[]> channels,
        EffectFactory.ChainPreset baseline,
        EffectFactory.ChainPreset recommended,
        GlobalStats global,
        HumEstimate hum,
        NoiseEstimate noise,
        int sampleRate,
        CancellationToken cancellationToken,
        List<CleanupMetric> metrics,
        List<CleanupRecommendation> recommendations)
    {
        EffectFactory.EffectState balance = State(recommended, "channel-balance");
        double balanceDb = 0;
        double balanceConfidence = 0;
        double alignmentMs = 0;
        double alignmentConfidence = 0;
        if (global.ChannelRms.Length >= 2)
        {
            balanceDb = Math.Clamp(20 * Math.Log10(
                Math.Max(1e-9, global.ChannelRms[0]) / Math.Max(1e-9, global.ChannelRms[1])), -12, 12);
            balanceConfidence = Ramp(Math.Abs(balanceDb), 0.5, 6);
            (alignmentMs, alignmentConfidence) = EstimateAlignment(channels[0], channels[1], sampleRate,
                cancellationToken);
        }
        double channelConfidence = Math.Max(balanceConfidence, alignmentConfidence);
        balance.Enabled = global.ChannelRms.Length >= 2 && channelConfidence >= 0.55;
        Set(balance, "balance", balanceDb);
        Set(balance, "align", alignmentMs);
        recommendations.Add(Recommendation(
            baseline, balance, "Channel Balance & Alignment",
            balance.Enabled
                ? $"Stereo RMS differs by {Math.Abs(balanceDb):0.0} dB; correlation is {global.StereoCorrelation:+0.00;-0.00;0.00}."
                : "No confident stereo level or timing mismatch was found.",
            ChannelText(State(baseline, "channel-balance")),
            balance.Enabled ? ChannelText(balance) : "Bypass",
            channelConfidence, true));

        EffectFactory.EffectState dehum = State(recommended, "dehum");
        ApplyHum(dehum, hum);
        recommendations.Add(Recommendation(
            baseline, dehum, "Hum Removal",
            hum.Detected
                ? $"A stable {hum.Frequency:0.0} Hz family is shared across the transfer."
                : "No persistent mains family was found; the notch bank is bypassed.",
            HumText(State(baseline, "dehum")), hum.Detected ? HumText(dehum) : "Bypass",
            hum.Confidence, true));

        EffectFactory.EffectState denoise = State(recommended, "denoise");
        ApplyNoise(denoise, noise with
        {
            ReductionDb = Math.Min(noise.ReductionDb, 8),
            HissDb = Math.Min(noise.HissDb, 6),
        });
        recommendations.Add(Recommendation(
            baseline, denoise, "Noise & Hiss Reduction",
            NoiseEvidence(noise, global,
                "Quiet/active separation supports restrained transfer cleanup without lifting the noise floor.",
                "No reliable quiet/active separation was found, so automatic reduction is bypassed."),
            NoiseText(State(baseline, "denoise")), denoise.Enabled ? NoiseText(denoise) : "Bypass",
            noise.Confidence, true));

        EffectFactory.EffectState normalizer = State(recommended, "normalizer");
        bool levelAvailable = double.IsFinite(global.IntegratedLufs) && global.Frames >= sampleRate;
        double target = -20;
        double boost = levelAvailable ? Math.Clamp(target - global.IntegratedLufs, 0, 6) : 0;
        double cut = levelAvailable ? Math.Clamp(global.IntegratedLufs - target, 0, 8) : 0;
        double gate = double.IsFinite(global.NoiseFloorDb)
            ? Math.Clamp(global.NoiseFloorDb + 3, -70, -45)
            : -58;
        normalizer.Enabled = levelAvailable;
        Set(normalizer, "target", target);
        Set(normalizer, "maxBoost", Quantize(boost, 0.5));
        Set(normalizer, "maxCut", Quantize(cut, 0.5));
        Set(normalizer, "gate", Math.Round(gate));
        Set(normalizer, "response", global.DynamicRangeDb > 14 ? 3000 : 2200);
        double normalizerConfidence = levelAvailable ? Math.Clamp(0.65 + global.Frames / (sampleRate * 60.0) * 0.2, 0, 0.9) : 0.3;
        recommendations.Add(Recommendation(
            baseline, normalizer, "Level Normalizer",
            levelAvailable
                ? $"Program loudness is {global.IntegratedLufs:0.0} LUFS; gain is bounded around a conservative −20 dBFS RMS transfer target."
                : "The range is too short for a reliable program-level recommendation.",
            NormalizerText(State(baseline, "normalizer")),
            levelAvailable ? NormalizerText(normalizer) : "Bypass",
            normalizerConfidence, true));

        EffectFactory.EffectState trim = State(recommended, "trim");
        double trimDb = double.IsFinite(global.TruePeakDb)
            ? Quantize(Math.Clamp(-1 - global.TruePeakDb, -6, 0), 0.25)
            : 0;
        Set(trim, "gain", trimDb);
        trim.Enabled = Math.Abs(trimDb) >= 0.125;
        double trimConfidence = double.IsFinite(global.TruePeakDb) ? 0.86 : 0.3;
        recommendations.Add(Recommendation(
            baseline, trim, "Gain & Trim",
            trim.Enabled
                ? "Input trim protects headroom before the slow level controller and limiter."
                : "No additional input trim is needed before leveling.",
            TrimText(State(baseline, "trim")), trim.Enabled ? TrimText(trim) : "Bypass",
            trimConfidence, true));

        EffectFactory.EffectState limiter = State(recommended, "limiter");
        ConfigureLimiter(limiter, global, targetLufs: -20, maximumDriveDb: 3);
        double limiterConfidence = global.Frames >= sampleRate * 3 ? 0.9 : 0.45;
        recommendations.Add(Recommendation(
            baseline, limiter, "Precision Limiter",
            "The final ceiling remains −1.0 dBTP and added drive is limited to preserve transfer dynamics.",
            LimiterText(State(baseline, "limiter")), LimiterText(limiter),
            limiterConfidence, true));

        metrics.Add(new CleanupMetric("CHANNEL BALANCE",
            global.ChannelRms.Length < 2 ? "Mono source" : $"{balanceDb:+0.0;-0.0;0.0} dB correction",
            balance.Enabled ? $"Timing recommendation {alignmentMs:+0.00;-0.00;0.00} ms" : "No confident stereo mismatch"));
        metrics.Add(new CleanupMetric("MAINS HUM",
            hum.Detected ? $"{hum.Frequency:0.0} Hz family" : "No stable hum family",
            hum.Detected ? $"{hum.Harmonics} narrow notches recommended" : "Hum processing can remain bypassed"));
        metrics.Add(new CleanupMetric("BACKGROUND NOISE",
            double.IsFinite(global.NoiseFloorDb) ? $"{global.NoiseFloorDb:0.0} dBFS" : "Not measurable",
            noise.Detected ? $"{noise.ReductionDb:0.0} dB maximum reduction" : "No safe automatic reduction"));
        metrics.Add(new CleanupMetric("PROGRAM LEVEL",
            FormatLufs(global.IntegratedLufs),
            $"True peak {FormatDb(global.TruePeakDb, "dBTP")} · range {global.DynamicRangeDb:0.0} dB"));
    }

    private static GlobalStats MeasureGlobal(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        int frames,
        CancellationToken cancellationToken)
    {
        int channelCount = channels.Count;
        var sumSquares = new double[channelCount];
        double cross = 0;
        double peak = 0;
        var blockLevels = new List<double>();
        int blockFrames = Math.Max(1, sampleRate / 20);
        const int meterBlock = 16384;
        var interleaved = new float[Math.Max(1, meterBlock * channelCount)];
        var meter = new LoudnessMeter();
        meter.Configure(sampleRate, channelCount);

        for (int start = 0; start < frames; start += meterBlock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(meterBlock, frames - start);
            for (int frame = 0; frame < count; frame++)
            {
                for (int channel = 0; channel < channelCount; channel++)
                {
                    float value = Finite(channels[channel][start + frame]);
                    interleaved[frame * channelCount + channel] = value;
                    sumSquares[channel] += value * value;
                    peak = Math.Max(peak, Math.Abs(value));
                }
                if (channelCount >= 2)
                    cross += Finite(channels[0][start + frame]) * Finite(channels[1][start + frame]);
            }
            meter.Process(interleaved, 0, count * channelCount);
        }
        meter.FlushTruePeak();

        // Mid and side energy is accumulated alongside the block level, per block, so that the
        // side-to-mid ratio can be taken over the programme rather than over the whole file. Over
        // the whole file it is dominated by whichever is longer, the music or the run-out, and on a
        // record those two answer opposite questions.
        var blockMidSide = new List<(double Level, double Mid, double Side)>();

        for (int start = 0; start < frames; start += blockFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(frames, start + blockFrames);
            double power = 0;
            double midPower = 0, sidePower = 0;
            int zeros = 0;
            int values = Math.Max(1, (end - start) * channelCount);
            for (int frame = start; frame < end; frame++)
            {
                for (int channel = 0; channel < channelCount; channel++)
                {
                    float value = Finite(channels[channel][frame]);
                    power += value * value;
                    if (value == 0) zeros++;
                }
                if (channelCount >= 2)
                {
                    double mid = (Finite(channels[0][frame]) + Finite(channels[1][frame])) * 0.5;
                    double side = (Finite(channels[0][frame]) - Finite(channels[1][frame])) * 0.5;
                    midPower += mid * mid;
                    sidePower += side * side;
                }
            }
            double rmsDb = 10 * Math.Log10(Math.Max(1e-12, power / values));
            if (rmsDb > -100 && zeros < values * 0.95)
            {
                blockLevels.Add(rmsDb);
                if (channelCount >= 2) blockMidSide.Add((rmsDb, midPower, sidePower));
            }
        }

        blockLevels.Sort();
        double noiseFloor = blockLevels.Count > 0 ? PercentileSorted(blockLevels, 0.10) : double.NegativeInfinity;
        double activeMedian = blockLevels.Count > 0 ? PercentileSorted(blockLevels, 0.60) : double.NegativeInfinity;
        double programmeFloor = blockLevels.Count > 0 ? PercentileSorted(blockLevels, 0.30) : double.NegativeInfinity;
        double dynamic = double.IsFinite(noiseFloor) && double.IsFinite(activeMedian)
            ? Math.Max(0, activeMedian - noiseFloor)
            : 0;
        var rms = sumSquares.Select(sum => Math.Sqrt(sum / Math.Max(1, frames))).ToArray();
        double correlation = channelCount >= 2
            ? cross / Math.Max(1e-12, Math.Sqrt(sumSquares[0] * sumSquares[1]))
            : 1;
        return new GlobalStats(frames, rms, peak, noiseFloor, activeMedian, dynamic,
            meter.IntegratedLufs, meter.TruePeakDb, Math.Clamp(correlation, -1, 1),
            SideToMidDb(blockMidSide, programmeFloor));
    }

    /// <summary>
    /// How far the side signal sits under the mid over the programme, in dB. Positive means the
    /// difference signal is the louder of the two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This measures the pressing, not the noise, and the distinction is the whole point.</b> A
    /// record's surface noise is vertical whatever was cut into it — measured across five transfers,
    /// side-to-mid rose from the programme to the quietest window by 11.0, 13.1, 16.0, 19.1 and 24.1
    /// dB, every one of them, including the widest stereo record in the set. So that rise says the
    /// noise is in the side signal and says nothing about whether music is in there with it. Only
    /// the programme ratio answers that, and it split the same five cleanly: −16.5, −15.2 and −12.3
    /// dB where the disc was cut mono, −9.8 and −6.0 where it was not.
    /// </para>
    /// <para>
    /// The gate is the 30th percentile of block level, so the ratio is taken over the lower three
    /// quarters of the programme rather than only its loudest two fifths. Widening the gate stops a
    /// loud mono-dominant passage (centred vocal, bass) from dominating the measurement — quieter
    /// passages that carry real stereo information still contribute, and the run-out and noise floor
    /// are excluded the same way they always were. The original 60th-percentile gate was right about
    /// excluding the run-out but could read a naturally narrow loud passage as a mono pressing.
    /// </para>
    /// </remarks>
    private static double SideToMidDb(
        IReadOnlyList<(double Level, double Mid, double Side)> blocks, double activeMedianDb)
    {
        double mid = 0, side = 0;
        foreach (var block in blocks)
        {
            if (block.Level < activeMedianDb) continue;
            mid += block.Mid;
            side += block.Side;
        }
        // Nothing cleared the gate, or the file is silent: no reading rather than a wrong one.
        // Zero is the neutral answer, and it recommends leaving the side alone.
        if (mid <= 0) return 0;
        return 10 * Math.Log10(Math.Max(1e-20, side) / mid);
    }

    private static SpectralStats MeasureSpectra(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        int frames,
        CancellationToken cancellationToken,
        Action<double> report)
    {
        int maximum = Math.Min(32768, frames);
        int fftSize = HighestPowerOfTwo(maximum);
        if (fftSize < 512)
            return new SpectralStats(Math.Max(1, fftSize), 0, 0, [], [], []);

        int windowCount = frames == fftSize ? 1 : Math.Min(24, Math.Max(2, frames / Math.Max(1, fftSize / 2)));
        var starts = new List<int>(windowCount);
        for (int i = 0; i < windowCount; i++)
        {
            int start = windowCount == 1 ? 0 : (int)Math.Round(i * (frames - fftSize) / (double)(windowCount - 1));
            if (starts.Count == 0 || starts[^1] != start) starts.Add(start);
        }

        float[] window = Fft.HannWindow(fftSize);
        double windowSum = window.Sum(value => (double)value);
        double normalization = 2.0 / Math.Max(1e-12, windowSum);
        var measured = new List<SpectralWindow>(starts.Count);

        // Hoisted out of both loops, and a real transform rather than a complex one on
        // real input: RealForward does half the work and returns exactly the bins read
        // below. Allocating the pair per channel per window pushed tens of megabytes
        // through the heap for an analysis that reads the same shape every time.
        var windowed = new float[fftSize];
        var binRe = new float[fftSize / 2 + 1];
        var binIm = new float[fftSize / 2 + 1];

        for (int windowIndex = 0; windowIndex < starts.Count; windowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = starts[windowIndex];
            var power = new double[fftSize / 2];
            double sumSquares = 0;
            int zeros = 0;
            foreach (float[] channel in channels)
            {
                for (int i = 0; i < fftSize; i++)
                {
                    float sample = Finite(channel[start + i]);
                    windowed[i] = sample * window[i];
                    sumSquares += sample * sample;
                    if (sample == 0) zeros++;
                }
                Fft.RealForward(windowed, binRe, binIm);
                for (int bin = 0; bin < power.Length; bin++)
                {
                    double magnitude = Math.Sqrt(binRe[bin] * binRe[bin] + binIm[bin] * binIm[bin]) * normalization;
                    power[bin] += magnitude * magnitude / channels.Count;
                }
            }
            double rmsDb = 10 * Math.Log10(Math.Max(1e-12,
                sumSquares / Math.Max(1, fftSize * channels.Count)));
            measured.Add(new SpectralWindow(power, rmsDb,
                zeros / (double)Math.Max(1, fftSize * channels.Count)));
            report((windowIndex + 1.0) / starts.Count);
        }

        var valid = measured.Where(item => item.RmsDb > -100 && item.ZeroRatio < 0.95).ToList();
        if (valid.Count == 0)
            return new SpectralStats(fftSize, 0, 0, [], [], []);
        var quiet = valid.OrderBy(item => item.RmsDb).Take(Math.Min(8, valid.Count)).ToList();
        double[] program = AveragePower(valid, fftSize / 2);
        double[] noise = AveragePower(quiet, fftSize / 2);
        double[] rumbleRatios = valid
            .Select(item =>
            {
                double subsonic = BandEnergyDb(item.Power, 5, 25, sampleRate, fftSize);
                double bass = BandEnergyDb(item.Power, 40, 120, sampleRate, fftSize);
                // If both bands hit the numerical floor their ratio is spuriously 0 dB, which is
                // maximum-confidence "rumble" in a file containing only midrange. Keep the window
                // in the persistence denominator, but give absent subsonic energy a safe floor.
                return subsonic <= -90 ? -200 : subsonic - bass;
            })
            .Order()
            .ToArray();
        return new SpectralStats(fftSize, measured.Count, quiet.Count, program, noise,
            rumbleRatios);
    }

    private static HumEstimate EstimateHum(
        SpectralStats spectral,
        IReadOnlyList<float[]> channels,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        if (spectral.ProgramPower.Length == 0)
            return new HumEstimate(60, 1, 1, 40, 0, 0, 0, false);

        var candidates = new List<(double Frequency, double Score, int Supported,
            int HighestHarmonic, int HarmonicMask, double Excess)>();
        double bestFrequency = 60;
        double bestScore = double.NegativeInfinity;
        int bestSupported = 0;
        int bestHighestHarmonic = 1;
        int bestHarmonicMask = 0;
        double bestExcess = 0;
        for (double frequency = 45; frequency <= 65.0001; frequency += 0.25)
        {
            double score = 0;
            int supported = 0;
            int highestHarmonic = 1;
            int harmonicMask = 0;
            double excessSum = 0;
            for (int harmonic = 1; harmonic <= 8; harmonic++)
            {
                double center = frequency * harmonic;
                if (center >= Math.Min(1200, sampleRate * 0.45)) break;
                (double excess, double lineDb) = LineExcess(spectral.ProgramPower, center, sampleRate, spectral.FftSize);
                if (excess >= 5.5 && lineDb > -85)
                {
                    supported++;
                    highestHarmonic = harmonic;
                    harmonicMask |= 1 << (harmonic - 1);
                    excessSum += excess;
                    score += Math.Max(0, excess - 3) / Math.Sqrt(harmonic);
                }
            }
            double averageExcess = supported > 0 ? excessSum / supported : 0;
            candidates.Add((frequency, score, supported, highestHarmonic, harmonicMask, averageExcess));
            if (score > bestScore)
            {
                bestScore = score;
                bestFrequency = frequency;
                bestSupported = supported;
                bestHighestHarmonic = highestHarmonic;
                bestHarmonicMask = harmonicMask;
                bestExcess = averageExcess;
            }
        }

        double runnerUpScore = candidates
            .Where(candidate => Math.Abs(candidate.Frequency - bestFrequency) >= 2.5)
            .Select(candidate => candidate.Score)
            .DefaultIfEmpty(double.NegativeInfinity)
            .Max();
        double separation = double.IsFinite(runnerUpScore) ? bestScore - runnerUpScore : bestScore;
        double separationConfidence = Ramp(separation, 0.5, 8);
        bool hasLowOrderEvidence = (bestHarmonicMask & 0b11) != 0;
        double persistence = bestSupported >= 2 && hasLowOrderEvidence
            ? MeasureHumPersistence(channels, sampleRate, bestFrequency, bestHarmonicMask,
                cancellationToken)
            : 0;

        bool detected = bestSupported >= 2 && bestScore > 4 && hasLowOrderEvidence &&
                        persistence >= 0.68 && separation >= 0.5;
        double confidence = detected
            ? Math.Clamp(0.16 + bestSupported * 0.07 + Ramp(bestExcess, 6, 18) * 0.27 +
                         persistence * 0.28 + separationConfidence * 0.10, 0, 0.98)
            : Math.Clamp(Ramp(bestExcess, 8, 20) * 0.20 + persistence * 0.24 +
                         separationConfidence * 0.10, 0, 0.54);
        double q = Math.Clamp(28 + bestExcess * 1.35, 25, 65);
        double amount = detected ? Quantize(Math.Clamp((bestExcess - 3) / 20, 0.35, 0.9), 0.05) : 0;
        return new HumEstimate(Quantize(bestFrequency, 0.1), Math.Clamp(bestHighestHarmonic, 1, 8),
            bestHarmonicMask, Math.Round(q), amount, bestExcess, confidence, detected);
    }

    /// <summary>
    /// Validate a candidate on short windows. A musical bass note may have the same
    /// harmonic spacing as mains hum, but it will not remain as a narrow low-order
    /// family through most of the transfer.
    /// </summary>
    private static double MeasureHumPersistence(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        double frequency,
        int harmonicMask,
        CancellationToken cancellationToken)
    {
        int frames = channels.Count == 0 ? 0 : channels[0].Length;
        int blockFrames = Math.Clamp(sampleRate / 4, 2048, 16384);
        if (frames < blockFrames) return 0;

        int blockCount = Math.Min(40, Math.Max(1, frames / blockFrames));
        float[] window = Fft.HannWindow(blockFrames);
        int persistentBlocks = 0;
        for (int block = 0; block < blockCount; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = blockCount == 1
                ? 0
                : (int)Math.Round(block * (frames - blockFrames) / (double)(blockCount - 1));
            int supported = 0;
            bool lowOrderSupported = false;
            for (int harmonic = 1; harmonic <= 8; harmonic++)
            {
                if ((harmonicMask & (1 << (harmonic - 1))) == 0) continue;
                double center = frequency * harmonic;
                if (center + 12 >= sampleRate * 0.48) continue;

                double linePower = TonePower(channels, start, window, center, sampleRate);
                double lowerPower = TonePower(channels, start, window, Math.Max(5, center - 12), sampleRate);
                double upperPower = TonePower(channels, start, window, center + 12, sampleRate);
                double floorPower = (lowerPower + upperPower) * 0.5;
                double excess = 10 * Math.Log10(Math.Max(1e-18, linePower) /
                                                 Math.Max(1e-18, floorPower));
                double lineDb = 10 * Math.Log10(Math.Max(1e-18, linePower));
                if (excess < 5.5 || lineDb <= -85) continue;
                supported++;
                if (harmonic <= 2) lowOrderSupported = true;
            }

            if (supported >= 2 && lowOrderSupported) persistentBlocks++;
        }
        return persistentBlocks / (double)blockCount;
    }

    private static double TonePower(
        IReadOnlyList<float[]> channels,
        int start,
        IReadOnlyList<float> window,
        double frequency,
        int sampleRate)
    {
        double step = 2 * Math.PI * frequency / sampleRate;
        double cosStep = Math.Cos(step);
        double sinStep = Math.Sin(step);
        double windowSum = 0;
        for (int i = 0; i < window.Count; i++) windowSum += window[i];
        double normalization = 2 / Math.Max(1e-12, windowSum);
        double combinedPower = 0;
        foreach (float[] channel in channels)
        {
            double oscillatorReal = 1;
            double oscillatorImaginary = 0;
            double real = 0;
            double imaginary = 0;
            for (int i = 0; i < window.Count; i++)
            {
                double sample = Finite(channel[start + i]) * window[i];
                real += sample * oscillatorReal;
                imaginary -= sample * oscillatorImaginary;
                double nextReal = oscillatorReal * cosStep - oscillatorImaginary * sinStep;
                oscillatorImaginary = oscillatorImaginary * cosStep + oscillatorReal * sinStep;
                oscillatorReal = nextReal;
            }
            combinedPower += (real * real + imaginary * imaginary) *
                             normalization * normalization;
        }
        return combinedPower / Math.Max(1, channels.Count);
    }

    private static RumbleEstimate EstimateRumble(SpectralStats spectral, int sampleRate)
    {
        if (spectral.RumbleRatiosDb.Length == 0)
            return new RumbleEstimate(double.NegativeInfinity, 0, 28, 0, false);

        // A ratio of averages lets one loud low-frequency event dominate the entire analysis and
        // then calls it "persistent". Use the median window instead, and separately require that
        // the same evidence clears the detection boundary in at least half of the passages.
        double[] ratios = spectral.RumbleRatiosDb;
        int middle = ratios.Length / 2;
        double ratio = (ratios.Length & 1) == 0
            ? (ratios[middle - 1] + ratios[middle]) * 0.5
            : ratios[middle];
        double confidence = Ramp(ratio, -20, -3);
        double detectionRatio = -20 + 0.55 * 17;
        double persistence = ratios.Count(value => value >= detectionRatio) / (double)ratios.Length;
        bool detected = confidence >= 0.55 && persistence >= 0.5;
        if (!detected) confidence = Math.Min(confidence, 0.54);
        double cutoff = detected
            ? Quantize(Math.Clamp(28 + Ramp(ratio, -12, 6) * 12, 28, 40), 1)
            : 28;
        return new RumbleEstimate(ratio, persistence, cutoff, confidence, detected);
    }

    private static NoiseEstimate EstimateNoise(GlobalStats global, SpectralStats spectral, int sampleRate)
    {
        double quietCoverage = Ramp(spectral.QuietWindows, 2, 8);
        double separation = Ramp(global.DynamicRangeDb, 5, 18);
        double floorEvidence = double.IsFinite(global.NoiseFloorDb)
            ? Ramp(global.NoiseFloorDb, -90, -55)
            : 0;
        double confidence = Math.Clamp(0.45 * separation + 0.35 * quietCoverage + 0.20 * floorEvidence, 0, 0.96);
        double threshold = double.IsFinite(global.NoiseFloorDb)
            ? Math.Clamp(Math.Round(global.NoiseFloorDb + 8), -78, -45)
            : -60;
        double high = BandDb(spectral.NoisePower, 6000,
            Math.Min(12000, sampleRate * 0.45), sampleRate, spectral.FftSize);
        double mid = BandDb(spectral.NoisePower, 1500,
            Math.Min(3000, sampleRate * 0.40), sampleRate, spectral.FftSize);
        double hissExcess = double.IsFinite(high) && double.IsFinite(mid) ? high - mid : 0;
        // Dynamic range tells us whether the quiet windows are representative; it
        // must not reduce the requested cleanup when that separation is strong.
        // Derive reduction depth from the measured floor and high-band excess,
        // while keeping the separation term in the confidence/detection gates.
        double reduction = Quantize(Math.Clamp(
            1.5 + floorEvidence * 6.5 + Math.Max(0, hissExcess) * 0.25,
            0, 12), 0.5);
        double hiss = Quantize(Math.Clamp(2 + Math.Max(0, hissExcess) * 0.55 +
                                                Math.Max(0, 12 - global.DynamicRangeDb) * 0.20,
            0, Math.Min(10, Math.Max(0, 14 - reduction))), 0.5);
        double hissUpper = Math.Clamp(sampleRate * 0.42, 3000, 10000);
        double hissLower = Math.Min(4500, hissUpper);
        double hissFrequency = Math.Clamp(
            Quantize(Math.Clamp(6500 - hissExcess * 250, hissLower, hissUpper), 250),
            hissLower,
            hissUpper);
        if (!double.IsFinite(hissFrequency) || hissFrequency < 3000)
            hissFrequency = Math.Min(5500, sampleRate * 0.4);
        double release = Quantize(Math.Clamp(300 + global.DynamicRangeDb * 8, 250, 600), 25);
        bool hasQuietActiveSeparation = global.DynamicRangeDb >= 7 && spectral.QuietWindows >= 2;
        bool detected = confidence >= 0.55 && hasQuietActiveSeparation &&
                        double.IsFinite(global.NoiseFloorDb) && global.NoiseFloorDb > -88 &&
                        reduction >= 2;
        if (!detected) confidence = Math.Min(confidence, 0.54);
        return new NoiseEstimate(threshold, reduction, hiss, hissFrequency, release,
            hissExcess, double.NaN, false, confidence, detected);
    }

    private static string NoiseEvidence(NoiseEstimate noise, GlobalStats global,
        string detectedEvidence, string absentEvidence)
    {
        if (noise.Detected) return detectedEvidence;
        if (!noise.BenefitWithheld) return absentEvidence;
        return $"A stationary {global.NoiseFloorDb:0.0} dBFS floor was measured, but it sits "
             + $"{noise.NoiseToProgrammeDb:0.0} dB under the programme; reduction would remove "
             + "more music than noise and remains bypassed.";
    }

    private static (double AlignmentMs, double Confidence) EstimateAlignment(
        float[] left,
        float[] right,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        int available = Math.Min(left.Length, right.Length);
        if (available < sampleRate / 2) return (0, 0);
        int stride = 4;
        int count = Math.Min(available, sampleRate * 2);
        int start = Math.Max(0, (available - count) / 2);
        int maximumLag = Math.Max(1, sampleRate * 2 / 1000 / stride);
        double[] correlations = new double[maximumLag * 2 + 1];
        double best = double.NegativeInfinity;
        int bestLag = 0;
        for (int lag = -maximumLag; lag <= maximumLag; lag++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double cross = 0, leftSquare = 0, rightSquare = 0;
            int begin = Math.Max(1, -lag + 1);
            int end = Math.Min(count / stride, count / stride - lag);
            for (int i = begin; i < end; i++)
            {
                // First differences suppress stationary DC/low-frequency energy
                // and give a physical broadband delay a much sharper peak than a
                // phase-shifted periodic tone.
                int leftIndex = start + i * stride;
                int rightIndex = start + (i + lag) * stride;
                double l = Finite(left[leftIndex]) - Finite(left[leftIndex - stride]);
                double r = Finite(right[rightIndex]) - Finite(right[rightIndex - stride]);
                cross += l * r;
                leftSquare += l * l;
                rightSquare += r * r;
            }
            double correlation = cross / Math.Max(1e-12, Math.Sqrt(leftSquare * rightSquare));
            correlations[lag + maximumLag] = correlation;
            if (correlation > best)
            {
                best = correlation;
                bestLag = lag;
            }
        }

        double milliseconds = -bestLag * stride * 1000.0 / sampleRate;
        if (bestLag == 0 || best < 0.5) return (0, 0);

        // A periodic or narrow-band signal can have several equally plausible
        // correlation peaks. Require the winning lag to be materially better
        // than both zero lag and every non-adjacent alternative before proposing
        // a channel delay.
        double zeroCorrelation = correlations[maximumLag];
        double runnerUp = double.NegativeInfinity;
        const int adjacentPeakRadius = 1;
        for (int index = 0; index < correlations.Length; index++)
        {
            int lag = index - maximumLag;
            if (Math.Abs(lag - bestLag) <= adjacentPeakRadius) continue;
            runnerUp = Math.Max(runnerUp, correlations[index]);
        }
        double zeroImprovement = best - zeroCorrelation;
        double peakContrast = best - runnerUp;
        if (zeroImprovement < 0.05 || peakContrast < 0.08) return (0, 0);

        double confidence = Math.Clamp(
            Math.Max(0, best) *
            Ramp(Math.Abs(milliseconds), 0.08, 0.6) *
            Ramp(zeroImprovement, 0.05, 0.30) *
            Ramp(peakContrast, 0.08, 0.35),
            0, 0.95);
        return (Quantize(Math.Clamp(milliseconds, -2, 2), 0.01), confidence);
    }

    private static void ApplyHum(EffectFactory.EffectState state, HumEstimate hum)
    {
        state.Enabled = hum.Detected;
        Set(state, "frequency", hum.Frequency);
        Set(state, "harmonics", hum.Harmonics);
        Set(state, "harmonicMask", hum.HarmonicMask);
        Set(state, "q", hum.Q);
        Set(state, "amount", hum.Amount);
    }

    private static void ApplyNoise(EffectFactory.EffectState state, NoiseEstimate noise)
    {
        state.Enabled = noise.Detected;
        Set(state, "threshold", noise.ThresholdDb);
        Set(state, "reduction", noise.ReductionDb);
        Set(state, "hiss", noise.HissDb);
        Set(state, "hissFreq", noise.HissFrequency);
        Set(state, "release", noise.ReleaseMs);
    }

    private static void ConfigureLimiter(
        EffectFactory.EffectState limiter,
        GlobalStats global,
        double targetLufs,
        double maximumDriveDb)
    {
        double loudnessDrive = double.IsFinite(global.IntegratedLufs)
            ? Math.Clamp(targetLufs - global.IntegratedLufs, 0, maximumDriveDb)
            : 0;
        double peakDrive = double.IsFinite(global.TruePeakDb)
            ? Math.Max(0, -0.5 - global.TruePeakDb)
            : 0;
        double drive = global.Frames > 0 ? Math.Min(loudnessDrive, peakDrive) : 0;
        Set(limiter, "thresh", -Quantize(drive, 0.25));
        Set(limiter, "ceiling", -1);
        limiter.Enabled = true;
    }

    private static CleanupRecommendation Recommendation(
        EffectFactory.ChainPreset baseline,
        EffectFactory.EffectState recommended,
        string displayName,
        string evidence,
        string currentText,
        string recommendedText,
        double confidence,
        bool applyByDefault) => new(
            recommended.TypeId,
            displayName,
            evidence,
            currentText,
            recommendedText,
            Math.Clamp(double.IsFinite(confidence) ? confidence : 0, 0, 1),
            applyByDefault);

    private static void NormalizePreset(EffectFactory.ChainPreset preset)
    {
        foreach (EffectFactory.EffectState state in preset.Effects)
        {
            IAudioEffect effect = EffectFactory.Create(state.TypeId);
            foreach (EffectParam descriptor in effect.Params)
            {
                double value = state.Params.TryGetValue(descriptor.Key, out double candidate) && double.IsFinite(candidate)
                    ? candidate
                    : descriptor.Default;
                state.Params[descriptor.Key] = Math.Clamp(value, descriptor.Min, descriptor.Max);
            }
        }
    }

    private static void ValidateInput(IReadOnlyList<float[]> channels, int sampleRate, out int frames)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels.Count == 0)
            throw new ArgumentException("At least one audio channel is required.", nameof(channels));
        frames = channels[0]?.Length ?? throw new ArgumentException("Audio channels cannot be null.", nameof(channels));
        for (int channel = 0; channel < channels.Count; channel++)
        {
            if (channels[channel] == null || channels[channel].Length != frames)
                throw new ArgumentException("Audio channels must be non-null and equal in length.", nameof(channels));
        }
    }

    private static EffectFactory.EffectState State(EffectFactory.ChainPreset preset, string typeId) =>
        preset.Effects.First(effect => effect.TypeId == typeId);

    private static double Param(EffectFactory.EffectState state, string key) =>
        state.Params.TryGetValue(key, out double value) ? value : 0;

    private static void Set(EffectFactory.EffectState state, string key, double value)
    {
        IAudioEffect effect = EffectFactory.Create(state.TypeId);
        EffectParam? descriptor = effect.Params.FirstOrDefault(parameter => parameter.Key == key);
        if (descriptor == null || !double.IsFinite(value)) return;
        state.Params[key] = Math.Clamp(value, descriptor.Min, descriptor.Max);
    }

    private static double[] AveragePower(IReadOnlyList<SpectralWindow> windows, int bins)
    {
        if (windows.Count == 0 || bins <= 0) return [];
        var result = new double[bins];
        foreach (SpectralWindow window in windows)
            for (int bin = 0; bin < bins; bin++) result[bin] += window.Power[bin];
        for (int bin = 0; bin < bins; bin++) result[bin] /= windows.Count;
        return result;
    }

    private static (double ExcessDb, double LineDb) LineExcess(
        double[] power,
        double frequency,
        int sampleRate,
        int fftSize)
    {
        var lineValues = new List<double>();
        var floorValues = new List<double>();
        double binHz = sampleRate / (double)fftSize;
        int first = Math.Max(1, (int)Math.Floor((frequency - 8) / binHz));
        int last = Math.Min(power.Length - 1, (int)Math.Ceiling((frequency + 8) / binHz));
        for (int bin = first; bin <= last; bin++)
        {
            double distance = Math.Abs(bin * binHz - frequency);
            if (distance <= 2) lineValues.Add(power[bin]);
            else if (distance >= 3.5) floorValues.Add(power[bin]);
        }
        if (lineValues.Count == 0 || floorValues.Count == 0) return (0, -120);
        floorValues.Sort();
        double line = lineValues.Average();
        double floor = PercentileSorted(floorValues, 0.5);
        return (10 * Math.Log10(Math.Max(1e-18, line) / Math.Max(1e-18, floor)),
            10 * Math.Log10(Math.Max(1e-18, line)));
    }

    private static double BandDb(double[] power, double lowHz, double highHz, int sampleRate, int fftSize)
    {
        if (power.Length == 0 || fftSize <= 1 || highHz <= lowHz) return double.NegativeInfinity;
        double binHz = sampleRate / (double)fftSize;
        int first = Math.Clamp((int)Math.Ceiling(lowHz / binHz), 1, power.Length - 1);
        int last = Math.Clamp((int)Math.Floor(highHz / binHz), first, power.Length - 1);
        double sum = 0;
        for (int bin = first; bin <= last; bin++) sum += power[bin];
        return 10 * Math.Log10(Math.Max(1e-18, sum / Math.Max(1, last - first + 1)));
    }

    /// <summary>Total energy in a band, used when two differently wide bands are compared.</summary>
    private static double BandEnergyDb(double[] power, double lowHz, double highHz,
        int sampleRate, int fftSize)
    {
        if (power.Length == 0 || fftSize <= 1 || highHz <= lowHz) return double.NegativeInfinity;
        double binHz = sampleRate / (double)fftSize;
        int first = Math.Clamp((int)Math.Ceiling(lowHz / binHz), 1, power.Length - 1);
        int last = Math.Clamp((int)Math.Floor(highHz / binHz), first, power.Length - 1);
        double sum = 0;
        for (int bin = first; bin <= last; bin++) sum += power[bin];
        return 10 * Math.Log10(Math.Max(1e-18, sum));
    }

    private static int HighestPowerOfTwo(int value)
    {
        int result = 1;
        while (result <= value / 2) result <<= 1;
        return result;
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0f;

    private static double Ramp(double value, double low, double high) =>
        !double.IsFinite(value) || high <= low ? 0 : Math.Clamp((value - low) / (high - low), 0, 1);

    private static double Quantize(double value, double step) =>
        !double.IsFinite(value) || step <= 0 ? 0 : Math.Round(value / step) * step;

    private static double PercentileSorted(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return double.NaN;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(sorted.Count - 1, lower + 1);
        double fraction = position - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }

    private static string HumText(EffectFactory.EffectState state) =>
        $"{Param(state, "frequency"):0.0} Hz · {Param(state, "harmonics"):0} × Q{Param(state, "q"):0} · {Param(state, "amount"):P0}";

    private static string NoiseText(EffectFactory.EffectState state) =>
        $"{Param(state, "threshold"):0} dB floor · {Param(state, "reduction"):0.#} dB NR · {Param(state, "hiss"):0.#} dB @ {Param(state, "hissFreq") / 1000:0.0}k";

    private static string EqText(EffectFactory.EffectState state) =>
        $"L {Param(state, "lowGain"):+0.0;-0.0;0.0} · M {Param(state, "midGain"):+0.0;-0.0;0.0} · H {Param(state, "highGain"):+0.0;-0.0;0.0} dB";

    private static string LimiterText(EffectFactory.EffectState state) =>
        $"{Param(state, "thresh"):0.0} dB drive · {Param(state, "ceiling"):0.0} dBTP";

    private static string ChannelText(EffectFactory.EffectState state) =>
        $"{Param(state, "balance"):+0.0;-0.0;0.0} dB · {Param(state, "align"):+0.00;-0.00;0.00} ms";

    private static string NormalizerText(EffectFactory.EffectState state) =>
        $"{Param(state, "target"):0} dB RMS · +{Param(state, "maxBoost"):0.#}/−{Param(state, "maxCut"):0.#} dB · gate {Param(state, "gate"):0}";

    private static string TrimText(EffectFactory.EffectState state) =>
        $"{Param(state, "gain"):+0.00;-0.00;0.00} dB";

    private static string FormatLufs(double value) =>
        double.IsFinite(value) ? $"{value:0.0} LUFS" : "Not measurable";

    private static string FormatDb(double value, string unit) =>
        double.IsFinite(value) ? $"{value:0.0} {unit}" : "not measurable";
}
