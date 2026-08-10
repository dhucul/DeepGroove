using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

public sealed class EffectFactoryPresetTests
{
    public static TheoryData<string> AllFactoryPresetNames => new()
    {
        "Default",
        "Podcast Voice",
        "Master Bus",
        "Vocal Space",
        "Vinyl Cleanup",
        "Mono Record Presence",
        "Clean Transfer",
        "Record to CD - Gentle Clarity",
        "Record to CD - Dull Source Rescue",
        "Record to CD - Warm Record Open-Up",
    };

    private static readonly string[] RecordToCdNames =
    [
        "Record to CD - Gentle Clarity",
        "Record to CD - Dull Source Rescue",
        "Record to CD - Warm Record Open-Up",
    ];

    public static TheoryData<string> RecordToCdPresetNames => new()
    {
        RecordToCdNames[0],
        RecordToCdNames[1],
        RecordToCdNames[2],
    };

    [Theory]
    [MemberData(nameof(RecordToCdPresetNames))]
    public void RecordToCdPresetsUseFiveBandEqWithConservativeOutputProtection(string name)
    {
        EffectFactory.ChainPreset preset = EffectFactory.CreateFactoryPreset(name);
        EffectFactory.EffectState eq = State(preset, "eq");
        EffectFactory.EffectState trim = State(preset, "trim");
        EffectFactory.EffectState limiter = State(preset, "limiter");

        Assert.True(eq.Enabled);
        Assert.All((string[])["lowGain", "lmGain", "midGain", "hmGain", "highGain"],
            key => Assert.True(eq.Params.ContainsKey(key), $"Missing EQ band '{key}'."));
        Assert.True(Param(trim, "gain") < 0);
        Assert.Equal(0, Param(limiter, "thresh"));
        Assert.Equal(-1, Param(limiter, "ceiling"));
        Assert.Equal(1, Param(limiter, "oversample"));
        Assert.Equal("limiter", preset.Effects[^1].TypeId);
        Assert.DoesNotContain(preset.Effects,
            effect => effect.TypeId is "denoise" or "compressor" or "normalizer");
        AssertPresetParametersAreFiniteAndInRange(preset);
        Assert.Equal(preset.Effects.Count, EffectFactory.Instantiate(preset).Count);
    }

    [Theory]
    [MemberData(nameof(AllFactoryPresetNames))]
    public void EveryFactoryRackPresetCanBeLoadedBeforeAudioConfiguration(string name)
    {
        EffectFactory.ChainPreset preset = EffectFactory.CreateFactoryPreset(name);

        List<IAudioEffect> loaded = EffectFactory.Instantiate(preset);

        Assert.Equal(preset.Effects.Count, loaded.Count);
        Assert.Equal(preset.Effects.Select(effect => effect.TypeId), loaded.Select(effect => effect.TypeId));
    }

    [Fact]
    public void EveryAvailableRackEffectCanRoundTripThroughPresetLoading()
    {
        var rack = EffectFactory.Available
            .Select(item => EffectFactory.Create(item.TypeId))
            .ToList();
        EffectFactory.ChainPreset captured = EffectFactory.Capture("Complete Rack", rack);

        List<IAudioEffect> loaded = EffectFactory.Instantiate(captured);

        Assert.Equal(EffectFactory.Available.Length, loaded.Count);
        Assert.Equal(EffectFactory.Available.Select(item => item.TypeId),
            loaded.Select(effect => effect.TypeId));
    }

    [Fact]
    public void DullSourceRescueAddsMorePresenceAndAirThanGentleClarity()
    {
        EffectFactory.EffectState gentle = State(
            EffectFactory.CreateFactoryPreset("Record to CD - Gentle Clarity"), "eq");
        EffectFactory.EffectState rescue = State(
            EffectFactory.CreateFactoryPreset("Record to CD - Dull Source Rescue"), "eq");

        Assert.True(Param(rescue, "hmGain") > Param(gentle, "hmGain"));
        Assert.True(Param(rescue, "highGain") > Param(gentle, "highGain"));
        Assert.True(Param(rescue, "lmGain") < Param(gentle, "lmGain"));
    }

    [Fact]
    public void CleanTransferNowIncludesPresenceEqBeforeGainManagement()
    {
        EffectFactory.ChainPreset preset = EffectFactory.CreateFactoryPreset("Clean Transfer");
        int eqIndex = preset.Effects.FindIndex(effect => effect.TypeId == "eq");
        int trimIndex = preset.Effects.FindIndex(effect => effect.TypeId == "trim");
        int normalizerIndex = preset.Effects.FindIndex(effect => effect.TypeId == "normalizer");
        EffectFactory.EffectState eq = State(preset, "eq");

        Assert.InRange(eqIndex, 0, int.MaxValue);
        Assert.True(eqIndex < trimIndex);
        Assert.True(eqIndex < normalizerIndex);
        Assert.True(Param(eq, "hmGain") > 0);
        Assert.True(Param(eq, "highGain") > 0);
    }

    [Fact]
    public void FactoryPublishingIncludesEveryRecordToCdPreset()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.EnsureFactoryPresets(directory);

            foreach (string name in RecordToCdNames)
                Assert.True(File.Exists(Path.Combine(directory, name + ".chain.json")), name);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void UntouchedPreEqCleanTransferIsUpgradedWithoutOverwritingCustomPresets()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.ChainPreset previous = EffectFactory.CreateFactoryPreset("Clean Transfer");
            previous.Effects.Remove(State(previous, "eq"));
            string factoryPath = Path.Combine(directory, "Clean Transfer.chain.json");
            File.WriteAllText(factoryPath, System.Text.Json.JsonSerializer.Serialize(previous));

            EffectFactory.ChainPreset customized = EffectFactory.CreateFactoryPreset("Clean Transfer");
            customized.Effects.Remove(State(customized, "eq"));
            State(customized, "denoise").Params["reduction"] = 2.5;
            customized.Name = "My Clean Transfer";
            string customPath = Path.Combine(directory, "My Clean Transfer.chain.json");
            File.WriteAllText(customPath, System.Text.Json.JsonSerializer.Serialize(customized));

            EffectFactory.EnsureFactoryPresets(directory);

            string upgraded = File.ReadAllText(factoryPath);
            string preserved = File.ReadAllText(customPath);
            Assert.Contains("\"TypeId\": \"eq\"", upgraded);
            Assert.DoesNotContain("\"TypeId\": \"eq\"", preserved);
            Assert.Contains("2.5", preserved);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static EffectFactory.EffectState State(EffectFactory.ChainPreset preset, string typeId) =>
        Assert.Single(preset.Effects, effect => effect.TypeId == typeId);

    private static double Param(EffectFactory.EffectState state, string key) =>
        Assert.Contains(key, state.Params);

    private static void AssertPresetParametersAreFiniteAndInRange(EffectFactory.ChainPreset preset)
    {
        foreach (EffectFactory.EffectState state in preset.Effects)
        {
            IAudioEffect effect = EffectFactory.Create(state.TypeId);
            foreach (EffectParam descriptor in effect.Params)
            {
                double value = Param(state, descriptor.Key);
                Assert.True(double.IsFinite(value), $"{state.TypeId}.{descriptor.Key} is not finite.");
                Assert.InRange(value, descriptor.Min, descriptor.Max);
            }
        }
    }
}
