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
    private static long _mutationGeneration;
    private static string ManifestPath => Path.Combine(AppSettings.AutosaveDir, "manifest.json");

    public sealed class Entry
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string ManifestKey { get; set; } = "";
        public string AutosaveFile { get; set; } = "";
        public string Title { get; set; } = "";
        public string? OriginalPath { get; set; }
        public int? SourceBitDepth { get; set; }
        public bool? Dither16BitOnSave { get; set; }
        public bool? RequiresSaveAs { get; set; }
        public DateTime SavedAt { get; set; }
    }

    /// <summary>
    /// Restore format metadata that cannot be inferred from the 32-bit recovery WAV.
    /// Nullable fields keep manifests written by older versions backward compatible.
    /// </summary>
    public static void RestoreFormatMetadata(AudioDocument document, Entry entry)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SourceBitDepth is 16 or 24 or 32)
            document.SourceBitDepth = entry.SourceBitDepth.Value;
        if (entry.Dither16BitOnSave is { } dither16BitOnSave)
            document.Dither16BitOnSave = dither16BitOnSave;
        if (entry.RequiresSaveAs is { } requiresSaveAs)
            document.RequiresSaveAs = requiresSaveAs;
    }

    /// <summary>Write every dirty document; returns how many were saved.</summary>
    public static int RunNow(IEnumerable<(AudioDocument Doc, Guid Id)> dirtyDocs)
    {
        var inputs = dirtyDocs.ToList();
        var payload = new List<(string File, AudioDocument Doc, Guid Id)>();
        long mutationGeneration;
        lock (ManifestLock)
        {
            mutationGeneration = _mutationGeneration;
        }

        try
        {
            Directory.CreateDirectory(AppSettings.AutosaveDir);
            foreach (var (doc, id) in inputs)
            {
                string file = Path.Combine(AppSettings.AutosaveDir, $"{id:N}_{Guid.NewGuid():N}.wav");
                payload.Add((file, doc, id));
                WavCodec.Save(doc, file, 32, dither: false); // heavy IO stays outside the lock
            }
            lock (ManifestLock)
            {
                if (mutationGeneration != _mutationGeneration)
                {
                    foreach (var (file, _, _) in payload)
                        try { DeleteAutosaveFile(file); } catch { }
                    return 0;
                }

                var manifest = LoadManifest();
                var superseded = new List<string>();
                foreach (var (file, doc, id) in payload)
                {
                    if (manifest.TryGetValue(id.ToString("N"), out var previous)
                        && !string.Equals(previous.AutosaveFile, file, StringComparison.OrdinalIgnoreCase))
                        superseded.Add(previous.AutosaveFile);
                    manifest[id.ToString("N")] = new Entry
                    {
                        AutosaveFile = file,
                        Title = doc.Title,
                        OriginalPath = doc.FilePath,
                        SourceBitDepth = doc.SourceBitDepth,
                        Dither16BitOnSave = doc.Dither16BitOnSave,
                        RequiresSaveAs = doc.RequiresSaveAs,
                        SavedAt = DateTime.Now,
                    };
                }
                SaveManifest(manifest);
                foreach (string oldFile in superseded)
                    try { DeleteAutosaveFile(oldFile); } catch { }
            }
            return payload.Count;
        }
        catch
        {
            foreach (var (file, _, _) in payload)
                try { DeleteAutosaveFile(file); } catch { }
            return 0;
        }
    }

    public static void Remove(Guid id)
    {
        try
        {
            lock (ManifestLock)
            {
                _mutationGeneration++;
                var manifest = LoadManifest();
                if (manifest.Remove(id.ToString("N"), out var entry))
                {
                    DeleteAutosaveFile(entry.AutosaveFile);
                    SaveManifest(manifest);
                }
            }
        }
        catch { }
    }

    public static List<Entry> GetRecoverable()
    {
        try
        {
            lock (ManifestLock)
            {
                return LoadManifest()
                    .Where(pair => pair.Value != null
                        && IsAutosavePath(pair.Value.AutosaveFile, out string fullPath)
                        && File.Exists(fullPath))
                    .Select(pair =>
                    {
                        pair.Value.ManifestKey = pair.Key;
                        return pair.Value;
                    }).ToList();
            }
        }
        catch { return []; }
    }

    public static void RemoveRecoverable(IEnumerable<string> manifestKeys)
    {
        try
        {
            lock (ManifestLock)
            {
                _mutationGeneration++;
                var manifest = LoadManifest();
                foreach (string key in manifestKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!manifest.TryGetValue(key, out var entry)) continue;
                    try
                    {
                        DeleteAutosaveFile(entry.AutosaveFile);
                        manifest.Remove(key);
                    }
                    catch { /* keep the manifest entry so the recovery file is not orphaned */ }
                }
                SaveManifest(manifest);
            }
        }
        catch { }
    }

    public static bool ClearAll()
    {
        try
        {
            lock (ManifestLock)
            {
                _mutationGeneration++;
                if (Directory.Exists(AppSettings.AutosaveDir))
                    Directory.Delete(AppSettings.AutosaveDir, recursive: true);
            }
            return true;
        }
        catch { return false; }
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

    private static void SaveManifest(Dictionary<string, Entry> manifest)
    {
        Directory.CreateDirectory(AppSettings.AutosaveDir);
        string temporary = ManifestPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest));
            File.Move(temporary, ManifestPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsAutosavePath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string root = Path.GetFullPath(AppSettings.AutosaveDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void DeleteAutosaveFile(string? path)
    {
        if (!IsAutosavePath(path, out string fullPath)) return;
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }
}
