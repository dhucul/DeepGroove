namespace WaveLab.Audio.Dsp;

/// <summary>How a spectral selection was drawn.</summary>
public enum SpectralSelectionKind
{
    /// <summary>A time span crossed with a frequency band.</summary>
    Rectangle,

    /// <summary>A freehand outline, for defects that are not rectangular.</summary>
    Lasso,

    /// <summary>Grown from a seed through connected energy.</summary>
    MagicWand,

    /// <summary>A fundamental and its partials, taken together.</summary>
    Harmonic,
}

/// <summary>
/// A weighted region of the time-frequency plane: which cells a spectral edit applies to, and how
/// strongly.
/// </summary>
/// <remarks>
/// <para>
/// Weights rather than a yes/no mask, because a hard-edged edit rings. Cutting a rectangle out of a
/// spectrum is a multiplication by a step function, and a step in the frequency domain is a sinc in
/// the time domain — the edit is heard as a short chirp around the repair. Tapering the boundary
/// removes it.
/// </para>
/// <para>
/// The taper goes <em>inward</em>. The region is eroded by the feather radius and then smoothed, so
/// full strength is reached a little inside the outline and the weight is zero exactly at it. A
/// taper that spread outward instead would quietly modify audio outside what was drawn, which is the
/// one thing a spectral edit must not do.
/// </para>
/// </remarks>
public sealed class SpectralMask
{
    /// <summary>First frame covered, in the analysis grid's own coordinates.</summary>
    public int FrameOffset { get; }

    /// <summary>First bin covered.</summary>
    public int BinOffset { get; }

    public int Frames { get; }
    public int Bins { get; }

    /// <summary>Row-major weights in 0..1, <see cref="Frames"/> × <see cref="Bins"/>.</summary>
    public float[] Weight { get; }

    public SpectralSelectionKind Kind { get; }

    internal SpectralMask(int frameOffset, int binOffset, int frames, int bins, float[] weight,
        SpectralSelectionKind kind)
    {
        FrameOffset = frameOffset;
        BinOffset = binOffset;
        Frames = frames;
        Bins = bins;
        Weight = weight;
        Kind = kind;
    }

    /// <summary>
    /// The same mask with its frames renumbered. Tools that grow a selection inside a small analysed
    /// window work in that window's own frame numbering and shift the result back to absolute.
    /// </summary>
    public SpectralMask Shifted(int frames) =>
        Frames <= 0 ? this : new SpectralMask(FrameOffset + frames, BinOffset, Frames, Bins, Weight, Kind);

    /// <summary>Weight at an absolute grid position; zero outside the region.</summary>
    public float At(int frame, int bin)
    {
        int f = frame - FrameOffset, b = bin - BinOffset;
        if ((uint)f >= (uint)Frames || (uint)b >= (uint)Bins) return 0f;
        return Weight[f * Bins + b];
    }

    /// <summary>Sum of all weights — the effective area, used to tell an empty selection from a small one.</summary>
    public double Coverage()
    {
        double total = 0;
        foreach (float value in Weight) total += value;
        return total;
    }

    public bool IsEmpty => Frames <= 0 || Bins <= 0 || Coverage() <= 0;

    /// <summary>
    /// How far above the quietest cell present a seed has to be for the wand to grow from it, and
    /// the level growth will not reach below.
    /// </summary>
    private const double FloorMarginDb = 6;

    /// <summary>Nothing selected.</summary>
    public static SpectralMask Empty { get; } =
        new(0, 0, 0, 0, [], SpectralSelectionKind.Rectangle);

    /// <summary>
    /// Nothing selected, but still recording which tool drew it. A builder that finds nothing has
    /// still been used, and callers key off the kind.
    /// </summary>
    private static SpectralMask Nothing(SpectralSelectionKind kind) => new(0, 0, 0, 0, [], kind);

    // ── builders ─────────────────────────────────────────────────

