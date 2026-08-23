using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveLab.Util;

/// <summary>Persisted application settings (%AppData%\WaveLab\settings.json).</summary>
public sealed class AppSettings
{
    private static readonly object SaveLock = new();

    private static string _appDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveLab");

    /// <summary>
    /// Root directory every path below is derived from. Defaults to %AppData%\WaveLab.
    /// Tests point it at a private temp directory so they never read or write the real
    /// user profile; assigning it drops the cached <see cref="Instance"/> so the next
    /// read loads from the new root instead of the previous one.
    /// </summary>
    public static string AppDataDir
    {
        get => _appDataDir;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _appDataDir = value;
            Volatile.Write(ref _instance, null);
        }
    }

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string AutosaveDir => Path.Combine(AppDataDir, "Autosave");
    public static string PresetsDir => Path.Combine(AppDataDir, "Presets");

    private static AppSettings? _instance;

    /// <remarks>
    /// <c>_instance ??= Load()</c> is not atomic. Two threads reaching it first both
    /// load, one instance wins the field, and the other is handed to a caller who then
    /// mutates settings nobody will ever save. Every reader is on the UI thread today,
    /// but this is read from forty-odd places across the audio layer and the next one
    /// added will not know that.
    /// </remarks>
    public static AppSettings Instance
    {
        get
        {
            AppSettings? current = Volatile.Read(ref _instance);
            if (current != null) return current;
            lock (SaveLock)
            {
                current = _instance;
                if (current == null) Volatile.Write(ref _instance, current = Load());
                return current;
            }
        }
    }

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
    /// Peak the Recording Level Assistant aims a transfer at, in dBTP. Lower
    /// leaves more room for the restoration stages that follow — declicking and
    /// peak reconstruction can put repaired peaks back above what was captured.
    /// </summary>
    public double RecordingTargetCeilingDb { get; set; } = DefaultRecordingTargetCeilingDb;

    /// <summary>
    /// Safest level-check outcome per capture device id, so the Recording Level
    /// Assistant can recall — and replay — what a given input needed previously.
    /// </summary>
    public Dictionary<string, InputCalibrationInfo> InputCalibrations { get; set; } = [];

    /// <summary>−6 dBTP: a transfer default, not a mastering one. See the property.</summary>
    public const double DefaultRecordingTargetCeilingDb = -6;

    /// <summary>
    /// Marked on the ceiling slider. These were the whole choice before it became a
    /// continuous value, and they are still the three worth aiming at deliberately:
    /// −3 the old behaviour, −6 the transfer default, −10 heavy repair expected.
    /// </summary>
    public static readonly double[] RecordingTargetCeilingLandmarksDb = [-3, -6, -10];

    /// <summary>
    /// Deepest ceiling the slider reaches. The analyzer accepts down to −24 dBTP and
    /// a settings file holding one is honoured — the slider simply extends to meet it
    /// — but nothing below −12 is worth the track space by default.
    /// </summary>
    public const double AdjustableRecordingTargetCeilingFloorDb = -12;

    /// <summary>Ceiling resolution. Finer than this is below what a gain step buys.</summary>
    public const double RecordingTargetCeilingStepDb = 0.5;

    /// <summary>
    /// Clamps a ceiling into the range the analyzer enforces and snaps it to the
    /// slider's step, so a hand-edited or out-of-range value is corrected rather than
    /// discarded. Only a non-finite value falls back to the default.
    /// </summary>
    public static double NormalizeTargetCeilingDb(double ceilingDb)
    {
        if (!double.IsFinite(ceilingDb)) return DefaultRecordingTargetCeilingDb;
        double clamped = Math.Clamp(
            ceilingDb,
            Audio.Dsp.RecordingLevelAnalyzer.MinimumTargetCeilingDb,
            Audio.Dsp.RecordingLevelAnalyzer.MaximumTargetCeilingDb);
        return Math.Round(clamped / RecordingTargetCeilingStepDb, MidpointRounding.AwayFromZero)
            * RecordingTargetCeilingStepDb;
    }

    /// <summary>A remembered calibration older than this is no longer worth trusting.</summary>
    public const int CalibrationMemoryDays = 180;

    /// <summary>
    /// How far ahead of now a calibration's timestamp may sit and still be believed.
    /// Ordinary clock skew, not a licence — beyond this the entry is treated as corrupt.
    /// </summary>
    public const int CalibrationClockSkewDays = 1;

    /// <summary>Bound on remembered devices, so the dictionary cannot grow forever.</summary>
    public const int MaximumRememberedCalibrations = 32;

    // Recording — automatic stop. Bounds live with the code that enforces them.
    public bool RecordAutoStopOnRunOut { get; set; }
    public double RecordRunOutHoldSeconds { get; set; } = Audio.RunOutDetector.DefaultHoldSeconds;
    public bool RecordAutoStopOnDuration { get; set; }
    public int RecordAutoStopMinutes { get; set; } = ViewModels.RecordViewModel.DefaultAutoStopMinutes;

    // General
    public bool ReopenLastSession { get; set; } = true;
    public int UndoLimitMb { get; set; } = 512;
    public List<string> RecentFiles { get; set; } = [];
    public List<string> LastSessionFiles { get; set; } = [];
    public string? LastOpenFolder { get; set; }

    /// <summary>
    /// Whether a restoration pass keeps what it removed as its own tab. Off by default: it costs
    /// a second copy of the range, and someone who has not asked for it should not pay that.
    /// Remembered because the people who do want it want it for a whole collection.
    /// </summary>
    public bool KeepRemovedMaterial { get; set; }

    /// <summary>
    /// How far the programme may sit above its own noise floor before broadband reduction declines
    /// entirely, in dB. Ten is the shipped default and the measured optimum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a setting rather than a raised default, and the difference is the whole point.</b>
    /// The rule it feeds was measured over 108 cells: a fixed depth scores −0.85 dB segmental,
    /// worse than leaving the audio alone, and scaling the depth takes cells that come out worse
    /// than doing nothing from 46 of 108 to 15. Raising the ceiling gives that protection back, so
    /// the default keeps its measurement and anyone who needs the reducer to fire on a quiet-floored
    /// file can say so per installation.
    /// </para>
    /// <para>
    /// <b>The case it exists for is a record rather than a tape.</b> Surface crackle is impulsive,
    /// the estimate behind the rule is an RMS ratio, and a plainly audible crackle can measure 24 dB
    /// under the programme — so the rule declines on exactly the material a user is most sure needs
    /// help. Raising this is one answer; the de-crackle card is the better one, and the readouts say
    /// so.
    /// </para>
    /// </remarks>
    public double NoiseDepthCeilingDb { get; set; } = Audio.Dsp.Restoration.NoiseDepthCeilingDb;

    /// <summary>Step the settings slider moves the ceiling in.</summary>
    public const double NoiseDepthCeilingStepDb = 1.0;

    /// <summary>
    /// Clamps a ceiling into the range the rule enforces and snaps it to the slider's step, so a
    /// hand-edited or out-of-range value is corrected rather than discarded. Only a non-finite value
    /// falls back to the default.
    /// </summary>
    public static double NormalizeNoiseDepthCeilingDb(double ceilingDb)
    {
        if (!double.IsFinite(ceilingDb)) return Audio.Dsp.Restoration.NoiseDepthCeilingDb;
        double clamped = Math.Clamp(
            ceilingDb,
            Audio.Dsp.Restoration.MinimumNoiseDepthCeilingDb,
            Audio.Dsp.Restoration.MaximumNoiseDepthCeilingDb);
        return Math.Round(clamped / NoiseDepthCeilingStepDb, MidpointRounding.AwayFromZero)
            * NoiseDepthCeilingStepDb;
    }

    // Autosave
    public bool AutosaveEnabled { get; set; } = true;
    public int AutosaveMinutes { get; set; } = 3;

    // Export defaults
    public string ExportFormat { get; set; } = "wav32";
    public int ExportBitrateKbps { get; set; } = 192;

    /// <summary>
    /// Plugin folders beyond the two Windows defaults. Kept because installers do not agree: the
    /// common-files folder is the convention, and plenty of plugins are somewhere else entirely.
    /// </summary>
    public List<string> Vst3ExtraFolders { get; set; } = [];

    /// <summary>
    /// Where the last impulse response was chosen from. Kept apart from <see cref="LastOpenFolder"/>
    /// because a library of rooms is not where anybody keeps their music.
    /// </summary>
    public string? LastImpulseFolder { get; set; }

    /// <summary>
    /// Plugins that scanned cleanly but are not to be offered in the Add Effect menu.
    /// </summary>
    /// <remarks>
    /// A blocklist rather than an allowlist, so a newly installed plugin appears without being
    /// enabled first — and so this file staying empty means "everything that works", which is what a
    /// user who has never opened the manager should get.
    /// </remarks>
    public List<string> Vst3BlockedPlugins { get; set; } = [];

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

    /// <summary>
    /// One remembered level-check outcome for an input device. The applied-setting
    /// fields are optional so that entries written before they existed still load;
    /// they are null when the outcome was never realized as a device setting, in
    /// which case the memory can be shown but not replayed.
    /// </summary>
    /// <remarks>
    /// These are nullable rather than NaN-sentinelled on purpose: System.Text.Json
    /// refuses to write non-finite doubles, so a NaN here would fail the write of
    /// the <em>entire</em> settings file, not just this entry.
    /// </remarks>
    public sealed record InputCalibrationInfo(
        double SuggestedGainDb,
        double ProgramPeakDb,
        DateTime CheckedUtc,
        double? DeviceLevelDb = null,
        double? FineTrimDb = null,
        double? TotalLevelDb = null)
    {
        /// <summary>True when this entry records a setting that can be restored.</summary>
        [JsonIgnore]
        public bool HasAppliedSetting =>
            DeviceLevelDb is { } device && double.IsFinite(device)
            && FineTrimDb is { } fine && double.IsFinite(fine)
            && TotalLevelDb is { } total && double.IsFinite(total);
    }

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
            // Unique and CreateNew, like the codecs, and flushed before the move. A fixed
            // "<name>.tmp" is shared with any other copy of the app running against the
            // same profile, and File.WriteAllText truncates rather than failing.
            string temporary = Path.Combine(
                AppDataDir, $".settings.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(AppDataDir);
                using (var stream = new FileStream(temporary, FileMode.CreateNew,
                           FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 12, leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(this, JsonOpts));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
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

    /// <summary>
    /// The gate that orders every mutation of the mutable collections below against the
    /// serialize in <see cref="Save"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Save"/> walks the whole object graph. A dictionary or list being edited on
    /// another thread at that moment throws out of the serializer and loses the write for the
    /// entire file, not just that entry. Every reader and writer of the collections goes
    /// through the accessors below rather than touching them directly, because this is read
    /// and written from across the audio layer and the next caller added will not know the
    /// rule. Monitor is reentrant, so an accessor may call <see cref="Save"/> while holding it.
    /// </remarks>
    public static object SyncRoot => SaveLock;

    public bool AddRecentFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (SaveLock)
        {
            var previous = RecentFiles.ToList();
            RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, path);
            if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
            if (Save()) return true;
            RecentFiles = previous;
            return false;
        }
    }

    /// <summary>
    /// Empties the recent-file list, on the same save-or-roll-back contract as
    /// <see cref="AddRecentFile"/>: the list only stays cleared in memory if the file took it,
    /// so a failed write leaves the menu showing what the next launch will still show.
    /// </summary>
    public bool ClearRecentFiles()
    {
        lock (SaveLock)
        {
            if (RecentFiles.Count == 0) return true;
            var previous = RecentFiles.ToList();
            RecentFiles.Clear();
            if (Save()) return true;
            RecentFiles = previous;
            return false;
        }
    }

    /// <summary>A stable copy of the recent-file list, safe to enumerate.</summary>
    public List<string> RecentFilesSnapshot()
    {
        lock (SaveLock) return [.. RecentFiles];
    }

    /// <summary>A stable copy of the extra VST3 scan folders, safe to enumerate.</summary>
    public List<string> Vst3FolderSnapshot()
    {
        lock (SaveLock) return [.. Vst3ExtraFolders ?? []];
    }

    /// <summary>Adds a VST3 scan folder and persists it. False when the write failed.</summary>
    public bool AddVst3Folder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        lock (SaveLock)
        {
            List<string> folders = Vst3ExtraFolders ??= [];
            if (folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return true;
            folders.Add(folder);
            if (Save()) return true;
            folders.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
            return false;
        }
    }

    /// <summary>Removes a VST3 scan folder and persists it. False when the write failed.</summary>
    public bool RemoveVst3Folder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        lock (SaveLock)
        {
            List<string> folders = Vst3ExtraFolders ??= [];
            var previous = folders.ToList();
            if (folders.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)) == 0)
                return true;
            if (Save()) return true;
            Vst3ExtraFolders = previous;
            return false;
        }
    }

    /// <summary>The remembered level-check outcome for a capture device, or null.</summary>
    public InputCalibrationInfo? GetInputCalibration(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        lock (SaveLock)
            return InputCalibrations.TryGetValue(deviceId, out InputCalibrationInfo? info) ? info : null;
    }

    /// <summary>Records a level-check outcome for a capture device and persists it.</summary>
    /// <returns>False when the settings file could not be written; the entry is rolled back.</returns>
    public bool SetInputCalibration(string deviceId, InputCalibrationInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(info);
        lock (SaveLock)
        {
            InputCalibrations.TryGetValue(deviceId, out InputCalibrationInfo? previous);
            InputCalibrations[deviceId] = info;
            if (Save()) return true;
            // Reporting a failure while the live dictionary already holds the new entry would
            // let the next unrelated Save() persist it anyway.
            if (previous == null) InputCalibrations.Remove(deviceId);
            else InputCalibrations[deviceId] = previous;
            return false;
        }
    }

    /// <summary>Forgets the remembered outcome for a capture device and persists the removal.</summary>
    public InputCalibrationRemoval RemoveInputCalibration(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (SaveLock)
        {
            if (!InputCalibrations.Remove(deviceId, out InputCalibrationInfo? removed))
                return InputCalibrationRemoval.NothingRemembered;
            if (Save()) return InputCalibrationRemoval.Removed;
            InputCalibrations[deviceId] = removed;
            return InputCalibrationRemoval.SaveFailed;
        }
    }

    /// <summary>Outcome of <see cref="RemoveInputCalibration"/>.</summary>
    public enum InputCalibrationRemoval
    {
        NothingRemembered,
        Removed,
        SaveFailed,
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
        settings.RecordRunOutHoldSeconds =
            Audio.RunOutDetector.NormalizeHoldSeconds(settings.RecordRunOutHoldSeconds);
        settings.RecordAutoStopMinutes = Math.Clamp(
            settings.RecordAutoStopMinutes,
            ViewModels.RecordViewModel.MinimumAutoStopMinutes,
            ViewModels.RecordViewModel.MaximumAutoStopMinutes);
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
        settings.RecordingTargetCeilingDb =
            NormalizeTargetCeilingDb(settings.RecordingTargetCeilingDb);
        settings.NoiseDepthCeilingDb =
            NormalizeNoiseDepthCeilingDb(settings.NoiseDepthCeilingDb);
        // Entries expire and are capped: a calibration is a statement about a
        // physical setup, and neither a six-month-old one nor an unbounded pile of
        // long-unplugged devices is worth offering back to the user. The optional
        // applied-setting fields are validated only when present, so entries
        // written before they existed survive rather than being silently wiped.
        DateTime now = DateTime.UtcNow;
        settings.InputCalibrations = (settings.InputCalibrations ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                && pair.Value != null
                && double.IsFinite(pair.Value.SuggestedGainDb)
                && double.IsFinite(pair.Value.ProgramPeakDb)
                && pair.Value.CheckedUtc != default
                // Tolerate a clock that ran ahead. An NTP correction, a clock set
                // back, or a settings file carried from a fast machine would
                // otherwise wipe a perfectly good calibration — permanently, since
                // this rewrites the dictionary. Anything further out is corrupt.
                && (pair.Value.CheckedUtc - now).TotalDays <= CalibrationClockSkewDays
                && (now - pair.Value.CheckedUtc).TotalDays <= CalibrationMemoryDays)
            .Select(pair => KeyValuePair.Create(pair.Key, SanitizeCalibration(pair.Value, now)))
            .OrderByDescending(pair => pair.Value.CheckedUtc)
            .Take(MaximumRememberedCalibrations)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!double.IsFinite(settings.WindowWidth) || settings.WindowWidth < 0) settings.WindowWidth = 0;
        if (!double.IsFinite(settings.WindowHeight) || settings.WindowHeight < 0) settings.WindowHeight = 0;
        if (settings.WindowLeft is not { } left || !double.IsFinite(left)) settings.WindowLeft = null;
        if (settings.WindowTop is not { } top || !double.IsFinite(top)) settings.WindowTop = null;
        return settings;
    }

    /// <summary>
    /// Drops a half-written applied setting rather than trusting it: a plan is only
    /// replayable if all three parts agree, and the fine trim has to be a value the
    /// engine would actually accept. A timestamp inside the tolerated clock skew is
    /// pulled back to now, so an entry that survived the filter cannot then report a
    /// negative age to everything downstream.
    /// </summary>
    private static InputCalibrationInfo SanitizeCalibration(InputCalibrationInfo entry, DateTime now)
    {
        if (entry.CheckedUtc > now) entry = entry with { CheckedUtc = now };

        if (!entry.HasAppliedSetting)
        {
            return entry with
            {
                DeviceLevelDb = null,
                FineTrimDb = null,
                TotalLevelDb = null,
            };
        }

        double deviceLevelDb = entry.DeviceLevelDb!.Value;
        double fineTrimDb = Audio.RecordingEngine.NormalizeInputFineTrimDb(entry.FineTrimDb!.Value);
        return entry with
        {
            DeviceLevelDb = deviceLevelDb,
            FineTrimDb = fineTrimDb,
            TotalLevelDb = deviceLevelDb + fineTrimDb,
        };
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
        RecordAutoStopOnRunOut = d.RecordAutoStopOnRunOut;
        RecordRunOutHoldSeconds = d.RecordRunOutHoldSeconds;
        RecordAutoStopOnDuration = d.RecordAutoStopOnDuration;
        RecordAutoStopMinutes = d.RecordAutoStopMinutes;
        RecordingTargetCeilingDb = d.RecordingTargetCeilingDb;
        // Remembered calibrations describe physical inputs, but they are settings
        // the user can only reach through this reset, so it has to clear them.
        InputCalibrations = d.InputCalibrations;
        ReopenLastSession = d.ReopenLastSession;
        UndoLimitMb = d.UndoLimitMb;
        KeepRemovedMaterial = d.KeepRemovedMaterial;
        NoiseDepthCeilingDb = d.NoiseDepthCeilingDb;
        AutosaveEnabled = d.AutosaveEnabled;
        AutosaveMinutes = d.AutosaveMinutes;
        ExportFormat = d.ExportFormat;
        ExportBitrateKbps = d.ExportBitrateKbps;
    }
}
