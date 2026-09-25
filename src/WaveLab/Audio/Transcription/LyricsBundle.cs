using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WaveLab.Audio.Transcription;

/// <summary>Checks the application payload, without installing software or contacting a service.</summary>
internal static class LyricsBundle
{
    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string RequirementsSha256 { get; set; } = "";
        public Dictionary<string, long> RequiredFiles { get; set; } = [];
    }

    public static bool IsReady(string assets)
    {
        try
        {
            string bundle = Path.GetFullPath(Path.Combine(assets, "Engine"));
            string manifestPath = Path.Combine(bundle, "bundle.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1_000_000) return false;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), LyricsTranscript.JsonOptions);
            if (manifest?.SchemaVersion != 1 || manifest.RequiredFiles == null
                || !manifest.RequiredFiles.ContainsKey("python/python.exe")
                || !manifest.RequiredFiles.ContainsKey("python/python311.dll")
                || !manifest.RequiredFiles.Keys.Any(p => p.StartsWith("models/", StringComparison.Ordinal))) return false;
            string fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(assets, "requirements.txt"))));
            if (!string.Equals(manifest.RequirementsSha256, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var file in manifest.RequiredFiles)
            {
                string path = Path.GetFullPath(Path.Combine(bundle, file.Key));
                if (!path.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || file.Value <= 0 || !File.Exists(path) || new FileInfo(path).Length != file.Value) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
