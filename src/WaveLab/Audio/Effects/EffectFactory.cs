using System.IO;
using System.Text;
using System.Text.Json;
using WaveLab.Util;

namespace WaveLab.Audio.Effects;

/// <summary>Registry of available effect types plus chain preset (de)serialization.</summary>
public static class EffectFactory
{
    public static readonly (string TypeId, string Name)[] Available =
    [
        ("eq", "Parametric EQ"),
        ("compressor", "Compressor"),
        ("normalizer", "Level Normalizer"),
        ("trim", "Gain & Trim"),
        ("mono-stereo", "Mono-to-Stereo Enhancer"),
        ("stereo-width", "Stereo Width"),
        ("channel-balance", "Channel Balance & Alignment"),
        ("denoise", "Noise & Hiss Reduction"),
        ("dehum", "Hum Removal"),
        ("gate", "Noise Gate"),
        ("reverb", "Reverb"),
        ("delay", "Stereo Delay"),
        ("chorus", "Chorus"),
        ("saturation", "Saturation"),
        ("filter", "Multi-Mode Filter"),
        ("limiter", "Precision Limiter"),
    ];

    public static IAudioEffect Create(string typeId) => typeId switch
    {
        "eq" => new EqEffect(),
        "compressor" => new CompressorEffect(),
        "normalizer" => new LevelNormalizerEffect(),
        "trim" => new TrimEffect(),
        "mono-stereo" => new MonoToStereoEffect(),
        "stereo-width" => new StereoWidthEffect(),
        "channel-balance" => new ChannelBalanceEffect(),
        "denoise" => new NoiseReductionEffect(),
        "dehum" => new HumRemovalEffect(),
        "gate" => new GateEffect(),
        "reverb" => new ReverbEffect(),
        "delay" => new DelayEffect(),
        "chorus" => new ChorusEffect(),
        "saturation" => new SaturationEffect(),
        "filter" => new FilterEffect(),
        "limiter" => new LimiterEffect(),
        _ => throw new ArgumentException($"Unknown effect type '{typeId}'."),
    };

    // ── chain presets ────────────────────────────────────────────

    public sealed class EffectState
    {
        public string TypeId { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public Dictionary<string, double> Params { get; set; } = [];
    }

    public sealed class ChainPreset
    {
        public string Name { get; set; } = "";
        public List<EffectState> Effects { get; set; } = [];
    }

    public static ChainPreset Capture(string name, IEnumerable<IAudioEffect> chain) => new()
    {
        Name = name,
        Effects = chain.Select(e => new EffectState
        {
            TypeId = e.TypeId,
            Enabled = e.Enabled,
            Params = e.Params.ToDictionary(p => p.Key, p => e.GetParam(p.Key)),
        }).ToList(),
    };

    public static List<IAudioEffect> Instantiate(ChainPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var result = new List<IAudioEffect>();
        if (preset.Effects is not { } states) return result;
        foreach (var state in states)
        {
            if (state is null || !IsKnownTypeId(state.TypeId)) continue;
            IAudioEffect fx;
            try { fx = Create(state.TypeId); } catch { continue; }
            fx.Enabled = state.Enabled;
            if (state.Params is { } parameters)
                foreach (var (key, value) in parameters)
                    if (!string.IsNullOrWhiteSpace(key) && double.IsFinite(value))
                        fx.SetParam(key, value);
            result.Add(fx);
        }
        return result;
    }

    public static IAudioEffect Clone(IAudioEffect source)
    {
        var copy = Create(source.TypeId);
        copy.Enabled = source.Enabled;
        foreach (var p in source.Params) copy.SetParam(p.Key, source.GetParam(p.Key));
        return copy;
    }

    // ── preset files ─────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly string[] FactoryPresetNames =
    [
        "Default", "Podcast Voice", "Master Bus", "Vocal Space", "Vinyl Cleanup",
        "Mono Record Presence", "Clean Transfer",
    ];

