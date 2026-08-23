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
        Assert.Equal(RestorationRecommendations.ExploratoryClickSensitivity,
            result.ClickSensitivity);
        Assert.Equal(1.0, result.ClickStrength);
        Assert.True(result.Declip);
        Assert.InRange(result.DeclipStrength, 0.80, 0.90);
        Assert.InRange(result.DeclipHeadroomDb, 2.0, 3.0);
    }

    [Fact]
    public void DenseHighConfidenceDamageDoesNotLowerAutoSensitivity()
    {
        ClickEvent[] events = Enumerable.Range(0, 180)
            .Select(index => new ClickEvent(0, index * 12_000 + 2, index * 12_000 + 4,
                index * 12_000 + 3, ImpulseDefectKind.Click,
                0.86f, 0.72f, 0.8f, 0.1f))
            .ToArray();

        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            new ClickAnalysisResult(events, SampleRate * 60, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 60, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false));

        Assert.Equal(RestorationRecommendations.ExploratoryClickSensitivity,
            result.ClickSensitivity);
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

    // ── the three stages added for vertical surface noise ───────────

    /// <summary>
    /// The rumble measurement already existed and reached only the rack chain. This is the whole of
    /// the change: it is read back out of the recommended preset the same way hum and noise are.
    /// </summary>
    [Fact]
    public void RumbleReachesTheWorkbenchHighPass()
    {
        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            new ClickAnalysisResult([], SampleRate * 10, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 10, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false, rumbleEnabled: true, rumbleCutoff: 34));

        Assert.True(result.HighPass);
        Assert.Equal(34, result.HighPassCutoffHz);
    }

    [Fact]
    public void NoRumbleLeavesTheHighPassBypassed()
    {
        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            new ClickAnalysisResult([], SampleRate * 10, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 10, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false));

        Assert.False(result.HighPass);
    }

    /// <summary>
    /// The anchors are the measured gap between the mono-cut and stereo transfers: -16.5, -15.2 and
    /// -12.3 dB on one side of it, -9.8 and -6.0 on the other.
    /// </summary>
    [Theory]
    [InlineData(-16.5, 0.0)]     // a mono pressing: the side is noise, discard it
    [InlineData(-14.0, 0.0)]     // the anchor itself
    [InlineData(-11.0, 0.5)]     // between the two, so half of what goes is music
    [InlineData(-8.0, 1.0)]      // the other anchor
    [InlineData(-6.0, 1.0)]      // real stereo: leave it entirely alone
    [InlineData(0.0, 1.0)]       // no reading taken
    public void TheSideLevelFollowsThePressingRatherThanTheNoise(double sideToMidDb, double expected)
    {
        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            new ClickAnalysisResult([], SampleRate * 10, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 10, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false, sideToMidDb: sideToMidDb));

        Assert.Equal(expected, result.SideLevel, 3);
    }

    /// <summary>
    /// De-crackle rides on the click analysis, which is weaker evidence than the other stages have
    /// and is pinned here so that it stays visible rather than becoming folklore.
    /// </summary>
    [Fact]
    public void DecrackleFollowsWhetherImpulsesWereFoundAtAll()
    {
        ClickEvent[] events =
        [
            new(0, 2, 4, 3, ImpulseDefectKind.Click, 0.86f, 0.72f, 0.8f, 0.1f),
        ];

        RestorationRecommendations.Settings withDamage = RestorationRecommendations.Create(
            new ClickAnalysisResult(events, SampleRate * 10, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 10, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false));
        RestorationRecommendations.Settings clean = RestorationRecommendations.Create(
            new ClickAnalysisResult([], SampleRate * 10, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 10, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false));

        Assert.True(withDamage.Decrackle);
        Assert.False(clean.Decrackle);

        // Never recommended below 3.0: measured, 2.5 deviations repairs twice as many samples and
        // leaves more audible ticks than 3.5 does.
        Assert.Equal(RestorationRecommendations.DefaultDecrackleThreshold, withDamage.DecrackleThreshold);
        Assert.True(withDamage.DecrackleThreshold >= 3.0);
    }

    private static CleanupAnalysisResult Cleanup(bool humEnabled, bool noiseEnabled,
        bool rumbleEnabled = false, double rumbleCutoff = 30, double sideToMidDb = 0)
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
                new EffectFactory.EffectState
                {
                    TypeId = "filter",
                    Enabled = rumbleEnabled,
                    Params = new Dictionary<string, double>
                    {
                        ["mode"] = 1,
                        ["cutoff"] = rumbleCutoff,
                        ["q"] = 0.707,
                        ["slope"] = 0,
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
            SideToMidDb = sideToMidDb,
        };
    }
}
