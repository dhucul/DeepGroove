using WaveLab.Audio.Dsp;

namespace WaveLab.Tests;

/// <summary>One recording with crackle planted in it.</summary>
public sealed record CrackleCell(CorpusRecording Recording, double Severity, int SampleRate,
    float[] Clean, float[] Damaged, bool[] Hit, int Count)
{
    public double Score(float[] candidate) => DeclipCorpus.SnrDb(Clean, candidate, Hit);
    public double Raw => Score(Damaged);
}

/// <summary>One recording with a known speed variation warped into it.</summary>
public sealed record WowCell(CorpusRecording Recording, double PlantedPercent, int SampleRate,
    float[] Clean, float[] Damaged);

/// <summary>One recording with broadband hiss planted in it, and the profile a tool would learn.</summary>
public sealed record NoiseCell(CorpusRecording Recording, double SnrDb, int SampleRate,
    float[] Clean, float[] Damaged, float[] Profile, bool[] Scored)
{
    /// <summary>Whole-signal signal-to-noise ratio, the way declip and clicks are scored.</summary>
    public double Score(float[] candidate) => DeclipCorpus.SnrDb(Clean, candidate, Scored);
    public double Raw => Score(Damaged);

    /// <summary>Segmental signal-to-noise ratio, which is the one that can see this tool work.</summary>
    public double Segmental(float[] candidate) =>
        RestorationCorpus.SegmentalSnrDb(Clean, candidate, SampleRate);
    public double RawSegmental => Segmental(Damaged);
}

/// <summary>One recording with a noise burst planted in one time-frequency region.</summary>
public sealed record SpectralCell(CorpusRecording Recording, double Severity, int SampleRate,
    float[] Clean, float[] Damaged, int From, int To, double LowHz, double HighHz);

/// <summary>
/// The rest of the restoration suite measured against real recordings: crackle, speed variation and
/// spectral repair. Declip and click repair have their own harnesses; this covers what was left.
/// </summary>
/// <remarks>
/// <para>
/// All three were synthetic-only, which is the exposure that produced five wrong declip thresholds
/// and a sixth rule that survived three real corpora and failed on five. <b>Each tool needs a
/// different metric, and the choice is stated rather than assumed</b>, because a metric that
/// flatters the tool is worse than no measurement.
/// </para>
/// </remarks>
public static class RestorationCorpus
{
    // ---------- crackle ----------

    /// <summary>How far a crackle grain stands above the local RMS, in dB.</summary>
    public static IReadOnlyList<double> CrackleSeverities { get; } = [12.0, 6.0, 0.0, -6.0];

    /// <summary>Grains a second. Crackle is dense and quiet where a click is sparse and loud.</summary>
    public const double GrainsPerSecond = 200.0;

