namespace WaveLab.Audio.Dsp;

/// <summary>Overall result of a recording-level check.</summary>
public enum RecordingLevelStatus
{
    WaitingForSignal,
    Analyzing,
    TooLow,
    Good,
    Hot,
    Clipping,
    UpstreamClipping,
}

/// <summary>
/// Immutable point-in-time result from <see cref="RecordingLevelAnalyzer"/>.
/// A missing signal level is represented by negative infinity. Noise floor and
/// crest factor are NaN when the captured programme does not make those values
/// distinguishable.
/// </summary>
public sealed record RecordingLevelSnapshot(
    RecordingLevelStatus Status,
    double ElapsedSeconds,
    double ActiveSeconds,
    double Confidence,
    double PeakLeftDb,
    double PeakRightDb,
    double TruePeakDb,
    double ProjectedPeakDb,
    double ProgramRmsDb,
    double NoiseFloorDb,
    double CrestFactorDb,
    double BalanceDb,
    double ReserveDb,
    double SuggestedGainDb,
    long ClippedSamples,
    long InvalidSamples,
    long FlatTopCount);

/// <summary>
/// Streaming, linked-channel input-level analysis for a representative passage.
/// The recommendation protects analogue capture headroom; it does not apply gain
/// to the audio. Instances are safe to feed from a capture callback while the UI
/// reads immutable <see cref="Snapshot"/> values on another thread.
/// </summary>
public sealed class RecordingLevelAnalyzer
{
    private const double DigitalClipLevel = 0.999969;
    private const double MinimumProgramBlockDb = -55;
    private const double QuietProgramSeparationDb = 10;
    private const double MinimumProgramPeakDb = -35;
    private const double TargetProjectedPeakDb = -3;
    private const double MinimumActiveSeconds = 10;
    private const long MinimumFlatTopEvents = 3;
    private const int MaximumAnalysisBlocks = 1200; // most recent two minutes at 100 ms

    private readonly object _sync = new();
    private readonly LoudnessMeter _loudness = new();
    private readonly List<BlockSummary> _blocks = [];
    private FlatTopTracker[] _flatTopTrackers = [];
    private long[] _flatTopCounts = [];

    private int _sampleRate;
    private int _channels;
    private int _blockFrames;
    private int _blockFill;
    private double _blockPower;
    private double _blockLeftPower;
    private double _blockRightPower;
    private double _blockPeak;

    private long _framesProcessed;
    private long _clippedSamples;
    private long _invalidSamples;
    private long _flatTopCount;
    private double _peakLeft;
    private double _peakRight;
    private double _overallPeak;

    private readonly record struct BlockSummary(
        double RmsDb,
        double PeakDb,
        double LeftMeanSquare,
        double RightMeanSquare);

    public RecordingLevelAnalyzer(int sampleRate, int channels) => Configure(sampleRate, channels);

    /// <summary>Returns a consistent immutable view of all samples processed so far.</summary>
    public RecordingLevelSnapshot Snapshot
    {
        get
        {
            lock (_sync) return BuildSnapshot();
        }
    }

    /// <summary>Changes the stream format and clears all prior measurements.</summary>
    public void Configure(int sampleRate, int channels)
    {
        if (sampleRate < 8_000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be at least 8 kHz.");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "At least one channel is required.");

        lock (_sync)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _blockFrames = Math.Max(1, sampleRate / 10);
            _flatTopTrackers = Enumerable.Range(0, channels)
                .Select(_ => new FlatTopTracker())
                .ToArray();
            _flatTopCounts = new long[channels];
            _loudness.Configure(sampleRate, channels);
            ResetCore(resetLoudness: false);
        }
    }

    /// <summary>Clears the measurement while retaining the configured stream format.</summary>
    public void Reset()
    {
        lock (_sync) ResetCore(resetLoudness: true);
    }

