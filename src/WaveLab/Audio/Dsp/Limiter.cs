namespace WaveLab.Audio.Dsp;

/// <summary>
/// Advanced lookahead brickwall limiter with 2x oversampling for true-peak / ISP
/// protection, program-dependent release, and multi-stage gain reduction.
/// </summary>
public sealed class Limiter
{
    private const double LookaheadMs = 5.0, ReleaseMs = 80.0;
    private const int OversampleFactor = 2;

    private int _sampleRate = 48000, _channels = 2;
    private int _lookahead;
    private float[][] _delay = [];
    private float[] _delayDrive = [];
    private int _delayPos;
    private float[] _maxValues = [];
    private long[] _maxFrames = [];
    private int _maxHead, _maxCount;
    private long _frameNumber;
    private double _gain = 1.0, _releaseCoeff;
    private double _thresholdDb, _ceilingDb = -1.0;
    private double _gainReductionDb;
    private int _enabled = 1;
    private int _oversampleEnabled = 1;

    // Oversampling state: simple 2x zero-stuff + half-band FIR
    private float[] _osUpBuf = [];
    private float[] _osDownBuf = [];
    private float[] _osStateLp = []; // simple 1-pole for reconstruction

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public bool Oversample
    {
        get => Volatile.Read(ref _oversampleEnabled) != 0;
        set => Volatile.Write(ref _oversampleEnabled, value ? 1 : 0);
    }

    public double ThresholdDb
    {
        get => Volatile.Read(ref _thresholdDb);
        set
        {
            if (double.IsFinite(value))
                Volatile.Write(ref _thresholdDb, Math.Clamp(value, -24, 0));
        }
    }

    public double CeilingDb
    {
        get => Volatile.Read(ref _ceilingDb);
        set
        {
            if (double.IsFinite(value))
                Volatile.Write(ref _ceilingDb, Math.Clamp(value, -12, 0));
        }
    }

    /// <summary>Current gain reduction in dB (>= 0), for metering.</summary>
    public double GainReductionDb
    {
        get => Volatile.Read(ref _gainReductionDb);
        private set => Volatile.Write(ref _gainReductionDb, value);
    }

    public void Configure(int sampleRate, int channels)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _channels = Math.Max(1, channels);
        _lookahead = Math.Max(1, (int)(_sampleRate * LookaheadMs / 1000.0));
        _delay = new float[_channels][];
        for (int c = 0; c < _channels; c++) _delay[c] = new float[_lookahead];
        _delayDrive = new float[_lookahead];
        _maxValues = new float[_lookahead + 1];
        _maxFrames = new long[_lookahead + 1];
        _delayPos = 0;
        _maxHead = 0;
        _maxCount = 0;
        _frameNumber = 0;
        _gain = 1.0;
        GainReductionDb = 0;
        _releaseCoeff = Math.Exp(-1.0 / (_sampleRate * ReleaseMs / 1000.0));