    /// <summary>A time span crossed with a frequency band.</summary>
    public static SpectralMask Rectangle(int frameFrom, int frameTo, int binFrom, int binTo, int feather = 2)
    {
        (frameFrom, frameTo) = Order(frameFrom, frameTo);
        (binFrom, binTo) = Order(binFrom, binTo);
        int frames = frameTo - frameFrom, bins = binTo - binFrom;
        if (frames <= 0 || bins <= 0)
            return new SpectralMask(frameFrom, binFrom, 0, 0, [], SpectralSelectionKind.Rectangle);

        var weight = new float[frames * bins];
        Array.Fill(weight, 1f);
        Feather(weight, frames, bins, feather);
        return new SpectralMask(frameFrom, binFrom, frames, bins, weight, SpectralSelectionKind.Rectangle);
    }

    /// <summary>
    /// A rectangle given in the units the user sees — samples and hertz — converted onto the
    /// analysis grid.
    /// </summary>
    /// <remarks>
    /// The grid is anchored at sample zero, matching <see cref="Spectrogram.Analyze"/> called with an
    /// origin of zero, so a frame index means the same thing wherever the view happens to be scrolled
    /// to. Both ranges are rounded outward: a selection that half covers a cell is asking for that
    /// cell, and rounding inward would leave a rim of the defect behind at every edge.
    /// </remarks>
    public static SpectralMask ForRegion(int startSample, int endSample, double lowHz, double highHz,
        int sampleRate, int fftSize, int hop, int feather = 2)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (fftSize <= 0) throw new ArgumentOutOfRangeException(nameof(fftSize));
        if (hop <= 0) throw new ArgumentOutOfRangeException(nameof(hop));

        (startSample, endSample) = Order(startSample, endSample);
        if (lowHz > highHz) (lowHz, highHz) = (highHz, lowHz);

        // A region of no extent is nothing drawn. The rounding below is outward, so without this a
        // zero-width drag would still claim a couple of frames and the repair actions would light up.
        if (endSample <= startSample || highHz <= lowHz) return Empty;

        int bins = fftSize / 2 + 1;
        int frameFrom = Math.Max(0, (int)Math.Floor(startSample / (double)hop));
        int frameTo = (int)Math.Ceiling(endSample / (double)hop) + 1;
        double perHz = fftSize / (double)sampleRate;
        int binFrom = Math.Clamp((int)Math.Floor(lowHz * perHz), 0, bins - 1);
        int binTo = Math.Clamp((int)Math.Ceiling(highHz * perHz) + 1, binFrom + 1, bins);

