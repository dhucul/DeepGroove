namespace WaveLab.Audio.Dsp;

/// <summary>
/// Advanced lookahead brickwall limiter with 4x oversampled true-peak / ISP
/// detection, program-dependent release, and multi-stage gain reduction.
/// </summary>
public sealed class Limiter
{
    private const double LookaheadMs = 5.0, ReleaseMs = 80.0;

    private const int TruePeakTapsPerPhase = 12;

    // ITU-R BS.1770 Annex 2: order-48, four-phase FIR interpolator, arranged by
    // output phase and then by input-sample delay. Band-limited interpolation is
    // what makes an inter-sample peak visible at all: any convex combination of
    // two neighbouring samples is bounded by them and can never overshoot.
    private static readonly double[][] TruePeakPhaseCoefficients =
    [
        [
            0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000,
            -0.0594482421875, 0.1373291015625, 0.9721679687500, -0.1022949218750,
            0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500,
        ],
        [
            -0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250,
            -0.1665039062500, 0.4650878906250, 0.7797851562500, -0.2003173828125,
            0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375,
        ],
        [
            -0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000,
            -0.2003173828125, 0.7797851562500, 0.4650878906250, -0.1665039062500,
            0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875,
        ],
        [
            -0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750,
            -0.1022949218750, 0.9721679687500, 0.1373291015625, -0.0594482421875,
            0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750,
        ],
    ];

    /// <summary>Linear-range width of the soft knee just above the ceiling.</summary>
    private const double SoftKneeLinear = 0.05;

    private int _sampleRate = 48000, _channels = 2;
    private int _lookahead;
    private float[][] _delay = [];
    private float[] _delayDrive = [];
    private int _delayPos;
    private float[] _maxValues = [];
    private long[] _maxFrames = [];
    private int _maxHead, _maxCount;
    private long _frameNumber;
    private double _gain = 1.0;

    /// <summary>
    /// Release coefficients for the program-dependent release, by peak ratio.
    /// </summary>
    /// <remarks>
    /// The coefficient is exp(-1 / (rate * ms / 1000)) and the only thing that moves is
    /// the peak ratio, which is bounded to [0, 1]. Evaluating it per frame put a
    /// Math.Exp on the audio thread for every sample of every stream; a table over the
    /// ratio, rebuilt whenever the rate changes, is indistinguishable at audio rates.
    /// </remarks>
    private const int ReleaseTableSize = 65;
    private double[] _releaseTable = [];
    private double _thresholdDb, _ceilingDb = -1.0;
    private double _gainReductionDb;
    private int _enabled = 1;
    private int _oversampleEnabled = 1;

    // True-peak detection state: per-channel FIR delay line plus a fill counter so
    // the interpolator is only trusted once it holds real samples.
    private float[][] _ispDelay = [];
    private int[] _ispHistory = [];

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
        _releaseTable = new double[ReleaseTableSize];
        for (int i = 0; i < ReleaseTableSize; i++)
        {
            double ratio = (double)i / (ReleaseTableSize - 1);
            double ms = ReleaseMs * (0.4 + 0.6 * ratio);
            _releaseTable[i] = Math.Exp(-1.0 / (_sampleRate * ms / 1000.0));
        }

        // True-peak interpolator state
        _ispDelay = new float[_channels][];
        for (int c = 0; c < _channels; c++) _ispDelay[c] = new float[TruePeakTapsPerPhase];
        _ispHistory = new int[_channels];
    }

    /// <summary>
    /// Clear every delay line and reseed the scalars without reallocating, for a
    /// transport stop/start or an offline render boundary. Use
    /// <see cref="Configure(int, int)"/> only for a real rate or channel change.
    /// </summary>
    public void Reset()
    {
        for (int c = 0; c < _delay.Length; c++) Array.Clear(_delay[c]);
        for (int c = 0; c < _ispDelay.Length; c++) Array.Clear(_ispDelay[c]);
        Array.Clear(_ispHistory);
        Array.Clear(_delayDrive);
        Array.Clear(_maxValues);
        Array.Clear(_maxFrames);
        _delayPos = 0;
        _maxHead = 0;
        _maxCount = 0;
        _frameNumber = 0;
        _gain = 1.0;
        GainReductionDb = 0;
    }

    public void Process(float[] interleaved, int offset, int count)
    {
        if (_delay.Length != _channels || _releaseTable.Length != ReleaseTableSize)
            Configure(_sampleRate, _channels);
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
                // 4x oversampled true peak: BS.1770 polyphase interpolation. The
                // interpolator's ~6-sample group delay is far shorter than the
                // lookahead, so a peak it reports still arrives before the audio
                // it belongs to reaches the output.
                for (int c = 0; c < _channels; c++)
                {
                    float sample = interleaved[idx + c];
                    if (!float.IsFinite(sample))
                    {
                        sample = 0;
                        interleaved[idx + c] = 0;
                        Array.Clear(_ispDelay[c]);
                        _ispHistory[c] = 0;
                    }

                    // Current sample
                    float v0 = (float)(sample * drive);
                    float a0 = Math.Abs(v0);
                    if (a0 > framePeak) framePeak = a0;

                    float[] history = _ispDelay[c];
                    for (int tap = TruePeakTapsPerPhase - 1; tap > 0; tap--)
                        history[tap] = history[tap - 1];
                    history[0] = sample;
                    if (_ispHistory[c] < TruePeakTapsPerPhase) _ispHistory[c]++;
                    if (_ispHistory[c] < TruePeakTapsPerPhase) continue;

                    foreach (double[] phase in TruePeakPhaseCoefficients)
                    {
                        double interpolated = 0;
                        for (int tap = 0; tap < TruePeakTapsPerPhase; tap++)
                            interpolated += phase[tap] * history[tap];
                        interpolated = Math.Abs(interpolated * drive);
                        if (double.IsFinite(interpolated) && interpolated > framePeak)
                            framePeak = (float)interpolated;
                    }
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

            // Program-dependent release: gain stays pinned while a peak dominates the
            // lookahead window, then releases faster over transient material.
            double peakRatio = wmax > 1e-9 ? Math.Clamp(framePeak / wmax, 0.0, 1.0) : 1.0;
            double tableIndex = peakRatio * (ReleaseTableSize - 1);
            int lower = (int)tableIndex;
            int upper = Math.Min(lower + 1, ReleaseTableSize - 1);
            double blend = tableIndex - lower;
            double adaptiveReleaseCoeff =
                _releaseTable[lower] + (_releaseTable[upper] - _releaseTable[lower]) * blend;

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
                    // Soft knee just above the ceiling for ISP safety: continuous at the
                    // junction (slope 1) and bounded; the clamp below remains the wall.
                    double overshoot = Math.Abs(outv) - ceiling;
                    if (overshoot > 0)
                        outv = Math.Sign(outv) * (ceiling + overshoot / (1.0 + overshoot / SoftKneeLinear));
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