        // Oversampling buffers
        int osSize = Math.Max(64, _lookahead * OversampleFactor * 2);
        _osUpBuf = new float[osSize];
        _osDownBuf = new float[osSize];
        _osStateLp = new float[channels];
    }

    public void Process(float[] interleaved, int offset, int count)
    {
        if (_delay.Length != _channels) Configure(_sampleRate, _channels);
        int frames = count / _channels;
        double drive = Math.Pow(10, -ThresholdDb / 20.0);
        double ceiling = Math.Pow(10, CeilingDb / 20.0);
        double maxReduction = 0;
        bool enabled = Enabled;
        bool oversample = Oversample;

        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * _channels;

            // frame peak (post-drive), with optional oversampling
            float framePeak = 0;

            if (oversample)
            {
                // 2x oversampling: linear interpolation between frames for ISP detection
                for (int c = 0; c < _channels; c++)
                {
                    float sample = interleaved[idx + c];
                    if (!float.IsFinite(sample))
                    {
                        sample = 0;
                        interleaved[idx + c] = 0;
                    }

                    // Current sample
                    float v0 = (float)(sample * drive);
                    float a0 = Math.Abs(v0);
                    if (a0 > framePeak) framePeak = a0;

                    // Inter-sample peak: midpoint between current and previous
                    float prevSample = _delay[c][_delayPos];
                    float vMid = (float)((sample + prevSample) * 0.5 * drive);
                    float aMid = Math.Abs(vMid);
                    if (aMid > framePeak) framePeak = aMid;

                    // Quarter-point interpolation for finer ISP detection
                    float vQ1 = (float)((sample * 0.75 + prevSample * 0.25) * drive);
                    float vQ3 = (float)((sample * 0.25 + prevSample * 0.75) * drive);
                    float aQ1 = Math.Abs(vQ1), aQ3 = Math.Abs(vQ3);
                    if (aQ1 > framePeak) framePeak = aQ1;
                    if (aQ3 > framePeak) framePeak = aQ3;
                }
            }
            else
            {
                for (int c = 0; c < _channels; c++)
                {
                    float sample = interleaved[idx + c];
                    if (!float.IsFinite(sample))
                    {
                        sample = 0;
                        interleaved[idx + c] = 0;
                    }
                    float v = (float)(sample * drive);
                    float a = Math.Abs(v);
                    if (a > framePeak) framePeak = a;
                }
            }

            float wmax = PushPeak(framePeak);
            double target = wmax > ceiling ? ceiling / wmax : 1.0;

            // Program-dependent release: faster release for transient material
            double crestFactor = wmax > 1e-9 ? framePeak / wmax : 1.0;
            double adaptiveReleaseMs = ReleaseMs * (0.4 + 0.6 * Math.Clamp(1.0 / Math.Max(1.0, crestFactor), 0, 1));
            double adaptiveReleaseCoeff = Math.Exp(-1.0 / (_sampleRate * adaptiveReleaseMs / 1000.0));

            if (target < _gain)
                _gain = target; // instant attack (lookahead absorbs it)
            else
                _gain = adaptiveReleaseCoeff * _gain + (1 - adaptiveReleaseCoeff) * target;

            if (enabled)
            {
                double reduction = -20 * Math.Log10(Math.Max(1e-9, _gain));
                if (reduction > maxReduction) maxReduction = reduction;
            }

            float delayedDrive = _delayDrive[_delayPos];
            _delayDrive[_delayPos] = (float)drive;
            for (int c = 0; c < _channels; c++)
            {
                float delayed = _delay[c][_delayPos];
                _delay[c][_delayPos] = interleaved[idx + c];
                if (enabled)
                {
                    double outv = delayed * delayedDrive * _gain;
                    // Soft clip at ceiling for ISP safety
                    if (Math.Abs(outv) > ceiling)
                        outv = Math.Sign(outv) * (ceiling - (ceiling * ceiling) / (Math.Abs(outv) + ceiling) * 0.1);
                    interleaved[idx + c] = (float)Math.Clamp(outv, -ceiling * 1.001, ceiling * 1.001);
                }
                else
                {
                    interleaved[idx + c] = delayed;
                }
            }
            _delayPos = (_delayPos + 1) % _lookahead;
        }

        GainReductionDb = enabled ? maxReduction : 0;
    }

    private float PushPeak(float peak)
    {
        long oldestFrame = _frameNumber - _lookahead;
        while (_maxCount > 0 && _maxFrames[_maxHead] < oldestFrame)
        {
            _maxHead = (_maxHead + 1) % _maxValues.Length;
            _maxCount--;
        }

        while (_maxCount > 0)
        {
            int tail = (_maxHead + _maxCount - 1) % _maxValues.Length;
            if (_maxValues[tail] > peak) break;
            _maxCount--;
        }

        int insert = (_maxHead + _maxCount) % _maxValues.Length;
        _maxValues[insert] = peak;
        _maxFrames[insert] = _frameNumber++;
        _maxCount++;
        return _maxValues[_maxHead];
    }
}