        return Rectangle(frameFrom, frameTo, binFrom, binTo, feather);
    }

    /// <summary>A freehand outline, given as grid-space points.</summary>
    public static SpectralMask Lasso(IReadOnlyList<(double Frame, double Bin)> outline, int feather = 2)
    {
        ArgumentNullException.ThrowIfNull(outline);
        if (outline.Count < 3)
            return new SpectralMask(0, 0, 0, 0, [], SpectralSelectionKind.Lasso);

        double minFrame = double.MaxValue, maxFrame = double.MinValue;
        double minBin = double.MaxValue, maxBin = double.MinValue;
        foreach (var (frame, bin) in outline)
        {
            minFrame = Math.Min(minFrame, frame); maxFrame = Math.Max(maxFrame, frame);
            minBin = Math.Min(minBin, bin); maxBin = Math.Max(maxBin, bin);
        }

        int frameFrom = (int)Math.Floor(minFrame), binFrom = (int)Math.Floor(minBin);
        int frames = (int)Math.Ceiling(maxFrame) - frameFrom + 1;
        int bins = (int)Math.Ceiling(maxBin) - binFrom + 1;
        if (frames <= 0 || bins <= 0)
            return new SpectralMask(frameFrom, binFrom, 0, 0, [], SpectralSelectionKind.Lasso);

        var weight = new float[frames * bins];
        for (int f = 0; f < frames; f++)
        {
            for (int b = 0; b < bins; b++)
            {
                if (Contains(outline, frameFrom + f + 0.5, binFrom + b + 0.5))
                    weight[f * bins + b] = 1f;
            }
        }
        Feather(weight, frames, bins, feather);
        return new SpectralMask(frameFrom, binFrom, frames, bins, weight, SpectralSelectionKind.Lasso);
    }

    /// <summary>
    /// Grows from a seed cell through neighbours within <paramref name="toleranceDb"/> of it —
    /// the tool for a cough or a thump, whose edges are wherever its energy stops.
    /// </summary>
    public static SpectralMask MagicWand(SpectrogramData data, int seedFrame, int seedBin,
        double toleranceDb = 18, int maximumCells = 80_000, int feather = 2)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((uint)seedFrame >= (uint)data.Frames || (uint)seedBin >= (uint)data.Bins)
            return Nothing(SpectralSelectionKind.MagicWand);

        // The wand grows through energy, so it needs a floor as well as a tolerance. Seeded on the
        // noise floor every cell is within tolerance of every other and the region swallows the whole
        // analysed window — measured at 195 000 cells from one click on silence, stopped only by the
        // cell cap. A seed that is not itself above the floor has nothing to grow through and selects
        // nothing at all, which is the honest answer and leaves the repair actions disabled.
        float quietest = float.MaxValue;
        foreach (float value in data.MagnitudeDb) quietest = Math.Min(quietest, value);

        float seed = data[seedFrame, seedBin];
        if (seed <= quietest + FloorMarginDb) return Nothing(SpectralSelectionKind.MagicWand);

        double floor = Math.Max(seed - Math.Abs(toleranceDb), quietest + FloorMarginDb);

        // `visited` stops a cell being queued twice; `selected` is what actually ends up in the mask.
        // They are separate because the queue can hold far more than the cell limit allows, and
        // filling the mask from everything ever queued would blow straight through that limit.
        var visited = new bool[data.Frames * data.Bins];
        var selected = new bool[data.Frames * data.Bins];
        var queue = new Queue<int>();
        int seedIndex = seedFrame * data.Bins + seedBin;
        visited[seedIndex] = true;
        queue.Enqueue(seedIndex);

        int minFrame = seedFrame, maxFrame = seedFrame, minBin = seedBin, maxBin = seedBin, taken = 0;

        while (queue.Count > 0 && taken < maximumCells)
        {
            int index = queue.Dequeue();
            int frame = index / data.Bins, bin = index % data.Bins;
            selected[index] = true;
            taken++;
            minFrame = Math.Min(minFrame, frame); maxFrame = Math.Max(maxFrame, frame);
            minBin = Math.Min(minBin, bin); maxBin = Math.Max(maxBin, bin);

            // Four-connected: a diagonal step would leak between a partial and its neighbour.
            Consider(frame - 1, bin); Consider(frame + 1, bin);
            Consider(frame, bin - 1); Consider(frame, bin + 1);

            void Consider(int f, int b)
            {
                if ((uint)f >= (uint)data.Frames || (uint)b >= (uint)data.Bins) return;
                int next = f * data.Bins + b;
                if (visited[next]) return;
                if (data[f, b] < floor) return;
                visited[next] = true;
                queue.Enqueue(next);
            }
        }

        int frames = maxFrame - minFrame + 1, bins = maxBin - minBin + 1;
        var weight = new float[frames * bins];
        for (int f = 0; f < frames; f++)
            for (int b = 0; b < bins; b++)
                if (selected[(minFrame + f) * data.Bins + (minBin + b)])
                    weight[f * bins + b] = 1f;

        Feather(weight, frames, bins, feather);
        return new SpectralMask(minFrame, minBin, frames, bins, weight, SpectralSelectionKind.MagicWand);
    }

    /// <summary>
    /// A fundamental and its partials, each as a narrow band. A buzz is not a region of the
    /// spectrogram; it is a comb, and selecting it as a rectangle would take the music between the
    /// teeth as well.
    /// </summary>
    public static SpectralMask Harmonic(SpectrogramData data, int frameFrom, int frameTo,
        double fundamentalHz, int partials = 12, double relativeBandwidth = 0.03, int feather = 1)
    {
        ArgumentNullException.ThrowIfNull(data);
        frameFrom = Math.Clamp(Math.Min(frameFrom, frameTo), 0, Math.Max(0, data.Frames));
        frameTo = Math.Clamp(Math.Max(frameFrom, frameTo), 0, data.Frames);
        return Harmonic(data.Bins, data.FftSize, data.SampleRate, frameFrom, frameTo, fundamentalHz,
            partials, relativeBandwidth, feather);
    }

    /// <summary>
    /// The same comb, described by the grid's dimensions rather than by an analysed block — for
    /// callers building a mask before anything has been analysed, which every selection tool does.
    /// </summary>
    public static SpectralMask Harmonic(int bins, int fftSize, int sampleRate, int frameFrom, int frameTo,
        double fundamentalHz, int partials = 12, double relativeBandwidth = 0.03, int feather = 1)
    {
        if (bins <= 0 || fftSize <= 0 || sampleRate <= 0)
            return new SpectralMask(0, 0, 0, 0, [], SpectralSelectionKind.Harmonic);

        (frameFrom, frameTo) = Order(frameFrom, frameTo);
        int frames = frameTo - frameFrom;
        if (frames <= 0 || !(fundamentalHz > 0) || partials <= 0)
            return new SpectralMask(frameFrom, 0, 0, 0, [], SpectralSelectionKind.Harmonic);

        var weight = new float[frames * bins];
        double binsPerHz = fftSize / (double)sampleRate;

        // Each band gets a raised-cosine profile across frequency, written directly rather than
        // eroded and smoothed afterwards. The general feather works by eroding first, which a band
        // only a few bins wide cannot survive — it came back at half weight, so a harmonic repair
        // applied at half strength however hard it was asked for. Widening the bands to give the
        // erosion room is worse still: partials of a low fundamental sit a handful of bins apart, so
        // wider bands merge into a solid block and take the music between the teeth, which is the
        // one thing this tool exists to avoid.
        for (int n = 1; n <= partials; n++)
        {
            double centre = fundamentalHz * n;
            if (centre >= sampleRate / 2.0) break;

            double centreBin = centre * binsPerHz;
            double halfWidth = Math.Max(1.0, centre * relativeBandwidth * binsPerHz);
            int from = Math.Max(0, (int)Math.Floor(centreBin - halfWidth));
            int to = Math.Min(bins - 1, (int)Math.Ceiling(centreBin + halfWidth));

            for (int b = from; b <= to; b++)
            {
                // Flat across the core, cosine shoulders to the edges. A profile that peaked only at
                // the exact centre would under-apply whenever the partial falls between bins, which
                // is almost always — the band has to reach full weight wherever the partial landed.
                double distance = Math.Abs(b - centreBin) / halfWidth;
                if (distance >= 1) continue;
                float profile = distance <= 0.5f
                    ? 1f
                    : (float)(0.5 + 0.5 * Math.Cos(Math.PI * (distance - 0.5) * 2));
                for (int f = 0; f < frames; f++)
                {
                    int index = f * bins + b;
                    if (profile > weight[index]) weight[index] = profile;
                }
            }
        }

        // Time is feathered separately: the ends of the span are where the user stopped dragging,
        // and a hard edge there is a click.
        TaperFrames(weight, frames, bins, Math.Max(0, feather));
        return new SpectralMask(frameFrom, 0, frames, bins, weight, SpectralSelectionKind.Harmonic);
    }

    // ── feathering ───────────────────────────────────────────────

    /// <summary>
    /// Erodes by <paramref name="radius"/> and then smooths by the same amount, so the weight
    /// reaches 1 inside the region and 0 at its outline, never beyond it.
    /// </summary>
    internal static void Feather(float[] weight, int frames, int bins, int radius)
    {
        if (radius <= 0 || frames <= 0 || bins <= 0) return;

        // Never erode a region out of existence. A two-frame selection fed to a radius-two feather
        // has nothing left after the erosion, so a small selection would silently do nothing at all
        // — the taper has to give way to the region rather than the other way round.
        radius = Math.Min(radius, (Math.Min(frames, bins) - 1) / 2);
        if (radius <= 0) return;

        Erode(weight, frames, bins, radius);
        Smooth(weight, frames, bins, radius);
    }

    /// <summary>
    /// Fades the first and last few frames, leaving the frequency profile alone. For a comb the
    /// bands are already tapered across frequency by construction; only the ends of the time span
    /// need softening.
    /// </summary>
    private static void TaperFrames(float[] weight, int frames, int bins, int radius)
    {
        int ramp = Math.Min(radius, frames / 2);
        if (ramp <= 0) return;

        for (int i = 0; i < ramp; i++)
        {
            var scale = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (i + 0.5) / ramp));
            for (int b = 0; b < bins; b++)
            {
                weight[i * bins + b] *= scale;
                weight[(frames - 1 - i) * bins + b] *= scale;
            }
        }
    }

    private static void Erode(float[] weight, int frames, int bins, int radius)
    {
        var scratch = new float[weight.Length];

        for (int f = 0; f < frames; f++)
            for (int b = 0; b < bins; b++)
            {
                float lowest = 1f;
                for (int k = -radius; k <= radius; k++)
                {
                    int bb = b + k;
                    lowest = Math.Min(lowest, (uint)bb < (uint)bins ? weight[f * bins + bb] : 0f);
                }
                scratch[f * bins + b] = lowest;
            }

        for (int f = 0; f < frames; f++)
            for (int b = 0; b < bins; b++)
            {
                float lowest = 1f;
                for (int k = -radius; k <= radius; k++)
                {
                    int ff = f + k;
                    lowest = Math.Min(lowest, (uint)ff < (uint)frames ? scratch[ff * bins + b] : 0f);
                }
                weight[f * bins + b] = lowest;
            }
    }

    private static void Smooth(float[] weight, int frames, int bins, int radius)
    {
        int taps = radius * 2 + 1;
        var kernel = new float[taps];
        double total = 0;
        for (int i = 0; i < taps; i++)
        {
            kernel[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 1) / (taps + 1)));
            total += kernel[i];
        }
        for (int i = 0; i < taps; i++) kernel[i] = (float)(kernel[i] / total);

        var scratch = new float[weight.Length];
        for (int f = 0; f < frames; f++)
            for (int b = 0; b < bins; b++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int bb = b + k;
                    if ((uint)bb < (uint)bins) sum += weight[f * bins + bb] * kernel[k + radius];
                }
                scratch[f * bins + b] = sum;
            }

        for (int f = 0; f < frames; f++)
            for (int b = 0; b < bins; b++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int ff = f + k;
                    if ((uint)ff < (uint)frames) sum += scratch[ff * bins + b] * kernel[k + radius];
                }
                weight[f * bins + b] = Math.Clamp(sum, 0f, 1f);
            }
    }

    // ── helpers ──────────────────────────────────────────────────

    private static (int From, int To) Order(int a, int b) => a <= b ? (a, b) : (b, a);

    /// <summary>Even-odd point-in-polygon test.</summary>
    private static bool Contains(IReadOnlyList<(double Frame, double Bin)> outline, double frame, double bin)
    {
        bool inside = false;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            var (fi, bi) = outline[i];
            var (fj, bj) = outline[j];
            if (bi > bin != bj > bin &&
                frame < (fj - fi) * (bin - bi) / (bj - bi) + fi)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
