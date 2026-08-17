using WaveLab.Util;

namespace WaveLab.Audio.Dsp;

/// <summary>A disc equalisation curve, identified by the standard that defined it.</summary>
public enum RecordingCurve
{
    /// <summary>RIAA (1954): 3180 / 318 / 75 µs. What every LP cut after about 1955 uses.</summary>
    Riaa,

    /// <summary>RIAA with the IEC 1976 amendment: a 7950 µs rumble filter at 20.02 Hz as well.</summary>
    RiaaIec,

    /// <summary>Columbia LP (1948): 3180 / 318 / 100 µs. Also published as NAB / NARTB.</summary>
    ColumbiaLp,

    /// <summary>AES (1951): 3180 / 398 / 63.6 µs — a 400 Hz turnover and a gentler treble.</summary>
    Aes,

    /// <summary>
    /// A 500 Hz turnover with no treble rolloff, for the many coarse-groove 78s cut that way.
    /// </summary>
    Coarse78,
}

/// <summary>Whether to undo a curve or impose one.</summary>
public enum CurveDirection
{
    /// <summary>De-emphasis: undo the curve, as a preamp does on the way out of the disc.</summary>
    Playback,

    /// <summary>Pre-emphasis: impose the curve, as a cutting chain does on the way in.</summary>
    Record,
}

/// <summary>How the correction's phase behaves.</summary>
public enum CurvePhase
{
    /// <summary>
    /// Minimum phase, as the analog original is. The default, because a disc's curve was imposed
    /// by a minimum-phase network and its exact inverse is minimum phase too.
    /// </summary>
    Minimum,

    /// <summary>
    /// Linear phase: the same magnitude with no phase shift at all. Not what a preamp does, but it
    /// adds no dispersion of its own, which is what some transfers want.
    /// </summary>
    Linear,
}

/// <summary>
/// A curve as a set of time constants. Every published curve reduces to these, which is why they
/// are stored rather than the turnover and rolloff figures usually quoted alongside them.
/// </summary>
/// <param name="BassShelfUs">Where the bass boost stops rising, in µs. 3180 µs is 50.05 Hz.</param>
/// <param name="TurnoverUs">The bass turnover, in µs. 318 µs is 500.5 Hz.</param>
/// <param name="TrebleUs">The treble rolloff, in µs. 75 µs is 2122 Hz; zero means none.</param>
/// <param name="RumbleUs">An optional highpass below the shelf, in µs. Zero means none.</param>
public readonly record struct RecordingCurveSpec(
    RecordingCurve Curve, string Name, double BassShelfUs, double TurnoverUs,
    double TrebleUs, double RumbleUs = 0)
{
    private static double Corner(double microseconds) =>
        microseconds > 0 ? 1e6 / (2 * Math.PI * microseconds) : 0;

    /// <summary>The bass turnover in Hz, as the curve is usually quoted.</summary>
    public double TurnoverHz => Corner(TurnoverUs);

    /// <summary>The treble corner in Hz, or zero when the curve has no rolloff.</summary>
    public double TrebleHz => Corner(TrebleUs);

    public double ShelfHz => Corner(BassShelfUs);
    public double RumbleHz => Corner(RumbleUs);
}

/// <summary>
/// Disc equalisation: the RIAA curve and the ones that came before it, applied either way round.
/// </summary>
/// <remarks>
/// <para>
/// The curve is held as an exact analytic transfer function built from its time constants, and the
/// filter is designed to match it rather than approximated by a transform of the analog prototype.
/// A plain bilinear transform of the RIAA network is what most implementations do and it is wrong by
/// a noticeable margin near Nyquist at 44.1 kHz, because the bilinear map compresses the frequency
/// axis exactly where the last time constant is still doing work. Sampling the true response and
/// designing a filter to it has no such error: the accuracy is set by the filter length alone, and
/// is measured in <c>RecordingCurveTests</c>.
/// </para>
/// <para>
/// The correction defaults to <b>minimum phase</b>, because the curve on the disc was imposed by a
/// minimum-phase analog network and its exact inverse is minimum phase as well. A linear-phase
/// version of the same magnitude is offered for transfers that would rather add no dispersion at
/// all; it is a different thing from what a preamp does, not a better-behaved version of it.
/// </para>
/// <para>
/// The historical curves are the commonly published time-constant triples. Labels and practice
/// varied by company and by year far more than the tidy tables suggest, so these are a starting
/// point for a transfer rather than an authority on what any given disc was cut with.
/// </para>
/// </remarks>
public static class RecordingCurves
{
    /// <summary>Level is always referred to 1 kHz, as every published curve is.</summary>
    public const double ReferenceHz = 1_000;

