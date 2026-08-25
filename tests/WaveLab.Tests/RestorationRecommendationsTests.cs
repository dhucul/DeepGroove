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
    /// <remarks>
    /// <para>
    /// <b>Re-pinned once</b>, when the linear ramp between the anchors became a sigmoid with a floor
    /// at 0.20. The floor is why a mono pressing now keeps a fifth of its side instead of none, and
    /// the softer knee is why the two anchors no longer sit exactly on 0 and 1 — both deliberate, on
    /// the grounds that five records from one collection is not enough to justify a hard switch.
    /// </para>
    /// <para>
    /// The two ends are not free to move, and that is what the last three rows are here to hold.
    /// <b>Anything at or above real stereo must read exactly 1.0</b>, because three separate places
    /// downstream test <c>SideLevel &lt; 1.0</c> to decide whether this stage exists at all: the
    /// workbench's card, the render, and the evidence line. A curve that merely approaches 1.0
    /// turns the card on for every record in the world. The 0.0 dB row is the same rule reached from
    /// the other direction — <see cref="CleanupAnalyzer"/> returns 0 for "nothing measurable", and
    /// documents that zero recommends leaving the side alone.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(-16.5, 0.20)]    // a mono pressing: the side is mostly noise, but the floor keeps a fifth
    [InlineData(-14.0, 0.25)]    // the anchor itself
    [InlineData(-11.0, 0.55)]    // between the two, so about half of what goes is music
    [InlineData(-8.0, 0.90)]     // the other anchor: softened, so landing on it is not a cliff
    [InlineData(-6.0, 1.00)]     // real stereo: left entirely alone, and the card stays off
    [InlineData(0.0, 1.00)]      // no reading taken: the neutral answer, not a small reduction
    public void TheSideLevelFollowsThePressingRatherThanTheNoise(double sideToMidDb, double expected)
    {
        RestorationRecommendations.Settings result = RestorationRecommendations.Create(
            new ClickAnalysisResult([], SampleRate * 10, 2, SampleRate),
            new ClippingAnalysisResult([], SampleRate * 10, 2, SampleRate, true),
            Cleanup(humEnabled: false, noiseEnabled: false, sideToMidDb: sideToMidDb));

        Assert.Equal(expected, result.SideLevel, 3);

        // The interior rows are curve outputs and a tolerance is the right assertion for them. The
        // full-level rows are a boundary, and the three sites downstream read `SideLevel < 1.0` as
        // "this stage does not exist" — so a curve that merely approaches 1.0 turns the card on for
        // every record in the world. 0.9999 satisfies the tolerance above and fails this.
        if (expected >= 1.0)
            Assert.False(result.SideLevel < 1.0,
                $"must be exactly 1.0 for the < 1.0 guards to hold, was {result.SideLevel:R}");
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
