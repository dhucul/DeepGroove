using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveLab.Util;

/// <summary>Persisted application settings (%AppData%\WaveLab\settings.json).</summary>
public sealed class AppSettings
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveLab");
    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string AutosaveDir => Path.Combine(AppDataDir, "Autosave");
    public static string PresetsDir => Path.Combine(AppDataDir, "Presets");

    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    // Audio
    public string? OutputDeviceId { get; set; }
    public string? InputDeviceId { get; set; }
    public int BufferMs { get; set; } = 60;

    // General
    public bool ReopenLastSession { get; set; } = true;
    public int UndoLimitMb { get; set; } = 512;
    public List<string> RecentFiles { get; set; } = [];
    public List<string> LastSessionFiles { get; set; } = [];
    public string? LastOpenFolder { get; set; }

    // Autosave
    public bool AutosaveEnabled { get; set; } = true;
    public int AutosaveMinutes { get; set; } = 3;

    // Export defaults
    public string ExportFormat { get; set; } = "wav32";
    public int ExportBitrateKbps { get; set; } = 192;

    // Window placement
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public long UndoLimitBytes => (long)Math.Max(64, UndoLimitMb) * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        Save();
    }

    public void RestoreDefaults()
    {
        var d = new AppSettings();
        OutputDeviceId = d.OutputDeviceId;
        InputDeviceId = d.InputDeviceId;
        BufferMs = d.BufferMs;
        ReopenLastSession = d.ReopenLastSession;
        UndoLimitMb = d.UndoLimitMb;
        AutosaveEnabled = d.AutosaveEnabled;
        AutosaveMinutes = d.AutosaveMinutes;
        ExportFormat = d.ExportFormat;
        ExportBitrateKbps = d.ExportBitrateKbps;
    }
}
