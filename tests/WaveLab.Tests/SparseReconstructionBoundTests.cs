using WaveLab.Audio.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The rule that decides whether the shoulder extrapolation is allowed to cap A-SPADE.
/// </summary>
/// <remarks>
/// <para>
/// A-SPADE knows only that the clipped samples reached the rail, so it may reconstruct a peak far
/// above one. Capping it everywhere costs 0.71 dB a cell over three corpora; capping it nowhere
/// leaves the lightest-damage cells barely worth repairing. What separates the two is <b>the size
/// of the overshoot the shoulders themselves claim</b> — not the length of the plateau, which was
/// swept over thirteen settings and never beat leaving the reconstruction alone.
/// </para>
/// <para>
/// The numbers in these tests are measured, not invented: the claims come from real cells in the
/// three corpora named in <c>docs/validation-corpora.md</c>. They are here so the threshold cannot
/// be moved without someone seeing which real material changes sides.
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
    /// <b>The threshold sits next to a cliff and the margin is the point.</b> Measured over 272
    /// cells the rule gains +46.5 dB at 0.15 and +49.6 at 0.167, then collapses: +16.1 at 0.20 and
    /// −16.2 at 0.25. Leaving out a whole corpus at a time, the folds chose 0.15, 0.16 and 0.17, so
    /// it is set at the bottom of that range rather than at the in-sample optimum.
    /// </summary>
    [Fact]
    public void TheThresholdStaysBelowTheMeasuredCliff()
    {
        Assert.InRange(Restoration.MaximumBoundedOvershoot, 0.10, 0.18);
        output.WriteLine($"bounding below a mean claim of {Restoration.MaximumBoundedOvershoot:P0}; " +
            "0.20 was worth +16.1 dB and 0.25 was worth -16.2 over the same 272 cells");
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
