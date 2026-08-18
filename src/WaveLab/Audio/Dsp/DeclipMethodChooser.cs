namespace WaveLab.Audio.Dsp;

/// <summary>
/// Decides, per channel, whether a clipped signal is better served by A-SPADE or by the peak
/// reconstruction.
/// </summary>
/// <remarks>
/// <para>
/// Neither method dominates. The two were run against each other on the samples clipping destroyed
/// over 362 cells — thirty-one synthetic materials and six sections of real programme
/// (<c>demo_track.wav</c>), at ten severities each — and the boundary is one curve in the clipped
/// fraction and the mean plateau length.
/// </para>
/// <para>
/// <b>It is a hump, and both ends belong to the arch for different reasons.</b> Tolerated damage is
/// near zero at plateaus of one or two samples, peaks around 85% at runs of ten to twenty, and
/// returns to zero past about a hundred. A two-sample plateau is bracketed exactly by the samples
/// either side of it, so a 1024-sample frame solve has nothing to add; a hundred-and-fifty-sample
/// plateau is a wide smooth span an arch draws well and a frame with almost nothing reliable left
/// in it for a sparse model to fit.
/// </para>
/// <para>
/// <b>Validating against real programme is what forced this shape, and it condemned every rule
/// fitted to synthetic material alone.</b> The previous rule — a straight line, fitted to 310
/// synthetic cells and cross-validated over them — scores 99.1 dB of shortfall on real audio, worse
/// than always choosing A-SPADE (23.0) and close to never choosing it (96.2). It was wrong in 34 of
/// 52 real cells. The reason is a regime the synthetic set never contained: <b>at light damage,
/// synthetic materials have a median plateau of 3.1 samples and real programme has 57.3.</b> Real
/// music is low-frequency dominated, so even 0.2% clipping makes long plateaus, and the synthetic
/// fit had calibrated that corner of the space on data that does not occur in practice. This curve
/// scores 7.0 dB on the real sections and 226.1 on the synthetic ones, against the old rule's 99.1
/// and 235.2 — better on both, dramatically so on the one that matters.
/// </para>
/// <para>
/// <b>There is no damage floor, and that too is measured.</b> A floor looks obviously right, since
/// A-SPADE costs roughly 700×, and two calibrations shipped one. It is wrong: barely-clipped real
/// programme has long plateaus, which is where A-SPADE wins, so a guard at 0.02% of samples costs
/// 19.8 dB across the real sections. The cost argument does not hold either — A-SPADE skips
/// undamaged frames, so trivial damage is trivially cheap. Measured on the real track, a 24-second
/// stereo file takes 1.2 s at 0.4% clipped against 2 ms for the arch, rising to 16 s at 36%.
/// </para>
/// <para>
/// <b>A third decision variable was looked for and there is not one.</b> Stationarity, periodicity,
/// high-frequency share, event density, shoulder trust and run-length-over-period were each
/// measured on the synthetic set. An exception aimed at the residual's exact signature improves the
/// fit from 129.8 to 119.3 dB and makes cross-validation <em>worse</em>, 143.8 to 145.8; the best
/// two-feature rule manages 143.4 fitted against 185.6 held out. They memorise materials. Spectral
/// sparsity, which two calibrations used as the second variable, measured worse than a bare damage
/// floor and cost a bank of FFTs to compute a number beaten by counting samples.
/// </para>
/// <para>
/// <b>Five calibrations of this threshold have been wrong, and the lesson is the same every time:
/// the material set was too narrow.</b> Reasoning from A-SPADE's assumptions got the direction
/// backwards. Thirty-two cells was too few. Ten materials <em>with</em> leave-one-out
/// cross-validation still produced a rule worse than doing nothing clever. Twenty-four materials
/// missed simple tonal programme because none was in the set. And thirty-one synthetic materials,
/// cross-validated, was still beaten on real audio by a one-parameter rule. Hold out
/// <em>materials</em>, never severities; include real programme; and treat a synthetic-only result
/// as untested until something recorded has been through it.
/// </para>
/// </remarks>
public static class DeclipMethodChooser
{
    /// <summary>Constant term of the boundary.</summary>
    public const double ToleranceConstant = -0.50;

