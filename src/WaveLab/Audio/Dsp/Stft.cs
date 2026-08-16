namespace WaveLab.Audio.Dsp;

/// <summary>Where the first analysis frame begins.</summary>
public enum StftLeadIn
{
    /// <summary>
    /// Begin before sample zero, so every sample of the signal is covered by a full set of
    /// overlapping frames. The right default for new code.
    /// </summary>
    Padded,

    /// <summary>
    /// Begin the first frame at sample zero. Nothing is invented ahead of the signal, but the
    /// opening samples are reconstructed from a partial overlap, and any per-frame state a processor
    /// carries starts cold on real audio rather than on lead-in.
    /// </summary>
    None,
}

/// <summary>How synthesis undoes the analysis and synthesis windowing.</summary>
public enum StftNormalization
{
    /// <summary>
    /// Divide by the single constant the window pair sums to. Cheapest, and exact wherever the pair
    /// satisfies COLA at the chosen hop.
    /// </summary>
    Constant,

    /// <summary>
    /// Divide each output sample by the window weight that actually accumulated on it. Correct for
    /// any window and hop, including pairs that do not tile and partial overlaps at the edges. Where
    /// no meaningful weight accumulates — under a window value of zero — the input passes through
    /// untouched rather than being divided by nothing.
    /// </summary>
    RunningSum,
}

/// <summary>
/// Short-time Fourier transform with weighted overlap-add resynthesis.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the app grew three separate hand-rolled overlap-add loops — in
/// <c>Restoration.ReduceNoise</c>, <c>Restoration.ReduceNoiseAdvanced</c> and
/// <c>NoiseReductionEffect</c> — each with its own normalization scheme: one accumulates a running
/// sum of squared window values and divides at the end, another precomputes a COLA constant, a third
/// undoes the analysis window on read-back. They agree on the answer only because each was tuned
/// against its own error.
/// </para>
/// <para>
/// The default window pair is √Hann for both analysis and synthesis at 75% overlap. Applying half
/// the window going in and half coming out is what weighted overlap-add is for: the frame that gets
/// modified is still tapered, so a discontinuity introduced by the modification is faded rather than
/// clicked, and the pair still sums to a constant so the round trip is exact.
/// </para>
/// <para>
/// An instance owns its scratch buffers and is therefore <b>not</b> thread-safe. Give each channel
/// or worker its own; the underlying <see cref="Fft"/> plans are shared and immutable, so
/// constructing several is cheap.
/// </para>
/// </remarks>
public sealed class Stft
{
    /// <summary>
    /// Called once per frame with that frame's spectrum, which it may modify in place. Bin 0 is DC
    /// and bin <see cref="Bins"/>-1 is Nyquist; the mirrored upper half is implied and must not be
    /// written, which is the bookkeeping the hand-rolled loops all had to do for themselves.
    /// </summary>
    public delegate void FrameProcessor(int frameIndex, int frameStart, Span<float> binRe, Span<float> binIm);

    /// <summary>Called once per frame with a read-only spectrum, for analysis that produces no audio.</summary>
    public delegate void FrameObserver(int frameIndex, int frameStart, ReadOnlySpan<float> binRe, ReadOnlySpan<float> binIm);

    public int FftSize { get; }
    public int Hop { get; }

    /// <summary>Number of unique bins: FftSize/2 + 1, DC through Nyquist inclusive.</summary>
    public int Bins { get; }

    public float[] AnalysisWindow { get; }
    public float[] SynthesisWindow { get; }

    /// <summary>
    /// The constant the analysis/synthesis pair sums to at the chosen hop. Synthesis divides by it,
    /// so a frame passed through untouched reconstructs exactly.
    /// </summary>
    public double OverlapConstant { get; }

    /// <summary>Samples of lookahead a streaming user of this configuration would incur.</summary>
    public int Latency => FftSize - Hop;

    private readonly float[] _frame;
    private readonly float[] _binRe;
    private readonly float[] _binIm;
    private readonly float[] _lookahead;
    private readonly float[] _ringOutput;
    private readonly float[] _ringWeight;

    public StftLeadIn LeadIn { get; }
    public StftNormalization Normalization { get; }

    /// <summary>
    /// Accumulated weight below which a sample is treated as uncovered and passed through. Matches
    /// the guard the restoration passes have always used.
    /// </summary>
    private const float NormalizationFloor = 1e-6f;

