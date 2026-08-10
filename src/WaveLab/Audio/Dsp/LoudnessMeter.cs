namespace WaveLab.Audio.Dsp;

/// <summary>
/// EBU R128 / ITU-R BS.1770 loudness meter: momentary (400 ms), short-term (3 s),
/// gated integrated loudness, loudness range approximation, and 4x oversampled true peak.
/// </summary>
public sealed class LoudnessMeter
{
    private const int TruePeakTapsPerPhase = 12;

    // ITU-R BS.1770 Annex 2: order-48, four-phase FIR interpolator. The
    // coefficients are arranged by output phase and then by input-sample delay.
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

    private int _sampleRate = 48000, _channels = 2;
    private Biquad[] _stage1 = [], _stage2 = [];
    private float[][] _truePeakDelay = [];
    private int[] _truePeakHistory = [];

    private int _subBlockSize;           // 100 ms
    private double[] _subBlockSumSq = [];
    private int _subBlockFill;
    private readonly Queue<double> _last400 = new();   // 4 sub-blocks
    private readonly Queue<double> _last3s = new();    // 30 sub-blocks
    private readonly List<double> _blockLoudness = []; // 400ms block loudness values (75% overlap)
    private readonly object _lock = new();
    private double _momentaryLufs = double.NegativeInfinity;
    private double _shortTermLufs = double.NegativeInfinity;
    private double _truePeakDb = double.NegativeInfinity;
    private long _framesProcessed;

    public double MomentaryLufs
    {
        get => Volatile.Read(ref _momentaryLufs);
        private set => Volatile.Write(ref _momentaryLufs, value);
    }
    public double ShortTermLufs
    {
        get => Volatile.Read(ref _shortTermLufs);
        private set => Volatile.Write(ref _shortTermLufs, value);
    }
    public double TruePeakDb
    {
        get => Volatile.Read(ref _truePeakDb);
        private set => Volatile.Write(ref _truePeakDb, value);
    }

    public void Configure(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _stage1 = new Biquad[channels];
        _stage2 = new Biquad[channels];
        for (int c = 0; c < channels; c++)
        {
            // K-weighting: high shelf +4 dB @ ~1.68 kHz, then high-pass @ ~38 Hz
            _stage1[c] = Biquad.HighShelf(sampleRate, 1681.97, 3.99982, 1.0);
            _stage2[c] = Biquad.HighPass(sampleRate, 38.13, 0.5);
        }
        _truePeakDelay = Enumerable.Range(0, channels)
            .Select(_ => new float[TruePeakTapsPerPhase])
            .ToArray();
        _truePeakHistory = new int[channels];
        _subBlockSize = Math.Max(1, sampleRate / 10);
        Reset();
    }

    public void Reset()
    {
        lock (_lock)
        {
            for (int channel = 0; channel < _stage1.Length; channel++) _stage1[channel].Reset();
            for (int channel = 0; channel < _stage2.Length; channel++) _stage2[channel].Reset();
            foreach (float[] delay in _truePeakDelay) Array.Clear(delay);
            Array.Clear(_truePeakHistory);
            _subBlockSumSq = new double[_channels];
            _subBlockFill = 0;
            _last400.Clear();
            _last3s.Clear();
            _blockLoudness.Clear();
            _framesProcessed = 0;
            MomentaryLufs = ShortTermLufs = TruePeakDb = double.NegativeInfinity;
        }
    }

    public void Process(float[] interleaved, int offset, int count)
    {
        int frames = count / _channels;
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < _channels; c++)
                {
                    float raw = interleaved[offset + f * _channels + c];
                    bool valid = float.IsFinite(raw);
                    if (!valid)
                    {
                        raw = 0;
                        _truePeakHistory[c] = 0;
                        Array.Clear(_truePeakDelay[c]);
                    }
                    else
                    {
                        double sampleDb = 20 * Math.Log10(Math.Max(1e-9, Math.Abs(raw)));
                        if (sampleDb > TruePeakDb) TruePeakDb = sampleDb;

                        // Capture can begin at an arbitrary waveform phase. Wait
                        // for a complete real-sample history rather than filtering
                        // against reset-time zeros, and break continuity at invalid
                        // driver samples.
                        PushTruePeakSample(_truePeakDelay[c], raw);
                        if (_truePeakHistory[c] < TruePeakTapsPerPhase)
                            _truePeakHistory[c]++;
                        if (_truePeakHistory[c] == TruePeakTapsPerPhase)
                            MeasureTruePeak(_truePeakDelay[c]);
                    }

                    // K-weighted mean square
                    float w = _stage2[c].Process(_stage1[c].Process(raw));
                    _subBlockSumSq[c] += w * w;
                }

