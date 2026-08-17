namespace WaveLab.Audio.Dsp;

/// <summary>Three-band master EQ: low shelf 80 Hz, peaking 650 Hz, high shelf 8 kHz.</summary>
public sealed class StudioEq
{
    public const double LowFreq = 80, MidFreq = 650, HighFreq = 8000, MidQ = 0.9;

    private Biquad[][] _filters = []; // [band][channel] — audio-thread state only
    private BiquadCoefficients _bands = BiquadCoefficients.Identity;
    private int _sampleRate, _channels;
    private double _lowDb, _midDb, _highDb;

    public bool Enabled { get; set; } = true;

    public double LowGainDb { get => _lowDb; set { _lowDb = value; Rebuild(); } }
    public double MidGainDb { get => _midDb; set { _midDb = value; Rebuild(); } }
    public double HighGainDb { get => _highDb; set { _highDb = value; Rebuild(); } }

    public void Configure(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_sampleRate <= 0 || _channels <= 0) return;

        // Only the coefficients change on a gain move, so the bank is left alone and the delay
        // lines survive — a slider tick cannot click. The bank itself is replaced only when the
        // stream's shape changes, which is the one case where there is no state worth carrying.
        var bank = Volatile.Read(ref _filters);
        if (bank.Length != 3 || bank[0].Length != _channels)
        {
            bank = new Biquad[3][];
            for (int b = 0; b < 3; b++) bank[b] = new Biquad[_channels];
            Volatile.Write(ref _filters, bank);
        }

        Volatile.Write(ref _bands, new BiquadCoefficients(
            Biquad.LowShelf(_sampleRate, LowFreq, _lowDb),
            Biquad.Peaking(_sampleRate, MidFreq, MidQ, _midDb),
            Biquad.HighShelf(_sampleRate, HighFreq, _highDb)));
    }

    public void Process(float[] interleaved, int offset, int count)
    {
        if (!Enabled) return;

        // No lock on the audio path. A monitor is not priority-inheriting, so a gain setter
        // preempted between taking it and releasing it would stall the render callback for a
        // whole scheduler quantum; the setters publish a coefficient snapshot instead.
        var filters = Volatile.Read(ref _filters);
        // Guard on the bank that actually exists: Configure(0, n) leaves it empty.
        if (filters.Length != 3) return;
        int channels = filters[0].Length;
        if (channels <= 0 || filters[1].Length != channels || filters[2].Length != channels) return;

        var bands = Volatile.Read(ref _bands);
        if (bands.Count != 3) return;
        for (int b = 0; b < 3; b++) bands.ApplyTo(filters[b], b);

        for (int i = offset; i < offset + count; i++)
        {
            int c = (i - offset) % channels;
            float v = interleaved[i];
            for (int b = 0; b < 3; b++) v = filters[b][c].Process(v);
            interleaved[i] = v;
        }
    }

    /// <summary>Combined response in dB at frequency f, for the curve display.</summary>
    public double ResponseDb(double f)
    {
        int fs = _sampleRate > 0 ? _sampleRate : 48000;
        double db = 0;
        db += Biquad.LowShelf(fs, LowFreq, _lowDb).MagnitudeDb(f, fs);
        db += Biquad.Peaking(fs, MidFreq, MidQ, _midDb).MagnitudeDb(f, fs);
        db += Biquad.HighShelf(fs, HighFreq, _highDb).MagnitudeDb(f, fs);
        return db;
    }
}
