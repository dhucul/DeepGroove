using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.ViewModels;
using Xunit;
using System.Text.Json;

namespace WaveLab.Tests;

public sealed class CleanupAnalyzerTests
{
    private const int SampleRate = 48_000;

    [Theory]
    [InlineData(64)]
    [InlineData(SampleRate * 2)]
    public void SilenceAndShortInputsReturnSafeFinitePresets(int sampleCount)
    {
        float[][] input = [new float[sampleCount], new float[sampleCount]];

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);

        Assert.Equal(CleanupProfile.VinylCleanup, result.Profile);
        Assert.NotNull(result.BaselinePreset);
        Assert.NotNull(result.RecommendedPreset);
        Assert.NotEmpty(result.RecommendedPreset.Effects);
        AssertPresetIsFiniteAndClamped(result.RecommendedPreset);
        foreach (string typeId in (string[])["filter", "dehum", "denoise", "eq"])
            Assert.False(State(result.RecommendedPreset, typeId).Enabled,
                $"{typeId} should remain bypassed for digital silence.");

        Assert.Equal(0, Param(State(result.RecommendedPreset, "limiter"), "thresh"));
        Assert.Equal(-1, Param(State(result.RecommendedPreset, "limiter"), "ceiling"));
    }

    [Fact]
    public void SixtyHertzHumAndHarmonicsEnableA60HzDehumRecommendation()
    {
        float[][] input = StereoSignal(12, (time, channel) =>
        {
            double hum = 0.055 * Math.Sin(2 * Math.PI * 60 * time)
                       + 0.032 * Math.Sin(2 * Math.PI * 120 * time)
                       + 0.020 * Math.Sin(2 * Math.PI * 180 * time)
                       + 0.012 * Math.Sin(2 * Math.PI * 240 * time);
            double program = 0.008 * Math.Sin(2 * Math.PI * (channel == 0 ? 437 : 523) * time);
            return hum + program;
        });

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);
        EffectFactory.EffectState dehum = State(result.RecommendedPreset, "dehum");

        Assert.True(dehum.Enabled);
        Assert.InRange(Param(dehum, "frequency"), 59.0, 61.0);
        Assert.InRange(Param(dehum, "harmonics"), 2.0, 12.0);
        Assert.True(Param(dehum, "amount") > 0.15);
        Assert.True(Recommendation(result, "dehum").Confidence >= 0.55);
    }

    [Fact]
    public void FiftyHertzHumAndHarmonicsEnableA50HzDehumRecommendation()
    {
        float[][] input = StereoSignal(12, (time, channel) =>
        {
            double hum = 0.050 * Math.Sin(2 * Math.PI * 50 * time)
                       + 0.030 * Math.Sin(2 * Math.PI * 100 * time)
                       + 0.018 * Math.Sin(2 * Math.PI * 150 * time)
                       + 0.010 * Math.Sin(2 * Math.PI * 200 * time);
            double program = 0.007 * Math.Sin(2 * Math.PI * (channel == 0 ? 431 : 521) * time);
            return hum + program;
        });

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);
        EffectFactory.EffectState dehum = State(result.RecommendedPreset, "dehum");

        Assert.True(dehum.Enabled);
        Assert.InRange(Param(dehum, "frequency"), 49.0, 51.0);
        Assert.True(Param(dehum, "harmonics") >= 2);
        Assert.True(Recommendation(result, "dehum").Confidence >= 0.55);
    }

    [Fact]
    public void TonalProgramWithoutMainsCombDoesNotEnableHumRemoval()
    {
        uint noiseState = 0xA341_316Cu;
        float[][] input = StereoSignal(12, (time, channel) =>
        {
            noiseState = noiseState * 1_664_525u + 1_013_904_223u;
            double noise = ((noiseState >> 8) / 16_777_215.0 * 2 - 1) * 0.0015;
            // Repeated A1 bass notes have a genuine 55 Hz harmonic series, but unlike
            // mains hum it is intermittent and belongs to the musical program.
            double notePosition = time % 0.8;
            double envelope = notePosition < 0.22
                ? 0.5 - 0.5 * Math.Cos(2 * Math.PI * notePosition / 0.22)
                : 0;
            double bass = envelope * (0.10 * Math.Sin(2 * Math.PI * 55 * time)
                                    + 0.060 * Math.Sin(2 * Math.PI * 110 * time)
                                    + 0.035 * Math.Sin(2 * Math.PI * 165 * time)
                                    + 0.020 * Math.Sin(2 * Math.PI * 220 * time));
            double program = 0.035 * Math.Sin(2 * Math.PI * (channel == 0 ? 437 : 523) * time);
            return bass + program + noise;
        });

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);
        EffectFactory.EffectState dehum = State(result.RecommendedPreset, "dehum");

        Assert.False(dehum.Enabled);
        Assert.True(Recommendation(result, "dehum").Confidence < 0.65);
    }

    [Fact]
    public void StrongSubsonicRumbleRaisesTheHighPassCutoff()
    {
        float[][] input = StereoSignal(12, (time, channel) =>
            0.13 * Math.Sin(2 * Math.PI * 14 * time + channel * 0.37)
          + 0.045 * Math.Sin(2 * Math.PI * 440 * time)
          + 0.025 * Math.Sin(2 * Math.PI * 131 * time));

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);
        EffectFactory.EffectState highPass = State(result.RecommendedPreset, "filter");

        Assert.True(highPass.Enabled);
        Assert.Equal(1, Param(highPass, "mode")); // HP mode of the unified Multi-Mode Filter
        Assert.InRange(Param(highPass, "cutoff"), 28.0, 45.0);
        Assert.InRange(Param(highPass, "q"), 0.65, 0.75);
        Assert.True(Recommendation(result, "filter").Confidence >= 0.55);

    }

    [Fact]
    public void CleanTransferCorrectsAnObviousStereoLevelImbalance()
    {
        float[][] input = StereoSignal(10, (time, channel) =>
        {
            double shared = 0.18 * Math.Sin(2 * Math.PI * 311 * time)
                          + 0.07 * Math.Sin(2 * Math.PI * 877 * time);
            return shared * (channel == 0 ? 1.0 : 0.25);
        });

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.CleanTransfer);
        EffectFactory.EffectState balance = State(result.RecommendedPreset, "channel-balance");

        Assert.True(balance.Enabled);
        Assert.InRange(Param(balance, "balance"), 3.0, 12.0);
        Assert.True(Recommendation(result, "channel-balance").Confidence >= 0.55);
    }

    [Fact]
    public void CleanTransferDelaysTheLeadingChannelToCorrectRightChannelLatency()
    {
        const int delaySamples = 48; // 1.00 ms at 48 kHz
        int length = SampleRate * 4;
        float[][] input = [new float[length], new float[length]];
        uint state = 0xC001_D00Du;
        for (int index = 0; index < length - delaySamples; index++)
        {
            state = state * 1_664_525u + 1_013_904_223u;
            float sample = (float)(((state >> 8) / 16_777_215.0 * 2 - 1) * 0.12);
            input[0][index] = sample;
            input[1][index + delaySamples] = sample;
        }

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.CleanTransfer);
        EffectFactory.EffectState balance = State(result.RecommendedPreset, "channel-balance");

        Assert.True(balance.Enabled);
        // The right channel is already late, so the effect must delay the left channel:
        // ChannelBalanceEffect represents that correction with a negative alignment.
        Assert.InRange(Param(balance, "align"), -1.10, -0.90);
        Assert.True(Recommendation(result, "channel-balance").Confidence >= 0.55);
    }

    [Fact]
    public void CleanTransferDoesNotTreatAnInvertedPeriodicToneAsChannelLatency()
    {
        float[][] input = StereoSignal(4, (time, channel) =>
        {
            double tone = 0.16 * Math.Sin(2 * Math.PI * 440 * time);
            return channel == 0 ? tone : -tone;
        });

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.CleanTransfer);
        EffectFactory.EffectState balance = State(result.RecommendedPreset, "channel-balance");

        Assert.Equal(0, Param(balance, "align"));
        Assert.False(balance.Enabled);
    }

    [Fact]
    public void DefaultFactoryPresetStartsWithLimiterBypassed()
    {
        EffectFactory.ChainPreset preset = EffectFactory.CreateFactoryPreset("Default");

        Assert.False(State(preset, "limiter").Enabled);
        Assert.True(State(preset, "eq").Enabled);
    }

    [Fact]
    public void FreshMasterRackStartsWithLimiterBypassed()
    {
        var master = new MasterSection();

        IAudioEffect limiter = Assert.Single(master.ChainSnapshot, effect => effect.TypeId == "limiter");
        Assert.False(limiter.Enabled);
    }

    [Fact]
    public void ResetMasterRackKeepsLimiterBypassed()
    {
        var master = new MasterSection();
        var viewModel = new MasterSectionViewModel(master);
        master.SetEffectEnabled(
            Assert.Single(master.ChainSnapshot, effect => effect.TypeId == "limiter"),
            enabled: true);

        viewModel.ResetChainCommand.Execute(null);

        IAudioEffect limiter = Assert.Single(master.ChainSnapshot, effect => effect.TypeId == "limiter");
        Assert.False(limiter.Enabled);
    }

    [Fact]
    public void CleanTransferAppliesInputTrimBeforeLevelNormalization()
    {
        EffectFactory.ChainPreset factory = EffectFactory.CreateFactoryPreset("Clean Transfer");
        int trimIndex = factory.Effects.FindIndex(effect => effect.TypeId == "trim");
        int normalizerIndex = factory.Effects.FindIndex(effect => effect.TypeId == "normalizer");

        Assert.True(trimIndex >= 0);
        Assert.True(normalizerIndex >= 0);
        Assert.True(trimIndex < normalizerIndex);

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            StereoSignal(2, (time, _) => 0.8 * Math.Sin(2 * Math.PI * 997 * time)),
            SampleRate,
            CleanupProfile.CleanTransfer);
        Assert.Equal(factory.Effects.Select(effect => effect.TypeId),
            result.RecommendedPreset.Effects.Select(effect => effect.TypeId));
    }

    [Theory]
    [InlineData(CleanupProfile.VinylCleanup)]
    [InlineData(CleanupProfile.CleanTransfer)]
    public void EveryRecommendedParameterIsFiniteAndWithinItsEffectDescriptor(CleanupProfile profile)
    {
        float[][] input = StereoSignal(6, (time, channel) =>
            0.12 * Math.Sin(2 * Math.PI * (channel == 0 ? 223 : 227) * time)
          + 0.03 * Math.Sin(2 * Math.PI * 61 * time));

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(input, SampleRate, profile);

        AssertPresetIsFiniteAndClamped(result.RecommendedPreset);
    }

    [Fact]
    public void LowestSupportedSampleRateReturnsFiniteParametersWithoutInventingUltrasonicEq()
    {
        const int lowSampleRate = 8_000;
        float[][] input = StereoSignal(4, (time, channel) =>
            0.12 * Math.Sin(2 * Math.PI * (channel == 0 ? 311 : 347) * time), lowSampleRate);

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, lowSampleRate, CleanupProfile.VinylCleanup);

        AssertPresetIsFiniteAndClamped(result.RecommendedPreset);
        Assert.Equal(0, Param(State(result.RecommendedPreset, "eq"), "highGain"));

    }

    [Fact]
    public void NonFiniteSamplesProduceAFiniteConservativePreset()
    {
        int length = SampleRate * 2;
        float[][] input = [new float[length], new float[length]];
        for (int channel = 0; channel < input.Length; channel++)
            for (int index = 0; index < length; index++)
                input[channel][index] = ((index + channel) % 3) switch
                {
                    0 => float.NaN,
                    1 => float.PositiveInfinity,
                    _ => float.NegativeInfinity,
                };
        Assert.DoesNotContain(input.SelectMany(channel => channel), float.IsFinite);

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);

        AssertPresetIsFiniteAndClamped(result.RecommendedPreset);
        foreach (string typeId in (string[])["filter", "dehum", "denoise", "eq"])
            Assert.False(State(result.RecommendedPreset, typeId).Enabled,
                $"{typeId} should remain bypassed when every input sample is non-finite.");

        Assert.Equal(0, Param(State(result.RecommendedPreset, "limiter"), "thresh"));
    }

    [Fact]
    public void ApplyingEveryDefaultRecommendationToSilenceKeepsCleanupProcessorsBypassed()
    {
        float[][] input = [new float[SampleRate * 2], new float[SampleRate * 2]];
        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);
        string[] selected = result.Recommendations
            .Where(recommendation => recommendation.ApplyByDefault)
            .Select(recommendation => recommendation.TypeId)
            .ToArray();

        EffectFactory.ChainPreset applied = result.BuildSelectedPreset(selected);

        Assert.NotEmpty(selected);
        foreach (string typeId in (string[])["filter", "dehum", "denoise", "eq"])
            Assert.False(State(applied, typeId).Enabled);

        Assert.True(State(applied, "limiter").Enabled);
    }

    [Fact]
    public void QuietPassageWithPersistentHissEnablesBoundedNoiseReduction()
    {
        uint state = 0x51A7_0E5Eu;
        float[][] input = StereoSignal(12, (time, channel) =>
        {
            state = state * 1_664_525u + 1_013_904_223u;
            double noise = ((state >> 8) / 16_777_215.0 * 2 - 1) * 0.00035;
            double hiss = 0.0014 * Math.Sin(2 * Math.PI * (channel == 0 ? 8_700 : 9_100) * time);
            double program = time >= 3
                ? 0.012 * Math.Sin(2 * Math.PI * (channel == 0 ? 439 : 443) * time)
                : 0;
            return noise + hiss + program;
        });

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);
        EffectFactory.EffectState denoise = State(result.RecommendedPreset, "denoise");

        Assert.True(denoise.Enabled);
        Assert.InRange(Param(denoise, "reduction"), 0.5, 12.0);
        Assert.InRange(Param(denoise, "hiss"), 0.0, 10.0);
        Assert.True(Recommendation(result, "denoise").Confidence >= 0.55);
    }

    [Fact]
    public void StrongQuietToProgramSeparationStillRequestsUsefulNoiseReduction()
    {
        uint state = 0xA11C_E55Du;
        float[][] input = StereoSignal(12, (time, channel) =>
        {
            state = state * 1_664_525u + 1_013_904_223u;
            double noise = ((state >> 8) / 16_777_215.0 * 2 - 1) * 0.0031;
            double program = time >= 4
                ? 0.18 * Math.Sin(2 * Math.PI * (channel == 0 ? 431 : 439) * time)
                : 0;
            return noise + program;
        });

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);
        EffectFactory.EffectState denoise = State(result.RecommendedPreset, "denoise");

        Assert.True(denoise.Enabled);
        Assert.InRange(Param(denoise, "reduction"), 3.0, 12.0);
        Assert.True(Recommendation(result, "denoise").Confidence >= 0.55);
    }

    [Fact]
    public void UntouchedLegacyFactoryPresetIsMigratedButCustomizedPresetIsPreserved()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.ChainPreset legacyDefault = EffectFactory.CreateFactoryPreset("Default");
            State(legacyDefault, "limiter").Enabled = true;
            State(legacyDefault, "limiter").Params["thresh"] = 0;
            string defaultPath = Path.Combine(directory, "Default.chain.json");
            File.WriteAllText(defaultPath, JsonSerializer.Serialize(legacyDefault));

            EffectFactory.ChainPreset customized = EffectFactory.CreateFactoryPreset("Vocal Space");
            State(customized, "limiter").Params["thresh"] = -1.25;
            string customPath = Path.Combine(directory, "Vocal Space.chain.json");
            File.WriteAllText(customPath, JsonSerializer.Serialize(customized));

            EffectFactory.EnsureFactoryPresets(directory);

            EffectFactory.ChainPreset migrated = ReadPreset(defaultPath);
            EffectFactory.ChainPreset preserved = ReadPreset(customPath);
            Assert.Equal(0, Param(State(migrated, "limiter"), "thresh"), 10);
            Assert.Equal(-1.25, Param(State(preserved, "limiter"), "thresh"), 10);

        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void UntouchedLegacyCleanTransferIsMigratedToPreNormalizerTrim()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            EffectFactory.ChainPreset legacy = EffectFactory.CreateFactoryPreset("Clean Transfer");
            State(legacy, "limiter").Params["thresh"] = 0;
            EffectFactory.EffectState trim = State(legacy, "trim");
            legacy.Effects.Remove(trim);
            int normalizer = legacy.Effects.FindIndex(effect => effect.TypeId == "normalizer");
            legacy.Effects.Insert(normalizer + 1, trim);
            string path = Path.Combine(directory, "Clean Transfer.chain.json");
            File.WriteAllText(path, JsonSerializer.Serialize(legacy));

            EffectFactory.EnsureFactoryPresets(directory);

            EffectFactory.ChainPreset migrated = ReadPreset(path);
            int trimIndex = migrated.Effects.FindIndex(effect => effect.TypeId == "trim");
            normalizer = migrated.Effects.FindIndex(effect => effect.TypeId == "normalizer");
            Assert.True(trimIndex < normalizer);
            Assert.Equal(0, Param(State(migrated, "limiter"), "thresh"), 10);

        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void SteadyCleanProgramWithoutAQuietNoisePopulationAvoidsDenoise()
    {
        float[][] input = StereoSignal(10, (time, channel) =>
            0.11 * Math.Sin(2 * Math.PI * (channel == 0 ? 431 : 443) * time)
          + 0.04 * Math.Sin(2 * Math.PI * (channel == 0 ? 1291 : 1301) * time));

        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);

        Assert.False(State(result.RecommendedPreset, "denoise").Enabled);
    }

    [Fact]
    public void BuildSelectedPresetUsesRecommendationsOnlyForSelectedEffectTypes()
    {
        float[][] input = StereoSignal(8, (time, channel) =>
            0.10 * Math.Sin(2 * Math.PI * 13 * time + channel * 0.2)
          + 0.04 * Math.Sin(2 * Math.PI * 60 * time)
          + 0.02 * Math.Sin(2 * Math.PI * 120 * time));
        CleanupAnalysisResult result = CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup);

        EffectFactory.ChainPreset selected = result.BuildSelectedPreset(["filter", "dehum"]);

        Assert.Equal(result.BaselinePreset.Effects.Select(effect => effect.TypeId),
            selected.Effects.Select(effect => effect.TypeId));
        foreach (EffectFactory.EffectState baseline in result.BaselinePreset.Effects)
        {
            EffectFactory.EffectState actual = State(selected, baseline.TypeId);
            EffectFactory.EffectState expected = baseline.TypeId is "filter" or "dehum"
                ? State(result.RecommendedPreset, baseline.TypeId)
                : baseline;
            AssertStateEqual(expected, actual);
        }

    }

    [Fact]
    public void AnalyzeNeverMutatesTheInputChannels()
    {
        float[][] input = StereoSignal(5, (time, channel) =>
            0.17 * Math.Sin(2 * Math.PI * (channel == 0 ? 197 : 263) * time)
          + 0.025 * Math.Sin(2 * Math.PI * 60 * time));
        float[][] original = input.Select(channel => (float[])channel.Clone()).ToArray();

        _ = CleanupAnalyzer.Analyze(input, SampleRate, CleanupProfile.VinylCleanup);

        Assert.Equal(original.Length, input.Length);
        for (int channel = 0; channel < input.Length; channel++)
            Assert.True(original[channel].AsSpan().SequenceEqual(input[channel]));
    }

    [Fact]
    public void AnalyzeHonorsAPreCanceledToken()
    {
        float[][] input = StereoSignal(2, (time, _) =>
            0.1 * Math.Sin(2 * Math.PI * 440 * time));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CleanupAnalyzer.Analyze(
            input, SampleRate, CleanupProfile.VinylCleanup, cancellation.Token));
    }

    private static float[][] StereoSignal(
        int seconds,
        Func<double, int, double> sample,
        int sampleRate = SampleRate)
    {
        int length = checked(seconds * sampleRate);
        float[][] channels = [new float[length], new float[length]];
        for (int index = 0; index < length; index++)
        {
            double time = index / (double)sampleRate;
            for (int channel = 0; channel < channels.Length; channel++)
                channels[channel][index] = (float)sample(time, channel);
        }
        return channels;
    }

    private static CleanupRecommendation Recommendation(CleanupAnalysisResult result, string typeId) =>
        Assert.Single(result.Recommendations, recommendation => recommendation.TypeId == typeId);

    private static EffectFactory.ChainPreset ReadPreset(string path) =>
        JsonSerializer.Deserialize<EffectFactory.ChainPreset>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"Could not deserialize {path}.");

    private static EffectFactory.EffectState State(EffectFactory.ChainPreset preset, string typeId) =>
        Assert.Single(preset.Effects, effect => effect.TypeId == typeId);

    private static double Param(EffectFactory.EffectState effect, string key)
    {
        Assert.True(effect.Params.TryGetValue(key, out double value),
            $"Effect '{effect.TypeId}' did not contain parameter '{key}'.");
        return value;
    }

    private static void AssertPresetIsFiniteAndClamped(EffectFactory.ChainPreset preset)
    {
        foreach (EffectFactory.EffectState state in preset.Effects)
        {
            IAudioEffect effect = EffectFactory.Create(state.TypeId);
            foreach ((string key, double value) in state.Params)
            {
                EffectParam descriptor = Assert.Single(effect.Params, parameter => parameter.Key == key);
                Assert.True(double.IsFinite(value), $"{state.TypeId}.{key} was {value}.");
                Assert.InRange(value, descriptor.Min, descriptor.Max);
            }
        }
    }

    private static void AssertStateEqual(
        EffectFactory.EffectState expected,
        EffectFactory.EffectState actual)
    {
        Assert.Equal(expected.TypeId, actual.TypeId);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.Params.Count, actual.Params.Count);
        foreach ((string key, double value) in expected.Params)
        {
            Assert.True(actual.Params.TryGetValue(key, out double actualValue));
            Assert.Equal(value, actualValue, 10);
        }
    }
}
