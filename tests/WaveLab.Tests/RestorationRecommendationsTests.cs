using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using Xunit;

namespace WaveLab.Tests;

public sealed class RestorationRecommendationsTests
{
    private const int SampleRate = 48_000;

    [Fact]
    public void CleanFileBypassesRepairsAndCleanup()
    {
        var clicks = new ClickAnalysisResult([], SampleRate * 60, 2, SampleRate);
        var clipping = new ClippingAnalysisResult([], SampleRate * 60, 2, SampleRate, true);

        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            clicks, clipping, Cleanup(humEnabled: false, noiseEnabled: false));

        Assert.False(result.RepairClicks);
        Assert.False(result.Declip);
        Assert.False(result.RemoveHum);
        Assert.False(result.ReduceNoise);
        Assert.Equal(RestorationRecommendations.ExploratoryClickSensitivity,
            result.ClickSensitivity);
    }

    [Fact]
    public void DefectMeasurementsTuneRepairStrengthAndHeadroom()
    {
        ClickEvent[] clickEvents = Enumerable.Range(0, 20)
            .Select(index => new ClickEvent(0, index * 10_000 + 2, index * 10_000 + 4,
                index * 10_000 + 3,
                index % 5 == 0 ? ImpulseDefectKind.Pop : ImpulseDefectKind.Click,
                0.82f, 0.75f, 0.8f, 0.1f))
            .ToArray();
        ClippedPeakEvent[] clippedEvents =
        [
            new(0, 100, 105, 102, ClipPolarity.Positive,
                0.8f, 1.0f, 0.9f, 0.8f),
        ];

        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            new ClickAnalysisResult(clickEvents, SampleRate * 60, 2, SampleRate),
            new ClippingAnalysisResult(clippedEvents, SampleRate * 60, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false));

        Assert.True(result.RepairClicks);
        Assert.Equal(6.0, result.ClickSensitivity);
        Assert.InRange(result.ClickStrength, 0.75, 0.90);
        Assert.True(result.Declip);
        Assert.InRange(result.DeclipStrength, 0.80, 0.90);
        Assert.InRange(result.DeclipHeadroomDb, 2.0, 3.0);
    }

    [Fact]
    public void SpectralRecommendationsMapToWorkbenchHumAndNoiseControls()
    {
        CleanupAnalysisResult cleanup = Cleanup(humEnabled: true, noiseEnabled: true);

        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            new ClickAnalysisResult([], SampleRate * 10, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 10, 2, SampleRate, true),
            cleanup);

        Assert.True(result.RemoveHum);
        Assert.Equal(50, result.HumFrequency);
        Assert.Equal(5, result.HumHarmonics);
        Assert.Equal(42, result.HumQ);
        Assert.Equal(0.7, result.HumAmount, 10);
        Assert.True(result.ReduceNoise);
        Assert.Equal(9, result.NoiseReductionDb);
        Assert.InRange(result.NoiseSensitivityDb, 5.5, 6.0);
    }

    private static CleanupAnalysisResult Cleanup(bool humEnabled, bool noiseEnabled)
    {
        var preset = new EffectFactory.ChainPreset
        {
            Name = "Test",
            Effects =
            [
                new EffectFactory.EffectState
                {
                    TypeId = "dehum",
                    Enabled = humEnabled,
                    Params = new Dictionary<string, double>
                    {
                        ["frequency"] = 50,
                        ["harmonics"] = 5,
                        ["q"] = 42,
                        ["amount"] = 0.7,
                    },
                },
                new EffectFactory.EffectState
                {
                    TypeId = "denoise",
                    Enabled = noiseEnabled,
                    Params = new Dictionary<string, double>
                    {
                        ["reduction"] = 9,
                    },
                },
            ],
        };
        return new CleanupAnalysisResult
        {
            Profile = CleanupProfile.VinylCleanup,
            Metrics = [],
            Recommendations = [],
            BaselinePreset = preset,
            RecommendedPreset = preset,
            WindowsAnalyzed = 8,
        };
    }
}
