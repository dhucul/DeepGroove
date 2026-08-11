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
/// distinguishable. <see cref="ProgramPeakDb"/> is the click-resistant programme
/// peak that drives the gain recommendation; <see cref="TruePeakDb"/> remains the
/// absolute measurement including isolated artifacts.
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
    long FlatTopCount,
    double ProgramPeakDb,
    double ProgramLoudnessLufs,
    double DcOffsetDb,
    double HumLevelDb,
    int HumFrequencyHz);

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
    private const double ActivityHighPassHz = 150;
    private const double MinimumZeroCrossingsPerSecond = 150;
    private const double MaximumZeroCrossingFraction = 0.40;
    private const double TargetProjectedPeakDb = -3;
    private const double MinimumActiveSeconds = 10;
    private const long MinimumFlatTopEvents = 3;
    private const double FlatTopWindowSeconds = 10;
    private const double FlatTopConsistencyDb = 1;
    private const double FlatTopPeakProximityDb = 3;
    private const int MaximumAnalysisBlocks = 1200; // most recent two minutes at 100 ms

    // Click rejection: a block whose exact peak sits this far above its trimmed
    // (top-0.5%) peak is carrying a narrow artifact, not a musical transient.
    private const double RobustPeakPercentile = 0.99;
    private const double ClickNarrownessDb = 6;
    private const double TrimmedPeakFraction = 0.005;
    private const int PeakHistogramBins = 128; // 0.5 dB per bin over a 64 dB range
    private const double PeakHistogramDbPerBin = 0.5;
    private const double MaximumIntersamplePremiumDb = 3;

    // Dynamic programme (high crest factor) is likelier to hide unseen transients,
    // so it earns up to this much extra reserve beyond the scan-time schedule.
    private const double CrestReserveReferenceDb = 12;
    private const double CrestReserveSlopeDb = 0.5;
    private const double CrestReserveMaximumDb = 3;

    // Hum assessment runs over quiet (non-programme) blocks: a 50/60 Hz component
    // that both dominates those blocks and clears an absolute floor is reported.
    private const int MinimumHumQuietBlocks = 10;
    private const double HumQuietFloorDb = -90;
    private const double HumMinimumLevelDb = -80;
    private const double HumDominanceThresholdDb = -3;

    private readonly object _sync = new();
    private readonly LoudnessMeter _loudness = new();
    private readonly List<BlockSummary> _blocks = [];
    private readonly int[] _peakHistogram = new int[PeakHistogramBins];
    private FlatTopTracker[] _flatTopTrackers = [];
    private Queue<FlatTopEvent>[] _flatTopEvents = [];
    private Biquad[] _activityFilters = [];
    private float[] _previousActivitySamples = [];
    private bool[] _hasPreviousActivitySample = [];

    private int _sampleRate;
    private int _channels;
    private int _blockFrames;
    private int _blockFill;
    private double _blockPower;
    private double _blockLeftPower;
    private double _blockRightPower;
    private double _blockPeak;
    private double _blockActivityPower;
    private double _blockLeftSum;
    private double _blockRightSum;
    private long[] _blockZeroCrossings = [];

    // Goertzel resonators at the two mains frequencies, evaluated per block over
    // the mono mix. A 100 ms block holds exactly 5 and 6 cycles of 50/60 Hz, so
    // the bins land on whole cycles at any sample rate.
    private double _humCoeff50;
    private double _humCoeff60;
    private double _hum50S1;
    private double _hum50S2;
    private double _hum60S1;
    private double _hum60S2;

    private long _framesProcessed;
    private long _clippedSamples;
    private long _invalidSamples;
    private long _flatTopCount;
    private double _peakLeft;
    private double _peakRight;
    private double _overallPeak;

    // Snapshot memoization: the UI polls every ~33 ms while blocks only complete
    // per 100 ms of audio, so percentile work is cached until new samples arrive.
    private RecordingLevelSnapshot? _cachedSnapshot;

    private readonly record struct BlockSummary(
        double RmsDb,
        double ActivityRmsDb,
        double PeakDb,
        double TrimmedPeakDb,
        double Hum50Db,
        double Hum60Db,
        double ZeroCrossingsPerSecond,
        double LeftMeanSquare,
        double RightMeanSquare,
        double LeftMean,
        double RightMean);

    private readonly record struct FlatTopEvent(long Frame, double Level);

    public RecordingLevelAnalyzer(int sampleRate, int channels) => Configure(sampleRate, channels);

    /// <summary>Returns a consistent immutable view of all samples processed so far.</summary>
    public RecordingLevelSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                _cachedSnapshot ??= BuildSnapshot();
                return _cachedSnapshot;
            }
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
            _humCoeff50 = 2 * Math.Cos(2 * Math.PI * 50.0 / sampleRate);
            _humCoeff60 = 2 * Math.Cos(2 * Math.PI * 60.0 / sampleRate);
            _flatTopTrackers = Enumerable.Range(0, channels)
                .Select(_ => new FlatTopTracker())
                .ToArray();
            _flatTopEvents = Enumerable.Range(0, channels)
                .Select(_ => new Queue<FlatTopEvent>())
                .ToArray();
            _activityFilters = Enumerable.Range(0, channels)
                .Select(_ => Biquad.HighPass(sampleRate, ActivityHighPassHz, 0.707))
                .ToArray();
            _previousActivitySamples = new float[channels];
            _hasPreviousActivitySample = new bool[channels];
            _blockZeroCrossings = new long[channels];
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
                double frameActivityPower = 0;
                double frameMix = 0;
                double leftPower = 0;
                double rightPower = 0;
                double leftSample = 0;
                double rightSample = 0;

                for (int channel = 0; channel < _channels; channel++)
                {
                    float raw = interleaved[index + channel];
                    bool valid = float.IsFinite(raw);
                    float sample = valid ? raw : 0f;
                    if (!valid)
                    {
                        _invalidSamples++;
                        _flatTopTrackers[channel].BreakContinuity();
                        _activityFilters[channel].Reset();
                        _hasPreviousActivitySample[channel] = false;
                    }
                    else
                    {
                        double flatTopLevel = _flatTopTrackers[channel].Process(sample);
                        if (flatTopLevel > 0)
                        {
                            _flatTopCount++;
                            Queue<FlatTopEvent> events = _flatTopEvents[channel];
                            events.Enqueue(new FlatTopEvent(_framesProcessed, flatTopLevel));
                            PruneFlatTopEvents(events);
                        }
                    }

                    float activitySample = valid ? _activityFilters[channel].Process(sample) : 0;
                    if (valid)
                    {
                        if (_hasPreviousActivitySample[channel]
                            && Math.Sign(activitySample) != 0
                            && Math.Sign(_previousActivitySamples[channel]) != 0
                            && Math.Sign(activitySample) != Math.Sign(_previousActivitySamples[channel]))
                        {
                            _blockZeroCrossings[channel]++;
                        }
                        _previousActivitySamples[channel] = activitySample;
                        _hasPreviousActivitySample[channel] = true;
                    }

                    double magnitude = Math.Abs((double)sample);
                    if (magnitude >= DigitalClipLevel) _clippedSamples++;
                    if (magnitude > _overallPeak) _overallPeak = magnitude;
                    if (magnitude > _blockPeak) _blockPeak = magnitude;
                    if (magnitude > 1e-9)
                    {
                        int bin = (int)(-20 * Math.Log10(magnitude) / PeakHistogramDbPerBin);
                        _peakHistogram[Math.Clamp(bin, 0, PeakHistogramBins - 1)]++;
                    }
                    if (channel == 0 && magnitude > _peakLeft) _peakLeft = magnitude;
                    if (channel == 1 && magnitude > _peakRight) _peakRight = magnitude;

                    double power = sample * (double)sample;
                    framePower += power;
                    frameActivityPower += activitySample * (double)activitySample;
                    frameMix += sample;
                    if (channel == 0) { leftPower = power; leftSample = sample; }
                    else if (channel == 1) { rightPower = power; rightSample = sample; }
                }

                // Mono folds the one channel into both sides, matching the
                // per-channel power convention used for balance.
                if (_channels == 1) rightSample = leftSample;

                double mix = frameMix / _channels;
                double hum50S0 = mix + _humCoeff50 * _hum50S1 - _hum50S2;
                _hum50S2 = _hum50S1;
                _hum50S1 = hum50S0;
                double hum60S0 = mix + _humCoeff60 * _hum60S1 - _hum60S2;
                _hum60S2 = _hum60S1;
                _hum60S1 = hum60S0;

                _blockPower += framePower;
                _blockActivityPower += frameActivityPower;
                _blockLeftPower += leftPower;
                _blockRightPower += _channels == 1 ? leftPower : rightPower;
                _blockLeftSum += leftSample;
                _blockRightSum += rightSample;
                _blockFill++;
                _framesProcessed++;

                if (_blockFill >= _blockFrames) CompleteBlock();
            }

            _cachedSnapshot = null;
        }
    }

    private void CompleteBlock()
    {
        double frames = Math.Max(1, _blockFill);
        double meanSquare = _blockPower / (frames * _channels);
        double activityMeanSquare = _blockActivityPower / (frames * _channels);
        double leftMeanSquare = _blockLeftPower / frames;
        double rightMeanSquare = _blockRightPower / frames;
        double maximumZeroCrossingsPerSecond = _blockZeroCrossings.Length == 0
            ? 0
            : _blockZeroCrossings.Max() * _sampleRate / frames;
        _blocks.Add(new BlockSummary(
            PowerToDb(meanSquare), PowerToDb(activityMeanSquare), AmplitudeToDb(_blockPeak),
            TrimmedPeakDb(),
            GoertzelPowerDb(_hum50S1, _hum50S2, _humCoeff50, _blockFill),
            GoertzelPowerDb(_hum60S1, _hum60S2, _humCoeff60, _blockFill),
            maximumZeroCrossingsPerSecond,
            leftMeanSquare, rightMeanSquare,
            _blockLeftSum / frames, _blockRightSum / frames));
        if (_blocks.Count > MaximumAnalysisBlocks)
            _blocks.RemoveAt(0);

        _blockFill = 0;
        _blockPower = 0;
        _blockActivityPower = 0;
        _blockLeftPower = 0;
        _blockRightPower = 0;
        _blockLeftSum = 0;
        _blockRightSum = 0;
        _blockPeak = 0;
        _hum50S1 = _hum50S2 = _hum60S1 = _hum60S2 = 0;
        Array.Clear(_blockZeroCrossings);
        Array.Clear(_peakHistogram);
    }

    /// <summary>
    /// Peak of the loudest <see cref="TrimmedPeakFraction"/> of block samples. A
    /// vinyl click is a handful of samples and never reaches this rank, while a
    /// genuine musical transient sustains long enough to define it.
    /// </summary>
    private double TrimmedPeakDb()
    {
        long total = (long)_blockFill * _channels;
        int target = Math.Max(1, (int)(total * TrimmedPeakFraction));
        int cumulative = 0;
        for (int bin = 0; bin < PeakHistogramBins; bin++)
        {
            cumulative += _peakHistogram[bin];
            if (cumulative >= target)
                return -(bin + 0.5) * PeakHistogramDbPerBin;
        }
        return double.NegativeInfinity;
    }

    /// <summary>RMS level of the resonator's frequency bin, in dBFS.</summary>
    private static double GoertzelPowerDb(double s1, double s2, double coeff, int frames)
    {
        if (frames <= 0) return double.NegativeInfinity;
        double dftPower = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        double amplitudeMeanSquare = 2 * dftPower / ((double)frames * frames);
        return amplitudeMeanSquare > 0 ? 10 * Math.Log10(amplitudeMeanSquare) : double.NegativeInfinity;
    }

    private RecordingLevelSnapshot BuildSnapshot()
    {
        double elapsed = _framesProcessed / (double)_sampleRate;
        double peakLeftDb = AmplitudeToDb(_peakLeft);
        double peakRightDb = _channels == 1 ? peakLeftDb : AmplitudeToDb(_peakRight);
        double truePeakDb = _overallPeak > 0
            ? Math.Max(AmplitudeToDb(_overallPeak), _loudness.TruePeakDb)
            : double.NegativeInfinity;

        ClassifyBlocks(out var active, out var quiet, out double noiseFloorDb);
        double activeSeconds = active.Count * _blockFrames / (double)_sampleRate;
        double programRmsDb = active.Count == 0
            ? double.NegativeInfinity
            : Percentile(active.Select(block => block.RmsDb), 0.5);

        // The recommendation is based on the programme peak: a high percentile of
        // per-block peaks after narrow-artifact rejection, plus the material's
        // (capped) intersample premium. Isolated clicks still show in TruePeakDb
        // but no longer dictate the gain for the whole side.
        double programSamplePeakDb = active.Count == 0
            ? double.NegativeInfinity
            : Percentile(active.Select(RobustBlockPeakDb), RobustPeakPercentile);
        double samplePeakDb = AmplitudeToDb(_overallPeak);
        // The intersample premium only describes the programme when the loudest
        // sample was programme itself. When a click owns the global peak, its
        // interpolator ringing says nothing about the music, so no premium is
        // applied (the unseen-transient reserve covers ordinary overs).
        bool globalPeakIsClick = double.IsFinite(programSamplePeakDb)
            && double.IsFinite(samplePeakDb)
            && samplePeakDb - programSamplePeakDb > ClickNarrownessDb;
        double intersamplePremiumDb = double.IsFinite(truePeakDb) && _overallPeak > 0 && !globalPeakIsClick
            ? Math.Clamp(truePeakDb - samplePeakDb, 0, MaximumIntersamplePremiumDb)
            : 0;
        double programPeakDb = double.IsFinite(programSamplePeakDb)
            ? programSamplePeakDb + intersamplePremiumDb
            : double.NegativeInfinity;

        double crestFactorDb = double.IsFinite(programPeakDb) && double.IsFinite(programRmsDb)
            ? Math.Max(0, programPeakDb - programRmsDb)
            : double.NaN;
        double balanceDb = MeasureBalance(active);
        double dcOffsetDb = MeasureDcOffset(active);
        AssessHum(quiet, out double humLevelDb, out int humFrequencyHz);

        bool settled = activeSeconds + 1e-9 >= MinimumActiveSeconds;
        double reserveDb = settled
            ? ReserveFor(activeSeconds) + CrestReservePremiumDb(crestFactorDb)
            : 0;
        double projectedPeakDb = double.IsFinite(programPeakDb)
            ? programPeakDb + reserveDb
            : double.NegativeInfinity;
        double suggestedGainDb = settled && double.IsFinite(projectedPeakDb)
            ? RoundHalfDb(Math.Clamp(TargetProjectedPeakDb - projectedPeakDb, -18, 6))
            : 0;
        double confidence = Math.Min(0.95, 1 - Math.Exp(-activeSeconds / 30.0));

        RecordingLevelStatus status;
        if (_clippedSamples > 0)
            status = RecordingLevelStatus.Clipping;
        else if (HasRepeatedFlatTops())
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
            _flatTopCount,
            programPeakDb,
            _loudness.IntegratedLufs,
            dcOffsetDb,
            humLevelDb,
            humFrequencyHz);
    }

    /// <summary>
    /// A block whose exact peak towers over its trimmed peak carries a narrow
    /// spike; substitute the trimmed peak so clicks cannot steer the gain advice.
    /// </summary>
    private static double RobustBlockPeakDb(BlockSummary block)
    {
        if (double.IsFinite(block.TrimmedPeakDb)
            && double.IsFinite(block.PeakDb)
            && block.PeakDb - block.TrimmedPeakDb > ClickNarrownessDb)
        {
            return block.TrimmedPeakDb;
        }
        return block.PeakDb;
    }

    private void ClassifyBlocks(
        out List<BlockSummary> active,
        out List<BlockSummary> quiet,
        out double noiseFloorDb)
    {
        active = [];
        quiet = [];
        if (_blocks.Count == 0)
        {
            noiseFloorDb = double.NaN;
            return;
        }

        double p10 = Percentile(_blocks.Select(block => block.ActivityRmsDb), 0.10);
        double p50 = Percentile(_blocks.Select(block => block.ActivityRmsDb), 0.50);
        double p90 = Percentile(_blocks.Select(block => block.ActivityRmsDb), 0.90);
        bool anyEnergy = _blocks.Any(block => double.IsFinite(block.ActivityRmsDb));
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
            PartitionBlocks(threshold, active, quiet);
            return;
        }

        // A steady passage has no low percentile that can honestly be called its
        // noise floor. Treat it as programme when it is plainly above silence.
        noiseFloorDb = double.NaN;
        if (p50 > MinimumProgramBlockDb)
            PartitionBlocks(MinimumProgramBlockDb, active, quiet);
    }

    private void PartitionBlocks(double activityThresholdDb, List<BlockSummary> active, List<BlockSummary> quiet)
    {
        foreach (BlockSummary block in _blocks)
        {
            if (IsProgramBlock(block, activityThresholdDb))
                active.Add(block);
            else if (double.IsFinite(block.RmsDb) && block.RmsDb > HumQuietFloorDb)
                quiet.Add(block);
        }
    }

    private bool IsProgramBlock(BlockSummary block, double activityThresholdDb) =>
        block.ActivityRmsDb > activityThresholdDb
        && block.PeakDb > MinimumProgramPeakDb
        && block.ZeroCrossingsPerSecond >= MinimumZeroCrossingsPerSecond
        && block.ZeroCrossingsPerSecond <= _sampleRate * MaximumZeroCrossingFraction;

    /// <summary>
    /// Flags mains hum when one frequency dominates the quiet passages (lead-in
    /// groove, pauses) and is strong enough to matter for a vinyl transfer.
    /// </summary>
    private static void AssessHum(List<BlockSummary> quiet, out double humLevelDb, out int humFrequencyHz)
    {
        humLevelDb = double.NegativeInfinity;
        humFrequencyHz = 0;
        if (quiet.Count < MinimumHumQuietBlocks) return;

        double level50 = Percentile(quiet.Select(block => block.Hum50Db), 0.5);
        double level60 = Percentile(quiet.Select(block => block.Hum60Db), 0.5);
        double dominance50 = Percentile(quiet.Select(block => block.Hum50Db - block.RmsDb), 0.5);
        double dominance60 = Percentile(quiet.Select(block => block.Hum60Db - block.RmsDb), 0.5);
        bool hum50 = level50 >= HumMinimumLevelDb && dominance50 >= HumDominanceThresholdDb;
        bool hum60 = level60 >= HumMinimumLevelDb && dominance60 >= HumDominanceThresholdDb;

        if (hum50 && (!hum60 || level50 >= level60))
        {
            humFrequencyHz = 50;
            humLevelDb = level50;
        }
        else if (hum60)
        {
            humFrequencyHz = 60;
            humLevelDb = level60;
        }
    }

    /// <summary>
    /// Mean DC across the active programme, worst channel. DC wastes asymmetric
    /// headroom and usually indicates a cabling or interface fault.
    /// </summary>
    private double MeasureDcOffset(IReadOnlyList<BlockSummary> active)
    {
        if (active.Count == 0) return double.NegativeInfinity;
        double left = 0;
        double right = 0;
        foreach (var block in active)
        {
            left += block.LeftMean;
            right += block.RightMean;
        }
        double dc = Math.Max(Math.Abs(left), Math.Abs(right)) / active.Count;
        return AmplitudeToDb(dc);
    }

    private bool HasRepeatedFlatTops()
    {
        double overallPeakDb = AmplitudeToDb(_overallPeak);
        foreach (Queue<FlatTopEvent> events in _flatTopEvents)
        {
            PruneFlatTopEvents(events);
            if (events.Count < MinimumFlatTopEvents) continue;

            double maximumLevel = events.Max(entry => entry.Level);
            if (AmplitudeToDb(maximumLevel) < overallPeakDb - FlatTopPeakProximityDb)
                continue;

            int consistentEvents = events.Count(entry =>
                AmplitudeToDb(maximumLevel / entry.Level) <= FlatTopConsistencyDb);
            if (consistentEvents >= MinimumFlatTopEvents) return true;
        }
        return false;
    }

    private void PruneFlatTopEvents(Queue<FlatTopEvent> events)
    {
        long oldestFrame = _framesProcessed - (long)Math.Ceiling(FlatTopWindowSeconds * _sampleRate);
        while (events.Count > 0 && events.Peek().Frame < oldestFrame)
            events.Dequeue();
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

    private static double CrestReservePremiumDb(double crestFactorDb) =>
        double.IsFinite(crestFactorDb)
            ? Math.Clamp((crestFactorDb - CrestReserveReferenceDb) * CrestReserveSlopeDb, 0, CrestReserveMaximumDb)
            : 0;

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
        _blockActivityPower = 0;
        _blockLeftPower = 0;
        _blockRightPower = 0;
        _blockLeftSum = 0;
        _blockRightSum = 0;
        _blockPeak = 0;
        _hum50S1 = _hum50S2 = _hum60S1 = _hum60S2 = 0;
        Array.Clear(_blockZeroCrossings);
        Array.Clear(_peakHistogram);
        _framesProcessed = 0;
        _clippedSamples = 0;
        _invalidSamples = 0;
        _flatTopCount = 0;
        foreach (Queue<FlatTopEvent> events in _flatTopEvents) events.Clear();
        for (int channel = 0; channel < _activityFilters.Length; channel++)
            _activityFilters[channel].Reset();
        Array.Clear(_previousActivitySamples);
        Array.Clear(_hasPreviousActivitySample);
        _peakLeft = 0;
        _peakRight = 0;
        _overallPeak = 0;
        _cachedSnapshot = null;
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

        public double Process(float sample)
        {
            double detectedLevel = 0;
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
                        if (FinishRun(Math.Abs(sample - _previous)))
                            detectedLevel = _runLevel;
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
            return detectedLevel;
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
