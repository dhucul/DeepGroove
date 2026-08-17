namespace WaveLab.Audio.Dsp;

/// <summary>How steeply a crossover separates its bands.</summary>
public enum CrossoverSlope
{
    /// <summary>Linkwitz-Riley 4th order, 24 dB per octave. Two cascaded Butterworth pairs.</summary>
    Lr24,

    /// <summary>Linkwitz-Riley 8th order, 48 dB per octave, for tight band separation.</summary>
    Lr48,
}

/// <summary>
/// A Linkwitz-Riley band splitter whose bands sum back to the signal they came from.
/// </summary>
/// <remarks>
/// <para>
/// Splitting a signal into bands is easy; splitting it so the bands <em>add back up</em> is not, and
/// it is the only thing that makes a multiband processor usable. A Butterworth pair crossed over at
/// the same frequency sums to +3 dB at the crossing; a Linkwitz-Riley pair — two Butterworths
/// cascaded, so each is 6 dB down where they meet rather than 3 — sums to exactly unity magnitude.
/// The sum is not the identity: it is an <b>all-pass</b>, flat in level with the phase rotating
/// through the crossing. That is the accepted price and is why the bands must be recombined by
/// addition and never compared sample-for-sample against the input.
/// </para>
/// <para>
/// With three or more bands the naive tree stops summing flat, and the reason is easy to miss: the
/// low band leaves after the first split and never meets the second, so it arrives without the phase
/// rotation the other two picked up on their way through. It is corrected by passing it through an
/// <b>all-pass matching every crossover it skipped</b> — a filter that changes nothing a listener can
/// hear in that band alone, and without which the whole thing has a hole at the upper crossing.
/// </para>
/// </remarks>
public sealed class Crossover
{
    /// <summary>Butterworth Q for a second-order section: the only Q that gives a maximally flat pair.</summary>
    private const double ButterworthQ = 0.70710678118654752;

    /// <summary>Q pairs for a fourth-order Butterworth, which cascaded twice gives 48 dB per octave.</summary>
    private static readonly double[] FourthOrderQ = [0.54119610014619698, 1.30656296487637652];

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double[] _frequencies;
    private readonly CrossoverSlope _slope;

    // [crossover][channel][section] — one filter chain per crossover, per channel.
    private readonly Biquad[][][] _lowPass;
    private readonly Biquad[][][] _highPass;

    // [band][crossover][channel][section] — the all-pass correction each band needs.
    private readonly Biquad[][][][] _allPass;

    public int Bands => _frequencies.Length + 1;
    public IReadOnlyList<double> Frequencies => _frequencies;
    public CrossoverSlope Slope => _slope;

    /// <param name="frequencies">Crossover points in Hz. Sorted and de-duplicated on the way in.</param>
    public Crossover(int sampleRate, IReadOnlyList<double> frequencies, int channels,
        CrossoverSlope slope = CrossoverSlope.Lr24)
    {
        ArgumentNullException.ThrowIfNull(frequencies);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _sampleRate = sampleRate;
        _channels = channels;
        _slope = slope;

        double nyquist = sampleRate / 2.0;
        var sorted = new List<double>();
        foreach (double frequency in frequencies)
        {
            double clamped = Math.Clamp(frequency, 10, nyquist * 0.95);
            if (!sorted.Exists(existing => Math.Abs(existing - clamped) < 1e-6)) sorted.Add(clamped);
        }
        sorted.Sort();
        _frequencies = [.. sorted];

        int crossovers = _frequencies.Length;
        _lowPass = new Biquad[crossovers][][];
        _highPass = new Biquad[crossovers][][];
        for (int c = 0; c < crossovers; c++)
        {
            _lowPass[c] = Build(channels, _frequencies[c], lowPass: true);
            _highPass[c] = Build(channels, _frequencies[c], lowPass: false);
        }

        // Band i skips every crossover above the one that made it, so it needs their all-passes.
        _allPass = new Biquad[Bands][][][];
        for (int band = 0; band < Bands; band++)
        {
            _allPass[band] = new Biquad[crossovers][][];
            for (int c = 0; c < crossovers; c++)
            {
                _allPass[band][c] = c >= band + 1 && band < Bands - 1
                    ? BuildAllPass(channels, _frequencies[c])
                    : [];
            }
        }
    }