    private static double LocalRms(float[] x, int at, int half)
    {
        int from = Math.Max(0, at - half), to = Math.Min(x.Length, at + half);
        double sum = 0;
        for (int i = from; i < to; i++) sum += (double)x[i] * x[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>
    /// Plants crackle: many short, quiet grains rather than the few loud impulses
    /// <see cref="ClickCorpus"/> plants. That difference is the whole distinction between the two
    /// tools, so the damage models have to differ in the same way or the measurement says nothing
    /// about which tool is right for which defect.
    /// </summary>
    public static (float[] Clean, float[] Damaged, bool[] Hit, int Count) PlantCrackle(
        float[] source, int sampleRate, double aboveLocalDb, int seed)
    {
        var clean = (float[])source.Clone();
        var damaged = (float[])source.Clone();
        var hit = new bool[source.Length];

        int half = Math.Max(1, sampleRate / 100);
        double overall = LocalRms(source, source.Length / 2, source.Length / 2);
        double floor = overall * 0.05;
        double gain = Math.Pow(10.0, aboveLocalDb / 20.0);

        var random = new Random(seed);
        int spacing = Math.Max(2, (int)(sampleRate / GrainsPerSecond));
        int count = 0;

        for (int centre = spacing; centre < source.Length - spacing; centre += spacing)
        {
            int at = centre + random.Next(-spacing / 3, spacing / 3);
            if (at < 8 || at >= source.Length - 8) continue;
            double local = LocalRms(source, at, half);
            if (local < floor) continue;

            int length = random.Next(1, 4);
            double amplitude = local * gain * (0.5 + random.NextDouble());
            double sign = random.Next(2) == 0 ? 1 : -1;
            for (int i = 0; i < length && at + i < source.Length; i++)
            {
                damaged[at + i] = (float)Math.Clamp(damaged[at + i] + amplitude * sign, -4.0, 4.0);
                hit[at + i] = true;
                sign = -sign;
            }
            count++;
        }
        return (clean, damaged, hit, count);
    }

    // ---------- speed variation ----------

    /// <summary>Peak speed deviation planted, as a percentage.</summary>
    public static IReadOnlyList<double> WowSeverities { get; } = [2.4, 1.2, 0.6, 0.3];

    /// <summary>
    /// Warps the signal with a known speed variation: a slow wow term and a faster flutter term,
    /// both zero-mean so the recording neither gains nor loses length overall.
    /// </summary>
    public static float[] PlantWow(float[] source, int sampleRate, double peakPercent)
    {
        var damaged = new float[source.Length];
        double wowHz = 0.7, flutterHz = 6.3;
        double depth = peakPercent / 100.0;
        double position = 0;

        for (int i = 0; i < source.Length; i++)
        {
            double t = i / (double)sampleRate;
            double ratio = 1.0 + depth * (0.75 * Math.Sin(2 * Math.PI * wowHz * t)
                                        + 0.25 * Math.Sin(2 * Math.PI * flutterHz * t + 1.1));
            int index = (int)position;
            double frac = position - index;
            damaged[i] = index + 1 < source.Length
                ? (float)(source[index] * (1 - frac) + source[index + 1] * frac)
                : source[Math.Min(index, source.Length - 1)];
            position += ratio;
            if (position >= source.Length - 2) break;
        }
        return damaged;
    }

    /// <summary>
    /// Undoes <see cref="PlantWow"/> with the exact warp that was planted, using the same linear
    /// interpolation the correction uses. <b>This is the ceiling</b>: whatever signal to noise a
    /// perfect estimator could reach, given the same resampler. Without it a negative waveform
    /// result cannot be read, because resampling costs accuracy on its own and the measurement
    /// would be charging the correction for it.
    /// </summary>
    public static float[] UnplantWow(float[] damaged, int sampleRate, double peakPercent)
    {
        double wowHz = 0.7, flutterHz = 6.3;
        double depth = peakPercent / 100.0;

        // Where each output sample was read from when the warp was planted.
        var readAt = new double[damaged.Length];
        double position = 0;
        for (int i = 0; i < damaged.Length; i++)
        {
            readAt[i] = position;
            double t = i / (double)sampleRate;
            position += 1.0 + depth * (0.75 * Math.Sin(2 * Math.PI * wowHz * t)
                                     + 0.25 * Math.Sin(2 * Math.PI * flutterHz * t + 1.1));
        }

        // Invert that map: for each original sample, find where it ended up.
        var restored = new float[damaged.Length];
        int cursor = 0;
        for (int n = 0; n < damaged.Length; n++)
        {
            while (cursor + 1 < damaged.Length && readAt[cursor + 1] <= n) cursor++;
            if (cursor + 1 >= damaged.Length) { restored[n] = damaged[cursor]; continue; }
            double span = readAt[cursor + 1] - readAt[cursor];
            double frac = span > 1e-9 ? (n - readAt[cursor]) / span : 0;
            frac = Math.Clamp(frac, 0, 1);
            restored[n] = (float)(damaged[cursor] * (1 - frac) + damaged[cursor + 1] * frac);
        }
        return restored;
    }

    /// <summary>
    /// Root-mean-square timing error between two signals, in samples, measured by cross-correlating
    /// them in windows.
    /// </summary>
    /// <remarks>
    /// <b>Signal to noise is the wrong instrument for a timing error and this is the right one.</b>
    /// At 0.3% the planted drift reaches about 22 samples; halving it to 11 leaves the waveforms
    /// still uncorrelated sample for sample, so signal to noise reads about the same for a
    /// half-corrected recording as for an uncorrected one — it is nearly all-or-nothing, and cannot
    /// tell a correction that recovered most of the drift from one that recovered none. Residual
    /// shift is linear in what was recovered and says which happened.
    /// </remarks>
    public static double ResidualShiftSamples(float[] reference, float[] candidate, int sampleRate)
    {
        // The reach has to exceed the drift being measured or the search saturates and reports a
        // number that looks like a result. A 2.4% wow at 0.7 Hz drifts about 180 samples, so 512.
        const int window = 8192, reach = 512;
        int step = Math.Max(window, sampleRate);
        double sum = 0;
        int counted = 0;

        for (int start = reach; start + window + reach < Math.Min(reference.Length, candidate.Length);
             start += step)
        {
            double energy = 0;
            for (int i = start; i < start + window; i++) energy += (double)reference[i] * reference[i];
            if (energy <= 1e-9) continue;

            double best = double.NegativeInfinity;
            int bestLag = 0;
            var scores = new double[reach * 2 + 1];
            for (int lag = -reach; lag <= reach; lag++)
            {
                double dot = 0;
                for (int i = start; i < start + window; i++) dot += (double)reference[i] * candidate[i + lag];
                scores[lag + reach] = dot;
                if (dot > best) { best = dot; bestLag = lag; }
            }
            if (best <= 0) continue;

            int index = bestLag + reach;
            double before = index > 0 ? scores[index - 1] : best;
            double after = index < scores.Length - 1 ? scores[index + 1] : best;
            double denominator = before - 2 * best + after;
            double refined = bestLag + (Math.Abs(denominator) > 1e-12
                ? 0.5 * (before - after) / denominator : 0);

            sum += refined * refined;
            counted++;
        }
        return counted == 0 ? 0 : Math.Sqrt(sum / counted);
    }

    // ---------- spectral repair ----------

    /// <summary>Burst amplitude relative to the local RMS, in dB.</summary>
    public static IReadOnlyList<double> BurstSeverities { get; } = [12.0, 6.0, 0.0];

    public const double BurstLowHz = 900, BurstHighHz = 3500;
    public const int BurstSamples = 8000;

    /// <summary>
    /// Plants a band-limited noise burst — the defect spectral repair exists for, and the one the
    /// synthetic tests already use, so the only thing changing here is the material underneath it.
    /// </summary>
    public static (float[] Clean, float[] Damaged, int From, int To) PlantBurst(
        float[] source, int sampleRate, double aboveLocalDb, int seed)
    {
        var clean = (float[])source.Clone();
        var damaged = (float[])source.Clone();
        int from = Math.Max(0, source.Length / 2 - BurstSamples / 2);
        int to = Math.Min(source.Length, from + BurstSamples);

        double local = LocalRms(source, (from + to) / 2, (to - from) / 2);
        double amplitude = local * Math.Pow(10.0, aboveLocalDb / 20.0);

        var random = new Random(seed);
        const int partials = 96;
        var frequency = new double[partials];
        var phase = new double[partials];
        for (int p = 0; p < partials; p++)
        {
            frequency[p] = BurstLowHz + (BurstHighHz - BurstLowHz) * random.NextDouble();
            phase[p] = random.NextDouble() * 2 * Math.PI;
        }
        for (int i = from; i < to; i++)
        {
            double t = (i - from) / (double)sampleRate;
            // Raised-cosine envelope, so the burst does not itself look like a click.
            double envelope = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - from) / (double)(to - from));
            double sum = 0;
            for (int p = 0; p < partials; p++) sum += Math.Sin(2 * Math.PI * frequency[p] * t + phase[p]);
            damaged[i] = (float)Math.Clamp(
                damaged[i] + amplitude * envelope * sum / Math.Sqrt(partials), -4.0, 4.0);
        }
        return (clean, damaged, from, to);
    }

