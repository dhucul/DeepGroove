namespace WaveLab.Audio.Dsp;

/// <summary>Analysis settings for <see cref="Spectrogram"/>.</summary>
/// <param name="FftSize">Transform length; power of two, 256…16384.</param>
/// <param name="Hop">Frame advance in samples; must divide <paramref name="FftSize"/>.</param>
/// <param name="Window">Analysis window. Blackman-Harris by default: a spectrogram is read for what
/// is <em>near</em> a loud partial, so sidelobe suppression matters more than the last of the
/// resolution.</param>
/// <param name="Reassign">Relocate each bin to its own centre of energy.</param>
/// <param name="FloorDb">Level mapped to the bottom of the scale.</param>
/// <param name="CeilingDb">Level mapped to the top of the scale.</param>
public readonly record struct SpectrogramSettings(
    int FftSize = 2048,
    int Hop = 512,
    WindowKind Window = WindowKind.BlackmanHarris,
    bool Reassign = true,
    double FloorDb = -96,
    double CeilingDb = 0)
{
    /// <remarks>
    /// Spelled out rather than written <c>new()</c>. On a record struct the parameterless form
    /// zero-initialises every field instead of applying the defaults declared above, which here
    /// would mean a zero FFT size and a 0 dB floor — every level clamped to zero and every analysis
    /// rejected as invalid.
    /// </remarks>
    public static SpectrogramSettings Default { get; } = new(
        FftSize: 2048,
        Hop: 512,
        Window: WindowKind.BlackmanHarris,
        Reassign: true,
        FloorDb: -96,
        CeilingDb: 0);
}

/// <summary>A computed time-frequency image: magnitudes in dB, one row per frame.</summary>
public sealed class SpectrogramData
{
    internal SpectrogramData(int frames, int bins, float[] magnitudeDb, int sampleRate,
        int fftSize, int hop, int firstSample, double[]? binFrequencies = null)
    {
        Frames = frames;
        Bins = bins;
        MagnitudeDb = magnitudeDb;
        SampleRate = sampleRate;
        FftSize = fftSize;
        Hop = hop;
        FirstSample = firstSample;
        BinFrequencies = binFrequencies;
    }

    /// <summary>
    /// Centre frequency of every bin, when they are not evenly spaced. Null for the linear analysis,
    /// whose bins are at <c>bin·rate/size</c> and need no table.
    /// </summary>
    /// <remarks>
    /// This is what lets a constant-Q analysis travel through the same display as a linear one. The
    /// image asks which bins fall in a row rather than assuming an arithmetic there, so the two only
    /// have to agree about what a bin's frequency <em>is</em>.
    /// </remarks>
    public double[]? BinFrequencies { get; }

    public int Frames { get; }
    public int Bins { get; }
    public int SampleRate { get; }
    public int FftSize { get; }
    public int Hop { get; }

    /// <summary>Sample position of the centre of frame 0.</summary>
    public int FirstSample { get; }

    /// <summary>Row-major, <see cref="Frames"/> × <see cref="Bins"/>.</summary>
    public float[] MagnitudeDb { get; }

    public float this[int frame, int bin] => MagnitudeDb[frame * Bins + bin];

    /// <summary>Centre frequency of a bin, in Hz.</summary>
    public double Frequency(int bin) => BinFrequencies is { } table
        ? table[Math.Clamp(bin, 0, table.Length - 1)]
        : (double)bin * SampleRate / FftSize;

    /// <summary>
    /// Where a frequency falls on the bin axis — the inverse of <see cref="Frequency"/>, and
    /// fractional, because a row of pixels rarely lands on a bin boundary.
    /// </summary>
    public double BinForFrequency(double hz)
    {
        if (BinFrequencies is not { Length: > 1 } table)
            return hz * FftSize / SampleRate;

        // Geometric, which is what a constant-Q axis is, so the inverse is a logarithm rather than
        // a search. Taken from the two ends so it does not assume which spacing was chosen.
        double lowest = table[0], highest = table[^1];
        if (!(hz > lowest)) return 0;
        if (hz >= highest) return table.Length - 1;

        double ratio = Math.Log(highest / lowest);
        return ratio > 0 ? Math.Log(hz / lowest) / ratio * (table.Length - 1) : 0;
    }

    /// <summary>Sample position of a frame's centre.</summary>
    public int SampleAt(int frame) => FirstSample + frame * Hop;
}

/// <summary>
/// Short-time spectral analysis for display, with optional time-frequency reassignment.
/// </summary>
/// <remarks>
/// <para>
/// The spectrogram this replaces was a fixed 1024-point Hann transform rendered into a fixed
/// 800×256 bitmap with every channel summed to mono — adequate as a readout, not as something to
/// edit in.
/// </para>
/// <para>
/// Reassignment (Auger &amp; Flandrin) is the reason a professional spectral display looks sharp
/// where an ordinary one looks smeared. An ordinary spectrogram places all of a bin's energy at the
/// centre of its cell, which is almost never where the energy actually is: a partial lying between
/// two bins is drawn as a band across both. Reassignment computes where the energy in each cell
/// really sits — in both time and frequency — from two extra transforms per frame, one with the
/// window multiplied by time and one with the window's derivative, and moves it there. Partials
/// collapse to lines and transients to vertical strokes.
/// </para>
/// </remarks>
public static class Spectrogram
{
    public const int MinimumFftSize = 256;
    public const int MaximumFftSize = 16384;

