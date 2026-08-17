namespace WaveLab.Audio.Montage;

/// <summary>The shape of a fade, as a gain rising from zero to one.</summary>
public enum FadeShape
{
    /// <summary>Straight line in amplitude. Constant-gain: right for correlated material.</summary>
    Linear,

    /// <summary>A quarter sine. Constant-power: right for uncorrelated material.</summary>
    EqualPower,

    /// <summary>Raised cosine — flat at both ends, steepest in the middle.</summary>
    SCurve,

    /// <summary>
    /// Straight line in <em>decibels</em>: a constant rate of decay, which is what a fade-out is
    /// usually meant to sound like.
    /// </summary>
    DecibelLinear,
}

/// <summary>
/// Fade shapes, and the crossfade law that holds the level through an overlap.
/// </summary>
/// <remarks>
/// <para>
/// The interesting problem is not the shapes. It is that <b>the right crossfade depends on what the
/// two clips contain.</b> Fading between two unrelated pieces of music, the powers add, so the pair
/// has to satisfy <c>a² + b² = 1</c> — equal power — or the join dips by 3 dB. Fading between two
/// takes of the same thing, the amplitudes add, so the pair has to satisfy <c>a + b = 1</c> — equal
/// gain — or the join bumps by 3 dB. Picking one law and living with the other case being 3 dB
/// wrong is what most crossfades do.
/// </para>
/// <para>
/// Both are the same equation with the correlation left in. Summing two signals of equal power
/// <c>P</c> and correlation <c>ρ</c> under gains <c>a</c> and <c>b</c> gives
/// <c>P·(a² + b² + 2abρ)</c>, so holding the level means holding that bracket at one. Choose the
/// incoming shape freely, and the outgoing gain that satisfies it is the root of a quadratic:
/// </para>
/// <code>
///   b(t) = −ρ·a(t) + √(1 − a(t)²·(1 − ρ²))
/// </code>
/// <para>
/// At <c>ρ = 0</c> that is <c>√(1 − a²)</c>, equal power. At <c>ρ = 1</c> it is <c>1 − a</c>, equal
/// gain. In between it is neither, and it is <em>exact</em> at every point of the fade rather than
/// an interpolation between the two familiar answers. <see cref="Crossfade.MeasureCorrelation"/>
/// takes ρ from the audio actually being joined, so the law is measured rather than assumed.
/// </para>
/// </remarks>
public static class Fades
{
    /// <summary>
    /// The span a decibel-linear fade is drawn over. 60 dB is the conventional reach of a fade: at a
    /// third of the way through, the level is down 20 dB.
    /// </summary>
    public const double DecibelRange = 60;

    /// <summary>
    /// How far down a <see cref="FadeShape.DecibelLinear"/> fade tracks a straight line in decibels
    /// to within half a dB. Below this it dives faster, which is how it reaches true silence.
    /// </summary>
    /// <remarks>
    /// <b>A straight line in decibels never reaches zero</b>, so no curve is both exactly dB-linear
    /// and exactly silent at its end; every implementation picks one and this one picks silence.
    /// The usual answer — <c>10^((t−1)R/20)</c>, clamped — is dB-linear all the way but starts at
    /// −60 dB rather than nothing, which puts a step at the very start of every fade-in and leaves a
    /// fade-out ending on an audible edge. Subtracting the floor and renormalising removes the step
    /// and costs straightness only in the last 25 dB, where a fade is over. A piecewise curve could
    /// hold the line further and taper at the bottom, but a kink partway down a fade is worse than a
    /// tail that accelerates smoothly.
    /// </remarks>
    public const double DecibelLinearTo = -35;

    /// <summary>A fade-in gain at normalised position <paramref name="t"/>.</summary>
    public static double In(FadeShape shape, double t)
    {
        if (!double.IsFinite(t)) return 0;
        t = Math.Clamp(t, 0, 1);

        return shape switch
        {
            FadeShape.Linear => t,
            FadeShape.EqualPower => Math.Sin(t * Math.PI / 2),
            FadeShape.SCurve => (1 - Math.Cos(t * Math.PI)) / 2,

            // Exact at both ends by construction, so no clamping is needed to reach silence: the
            // usual 10^((t−1)R/20) never gets there and has to be forced, which puts a step at the
            // very start of every fade.
            FadeShape.DecibelLinear => (Math.Pow(10, t * DecibelRange / 20) - 1) /
                                       (Math.Pow(10, DecibelRange / 20) - 1),
            _ => t,
        };
    }

    /// <summary>A fade-out gain at normalised position <paramref name="t"/>: the fade-in reversed.</summary>
    public static double Out(FadeShape shape, double t) => In(shape, 1 - t);