    // ---------- walks ----------

    private static bool UsableReference(CorpusRecording recording, WaveLab.Audio.AudioDocument document,
        out string? why)
    {
        why = null;
        if (recording.Corpus == "3")
        {
            why = "shellac already carries real crackle and clicks, so it has no clean reference";
            return false;
        }
        var source = document.Channels[0];
        var found = Restoration.AnalyzeClicks([source], document.SampleRate, new ClickAnalysisOptions());
        double perSecond = found.Events.Count / (source.Length / (double)document.SampleRate);
        if (perSecond > 1.0)
        {
            why = $"already clicky: {perSecond:0.0}/s before any damage";
            return false;
        }
        return true;
    }

    public static List<T> MeasureCrackle<T>(Func<CrackleCell, T> measure,
        Action<CorpusRecording, string>? onExcluded = null) =>
        DeclipCorpus.ForEachRecording<T>((recording, document) =>
        {
            if (!UsableReference(recording, document, out string? why)) return ((List<T>?)null, why);
            var source = document.Channels[0];
            var results = new List<T>();
            foreach (double severity in CrackleSeverities)
            {
                int seed = DeclipCorpus.StableHash(recording.Path) ^ (int)(severity * 32);
                var (clean, damaged, hit, count) = PlantCrackle(source, document.SampleRate, severity, seed);
                if (count < 50) continue;
                results.Add(measure(new CrackleCell(recording, severity, document.SampleRate,
                    clean, damaged, hit, count)));
            }
            return (results, (string?)null);
        }, onExcluded: onExcluded);

