using System.Numerics;

namespace WaveLab.Audio.Dsp;

/// <summary>
/// Twiddle tables and a radix schedule for one power-of-two transform size, built once and shared
/// by every caller of that size. Plans are immutable and therefore safe to use from several threads
/// at once; the scratch a transform needs is supplied per call, never held here.
/// </summary>
/// <remarks>
/// The table is the whole point of this type. The transform it replaced generated its twiddles by
/// repeated complex multiplication in <c>float</c>, so the rotation error compounded along each
/// stage and grew with the transform size — at 4096 points that error was the accuracy floor of
/// every spectral feature in the app. Here the first quadrant is evaluated once in double precision
/// and the other three are filled by exact symmetry (a swap and a sign), so no entry is more than
/// one rounding away from the true value and the table's quarter-wave symmetry is exact rather than
/// approximate.
/// </remarks>
internal sealed class FftPlan
{
    /// <summary>Transform size; always a power of two of at least 2.</summary>
    internal int Size { get; }

    /// <summary>log2(<see cref="Size"/>).</summary>
    internal int Log2Size { get; }

    /// <summary>
    /// Real parts of exp(-2πik/Size), k = 0..Size-1. The radix-4 butterfly reads up to index
    /// 3(l/4-1)(Size/l) &lt; 3·Size/4, so the table is kept whole rather than folded to a half or a
    /// quarter — one branch-free index is worth more than the memory it saves.
    /// </summary>
    internal double[] TwiddleRe { get; }

    /// <summary>Imaginary parts of exp(-2πik/Size).</summary>
    internal double[] TwiddleIm { get; }

    /// <summary>
    /// True when log2(Size) is odd, so the schedule opens with one radix-2 stage and the remaining
    /// stages are all radix-4.
    /// </summary>
    internal bool LeadingRadix2 { get; }

    internal FftPlan(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("Transform size must be a power of two of at least 2.", nameof(size));

        Size = size;
        Log2Size = BitOperations.Log2((uint)size);
        LeadingRadix2 = (Log2Size & 1) != 0;
        TwiddleRe = new double[size];
        TwiddleIm = new double[size];

        int quarter = size >> 2;
        if (quarter == 0)
        {
            // Size 2 has no quadrant to reflect through; the two roots are exact constants.
            TwiddleRe[0] = 1; TwiddleIm[0] = 0;
            TwiddleRe[1] = -1; TwiddleIm[1] = 0;
            return;
        }

        // First quadrant by direct evaluation. The argument stays inside [-π/2, 0], which is the
        // range Math.SinCos is most accurate over.
        for (int k = 0; k <= quarter; k++)
        {
            (double sin, double cos) = Math.SinCos(-2.0 * Math.PI * k / size);
            TwiddleRe[k] = cos;
            TwiddleIm[k] = sin;
        }

        // w[k + Size/4] = w[k] · (-i), i.e. (re, im) -> (im, -re). Applied three times this fills
        // the remaining quadrants with no further trigonometry and no additional rounding: each
        // entry is a swap and a sign away from one already computed, never a fresh evaluation.
        for (int k = quarter + 1; k < size; k++)
        {
            int previous = k - quarter;
            TwiddleRe[k] = TwiddleIm[previous];
            TwiddleIm[k] = -TwiddleRe[previous];
        }
    }

    /// <summary>
    /// Forward transform of split-complex data, Stockham autosort, mixed radix-4/2.
    /// </summary>
    /// <remarks>
    /// Stockham is used rather than the usual in-place Cooley-Tukey because it carries the
    /// permutation along in the addressing instead of paying for a separate bit-reversal pass, and
    /// because every stage reads and writes consecutive runs of <c>s</c> elements — which is what
    /// makes the inner loop vectorizable at all. The cost is a second buffer to ping-pong through.
    /// Returns true when the result landed in the scratch pair rather than the input pair.
    /// </remarks>
    internal bool Transform(double[] re, double[] im, double[] scratchRe, double[] scratchIm)
    {
        int n = Size;
        double[] sourceRe = re, sourceIm = im, destRe = scratchRe, destIm = scratchIm;
        bool inScratch = false;
        int span = 1;               // s: number of contiguous elements per butterfly leg
        int length = n;             // l: length of the sub-transform being split; l * s == n always

        if (LeadingRadix2)
        {
            Radix2Stage(sourceRe, sourceIm, destRe, destIm, span, length, n);
            (sourceRe, destRe) = (destRe, sourceRe);
            (sourceIm, destIm) = (destIm, sourceIm);
            inScratch = !inScratch;
            length >>= 1;
            span <<= 1;
        }

        while (length >= 4)
        {
            Radix4Stage(sourceRe, sourceIm, destRe, destIm, span, length, n);
            (sourceRe, destRe) = (destRe, sourceRe);
            (sourceIm, destIm) = (destIm, sourceIm);
            inScratch = !inScratch;
            length >>= 2;
            span <<= 2;
        }

        return inScratch;
    }

