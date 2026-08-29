using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WaveLab.Audio;

namespace WaveLab.Util;

/// <summary>
/// Writes dirty documents to process-owned recovery storage. A later process can recover sessions
/// whose owner is no longer running without either process deleting the other's snapshots.
/// </summary>
public static class AutosaveService
{
    private static readonly object ManifestLock = new();
    private static readonly string SessionName = $"session-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private static long _mutationGeneration;
    private static string SessionDirectory => Path.Combine(AppSettings.AutosaveDir, SessionName);
    private static string ManifestPath => Path.Combine(SessionDirectory, "manifest.json");

    public readonly record struct DocumentSnapshot(
        AudioDocument Doc,
        Guid Id,
        IReadOnlyList<Marker>? Markers = null,
        IReadOnlyList<NamedRegion>? Regions = null);

    public sealed class Entry
    {
        [JsonIgnore] public string ManifestKey { get; set; } = "";
        [JsonIgnore] public string RecoveryManifestPath { get; set; } = "";
        public string AutosaveFile { get; set; } = "";
        public string Title { get; set; } = "";
        public string? OriginalPath { get; set; }
        public int? SourceBitDepth { get; set; }
        public bool? Dither16BitOnSave { get; set; }
        public bool? RequiresSaveAs { get; set; }
        public List<Marker>? Markers { get; set; }
        public List<NamedRegion>? Regions { get; set; }
        public DateTime SavedAt { get; set; }
    }

