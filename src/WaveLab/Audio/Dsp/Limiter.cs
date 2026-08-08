namespace WaveLab.Audio.Dsp;

/// <summary>
/// Lookahead brickwall limiter. Threshold acts as input drive (lower threshold = more gain
/// into the limiter), Ceiling is the output maximum. 5 ms lookahead, 80 ms release.
/// </summary>
public sealed class Limiter
{
    private const double LookaheadMs = 5.0, ReleaseMs = 80.0;

    private int _sampleRate = 48000, _channels = 2;
    private int _lookahead;
    private float[][] _delay = [];
    private int _delayPos;
    private float[] _windowMax = [];
    private double _gain = 1.0, _releaseCoeff;
    private double _thresholdDb, _ceilingDb = -1.0;

    public bool Enabled { get; set; } = true;
    public double ThresholdDb { get => _thresholdDb; set => _thresholdDb = Math.Clamp(value, -24, 0); }
    public double CeilingDb { get => _ceilingDb; set => _ceilingDb = Math.Clamp(value, -12, 0); }
    /// <summary>Current gain reduction in dB (>= 0), for metering.</summary>
    public double GainReductionDb { get; private set; }

    public void Configure(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _lookahead = Math.Max(1, (int)(sampleRate * LookaheadMs / 1000.0));
        _delay = new float[channels][];
        for (int c = 0; c < channels; c++) _delay[c] = new float[_lookahead];
        _windowMax = new float[_lookahead];
        _delayPos = 0;
        _gain = 1.0;
        _releaseCoeff = Math.Exp(-1.0 / (sampleRate * ReleaseMs / 1000.0));
    }

    public void Process(float[] interleaved, int offset, int count)
    {
        if (_delay.Length != _channels) Configure(_sampleRate, _channels);
        int frames = count / _channels;
        double drive = Math.Pow(10, -_thresholdDb / 20.0);
        double ceiling = Math.Pow(10, _ceilingDb / 20.0);
        double maxReduction = 0;

        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * _channels;

            // frame peak (post-drive)
            float framePeak = 0;
            for (int c = 0; c < _channels; c++)
            {
                float v = (float)(interleaved[idx + c] * drive);
                float a = Math.Abs(v);
                if (a > framePeak) framePeak = a;
            }

            if (Enabled)
            {
                _windowMax[_delayPos] = framePeak;
                // peak over the lookahead window (small window: linear scan is fine at 5 ms)
                float wmax = 0;
                for (int i = 0; i < _lookahead; i++) if (_windowMax[i] > wmax) wmax = _windowMax[i];

                double target = wmax > ceiling ? ceiling / wmax : 1.0;
                if (target < _gain) _gain = target;                       // instant attack (lookahead absorbs it)
                else _gain = _releaseCoeff * _gain + (1 - _releaseCoeff) * target; // smooth release

                double reduction = -20 * Math.Log10(Math.Max(1e-9, _gain));
                if (reduction > maxReduction) maxReduction = reduction;
            }

            for (int c = 0; c < _channels; c++)
            {
                float delayed = _delay[c][_delayPos];
                _delay[c][_delayPos] = (float)(interleaved[idx + c] * drive);
                if (Enabled)
                {
                    double outv = delayed * _gain;
                    interleaved[idx + c] = (float)Math.Clamp(outv, -ceiling, ceiling);
                }
                else
                {
                    interleaved[idx + c] = delayed; // keep latency constant so toggling doesn't click
                }
            }
            _delayPos = (_delayPos + 1) % _lookahead;
        }

        GainReductionDb = Enabled ? maxReduction : 0;
    }
}
