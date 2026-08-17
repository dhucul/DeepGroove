using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveLab.Audio.Montage;

/// <summary>What loading a montage produced, including the sources that could not be found.</summary>
public sealed record MontageLoadResult(MontageDocument Montage, IReadOnlyList<string> MissingSources);

/// <summary>
/// Reads and writes a montage as <c>.wlmontage.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The audio is not in the file.</b> A montage is a set of decisions about other people's files —
/// which part of which take, where, how loud, joined how — and writing the samples into it would
/// turn a few kilobytes into gigabytes and make every clip a copy that stops tracking its source.
/// Sources are referenced by path and reloaded, which means a montage can be broken by moving a
/// file; a missing source is reported rather than silently dropped, and its clips are kept so the
/// arrangement survives being repaired.
/// </para>
/// <para>
/// The write follows <see cref="MarkerStore"/>: stage to a unique hidden name, flush to disk, then
/// move into place. A montage is the arrangement of a whole record and half of one is worthless, so
/// it must never be possible to read a partly-written file.
/// </para>
/// </remarks>
public static class MontageStore
{
    private static readonly object WriteLock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string Extension = ".wlmontage.json";

    private sealed class SourceDto
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class ClipDto
    {
        public int SourceIndex { get; set; }
        public string Name { get; set; } = "Clip";
        public int SourceStart { get; set; }
        public int Length { get; set; }
        public int TimelineStart { get; set; }
        public double GainDb { get; set; }
        public int FadeInSamples { get; set; }
        public int FadeOutSamples { get; set; }
        public FadeShape FadeInShape { get; set; } = FadeShape.EqualPower;
        public FadeShape FadeOutShape { get; set; } = FadeShape.EqualPower;
    }

    private sealed class MontageDto
    {
        public int Version { get; set; } = 1;
        public int SampleRate { get; set; } = 44_100;
        public int ChannelCount { get; set; } = 2;
        public string Title { get; set; } = "Untitled montage";
        public List<SourceDto> Sources { get; set; } = [];
        public List<ClipDto> Clips { get; set; } = [];
    }

    public static void Save(MontageDocument montage, string path)
    {
        ArgumentNullException.ThrowIfNull(montage);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("The montage path has no directory.");

        var dto = new MontageDto
        {
            SampleRate = montage.SampleRate,
            ChannelCount = montage.ChannelCount,
            Title = montage.Title,
        };

        foreach (MontageSource source in montage.Sources)
        {
            dto.Sources.Add(new SourceDto
            {
                // Relative where it can be, so a montage and its audio move together. A source on
                // another volume has no relative form and keeps its absolute path.
                Path = Relative(directory, source.Path),
                Name = source.Name,
            });
        }

        foreach (MontageClip clip in montage.Clips)
        {
            dto.Clips.Add(new ClipDto
            {
                SourceIndex = clip.SourceIndex,
                Name = clip.Name,
                SourceStart = clip.SourceStart,
                Length = clip.Length,
                TimelineStart = clip.TimelineStart,
                GainDb = clip.GainDb,
                FadeInSamples = clip.FadeInSamples,
                FadeOutSamples = clip.FadeOutSamples,
                FadeInShape = clip.FadeInShape,
                FadeOutShape = clip.FadeOutShape,
            });
        }

        string json = JsonSerializer.Serialize(dto, Options);
        string stagePath = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            lock (WriteLock)
            {
                using (var stream = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 16 * 1024, FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(stagePath, full, overwrite: true);
            }
            montage.FilePath = full;
        }
        catch
        {
            try { File.Delete(stagePath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Reads a montage and reloads its sources. Sources that cannot be read are named in the result
    /// and their clips are kept, so the arrangement survives a file being moved.
    /// </summary>
    public static MontageLoadResult Load(string path, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("The montage path has no directory.");

        MontageDto dto = JsonSerializer.Deserialize<MontageDto>(File.ReadAllText(full), Options)
            ?? throw new InvalidDataException("The montage file is empty.");
        if (dto.SampleRate <= 0 || dto.ChannelCount is <= 0 or > 8)
            throw new InvalidDataException("The montage states an impossible format.");

        var montage = new MontageDocument(dto.SampleRate, dto.ChannelCount)
        {
            Title = string.IsNullOrWhiteSpace(dto.Title) ? "Untitled montage" : dto.Title,
            FilePath = full,
        };

        var missing = new List<string>();

        // Sources are added in file order whatever happens, because clips name them by index and a
        // skipped source would silently re-point every clip after it at the wrong audio.
        for (int i = 0; i < dto.Sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(dto.Sources.Count > 0 ? (double)i / dto.Sources.Count : 1);

            SourceDto source = dto.Sources[i];
            string sourcePath = Path.IsPathRooted(source.Path)
                ? source.Path
                : Path.GetFullPath(Path.Combine(directory, source.Path));

            try
            {
                montage.AddSource(MontageSource.Load(sourcePath, dto.SampleRate, dto.ChannelCount,
                    cancellationToken));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                missing.Add(sourcePath);
                montage.AddSource(MontageSource.From(
                    [.. Enumerable.Range(0, dto.ChannelCount).Select(_ => Array.Empty<float>())],
                    dto.SampleRate, dto.SampleRate, dto.ChannelCount,
                    string.IsNullOrWhiteSpace(source.Name) ? Path.GetFileName(sourcePath) : source.Name,
                    sourcePath));
            }
        }

        foreach (ClipDto clip in dto.Clips)
        {
            if (clip.SourceIndex < 0 || clip.SourceIndex >= montage.Sources.Count) continue;
            montage.Add(new MontageClip
            {
                SourceIndex = clip.SourceIndex,
                Name = string.IsNullOrWhiteSpace(clip.Name) ? "Clip" : clip.Name,
                SourceStart = Math.Max(0, clip.SourceStart),
                Length = Math.Max(0, clip.Length),
                TimelineStart = Math.Max(0, clip.TimelineStart),
                GainDb = double.IsFinite(clip.GainDb) ? Math.Clamp(clip.GainDb, -96, 24) : 0,
                FadeInSamples = Math.Max(0, clip.FadeInSamples),
                FadeOutSamples = Math.Max(0, clip.FadeOutSamples),
                FadeInShape = clip.FadeInShape,
                FadeOutShape = clip.FadeOutShape,
            });
        }

        progress?.Report(1);
        return new MontageLoadResult(montage, missing);
    }

    /// <summary>A path relative to the montage's folder where one exists, absolute where it does not.</summary>
    private static string Relative(string directory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string relative = Path.GetRelativePath(directory, path);

            // GetRelativePath returns the input unchanged when there is no relative form (a
            // different volume), and that is exactly when the absolute path is what we want.
            return Path.IsPathRooted(relative) ? path : relative;
        }
        catch { return path; }
    }
}
