namespace WaveLab.Audio.Dsp;

/// <summary>Settings for <see cref="SpectralRepair"/>.</summary>
/// <param name="FftSize">Transform length. Must match the grid the mask was drawn against.</param>
/// <param name="Hop">Frame advance. Must divide <paramref name="FftSize"/>.</param>
/// <param name="PartialDriftRadians">How far the two edges of a gap may disagree about a bin's
/// frequency and still have it reconstructed. Zero removes the selection instead of rebuilding it;
/// π rebuilds every bin whether or not anything tonal runs through it.</param>
public readonly record struct SpectralRepairOptions(
    int FftSize = 2048,
    int Hop = 512,
    double PartialDriftRadians = 0.10)
{
    /// <remarks>
    /// Spelled out field by field rather than written <c>new()</c>: on a record struct the
    /// parameterless form zero-initialises instead of applying these defaults, which here would mean
    /// a zero FFT size. The same trap applies to every options struct in this namespace.
    /// </remarks>
    public static SpectralRepairOptions Default { get; } = new(
        FftSize: 2048,
        Hop: 512,
        PartialDriftRadians: 0.10);
}

/// <summary>The span of audio a repair replaces, ready for <c>ReplaceRange</c>.</summary>
public readonly record struct SpectralRepairResult(int Start, float[] Samples)
{
    public int End => Start + Samples.Length;
    public bool IsEmpty => Samples.Length == 0;
    public static SpectralRepairResult None => new(0, []);
}

/// <summary>
/// Edits audio through a time-frequency mask: attenuating what was selected, or reconstructing it
/// from what surrounds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attenuate</b> scales the selected cells and resynthesises. Predictable, exact, and the right
/// tool when the defect and the music do not overlap.
/// </para>
/// <para>
/// <b>Heal</b> replaces them. Each bin's masked run is filled with a continuation of whatever
/// partial passes through it — magnitude interpolated between the two edges, phase advanced from the
/// left edge at the rate measured there and corrected to land exactly on the right edge — but only
/// where both edges agree on what that frequency is. Bins that fail the test are emptied instead of
/// invented, which is what makes the operation safe on material that is not purely tonal.
/// </para>
/// <para>
/// The mask is interpreted in the grid <see cref="Spectrogram.Analyze"/> produces: frame <c>f</c> is
/// centred on sample <c>analysisOrigin + f·Hop</c>. Repair analyses with a √Hann pair rather than the
/// display's window, because a modified frame has to be tapered on the way out as well as in; the
/// mask is a region the user drew and feathered, so the small difference in window shape is not
/// something it can resolve.
/// </para>
/// </remarks>
public static class SpectralRepair
{
    /// <summary>Accumulated window weight below which a sample is taken from the source instead.</summary>
    private const float NormalizationFloor = 1e-6f;

    /// <summary>
    /// Scales the selected cells by <paramref name="gainDb"/>, tapered by the mask's own weights.
    /// A gain far below zero silences the selection; a gain above it emphasises what is there.
    /// </summary>
    public static SpectralRepairResult Attenuate(float[] samples, int analysisOrigin, SpectralMask mask,
        double gainDb, SpectralRepairOptions options = default, CancellationToken cancellationToken = default)
    {
        Frame? frame = Frame.Create(samples, analysisOrigin, mask, options, cancellationToken);
        if (frame is null) return SpectralRepairResult.None;

        var gain = (float)Math.Pow(10, gainDb / 20.0);
        for (int i = 0; i < frame.Re.Length; i++)
        {
            float scale = 1 + frame.MaskWeight[i] * (gain - 1);
            frame.Re[i] *= scale;
            frame.Im[i] *= scale;
        }

        return frame.Synthesize(cancellationToken);
    }

    /// <summary>
    /// Discards the selected cells and reconstructs each from the partial running through it, where
    /// there is one.
    /// </summary>
    public static SpectralRepairResult Heal(float[] samples, int analysisOrigin, SpectralMask mask,
        SpectralRepairOptions options = default, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        Frame? frame = Frame.Create(samples, analysisOrigin, mask, options, cancellationToken);
        if (frame is null) return SpectralRepairResult.None;

        float[] observedRe = (float[])frame.Re.Clone();
        float[] observedIm = (float[])frame.Im.Clone();

        ContinuePartials(observedRe, observedIm, frame.MaskWeight, frame.Frames, frame.Bins,
            frame.Re, frame.Im, frame.HopFraction, options.PartialDriftRadians, cancellationToken);
        progress?.Report(0.5);

        Blend(observedRe, observedIm, frame.MaskWeight, frame.Re, frame.Im);
        SpectralRepairResult result = frame.Synthesize(cancellationToken);
        progress?.Report(1);
        return result;
    }

