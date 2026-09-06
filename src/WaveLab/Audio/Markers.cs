using System.IO;
using System.Text;
using System.Text.Json;

namespace WaveLab.Audio;

public sealed class Marker
{
    public string Name { get; set; } = "Marker";
    public int Position { get; set; }
}

public sealed class NamedRegion
{
    public string Name { get; set; } = "Region";
    public int Start { get; set; }
    public int End { get; set; }
    /// <summary>
    /// One-based order when this region belongs to an arranged audio-CD plan.
    /// Null keeps ordinary editing/restoration regions independent of that plan.
    /// </summary>
    public int? CdTrackOrder { get; set; }
}

/// <summary>Sidecar persistence for markers/regions: &lt;audiofile&gt;.wlmeta.json.</summary>
public static class MarkerStore
{
    private static readonly object WriteLock = new();

    private sealed class Meta
    {
        public List<Marker> Markers { get; set; } = [];
        public List<NamedRegion> Regions { get; set; } = [];
        public long? AudioLengthBytes { get; set; }
        public long? AudioWriteUtcTicks { get; set; }
    }

    public static string SidecarPath(string audioPath) => audioPath + ".wlmeta.json";

    public static (List<Marker> Markers, List<NamedRegion> Regions) Load(string? audioPath)
    {
        if (audioPath == null) return ([], []);
        try
        {
            string path = SidecarPath(audioPath);
            if (!File.Exists(path)) return ([], []);
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(path));
            if (meta?.AudioLengthBytes is { } length && meta.AudioWriteUtcTicks is { } ticks)
            {
                var audio = new FileInfo(audioPath);
                if (!audio.Exists || audio.Length != length || audio.LastWriteTimeUtc.Ticks != ticks)
                    return ([], []);
            }
            return (meta?.Markers ?? [], meta?.Regions ?? []);
        }
        catch { return ([], []); }
    }

    /// <summary>
    /// Markers embedded in the file itself, for when there is no sidecar.
    /// </summary>
    /// <remarks>
    /// The sidecar wins where both exist: it carries regions and CD track order, which cue points
    /// cannot express, so preferring the file's own marks would silently lose the richer set. Cue
    /// points are what a file arrives with from somebody else's program, and this is what makes
    /// those visible here at all.
    /// </remarks>
    public static List<Marker> FromRiff(RiffMetadata? riff)
    {
        var markers = new List<Marker>();
        if (riff == null) return markers;

        // A WAV says this with cue points and a label list; an AIFF says it with one MARK chunk.
        // Same information either way, so both arrive as the same type and the caller sees neither.
        IEnumerable<BroadcastMetadata.CuePoint> points =
            riff.Find("cue ") is { } cue
                ? BroadcastMetadata.ReadCuePoints(cue.Data, riff.FindList("adtl")?.Data)
                : riff.Find("MARK") is { } mark
                    ? AiffMetadata.ReadMarkChunk(mark.Data)
                    : [];

        foreach (BroadcastMetadata.CuePoint point in points)
        {
            markers.Add(new Marker
            {
                Name = string.IsNullOrWhiteSpace(point.Label) ? $"Cue {point.Id}" : point.Label,
                Position = Math.Max(0, point.Position),
            });
        }
        return markers;
    }

    public static void Save(string? audioPath, IEnumerable<Marker> markers, IEnumerable<NamedRegion> regions)
    {
        if (audioPath == null) return;
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(regions);

        var meta = new Meta { Markers = [.. markers], Regions = [.. regions] };
        var audioFile = new FileInfo(audioPath);
        if (audioFile.Exists)
        {
            meta.AudioLengthBytes = audioFile.Length;
            meta.AudioWriteUtcTicks = audioFile.LastWriteTimeUtc.Ticks;
        }
        string path = Path.GetFullPath(SidecarPath(audioPath));
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The marker sidecar path has no directory.");

        if (meta.Markers.Count == 0 && meta.Regions.Count == 0)
        {
            lock (WriteLock)
                File.Delete(path);
            return;
        }

        string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        string stagePath = Path.Combine(directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            lock (WriteLock)
            {
                using (var stream = new FileStream(
                           stagePath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           16 * 1024,
                           FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(stagePath, path, overwrite: true);
            }
        }
        catch
        {
            try { File.Delete(stagePath); } catch { }
            throw;
        }
    }
}