    private Biquad[][] Build(int channels, double frequency, bool lowPass)
    {
        double[] qs = _slope == CrossoverSlope.Lr24 ? [ButterworthQ] : FourthOrderQ;

        // Cascaded twice: that is what makes it Linkwitz-Riley rather than Butterworth, and what
        // puts the two halves 6 dB down where they cross so their sum is unity.
        var prototypes = new List<Biquad>();
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (double q in qs)
            {
                prototypes.Add(lowPass
                    ? Biquad.LowPass(_sampleRate, frequency, q)
                    : Biquad.HighPass(_sampleRate, frequency, q));
            }
        }

        var chains = new Biquad[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            chains[channel] = new Biquad[prototypes.Count];
            for (int i = 0; i < prototypes.Count; i++) chains[channel][i] = prototypes[i];
        }
        return chains;
    }

    /// <summary>
    /// The all-pass a Linkwitz-Riley crossover's own sum amounts to, which is what a band that
    /// skipped it has to be put through to stay aligned with the ones that did not.
    /// </summary>
    private Biquad[][] BuildAllPass(int channels, double frequency)
    {
        double[] qs = _slope == CrossoverSlope.Lr24 ? [ButterworthQ] : FourthOrderQ;
        var prototypes = new List<Biquad>();
        foreach (double q in qs) prototypes.Add(Biquad.AllPass(_sampleRate, frequency, q));

        var chains = new Biquad[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            chains[channel] = new Biquad[prototypes.Count];
            for (int i = 0; i < prototypes.Count; i++) chains[channel][i] = prototypes[i];
        }
        return chains;
    }

    /// <summary>Splits one sample of one channel into every band.</summary>
    public void ProcessSample(int channel, float input, Span<float> bandOutputs)
    {
        if (bandOutputs.Length < Bands) throw new ArgumentException("One output per band is required.",
            nameof(bandOutputs));
        if ((uint)channel >= (uint)_channels) throw new ArgumentOutOfRangeException(nameof(channel));

        float remaining = input;
        for (int c = 0; c < _frequencies.Length; c++)
        {
            float low = remaining;
            for (int s = 0; s < _lowPass[c][channel].Length; s++) low = _lowPass[c][channel][s].Process(low);

            float high = remaining;
            for (int s = 0; s < _highPass[c][channel].Length; s++) high = _highPass[c][channel][s].Process(high);

            bandOutputs[c] = low;
            remaining = high;
        }
        bandOutputs[^1] = remaining;

        // Every band that left early is put through the crossovers it missed.
        for (int band = 0; band < Bands - 1; band++)
        {
            float value = bandOutputs[band];
            for (int c = 0; c < _frequencies.Length; c++)
            {
                Biquad[][] chain = _allPass[band][c];
                if (chain.Length == 0) continue;
                for (int s = 0; s < chain[channel].Length; s++) value = chain[channel][s].Process(value);
            }
            bandOutputs[band] = value;
        }
    }

    public void Reset()
    {
        foreach (Biquad[][] crossover in _lowPass)
            foreach (Biquad[] chain in crossover)
                for (int i = 0; i < chain.Length; i++) chain[i].Reset();

        foreach (Biquad[][] crossover in _highPass)
            foreach (Biquad[] chain in crossover)
                for (int i = 0; i < chain.Length; i++) chain[i].Reset();

        foreach (Biquad[][][] band in _allPass)
            foreach (Biquad[][] crossover in band)
                foreach (Biquad[] chain in crossover)
                    for (int i = 0; i < chain.Length; i++) chain[i].Reset();
    }
}
