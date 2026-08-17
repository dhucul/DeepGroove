namespace WaveLab.Audio.Dsp;

/// <summary>What kind of dither, if any, is applied when reducing bit depth.</summary>
public enum DitherKind
{
    /// <summary>None: truncation distortion, correlated with the signal and audible on fades.</summary>
    None,

    /// <summary>Flat triangular dither. Correct, unobtrusive, and the safe default.</summary>
    FlatTpdf,

    /// <summary>Triangular dither with its own spectrum tilted upward, away from where hearing is keenest.</summary>
    HighPassTpdf,

    /// <summary>Triangular dither plus noise shaping weighted to the ear's sensitivity.</summary>
    NoiseShapedE,

    /// <summary>A gentler shaping curve — less noise moved, and less of it piled up at the top.</summary>
    NoiseShapedF,
}

/// <summary>
/// Dither and noise shaping for bit-depth reduction.
/// </summary>
/// <remarks>
/// <para>
/// Truncating to a shorter word length without dither does not add noise; it adds <em>distortion</em>
/// correlated with the signal, which is why an undithered fade to silence grinds rather than fades.
/// Adding a triangular random signal of the right size before quantising decorrelates the error
/// completely: what is left is a steady, signal-independent hiss, which is a far better thing to
/// have. Triangular rather than rectangular because that is what makes the error's <em>variance</em>
/// independent of the signal too, not merely its mean — rectangular dither leaves the noise floor
/// modulating with the music.
/// </para>
/// <para>
/// Noise shaping then moves that hiss to where it matters least. The total noise power cannot be
/// reduced — it can only be redistributed — so the shaper trades more noise above 15 kHz, where
/// hearing is poor, for less between 1 and 5 kHz, where it is keenest. The audible result is several
/// decibels quieter even though the measured wideband noise is louder, which is the whole point and
/// also why a wideband number is the wrong way to judge it.
/// </para>
/// <para>
/// The shaping is done by feeding the quantiser's own error back through a filter, so the quantiser
/// must be part of this class rather than something the caller does afterwards: there is no error to
/// feed back until the quantisation has happened.
/// </para>
/// </remarks>
public sealed class Dither
{
    /// <summary>Length of the error-feedback filter the shaping curves are realised with.</summary>
    private const int ShaperTaps = 16;

    /// <summary>Samples of digital silence before dither is switched off.</summary>
    private const int BlankingSamples = 8_000;

    private readonly DitherKind _kind;
    private readonly double[] _coefficients;
    private readonly double[][] _errors;      // [channel][tap]
    private readonly double[] _previousNoise; // for the high-pass variant
    private readonly int[] _silence;
    private readonly Random _random;
    private readonly bool _autoBlank;

    /// <summary>One least-significant bit at the target depth, in the ±1 sample scale.</summary>
    public double Lsb { get; }

    /// <summary>Largest integer the target depth can hold.</summary>
    public double FullScale { get; }

    public DitherKind Kind => _kind;

