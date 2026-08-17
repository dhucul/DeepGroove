using WaveLab.Audio.Dsp;

namespace WaveLab.Audio.Effects;

/// <summary>
/// Removes sibilance by attenuating the bins that carry it, not the band that contains it.
/// </summary>
/// <remarks>
/// <para>
/// The usual de-esser is a compressor on a high band: when it detects an "s" it ducks everything
/// above 5 kHz, cymbals and air included, for as long as the "s" lasts. What is heard is not a
/// quieter "s" so much as the top of the record moving.
/// </para>
/// <para>
/// Working in the frequency domain lets the attenuation land only where the sibilance actually is.
/// A frame is analysed, the bins that look sibilant are turned down individually, and the rest of
/// the band is passed through untouched — so a cymbal ringing through a vocal "s" keeps its own
/// bins even while the "s" loses its.
/// </para>
/// <para>
/// Sibilance is recognised by two things together, because neither alone is sufficient: the share of
/// the frame's energy sitting in the sibilant range, and the <b>zero-crossing rate</b>, which is high
/// for noise-like sounds and low for pitched ones. A cymbal has the energy but so does a bright
/// vowel; the crossing rate is what separates the hiss of an "s" from the harmonics of an "ee".
/// </para>
/// </remarks>
public sealed class DeEsserEffect : EffectBase
{
    private const int FftSize = 1024;
    private const int Hop = FftSize / 4;

    private static readonly EffectParam[] P =
    [
        new("freq", "FROM", 2_000, 12_000, 5_000, EffectParam.Hz),
        new("threshold", "THRESHOLD", -60, 0, -30, EffectParam.Db),
        new("range", "RANGE", 0, 24, 10, EffectParam.Db),
        new("sharpness", "SHARPNESS", 0, 1, 0.5, EffectParam.Pct),
    ];

    private Stft? _stft;
    private float[][] _input = [];
    private float[][] _output = [];
    private DeEsserParameters _parameters = new(5_000, 0.03, 0.3, 0.5);
    private double _reduction;

    private sealed record DeEsserParameters(double FromHz, double Threshold, double Floor, double Sharpness);

    public override string TypeId => "deesser";
    public override string DisplayName => "De-Esser";
    public override IReadOnlyList<EffectParam> Params => P;

    /// <summary>A frame of lookahead, which offline rendering compensates for.</summary>
    public override int LatencySamples => FftSize;

    public override string Readout
    {
        get
        {
            double reduction = Volatile.Read(ref _reduction);
            return reduction < 0.05 ? "—" : $"{reduction:0.0} dB";
        }
    }

    protected override void OnConfigure()
    {
        Volatile.Write(ref _stft, new Stft(FftSize, Hop));
        _input = new float[ChannelCount][];
        _output = new float[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _input[c] = new float[FftSize * 4];
            _output[c] = new float[FftSize * 4];
        }
        OnParamsChanged();
    }

    protected override void OnParamsChanged() =>
        Volatile.Write(ref _parameters, new DeEsserParameters(
            Math.Min(GetParam("freq"), SampleRate * 0.45),
            Math.Pow(10, GetParam("threshold") / 20.0),
            Math.Pow(10, -GetParam("range") / 20.0),
            GetParam("sharpness")));

    public override void ResetState()
    {
        foreach (float[] channel in _input) Array.Clear(channel);
        foreach (float[] channel in _output) Array.Clear(channel);
        Volatile.Write(ref _reduction, 0);
    }

    public override void Process(float[] buffer, int offset, int count)
    {
        Stft? stft = Volatile.Read(ref _stft);
        var parameters = Volatile.Read(ref _parameters);
        if (stft == null || _input.Length != ChannelCount) return;

        int frames = count / ChannelCount;
        if (frames == 0) return;

        // De-interleave into the working buffers, process a whole block, interleave back. The STFT
        // wants a contiguous channel; the rack hands over interleaved audio.
        for (int c = 0; c < ChannelCount; c++)
        {
            if (_input[c].Length < frames) _input[c] = new float[frames];
            if (_output[c].Length < frames) _output[c] = new float[frames];
            for (int f = 0; f < frames; f++) _input[c][f] = buffer[offset + f * ChannelCount + c];
        }

        double worst = 0;
        int bins = FftSize / 2 + 1;
        int fromBin = Math.Clamp((int)(parameters.FromHz * FftSize / SampleRate), 1, bins - 1);

        for (int c = 0; c < ChannelCount; c++)
        {
            // The array rather than a span: the frame callback is a lambda and a span cannot be
            // captured by one.
            float[] source = _input[c];
            Span<float> destination = _output[c].AsSpan(0, frames);

            stft.Process(source.AsSpan(0, frames), destination, (_, start, re, im) =>
            {
                double sibilant = 0, total = 0;
                for (int b = 1; b < bins; b++)
                {
                    double power = (double)re[b] * re[b] + (double)im[b] * im[b];
                    total += power;
                    if (b >= fromBin) sibilant += power;
                }
                if (total <= 1e-20) return;

                double share = sibilant / total;
                double crossings = ZeroCrossingRate(source, start, FftSize, frames);

                // Both tests, multiplied: energy up top without the noisiness is a bright vowel or a
                // cymbal, and noisiness without the energy is not sibilance at all.
                double sibilance = share * crossings;
                double level = Math.Sqrt(sibilant / bins);
                if (level < parameters.Threshold || sibilance < 0.08) return;

                double strength = Math.Clamp((sibilance - 0.08) / 0.25, 0, 1)
                                * Math.Clamp(level / Math.Max(parameters.Threshold, 1e-9) - 1, 0, 1);
                double gain = 1 - (1 - parameters.Floor) * strength;
                double reduction = -20 * Math.Log10(Math.Max(gain, 1e-9));
                if (reduction > worst) worst = reduction;

                for (int b = fromBin; b < bins; b++)
                {
                    // Sharpness tapers the attenuation in from the corner rather than applying it as
                    // a step, which would ring.
                    double into = (b - fromBin) / (double)Math.Max(1, bins - fromBin);
                    double taper = parameters.Sharpness + (1 - parameters.Sharpness) * Math.Min(1, into * 4);
                    var applied = (float)(1 - (1 - gain) * taper);
                    re[b] *= applied;
                    im[b] *= applied;
                }
            });

            for (int f = 0; f < frames; f++) buffer[offset + f * ChannelCount + c] = destination[f];
        }

        Volatile.Write(ref _reduction, worst);
    }

    /// <summary>
    /// How often the waveform crosses zero over a frame, normalised. Noise-like sounds cross far
    /// more often than pitched ones, which is what separates an "s" from a bright vowel.
    /// </summary>
    private static double ZeroCrossingRate(float[] signal, int start, int length, int available)
    {
        int from = Math.Max(0, start);
        int to = Math.Min(available, start + length);
        if (to - from < 2) return 0;

        int crossings = 0;
        for (int i = from + 1; i < to; i++)
            if ((signal[i] >= 0) != (signal[i - 1] >= 0)) crossings++;

        return crossings / (double)(to - from);
    }
}

