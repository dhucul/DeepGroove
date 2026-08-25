using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Audio.Effects;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The prompt Normalize Loudness puts up when the true-peak ceiling stops it reaching the target,
/// and the limiter it now offers to close the gap with.
/// </summary>
/// <remarks>
/// <para>
/// The wording this replaced stated the arithmetic — "loudness alone asks for +9.8 dB, but the
/// −0.3 dBTP ceiling allows only +5.2 dB" — and never printed the number the reader would act on,
/// which is <b>where the file actually ends up</b>. So the first assertion here is that the
/// resulting loudness is in the message at all.
/// </para>
/// <para>
/// The second is that the two courses of action carry <b>different</b> gains. A limiter offered
/// after a gain already capped at the ceiling would have nothing to catch, so the offer would be
/// theatre: the full gain is what makes the limiter the thing that closes the shortfall.
/// </para>
/// </remarks>
public sealed class NormalizeLoudnessCeilingTests
{
    /// <summary>The case in the bug report: −21.8 LUFS peaking at −5.5 dBTP, aimed at a CD master.</summary>
    private static (LoudnessMatchPlan Plan, LoudnessMatchStep Step) Bound()
    {
        var plan = LoudnessMatch.Plan(
            [new LoudnessMeasurement("Take 1", -21.8, -5.5, 6.0, 44_100, 44_100 * 30)],
            LoudnessMatchMode.Target,
            LoudnessTarget.CompactDisc);
        return (plan, Assert.Single(plan.Steps));
    }

    [Fact]
    public void TheCeilingBindsExactlyAsTheReportDescribedIt()
    {
        var (plan, step) = Bound();

        Assert.Equal(-12.0, plan.TargetLufs, 6);
        Assert.Equal(-0.3, plan.CeilingDbtp, 6);
        Assert.Equal(9.8, step.RequestedGainDb, 6);     // loudness alone
        Assert.Equal(5.2, step.GainDb, 6);              // what the ceiling permits
        Assert.Equal(4.6, step.ShortfallDb, 6);
        Assert.Equal(-16.6, step.ResultingLufs, 6);     // and the number the old wording omitted
    }

    [Fact]
    public void TheMessageLeadsWithWhereTheFileEndsUpRatherThanWithTheArithmetic()
    {
        var (plan, step) = Bound();

        CeilingChoice choice = LoudnessMatch.DescribeCeilingChoice(plan, step);

        // The outcome, which is what is being decided.
        Assert.Contains("-16.6 LUFS", choice.Message.Replace('−', '-'));
        Assert.Contains("-12.0", choice.Message.Replace('−', '-'));
        // The reason, so the ceiling is not a mystery.
        Assert.Contains("-0.3 dBTP", choice.Message.Replace('−', '-'));
        // And it leads with the outcome rather than burying it behind the two gains.
        Assert.StartsWith("This file can only reach", choice.Message);
    }

    [Fact]
    public void BothCostsOfTheLimiterRouteAreStatedRatherThanDiscovered()
    {
        var (plan, step) = Bound();

        string message = LoudnessMatch.DescribeCeilingChoice(plan, step).Message;

        // Limiting removes energy as well as peaks, so it lands a little under the target...
        Assert.Contains("a little under", message);
        // ...and the full gain leaves the document above full scale until the rack is rendered,
        // which is hard clipping the moment it is saved at 16 or 24 bits.
        Assert.Contains("above full scale", message);
    }

    [Fact]
    public void TheTwoCoursesOfActionCarryDifferentGains()
    {
        var (plan, step) = Bound();

        CeilingChoice choice = LoudnessMatch.DescribeCeilingChoice(plan, step);

        // Stopping short takes what the ceiling permits and says where that lands.
        Assert.Contains("+5.2 dB", choice.StopShortLabel);
        Assert.Contains("-16.6 LUFS", choice.StopShortLabel.Replace('−', '-'));
        // The limiter takes the full gain, or it would have nothing to catch.
        Assert.Contains("+9.8 dB", choice.AddLimiterLabel);
        Assert.DoesNotContain("+5.2", choice.AddLimiterLabel);
    }

    [Fact]
    public void TheOptionsAreOfferedSafestFirstWithCancelLast()
    {
        var (plan, step) = Bound();

        string[] labels = LoudnessMatch.DescribeCeilingChoice(plan, step).Labels;

        Assert.Equal(3, labels.Length);
        Assert.Equal("Cancel", labels[^1]);
        // The first is the accented default in ChoiceDialog, so it has to be the conservative one:
        // stopping short changes the level and nothing else.
        Assert.Contains("stop at", labels[0]);
    }

    /// <summary>
    /// The rack half. A limiter added for this reason has to arrive holding the plan's own ceiling,
    /// or the bound the gain was computed against and the bound the rack enforces are two different
    /// numbers.
    /// </summary>
    [Fact]
    public void ALimiterAddedForTheCeilingArrivesHoldingIt()
    {
        var master = new MasterSection();
        var rack = new MasterSectionViewModel(master);
        int limitersBefore = master.ChainSnapshot.Count(effect => effect.TypeId == "limiter");

        string? name = rack.AddConfiguredEffect("limiter",
            ("thresh", 0), ("ceiling", -0.3), ("oversample", 1));

        Assert.NotNull(name);
        IAudioEffect added = master.ChainSnapshot.Last(effect => effect.TypeId == "limiter");
        Assert.Equal(limitersBefore + 1, master.ChainSnapshot.Count(e => e.TypeId == "limiter"));
        Assert.Equal(-0.3, added.GetParam("ceiling"), 6);
        Assert.Equal(0, added.GetParam("thresh"), 6);
        Assert.Equal(1, added.GetParam("oversample"), 6);
    }