    private void Radix2Stage(double[] sr, double[] si, double[] dr, double[] di, int span, int length, int n)
    {
        int half = length >> 1;
        int step = n / length;
        double[] twiddleRe = TwiddleRe, twiddleIm = TwiddleIm;

        for (int p = 0; p < half; p++)
        {
            double wr = twiddleRe[p * step];
            double wi = twiddleIm[p * step];
            int sourceA = span * p, sourceB = span * (p + half);
            int destA = span * (p << 1), destB = span * ((p << 1) + 1);

            for (int q = 0; q < span; q++)
            {
                double ar = sr[sourceA + q], ai = si[sourceA + q];
                double br = sr[sourceB + q], bi = si[sourceB + q];
                double tr = ar - br, ti = ai - bi;
                dr[destA + q] = ar + br;
                di[destA + q] = ai + bi;
                dr[destB + q] = tr * wr - ti * wi;
                di[destB + q] = tr * wi + ti * wr;
            }
        }
    }

    /// <summary>
    /// One radix-4 stage. Deriving the four outputs from the two sum/difference pairs costs 8 real
    /// adds and 3 complex multiplies instead of the 4 multiplies two radix-2 stages would need, and
    /// the multiply by ±i folds into a swap and a sign.
    /// </summary>
    private void Radix4Stage(double[] sr, double[] si, double[] dr, double[] di, int span, int length, int n)
    {
        int quarter = length >> 2;
        int step = n / length;
        double[] twiddleRe = TwiddleRe, twiddleIm = TwiddleIm;

        for (int p = 0; p < quarter; p++)
        {
            int t1Index = p * step, t2Index = 2 * p * step, t3Index = 3 * p * step;
            double w1r = twiddleRe[t1Index], w1i = twiddleIm[t1Index];
            double w2r = twiddleRe[t2Index], w2i = twiddleIm[t2Index];
            double w3r = twiddleRe[t3Index], w3i = twiddleIm[t3Index];

            int sourceA = span * p;
            int sourceB = span * (p + quarter);
            int sourceC = span * (p + 2 * quarter);
            int sourceD = span * (p + 3 * quarter);
            int destBase = span * (p << 2);

            for (int q = 0; q < span; q++)
            {
                double ar = sr[sourceA + q], ai = si[sourceA + q];
                double br = sr[sourceB + q], bi = si[sourceB + q];
                double cr = sr[sourceC + q], ci = si[sourceC + q];
                double dr4 = sr[sourceD + q], di4 = si[sourceD + q];

                double t0r = ar + cr, t0i = ai + ci;   // a + c
                double t1r = ar - cr, t1i = ai - ci;   // a - c
                double t2r = br + dr4, t2i = bi + di4; // b + d
                double t3r = br - dr4, t3i = bi - di4; // b - d

                // Y0 = t0 + t2
                dr[destBase + q] = t0r + t2r;
                di[destBase + q] = t0i + t2i;

                // Y1 = (t1 - i·t3) · w1
                double y1r = t1r + t3i, y1i = t1i - t3r;
                dr[destBase + span + q] = y1r * w1r - y1i * w1i;
                di[destBase + span + q] = y1r * w1i + y1i * w1r;

                // Y2 = (t0 - t2) · w2
                double y2r = t0r - t2r, y2i = t0i - t2i;
                dr[destBase + 2 * span + q] = y2r * w2r - y2i * w2i;
                di[destBase + 2 * span + q] = y2r * w2i + y2i * w2r;

                // Y3 = (t1 + i·t3) · w3
                double y3r = t1r - t3i, y3i = t1i + t3r;
                dr[destBase + 3 * span + q] = y3r * w3r - y3i * w3i;
                di[destBase + 3 * span + q] = y3r * w3i + y3i * w3r;
            }
        }
    }
}
