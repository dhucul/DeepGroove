using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;

namespace WaveLab.Tests;

/// <summary>One recording in an external validation corpus.</summary>
/// <param name="Corpus">Short name used to group results.</param>
/// <param name="Path">Where the audio is.</param>
/// <param name="ClickResistant">
/// Whether to take the clipping level from a 99.95th-percentile programme peak rather than the
/// absolute peak. A shellac transfer's loudest sample is a surface click, measured at 15.6 dB above
/// the music, so clipping relative to it barely touches the programme.
/// </param>
public sealed record CorpusRecording(string Corpus, string Path, bool ClickResistant)
{
    public string ShortName => System.IO.Path.GetFileName(Path).Replace(' ', '_');
}

/// <summary>One recording damaged at one severity: the unit every measurement is reported over.</summary>
public sealed record CorpusCell(CorpusRecording Recording, double Relative, int SampleRate,
    float[] Clean, float[] Clipped, bool[] Damaged, ClippedPeakEvent[] Events)
{
    public double ClipLevel
    {
        get
        {
            var levels = Events.Select(e => (double)e.AbsoluteClipLevel).Where(l => l > 0).ToList();
            levels.Sort();
            return levels.Count == 0 ? 0 : levels[levels.Count / 2];
        }
    }

    /// <summary>Signal to noise over the samples the clipping destroyed, which is the only fair set.</summary>
    public double Score(float[] candidate) => DeclipCorpus.SnrDb(Clean, candidate, Damaged);

    public double Raw => Score(Clipped);
}

