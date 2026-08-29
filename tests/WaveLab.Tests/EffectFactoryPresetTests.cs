using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.ViewModels;
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
    public void VinylCleanupWaitsForAnalysisBeforeEnablingDefectSpecificStages()
    {
        EffectFactory.ChainPreset preset = EffectFactory.CreateFactoryPreset("Vinyl Cleanup");

        Assert.False(State(preset, "filter").Enabled);
        Assert.False(State(preset, "dehum").Enabled);
        Assert.False(State(preset, "denoise").Enabled);
        Assert.Equal(1, Param(State(preset, "filter"), "phase"));
        Assert.Equal(1, Param(State(preset, "filter"), "slope"));
    }

    [Fact]
    public void UntouchedPreConsistencyVinylPresetIsSafelyUpgraded()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.ChainPreset previous = EffectFactory.CreateFactoryPreset("Vinyl Cleanup");
            foreach (EffectFactory.EffectState state in previous.Effects.Where(effect =>
                         effect.TypeId is "filter" or "dehum" or "denoise"))
                state.Enabled = true;
            EffectFactory.EffectState filter = State(previous, "filter");
            filter.Params["slope"] = 0;
            filter.Params.Remove("phase");
            State(previous, "dehum").Params.Remove("harmonicMask");
            string path = Path.Combine(directory, previous.Name + ".chain.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(previous));

            EffectFactory.EnsureFactoryPresets(directory);

            EffectFactory.ChainPreset upgraded =
                System.Text.Json.JsonSerializer.Deserialize<EffectFactory.ChainPreset>(
                    File.ReadAllText(path))!;
            Assert.False(State(upgraded, "filter").Enabled);
            Assert.False(State(upgraded, "dehum").Enabled);
            Assert.False(State(upgraded, "denoise").Enabled);
            Assert.Equal(1, Param(State(upgraded, "filter"), "phase"));
            Assert.True(State(upgraded, "dehum").Params.ContainsKey("harmonicMask"));
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
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
    public void RecordToCdPresetsProduceAudiblyDistinctToneShaping()
    {
        double gentleContrast = ToneContrastDb("Record to CD - Gentle Clarity");
        double rescueContrast = ToneContrastDb("Record to CD - Dull Source Rescue");
        double warmContrast = ToneContrastDb("Record to CD - Warm Record Open-Up");

        Assert.True(gentleContrast >= 2.0, $"Gentle contrast was only {gentleContrast:0.0} dB.");
        Assert.True(rescueContrast >= gentleContrast + 2.0,
            $"Rescue {rescueContrast:0.0} dB was not clearly stronger than gentle {gentleContrast:0.0} dB.");
        Assert.True(Math.Abs(warmContrast - rescueContrast) >= 1.0,
            $"Warm {warmContrast:0.0} dB was too similar to rescue {rescueContrast:0.0} dB.");
    }

    [Fact]
    public void LoadingAStoredPresetActivatesABypassedRack()
    {
        var master = new MasterSection { RackEnabled = false };
        var viewModel = new MasterSectionViewModel(master);

        viewModel.ApplyStoredPreset(
            EffectFactory.CreateFactoryPreset("Record to CD - Dull Source Rescue"));

        Assert.True(master.RackEnabled);
        Assert.Equal("ACTIVE", viewModel.RackStateText);
        Assert.Contains("rack activated", viewModel.RackStatusText);
    }

    [Fact]
    public void MissingStoredPresetKeepsThePreviousSelectionAndRack()
    {
        var master = new MasterSection();
        var viewModel = new MasterSectionViewModel(master);
        string[] originalTypes = master.ChainSnapshot.Select(effect => effect.TypeId).ToArray();

        viewModel.SelectedPreset = $"Missing {Guid.NewGuid():N}";

        Assert.Null(viewModel.SelectedPreset);
        Assert.Equal(originalTypes, master.ChainSnapshot.Select(effect => effect.TypeId));
        Assert.Contains("unavailable", viewModel.RackStatusText);
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
    public void OneBlockedFactoryPresetDoesNotSuppressLaterPresets()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string blocker = Path.Combine(directory, "Default.chain.json");
        Directory.CreateDirectory(blocker);
        try
        {
            EffectFactory.EnsureFactoryPresets(directory);

            Assert.True(File.Exists(Path.Combine(directory, "Podcast Voice.chain.json")));
            Assert.True(File.Exists(Path.Combine(directory, "Record to CD - Warm Record Open-Up.chain.json")));
        }
        finally
        {
            Directory.Delete(blocker);
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void UntouchedPreEqCleanTransferIsUpgraded()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.ChainPreset previous = EffectFactory.CreateFactoryPreset("Clean Transfer");
            previous.Effects.Remove(State(previous, "eq"));
            string factoryPath = Path.Combine(directory, "Clean Transfer.chain.json");
            File.WriteAllText(factoryPath, System.Text.Json.JsonSerializer.Serialize(previous));

            EffectFactory.EnsureFactoryPresets(directory);

            string upgraded = File.ReadAllText(factoryPath);
            Assert.Contains("\"TypeId\": \"eq\"", upgraded);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void CustomizedFactoryNamedCleanTransferIsPreservedByteForByte()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.ChainPreset customized = EffectFactory.CreateFactoryPreset("Clean Transfer");
            customized.Effects.Remove(State(customized, "eq"));
            State(customized, "denoise").Params["reduction"] = 2.5;
            string path = Path.Combine(directory, "Clean Transfer.chain.json");
            string before = System.Text.Json.JsonSerializer.Serialize(customized);
            File.WriteAllText(path, before);

            EffectFactory.EnsureFactoryPresets(directory);

            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void FirstGenerationDullSourcePresetIsUpgradedToAudibleSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.ChainPreset previous =
                EffectFactory.CreateFactoryPreset("Record to CD - Dull Source Rescue");
            EffectFactory.EffectState oldFilter = State(previous, "filter");
            oldFilter.Params["slope"] = 0;
            oldFilter.Params.Remove("phase");
            EffectFactory.EffectState eq = State(previous, "eq");
            eq.Params["lowGain"] = -0.8;
            eq.Params["lmGain"] = -1.8;
            eq.Params["midGain"] = 0.8;
            eq.Params["hmGain"] = 2.5;
            eq.Params["highGain"] = 3.2;
            State(previous, "trim").Params["gain"] = -3.0;
            string path = Path.Combine(directory, previous.Name + ".chain.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(previous));

            EffectFactory.EnsureFactoryPresets(directory);

            EffectFactory.ChainPreset upgraded =
                System.Text.Json.JsonSerializer.Deserialize<EffectFactory.ChainPreset>(File.ReadAllText(path))!;
            Assert.Equal(3.8, Param(State(upgraded, "eq"), "hmGain"));
            Assert.Equal(5.0, Param(State(upgraded, "eq"), "highGain"));
            Assert.Equal(-1.5, Param(State(upgraded, "trim"), "gain"));
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

    private static double ToneContrastDb(string presetName) =>
        MeasureGainDb(presetName, 3_600) - MeasureGainDb(presetName, 330);

    private static double MeasureGainDb(string presetName, double frequency)
    {
        const int sampleRate = 48_000;
        const int frames = sampleRate;
        var input = new float[2][];
        for (int channel = 0; channel < input.Length; channel++)
        {
            input[channel] = new float[frames];
            for (int frame = 0; frame < frames; frame++)
                input[channel][frame] = (float)(0.05 * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
        }

        var rack = new MasterSection();
        rack.ReplaceChain(EffectFactory.Instantiate(EffectFactory.CreateFactoryPreset(presetName)));
        float[][] output = rack.ProcessOffline(input, sampleRate);
        int start = sampleRate / 4;
        double inputRms = Rms(input[0], start);
        double outputRms = Rms(output[0], start);
        return 20 * Math.Log10(outputRms / inputRms);

        static double Rms(float[] samples, int start)
        {
            double sum = 0;
            for (int index = start; index < samples.Length; index++) sum += samples[index] * samples[index];
            return Math.Sqrt(sum / (samples.Length - start));
        }
    }
}
