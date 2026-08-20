using WaveLab.Audio;
using WaveLab.Audio.Dsp;

namespace WaveLab.Tests;

/// <summary>One recording with synthetic clicks planted in it, at one severity.</summary>
public sealed record ClickCell(CorpusRecording Recording, double Severity, int SampleRate,
    float[] Clean, float[] Damaged, bool[] Hit, int ClickCount)
{
    public double Score(float[] candidate) => DeclipCorpus.SnrDb(Clean, candidate, Hit);

    public double Raw => Score(Damaged);
}

/// <summary>
/// Click and pop repair measured against real recordings, the way declip already is.
/// </summary>
/// <remarks>
/// <para>
/// Every calibration of the declip chain that rested on synthetic material alone turned out wrong —
/// five thresholds, and a sixth rule that survived three real corpora and failed on five. Click
/// repair had no real-audio measurement at all, which is the same exposure on a tool that matters
/// more here: every record has clicks, not every record is clipped.
/// </para>
/// <para>
/// <b>The damage model is the part to argue with, so it states its choices.</b> A repair can only
/// be scored against a clean reference, so the clicks have to be planted rather than found, and
/// what gets planted decides what the numbers mean.
/// </para>
/// <list type="bullet">
/// <item><b>Amplitude is relative to the local signal, not absolute.</b> A click is audible because
/// it stands out from what surrounds it, and a fixed amplitude would be a catastrophe in a quiet
/// passage and inaudible in a loud one. The severity axis is how far the click stands above the
/// local RMS, measured over a 20 ms window.</item>
/// <item><b>Shape is a damped impulse with alternating polarity</b> — a sharp step followed by an
/// exponential decay over a few samples, which is what a surface defect does to a stylus. Not a
/// single-sample spike: those are trivially detectable by curvature and would flatter the
/// detector.</item>
/// <item><b>Length runs from 1 to 20 samples</b>, straddling <c>MaximumClickLengthMs</c> (0.35 ms,
/// about 15 samples at 44.1 kHz) so the set is not entirely inside what the analyser calls a click,
/// and some land in pop territory.</item>
/// <item><b>Density is 8 clicks a second.</b> The 78rpm corpus carries 788 to 6457 detected clicks
/// a side, which over a 3 to 4 minute side is roughly 4 to 35 a second, so this sits low in the
/// real range rather than at its worst.</item>
/// <item><b>Placement avoids near-silence.</b> A click planted where the music has stopped has no
/// local RMS to stand above, and its repair is unscoreable rather than hard.</item>
/// <item><b>Everything is seeded</b> from the file path and severity, so a rerun plants the same
/// clicks in the same places.</item>
/// </list>
/// <para>
/// Corpus 3 is excluded from scoring: shellac transfers already carry thousands of real clicks, so
/// their clean reference is not clean. They are the false-positive material instead — what the
/// analyser finds there cannot be scored, but what it finds on the digital corpora before any
/// damage can.
/// </para>
/// </remarks>
public static class ClickCorpus
{
    /// <summary>How far a planted click stands above the local RMS, in dB.</summary>
    public static IReadOnlyList<double> Severities { get; } = [24.0, 18.0, 12.0, 6.0];

    public const double ClicksPerSecond = 8.0;
    internal const int LocalWindowMs = 20;
    private const int MinimumLengthSamples = 1;
    private const int MaximumLengthSamples = 20;

    /// <summary>Local RMS over a window centred on <paramref name="at"/>.</summary>
    internal static double LocalRmsAt(float[] x, int at, int half) => LocalRms(x, at, half);

    private static double LocalRms(float[] x, int at, int half)
    {
        int from = Math.Max(0, at - half), to = Math.Min(x.Length, at + half);
        double sum = 0;
        for (int i = from; i < to; i++) sum += (double)x[i] * x[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>
    /// Plants clicks and returns the clean reference, the damaged audio, and which samples were
    /// touched — the only samples the repair is scored over.
    /// </summary>
    public static (float[] Clean, float[] Damaged, bool[] Hit, int Count) Damage(
        float[] source, int sampleRate, double aboveLocalDb, int seed)
    {
        var clean = (float[])source.Clone();
        var damaged = (float[])source.Clone();
        var hit = new bool[source.Length];

        int half = Math.Max(1, sampleRate * LocalWindowMs / 2000);
        double overall = LocalRms(source, source.Length / 2, source.Length / 2);
        double floor = overall * 0.05;          // "near silence" relative to the whole recording
        double gain = Math.Pow(10.0, aboveLocalDb / 20.0);

        var random = new Random(seed);
        int spacing = Math.Max(1, (int)(sampleRate / ClicksPerSecond));
        int count = 0;

        for (int centre = spacing; centre < source.Length - spacing; centre += spacing)
        {
            // Jitter inside the slot so the clicks are not periodic, which would let a detector key
            // on the period rather than on the defect.
            int at = centre + random.Next(-spacing / 3, spacing / 3);
            if (at < 32 || at >= source.Length - 32) continue;

            double local = LocalRms(source, at, half);
            if (local < floor) continue;        // nothing to stand above; the repair is unscoreable

            int length = random.Next(MinimumLengthSamples, MaximumLengthSamples + 1);
            double amplitude = local * gain * (0.6 + random.NextDouble() * 0.8);
            double sign = random.Next(2) == 0 ? 1 : -1;
            double decay = Math.Exp(-3.0 / Math.Max(1, length));

            double value = amplitude * sign;
            for (int i = 0; i < length && at + i < source.Length; i++)
            {
                // Alternating polarity through the decay: a defect rings rather than simply steps.
                damaged[at + i] = (float)Math.Clamp(damaged[at + i] + value, -4.0, 4.0);
                hit[at + i] = true;
                value *= -decay;
            }
            count++;
        }
        return (clean, damaged, hit, count);
    }

    /// <summary>
    /// Runs <paramref name="measure"/> over every recording that can carry planted clicks, at every
    /// severity. Corpus 3 is skipped: its clean reference already contains real clicks.
    /// </summary>
    public static List<T> Measure<T>(Func<ClickCell, T> measure, int? maximumParallelism = null,
        Action<CorpusRecording, string>? onExcluded = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        return DeclipCorpus.ForEachRecording<T>((recording, document) =>
        {
            if (recording.Corpus == "3")
                return ((List<T>?)null, "shellac already carries real clicks, so it has no clean reference");

            var source = document.Channels[0];
            var found = Restoration.AnalyzeClicks([source], document.SampleRate, new ClickAnalysisOptions());
            // A recording that is already full of clicks cannot be a reference either. A handful is
            // tolerated: no real transfer is perfectly free of them, and the scoring only looks at
            // samples this harness planted.
            double perSecond = found.Events.Count / (source.Length / (double)document.SampleRate);
            if (perSecond > 1.0)
                return ((List<T>?)null, $"already clicky: {found.Events.Count} events, {perSecond:0.0}/s before any damage");

            var results = new List<T>();
            foreach (double severity in Severities)
            {
                int seed = DeclipCorpus.StableHash(recording.Path) ^ (int)(severity * 16);
                var (clean, damaged, hit, count) = Damage(source, document.SampleRate, severity, seed);
                if (count < 8) continue;
                results.Add(measure(new ClickCell(recording, severity, document.SampleRate,
                    clean, damaged, hit, count)));
            }
            return (results, (string?)null);
        }, maximumParallelism, onExcluded);
    }
}