    /// <summary>
    /// Hands back to the observation in proportion to the mask's own weight. Every edit ends here,
    /// which is what makes the feather mean something: a cell the user's outline only half covers is
    /// half repaired, and a cell outside it is returned untouched however the reconstruction went.
    /// </summary>
    private static void Blend(float[] observedRe, float[] observedIm, float[] keep, float[] re, float[] im)
    {
        for (int i = 0; i < re.Length; i++)
        {
            float inside = keep[i], outside = 1 - inside;
            re[i] = observedRe[i] * outside + re[i] * inside;
            im[i] = observedIm[i] * outside + im[i] * inside;
        }
    }

    // ── partial continuation ─────────────────────────────────────

    /// <summary>Wraps an angle to (-π, π].</summary>
    private static double Principal(double angle) => angle - 2 * Math.PI * Math.Round(angle / (2 * Math.PI));

    /// <summary>
    /// Fills each masked run of frames, bin by bin, with a continuation of whatever partial passes
    /// through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is per-bin sinusoidal interpolation in the manner of McAulay and Quatieri, with the
    /// phase discrepancy at the far edge spread evenly across the gap — a first-order frequency
    /// correction, so the trajectory arrives at the right edge in phase rather than beating against
    /// it. Beating is what a magnitude-only fill sounds like: the level is right and the
    /// reconstruction cancels against the audio it is spliced to.
    /// </para>
    /// <para>
    /// The gate is what makes it safe. Both edges are asked what frequency they see and the bin is
    /// continued only if they agree: a sustained tone gives the same answer on each side, while noise
    /// gives a phase advance that is uniformly distributed, so the two answers differ by about a
    /// radian and a half. Without it the fill invents coherent tones out of a noise bed and out of
    /// the tail of a transient, which measures and sounds worse than removing the selection outright.
    /// </para>
    /// <para>
    /// Bins that hold nothing interpolate between two near-silent edges and stay near silent, so no
    /// separate level test is needed — that part of the construction is self-limiting.
    /// </para>
    /// </remarks>
    internal static void ContinuePartials(float[] observedRe, float[] observedIm, float[] keep,
        int frames, int bins, float[] re, float[] im, double hopFraction, double driftTolerance,
        CancellationToken cancellationToken = default)
    {
        // Any weight at all counts as masked. A half-and-half test would leave the outer half of
        // every feather holding its observed value, so the taper would hand back to audio the user
        // asked to have repaired instead of to the repair.
        const float Masked = 1e-3f;

        // Outside the masked runs the observation stands.
        observedRe.CopyTo(re.AsSpan());
        observedIm.CopyTo(im.AsSpan());

        for (int b = 0; b < bins; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double nominal = 2 * Math.PI * b * hopFraction;

            // Forward phase advance across the observed pair (earlier, earlier+1), unwrapped against
            // the bin's nominal rate. NaN when either frame is missing or carries nothing.
            double Advance(int earlier)
            {
                int later = earlier + 1;
                if (earlier < 0 || later >= frames) return double.NaN;
                if (Magnitude(observedRe, observedIm, earlier * bins + b) <= 0 ||
                    Magnitude(observedRe, observedIm, later * bins + b) <= 0) return double.NaN;

                double difference = Angle(observedRe, observedIm, later * bins + b)
                                  - Angle(observedRe, observedIm, earlier * bins + b);
                return nominal + Principal(difference - nominal);
            }

            int f = 0;
            while (f < frames)
            {
                if (keep[f * bins + b] < Masked) { f++; continue; }

                int first = f;
                while (f < frames && keep[f * bins + b] >= Masked) f++;
                int last = f - 1;

                int left = first - 1, right = last + 1;
                bool hasLeft = left >= 0, hasRight = right < frames;
                if (!hasLeft && !hasRight)
                {
                    Empty(first, last);
                    continue;
                }

                // Anchor on the left when there is one; a run touching the block edge is continued
                // from the one side it has and faded out rather than sustained indefinitely.
                int anchor = hasLeft ? left : right;
                double magnitudeAnchor = Magnitude(observedRe, observedIm, anchor * bins + b);
                double phase = Angle(observedRe, observedIm, anchor * bins + b);

                // Forward advance from the observed pair at the anchor: the pair arriving at a left
                // anchor, the pair leaving a right one.
                double advance = Advance(hasLeft ? anchor - 1 : anchor);

                int span = hasLeft && hasRight ? right - left : last - first + 2;
                double magnitudeFar = hasLeft && hasRight
                    ? Magnitude(observedRe, observedIm, right * bins + b)
                    : 0;

                if (hasLeft && hasRight)
                {
                    double fromRight = Advance(right);
                    if (double.IsNaN(advance) || double.IsNaN(fromRight) ||
                        Math.Abs(Principal(advance - fromRight)) > driftTolerance)
                    {
                        Empty(first, last);
                        continue;
                    }
                }
                if (double.IsNaN(advance)) advance = nominal;

                // Land on the far edge's measured phase by spreading the discrepancy evenly across
                // the gap — a small correction to the frequency rather than a jump at the join.
                double correction = 0;
                if (hasLeft && hasRight && magnitudeFar > 0 && magnitudeAnchor > 0)
                {
                    double target = Angle(observedRe, observedIm, right * bins + b);
                    correction = Principal(target - phase - advance * span) / span;
                }

                for (int g = first; g <= last; g++)
                {
                    int offset = g - anchor;
                    double blend = Math.Abs(offset) / (double)span;
                    double magnitude = hasLeft && hasRight
                        ? magnitudeAnchor + (magnitudeFar - magnitudeAnchor) * blend
                        : magnitudeAnchor * (1 - blend);
                    double angle = phase + (advance + correction) * offset;

                    re[g * bins + b] = (float)(magnitude * Math.Cos(angle));
                    im[g * bins + b] = (float)(magnitude * Math.Sin(angle));
                }

                void Empty(int from, int to)
                {
                    for (int g = from; g <= to; g++) { re[g * bins + b] = 0; im[g * bins + b] = 0; }
                }
            }
        }

        static double Magnitude(float[] re, float[] im, int index) =>
            Math.Sqrt((double)re[index] * re[index] + (double)im[index] * im[index]);

        static double Angle(float[] re, float[] im, int index) => Math.Atan2(im[index], re[index]);
    }

