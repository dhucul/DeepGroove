namespace WaveLab.Audio.Dsp;

/// <summary>
/// EBU R128 / ITU-R BS.1770 loudness meter: momentary (400 ms), short-term (3 s),
/// gated integrated loudness, loudness range approximation, and 4x oversampled true peak.
/// </summary>
public sealed class LoudnessMeter
{
    internal const int TruePeakTapsPerPhase = 12;

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

    // 400 ms block loudness history (75% overlap). A session cannot be allowed to
    // grow this without bound, so it lives in a ring capped at one hour; older
    // blocks fall out rather than the meter consuming memory forever.
    private const int MaximumBlockLoudnessEntries = 36_000; // one hour at 100 ms

    // Loudness range is not computed from the 400 ms blocks above. EBU Tech 3342 builds it from
    // *short-term* (3 s) loudness sampled at no less than 66.7% overlap, which is a 1 s hop, and then
    // gates that distribution twice. Measuring it from momentary blocks — as this did — reports a
    // range several LU too wide on almost any real programme, because 400 ms windows track the
    // envelope closely enough to include level swings that 3 s windows average away.
    private const int MaximumShortTermEntries = 3_600;      // one hour at 1 s
    private const int SubBlocksPerShortTermSample = 10;     // 100 ms sub-blocks per 1 s hop
    private const int ShortTermSubBlocks = 30;              // 3 s window
    private double[] _shortTermLoudness = [];
    private int _shortTermStart;
    private int _shortTermCount;
    private int _subBlocksSinceShortTerm;