/// <summary>
/// The external corpora the declip chain is measured against, and the machinery to run a
/// measurement over them.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the same corpus walk had been hand-written as a scratch probe once per
/// experiment, and the rewrites carried real bugs: an AppleDouble <c>._</c> resource fork counted
/// as a recording, a header column offset that silently misaligned every variant, a corpus taken
/// at the absolute peak where it needed the click-resistant one. A measurement harness that is
/// rewritten each time is a measurement harness nobody has reviewed.
/// </para>
/// <para>
/// <b>It is opt-in and does nothing unless <c>WAVELAB_CORPUS=1</c>.</b> The corpora are external and
/// mostly not redistributable, so the ordinary suite must not depend on them, and a harness that
/// runs by accident is how a 10-second suite became a 5m43s one. Paths come from
/// <c>WAVELAB_CORPUS1</c> to <c>WAVELAB_CORPUS5</c> when set.
/// </para>
/// <para>
/// <b>Recordings that are already clipped are excluded and reported.</b> A repair can only be
/// scored against a clean reference, and a recording that arrived damaged has none: every gain
/// measured against it is measured against the wrong thing. Reporting rather than silently
/// dropping matters, because a corpus that quietly shrinks is one nobody can reproduce.
/// </para>
/// <para>
/// Two things make it fast enough to iterate with. Recordings are measured <b>in parallel</b>, one
/// worker per recording so a file is decoded once for all four severities. And the A-SPADE result
/// is <b>cached on disk</b>. Sweeps that vary only the ceiling — which is most of them — then never
/// run the solver at all: measured over 272 cells and six ceilings, <b>576 seconds cold against
/// 12 warm</b>. <c>WAVELAB_CORPUS_CACHE</c> says where it goes.
/// </para>
/// <para>
/// <b>The cache is far larger than the reasoning behind it suggested, and the measurement is the
/// number to trust.</b> <c>Spade.Project</c> restores every reliable sample to exactly its windowed
/// input, so it looked as though the output would differ only around the plateaus and the deltas
/// would come to a few megabytes. Measured, the solver moves <b>9.65% of samples</b>, not the
/// fraction of a percent the plateaus occupy, and the three corpora cost <b>977 MB across 272
/// cells</b>: overlap-add sums in double and divides by the window weight, so a sample can come
/// back changed in its last bit or two without the reconstruction being wrong anywhere.
/// <i>Reconstructed exactly</i> and <i>bit-identical</i> are not the same claim. Each cell is
/// written in whichever of two forms is smaller — indices and values, or the whole array — and at
/// 9.65% the sparse form wins every time, so the dense branch is a guard that has never fired.
/// </para>
/// </remarks>
public static class DeclipCorpus
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("WAVELAB_CORPUS"), "1", StringComparison.Ordinal);

    /// <summary>Severities every recording is measured at, as a fraction of its programme peak.</summary>
    public static IReadOnlyList<double> Levels { get; } = [0.70, 0.50, 0.35, 0.22];

    private static string Root(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set : fallback;

    /// <summary>
    /// Workers to run. Two cores are left for the rest of the machine, and
    /// <c>WAVELAB_CORPUS_THREADS</c> overrides it - setting it to 1 gives the sequential baseline
    /// that any speedup claim has to be measured against.
    /// </summary>
    public static int DefaultParallelism =>
        int.TryParse(Environment.GetEnvironmentVariable("WAVELAB_CORPUS_THREADS"), out int threads) && threads > 0
            ? threads
            : Math.Max(1, Environment.ProcessorCount - 2);

    public static string CacheDirectory =>
        Root("WAVELAB_CORPUS_CACHE", Path.Combine(Path.GetTempPath(), "wavelab-corpus-cache"));

    /// <summary>Every recording in every corpus that is present on this machine.</summary>
    public static IReadOnlyList<CorpusRecording> Recordings()
    {
        var found = new List<CorpusRecording>();

        string one = Root("WAVELAB_CORPUS1", @"C:\Users\dhucu\Music\mymusic");
        if (Directory.Exists(one))
            foreach (var file in Directory.GetFiles(one).OrderBy(f => f, StringComparer.Ordinal))
            {
                // AppleDouble resource forks carry the same name as the audio beside them.
                if (Path.GetFileName(file).StartsWith("._", StringComparison.Ordinal)) continue;
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is ".aiff" or ".aif") found.Add(new CorpusRecording("1-record", file, false));
            }

        string two = Root("WAVELAB_CORPUS2", @"C:\Windows\Media");
        if (Directory.Exists(two))
            foreach (var file in Directory.GetFiles(two, "*.wav").OrderBy(f => f, StringComparer.Ordinal))
                if (new FileInfo(file).Length > 200_000) found.Add(new CorpusRecording("2", file, false));

        string three = Root("WAVELAB_CORPUS3", "");
        if (Directory.Exists(three))
            foreach (var file in Directory.GetFiles(three, "*.mp3").OrderBy(f => f, StringComparer.Ordinal))
                found.Add(new CorpusRecording("3", file, true));

        string four = Root("WAVELAB_CORPUS4", "");
        if (Directory.Exists(four))
            foreach (var file in Directory.GetFiles(four, "*.mp3").OrderBy(f => f, StringComparer.Ordinal))
                found.Add(new CorpusRecording("4", file, false));

        string five = Root("WAVELAB_CORPUS5", "");
        if (Directory.Exists(five))
            foreach (var file in Directory.GetFiles(five, "*.mp3").OrderBy(f => f, StringComparer.Ordinal))
                found.Add(new CorpusRecording("5", file, false));

        return found;
    }

    public static double SnrDb(float[] clean, float[] candidate, bool[] damaged)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            if (!damaged[i]) continue;
            double difference = clean[i] - candidate[i];
            signal += (double)clean[i] * clean[i];
            error += difference * difference;
        }
        return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
    }

    /// <summary>
    /// The level clipping is applied at. For shellac this is the 99.95th percentile rather than the
    /// maximum, because the maximum is a surface click and clipping relative to it is a no-op.
    /// </summary>
    public static double ProgrammePeak(float[] source, bool clickResistant)
    {
        if (!clickResistant)
        {
            double peak = 0;
            foreach (float value in source) peak = Math.Max(peak, Math.Abs(value));
            return peak;
        }
        var magnitudes = new float[source.Length];
        for (int i = 0; i < source.Length; i++) magnitudes[i] = Math.Abs(source[i]);
        Array.Sort(magnitudes);
        return magnitudes[(int)(magnitudes.Length * 0.9995)];
    }

    /// <summary>
    /// Clip at a fraction of the programme peak, rescaled so the plateau sits at the rail. A
    /// genuinely clipped recording ran out of numbers rather than being quiet, and without the
    /// rescale <see cref="ClippingAnalysisOptions.MinimumPeakLevel"/> skips the channel entirely.
    /// </summary>
    public static (float[] Clean, float[] Clipped, bool[] Damaged) Damage(
        float[] source, double relative, bool clickResistant)
    {
        double limit = ProgrammePeak(source, clickResistant) * relative;
        double scale = 1.0 / limit;
        var clean = new float[source.Length];
        var clipped = new float[source.Length];
        var damaged = new bool[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            clean[i] = (float)(source[i] * scale);
            float value = source[i];
            if (value > limit) { value = (float)limit; damaged[i] = true; }
            else if (value < -limit) { value = (float)-limit; damaged[i] = true; }
            clipped[i] = (float)(value * scale);
        }
        return (clean, clipped, damaged);
    }

    /// <summary>
    /// Runs <paramref name="measure"/> over every cell, one worker per recording, and returns the
    /// results in a deterministic order regardless of how the work was scheduled.
    /// </summary>
    public static List<T> Measure<T>(Func<CorpusCell, T> measure, int? maximumParallelism = null,
        Action<CorpusRecording, string>? onExcluded = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        var recordings = Recordings();
        var byRecording = new ConcurrentDictionary<string, List<(double Relative, T Value)>>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maximumParallelism ?? DefaultParallelism,
        };

        // Longest first. The corpora mix one-second notification chimes with four-minute shellac
        // sides, and in file order the long ones start last and run alone: the first measured pass
        // spent its closing minutes at 20% CPU behind a handful of stragglers. Scheduling the big
        // work first lets the small cells fill in around it.
        var scheduled = recordings
            .OrderByDescending(r => new FileInfo(r.Path).Length)
            .ToList();

        Parallel.ForEach(scheduled, options, recording =>
        {
            AudioDocument document;
            try { document = Load(recording.Path); }
            catch (Exception) { return; }
            if (document.Channels.Count == 0 || document.Channels[0].Length < 4096)
            {
                onExcluded?.Invoke(recording, "too short to measure");
                return;
            }

            // A repair can only be scored against a clean reference, so a recording that is already
            // clipped is not usable: its "clean" channel is itself damaged, and every gain measured
            // against it is measured against the wrong thing. This is reported rather than silently
            // dropped, because a corpus that quietly shrinks is a corpus nobody can reproduce.
            //
            // It screens the channel actually measured rather than the file. Corpus 1 has a track
            // clipped on channel 1 and clean on channel 0; the reference here is channel 0, so that
            // cell is sound and excluding the file would throw away good measurement.
            var asFound = Restoration.AnalyzeClipping([document.Channels[0]], document.SampleRate,
                new ClippingAnalysisOptions());
            if (asFound.Events.Count > 0)
            {
                onExcluded?.Invoke(recording, $"already clipped: {asFound.Events.Count} events before any damage");
                return;
            }

            var results = new List<(double, T)>();
            foreach (double relative in Levels)
            {
                var (clean, clipped, damaged) = Damage(document.Channels[0], relative, recording.ClickResistant);
                var analysis = Restoration.AnalyzeClipping([clipped], document.SampleRate,
                    new ClippingAnalysisOptions());
                if (analysis.Events.Count == 0) continue;
                var cell = new CorpusCell(recording, relative, document.SampleRate,
                    clean, clipped, damaged, analysis.Events.ToArray());
                results.Add((relative, measure(cell)));
            }
            byRecording[recording.Path] = results;
        });

        var ordered = new List<T>();
        foreach (var recording in recordings)
            if (byRecording.TryGetValue(recording.Path, out var results))
                foreach (var (_, value) in results)
                    ordered.Add(value);
        return ordered;
    }

    // MediaFoundation-backed import is not documented as thread-safe, and a corrupted decode would
    // be invisible in the numbers rather than loud, so decoding is serialised. It is a few percent
    // of the work; the solver is the rest.
    private static readonly object LoadGate = new();

    private static AudioDocument Load(string path)
    {
        lock (LoadGate) return AudioImporter.Load(path);
    }

    /// <summary>
    /// A-SPADE over a cell, cached on disk. Repeated sweeps that change only what happens to the
    /// solver's output — a ceiling, a blend — get it back in milliseconds.
    /// </summary>
    public static float[] Solve(CorpusCell cell, SpadeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(cell);
        double clipLevel = cell.ClipLevel;
        if (!(clipLevel > 0)) return (float[])cell.Clipped.Clone();

        if (options.FrameSize == 0) options = SpadeOptions.Default;
        string key = CacheKey(cell, clipLevel, options);
        string file = Path.Combine(CacheDirectory, key + ".delta");

        if (TryReadDelta(file, cell.Clipped, out var cached)) return cached;

        var solved = (float[])cell.Clipped.Clone();
        Spade.Declip(solved, clipLevel, options);
        WriteDelta(file, cell.Clipped, solved);
        return solved;
    }

    private static string CacheKey(CorpusCell cell, double clipLevel, SpadeOptions options)
    {
        var info = new FileInfo(cell.Recording.Path);
        string material = string.Join('|',
            cell.Recording.Path, info.Length, info.LastWriteTimeUtc.Ticks,
            cell.Relative.ToString("0.0000"), clipLevel.ToString("0.000000"),
            options.FrameSize, options.Hop, options.SparsityStep, options.RelaxEvery,
            options.MaxIterations, options.Tolerance);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    private const uint DeltaMagic = 0x53504145;   // "SPAE": format 2, sparse or dense per cell

    private const byte SparseForm = 0;
    private const byte DenseForm = 1;

    private static void WriteDelta(string file, float[] original, float[] solved)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string staging = file + ".tmp";
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                int changed = 0;
                for (int i = 0; i < original.Length; i++)
                    if (!original[i].Equals(solved[i])) changed++;

                writer.Write(DeltaMagic);
                writer.Write(original.Length);

                // Eight bytes an entry sparse against four a sample dense: past half the file
                // changing, naming the indices costs more than writing everything.
                if ((long)changed * 8 < (long)original.Length * 4)
                {
                    writer.Write(SparseForm);
                    writer.Write(changed);
                    for (int i = 0; i < original.Length; i++)
                    {
                        if (original[i].Equals(solved[i])) continue;
                        writer.Write(i);
                        writer.Write(solved[i]);
                    }
                }
                else
                {
                    writer.Write(DenseForm);
                    writer.Write(original.Length);
                    foreach (float value in solved) writer.Write(value);
                }
            }
            File.Move(staging, file, overwrite: true);
        }
        catch (IOException)
        {
            // A cache is an optimisation. Losing one costs time, never correctness.
        }
    }

    private static bool TryReadDelta(string file, float[] original, out float[] solved)
    {
        solved = [];
        if (!File.Exists(file)) return false;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != DeltaMagic) return false;
            if (reader.ReadInt32() != original.Length) return false;
            byte form = reader.ReadByte();
            int count = reader.ReadInt32();
            if (count < 0 || count > original.Length) return false;

            if (form == DenseForm)
            {
                if (count != original.Length) return false;
                var dense = new float[count];
                for (int i = 0; i < count; i++) dense[i] = reader.ReadSingle();
                solved = dense;
                return true;
            }
            if (form != SparseForm) return false;

            var result = (float[])original.Clone();
            for (int i = 0; i < count; i++)
            {
                int index = reader.ReadInt32();
                float value = reader.ReadSingle();
                if ((uint)index >= (uint)result.Length) return false;
                result[index] = value;
            }
            solved = result;
            return true;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return false;
        }
    }
}