    public static void RestoreFormatMetadata(AudioDocument document, Entry entry)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SourceBitDepth is 16 or 24 or 32)
            document.SourceBitDepth = entry.SourceBitDepth.Value;
        if (entry.Dither16BitOnSave is { } dither) document.Dither16BitOnSave = dither;
        if (entry.RequiresSaveAs is { } saveAs) document.RequiresSaveAs = saveAs;
    }

    public static int RunNow(IEnumerable<(AudioDocument Doc, Guid Id)> dirtyDocs,
        CancellationToken cancellationToken = default) =>
        RunNow(dirtyDocs.Select(item => new DocumentSnapshot(item.Doc, item.Id)), cancellationToken);

    /// <summary>Write every supplied document; returns how many were atomically published.</summary>
    public static int RunNow(IEnumerable<DocumentSnapshot> dirtyDocs,
        CancellationToken cancellationToken = default)
    {
        var inputs = dirtyDocs.ToList();
        var payload = new List<(string File, DocumentSnapshot Snapshot)>();
        long mutationGeneration;
        lock (ManifestLock) mutationGeneration = _mutationGeneration;

        try
        {
            Directory.CreateDirectory(SessionDirectory);
            foreach (DocumentSnapshot input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = Path.Combine(SessionDirectory, $"{input.Id:N}_{Guid.NewGuid():N}.wav");
                payload.Add((file, input));
                WavCodec.Save(input.Doc, file, 32, dither: false, cancellationToken,
                    markers: input.Markers);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (ManifestLock)
            {
                if (mutationGeneration != _mutationGeneration)
                {
                    foreach (var (file, _) in payload)
                        try { DeleteAutosaveFile(file); } catch { }
                    return 0;
                }

                var manifest = LoadManifest(ManifestPath);
                var superseded = new List<string>();
                foreach (var (file, input) in payload)
                {
                    string key = input.Id.ToString("N");
                    if (manifest.TryGetValue(key, out Entry? previous)
                        && !string.Equals(previous.AutosaveFile, file, StringComparison.OrdinalIgnoreCase))
                        superseded.Add(previous.AutosaveFile);
                    manifest[key] = new Entry
                    {
                        AutosaveFile = file,
                        Title = input.Doc.Title,
                        OriginalPath = input.Doc.FilePath,
                        SourceBitDepth = input.Doc.SourceBitDepth,
                        Dither16BitOnSave = input.Doc.Dither16BitOnSave,
                        RequiresSaveAs = input.Doc.RequiresSaveAs,
                        Markers = input.Markers?.Select(CloneMarker).ToList(),
                        Regions = input.Regions?.Select(CloneRegion).ToList(),
                        SavedAt = DateTime.Now,
                    };
                }
                SaveManifest(manifest, ManifestPath);
                foreach (string oldFile in superseded)
                    try { DeleteAutosaveFile(oldFile); } catch { }
            }
            return payload.Count;
        }
        catch (OperationCanceledException)
        {
            foreach (var (file, _) in payload)
                try { DeleteAutosaveFile(file); } catch { }
            throw;
        }
        catch
        {
            foreach (var (file, _) in payload)
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
                var manifest = LoadManifest(ManifestPath);
                if (manifest.Remove(id.ToString("N"), out Entry? entry))
                {
                    DeleteAutosaveFile(entry.AutosaveFile);
                    SaveManifest(manifest, ManifestPath);
                }
                DeleteEmptySessionDirectory(SessionDirectory);
            }
        }
        catch { }
    }

    public static List<Entry> GetRecoverable() => GetRecoverable(includeCurrentSession: false);

    internal static List<Entry> GetRecoverable(bool includeCurrentSession)
    {
        var entries = new List<Entry>();
        try
        {
            lock (ManifestLock)
            {
                foreach (string manifestPath in EnumerateRecoveryManifests(includeCurrentSession))
                {
                    foreach ((string key, Entry entry) in LoadManifest(manifestPath))
                    {
                        if (entry == null
                            || !IsAutosavePath(entry.AutosaveFile, out string fullPath)
                            || !File.Exists(fullPath))
                            continue;
                        entry.ManifestKey = key;
                        entry.RecoveryManifestPath = manifestPath;
                        entries.Add(entry);
                    }
                }
            }
        }
        catch { }
        return entries;
    }

    /// <summary>Removes only entries that were successfully re-secured in this process.</summary>
    public static bool RemoveRecoverable(IEnumerable<Entry> entries)
    {
        bool succeeded = true;
        try
        {
            lock (ManifestLock)
            {
                _mutationGeneration++;
                foreach (IGrouping<string, Entry> group in entries
                    .Where(entry => IsRecoveryManifestPath(entry.RecoveryManifestPath))
                    .GroupBy(entry => Path.GetFullPath(entry.RecoveryManifestPath),
                        StringComparer.OrdinalIgnoreCase))
                {
                    var manifest = LoadManifest(group.Key);
                    foreach (Entry requested in group
                        .DistinctBy(entry => entry.ManifestKey, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!manifest.TryGetValue(requested.ManifestKey, out Entry? stored)) continue;
                        try
                        {
                            DeleteAutosaveFile(stored.AutosaveFile);
                            manifest.Remove(requested.ManifestKey);
                        }
                        catch { succeeded = false; }
                    }
                    try { SaveManifest(manifest, group.Key); }
                    catch { succeeded = false; }
                    DeleteEmptySessionDirectory(Path.GetDirectoryName(group.Key)!);
                }
            }
        }
        catch { return false; }
        return succeeded;
    }

    public static bool ClearRecoverable() => RemoveRecoverable(GetRecoverable());

    /// <summary>Clear only snapshots owned by this process.</summary>
    public static bool ClearAll()
    {
        try
        {
            lock (ManifestLock)
            {
                _mutationGeneration++;
                if (Directory.Exists(SessionDirectory))
                    Directory.Delete(SessionDirectory, recursive: true);
            }
            return true;
        }
        catch { return false; }
    }

    private static IEnumerable<string> EnumerateRecoveryManifests(bool includeCurrentSession)
    {
        string root = AppSettings.AutosaveDir;
        if (!Directory.Exists(root)) yield break;

        string legacyManifest = Path.Combine(root, "manifest.json");
        if (File.Exists(legacyManifest)) yield return legacyManifest;

        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(root).ToArray(); }
        catch { yield break; }
        foreach (string directory in directories)
        {
            if (!includeCurrentSession
                && (string.Equals(directory, SessionDirectory, StringComparison.OrdinalIgnoreCase)
                    || IsOwnedByRunningProcess(directory)))
                continue;
            string manifest = Path.Combine(directory, "manifest.json");
            if (File.Exists(manifest)) yield return manifest;
        }
    }

    private static bool IsOwnedByRunningProcess(string directory)
    {
        string name = Path.GetFileName(directory);
        if (!name.StartsWith("session-", StringComparison.OrdinalIgnoreCase)) return false;
        string remainder = name["session-".Length..];
        int separator = remainder.IndexOf('-');
        if (separator <= 0 || !int.TryParse(remainder[..separator], out int processId)) return false;
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static Dictionary<string, Entry> LoadManifest(string manifestPath)
    {
        try
        {
            if (File.Exists(manifestPath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                    File.ReadAllText(manifestPath)) ?? [];
        }
        catch { }
        return [];
    }

    private static void SaveManifest(Dictionary<string, Entry> manifest, string manifestPath)
    {
        string directory = Path.GetDirectoryName(manifestPath)!;
        Directory.CreateDirectory(directory);
        string temporary = manifestPath + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest));
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsRecoveryManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string root = Path.GetFullPath(AppSettings.AutosaveDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

    private static void DeleteEmptySessionDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            string manifest = Path.Combine(directory, "manifest.json");
            if (File.Exists(manifest) && LoadManifest(manifest).Count == 0) File.Delete(manifest);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch { }
    }

    private static Marker CloneMarker(Marker marker) => new()
    {
        Name = marker.Name,
        Position = marker.Position,
    };

    private static NamedRegion CloneRegion(NamedRegion region) => new()
    {
        Name = region.Name,
        Start = region.Start,
        End = region.End,
        CdTrackOrder = region.CdTrackOrder,
    };
}