    /// <summary>Processes a complete interleaved buffer.</summary>
    public void Process(float[] interleaved)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        Process(interleaved, 0, interleaved.Length);
    }

    /// <summary>Processes complete interleaved sample frames from a buffer range.</summary>
    public void Process(float[] interleaved, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (offset < 0 || offset > interleaved.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || count > interleaved.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count));

        lock (_sync)
        {
            if (count % _channels != 0)
                throw new ArgumentException("Sample count must contain complete channel frames.", nameof(count));
            if (count == 0) return;

            // LoudnessMeter treats invalid samples as zero and deliberately
            // breaks true-peak interpolation continuity across corrupt data.
            _loudness.Process(interleaved, offset, count);

            int end = offset + count;
            for (int index = offset; index < end; index += _channels)
            {
                double framePower = 0;
                double leftPower = 0;
                double rightPower = 0;

                for (int channel = 0; channel < _channels; channel++)
                {
                    float raw = interleaved[index + channel];
                    bool valid = float.IsFinite(raw);
                    float sample = valid ? raw : 0f;
                    if (!valid)
                    {
                        _invalidSamples++;
                        _flatTopTrackers[channel].BreakContinuity();
                    }
                    else if (_flatTopTrackers[channel].Process(sample))
                    {
                        _flatTopCount++;
                        _flatTopCounts[channel]++;
                    }

                    double magnitude = Math.Abs((double)sample);
                    if (magnitude >= DigitalClipLevel) _clippedSamples++;
                    if (magnitude > _overallPeak) _overallPeak = magnitude;
                    if (magnitude > _blockPeak) _blockPeak = magnitude;
                    if (channel == 0 && magnitude > _peakLeft) _peakLeft = magnitude;
                    if (channel == 1 && magnitude > _peakRight) _peakRight = magnitude;

                    double power = sample * (double)sample;
                    framePower += power;
                    if (channel == 0) leftPower = power;
                    else if (channel == 1) rightPower = power;
                }

                _blockPower += framePower;
                _blockLeftPower += leftPower;
                _blockRightPower += _channels == 1 ? leftPower : rightPower;
                _blockFill++;
                _framesProcessed++;

                if (_blockFill >= _blockFrames) CompleteBlock();
            }
        }
    }

    private void CompleteBlock()
    {
        double frames = Math.Max(1, _blockFill);
        double meanSquare = _blockPower / (frames * _channels);
        double leftMeanSquare = _blockLeftPower / frames;
        double rightMeanSquare = _blockRightPower / frames;
        _blocks.Add(new BlockSummary(
            PowerToDb(meanSquare), AmplitudeToDb(_blockPeak),
            leftMeanSquare, rightMeanSquare));
        if (_blocks.Count > MaximumAnalysisBlocks)
            _blocks.RemoveAt(0);

        _blockFill = 0;
        _blockPower = 0;
        _blockLeftPower = 0;
        _blockRightPower = 0;
        _blockPeak = 0;
    }

    private RecordingLevelSnapshot BuildSnapshot()
    {
        double elapsed = _framesProcessed / (double)_sampleRate;
        double peakLeftDb = AmplitudeToDb(_peakLeft);
        double peakRightDb = _channels == 1 ? peakLeftDb : AmplitudeToDb(_peakRight);
        double truePeakDb = _overallPeak > 0
            ? Math.Max(AmplitudeToDb(_overallPeak), _loudness.TruePeakDb)
            : double.NegativeInfinity;

        ClassifyBlocks(out var active, out double noiseFloorDb);
        double activeSeconds = active.Count * _blockFrames / (double)_sampleRate;
        double programRmsDb = active.Count == 0
            ? double.NegativeInfinity
            : Percentile(active.Select(block => block.RmsDb), 0.5);
        double crestFactorDb = double.IsFinite(truePeakDb) && double.IsFinite(programRmsDb)
            ? Math.Max(0, truePeakDb - programRmsDb)
            : double.NaN;
        double balanceDb = MeasureBalance(active);

        bool settled = activeSeconds + 1e-9 >= MinimumActiveSeconds;
        double reserveDb = settled ? ReserveFor(activeSeconds) : 0;
        double projectedPeakDb = double.IsFinite(truePeakDb)
            ? truePeakDb + reserveDb
            : double.NegativeInfinity;
        double suggestedGainDb = settled && double.IsFinite(projectedPeakDb)
            ? RoundHalfDb(Math.Clamp(TargetProjectedPeakDb - projectedPeakDb, -18, 6))
            : 0;
        double confidence = Math.Min(0.95, 1 - Math.Exp(-activeSeconds / 30.0));

        RecordingLevelStatus status;
        if (_clippedSamples > 0)
            status = RecordingLevelStatus.Clipping;
        else if (_flatTopCounts.Any(count => count >= MinimumFlatTopEvents))
            status = RecordingLevelStatus.UpstreamClipping;
        else if (truePeakDb >= 0)
            // An intersample over is not the same as a sample hitting the
            // converter rail, but it is an immediate headroom warning.
            status = RecordingLevelStatus.Hot;
        else if (active.Count == 0)
            status = RecordingLevelStatus.WaitingForSignal;
        else if (!settled)
            status = RecordingLevelStatus.Analyzing;
        else if (suggestedGainDb < -1)
            status = RecordingLevelStatus.Hot;
        else if (suggestedGainDb > 1)
            status = RecordingLevelStatus.TooLow;
        else
            status = RecordingLevelStatus.Good;

        return new RecordingLevelSnapshot(
            status,
            elapsed,
            activeSeconds,
            confidence,
            peakLeftDb,
            peakRightDb,
            truePeakDb,
            projectedPeakDb,
            programRmsDb,
            noiseFloorDb,
            crestFactorDb,
            balanceDb,
            reserveDb,
            suggestedGainDb,
            _clippedSamples,
            _invalidSamples,
            _flatTopCount);
    }

    private void ClassifyBlocks(out List<BlockSummary> active, out double noiseFloorDb)
    {
        active = [];
        if (_blocks.Count == 0)
        {
            noiseFloorDb = double.NaN;
            return;
        }

        double p10 = Percentile(_blocks.Select(block => block.RmsDb), 0.10);
        double p50 = Percentile(_blocks.Select(block => block.RmsDb), 0.50);
        double p90 = Percentile(_blocks.Select(block => block.RmsDb), 0.90);
        bool anyEnergy = _blocks.Any(block => double.IsFinite(block.RmsDb));
        if (!anyEnergy)
        {
            noiseFloorDb = double.NegativeInfinity;
            return;
        }

        double spread = p90 - p10;
        if (double.IsPositiveInfinity(spread) ||
            double.IsFinite(spread) && spread >= QuietProgramSeparationDb)
        {
            noiseFloorDb = p10;
            double threshold = Math.Max(MinimumProgramBlockDb, p10 + QuietProgramSeparationDb);
            active.AddRange(_blocks.Where(block =>
                block.RmsDb > threshold && block.PeakDb > MinimumProgramPeakDb));
            return;
        }

        // A steady passage has no low percentile that can honestly be called its
        // noise floor. Treat it as programme when it is plainly above silence.
        noiseFloorDb = double.NaN;
        if (p50 > MinimumProgramBlockDb)
            active.AddRange(_blocks.Where(block =>
                block.RmsDb > MinimumProgramBlockDb && block.PeakDb > MinimumProgramPeakDb));
    }

    private double MeasureBalance(IReadOnlyList<BlockSummary> active)
    {
        if (_channels < 2 || active.Count == 0) return 0;
        double left = 0;
        double right = 0;
        foreach (var block in active)
        {
            left += block.LeftMeanSquare;
            right += block.RightMeanSquare;
        }
        if (left <= 0 && right <= 0) return 0;
        if (right <= 0) return 60;
        if (left <= 0) return -60;
        // Positive means the left channel is louder.
        return Math.Clamp(10 * Math.Log10(left / right), -60, 60);
    }

    private static double ReserveFor(double activeSeconds)
    {
        if (activeSeconds <= 10) return 6;
        if (activeSeconds < 30) return Lerp(6, 4, (activeSeconds - 10) / 20);
        if (activeSeconds < 60) return Lerp(4, 3, (activeSeconds - 30) / 30);
        if (activeSeconds < 120) return Lerp(3, 2, (activeSeconds - 60) / 60);
        return 2;
    }

    private static double Lerp(double from, double to, double amount) =>
        from + (to - from) * Math.Clamp(amount, 0, 1);

    private static double RoundHalfDb(double value) =>
        Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;

    private static double AmplitudeToDb(double amplitude) =>
        amplitude > 0 ? 20 * Math.Log10(amplitude) : double.NegativeInfinity;

    private static double PowerToDb(double power) =>
        power > 0 ? 10 * Math.Log10(power) : double.NegativeInfinity;

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return double.NaN;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(sorted.Length - 1, lower + 1);
        double fraction = position - lower;
        double low = sorted[lower];
        double high = sorted[upper];
        if (lower == upper || low.Equals(high)) return low;
        if (!double.IsFinite(low) || !double.IsFinite(high))
            return fraction >= 1 ? high : low;
        return low + (high - low) * fraction;
    }

    private void ResetCore(bool resetLoudness)
    {
        _blocks.Clear();
        _blockFill = 0;
        _blockPower = 0;
        _blockLeftPower = 0;
        _blockRightPower = 0;
        _blockPeak = 0;
        _framesProcessed = 0;
        _clippedSamples = 0;
        _invalidSamples = 0;
        _flatTopCount = 0;
        Array.Clear(_flatTopCounts);
        _peakLeft = 0;
        _peakRight = 0;
        _overallPeak = 0;
        foreach (var tracker in _flatTopTrackers) tracker.Reset();
        if (resetLoudness) _loudness.Reset();
    }

    /// <summary>
    /// Streaming counterpart to Restoration's conservative automatic plateau
    /// recognition. Both shoulders must be appreciably steeper than an exact-ish
    /// run, which rejects smooth quantized waveform maxima and steady DC.
    /// </summary>
    private sealed class FlatTopTracker
    {
        private bool _hasPrevious;
        private bool _hasBeforePrevious;
        private float _previous;
        private float _beforePrevious;
        private bool _inRun;
        private int _runLength;
        private int _runSign;
        private double _runLevel;
        private double _shoulderBefore;
        private double _maximumInsideDifference;

        public bool Process(float sample)
        {
            bool detected = false;
            if (_hasPrevious)
            {
                double previousMagnitude = Math.Abs((double)_previous);
                double magnitude = Math.Abs((double)sample);
                int sign = Math.Sign(sample);
                int previousSign = Math.Sign(_previous);

                if (_inRun)
                {
                    double tolerance = Tolerance(_runLevel);
                    double difference = Math.Abs(sample - _previous);
                    bool continues = sign == _runSign && sign != 0 &&
                                     difference <= tolerance * 1.5 &&
                                     Math.Abs(magnitude - _runLevel) <= tolerance * 1.5;
                    if (continues)
                    {
                        _runLength++;
                        _maximumInsideDifference = Math.Max(_maximumInsideDifference, difference);
                        _runLevel = Math.Max(_runLevel, magnitude);
                    }
                    else
                    {
                        detected = FinishRun(Math.Abs(sample - _previous));
                        _inRun = false;
                    }
                }

                if (!_inRun)
                {
                    double level = Math.Max(previousMagnitude, magnitude);
                    double difference = Math.Abs(sample - _previous);
                    bool equalPair = level >= 0.1 && sign != 0 && sign == previousSign &&
                                     difference <= Tolerance(level);
                    if (equalPair)
                    {
                        _inRun = true;
                        _runLength = 2;
                        _runSign = sign;
                        _runLevel = level;
                        _shoulderBefore = _hasBeforePrevious
                            ? Math.Abs(_previous - _beforePrevious)
                            : 0;
                        _maximumInsideDifference = difference;
                    }
                }
            }

            _beforePrevious = _previous;
            _hasBeforePrevious = _hasPrevious;
            _previous = sample;
            _hasPrevious = true;
            return detected;
        }

        private bool FinishRun(double shoulderAfter)
        {
            if (_runLength < 3 || _runLevel < 0.1 || _runLevel >= DigitalClipLevel)
                return false;
            double tolerance = Tolerance(_runLevel);
            if (_maximumInsideDifference > tolerance * 1.5) return false;
            double minimumShoulder = _runLevel * 0.001;
            return _shoulderBefore >= minimumShoulder && shoulderAfter >= minimumShoulder;
        }

        private static double Tolerance(double level) => Math.Max(2e-7, level * 0.0001);

        public void BreakContinuity() => Reset();

        public void Reset()
        {
            _hasPrevious = false;
            _hasBeforePrevious = false;
            _previous = 0;
            _beforePrevious = 0;
            _inRun = false;
            _runLength = 0;
            _runSign = 0;
            _runLevel = 0;
            _shoulderBefore = 0;
            _maximumInsideDifference = 0;
        }
    }
}