    /// <summary>Linear term in the natural log of mean run length.</summary>
    public const double TolerancePerLogRun = 1.10;

    /// <summary>Quadratic term, which is what turns the boundary over at long runs.</summary>
    public const double TolerancePerLogRunSquared = -0.22;

    /// <summary>
    /// Shortest mean run the boundary is evaluated at. A run cannot be shorter than one sample and
    /// the curve is not fitted below this.
    /// </summary>
    private const double ShortestRun = 1.5;

    /// <summary>
    /// The clipped fraction below which A-SPADE is the better method, for plateaus of this mean
    /// length.
    /// </summary>
    /// <remarks>
    /// The curve is a hump: near zero at a run of one or two samples, peaking around 85% at runs of
    /// ten to twenty, and back to zero past about a hundred. Both ends belong to the arch and for
    /// different reasons. A two-sample plateau is bracketed exactly by the samples either side of
    /// it, so there is nothing for a frame solve to add; a plateau of a hundred and fifty samples is
    /// a wide smooth span an arch draws well, and a frame with almost nothing reliable left in it
    /// for a sparse model to fit.
    /// </remarks>
    public static double ToleratedClippedFraction(double meanRunSamples)
    {
        if (!double.IsFinite(meanRunSamples)) return 0;
        double l = Math.Log(Math.Max(ShortestRun, meanRunSamples));
        return Math.Clamp(ToleranceConstant + TolerancePerLogRun * l + TolerancePerLogRunSquared * l * l, 0, 1);
    }

    /// <summary>Longest mean plateau the short-plateau exception applies to.</summary>
    public const double ShortPlateauRun = 8.0;

    /// <summary>Damage below which the exception does not apply.</summary>
    public const double ShortPlateauFloor = 0.0003;

    /// <summary>Damage above which the exception no longer applies.</summary>
    public const double ShortPlateauCeiling = 0.03;

    /// <summary>
    /// Whether short plateaus with modest damage should go to the arch despite the curve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The curve is fitted across the whole space and is wrong in one corner of it: real music with
    /// plateaus under eight samples and between 0.03% and 3% of samples clipped. There the arch beat
    /// A-SPADE in thirteen of nineteen measured cells and by up to 4.5 dB, and five of those were
    /// cells where the chain scored worse than leaving the damage alone — the only such cells in
    /// nineteen recordings.
    /// </para>
    /// <para>
    /// It reads sensibly in both directions. Short plateaus are bracketed closely by their
    /// shoulders, which is exactly what the arch is, and at this damage the shoulders are still
    /// clean. Below the floor the events are so isolated that A-SPADE has whole frames of context
    /// per defect and wins anyway; above the ceiling there is too much damage for the shoulders to
    /// stay clean and it wins again.
    /// </para>
    /// <para>
    /// <b>Unlike the exceptions that were tried and rejected, this one generalises, which is the
    /// only reason it is here.</b> Fitted on the real corpus alone it also improves the synthetic
    /// set it was never fitted to, 226.1 to 194.1 dB; it halves corpus regret under
    /// leave-one-recording-out, 34.4 to 15.9; and it picks identical parameters in 18 of 19 folds
    /// and again when fitted over all three datasets. The earlier candidates all did the opposite —
    /// better fitted, worse held out.
    /// </para>
    /// </remarks>
    public static bool PrefersArchDespiteCurve(double clippedFraction, double meanRunSamples) =>
        meanRunSamples < ShortPlateauRun &&
        clippedFraction >= ShortPlateauFloor &&
        clippedFraction < ShortPlateauCeiling;

    /// <summary>Whether A-SPADE should be preferred for this channel.</summary>
    public static bool PrefersSparse(double clippedFraction, double meanRunSamples) =>
        clippedFraction > 0 &&
        clippedFraction < ToleratedClippedFraction(meanRunSamples) &&
        !PrefersArchDespiteCurve(clippedFraction, meanRunSamples);
}