    // ── the analysis frame ───────────────────────────────────────

    /// <summary>
    /// One repair's working state: the coefficient grid, the mask resampled onto it, and the
    /// machinery to move between coefficients and samples.
    /// </summary>
    internal sealed class Frame
    {
        private readonly float[] _source;
        private readonly float[] _analysis;
        private readonly float[] _synthesis;
        private readonly float[] _time;
        private readonly int _origin;
        private readonly int _firstFrame;
        private readonly int _fftSize;
        private readonly int _hop;

        public int Frames { get; }
        public int Bins { get; }

        /// <summary>
        /// Hop as a fraction of the transform length. Bin <c>b</c> advances by
        /// <c>2π·b·HopFraction</c> radians per frame when it holds a partial at its own centre
        /// frequency, which is the rate a continuation falls back to when there is nothing to
        /// measure against.
        /// </summary>
        public double HopFraction => _hop / (double)_fftSize;

        /// <summary>Row-major coefficients, <see cref="Frames"/> × <see cref="Bins"/>.</summary>
        public float[] Re { get; }
        public float[] Im { get; }

        /// <summary>The mask's weight at each cell: 1 fully selected, 0 untouched.</summary>
        public float[] MaskWeight { get; }

        /// <summary>First sample of the span a repair replaces, and how many.</summary>
        public int SpanStart { get; }
        public int SpanLength { get; }

        private Frame(float[] source, int origin, int firstFrame, int frames, int bins,
            int fftSize, int hop, int spanStart, int spanLength)
        {
            _source = source;
            _origin = origin;
            _firstFrame = firstFrame;
            _fftSize = fftSize;
            _hop = hop;
            Frames = frames;
            Bins = bins;
            SpanStart = spanStart;
            SpanLength = spanLength;

            _analysis = WindowFunctions.Sqrt(WindowFunctions.Hann(fftSize, periodic: true));
            _synthesis = _analysis;
            _time = new float[fftSize];
            Re = new float[frames * bins];
            Im = new float[frames * bins];
            MaskWeight = new float[frames * bins];
        }

