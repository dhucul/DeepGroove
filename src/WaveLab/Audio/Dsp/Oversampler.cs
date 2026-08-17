namespace WaveLab.Audio.Dsp;

/// <summary>
/// Polyphase FIR oversampling, for running a non-linearity at a higher rate than the signal.
/// </summary>
/// <remarks>
/// <para>
/// Any non-linearity — saturation, a compressor's detector, a limiter's clipper — creates harmonics,
/// and every harmonic above Nyquist folds back down as an inharmonic tone that was not in the music
/// and cannot be removed afterwards. Running the non-linearity at two, four or eight times the rate
/// moves the fold-back point up by the same factor, and the filter on the way down removes what
/// would have folded.
/// </para>
/// <para>
/// The filter has to be genuinely band-limited for any of that to work. Repeating or linearly
/// interpolating samples on the way up is not: it leaves images of the original spectrum sitting
/// just above Nyquist, the non-linearity multiplies them with the signal, and the products land
/// back in the audible band. A decimation filter cannot recover what aliased on the way up. That is
/// what <c>SaturationEffect</c> did — <c>mid = (x + prev) * 0.5</c> — and it is why its harmonics
/// were never as clean as its curve.
/// </para>
/// <para>
/// The kernel is a windowed sinc arranged as polyphase branches: each output phase uses only the
/// taps that land on it, so upsampling costs the same per output sample whatever the factor, and
/// downsampling computes only the outputs it keeps. The alternative — filtering at the high rate and
/// throwing samples away — does the same arithmetic and discards most of it.
/// </para>
/// </remarks>
public sealed class Oversampler
{
    /// <summary>Taps per polyphase branch. The whole kernel is this times the factor.</summary>
    public const int DefaultTapsPerPhase = 32;

    private readonly int _factor;
    private readonly int _tapsPerPhase;
    private readonly int _channels;

    /// <summary>[phase][tap] — the kernel split so each output phase reads only its own taps.</summary>
    private readonly float[][] _phases;

    /// <summary>The same kernel whole, which the decimation side convolves directly.</summary>
    private readonly float[] _kernel;

    private readonly float[][] _upHistory;      // [channel][tap]
    private readonly float[][] _downHistory;    // [channel][tap * factor]
    private readonly int[] _upPosition;
    private readonly int[] _downPosition;

    public int Factor => _factor;

    /// <summary>
    /// Samples of delay the round trip introduces, at the base rate.
    /// </summary>
    /// <remarks>
    /// Both halves contribute. Each kernel is <c>tapsPerPhase × factor</c> long at the oversampled
    /// rate, so each carries half of that as group delay — a quarter of the whole kernel each in base
    /// samples, and half of it together. Counting only one half puts the round trip out by sixteen
    /// samples, which measures as no correlation at all rather than as a small error.
    /// </remarks>
    public int LatencySamples => _tapsPerPhase;