    /// <summary>
    /// Build a fresh in-memory copy of a factory preset. Analysis workflows use this
    /// same source of truth without reading or overwriting a user's saved presets.
    /// </summary>
    public static ChainPreset CreateFactoryPreset(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<EffectState> effects = name switch
        {
            "Default" => [State("eq"), DisabledState("limiter")],
            "Podcast Voice" =>
                [State("filter", ("mode", 1.0), ("cutoff", 80.0), ("slope", 0.0)),
                 State("gate", ("thresh", -55.0)),
                 State("eq", ("lowGain", 1.0), ("midGain", 1.5), ("highGain", 2.0)),
                 State("compressor", ("thresh", -20.0), ("ratio", 3.0), ("makeup", 4.0)),
                 State("limiter", ("ceiling", -1.0))],
            "Master Bus" =>
                [State("eq"), State("compressor", ("thresh", -14.0), ("ratio", 1.8),
                     ("attack", 30.0), ("release", 250.0)),
                 State("limiter", ("thresh", -3.0), ("ceiling", -1.0))],
            "Vocal Space" =>
                [State("eq", ("highGain", 1.5)), State("reverb", ("size", 0.55), ("mix", 0.18)),
                 State("limiter")],
            "Vinyl Cleanup" =>
                [State("filter", ("mode", 1.0), ("cutoff", 28.0), ("q", 0.707), ("slope", 0.0)),
                 State("dehum", ("frequency", 60.0), ("harmonics", 6.0), ("q", 40.0), ("amount", 0.75)),
                 State("denoise", ("threshold", -62.0), ("reduction", 8.0), ("hiss", 6.0), ("release", 350.0)),
                 State("eq", ("lowGain", 0.5), ("midGain", 0.5), ("highGain", 1.0)),
                 State("limiter", ("thresh", -1.5), ("ceiling", -1.0))],
            "Mono Record Presence" =>
                [State("mono-stereo", ("amount", 0.38), ("delay", 11.0), ("bass", 160.0), ("safety", 0.9)),
                 State("stereo-width", ("width", 1.15), ("monoBass", 140.0), ("safety", 1.0)),
                 State("eq", ("lowGain", 0.5), ("midGain", 0.8), ("highGain", 1.2)),
                 State("compressor", ("thresh", -16.0), ("ratio", 1.6), ("attack", 30.0), ("release", 280.0)),
                 State("limiter", ("thresh", -1.0), ("ceiling", -1.0))],
            "Clean Transfer" =>
                [State("channel-balance"),
                 State("dehum", ("amount", 0.65)),
                 State("denoise", ("threshold", -64.0), ("reduction", 6.0), ("hiss", 5.0), ("release", 400.0)),
                 State("trim"),
                 State("normalizer", ("target", -20.0), ("maxBoost", 3.0), ("maxCut", 6.0),
                     ("gate", -58.0), ("response", 2500.0)),
                 State("limiter", ("ceiling", -1.0))],
            _ => throw new ArgumentException($"Unknown factory preset '{name}'.", nameof(name)),
        };
        return new ChainPreset { Name = name, Effects = effects };
    }

    public static void EnsureFactoryPresets() => EnsureFactoryPresets(AppSettings.PresetsDir);

    /// <summary>
    /// Publish factory presets into a specific directory. Existing presets are
    /// preserved unless they are an exact semantic match for the previous
    /// generated factory payload, in which case they are safely upgraded.
    /// </summary>
    public static void EnsureFactoryPresets(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            Directory.CreateDirectory(directory);
            foreach (string name in FactoryPresetNames)
            {
                ChainPreset preset = CreateFactoryPreset(name);
                string path = PresetPath(directory, preset.Name);
                if (!File.Exists(path))
                {
                    WritePresetAtomically(preset, path, overwrite: false);
                    continue;
                }

                TryUpgradeLegacyFactoryPreset(path, name, preset);
            }
        }
        catch { }
    }

    private static EffectState State(string typeId, params (string Key, double Value)[] overrides)
    {
        var fx = Create(typeId);
        var state = new EffectState
        {
            TypeId = typeId,
            Params = fx.Params.ToDictionary(p => p.Key, p => e(fx, p.Key)),
        };
        foreach (var (key, value) in overrides) state.Params[key] = value;
        return state;

        static double e(IAudioEffect fx, string key) => fx.GetParam(key);
    }

    private static EffectState DisabledState(string typeId)
    {
        EffectState state = State(typeId);
        state.Enabled = false;
        return state;
    }

    private static void TryUpgradeLegacyFactoryPreset(
        string path,
        string name,
        ChainPreset currentFactoryPreset)
    {
        try
        {
            ChainPreset? existing = JsonSerializer.Deserialize<ChainPreset>(File.ReadAllText(path));
            if (existing == null) return;
            ChainPreset legacy = CreateLegacyFactoryPreset(name);
            ChainPreset previous = CreatePreviousFactoryPreset(name);
            if ((PresetStatesEqual(existing, legacy) || PresetStatesEqual(existing, previous)) &&
                !PresetStatesEqual(existing, currentFactoryPreset))
                WritePresetAtomically(currentFactoryPreset, path, overwrite: true);
        }
        catch
        {
            // A malformed, locked, or user-managed file is never replaced.
        }
    }

