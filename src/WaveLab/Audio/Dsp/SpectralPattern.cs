namespace WaveLab.Audio.Dsp;

/// <summary>Settings for learning and removing a spectral pattern.</summary>
/// <param name="FftSize">Transform length; must match the grid the mask was drawn in.</param>
/// <param name="Hop">Frame advance; must divide <paramref name="FftSize"/>.</param>
/// <param name="ReductionDb">How far a bin dominated by the pattern may be pushed down.</param>
/// <param name="Sensitivity">Scales the learned pattern before it is subtracted. Above one
/// over-subtracts, which removes more of the pattern at the cost of taking some programme with it.</param>
/// <param name="Smoothing">Decision-directed smoothing for the a priori SNR estimate. The single
/// most important number in the whole method: it is what turns a per-frame gate into an estimator
/// with memory, and it is why this does not produce musical noise.</param>
/// <param name="AbsenceProbability">Prior probability that the programme is absent in a bin, for the
/// presence-probability gating.</param>
public readonly record struct SpectralPatternOptions(
    int FftSize = 2048,
    int Hop = 512,
    double ReductionDb = 18,
    double Sensitivity = 1.0,
    double Smoothing = 0.98,
    double AbsenceProbability = 0.5)
{
    /// <remarks>
    /// Spelled out field by field rather than written <c>new()</c>: on a record struct the
    /// parameterless form zero-initialises instead of applying these defaults.
    /// </remarks>
    public static SpectralPatternOptions Default { get; } = new(
        FftSize: 2048,
        Hop: 512,
        ReductionDb: 18,
        Sensitivity: 1.0,
        Smoothing: 0.98,
        AbsenceProbability: 0.5);
}

/// <summary>
/// A noise signature learned from a region of the time-frequency plane, and the suppressor that
/// removes it from elsewhere in the file.
/// </summary>
/// <remarks>
/// <para>
/// This is not the same tool as <see cref="Restoration.LearnNoiseProfile"/>, which learns from a
/// span of <em>time</em> and therefore needs a passage where the noise plays alone. Here the
/// signature comes from a time-frequency <em>region</em>: select a buzz's partials with the harmonic
/// tool and only those bins are learned and only those bins are ever touched, with the music between
/// them left exactly as it was. A bin the selection never covered has no signature and is passed
/// through untouched — that is what makes it safe to run over a whole side.
/// </para>
/// <para>
/// Suppression is MMSE log-spectral amplitude (Ephraim &amp; Malah 1985) with presence-probability
/// gating (Cohen's OM-LSA). LSA minimises the error in the <em>log</em> spectrum rather than the
/// spectrum itself, which matters because hearing is closer to logarithmic than linear: the same
/// numerical error is far more audible in a quiet bin than a loud one, and minimising it where it is
/// audible is what makes this quieter than the MMSE-STSA the noise reduction already uses. The
/// gating then interpolates between that gain and the floor by how likely the programme is to be
/// present at all, so a bin holding only the pattern goes to the floor rather than hovering above it.
/// </para>
/// </remarks>
public sealed class SpectralPattern
{
    /// <summary>Mean power per bin over the learned region; zero where nothing was learned.</summary>
    public double[] Power { get; }

    public int Bins => Power.Length;
    public int FftSize { get; }
    public int Hop { get; }
    public int SampleRate { get; }

    /// <summary>How many analysis cells contributed, as a measure of how much was heard.</summary>
    public double Coverage { get; }

    /// <summary>Number of bins that carry a signature at all.</summary>
    public int LearnedBins { get; }

    private SpectralPattern(double[] power, int fftSize, int hop, int sampleRate, double coverage)
    {
        Power = power;
        FftSize = fftSize;
        Hop = hop;
        SampleRate = sampleRate;
        Coverage = coverage;
        foreach (double value in power) if (value > 0) LearnedBins++;
    }

    public bool IsEmpty => LearnedBins == 0;

    public static SpectralPattern None { get; } = new([], 2048, 512, 44_100, 0);

    /// <summary>Centre frequency of a bin.</summary>
    public double Frequency(int bin) => (double)bin * SampleRate / FftSize;

    /// <summary>The band the signature spans, for reporting what was learned.</summary>
    public (double Low, double High) Band
    {
        get
        {
            int low = -1, high = -1;
            for (int b = 0; b < Bins; b++)
            {
                if (Power[b] <= 0) continue;
                if (low < 0) low = b;
                high = b;
            }
            return low < 0 ? (0, 0) : (Frequency(low), Frequency(high + 1));
        }
    }