    public static List<T> MeasureWow<T>(Func<WowCell, T> measure,
        Action<CorpusRecording, string>? onExcluded = null) =>
        DeclipCorpus.ForEachRecording<T>((recording, document) =>
        {
            if (!UsableReference(recording, document, out string? why)) return ((List<T>?)null, why);
            var source = document.Channels[0];
            // Speed variation is measured over seconds, so a one-second notification chime cannot
            // carry it and would report noise as wow.
            if (source.Length < document.SampleRate * 20)
                return ((List<T>?)null, "shorter than 20 s, too short to carry a 0.7 Hz wow");

            var results = new List<T>();
            foreach (double percent in WowSeverities)
            {
                var damaged = PlantWow(source, document.SampleRate, percent);
                results.Add(measure(new WowCell(recording, percent, document.SampleRate,
                    (float[])source.Clone(), damaged)));
            }
            return (results, (string?)null);
        }, onExcluded: onExcluded);

    public static List<T> MeasureSpectral<T>(Func<SpectralCell, T> measure,
        Action<CorpusRecording, string>? onExcluded = null) =>
        DeclipCorpus.ForEachRecording<T>((recording, document) =>
        {
            if (!UsableReference(recording, document, out string? why)) return ((List<T>?)null, why);
            var source = document.Channels[0];
            if (source.Length < BurstSamples * 4)
                return ((List<T>?)null, "too short to plant a burst with context either side");

            var results = new List<T>();
            foreach (double severity in BurstSeverities)
            {
                int seed = DeclipCorpus.StableHash(recording.Path) ^ (int)(severity * 8);
                var (clean, damaged, from, to) = PlantBurst(source, document.SampleRate, severity, seed);
                results.Add(measure(new SpectralCell(recording, severity, document.SampleRate,
                    clean, damaged, from, to, BurstLowHz, BurstHighHz)));
            }
            return (results, (string?)null);
        }, onExcluded: onExcluded);

    // ---------- broadband hiss ----------

