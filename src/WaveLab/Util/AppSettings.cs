using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveLab.Util;

/// <summary>Persisted application settings (%AppData%\WaveLab\settings.json).</summary>
public sealed class AppSettings
{
    private static readonly object SaveLock = new();
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
    public int CaptureBufferMs { get; set; } = 100;
    public string OutputShareMode { get; set; } = "shared";
    public string InputShareMode { get; set; } = "shared";
    public bool OutputEventSync { get; set; } = true;
    public bool InputEventSync { get; set; } = true;
    public string OutputDefaultRole { get; set; } = "multimedia";
    public string InputDefaultRole { get; set; } = "console";

    /// <summary>
    /// Last settled level-check outcome per capture device id, so the Recording
    /// Level Assistant can recall what a given input needed previously.
    /// </summary>
    public Dictionary<string, InputCalibrationInfo> InputCalibrations { get; set; } = [];

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
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public long UndoLimitBytes => (long)Math.Max(64, UndoLimitMb) * 1024 * 1024;

    [JsonIgnore]
    public string? LastSaveError { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>One remembered level-check outcome for an input device.</summary>
    public sealed record InputCalibrationInfo(
        double SuggestedGainDb,
        double ProgramPeakDb,
        DateTime CheckedUtc);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings());
        }
        catch { }
        return new AppSettings();
    }

    public bool Save()
    {
        lock (SaveLock)
        {
            string temporary = SettingsPath + ".tmp";
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOpts));
                File.Move(temporary, SettingsPath, overwrite: true);
                LastSaveError = null;
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                LastSaveError = ex.Message;
                return false;
            }
        }
    }

    public bool AddRecentFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var previous = RecentFiles.ToList();
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        if (Save()) return true;
        RecentFiles = previous;
        return false;
    }

    internal static AppSettings Normalize(AppSettings settings)
    {
        settings.BufferMs = Math.Clamp(settings.BufferMs, 3, 500);
        settings.CaptureBufferMs = Math.Clamp(settings.CaptureBufferMs, 3, 500);
        settings.OutputShareMode = Audio.AudioHardwareOptions.NormalizeShareMode(settings.OutputShareMode);
        settings.InputShareMode = Audio.AudioHardwareOptions.NormalizeShareMode(settings.InputShareMode);
        settings.OutputDefaultRole = Audio.AudioHardwareOptions.NormalizeRole(
            settings.OutputDefaultRole, NAudio.CoreAudioApi.Role.Multimedia);
        settings.InputDefaultRole = Audio.AudioHardwareOptions.NormalizeRole(
            settings.InputDefaultRole, NAudio.CoreAudioApi.Role.Console);
        settings.UndoLimitMb = Math.Clamp(settings.UndoLimitMb, 64, 4096);
        settings.AutosaveMinutes = settings.AutosaveMinutes is 1 or 2 or 3 or 5 or 10 or 15
            ? settings.AutosaveMinutes
            : 3;
        settings.ExportFormat = settings.ExportFormat is
            "wav32" or "wav24" or "wav16" or "wav16nodither" or
            "aiff32" or "aiff24" or "aiff16" or "aiff16nodither" or
            "mp3" or "aac" or "wma" or "flac"
            ? settings.ExportFormat
            : "wav32";
        settings.ExportBitrateKbps = settings.ExportBitrateKbps is 128 or 160 or 192 or 256 or 320
            ? settings.ExportBitrateKbps
            : 192;
        settings.RecentFiles = NormalizePaths(settings.RecentFiles, 10);
        settings.LastSessionFiles = NormalizePaths(settings.LastSessionFiles, int.MaxValue);
        settings.InputCalibrations = (settings.InputCalibrations ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                && pair.Value != null
                && double.IsFinite(pair.Value.SuggestedGainDb)
                && double.IsFinite(pair.Value.ProgramPeakDb))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!double.IsFinite(settings.WindowWidth) || settings.WindowWidth < 0) settings.WindowWidth = 0;
        if (!double.IsFinite(settings.WindowHeight) || settings.WindowHeight < 0) settings.WindowHeight = 0;
        if (settings.WindowLeft is not { } left || !double.IsFinite(left)) settings.WindowLeft = null;
        if (settings.WindowTop is not { } top || !double.IsFinite(top)) settings.WindowTop = null;
        return settings;
    }

    private static List<string> NormalizePaths(List<string>? paths, int limit) =>
        (paths ?? [])
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(limit)
        .ToList();

    public void RestoreDefaults()
    {
        var d = new AppSettings();
        OutputDeviceId = d.OutputDeviceId;
        InputDeviceId = d.InputDeviceId;
        BufferMs = d.BufferMs;
        CaptureBufferMs = d.CaptureBufferMs;
        OutputShareMode = d.OutputShareMode;
        InputShareMode = d.InputShareMode;
        OutputEventSync = d.OutputEventSync;
        InputEventSync = d.InputEventSync;
        OutputDefaultRole = d.OutputDefaultRole;
        InputDefaultRole = d.InputDefaultRole;
        ReopenLastSession = d.ReopenLastSession;
        UndoLimitMb = d.UndoLimitMb;
        AutosaveEnabled = d.AutosaveEnabled;
        AutosaveMinutes = d.AutosaveMinutes;
        ExportFormat = d.ExportFormat;
        ExportBitrateKbps = d.ExportBitrateKbps;
    }
}