    /// <summary>The shape's fade-in over a whole span, for drawing and for tests.</summary>
    public static double[] Envelope(FadeShape shape, int samples, bool fadeIn = true)
    {
        if (samples <= 0) return [];
        var gains = new double[samples];

        // A one-sample fade is a step; anything longer divides by the last index so the curve
        // actually reaches its endpoint rather than stopping one step short of it.
        double last = Math.Max(1, samples - 1);
        for (int i = 0; i < samples; i++)
        {
            double t = i / last;
            gains[i] = fadeIn ? In(shape, t) : Out(shape, t);
        }
        return gains;
    }
}

/// <summary>The crossfade law: an incoming shape, and the outgoing gain that holds the level.</summary>
public static class Crossfade
{
    /// <summary>
    /// The outgoing gain that keeps the sum at constant level, given the incoming gain and the
    /// correlation between the two clips.
    /// </summary>
    /// <param name="incoming">The incoming clip's gain, 0 to 1.</param>
    /// <param name="correlation">
    /// Zero for unrelated material, one for identical material. See
    /// <see cref="MeasureCorrelation"/> for why it is never negative here.
    /// </param>
    public static double Partner(double incoming, double correlation)
    {
        double a = Math.Clamp(double.IsFinite(incoming) ? incoming : 0, 0, 1);
        double rho = Math.Clamp(double.IsFinite(correlation) ? correlation : 0, 0, 1);

        double under = 1 - a * a * (1 - rho * rho);
        return Math.Max(0, -rho * a + Math.Sqrt(Math.Max(0, under)));
    }

    /// <summary>Both gains across an overlap of <paramref name="samples"/> samples.</summary>
    public static (double[] Outgoing, double[] Incoming) Law(
        FadeShape shape, int samples, double correlation)
    {
        if (samples <= 0) return ([], []);

        var incoming = new double[samples];
        var outgoing = new double[samples];
        double last = Math.Max(1, samples - 1);
        for (int i = 0; i < samples; i++)
        {
            incoming[i] = Fades.In(shape, i / last);
            outgoing[i] = Partner(incoming[i], correlation);
        }
        return (outgoing, incoming);
    }

    /// <summary>
    /// The zero-lag normalised correlation of two spans, which is what decides the law.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clamped to zero at the bottom. A negative correlation means the two clips partly cancel, and
    /// <b>no pair of monotone fades can hold the level through that</b>: the compensation the
    /// equation asks for does not reach zero at the end of the overlap, so it is not a fade any
    /// more. Material that cancels needs its polarity fixed, not its crossfade adjusted.
    /// </para>
    /// <para>
    /// One figure for every channel rather than one per channel, because a crossfade is one gain law
    /// applied to all of them — solving per channel would move the image through the join.
    /// </para>
    /// </remarks>
    public static double MeasureCorrelation(
        IReadOnlyList<float[]> first, int firstStart,
        IReadOnlyList<float[]> second, int secondStart,
        int count) =>
        Math.Max(0, MeasureSignedCorrelation(first, firstStart, second, secondStart, count));

    /// <summary>
    /// The same measurement without the floor, from −1 to 1.
    /// </summary>
    /// <remarks>
    /// The law cannot use a negative value, but telling a user about the join requires it: a
    /// clamped zero means <em>either</em> "these two are unrelated", which is the ordinary case and
    /// perfectly fine, <em>or</em> "these two cancel", which needs fixing. Those are opposite
    /// situations and reporting them as the same number said the first was the second.
    /// </remarks>
    public static double MeasureSignedCorrelation(
        IReadOnlyList<float[]> first, int firstStart,
        IReadOnlyList<float[]> second, int secondStart,
        int count)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (count <= 0 || first.Count == 0 || second.Count == 0) return 0;

        int channels = Math.Min(first.Count, second.Count);
        double product = 0, firstEnergy = 0, secondEnergy = 0;

        for (int c = 0; c < channels; c++)
        {
            float[] x = first[c];
            float[] y = second[c];
            for (int i = 0; i < count; i++)
            {
                int xi = firstStart + i;
                int yi = secondStart + i;
                double a = xi >= 0 && xi < x.Length ? x[xi] : 0;
                double b = yi >= 0 && yi < y.Length ? y[yi] : 0;
                if (!double.IsFinite(a)) a = 0;
                if (!double.IsFinite(b)) b = 0;

                product += a * b;
                firstEnergy += a * a;
                secondEnergy += b * b;
            }
        }

        // Silence on either side leaves the law undetermined, and it does not matter: nothing is
        // being summed, so equal power is as right as anything else.
        double denominator = Math.Sqrt(firstEnergy * secondEnergy);
        if (denominator <= 1e-20) return 0;

        return Math.Clamp(product / denominator, -1, 1);
    }
}