    /// <summary>Analyses <paramref name="count"/> samples from <paramref name="from"/>.</summary>
    public static SpectrogramData Analyze(float[] samples, int from, int count, int sampleRate,
        SpectrogramSettings settings = default,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (settings.FftSize == 0) settings = SpectrogramSettings.Default;

        int n = settings.FftSize;
        int hop = settings.Hop;
        if (n < MinimumFftSize || n > MaximumFftSize || (n & (n - 1)) != 0)
            throw new ArgumentException(
                $"FFT size must be a power of two between {MinimumFftSize} and {MaximumFftSize}.", nameof(settings));
        if (hop <= 0 || n % hop != 0)
            throw new ArgumentException("Hop must divide the FFT size.", nameof(settings));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int bins = n / 2 + 1;
        int frames = count <= 0 ? 0 : Math.Max(1, count / hop);
        var image = new float[frames * bins];
        var floor = (float)settings.FloorDb;
        Array.Fill(image, floor);
        if (frames == 0)
            return new SpectrogramData(0, bins, image, sampleRate, n, hop, from);

        float[] window = WindowFunctions.Create(settings.Window, n, periodic: true);
        float[]? timeWindow = null;
        float[]? derivativeWindow = null;
        if (settings.Reassign)
        {
            (timeWindow, derivativeWindow) = ReassignmentWindows(window, n);
        }

        // Energy is accumulated rather than assigned so that reassignment can move it between
        // cells; a plain analysis simply never moves anything.
        var power = new double[frames * bins];

        var frame = new float[n];
        var binRe = new float[bins];
        var binIm = new float[bins];
        var timeRe = new float[bins];
        var timeIm = new float[bins];
        var derivativeRe = new float[bins];
        var derivativeIm = new float[bins];

        int half = n / 2;
        for (int f = 0; f < frames; f++)
        {
            if ((f & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)f / frames);
            }

            int centre = from + f * hop;
            int start = centre - half;
            for (int i = 0; i < n; i++)
            {
                int index = start + i;
                frame[i] = (uint)index < (uint)samples.Length ? samples[index] * window[i] : 0f;
            }
            Fft.RealForward(frame, binRe, binIm);

            if (!settings.Reassign)
            {
                for (int b = 0; b < bins; b++)
                    power[f * bins + b] += (double)binRe[b] * binRe[b] + (double)binIm[b] * binIm[b];
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                int index = start + i;
                float value = (uint)index < (uint)samples.Length ? samples[index] : 0f;
                frame[i] = value * timeWindow![i];
            }
            Fft.RealForward(frame, timeRe, timeIm);

            for (int i = 0; i < n; i++)
            {
                int index = start + i;
                float value = (uint)index < (uint)samples.Length ? samples[index] : 0f;
                frame[i] = value * derivativeWindow![i];
            }
            Fft.RealForward(frame, derivativeRe, derivativeIm);

            for (int b = 0; b < bins; b++)
            {
                double re = binRe[b], im = binIm[b];
                double energy = re * re + im * im;
                if (energy <= 1e-20) continue;

                // t̂ = t + Re{ X_Tw · conj(X_w) } / |X_w|², in samples.
                double timeOffset = (timeRe[b] * re + timeIm[b] * im) / energy;

                // f̂ = f - Im{ X_Dw · conj(X_w) } / |X_w|², converted from radians/sample to bins.
                double frequencyOffset =
                    -(derivativeIm[b] * re - derivativeRe[b] * im) / energy * n / (2 * Math.PI);

                int targetFrame = f + (int)Math.Round(timeOffset / hop);
                int targetBin = b + (int)Math.Round(frequencyOffset);
                if ((uint)targetFrame >= (uint)frames || (uint)targetBin >= (uint)bins) continue;

                power[targetFrame * bins + targetBin] += energy;
            }
        }

        // The window's coherent gain is divided out so that a full-scale sine reads 0 dB whichever
        // window is chosen, rather than a level that silently depends on the analysis settings.
        double windowSum = 0;
        foreach (float value in window) windowSum += value;
        double norm = 2.0 / Math.Max(1e-9, windowSum);
        double ceiling = settings.CeilingDb;

        for (int i = 0; i < power.Length; i++)
        {
            double magnitude = Math.Sqrt(power[i]) * norm;
            double db = 20 * Math.Log10(Math.Max(1e-12, magnitude));
            image[i] = (float)Math.Clamp(db, settings.FloorDb, ceiling);
        }

        progress?.Report(1);
        return new SpectrogramData(frames, bins, image, sampleRate, n, hop, from);
    }

    /// <summary>
    /// The two companion windows reassignment needs: the analysis window multiplied by time
    /// (measured in samples from the frame centre), and its derivative with respect to time.
    /// </summary>
    internal static (float[] TimeWeighted, float[] Derivative) ReassignmentWindows(float[] window, int n)
    {
        var timeWeighted = new float[n];
        var derivative = new float[n];
        double centre = (n - 1) / 2.0;

        for (int i = 0; i < n; i++)
        {
            timeWeighted[i] = (float)((i - centre) * window[i]);

            // Central difference; the ends fall back to a one-sided difference.
            float previous = i > 0 ? window[i - 1] : window[0];
            float next = i < n - 1 ? window[i + 1] : window[n - 1];
            int span = (i > 0 ? 1 : 0) + (i < n - 1 ? 1 : 0);
            derivative[i] = span == 0 ? 0f : (next - previous) / span;
        }
        return (timeWeighted, derivative);
    }
}
