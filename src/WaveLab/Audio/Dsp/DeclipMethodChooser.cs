namespace WaveLab.Audio.Dsp;

/// <summary>
/// Decides, per channel, whether a clipped signal is better served by A-SPADE or by the peak
/// reconstruction.
/// </summary>
/// <remarks>
/// <para>
/// Neither method dominates, and which one wins is not a matter of taste — it was measured against
/// synthetic damage on four kinds of material, scored on the samples clipping destroyed. A-SPADE
/// wins on lightly clipped material and loses on heavily crushed material, which is the **opposite**
/// of what this repo assumed while A-SPADE sat unwired. That earlier claim compared A-SPADE against
/// the clipped input rather than against the reconstruction it would replace.
/// </para>
/// <para>
/// The crossover is not one number, because it moves with how sparse the material is: tonal
/// programme flips at about 42% of samples clipped, a dense harmonic stack over a noise bed at about
/// 22%. That makes sense — A-SPADE infers the missing samples from a model with few significant
/// frequency components, so material that genuinely has few survives more damage before the model
/// runs out of evidence.
/// </para>
/// <para>
/// <b>Two obvious measures of "sparse" were tried first and both failed.</b> Spectral flatness reads
/// 0.0130 for the dense stack and 0.0131 for sustained struck notes — indistinguishable — while
/// A-SPADE loses on the first above 20% clipping and wins on the second all the way to 60%. Linear
/// prediction gain fails the same pair the same way, 16.3 dB against 15.5. Both are dominated by the
/// noise bed rather than by the count of partials, which is the thing that actually matters. What
/// separates them is <see cref="EffectiveSparsity"/> — how many bins it takes to hold 98% of a
/// frame's energy — reading 42.6 against 13.0. That is A-SPADE's own assumption stated as a number,
/// which is presumably why it is the one that predicts A-SPADE's behaviour.
/// </para>
/// <para>
/// <b>The rule picks the better method in 26 of the 32 measured cells, and every miss is bounded.</b>
/// Tonal and percussive material are called correctly at every severity. The misses are dense
/// material past 60% clipped (−2.1 dB, twice) and sustained material at 42% and 60% (−6.7 and
/// −3.8) — sustained is the hard case, because its envelope barely decays, so past about 40%
/// clipping it offers no clean frame to read the material from and the reading falls back to the
/// damage. Two further misses sit at 0.4% and 0.0% clipped, where a handful of destroyed samples
/// carry the whole score; at that severity the peak reconstruction is itself 2.2 dB <em>worse than
/// leaving the audio alone</em>, so those cells say more about the incumbent than about the choice.
/// Against a single conservative threshold on damage alone this is about three times better summed
/// over every cell.
/// </para>
/// </remarks>
public static class DeclipMethodChooser
{
    /// <summary>Transform length the sparsity measure is taken at, matching <see cref="SpadeOptions"/>.</summary>
    private const int FrameSize = 1024;

    /// <summary>Share of a frame's energy the bin count is asked to cover.</summary>
    private const double EnergyShare = 0.98;

    /// <summary>At or below this many bins the material is sparse enough for the wide tolerance.</summary>
    public const double SparseBins = 20;

    /// <summary>At or above this many bins only light clipping is worth handing to A-SPADE.</summary>
    public const double DenseBins = 40;

    /// <summary>Clipped fraction A-SPADE stays ahead to on sparse material.</summary>
    public const double SparseTolerance = 0.45;

    /// <summary>Clipped fraction A-SPADE stays ahead to on dense material.</summary>
    public const double DenseTolerance = 0.22;

    /// <summary>
    /// The clipped fraction below which A-SPADE is the better method for material of this sparsity.
    /// </summary>
    public static double ToleratedClippedFraction(double effectiveSparsity)
    {
        if (!double.IsFinite(effectiveSparsity)) return DenseTolerance;
        if (effectiveSparsity <= SparseBins) return SparseTolerance;
        if (effectiveSparsity >= DenseBins) return DenseTolerance;
        double t = (effectiveSparsity - SparseBins) / (DenseBins - SparseBins);
        return SparseTolerance + (DenseTolerance - SparseTolerance) * t;
    }

    /// <summary>Whether A-SPADE should be preferred for this channel.</summary>
    public static bool PrefersSparse(double clippedFraction, double effectiveSparsity) =>
        clippedFraction > 0 && clippedFraction < ToleratedClippedFraction(effectiveSparsity);

    /// <summary>Share of a frame that may be clipped before it is too damaged to read sparsity from.</summary>
    private const double MaximumFrameDamage = 0.05;