    /// <param name="fftSize">Transform size; must be a power of two.</param>
    /// <param name="hop">Frame advance; must divide <paramref name="fftSize"/> so the overlap tiles evenly.</param>
    /// <param name="analysis">Analysis window, or null for √Hann.</param>
    /// <param name="synthesis">Synthesis window, or null to reuse the analysis window.</param>
    /// <param name="leadIn">Where the first frame begins.</param>
    /// <param name="normalization">How synthesis undoes the windowing.</param>
    public Stft(int fftSize, int hop, float[]? analysis = null, float[]? synthesis = null,
        StftLeadIn leadIn = StftLeadIn.Padded,
        StftNormalization normalization = StftNormalization.Constant)
    {
        LeadIn = leadIn;
        Normalization = normalization;
        if (fftSize < 4 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two of at least 4.", nameof(fftSize));
        if (hop <= 0 || hop > fftSize)
            throw new ArgumentOutOfRangeException(nameof(hop), "Hop must be positive and no larger than the FFT size.");
        if (fftSize % hop != 0)
            throw new ArgumentException("Hop must divide the FFT size so that frames tile evenly.", nameof(hop));
        if (analysis is not null && analysis.Length != fftSize)
            throw new ArgumentException("Analysis window must match the FFT size.", nameof(analysis));
        if (synthesis is not null && synthesis.Length != fftSize)
            throw new ArgumentException("Synthesis window must match the FFT size.", nameof(synthesis));

        FftSize = fftSize;
        Hop = hop;
        Bins = fftSize / 2 + 1;
        AnalysisWindow = analysis ?? WindowFunctions.Sqrt(WindowFunctions.Hann(fftSize, periodic: true));
        SynthesisWindow = synthesis ?? AnalysisWindow;

        double[] sums = WindowFunctions.OverlapSum(AnalysisWindow, SynthesisWindow, hop);
        double total = 0;
        foreach (double sum in sums) total += sum;
        OverlapConstant = total / sums.Length;
        if (Math.Abs(OverlapConstant) < 1e-12)
            throw new ArgumentException("The analysis/synthesis pair sums to zero at this hop.", nameof(hop));

        _frame = new float[fftSize];
        _binRe = new float[Bins];
        _binIm = new float[Bins];
        _lookahead = new float[fftSize];
        _ringOutput = new float[fftSize];
        _ringWeight = new float[fftSize];
    }

    /// <summary>
    /// True when this configuration reconstructs exactly. A pair that fails this still works, but
    /// synthesis will impose a periodic amplitude ripple at the hop rate.
    /// </summary>
    public bool ReconstructsExactly(double tolerance = 1e-6) =>
        WindowFunctions.SatisfiesCola(AnalysisWindow, SynthesisWindow, Hop, out _, tolerance);

    /// <summary>Number of frames <see cref="Process"/> and <see cref="Analyze"/> will visit.</summary>
    public int FrameCount(int sampleCount)
    {
        if (sampleCount <= 0) return 0;
        int first = FirstFrameStart;
        return (sampleCount - first + Hop - 1) / Hop;
    }

    // With a padded lead-in, frames begin far enough ahead of sample zero that every sample in the
    // signal is covered by a full set of overlapping frames; without it the opening samples are
    // reconstructed from a partial overlap, which running-sum normalization handles exactly.
    private int FirstFrameStart => LeadIn == StftLeadIn.Padded ? -(FftSize - Hop) : 0;

    /// <summary>
    /// Analyses <paramref name="input"/>, lets <paramref name="processor"/> modify each spectrum, and
    /// overlap-adds the result into <paramref name="output"/>. Input and output may be the same span.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, FrameProcessor? processor,
        CancellationToken cancellationToken = default)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("Output must be at least as long as input.", nameof(output));

        int length = input.Length;
        if (length == 0) return;

        if (Normalization == StftNormalization.RunningSum)
        {
            ProcessRunningSum(input, output, processor, cancellationToken);
            return;
        }

        // A frame reads input[start, start+FftSize) while earlier frames have already written output
        // as far as start+FftSize-Hop. When the caller aliases input and output — which the
        // restoration code does, editing a channel in place — those reads would pick up finished
        // output instead of source. Holding the frame's input in a lookahead that is refilled only
        // from positions no frame has written yet keeps that safe without a full-length copy. For the
        // same reason the output is zeroed a hop at a time, just after the input under it has been
        // captured, rather than all at once up front: clearing the whole buffer first would erase the
        // input of an aliased caller before a single frame had read it.
        int start = FirstFrameStart;
        for (int i = 0; i < FftSize; i++) _lookahead[i] = SampleAt(input, start + i);
        ClearRange(output, start, start + FftSize, length);