    /// <summary>
    /// Every ceiling the command can be aimed at is inside the limiter's own range, so the rack can
    /// always hold the bound the plan was computed against. The custom ceiling slider stops at −6,
    /// and the parameter reaches −12, so the presets are what this actually checks.
    /// </summary>
    [Fact]
    public void EveryTargetsCeilingIsReachableByTheLimiter()
    {
        var master = new MasterSection();
        var rack = new MasterSectionViewModel(master);

        foreach (LoudnessTarget target in LoudnessTarget.All)
        {
            Assert.NotNull(rack.AddConfiguredEffect("limiter", ("ceiling", target.TruePeakDbtp)));
            IAudioEffect added = master.ChainSnapshot.Last(effect => effect.TypeId == "limiter");
            Assert.Equal(target.TruePeakDbtp, added.GetParam("ceiling"), 6);
        }
    }

    /// <summary>
    /// Normalizing twice must not leave two limiters in the rack: the second is 5 ms more latency
    /// and a second gain stage doing the first one's job.
    /// </summary>
    /// <remarks>
    /// Re-aiming overwrites whatever the user had dialled in, which is why the rack status line
    /// says that is what happened. The alternative — leaving the first one and saying the rack
    /// already holds one — is defensible; silently stacking is not.
    /// </remarks>
    [Fact]
    public void TheRackIsReAimedRatherThanStackedWhenItAlreadyHoldsALimiter()
    {
        var master = new MasterSection();
        var rack = new MasterSectionViewModel(master);

        Assert.NotNull(rack.ConfigureOrAddEffect("limiter", ("ceiling", -1.0)));
        Assert.NotNull(rack.ConfigureOrAddEffect("limiter", ("ceiling", -0.3)));

        IAudioEffect only = Assert.Single(master.ChainSnapshot, fx => fx.TypeId == "limiter");
        Assert.Equal(-0.3, only.GetParam("ceiling"), 6);
        Assert.True(only.Enabled);
        Assert.Contains("re-aimed", rack.RackStatusText);
    }

    /// <summary>
    /// The reason the command switches the rack out of bypass: a bypassed rack renders as an empty
    /// chain, so the limiter that justifies applying the full gain would hold nothing and the
    /// render would write the overs.
    /// </summary>
    /// <remarks>
    /// This pins <see cref="MasterSection.ProcessOffline"/>'s own behaviour rather than the command,
    /// because it is the fact the command's decision rests on — and it is the half that would go on
    /// being true if the activation were ever removed again.
    /// </remarks>
    [Fact]
    public void ABypassedRackRendersAsThoughTheLimiterWereNotThere()
    {
        var master = new MasterSection();
        var rack = new MasterSectionViewModel(master);
        Assert.NotNull(rack.ConfigureOrAddEffect("limiter",
            ("thresh", 0), ("ceiling", -0.3), ("oversample", 1)));

        // Well above full scale, which is what the full-gain route leaves behind.
        float[][] over = [new float[4096], new float[4096]];
        for (int c = 0; c < over.Length; c++)
            for (int i = 0; i < over[c].Length; i++)
                over[c][i] = 2.0f * MathF.Sin(MathF.Tau * 1000f * i / 44_100f);

        rack.RackEnabled = false;
        float[][] bypassed = master.ProcessOffline(over, 44_100);
        Assert.Equal(2.0, (double)bypassed.Max(ch => ch.Max(MathF.Abs)), 3);

        rack.RackEnabled = true;
        float[][] held = master.ProcessOffline(over, 44_100);
        Assert.True(held.Max(ch => ch.Max(MathF.Abs)) < 1.0f,
            "the limiter should hold the ceiling once the rack is actually running");
    }

    /// <summary>
    /// An effect added for a purpose must carry its caller's values before the chain can see it.
    /// </summary>
    /// <remarks>
    /// <c>Read</c> holds the chain lock for a whole block, so an effect published at its defaults
    /// processes at its defaults until the next one — and setting three parameters one at a time
    /// after publishing also lets the audio thread observe one moved and another not. This is the
    /// same class of defect <c>CoefficientPublishingTests</c> exists for, and like that one it can
    /// only fail, never prove: it is a backstop against anyone moving the settings back out of
    /// <see cref="MasterSection.AddEffect"/>.
    /// </remarks>
    [Fact]
    public async Task TheSettingsAreOnTheEffectBeforeTheChainCanSeeIt()
    {
        var master = new MasterSection();
        var rack = new MasterSectionViewModel(master);
        var stop = new CancellationTokenSource();
        var seenAtDefaults = new List<double>();

        var watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                foreach (IAudioEffect fx in master.ChainSnapshot)
                    if (fx.TypeId == "limiter" && Math.Abs(fx.GetParam("ceiling") - -0.3) > 1e-9)
                        lock (seenAtDefaults) seenAtDefaults.Add(fx.GetParam("ceiling"));
        });

        for (int i = 0; i < 200; i++)
        {
            rack.ConfigureOrAddEffect("limiter", ("thresh", 0), ("ceiling", -0.3), ("oversample", 1));
            master.RemoveEffect(master.ChainSnapshot.Last(fx => fx.TypeId == "limiter"));
        }

        await stop.CancelAsync();
        await watcher.WaitAsync(TimeSpan.FromSeconds(5));
        lock (seenAtDefaults)
            Assert.True(seenAtDefaults.Count == 0,
                $"a limiter was visible in the chain at {seenAtDefaults.Count} unasked-for "
                + $"ceiling(s): {string.Join(", ", seenAtDefaults.Take(5).Select(v => $"{v:0.000}"))}");
    }
}