    /// <summary>Fewest undamaged frames worth trusting before falling back to measuring everything.</summary>
    private const int MinimumCleanFrames = 8;

    /// <summary>
    /// How many frequency bins it takes to hold 98% of a frame's energy, averaged over frames. Low
    /// is sparse.
    /// </summary>
    /// <param name="clipLevel">
    /// Magnitude at or beyond which a sample is damaged, so frames containing damage can be left
    /// out. Pass zero to measure every frame.
    /// </param>
    /// <remarks>
    /// <b>Undamaged frames are measured in preference to damaged ones, and that is not a detail.</b>
    /// Clipping flattens peaks, a flat top is a corner, and a corner is broadband — so damage
    /// inflates this number, and the more of it there is the more inflated the reading. Measured on
    /// the clipped signal, sustained struck notes read 40.5 where the same material clean reads 13.0,
    /// which is enough to reclassify sparse material as dense at exactly the severities where the
    /// classification decides something. Measuring the surviving audio instead reads the material
    /// rather than the damage. Where clipping is heavy enough that too few clean frames remain, the
    /// fallback measures everything — and reading damaged material as dense is the safe direction,
    /// because dense goes to the method that degrades gracefully.
    /// <para>
    /// <b>This earns its keep on percussive material, which is where it was measured.</b> Struck
    /// notes clipped 20% to 35% read 8.3–9.0 from their decay tails, against 31 and up once the
    /// damaged frames are counted — the difference between a tolerance of 0.45 and one of 0.22, and
    /// so between the right method and the wrong one at three of those four severities, worth 1.3
    /// to 2.6 dB. Lightly damaged frames are kept for the same reason the measurement exists:
    /// rejecting a frame for a single railed sample means almost nothing qualifies past about 40%
    /// clipping, the fallback fires, and the reading comes from the damage after all.
    /// </para>
    /// </remarks>
    public static double EffectiveSparsity(float[] samples, double clipLevel = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length < FrameSize) return DenseBins;

        if (clipLevel > 0)
        {
            double clean = Measure(samples, clipLevel, MinimumCleanFrames, cancellationToken);
            if (clean > 0) return clean;
        }
        double all = Measure(samples, 0, 1, cancellationToken);
        // Silence has no material to judge, and the safe reading of "no evidence" is the method that
        // degrades gracefully rather than the one that needs evidence.
        return all > 0 ? all : DenseBins;
    }

    private static double Measure(float[] samples, double clipLevel, int minimumFrames,
        CancellationToken cancellationToken)
    {
        int bins = FrameSize / 2 + 1;
        var frame = new float[FrameSize];
        var re = new float[bins];
        var im = new float[bins];
        var power = new double[bins];
        float[] window = WindowFunctions.Hann(FrameSize, periodic: true);

        // A whole side is millions of samples and the statistic settles long before that, so frames
        // are spread across the file rather than taken from its opening. Hunting for clean frames
        // needs a finer comb than measuring everything, or a busy side offers too few to average.
        int wanted = clipLevel > 0 ? 512 : 64;
        int stride = Math.Max(FrameSize / (clipLevel > 0 ? 4 : 1),
            (samples.Length - FrameSize) / wanted);

        double total = 0;
        int counted = 0;
        for (int start = 0; start + FrameSize <= samples.Length; start += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (clipLevel > 0)
            {
                // Lightly damaged frames still count. Rejecting a frame for a single railed sample
                // is the same mistake in a different place: past about 40% clipping almost no frame
                // is spotless, the fallback fires, and the reading is taken from the damage again —
                // exactly at the severities where the classification decides something.
                int damaged = 0;
                for (int i = 0; i < FrameSize; i++)
                    if (Math.Abs(samples[start + i]) >= clipLevel - RailTolerance) damaged++;
                if (damaged > FrameSize * MaximumFrameDamage) continue;
            }

            for (int i = 0; i < FrameSize; i++) frame[i] = samples[start + i] * window[i];
            Fft.RealForward(frame, re, im);

            double sum = 0;
            for (int b = 0; b < bins; b++)
            {
                power[b] = re[b] * (double)re[b] + im[b] * (double)im[b];
                sum += power[b];
            }
            if (sum <= 1e-18) continue;

            Array.Sort(power);
            double running = 0;
            int used = 0;
            for (int b = bins - 1; b >= 0; b--)
            {
                running += power[b];
                used++;
                if (running >= EnergyShare * sum) break;
            }
            total += used;
            counted++;
        }

        return counted >= minimumFrames ? total / counted : 0;
    }

    /// <summary>Matches <see cref="Spade"/>'s own rail test: a float stores 0.35 as 0.34999999.</summary>
    private const double RailTolerance = 1e-5;
}