        /// <summary>
        /// Builds the working state, or null when there is nothing to do. The frame range is padded
        /// by a full window either side of the mask so that every sample in the replaced span is
        /// covered by a complete set of overlapping frames.
        /// </summary>
        public static Frame? Create(float[] samples, int analysisOrigin, SpectralMask mask,
            SpectralRepairOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(samples);
            ArgumentNullException.ThrowIfNull(mask);
            if (options.FftSize == 0) options = SpectralRepairOptions.Default;

            int n = options.FftSize, hop = options.Hop;
            if (n < 4 || (n & (n - 1)) != 0)
                throw new ArgumentException("FFT size must be a power of two of at least 4.", nameof(options));
            if (hop <= 0 || n % hop != 0)
                throw new ArgumentException("Hop must divide the FFT size.", nameof(options));
            if (mask.IsEmpty || samples.Length == 0) return null;

            int half = n / 2;
            int pad = n / hop;
            int firstFrame = mask.FrameOffset - pad;
            int frames = mask.Frames + 2 * pad;
            int bins = n / 2 + 1;

            int spanStart = analysisOrigin + mask.FrameOffset * hop - half;
            int spanEnd = analysisOrigin + (mask.FrameOffset + mask.Frames - 1) * hop - half + n;
            spanStart = Math.Clamp(spanStart, 0, samples.Length);
            spanEnd = Math.Clamp(spanEnd, spanStart, samples.Length);
            if (spanEnd <= spanStart) return null;

            var frame = new Frame(samples, analysisOrigin, firstFrame, frames, bins, n, hop,
                spanStart, spanEnd - spanStart);

            for (int f = 0; f < frames; f++)
            {
                int row = f * bins;
                for (int b = 0; b < bins; b++)
                    frame.MaskWeight[row + b] = Math.Clamp(mask.At(firstFrame + f, b), 0f, 1f);
            }

            frame.Analyze(cancellationToken);
            return frame;
        }

        private int FrameStart(int index) => _origin + (_firstFrame + index) * _hop - _fftSize / 2;

        /// <summary>Fills the coefficient grid from the source samples.</summary>
        private void Analyze(CancellationToken cancellationToken)
        {
            var re = new float[Bins];
            var im = new float[Bins];

            for (int f = 0; f < Frames; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = FrameStart(f);
                for (int i = 0; i < _fftSize; i++)
                {
                    int index = start + i;
                    _time[i] = (uint)index < (uint)_source.Length ? _source[index] * _analysis[i] : 0f;
                }

                Fft.RealForward(_time, re, im);
                re.CopyTo(Re.AsSpan(f * Bins, Bins));
                im.CopyTo(Im.AsSpan(f * Bins, Bins));
            }
        }

        /// <summary>Resynthesises and returns only the span the repair is allowed to replace.</summary>
        public SpectralRepairResult Synthesize(CancellationToken cancellationToken)
        {
            var signal = new float[SpanLength];
            var weight = new float[SpanLength];

            var re = new float[Bins];
            var im = new float[Bins];

            for (int f = 0; f < Frames; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = FrameStart(f);
                if (start + _fftSize <= SpanStart || start >= SpanStart + SpanLength) continue;

                Re.AsSpan(f * Bins, Bins).CopyTo(re);
                Im.AsSpan(f * Bins, Bins).CopyTo(im);
                Fft.RealInverse(re, im, _time);

                int offset = start - SpanStart;
                int i0 = Math.Max(0, -offset);
                int i1 = Math.Min(_fftSize, SpanLength - offset);
                for (int i = i0; i < i1; i++)
                {
                    signal[offset + i] += _time[i] * _synthesis[i];
                    weight[offset + i] += _analysis[i] * _synthesis[i];
                }
            }

            // Where no meaningful weight accumulated the source passes through untouched, rather
            // than being divided by nothing — the same guard the overlap-add in Stft uses.
            for (int i = 0; i < SpanLength; i++)
            {
                signal[i] = weight[i] > NormalizationFloor
                    ? signal[i] / weight[i]
                    : _source[SpanStart + i];
            }

            return new SpectralRepairResult(SpanStart, signal);
        }
    }
}