                if (++_subBlockFill >= _subBlockSize)
                {
                    double ms = 0;
                    for (int c = 0; c < _channels; c++) ms += _subBlockSumSq[c] / _subBlockSize;
                    _last400.Enqueue(ms);
                    while (_last400.Count > 4) _last400.Dequeue();
                    _last3s.Enqueue(ms);
                    while (_last3s.Count > 30) _last3s.Dequeue();

                    MomentaryLufs = Lufs(Avg(_last400));
                    ShortTermLufs = Lufs(Avg(_last3s));
                    if (_last400.Count == 4) _blockLoudness.Add(Lufs(Avg(_last400)));

                    _subBlockSumSq = new double[_channels];
                    _subBlockFill = 0;
                }

                _framesProcessed++;
            }
        }
    }

    /// <summary>
    /// Finalize the last true-peak interpolation segment after end of stream.
    /// Calling this repeatedly is safe; call it only when no more samples will be
    /// appended to the current measurement.
    /// </summary>
    public void FlushTruePeak()
    {
        lock (_lock)
        {
            if (_framesProcessed == 0) return;
            for (int c = 0; c < _channels; c++)
            {
                if (_truePeakHistory[c] < TruePeakTapsPerPhase) continue;
                var tail = (float[])_truePeakDelay[c].Clone();
                for (int sample = 0; sample < TruePeakTapsPerPhase - 1; sample++)
                {
                    PushTruePeakSample(tail, 0);
                    MeasureTruePeak(tail);
                }
            }
        }
    }

    private static void PushTruePeakSample(float[] delay, float sample)
    {
        for (int tap = delay.Length - 1; tap > 0; tap--)
            delay[tap] = delay[tap - 1];
        delay[0] = sample;
    }

    private void MeasureTruePeak(float[] delay)
    {
        foreach (double[] phase in TruePeakPhaseCoefficients)
        {
            double interpolated = 0;
            for (int tap = 0; tap < TruePeakTapsPerPhase; tap++)
                interpolated += phase[tap] * delay[tap];
            double db = 20 * Math.Log10(Math.Max(1e-9, Math.Abs(interpolated)));
            if (db > TruePeakDb) TruePeakDb = db;
        }
    }

    /// <summary>Gated integrated loudness per BS.1770-4.</summary>
    public double IntegratedLufs
    {
        get
        {
            lock (_lock)
            {
                var abs = _blockLoudness.Where(l => l > -70).ToList();
                if (abs.Count == 0) return double.NegativeInfinity;
                double meanPower = abs.Average(l => Math.Pow(10, (l + 0.691) / 10));
                double relGate = Lufs(meanPower) - 10;
                var gated = abs.Where(l => l > relGate).ToList();
                if (gated.Count == 0) return double.NegativeInfinity;
                return Lufs(gated.Average(l => Math.Pow(10, (l + 0.691) / 10)));
            }
        }
    }

    /// <summary>Simple LRA approximation: 10th..95th percentile of short-term-ish blocks.</summary>
    public double LoudnessRangeLu
    {
        get
        {
            lock (_lock)
            {
                var abs = _blockLoudness.Where(l => l > -70).OrderBy(l => l).ToList();
                if (abs.Count < 4) return 0;
                double lo = abs[(int)(abs.Count * 0.10)];
                double hi = abs[Math.Min(abs.Count - 1, (int)(abs.Count * 0.95))];
                return Math.Max(0, hi - lo);
            }
        }
    }

    private static double Avg(Queue<double> q) => q.Count == 0 ? 0 : q.Average();
    private static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(Math.Max(1e-12, meanSquare));
}
