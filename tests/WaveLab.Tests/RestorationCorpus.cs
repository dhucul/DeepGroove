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
                int seed = recording.Path.GetHashCode(StringComparison.Ordinal) ^ (int)(severity * 32);
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
                int seed = recording.Path.GetHashCode(StringComparison.Ordinal) ^ (int)(severity * 8);
                var (clean, damaged, from, to) = PlantBurst(source, document.SampleRate, severity, seed);
                results.Add(measure(new SpectralCell(recording, severity, document.SampleRate,
                    clean, damaged, from, to, BurstLowHz, BurstHighHz)));
            }
            return (results, (string?)null);
        }, onExcluded: onExcluded);
}