    public Oversampler(int factor, int channels, int tapsPerPhase = DefaultTapsPerPhase)
    {
        if (factor is not (1 or 2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(factor),
            "Oversampling factor must be 1, 2, 4 or 8.");
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (tapsPerPhase < 4 || tapsPerPhase % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(tapsPerPhase), "Taps per phase must be even and at least 4.");

        _factor = factor;
        _tapsPerPhase = tapsPerPhase;
        _channels = channels;

        int length = tapsPerPhase * factor;
        float[] kernel = Design(length, factor);
        _kernel = kernel;

        _phases = new float[factor][];
        for (int phase = 0; phase < factor; phase++)
        {
            _phases[phase] = new float[tapsPerPhase];
            for (int tap = 0; tap < tapsPerPhase; tap++)
            {
                int index = tap * factor + phase;
                _phases[phase][tap] = index < length ? kernel[index] : 0;
            }
        }

        _upHistory = new float[channels][];
        _downHistory = new float[channels][];
        _upPosition = new int[channels];
        _downPosition = new int[channels];
        for (int c = 0; c < channels; c++)
        {
            _upHistory[c] = new float[tapsPerPhase];
            _downHistory[c] = new float[tapsPerPhase * factor];
        }
    }

    /// <summary>
    /// A windowed sinc cutting just below the base rate's Nyquist, scaled so upsampling preserves
    /// level.
    /// </summary>
    /// <remarks>
    /// The cutoff sits a little under Nyquist rather than exactly on it. A brick wall placed exactly
    /// there needs an impossibly long kernel to have stopped by the time the images begin; backing
    /// off a few percent buys the transition band that makes a finite kernel work, at the cost of
    /// the very top of the audio band, which is where the least is happening.
    /// </remarks>
    private static float[] Design(int length, int factor)
    {
        var kernel = new float[length];
        if (factor == 1) { kernel[0] = 1; return kernel; }

        double cutoff = 0.46 / factor;          // as a fraction of the oversampled rate

        // Centred on length/2, not (length−1)/2. An even-length kernel centred the usual way has its
        // symmetry point <em>between</em> samples, so the group delay is a half-integer and the round
        // trip cannot be undone by any whole number of samples — measured, it left the signal
        // half a sample out and correlated at 5.6 dB. Centring here makes the delay exactly
        // length/2, and the first tap falls where the window is zero anyway.
        double centre = length / 2.0;
        double sum = 0;

        for (int i = 0; i < length; i++)
        {
            double x = i - centre;
            double sinc = Math.Abs(x) < 1e-9 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);

            // Blackman-Harris: the stopband has to be well below anything the non-linearity will
            // produce, or the images it suppresses come back as intermodulation.
            double t = (double)i / length;
            double window = 0.35875
                          - 0.48829 * Math.Cos(2 * Math.PI * t)
                          + 0.14128 * Math.Cos(4 * Math.PI * t)
                          - 0.01168 * Math.Cos(6 * Math.PI * t);

            kernel[i] = (float)(sinc * window);
            sum += kernel[i];
        }

        // Normalised so a constant passes through at unity, then scaled by the factor because
        // inserting zeros between samples divides the level by it.
        double scale = factor / Math.Max(sum, 1e-12);
        for (int i = 0; i < length; i++) kernel[i] = (float)(kernel[i] * scale);
        return kernel;
    }

    /// <summary>Expands one base-rate sample into <see cref="Factor"/> oversampled ones.</summary>
    public void Upsample(int channel, float input, Span<float> output)
    {
        if (output.Length < _factor) throw new ArgumentException("Output must hold one sample per phase.",
            nameof(output));

        float[] history = _upHistory[channel];
        int position = _upPosition[channel];
        history[position] = input;

        for (int phase = 0; phase < _factor; phase++)
        {
            float[] taps = _phases[phase];
            double sum = 0;
            int index = position;
            for (int tap = 0; tap < _tapsPerPhase; tap++)
            {
                sum += taps[tap] * history[index];
                if (--index < 0) index = _tapsPerPhase - 1;
            }
            output[phase] = (float)sum;
        }

        _upPosition[channel] = position + 1 >= _tapsPerPhase ? 0 : position + 1;
    }

    /// <summary>Reduces <see cref="Factor"/> oversampled samples back to one.</summary>
    public float Downsample(int channel, ReadOnlySpan<float> input)
    {
        if (input.Length < _factor) throw new ArgumentException("One sample per phase is required.",
            nameof(input));

        float[] history = _downHistory[channel];
        int position = _downPosition[channel];

        for (int phase = 0; phase < _factor; phase++)
        {
            history[position] = input[phase];
            position = position + 1 >= history.Length ? 0 : position + 1;
        }
        _downPosition[channel] = position;

        // A straight convolution of the whole kernel across the oversampled history, evaluated only
        // on the samples that are kept. Written the polyphase way it would be the same arithmetic
        // arranged more cleverly and much harder to read; the saving is on the decimation side only,
        // and it is not worth the obscurity.
        // Anchored on the first sample of the group, not the last. Reading from the newest sample
        // puts the output (factor−1)/factor of a base sample late, which is a fraction no integer
        // latency can compensate — it showed as a round trip that correlated best at 31 samples
        // while the reported latency said 32.
        double sum = 0;
        int index = position - _factor;
        while (index < 0) index += history.Length;
        for (int tap = 0; tap < _kernel.Length; tap++)
        {
            sum += _kernel[tap] * history[index];
            if (--index < 0) index = history.Length - 1;
        }

        // Divided by the factor because the kernel was scaled by it for upsampling, where it has to
        // make up for the zeros inserted between samples. Nothing is inserted here.
        return (float)(sum / _factor);
    }

    public void Reset()
    {
        for (int c = 0; c < _channels; c++)
        {
            Array.Clear(_upHistory[c]);
            Array.Clear(_downHistory[c]);
            _upPosition[c] = 0;
            _downPosition[c] = 0;
        }
    }
}
