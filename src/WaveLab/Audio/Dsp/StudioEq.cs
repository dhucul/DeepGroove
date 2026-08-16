namespace WaveLab.Audio.Dsp;

/// <summary>Three-band master EQ: low shelf 80 Hz, peaking 650 Hz, high shelf 8 kHz.</summary>
public sealed class StudioEq
{
    public const double LowFreq = 80, MidFreq = 650, HighFreq = 8000, MidQ = 0.9;

    private readonly object _lock = new();
    private Biquad[][] _filters = []; // [band][channel]
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
        lock (_lock)
        {
            // Only the coefficients change on a gain move: keep the existing bank so
            // the delay lines survive and a slider tick cannot click.
            if (_filters.Length != 3 || _filters[0].Length != _channels)
            {
                _filters = new Biquad[3][];
                for (int b = 0; b < 3; b++) _filters[b] = new Biquad[_channels];
            }
            for (int b = 0; b < 3; b++)
            {
                Biquad prototype = b switch
                {
                    0 => Biquad.LowShelf(_sampleRate, LowFreq, _lowDb),
                    1 => Biquad.Peaking(_sampleRate, MidFreq, MidQ, _midDb),
                    _ => Biquad.HighShelf(_sampleRate, HighFreq, _highDb),
                };
                for (int c = 0; c < _channels; c++) _filters[b][c].CopyCoefficientsFrom(prototype);
            }
        }
    }

    public void Process(float[] interleaved, int offset, int count)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            // Guard on the bank that actually exists: Configure(0, n) leaves it empty.
            if (_filters.Length != 3) return;
            int channels = _filters[0].Length;
            if (channels <= 0 || _filters[1].Length != channels || _filters[2].Length != channels) return;
            for (int i = offset; i < offset + count; i++)
            {
                int c = (i - offset) % channels;
                float v = interleaved[i];
                for (int b = 0; b < 3; b++) v = _filters[b][c].Process(v);
                interleaved[i] = v;
            }
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