    private static readonly RecordingCurveSpec[] Specs =
    [
        new(RecordingCurve.Riaa, "RIAA (1954)", 3180, 318, 75),
        new(RecordingCurve.RiaaIec, "RIAA + IEC rumble", 3180, 318, 75, RumbleUs: 7950),
        new(RecordingCurve.ColumbiaLp, "Columbia LP (1948)", 3180, 318, 100),
        new(RecordingCurve.Aes, "AES (1951)", 3180, 398, 63.6),
        new(RecordingCurve.Coarse78, "78 rpm, 500 Hz turnover", 3180, 318, 0),
    ];

    public static IReadOnlyList<RecordingCurveSpec> All => Specs;

    public static RecordingCurveSpec Spec(RecordingCurve curve) =>
        Array.Find(Specs, s => s.Curve == curve) is { Name: not null } found
            ? found
            : Specs[0];

    // ── the exact curve ──────────────────────────────────────────

    /// <summary>
    /// Magnitude of the playback curve at a frequency, relative to 1 kHz. This is the definition
    /// everything else is measured against.
    /// </summary>
    public static double Magnitude(in RecordingCurveSpec spec, double frequency) =>
        Raw(spec, frequency) / Raw(spec, ReferenceHz);

    /// <summary>The same in decibels, which is how the curve is published.</summary>
    public static double ResponseDb(in RecordingCurveSpec spec, double frequency) =>
        20 * Math.Log10(Math.Max(1e-12, Magnitude(spec, frequency)));

    /// <summary>
    /// Unnormalised |H(jω)| = |1 + jωT₂| / (|1 + jωT₁|·|1 + jωT₃|), times the rumble highpass when
    /// the curve carries one.
    /// </summary>
    private static double Raw(in RecordingCurveSpec spec, double frequency)
    {
        double omega = 2 * Math.PI * Math.Max(frequency, 1e-6);
        double shelf = spec.BassShelfUs * 1e-6;
        double turnover = spec.TurnoverUs * 1e-6;
        double treble = spec.TrebleUs * 1e-6;
        double rumble = spec.RumbleUs * 1e-6;

        double value = Hypot(omega * turnover);
        if (shelf > 0) value /= Hypot(omega * shelf);
        if (treble > 0) value /= Hypot(omega * treble);
        if (rumble > 0)
        {
            double x = omega * rumble;
            value *= x / Hypot(x);
        }
        return value;

        static double Hypot(double x) => Math.Sqrt(1 + x * x);
    }

    // ── filter design ────────────────────────────────────────────

    /// <summary>Default filter length. Long enough that the 50 Hz shelf is resolved properly.</summary>
    public const int DefaultTaps = 8192;

    /// <summary>
    /// Designs an impulse response matching the curve exactly in magnitude.
    /// </summary>
    /// <remarks>
    /// The design grid is four times the filter length, so the response is sampled far more finely
    /// than the filter can resolve and truncation is what limits accuracy rather than aliasing of
    /// the design itself. The minimum-phase version comes from the real cepstrum: taking the log
    /// magnitude into the quefrency domain, discarding the anticausal half and coming back gives the
    /// unique minimum-phase response with that magnitude.
    /// </remarks>
    public static float[] Design(in RecordingCurveSpec spec, int sampleRate, CurveDirection direction,
        CurvePhase phase, int taps = DefaultTaps)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (taps < 16) throw new ArgumentOutOfRangeException(nameof(taps));

        taps = Fft.NextPowerOfTwo(taps);
        int size = taps * 4;

        var magnitude = new double[size];
        for (int k = 0; k <= size / 2; k++)
        {
            double frequency = (double)k * sampleRate / size;
            double value = Magnitude(spec, frequency);
            if (direction == CurveDirection.Record) value = 1 / Math.Max(value, 1e-9);

            // Nothing useful lives above the audio band, and letting the curve run to Nyquist gives
            // the design a discontinuity to fit that costs ringing everywhere else.
            magnitude[k] = Math.Max(value, 1e-9);
            if (k > 0 && k < size / 2) magnitude[size - k] = magnitude[k];
        }

        double[] impulse = phase == CurvePhase.Minimum
            ? MinimumPhase(magnitude, size)
            : LinearPhase(magnitude, size);