        int frameIndex = 0;
        float scale = (float)(1.0 / OverlapConstant);

        while (start < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int i = 0; i < FftSize; i++) _frame[i] = _lookahead[i] * AnalysisWindow[i];
            Fft.RealForward(_frame, _binRe, _binIm);
            processor?.Invoke(frameIndex, start, _binRe.AsSpan(0, Bins), _binIm.AsSpan(0, Bins));
            Fft.RealInverse(_binRe.AsSpan(0, Bins), _binIm.AsSpan(0, Bins), _frame);

            // Capture the next frame's new input before anything writes over it, then zero the region
            // that frame will be the first to accumulate into.
            Array.Copy(_lookahead, Hop, _lookahead, 0, FftSize - Hop);
            for (int i = 0; i < Hop; i++)
                _lookahead[FftSize - Hop + i] = SampleAt(input, start + FftSize + i);
            ClearRange(output, start + FftSize, start + FftSize + Hop, length);

            int from = Math.Max(0, -start);
            int to = Math.Min(FftSize, length - start);
            for (int i = from; i < to; i++)
                output[start + i] += _frame[i] * SynthesisWindow[i] * scale;

            start += Hop;
            frameIndex++;
        }
    }

    /// <summary>
    /// Overlap-add with per-sample weight normalization, finalising each hop as soon as no further
    /// frame can contribute to it.
    /// </summary>
    /// <remarks>
    /// The accumulators are rings of one frame rather than buffers the length of the signal, and a
    /// sample is written out only once every frame that overlaps it has been added. That ordering is
    /// also what makes editing a channel in place safe without a lookahead: by the time a sample is
    /// written, every frame that reads it has already consumed it, and the next frame starts beyond
    /// the write point.
    /// </remarks>
    private void ProcessRunningSum(ReadOnlySpan<float> input, Span<float> output,
        FrameProcessor? processor, CancellationToken cancellationToken)
    {
        int length = input.Length;
        Array.Clear(_ringOutput);
        Array.Clear(_ringWeight);

        int nextOutput = 0;
        int frameIndex = 0;

        for (int start = FirstFrameStart; start < length; start += Hop, frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadFrame(input, start);
            Fft.RealForward(_frame, _binRe, _binIm);
            processor?.Invoke(frameIndex, start, _binRe.AsSpan(0, Bins), _binIm.AsSpan(0, Bins));
            Fft.RealInverse(_binRe.AsSpan(0, Bins), _binIm.AsSpan(0, Bins), _frame);

            for (int i = 0; i < FftSize; i++)
            {
                int index = start + i;
                if (index < 0) continue;
                if (index >= length) break;
                int slot = index % FftSize;
                _ringOutput[slot] += _frame[i] * SynthesisWindow[i];
                _ringWeight[slot] += AnalysisWindow[i] * SynthesisWindow[i];
            }

            int finalizedThrough = Math.Min(length, start + Hop);
            while (nextOutput < finalizedThrough)
            {
                int slot = nextOutput % FftSize;
                output[nextOutput] = _ringWeight[slot] > NormalizationFloor
                    ? _ringOutput[slot] / _ringWeight[slot]
                    : input[nextOutput];
                _ringOutput[slot] = 0f;
                _ringWeight[slot] = 0f;
                nextOutput++;
            }
        }
    }

    private static void ClearRange(Span<float> output, int from, int to, int length)
    {
        from = Math.Max(from, 0);
        to = Math.Min(to, length);
        if (to > from) output.Slice(from, to - from).Clear();
    }

    /// <summary>Analyses <paramref name="input"/> without synthesising, for measurement and display.</summary>
    public void Analyze(ReadOnlySpan<float> input, FrameObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        int length = input.Length;
        if (length == 0) return;

        int frameIndex = 0;
        for (int start = FirstFrameStart; start < length; start += Hop, frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadFrame(input, start);
            Fft.RealForward(_frame, _binRe, _binIm);
            observer(frameIndex, start, _binRe.AsSpan(0, Bins), _binIm.AsSpan(0, Bins));
        }
    }

    /// <summary>Windowed copy of one frame, zero-padded where the frame runs off either end.</summary>
    private void ReadFrame(ReadOnlySpan<float> input, int start)
    {
        for (int i = 0; i < FftSize; i++)
            _frame[i] = SampleAt(input, start + i) * AnalysisWindow[i];
    }

    private static float SampleAt(ReadOnlySpan<float> input, int index) =>
        (uint)index < (uint)input.Length ? input[index] : 0f;
}