    public static string PresetPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A preset name is required.", nameof(name));
        name = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return PresetPath(AppSettings.PresetsDir, name);
    }

    private static string PresetPath(string directory, string name)
    {
        name = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(directory, name + ".chain.json");
    }

    private static ChainPreset CreateLegacyFactoryPreset(string name)
    {
        ChainPreset legacy = CreateFactoryPreset(name);
        if (name is "Default" or "Podcast Voice" or "Vocal Space" or "Clean Transfer")
        {
            EffectState? limiter = legacy.Effects.FirstOrDefault(effect => effect.TypeId == "limiter");
            if (limiter != null)
            {
                limiter.Enabled = true;
                limiter.Params["thresh"] = 0;
            }
        }

        if (name == "Clean Transfer")
        {
            int trimIndex = legacy.Effects.FindIndex(effect => effect.TypeId == "trim");
            int normalizerIndex = legacy.Effects.FindIndex(effect => effect.TypeId == "normalizer");
            if (trimIndex >= 0 && normalizerIndex >= 0 && trimIndex < normalizerIndex)
            {
                EffectState trim = legacy.Effects[trimIndex];
                legacy.Effects.RemoveAt(trimIndex);
                normalizerIndex = legacy.Effects.FindIndex(effect => effect.TypeId == "normalizer");
                legacy.Effects.Insert(normalizerIndex + 1, trim);
            }
        }

        return legacy;
    }

    private static ChainPreset CreatePreviousFactoryPreset(string name)
    {
        ChainPreset previous = CreateFactoryPreset(name);
        if (name == "Default")
        {
            EffectState? limiter = previous.Effects.FirstOrDefault(effect => effect.TypeId == "limiter");
            if (limiter != null) limiter.Enabled = true;
        }
        return previous;
    }

    private static bool PresetStatesEqual(ChainPreset left, ChainPreset right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Effects == null || right.Effects == null ||
            left.Effects.Count != right.Effects.Count)
            return false;

        for (int index = 0; index < left.Effects.Count; index++)
        {
            EffectState a = left.Effects[index];
            EffectState b = right.Effects[index];
            if (a == null || b == null ||
                !string.Equals(a.TypeId, b.TypeId, StringComparison.Ordinal) ||
                a.Enabled != b.Enabled ||
                a.Params == null || b.Params == null ||
                a.Params.Count != b.Params.Count)
                return false;

            foreach ((string key, double value) in a.Params)
                if (!b.Params.TryGetValue(key, out double other) || value != other)
                    return false;
        }

        return true;
    }

    public static List<ChainPreset> LoadPresets()
    {
        var result = new List<ChainPreset>();
        try
        {
            if (!Directory.Exists(AppSettings.PresetsDir)) return result;
            foreach (var file in Directory.GetFiles(AppSettings.PresetsDir, "*.chain.json"))
            {
                try
                {
                    var preset = JsonSerializer.Deserialize<ChainPreset>(File.ReadAllText(file));
                    if (preset == null || !IsValidPresetName(preset.Name)) continue;
                    var effects = Instantiate(preset);
                    if (effects.Count > 0) result.Add(Capture(preset.Name.Trim(), effects));
                }
                catch { }
            }
        }
        catch { }
        return result.OrderBy(p => p.Name).ToList();
    }

    public static void SavePreset(ChainPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!IsValidPresetName(preset.Name))
            throw new ArgumentException("A valid preset name is required.", nameof(preset));
        var normalized = Capture(preset.Name.Trim(), Instantiate(preset));
        if (normalized.Effects.Count == 0)
            throw new ArgumentException("A preset must contain at least one valid effect.", nameof(preset));
        WritePresetAtomically(normalized, PresetPath(normalized.Name), overwrite: true);
    }

    private static void WritePresetAtomically(ChainPreset preset, string finalPath, bool overwrite)
    {
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The preset path has no directory.");
        Directory.CreateDirectory(directory);
        string stagePath = Path.Combine(directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        string json = JsonSerializer.Serialize(preset, JsonOpts);
        try
        {
            using (var stream = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(stagePath, finalPath, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(finalPath))
        {
            // Another startup published the same deterministic factory preset first.
        }
        finally
        {
            try { File.Delete(stagePath); } catch { }
        }
    }

    private static bool IsKnownTypeId(string? typeId) =>
        !string.IsNullOrWhiteSpace(typeId) && Available.Any(item => item.TypeId == typeId);

    private static bool IsValidPresetName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 128;
}