    // ── learning ─────────────────────────────────────────────────

    /// <summary>
    /// Averages the power in every cell the mask covers, weighted by how strongly it covers it.
    /// </summary>
    /// <remarks>
    /// The weighting matters at the edges: a cell the outline only half covers is half evidence, and
    /// counting it whole would drag the signature toward whatever lies just outside the region the
    /// user actually pointed at.
    /// </remarks>
    public static SpectralPattern Learn(float[] samples, int analysisOrigin, SpectralMask mask,
        int sampleRate, SpectralPatternOptions options = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(mask);
        if (options.FftSize == 0) options = SpectralPatternOptions.Default;
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var repair = new SpectralRepairOptions(options.FftSize, options.Hop, 0);
        SpectralRepair.Frame? frame = SpectralRepair.Frame.Create(samples, analysisOrigin, mask, repair,
            cancellationToken);
        if (frame is null) return None;

        int bins = frame.Bins;
        var total = new double[bins];
        var weight = new double[bins];

        for (int f = 0; f < frame.Frames; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = f * bins;
            for (int b = 0; b < bins; b++)
            {
                float w = frame.MaskWeight[row + b];
                if (w <= 0) continue;
                double re = frame.Re[row + b], im = frame.Im[row + b];
                total[b] += w * (re * re + im * im);
                weight[b] += w;
            }
        }

        double coverage = 0;
        var power = new double[bins];
        for (int b = 0; b < bins; b++)
        {
            if (weight[b] <= 0) continue;
            power[b] = total[b] / weight[b];
            coverage += weight[b];
        }

        return new SpectralPattern(power, options.FftSize, options.Hop, sampleRate, coverage);
    }

    // ── removal ──────────────────────────────────────────────────

