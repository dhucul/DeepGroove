using System.IO;
using System.Text.Json;
using WaveLab.Util;

namespace WaveLab.Audio.Effects;

/// <summary>Registry of available effect types plus chain preset (de)serialization.</summary>
public static class EffectFactory
{
    public static readonly (string TypeId, string Name)[] Available =
    [
        ("eq", "Studio EQ"),
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
        ("lpf", "Low-Pass Filter"),
        ("hpf", "High-Pass Filter"),
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
        "lpf" => new FilterEffect(highPass: false),
        "hpf" => new FilterEffect(highPass: true),
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
        var result = new List<IAudioEffect>();
        foreach (var state in preset.Effects)
        {
            IAudioEffect fx;
            try { fx = Create(state.TypeId); } catch { continue; }
            fx.Enabled = state.Enabled;
            foreach (var (key, value) in state.Params) fx.SetParam(key, value);
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

    public static void EnsureFactoryPresets()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.PresetsDir);
            WriteIfMissing("Default", [State("eq"), State("limiter")]);
            WriteIfMissing("Podcast Voice",
                [State("hpf", ("cutoff", 80.0)), State("gate", ("thresh", -55.0)),
                 State("eq", ("low", 1.0), ("mid", 1.5), ("high", 2.0)),
                 State("compressor", ("thresh", -20.0), ("ratio", 3.0), ("makeup", 4.0)),
                 State("limiter", ("ceiling", -1.0))]);
            WriteIfMissing("Master Bus",
                [State("eq"), State("compressor", ("thresh", -14.0), ("ratio", 1.8), ("attack", 30.0), ("release", 250.0)),
                 State("limiter", ("thresh", -3.0), ("ceiling", -1.0))]);
            WriteIfMissing("Vocal Space",
                [State("eq", ("high", 1.5)), State("reverb", ("size", 0.55), ("mix", 0.18)), State("limiter")]);
            WriteIfMissing("Vinyl Cleanup",
                [State("hpf", ("cutoff", 28.0), ("q", 0.707)),
                 State("dehum", ("frequency", 60.0), ("harmonics", 6.0), ("q", 40.0), ("amount", 0.75)),
                 State("denoise", ("threshold", -62.0), ("reduction", 8.0), ("hiss", 6.0), ("release", 350.0)),
                 State("eq", ("low", 0.5), ("mid", 0.5), ("high", 1.0)),
                 State("limiter", ("thresh", -1.5), ("ceiling", -1.0))]);
            WriteIfMissing("Mono Record Presence",
                [State("mono-stereo", ("amount", 0.38), ("delay", 11.0), ("bass", 160.0), ("safety", 0.9)),
                 State("stereo-width", ("width", 1.15), ("monoBass", 140.0), ("safety", 1.0)),
                 State("eq", ("low", 0.5), ("mid", 0.8), ("high", 1.2)),
                 State("compressor", ("thresh", -16.0), ("ratio", 1.6), ("attack", 30.0), ("release", 280.0)),
                 State("limiter", ("thresh", -1.0), ("ceiling", -1.0))]);
            WriteIfMissing("Clean Transfer",
                [State("channel-balance"),
                 State("dehum", ("amount", 0.65)),
                 State("denoise", ("threshold", -64.0), ("reduction", 6.0), ("hiss", 5.0), ("release", 400.0)),
                 State("normalizer", ("target", -20.0), ("maxBoost", 3.0), ("maxCut", 6.0), ("gate", -58.0), ("response", 2500.0)),
                 State("trim"), State("limiter", ("ceiling", -1.0))]);
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

    private static void WriteIfMissing(string name, List<EffectState> effects)
    {
        string path = PresetPath(name);
        if (File.Exists(path)) return;
        File.WriteAllText(path, JsonSerializer.Serialize(new ChainPreset { Name = name, Effects = effects }, JsonOpts));
    }

    public static string PresetPath(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(AppSettings.PresetsDir, name + ".chain.json");
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
                    if (preset != null && preset.Effects.Count > 0) result.Add(preset);
                }
                catch { }
            }
        }
        catch { }
        return result.OrderBy(p => p.Name).ToList();
    }

    public static void SavePreset(ChainPreset preset)
    {
        Directory.CreateDirectory(AppSettings.PresetsDir);
        File.WriteAllText(PresetPath(preset.Name), JsonSerializer.Serialize(preset, JsonOpts));
    }
}