    /// <param name="bits">Target word length. 16 and 24 are the usual cases.</param>
    /// <param name="sampleRate">
    /// Needed because the shaping curve is designed against the ear, and where the ear is deaf is a
    /// fact about frequency in hertz, not about fractions of Nyquist.
    /// </param>
    /// <param name="autoBlank">Stop dithering after a long run of digital silence.</param>
    public Dither(DitherKind kind, int bits, int channels, int sampleRate = 44_100,
        bool autoBlank = true, int seed = 0x5EED)
    {
        if (bits is < 2 or > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _kind = kind;
        _autoBlank = autoBlank;
        _random = new Random(seed);
        FullScale = Math.Pow(2, bits - 1);
        Lsb = 1.0 / FullScale;

        _coefficients = kind switch
        {
            DitherKind.NoiseShapedE => DesignShaper(sampleRate, strength: 1.0),
            DitherKind.NoiseShapedF => DesignShaper(sampleRate, strength: 0.5),
            _ => [],
        };

        _errors = new double[channels][];
        for (int c = 0; c < channels; c++) _errors[c] = new double[Math.Max(1, _coefficients.Length)];
        _previousNoise = new double[channels];
        _silence = new int[channels];
    }

    /// <summary>
    /// Quantises one sample to the target depth, returning it still in the ±1 scale.
    /// </summary>
    public double Process(int channel, double sample)
    {
        if (_kind == DitherKind.None) return Math.Round(sample * FullScale) * Lsb;

        // Auto-blanking. Digital silence should stay silent: a run-out or a gap between tracks with
        // dither still running is a hiss where the record had none, and it is audible precisely
        // because there is nothing else there.
        if (_autoBlank)
        {
            if (Math.Abs(sample) < Lsb * 0.5)
            {
                if (_silence[channel] < BlankingSamples) _silence[channel]++;
            }
            else
            {
                _silence[channel] = 0;
            }

            if (_silence[channel] >= BlankingSamples)
            {
                Array.Clear(_errors[channel]);
                return Math.Round(sample * FullScale) * Lsb;
            }
        }

        double[] history = _errors[channel];

        // The quantiser's past errors, filtered: this is what shapes the noise.
        double feedback = 0;
        for (int k = 0; k < _coefficients.Length; k++) feedback += _coefficients[k] * history[k];

        // Subtracted, not added. The noise transfer function is 1 − Σcₖz⁻ᵏ, and these coefficients
        // sum to about 0.75, so subtracting gives a quarter of the noise at DC and roughly twenty
        // times as much at Nyquist — which is the shape wanted. Adding them instead gives 1.75 at DC:
        // measured, that raised the 1–5 kHz band by 2.7 dB, the exact opposite of the point.
        double wanted = sample * FullScale - feedback + Noise(channel);
        double quantised = Math.Round(wanted);

        // Shift the history and record what this quantisation cost.
        for (int k = history.Length - 1; k > 0; k--) history[k] = history[k - 1];
        history[0] = quantised - wanted;

        return Math.Clamp(quantised, -FullScale, FullScale - 1) * Lsb;
    }

    /// <summary>
    /// Designs the error-feedback filter from the threshold of hearing, rather than using published
    /// coefficients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The optimum shape for the noise is the shape of the ear's own threshold: put noise where the
    /// threshold is high and take it out where the threshold is low. So the target magnitude for the
    /// noise transfer function is the threshold curve itself, and the filter that realises it is its
    /// minimum-phase factorisation — which is exactly what an error-feedback loop can implement,
    /// because minimum phase is what makes it causal.
    /// </para>
    /// <para>
    /// The target is normalised so that the mean of its logarithm is zero. That is not tidying: the
    /// Gerzon-Craven theorem says a realisable monic shaper cannot do better, and a curve that
    /// integrates to anything else is asking for total noise power to change, which no
    /// redistribution can deliver. Normalising is what turns a wish into a filter.
    /// </para>
    /// <para>
    /// Designing rather than quoting also means the curve can be stated and checked. A first version
    /// used coefficients recalled from the literature, applied them with the sign that seemed
    /// natural, and <em>raised</em> the 1–5 kHz band by 2.7 dB — the exact opposite of the intent,
    /// and invisible without measuring the spectrum.
    /// </para>
    /// </remarks>
    internal static double[] DesignShaper(int sampleRate, double strength)
    {
        const int size = 2048;
        var magnitude = new double[size];
        double mean = 0;

        for (int k = 0; k <= size / 2; k++)
        {
            double frequency = (double)k * sampleRate / size;
            double logMagnitude = strength * ThresholdDb(frequency) * Math.Log(10) / 20;
            magnitude[k] = logMagnitude;

            // DC and Nyquist appear once on the full grid; everything between appears twice.
            mean += k == 0 || k == size / 2 ? logMagnitude : 2 * logMagnitude;
        }
        mean /= size;

        for (int k = 0; k <= size / 2; k++)
        {
            magnitude[k] = Math.Exp(magnitude[k] - mean);
            if (k > 0 && k < size / 2) magnitude[size - k] = magnitude[k];
        }

        double[] impulse = MinimumPhase.FromMagnitude(magnitude);
        double lead = impulse[0];
        if (Math.Abs(lead) < 1e-9) return [];

        // N(z) = 1 − Σ cₖz⁻ᵏ, and the loop subtracts, so the coefficients are the negated tail of
        // the impulse response normalised to a leading one.
        var coefficients = new double[ShaperTaps];
        for (int k = 0; k < ShaperTaps; k++) coefficients[k] = -impulse[k + 1] / lead;
        return coefficients;
    }

    /// <summary>
    /// The absolute threshold of hearing in dB, by Terhardt's approximation — high where the ear is
    /// deaf, low where it is keen, which is precisely the shape wanted for the noise.
    /// </summary>
    private static double ThresholdDb(double frequency)
    {
        double kHz = Math.Clamp(frequency, 20, 20_000) / 1000.0;
        double threshold = 3.64 * Math.Pow(kHz, -0.8)
                         - 6.5 * Math.Exp(-0.6 * (kHz - 3.3) * (kHz - 3.3))
                         + 1e-3 * Math.Pow(kHz, 4);

        // Bounded, or the curve's steep ends ask for a shaper with more dynamic range than a short
        // filter can deliver, and the factorisation spends all its taps on the extremes.
        return Math.Clamp(threshold, -10, 40);
    }

    /// <summary>Triangular noise of ±1 LSB, in quantiser steps.</summary>
    private double Noise(int channel)
    {
        double rectangular = _random.NextDouble() - 0.5;

        if (_kind != DitherKind.HighPassTpdf)
        {
            // Two independent rectangular draws sum to a triangular distribution, which is what
            // makes the error's variance independent of the signal as well as its mean.
            return rectangular + (_random.NextDouble() - 0.5);
        }

        // The difference of successive rectangular draws is also triangular, but its own spectrum
        // rises with frequency — the same total noise, less of it where hearing is keenest.
        double value = rectangular - _previousNoise[channel];
        _previousNoise[channel] = rectangular;
        return value;
    }

    public void Reset()
    {
        foreach (double[] history in _errors) Array.Clear(history);
        Array.Clear(_previousNoise);
        Array.Clear(_silence);
    }
}

/// <summary>TPDF dither at ±1 LSB for 16-bit export.</summary>
/// <remarks>
/// Retained for the codecs that add noise to an already-scaled value and quantise themselves.
/// New work should use <see cref="Dither"/>, which owns the quantiser and can therefore shape.
/// </remarks>
public sealed class TpdfDither
{
    private readonly Random _rng = new(0x5EED);

    /// <summary>Triangular noise in [-1, 1] LSB (already scaled for 16-bit quantization).</summary>
    public double Next() => _rng.NextDouble() - _rng.NextDouble();
}