    private int _subBlockSize;           // 100 ms
    private double[] _subBlockSumSq = [];
    private int _subBlockFill;
    private readonly Queue<double> _last400 = new();   // 4 sub-blocks
    private readonly Queue<double> _last3s = new();    // 30 sub-blocks
    private double[] _blockLoudness = [];
    private int _blockLoudnessStart;
    private int _blockLoudnessCount;
    private long _blockLoudnessVersion;                // total blocks added; cache key
    private readonly object _lock = new();
    // Percentile/gating reads are serialized among themselves so they can share one
    // scratch buffer and do their sorting and Math.Pow work outside _lock — the
    // render thread takes _lock for every buffer and must not wait behind them.
    private readonly object _readLock = new();
    private double[] _readScratch = [];
    private long _integratedCacheVersion = -1;
    private double _integratedCacheValue = double.NegativeInfinity;
    private long _rangeCacheVersion = -1;
    private double _rangeCacheValue;
    private double _momentaryLufs = double.NegativeInfinity;
    private double _shortTermLufs = double.NegativeInfinity;
    // Tracked linearly and converted once on read. Taking 20*log10 per sample per
     // channel, and again for each of the four interpolation phases, put roughly
     // three-quarters of a million logarithms a second on the render thread.
    private double _truePeakLinear;
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
        get
        {
            double linear = Volatile.Read(ref _truePeakLinear);
            return linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;
        }
    }

    private void ObserveTruePeak(double magnitude)
    {
        if (magnitude > Volatile.Read(ref _truePeakLinear)) Volatile.Write(ref _truePeakLinear, magnitude);
    }

    public void Configure(int sampleRate, int channels)
    {
        // Built first, published under the lock Process holds. Assigning _channels before
        // the arrays it indexes let a render thread already inside Process see the new
        // channel count against the old, shorter arrays — an IndexOutOfRangeException on
        // the audio thread. Reachable, because PlaybackEngine tears the previous output
        // down asynchronously, so an old render thread can still be in MasterSection.Read
        // while Play reconfigures.
        var stage1 = new Biquad[channels];
        var stage2 = new Biquad[channels];
        for (int c = 0; c < channels; c++)
        {
            // K-weighting: high shelf +4 dB @ ~1.68 kHz, then high-pass @ ~38 Hz
            stage1[c] = Biquad.HighShelf(sampleRate, 1681.97, 3.99982, 1.0);
            stage2[c] = Biquad.HighPass(sampleRate, 38.13, 0.5);
        }
        float[][] delay = Enumerable.Range(0, channels)
            .Select(_ => new float[TruePeakTapsPerPhase])
            .ToArray();

        lock (_lock)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _stage1 = stage1;
            _stage2 = stage2;
            _truePeakDelay = delay;
            _truePeakHistory = new int[channels];
            _subBlockSize = Math.Max(1, sampleRate / 10);
            ResetCore();
        }
    }

    public void Reset()
    {
        lock (_lock) ResetCore();
    }

    /// <summary>Call while holding <see cref="_lock"/>.</summary>
    private void ResetCore()
    {
        for (int channel = 0; channel < _stage1.Length; channel++) _stage1[channel].Reset();
        for (int channel = 0; channel < _stage2.Length; channel++) _stage2[channel].Reset();
        foreach (float[] delay in _truePeakDelay) Array.Clear(delay);
        Array.Clear(_truePeakHistory);
        _subBlockSumSq = new double[_channels];
        _subBlockFill = 0;
        _last400.Clear();
        _last3s.Clear();
        _blockLoudnessStart = 0;
        _blockLoudnessCount = 0;
        _shortTermStart = 0;
        _shortTermCount = 0;
        _subBlocksSinceShortTerm = 0;
        // Keep the version monotonic across resets: a reader that sampled the
        // pre-reset history must never store its result under a key this meter
        // can hand out again.
        _blockLoudnessVersion++;
        _integratedCacheVersion = -1;
        _integratedCacheValue = double.NegativeInfinity;
        _rangeCacheVersion = -1;
        _rangeCacheValue = 0;
        _framesProcessed = 0;
        MomentaryLufs = ShortTermLufs = double.NegativeInfinity;
        Volatile.Write(ref _truePeakLinear, 0);
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
                        ObserveTruePeak(Math.Abs(raw));

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
                    if (_last400.Count == 4) AddBlockLoudness(Lufs(Avg(_last400)));

                    // Short-term samples for loudness range: one per second, and only once a full
                    // 3 s window has accumulated, so a partly-filled window cannot report as a quiet
                    // passage and stretch the range.
                    if (++_subBlocksSinceShortTerm >= SubBlocksPerShortTermSample)
                    {
                        _subBlocksSinceShortTerm = 0;
                        if (_last3s.Count == ShortTermSubBlocks) AddShortTermLoudness(ShortTermLufs);
                    }

                    // Cleared, not reallocated: this ran ten times a second on the
                    // render thread.
                    Array.Clear(_subBlockSumSq);
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
        => ObserveTruePeak(InterpolatedTruePeak(delay));

    /// <summary>
    /// Highest four-phase BS.1770 interpolated magnitude for one full history window. Shared by
    /// processors that must enforce a true-peak ceiling rather than a sample-peak approximation.
    /// </summary>
    internal static double InterpolatedTruePeak(ReadOnlySpan<float> delay)
    {
        double peak = 0;
        foreach (double[] phase in TruePeakPhaseCoefficients)
        {
            double interpolated = 0;
            for (int tap = 0; tap < TruePeakTapsPerPhase; tap++)
                interpolated += phase[tap] * delay[tap];
            peak = Math.Max(peak, Math.Abs(interpolated));
        }
        return peak;
    }

    /// <summary>Appends one block loudness value, dropping the oldest when the ring is full.</summary>
    private void AddBlockLoudness(double loudness)
    {
        if (_blockLoudnessCount == _blockLoudness.Length)
        {
            if (_blockLoudness.Length < MaximumBlockLoudnessEntries)
            {
                int capacity = Math.Min(MaximumBlockLoudnessEntries,
                    Math.Max(1024, _blockLoudness.Length * 2));
                var grown = new double[capacity];
                for (int i = 0; i < _blockLoudnessCount; i++)
                    grown[i] = _blockLoudness[(_blockLoudnessStart + i) % _blockLoudness.Length];
                _blockLoudness = grown;
                _blockLoudnessStart = 0;
            }
            else
            {
                _blockLoudnessStart = (_blockLoudnessStart + 1) % _blockLoudness.Length;
                _blockLoudnessCount--;
            }
        }

        _blockLoudness[(_blockLoudnessStart + _blockLoudnessCount) % _blockLoudness.Length] = loudness;
        _blockLoudnessCount++;
        _blockLoudnessVersion++;
    }

    /// <summary>Appends one short-term loudness sample, dropping the oldest when the ring is full.</summary>
    private void AddShortTermLoudness(double loudness)
    {
        if (_shortTermCount == _shortTermLoudness.Length)
        {
            if (_shortTermLoudness.Length < MaximumShortTermEntries)
            {
                int capacity = Math.Min(MaximumShortTermEntries,
                    Math.Max(256, _shortTermLoudness.Length * 2));
                var grown = new double[capacity];
                for (int i = 0; i < _shortTermCount; i++)
                    grown[i] = _shortTermLoudness[(_shortTermStart + i) % _shortTermLoudness.Length];
                _shortTermLoudness = grown;
                _shortTermStart = 0;
            }
            else
            {
                _shortTermLoudness[_shortTermStart] = loudness;
                _shortTermStart = (_shortTermStart + 1) % _shortTermLoudness.Length;
                _blockLoudnessVersion++;
                return;
            }
        }

        _shortTermLoudness[(_shortTermStart + _shortTermCount) % _shortTermLoudness.Length] = loudness;
        _shortTermCount++;
        _blockLoudnessVersion++;
    }

    /// <summary>
    /// Copies the short-term samples above the absolute gate into the shared read scratch. The caller
    /// must hold both the read lock and the state lock.
    /// </summary>
    private int CopyAbsoluteGatedShortTermLocked(out double[] values)
    {
        if (_readScratch.Length < _shortTermCount)
            _readScratch = new double[Math.Max(1024, _shortTermCount)];
        values = _readScratch;
        int count = 0;
        for (int i = 0; i < _shortTermCount; i++)
        {
            double loudness = _shortTermLoudness[(_shortTermStart + i) % _shortTermLoudness.Length];
            if (loudness > AbsoluteGateLufs) values[count++] = loudness;
        }
        return count;
    }

    private const double AbsoluteGateLufs = -70;

    /// <summary>
    /// Copies the blocks above the absolute gate into the shared read scratch, in
    /// arrival order. The caller must hold both the read lock and the state lock.
    /// </summary>
    private int CopyAbsoluteGatedBlocksLocked(out double[] values)
    {
        if (_readScratch.Length < _blockLoudnessCount)
            _readScratch = new double[Math.Max(1024, _blockLoudnessCount)];
        values = _readScratch;
        int count = 0;
        for (int i = 0; i < _blockLoudnessCount; i++)
        {
            double loudness = _blockLoudness[(_blockLoudnessStart + i) % _blockLoudness.Length];
            if (loudness > AbsoluteGateLufs) values[count++] = loudness;
        }
        return count;
    }

    /// <summary>Gated integrated loudness per BS.1770-4.</summary>
    public double IntegratedLufs
    {
        get
        {
            lock (_readLock)
            {
                long version;
                int count;
                double[] values;
                lock (_lock)
                {
                    if (_integratedCacheVersion == _blockLoudnessVersion) return _integratedCacheValue;
                    version = _blockLoudnessVersion;
                    count = CopyAbsoluteGatedBlocksLocked(out values);
                }

                double result = double.NegativeInfinity;
                if (count > 0)
                {
                    double meanPower = 0;
                    for (int i = 0; i < count; i++) meanPower += Math.Pow(10, (values[i] + 0.691) / 10);
                    meanPower /= count;
                    double relGate = Lufs(meanPower) - 10;

                    double gatedPower = 0;
                    int gatedCount = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (values[i] <= relGate) continue;
                        gatedPower += Math.Pow(10, (values[i] + 0.691) / 10);
                        gatedCount++;
                    }
                    if (gatedCount > 0) result = Lufs(gatedPower / gatedCount);
                }

                lock (_lock)
                {
                    _integratedCacheVersion = version;
                    _integratedCacheValue = result;
                }
                return result;
            }
        }
    }

    /// <summary>
    /// Loudness range per EBU Tech 3342: the spread between the 10th and 95th percentiles of the
    /// short-term (3 s) loudness distribution, after an absolute gate at -70 LUFS and a relative gate
    /// 20 LU below the mean of what survives it.
    /// </summary>
    /// <remarks>
    /// The relative gate is the part that matters most in practice. Without it, a fade-out, a lead-in
    /// or a quiet run-out counts as programme and inflates the range by as much as it is quiet —
    /// which for a vinyl side means the number described the silence between tracks rather than the
    /// music.
    /// </remarks>
    public double LoudnessRangeLu
    {
        get
        {
            lock (_readLock)
            {
                long version;
                int count;
                double[] values;
                lock (_lock)
                {
                    if (_rangeCacheVersion == _blockLoudnessVersion) return _rangeCacheValue;
                    version = _blockLoudnessVersion;
                    count = CopyAbsoluteGatedShortTermLocked(out values);
                }

                double result = 0;
                if (count >= 2)
                {
                    double meanPower = 0;
                    for (int i = 0; i < count; i++) meanPower += Math.Pow(10, (values[i] + 0.691) / 10);
                    double relativeGate = Lufs(meanPower / count) - 20;

                    int kept = 0;
                    for (int i = 0; i < count; i++)
                        if (values[i] > relativeGate) values[kept++] = values[i];

                    if (kept >= 2)
                    {
                        Array.Sort(values, 0, kept);
                        double lo = values[(int)((kept - 1) * 0.10 + 0.5)];
                        double hi = values[(int)((kept - 1) * 0.95 + 0.5)];
                        result = Math.Max(0, hi - lo);
                    }
                }

                lock (_lock)
                {
                    _rangeCacheVersion = version;
                    _rangeCacheValue = result;
                }
                return result;
            }
        }
    }

    private static double Avg(Queue<double> q) => q.Count == 0 ? 0 : q.Average();
    private static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(Math.Max(1e-12, meanSquare));
}
