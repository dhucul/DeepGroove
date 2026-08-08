using System.IO;
using System.Text.Json;
using WaveLab.Audio;

namespace WaveLab.Util;

/// <summary>
/// Periodically writes dirty documents to the autosave folder and keeps a manifest,
/// enabling crash recovery on next launch. Cleared on clean exit.
/// </summary>
public static class AutosaveService
{
    private static readonly object ManifestLock = new();
    private static string ManifestPath => Path.Combine(AppSettings.AutosaveDir, "manifest.json");

    public sealed class Entry
    {
        public string AutosaveFile { get; set; } = "";
        public string Title { get; set; } = "";
        public string? OriginalPath { get; set; }
        public DateTime SavedAt { get; set; }
    }

    /// <summary>Write every dirty document; returns how many were saved.</summary>
    public static int RunNow(IEnumerable<(AudioDocument Doc, Guid Id)> dirtyDocs)
    {
        int saved = 0;
        try
        {
            Directory.CreateDirectory(AppSettings.AutosaveDir);
            var payload = new List<(string File, AudioDocument Doc, Guid Id)>();
            foreach (var (doc, id) in dirtyDocs)
            {
                string file = Path.Combine(AppSettings.AutosaveDir, $"{id:N}.wav");
                WavCodec.Save(doc, file, 32, dither: false); // heavy IO stays outside the lock
                payload.Add((file, doc, id));
                saved++;
            }
            lock (ManifestLock)
            {
                var manifest = LoadManifest();
                foreach (var (file, doc, id) in payload)
                    manifest[id.ToString("N")] = new Entry
                    {
                        AutosaveFile = file,
                        Title = doc.Title,
                        OriginalPath = doc.FilePath,
                        SavedAt = DateTime.Now,
                    };
                File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
            }
        }
        catch { }
        return saved;
    }

    public static void Remove(Guid id)
    {
        try
        {
            lock (ManifestLock)
            {
                var manifest = LoadManifest();
                if (manifest.Remove(id.ToString("N"), out var entry))
                {
                    if (File.Exists(entry.AutosaveFile)) File.Delete(entry.AutosaveFile);
                    File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
                }
            }
        }
        catch { }
    }

    public static List<Entry> GetRecoverable()
    {
        try
        {
            return LoadManifest().Values.Where(e => File.Exists(e.AutosaveFile)).ToList();
        }
        catch { return []; }
    }

    public static void ClearAll()
    {
        try
        {
            if (Directory.Exists(AppSettings.AutosaveDir))
                Directory.Delete(AppSettings.AutosaveDir, recursive: true);
        }
        catch { }
    }

    private static Dictionary<string, Entry> LoadManifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(ManifestPath)) ?? [];
        }
        catch { }
        return [];
    }
}
