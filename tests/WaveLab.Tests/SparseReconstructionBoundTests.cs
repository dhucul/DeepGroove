using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The rule that decided whether the shoulder extrapolation could cap A-SPADE. <b>Withdrawn.</b>
/// </summary>
/// <remarks>
/// <para>
/// It shipped on three corpora, where capping below a mean claimed overshoot of 15% gained
/// +46.5 dB over 272 cells with no held-out fold negative. A fourth corpus of spoken word cost it
/// 5.2 dB and a fifth of classical another 33.4, leaving <b>+7.9 dB over 464 cells and −38.6 held
/// out</b>, so <see cref="Restoration.RepairChannelSparse"/> no longer calls it.
/// </para>
/// <para>
/// These tests are kept because <b>the function still describes something true about the</b>
/// <b>material</b>, and whatever attacks A-SPADE's overshoot next will have to reckon with it: the
/// shoulders claim little overshoot precisely when the signal is sparse, so this fires hardest on
/// the material that least needs capping. The claims below are measured from named real cells, so
/// they document which material sits on which side of the threshold.
/// </para>
/// </remarks>
public sealed class SparseReconstructionBoundTests(ITestOutputHelper output)
{
    /// <summary>An event whose shoulders claim <paramref name="claimedOvershoot"/> above the rail.</summary>
    private static ClippedPeakEvent Event(double claimedOvershoot, double rail = 0.5, int length = 10) =>
        new(0, 100, 100 + length, 100 + length / 2, ClipPolarity.Positive,
            (float)rail, (float)(rail * (1 + claimedOvershoot)), 0.9f, 0.5f);

    [Fact]
    public void AModestClaimMeansTheShouldersAreWorthListeningTo()
    {
        Assert.True(Restoration.ShouldersBoundTheReconstruction([Event(0.10)]));
    }

    [Fact]
    public void ALargeClaimMeansTheMaterialWasDrivenHardAndTheSolverIsLetRun()
    {
        Assert.False(Restoration.ShouldersBoundTheReconstruction([Event(0.40)]));
    }

    /// <summary>The decision is per channel, so it is the mean claim that counts.</summary>
    [Fact]
    public void TheDecisionIsTheChannelMeanRatherThanAnySingleEvent()
    {
        // Individually one would bound and one would not; together they must not.
        Assert.True(Restoration.ShouldersBoundTheReconstruction([Event(0.05)]));
        Assert.False(Restoration.ShouldersBoundTheReconstruction([Event(0.40)]));
        Assert.False(Restoration.ShouldersBoundTheReconstruction([Event(0.05), Event(0.40)]));
    }

    [Fact]
    public void AChannelWithNoUsableRailIsNotBounded()
    {
        Assert.False(Restoration.ShouldersBoundTheReconstruction([]));
        Assert.False(Restoration.ShouldersBoundTheReconstruction([Event(0.10, rail: 0)]));
    }

    /// <summary>
    /// The lightly damaged cells the rule exists for. These claims were measured on the nine record
    /// transfers at 0.70 of peak, where capping gains 1.4 to 2.8 dB a cell.
    /// </summary>
    [Theory]
    [InlineData(0.0846)]  // Holding Out Too Long, +2.09 dB
    [InlineData(0.1001)]  // Cold Hard Cash, +1.58
    [InlineData(0.1215)]  // Can't Catch Me, +2.84
    [InlineData(0.1233)]  // I.R.E.L.A.N.D., +1.86
    [InlineData(0.1247)]  // Super Do Nothing Day, +1.65
    [InlineData(0.1383)]  // Dartell Stomp, +1.40
    [InlineData(0.1395)]  // Loving You Baby, +2.19
    [InlineData(0.1458)]  // Windows Message Nudge, +8.12
    public void MeasuredLightlyClippedChannelsAreBounded(double claim)
    {
        Assert.True(Restoration.ShouldersBoundTheReconstruction([Event(claim)]),
            $"A channel whose shoulders claim {claim:P1} overshoot is one of the cells this rule was built for.");
    }

    /// <summary>
    /// The cells capping would ruin. Each was measured losing 11 to 14 dB with the cap applied, and
    /// the two nearest misses (0.170 and 0.175) are why the threshold is not raised.
    /// </summary>
    [Theory]
    [InlineData(0.17012)]  // Ring09 at 0.70, would lose 12.5 dB at 0.50
    [InlineData(0.17509)]  // Alarm01 at 0.70, would lose 13.4
    [InlineData(0.19740)]  // Alarm01 at 0.50, would lose 13.8
    [InlineData(0.27426)]  // Alarm03 at 0.70
    [InlineData(0.33512)]  // Ring03 at 0.50, would lose 10.8
    [InlineData(0.59773)]  // Alarm01 at 0.22
    public void MeasuredHardClippedChannelsAreNotBounded(double claim)
    {
        Assert.False(Restoration.ShouldersBoundTheReconstruction([Event(claim)]),
            $"A channel whose shoulders claim {claim:P1} overshoot really was driven hard; capping it costs double digits.");
    }

    /// <summary>
    /// <b>The threshold sits next to a cliff and the margin is the point.</b> Measured over the first
    /// 272 cells the rule gains +46.5 dB at 0.15 and +49.6 at 0.167, then collapses: +16.1 at 0.20
    /// and −16.2 at 0.25. Leaving out a whole corpus at a time, the folds chose 0.15, 0.16 and 0.17,
    /// so it is set at the bottom of that range rather than at the in-sample optimum. A fourth
    /// corpus of spoken word, added afterwards, costs the rule 5.2 dB and does not move the
    /// threshold.
    /// </summary>
    [Fact]
    public void TheThresholdStaysBelowTheMeasuredCliff()
    {
        Assert.InRange(Restoration.MaximumBoundedOvershoot, 0.10, 0.18);
        output.WriteLine($"bounding below a mean claim of {Restoration.MaximumBoundedOvershoot:P0}; " +
            "0.20 was worth +16.1 dB and 0.25 was worth -16.2 over the same cells");
    }

    /// <summary>
    /// The claim grows with damage, which is why one threshold works across corpora: on Alarm01 it
    /// runs 0.175, 0.197, 0.316, 0.598 as the clipping deepens through 0.70, 0.50, 0.35 and 0.22 of
    /// peak. The rule therefore selects roughly the lightest tenth of the material.
    /// </summary>
    [Fact]
    public void TheClaimRisesWithDamageSoOneThresholdTravels()
    {
        double[] alarm01 = [0.17509, 0.19740, 0.31628, 0.59773];
        for (int i = 1; i < alarm01.Length; i++)
            Assert.True(alarm01[i] > alarm01[i - 1], "measured claims rise monotonically with damage");
        Assert.All(alarm01, claim =>
            Assert.False(Restoration.ShouldersBoundTheReconstruction([Event(claim)])));
    }
}