    /// <summary>
    /// Segmental signal-to-noise ratio: the mean over short frames of each frame's ratio in dB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The standard measure in speech enhancement, and it is here for the reason it is standard
    /// there. A whole-signal ratio is dominated by the loudest passages — where the noise is masked
    /// and every suppressor passes the audio through at unity — while what separates a spectral
    /// gate from an MMSE estimator is what each does <b>in the quiet</b>. Averaging per-frame
    /// decibels gives every frame one vote, so the quiet gets a say proportional to how much of the
    /// record it is rather than to how little energy it carries.
    /// </para>
    /// <para>
    /// Clamped to [-10, +35] dB per frame, as the literature clamps it. An untouched frame reports
    /// an enormous ratio that would carry the mean on its own, and a destroyed one a hugely
    /// negative one; neither is information about typical behaviour. Frames with no signal at all
    /// are dropped rather than counted, because a lead-in has no ratio to have.
    /// </para>
    /// </remarks>
    public static double SegmentalSnrDb(float[] clean, float[] candidate, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(clean);
        ArgumentNullException.ThrowIfNull(candidate);

        int frame = Math.Max(64, sampleRate / 50);   // 20 ms
        double total = 0;
        int counted = 0;
        for (int start = 0; start + frame <= clean.Length; start += frame)
        {
            double signal = 0, error = 0;
            for (int i = start; i < start + frame; i++)
            {
                double difference = clean[i] - candidate[i];
                signal += (double)clean[i] * clean[i];
                error += difference * difference;
            }
            if (signal <= 0) continue;
            total += Math.Clamp(10 * Math.Log10(signal / Math.Max(error, 1e-30)), -10, 35);
            counted++;
        }
        return counted == 0 ? 0 : total / counted;
    }

    /// <summary>Seconds of each recording the hiss measurement uses.</summary>
    public const int MaximumAnalysedSeconds = 60;

    /// <summary>How far the planted hiss sits below the programme RMS, in dB.</summary>
    /// <remarks>
    /// A worn transfer and a clean one, and the range where the decision is interesting. Above
    /// about 30 dB down there is little to remove and every method looks alike; below about 12 the
    /// noise is most of the signal and no spectral method can do much without taking the music
    /// with it.
    /// </remarks>
    public static IReadOnlyList<double> HissSeverities { get; } = [30.0, 24.0, 18.0, 12.0, 6.0, 0.0];

    /// <summary>
    /// Plants broadband hiss at a known signal-to-noise ratio.
    /// </summary>
    /// <remarks>
    /// <b>Tilted, not white.</b> Surface noise is, and a flat floor would let a single threshold do
    /// as well as a learned profile — which is precisely the thing the profile exists for, so a
    /// white floor would under-test both methods and flatter the simpler one. It is also
    /// <b>stationary</b>, which is the assumption both estimators are built on: measuring them
    /// against noise that drifts would be measuring something neither claims to do.
    /// </remarks>
    public static (float[] Clean, float[] Damaged) PlantHiss(float[] source, double snrDb, int seed)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clean = (float[])source.Clone();
        var damaged = (float[])source.Clone();

        double programmeEnergy = 0;
        foreach (float value in source) programmeEnergy += (double)value * value;
        double programme = Math.Sqrt(programmeEnergy / Math.Max(1, source.Length));
        if (!(programme > 0)) return (clean, damaged);

        double target = programme * Math.Pow(10, -snrDb / 20.0);

        // Generated twice from the same seed rather than stored. Keeping the noise in a
        // double[] the length of the recording put a 114 MB large-object allocation on
        // every severity of every parallel worker, and the run spent more time in the
        // collector than in the estimators it was supposed to be comparing.
        double energy = 0;
        foreach (double sample in TiltedNoise(source.Length, seed)) energy += sample * sample;
        double scale = target / Math.Sqrt(Math.Max(1e-30, energy / Math.Max(1, source.Length)));