        var kernel = new float[taps];
        if (phase == CurvePhase.Minimum)
        {
            // Causal already: take the front and taper the tail so truncation does not ripple.
            for (int i = 0; i < taps; i++)
                kernel[i] = (float)(impulse[i] * TailWindow(i, taps));
        }
        else
        {
            // Symmetric about zero: take half from each side and window the pair.
            int half = taps / 2;
            for (int i = 0; i < taps; i++)
            {
                int source = (i - half + size) % size;
                kernel[i] = (float)(impulse[source] * Blackman(i, taps));
            }
        }
        return kernel;
    }

    /// <summary>Zero-phase impulse response: the inverse transform of a real, even spectrum.</summary>
    private static double[] LinearPhase(double[] magnitude, int size)
    {
        var re = (double[])magnitude.Clone();
        var im = new double[size];
        Fft.Inverse(re, im);
        return re;
    }

    /// <summary>
    /// Minimum-phase impulse response with the given magnitude, by folding the real cepstrum.
    /// </summary>
    private static double[] MinimumPhase(double[] magnitude, int size)
    {
        var re = new double[size];
        var im = new double[size];
        for (int i = 0; i < size; i++) re[i] = Math.Log(magnitude[i]);

        // Into the quefrency domain, where a minimum-phase signal is causal.
        Fft.Inverse(re, im);

        var foldRe = new double[size];
        var foldIm = new double[size];
        foldRe[0] = re[0];
        foldIm[0] = im[0];
        for (int i = 1; i < size / 2; i++)
        {
            foldRe[i] = 2 * re[i];
            foldIm[i] = 2 * im[i];
        }
        foldRe[size / 2] = re[size / 2];
        foldIm[size / 2] = im[size / 2];

        Fft.Forward(foldRe, foldIm);

        // exp of the complex log is the minimum-phase spectrum.
        for (int i = 0; i < size; i++)
        {
            double scale = Math.Exp(foldRe[i]);
            double angle = foldIm[i];
            foldRe[i] = scale * Math.Cos(angle);
            foldIm[i] = scale * Math.Sin(angle);
        }

        Fft.Inverse(foldRe, foldIm);
        return foldRe;
    }

    /// <summary>Flat for most of the kernel, cosine to zero over the last eighth.</summary>
    private static double TailWindow(int index, int length)
    {
        int taper = length / 8;
        int start = length - taper;
        if (index < start) return 1;
        return 0.5 + 0.5 * Math.Cos(Math.PI * (index - start) / taper);
    }

    private static double Blackman(int index, int length) =>
        0.42 - 0.5 * Math.Cos(2 * Math.PI * index / (length - 1))
             + 0.08 * Math.Cos(4 * Math.PI * index / (length - 1));

    // ── application ──────────────────────────────────────────────

    /// <summary>
    /// Applies the curve to every channel in place, by overlap-add convolution.
    /// </summary>
    /// <remarks>
    /// The output is aligned with the input: a linear-phase kernel is delayed by half its length and
    /// that delay is taken back off, so applying a curve and then its inverse returns the audio where
    /// it started rather than half a kernel later.
    /// </remarks>
    public static void Apply(float[][] channels, in RecordingCurveSpec spec, int sampleRate,
        CurveDirection direction, CurvePhase phase, int taps = DefaultTaps,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        float[] kernel = Design(spec, sampleRate, direction, phase, taps);
        int delay = phase == CurvePhase.Linear ? kernel.Length / 2 : 0;

        for (int c = 0; c < channels.Length; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Convolve(channels[c], kernel, delay, cancellationToken,
                SubProgress.Slice(progress, c, channels.Length));
        }
        progress?.Report(1);
    }

    /// <summary>Overlap-add FFT convolution, writing the aligned result back in place.</summary>
    internal static void Convolve(float[] signal, float[] kernel, int delay,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(kernel);
        if (signal.Length == 0 || kernel.Length == 0) return;

        int size = Fft.NextPowerOfTwo(kernel.Length * 4);
        int block = size - kernel.Length + 1;
        int bins = size / 2 + 1;

        var filterRe = new float[bins];
        var filterIm = new float[bins];
        var padded = new float[size];
        kernel.CopyTo(padded, 0);
        Fft.RealForward(padded, filterRe, filterIm);

        // Everything a block's convolution puts past its own span, waiting to be added to the next.
        var carry = new float[size];
        var frame = new float[size];
        var re = new float[bins];
        var im = new float[bins];

        // Runs past the end of the signal by the alignment delay, because the samples that belong at
        // the very end of the output are produced by blocks that start beyond it.
        int total = signal.Length + delay;

        for (int start = 0; start < total; start += block)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Array.Clear(frame);
            int available = Math.Clamp(signal.Length - start, 0, block);
            if (available > 0) signal.AsSpan(start, available).CopyTo(frame);

            Fft.RealForward(frame, re, im);
            for (int b = 0; b < bins; b++)
            {
                float rr = re[b] * filterRe[b] - im[b] * filterIm[b];
                float ii = re[b] * filterIm[b] + im[b] * filterRe[b];
                re[b] = rr;
                im[b] = ii;
            }
            Fft.RealInverse(re, im, frame);

            // Writing in place is safe because every position written sits at or behind the input
            // this block already consumed: the delay only ever moves the output earlier.
            for (int i = 0; i < block; i++)
            {
                int position = start + i - delay;
                if ((uint)position < (uint)signal.Length) signal[position] = frame[i] + carry[i];
            }

            for (int j = 0; j + block < size; j++) carry[j] = frame[block + j] + carry[block + j];
            Array.Clear(carry, size - block, block);

            progress?.Report(Math.Min(1, (start + block) / (double)total));
        }

        progress?.Report(1);
    }
}