    /// <summary>
    /// Removes the signature from <paramref name="count"/> samples starting at
    /// <paramref name="from"/>, returning the processed span.
    /// </summary>
    /// <remarks>
    /// The span is analysed with a run-up either side of what is returned, so the decision-directed
    /// estimate has settled and the overlap-add is complete before the first returned sample. Without
    /// it the opening of every processed range carries a swell as the estimator converges, which on a
    /// selection-sized range is most of the result.
    /// </remarks>
    public float[] Remove(float[] samples, int from, int count,
        SpectralPatternOptions options = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (options.FftSize == 0) options = SpectralPatternOptions.Default;
        if (options.FftSize != FftSize || options.Hop != Hop)
            throw new ArgumentException("The pattern was learned in a different analysis grid.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0 || IsEmpty) return [];

        count = Math.Min(count, Math.Max(0, samples.Length - from));
        if (count == 0) return [];

        int runUp = Math.Max(options.FftSize, 8 * options.Hop);
        int start = Math.Max(0, from - runUp);
        int end = Math.Min(samples.Length, from + count + runUp);
        var region = samples.AsSpan(start, end - start).ToArray();

        var floorGain = Math.Pow(10, -Math.Abs(options.ReductionDb) / 20.0);
        double sensitivity = Math.Max(0, options.Sensitivity);
        double alpha = Math.Clamp(options.Smoothing, 0, 0.9999);
        double absence = Math.Clamp(options.AbsenceProbability, 1e-6, 1 - 1e-6);
        double oddsAbsent = absence / (1 - absence);

        var stft = new Stft(options.FftSize, options.Hop, null, null,
            StftLeadIn.Padded, StftNormalization.RunningSum);
        int bins = Math.Min(stft.Bins, Bins);
        var previousGain = new double[bins];
        var previousPower = new double[bins];
        Array.Fill(previousGain, 1.0);

        int frames = Math.Max(1, stft.FrameCount(region.Length));

        stft.Process(region, region, (frameIndex, _, re, im) =>
        {
            for (int b = 0; b < bins; b++)
            {
                double noise = Power[b] * sensitivity * sensitivity;
                if (noise <= 0) continue;                     // never learned here; leave it alone

                double observed = (double)re[b] * re[b] + (double)im[b] * im[b];
                double posterior = observed / noise;

                // Decision-directed: mostly last frame's estimate, a little of this frame's
                // measurement. The memory is what stops the gain rattling between frames, which is
                // what a listener hears as musical noise.
                double prior = alpha * previousGain[b] * previousGain[b] * previousPower[b] / noise
                             + (1 - alpha) * Math.Max(0, posterior - 1);
                prior = Math.Max(prior, 1e-8);

                double gain = LogSpectralGain(prior, posterior, floorGain, oddsAbsent);

                previousGain[b] = gain;
                previousPower[b] = observed;
                re[b] = (float)(re[b] * gain);
                im[b] = (float)(im[b] * gain);
            }

            if ((frameIndex & 31) == 0) progress?.Report(Math.Min(1, frameIndex / (double)frames));
        }, cancellationToken);

        progress?.Report(1);
        return region.AsSpan(from - start, count).ToArray();
    }

    // ── the gain rule ────────────────────────────────────────────

    /// <summary>
    /// MMSE log-spectral amplitude gain, interpolated toward the floor by how likely the programme
    /// is to be present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The LSA gain is <c>ξ/(1+ξ) · exp(½·E₁(ν))</c> with <c>ν = ξ/(1+ξ)·γ</c>, where ξ is the a
    /// priori and γ the a posteriori SNR. The leading term is the Wiener gain; the exponential
    /// integral is the correction that makes it optimal in the log domain rather than the linear one.
    /// For large ν, E₁ tends to zero and the whole thing tends to Wiener, which is the sanity check.
    /// </para>
    /// <para>
    /// The gating is Cohen's: <c>G = G_LSA^p · G_min^(1−p)</c>, where p is the probability the
    /// programme is present. Interpolating in the exponent rather than linearly is what keeps a
    /// bin holding only the pattern pinned at the floor instead of hovering a few dB above it, which
    /// is audible as a residual whisper of whatever was removed.
    /// </para>
    /// </remarks>
    internal static double LogSpectralGain(double prior, double posterior, double floorGain,
        double oddsAbsent)
    {
        if (!double.IsFinite(prior) || !double.IsFinite(posterior) || prior <= 0) return floorGain;

        double wiener = prior / (1 + prior);
        double v = wiener * posterior;
        if (!double.IsFinite(v) || v <= 0) return floorGain;

        double lsa = wiener * Math.Exp(0.5 * ExponentialIntegral(v));
        if (!double.IsFinite(lsa)) lsa = wiener;
        lsa = Math.Clamp(lsa, floorGain, 1.0);

        // Probability the programme is present in this bin.
        double presence = 1.0 / (1.0 + oddsAbsent * (1 + prior) * Math.Exp(-v));
        if (!double.IsFinite(presence)) presence = 1;
        presence = Math.Clamp(presence, 0, 1);

        double gain = Math.Pow(lsa, presence) * Math.Pow(floorGain, 1 - presence);
        return double.IsFinite(gain) ? Math.Clamp(gain, floorGain, 1.0) : floorGain;
    }

    /// <summary>
    /// The exponential integral E₁(x) = ∫ₓ^∞ e⁻ᵗ/t dt, for x &gt; 0.
    /// </summary>
    /// <remarks>
    /// Series below one and a continued fraction above it, evaluated by modified Lentz. The usual
    /// rational approximation is good to about five decimal places, which is plenty for a gain — but
    /// this is a handful of lines for full precision and removes the question entirely.
    /// </remarks>
    internal static double ExponentialIntegral(double x)
    {
        if (x <= 0) return double.PositiveInfinity;
        if (x > 700) return 0;                      // e^-x has underflowed; the integral has too

        const double EulerMascheroni = 0.5772156649015328606;

        if (x <= 1)
        {
            // E₁(x) = -γ - ln x + Σ (-1)^(n+1) x^n / (n·n!)
            double sum = 0, term = 1;
            for (int n = 1; n <= 60; n++)
            {
                term *= -x / n;
                double contribution = -term / n;
                sum += contribution;
                if (Math.Abs(contribution) < 1e-18 * Math.Abs(sum)) break;
            }
            return -EulerMascheroni - Math.Log(x) + sum;
        }

        // E₁(x) = e^-x · 1/(x+1- 1²/(x+3- 2²/(x+5- …)))
        const double Tiny = 1e-300;
        double b = x + 1, c = 1 / Tiny, d = 1 / b, h = d;
        for (int i = 1; i <= 200; i++)
        {
            double a = -i * (double)i;
            b += 2;
            d = a * d + b;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = b + a / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            double delta = c * d;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-16) break;
        }
        return h * Math.Exp(-x);
    }
}
