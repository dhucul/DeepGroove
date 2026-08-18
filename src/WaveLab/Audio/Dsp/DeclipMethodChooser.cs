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

    /// <summary>
    /// <b>There is no short-plateau exception, and the way that was learned is the point.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// One was shipped: real music with plateaus under eight samples and 0.03%–3% clipped went to
    /// the arch despite the curve. It halved regret on the corpus it was fitted to (34.4 to 15.8 dB)
    /// and fixed the only five cells there where the chain scored worse than leaving the damage
    /// alone. It was checked the three ways the earlier rejected exceptions had failed — it improved
    /// the synthetic set it was never fitted to, halved held-out regret under
    /// leave-one-recording-out, and picked identical parameters in 18 of 19 folds.
    /// </para>
    /// <para>
    /// <b>A second real corpus destroyed it.</b> On 152 cells of entirely different real audio it
    /// costs <b>668.7 dB</b> — the chooser goes from 292.5 to 961.2, worse than always choosing
    /// A-SPADE and nearly as bad as never choosing it. Refitting the exception on all three datasets
    /// together selects no exception at all, in 87 of 88 leave-one-group-out folds.
    /// </para>
    /// <para>
    /// The lesson is not "exceptions are bad" but that <b>transfer to synthetic material is not
    /// evidence of transfer to real material</b>. Both real corpora contain short plateaus at modest
    /// damage; in one the arch wins them and in the other A-SPADE does, and nothing measured tells
    /// them apart. The five cells that motivated the exception are a real, characterised, unfixed
    /// defect — see the notes in CLAUDE.md — and they stay unfixed rather than be bought at this
    /// price.
    /// </para>
    /// </remarks>
    /// <summary>Whether A-SPADE should be preferred for this channel.</summary>
    public static bool PrefersSparse(double clippedFraction, double meanRunSamples) =>
        clippedFraction > 0 && clippedFraction < ToleratedClippedFraction(meanRunSamples);
}
