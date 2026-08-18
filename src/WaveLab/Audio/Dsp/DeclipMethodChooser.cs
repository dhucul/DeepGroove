namespace WaveLab.Audio.Dsp;

/// <summary>
/// Decides, per channel, whether a clipped signal is better served by A-SPADE or by the peak
/// reconstruction.
/// </summary>
/// <remarks>
/// <para>
/// Neither method dominates, and which one wins was measured rather than reasoned about: the two
/// run against each other on the samples clipping destroyed, over 310 cells — thirty-one synthetic
/// materials at ten severities each. What comes out is one line. A-SPADE gets the channel when
/// enough of it is clipped to be worth 700× the cost, and when the plateaus are short enough that
/// there is still reliable audio between them; the arch gets it otherwise.
/// </para>
/// <para>
/// <b>Damage and plateau length are one boundary, not two thresholds.</b> Treating them separately
/// scores 318.3 dB of shortfall against 244.8 for the joint line, cross-validated by leaving each
/// material out in turn. The tolerated damage falls as the plateaus lengthen — roughly 78% of
/// samples clipped is still A-SPADE's when the average run is ten samples, 37% when it is forty,
/// and nothing past about a hundred and fifty. That is the shape of the trade: a long plateau is a
/// wide smooth span for an arch to draw and a frame with little left in it for a sparse model to
/// fit.
/// </para>
/// <para>
/// <b>This replaced spectral sparsity, which was the wrong quantity and was used the wrong way.</b>
/// The rule it supersedes measured worse than a bare damage floor once enough materials were on the
/// table, and it cost a bank of FFTs per channel to compute a number beaten by counting samples.
/// </para>
/// <para>
/// <b>A third variable was looked for and there is not one, which is the finding.</b> Stationarity,
/// periodicity, high-frequency share, event density, shoulder trust and run-length-over-period were
/// each measured. The residual they would have to explain is sharply characterised — stationary
/// (frame-RMS spread 0.04), strongly periodic (autocorrelation 0.95), long plateaus at 20–50%
/// clipped — and an exception targeting exactly that signature improves the fit from 129.8 to 119.3
/// dB while making cross-validation <em>worse</em>, 143.8 to 145.8. The best two-feature rule does
/// the same thing more dramatically: 143.4 fitted against 185.6 held out. They memorise materials
/// rather than learning the distinction, and the remaining shortfall is still concentrated in the
/// dense stacks' mid band.
/// </para>
/// <para>
/// <b>Four calibrations of this threshold have now been wrong, each because it was fitted to too
/// few materials.</b> Ten materials with leave-one-out cross-validation still produced a rule that
/// measures worse than doing nothing clever, and one fit missed simple tonal programme entirely
/// because no such signal was in the set. Fit on many materials, hold out <em>materials</em> rather
/// than severities, and distrust any improvement that shows only in sample. And note the standing
/// limitation: every material here is synthetic, so what is validated is generalisation across
/// kinds of synthetic signal, not across real programme.
/// </para>
/// </remarks>
public static class DeclipMethodChooser
{
    /// <summary>
    /// Damage below which the peak reconstruction is preferred whatever the material.
    /// </summary>
    /// <remarks>
    /// A-SPADE has to earn its 700×, and below a few percent it does not: short runs leave excellent
    /// shoulders to draw between, while a frame-level sparse model has almost no damage to justify
    /// the assumptions it makes about the rest of the frame.
    /// </remarks>
    public const double MinimumClippedFraction = 0.025;

    /// <summary>Intercept of the boundary, at a mean run of one sample.</summary>
    public const double ToleranceIntercept = 1.475;

    /// <summary>How fast the tolerated damage falls per natural log of mean run length.</summary>
    public const double TolerancePerLogRun = -0.300;

    /// <summary>
    /// Shortest mean run the boundary is evaluated at. Below this the arithmetic would keep
    /// climbing past certainty for no reason — the fraction is already capped at 1.
    /// </summary>
    private const double ShortestRun = 1.5;

    /// <summary>
    /// The clipped fraction below which A-SPADE is the better method, for plateaus of this mean
    /// length. Falls as the plateaus lengthen.
    /// </summary>
    public static double ToleratedClippedFraction(double meanRunSamples)
    {
        if (!double.IsFinite(meanRunSamples)) return 0;
        double bound = ToleranceIntercept +
            TolerancePerLogRun * Math.Log(Math.Max(ShortestRun, meanRunSamples));
        return Math.Clamp(bound, 0, 1);
    }

    /// <summary>Whether A-SPADE should be preferred for this channel.</summary>
    public static bool PrefersSparse(double clippedFraction, double meanRunSamples) =>
        clippedFraction >= MinimumClippedFraction &&
        clippedFraction < ToleratedClippedFraction(meanRunSamples);
}