        int index = 0;
        foreach (double sample in TiltedNoise(source.Length, seed))
        {
            damaged[index] = (float)(damaged[index] + sample * scale);
            index++;
        }
        return (clean, damaged);
    }

    /// <summary>
    /// Surface-noise-shaped noise: white with a one-pole tilt, deterministic in the seed so it can
    /// be regenerated rather than held.
    /// </summary>
    private static IEnumerable<double> TiltedNoise(int count, int seed)
    {
        var random = new Random(seed);
        double state = 0;
        for (int i = 0; i < count; i++)
        {
            double white = random.NextDouble() * 2 - 1;
            state = 0.72 * state + 0.28 * white;
            yield return white * 0.55 + state * 1.6;
        }
    }

    /// <summary>
    /// The profile the workbench would learn: the quietest two-second window of the <b>damaged</b>
    /// audio, found the same way <c>BuildAutomaticNoiseProfile</c> finds it.
    /// </summary>
    /// <remarks>
    /// Learning from the clean signal, or from the planted noise on its own, would measure a
    /// suppressor nobody can run. Where a recording has no genuinely quiet passage the profile
    /// contains music and both methods suffer for it — which is the situation a user is actually
    /// in, and both are handed the identical profile, so the comparison stays fair.
    /// </remarks>
    public static float[] LearnProfileAsTheWorkbenchWould(float[] damaged, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(damaged);
        if (damaged.Length < Restoration.NrFftSize) return [];

        int windowLength = Math.Min(damaged.Length,
            Math.Max(Restoration.NrFftSize, checked(sampleRate * 2)));
        int hop = Math.Min(windowLength, 4096);

        double rolling = 0;
        for (int i = 0; i < windowLength; i++) rolling += (double)damaged[i] * damaged[i];
        double quietest = rolling;
        int quietestStart = 0, previousStart = 0;
        for (int start = hop; start + windowLength <= damaged.Length; start += hop)
        {
            for (int i = previousStart; i < start; i++) rolling -= (double)damaged[i] * damaged[i];
            for (int i = previousStart + windowLength; i < start + windowLength; i++)
                rolling += (double)damaged[i] * damaged[i];
            if (rolling < quietest) { quietest = rolling; quietestStart = start; }
            previousStart = start;
        }

        return Restoration.LearnNoiseProfile([damaged], quietestStart, windowLength);
    }

    public static List<T> MeasureNoise<T>(Func<NoiseCell, T> measure,
        Action<CorpusRecording, string>? onExcluded = null) =>
        DeclipCorpus.ForEachRecording<T>((recording, document) =>
        {
            // Deliberately NOT screened by UsableReference. That screen rejects a recording
            // carrying clicks, which is right for crackle and spectral repair, where a real
            // defect contaminates the clean reference. Hiss is different: the recording's own
            // surface clicks appear identically in the reference and in the processed output, so
            // they cancel out of the score. Applying the click screen here excluded all nine
            // record transfers - the exact material this tool exists for - and left the
            // measurement standing on five notification chimes.
            float[] source = document.Channels[0];

            // Two seconds go to the profile window, so anything near that length would be
            // learning its noise floor from most of the programme.
            if (source.Length < document.SampleRate * 6)
                return ((List<T>?)null, "shorter than 6 s, too short to hold a profile window and programme");

            // Capped. Hiss is stationary and both estimators reach steady state in seconds, so a
            // minute of a side measures them as well as five does and the run stays tractable —
            // uncapped it was two CPU-hours, nearly all of it collecting the damage buffers.
            int analysed = Math.Min(source.Length, document.SampleRate * MaximumAnalysedSeconds);
            if (analysed < source.Length) source = source[..analysed];

            var scored = new bool[source.Length];
            Array.Fill(scored, true);

            var results = new List<T>();
            foreach (double snr in HissSeverities)
            {
                int seed = DeclipCorpus.StableHash(recording.Path) ^ (int)(snr * 16);
                var (clean, damaged) = PlantHiss(source, snr, seed);
                float[] profile = LearnProfileAsTheWorkbenchWould(damaged, document.SampleRate);
                if (profile.Length == 0 || !profile.Any(value => value > 0)) continue;
                results.Add(measure(new NoiseCell(recording, snr, document.SampleRate,
                    clean, damaged, profile, scored)));
            }
            return (results, (string?)null);
        }, onExcluded: onExcluded);
}
