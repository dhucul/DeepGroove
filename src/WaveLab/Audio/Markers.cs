using System.IO;
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
            return (meta?.Markers ?? [], meta?.Regions ?? []);
        }
        catch { return ([], []); }
    }

    public static void Save(string? audioPath, IEnumerable<Marker> markers, IEnumerable<NamedRegion> regions)
    {
        if (audioPath == null) return;
        try
        {
            var meta = new Meta { Markers = [.. markers], Regions = [.. regions] };
            string path = SidecarPath(audioPath);
            lock (WriteLock)
            {
                if (meta.Markers.Count == 0 && meta.Regions.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                File.WriteAllText(path, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }
}
