using System.Buffers;

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
/// <para><see cref="IntegratedLufs"/> is whole-stream BS.1770 gated loudness — its
/// own absolute and relative gates exclude lead-in and pauses. That is a different
/// scope from <see cref="ProgramRmsDb"/>, which is the median of the blocks this
/// analyzer classified as programme.</para>
/// <para><see cref="NoiseFloorDb"/> is measured above 150 Hz, so rumble and mains
/// hum are excluded from it; hum is reported separately by
/// <see cref="HumLevelDb"/>. It is NaN when the passage is too steady to have a
/// noise floor distinguishable from its programme.</para>
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
    double IntegratedLufs,
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

    // Block classification rules are shared with RunOutDetector; see
    // ProgramBlockClassifier. Only the minimum peak differs — this analyzer is
    // choosing a gain from representative programme, so it is stricter about what
    // counts as programme than the run-out detector, which only has to notice that
    // the music stopped.
    private const double MinimumProgramBlockDb = ProgramBlockClassifier.MinimumProgramBlockDb;
    private const double ActivityHighPassHz = ProgramBlockClassifier.ActivityHighPassHz;
    private const double MinimumProgramPeakDb = -35;
    /// <summary>Ceiling assumed when the caller does not choose one.</summary>
    public const double DefaultTargetCeilingDb = -3;
    /// <summary>Deepest ceiling the recommendation will aim at. Public so the
    /// settings layer validates against the same range the analyzer enforces.</summary>
    public const double MinimumTargetCeilingDb = -24;
    /// <summary>Highest ceiling the recommendation will aim at.</summary>
    public const double MaximumTargetCeilingDb = -1;
    private const double MinimumActiveSeconds = 10;
    private const long MinimumFlatTopEvents = 3;
    private const double FlatTopWindowSeconds = 10;
    private const double FlatTopConsistencyDb = 1;
    private const double FlatTopPeakProximityDb = 3;
    private const int MaximumAnalysisBlocks = 1200; // most recent two minutes at 100 ms
    private const int MaximumFullScanBlocks = 72_000; // two hours at 100 ms

    // Click rejection: a block whose exact peak sits this far above its trimmed
    // (top-0.5%) peak is carrying a narrow artifact, not a musical transient.
    private const double RobustPeakPercentile = 0.99;
    private const double ClickNarrownessDb = 6;
    private const double TrimmedPeakFraction = 0.005;
    private const int PeakHistogramBins = 128; // 0.5 dB per bin over a 64 dB range
    private const double PeakHistogramDbPerBin = 0.5;

    /// <summary>
    /// Lower magnitude bound of each histogram bin, descending.
    /// </summary>
    /// <remarks>
    /// The bin used to come from -20*log10(magnitude), evaluated per sample per channel
    /// on the WASAPI capture callback — where an overrun drops recorded audio. A binary
    /// search over these edges is seven comparisons and no transcendental, and lands in
    /// exactly the bin the logarithm did.
    /// </remarks>
    private static readonly double[] PeakHistogramEdges = BuildPeakHistogramEdges();

    private static double[] BuildPeakHistogramEdges()
    {
        var edges = new double[PeakHistogramBins];
        for (int bin = 0; bin < PeakHistogramBins; bin++)
            edges[bin] = Math.Pow(10, -(bin + 1) * PeakHistogramDbPerBin / 20.0);
        return edges;
    }

    /// <summary>Smallest bin whose lower bound <paramref name="magnitude"/> exceeds.</summary>
    private static int PeakHistogramBin(double magnitude)
    {
        int low = 0, high = PeakHistogramBins - 1;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (magnitude > PeakHistogramEdges[mid]) high = mid;
            else low = mid + 1;
        }
        return low;
    }
    private const double MaximumIntersamplePremiumDb = 3;

    // Narrow-artifact rejection must not become a blanket amnesty. A block that
    // clips over at least the trimmed-peak fraction of its samples was clipping
    // for long enough to define that block's level, so it is real overload
    // however loud an isolated click elsewhere happens to be. The absolute floor
    // keeps short blocks (8 kHz test material) from qualifying on 4 samples.
    private const long MinimumSustainedClipSamples = 8;

    // Dynamic programme (high crest factor) is likelier to hide unseen transients,
    // so it earns up to this much extra reserve beyond the scan-time schedule.
    private const double CrestReserveReferenceDb = 12;
    private const double CrestReserveSlopeDb = 0.5;
    private const double CrestReserveMaximumDb = 3;

    // Evidence-based reserve. The scan-time schedule is only a floor; what the
    // material actually did carries the rest.
    //
    //  * Tail — the recommendation is built on the 99th percentile of block peaks,
    //    so the loudest moment already observed sits above it. Reserving less than
    //    that gap would let material we have *already heard* breach the ceiling.
    //    Whole-side scans use the maximum rather than a percentile, so their tail
    //    is zero by construction, which is exactly right: nothing is unseen.
    //  * Novelty — how much the running maximum rose during the second half of the
    //    scan. A programme still finding new peaks late is under-sampled; one whose
    //    ceiling settled early has been characterised.
    //
    // Both terms are exactly zero for perfectly steady material, so the schedule
    // still governs on its own there.
    private const double NoveltyLookbackFraction = 0.5;
    private const double NoveltyReserveMaximumDb = 3;
    private const double MaximumReserveDb = 9;
    private const double ConfidenceNoveltyHalfLifeDb = 6;

    // Hum assessment runs over quiet (non-programme) blocks: a 50/60 Hz component
    // that both dominates those blocks and clears an absolute floor is reported.
    private const int MinimumHumQuietBlocks = 10;
    private const double HumQuietFloorDb = -90;
    private const double HumMinimumLevelDb = -80;
    private const double HumDominanceThresholdDb = -3;

    private readonly object _sync = new();
    private readonly LoudnessMeter _loudness = new();
    private readonly BlockHistory _blocks = new();
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
    private long _blockClippedSamples;
    private long _invalidSamples;
    private long _flatTopCount;
    private double _peakLeft;
    private double _peakRight;
    private double _overallPeak;
    private bool _fullDurationScanEnabled;
    private double _targetCeilingDb = DefaultTargetCeilingDb;

    // Snapshot memoization: the UI polls every ~33 ms while blocks only complete
    // per 100 ms of audio, so percentile work is cached until new samples arrive.
    private Exception? _lastSnapshotBuildError;
    private RecordingLevelSnapshot? _cachedSnapshot;
    private long _cachedSnapshotFrame;
    private long _analysisGeneration;
    private long _snapshotBuildGeneration;

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
        double RightMean,
        int Frames,
        long ClippedSamples);

    private readonly record struct FlatTopEvent(long Frame, double Level);

    /// <summary>
    /// Append-only window of completed block summaries. The live entries occupy
    /// [start, start + count) of a buffer that is only ever appended to: making room
    /// copies into a fresh buffer instead of shifting entries in place, so a segment
    /// handed to a snapshot build stays valid while capture keeps running. That lets
    /// the analysis state publish the history without copying it — the copy used to
    /// run inside the lock the capture callback takes, once per second.
    /// </summary>
    private sealed class BlockHistory
    {
        private BlockSummary[] _buffer = [];
        private int _start;
        private int _count;

        public int Count => _count;

        public void Add(in BlockSummary block)
        {
            if (_start + _count == _buffer.Length)
            {
                // Keep a quarter of the window as headroom so the compaction cost is
                // amortized to O(1) per block however the caps are chosen.
                int capacity = Math.Max(64, _buffer.Length);
                while (capacity < _count + _count / 4 + 1) capacity *= 2;
                var grown = new BlockSummary[capacity];
                Array.Copy(_buffer, _start, grown, 0, _count);
                _buffer = grown;
                _start = 0;
            }

            _buffer[_start + _count] = block;
            _count++;
        }

        /// <summary>Drops the oldest entries until at most <paramref name="maximumBlocks"/> remain.</summary>
        public void Trim(int maximumBlocks)
        {
            int maximum = Math.Max(0, maximumBlocks);
            if (_count <= maximum) return;
            _start += _count - maximum;
            _count = maximum;
        }

        /// <summary>A stable view of the current window. Never copies.</summary>
        public ArraySegment<BlockSummary> Publish() => new(_buffer, _start, _count);

        public void Clear()
        {
            // Release the buffer rather than reuse it: a published segment may still
            // be in flight on a snapshot-building thread.
            _buffer = [];
            _start = 0;
            _count = 0;
        }
    }

    private sealed record AnalysisState(
        long Generation,
        int SampleRate,
        int Channels,
        int BlockFrames,
        long FramesProcessed,
        long ClippedSamples,
        long InvalidSamples,
        long FlatTopCount,
        double PeakLeft,
        double PeakRight,
        double OverallPeak,
        double TruePeakDb,
        double IntegratedLufs,
        bool FullDurationScanEnabled,
        double TargetCeilingDb,
        bool HasRepeatedFlatTops,
        ArraySegment<BlockSummary> Blocks);

    public RecordingLevelAnalyzer(int sampleRate, int channels) => Configure(sampleRate, channels);

    /// <summary>
    /// Retain the complete programme history for a deliberate whole-song or
    /// whole-side pass. Normal checks keep a rolling two-minute window.
    /// </summary>
    public bool FullDurationScanEnabled
    {
        get { lock (_sync) return _fullDurationScanEnabled; }
        set
        {
            lock (_sync)
            {
                if (_fullDurationScanEnabled == value) return;
                _fullDurationScanEnabled = value;
                if (!value) _blocks.Trim(MaximumAnalysisBlocks);
                _cachedSnapshot = null;
                _cachedSnapshotFrame = 0;
                _analysisGeneration++;
                _snapshotBuildGeneration = 0;
            }
        }
    }

    /// <summary>
    /// Peak level the recommendation aims the programme at, in dBTP. Lower values
    /// leave more room for the restoration stages that follow a transfer —
    /// declicking and peak reconstruction can put repaired peaks back *above* what
    /// was captured. Clamped to [-24, -1] dB.
    /// </summary>
    public double TargetCeilingDb
    {
        get { lock (_sync) return _targetCeilingDb; }
        set
        {
            double clamped = double.IsFinite(value)
                ? Math.Clamp(value, MinimumTargetCeilingDb, MaximumTargetCeilingDb)
                : DefaultTargetCeilingDb;
            lock (_sync)
            {
                if (_targetCeilingDb.Equals(clamped)) return;
                _targetCeilingDb = clamped;
                _cachedSnapshot = null;
                _cachedSnapshotFrame = 0;
                _analysisGeneration++;
                _snapshotBuildGeneration = 0;
            }
        }
    }

    /// <summary>
    /// Last failure from a background whole-side summary rebuild, or null. Those
    /// rebuilds are best effort and must not disturb capture, so this is how such
    /// a failure becomes visible instead of silently freezing the readout.
    /// </summary>
    internal Exception? LastSnapshotBuildError => Volatile.Read(ref _lastSnapshotBuildError);

    /// <summary>Returns a consistent immutable view of all samples processed so far.</summary>
    public RecordingLevelSnapshot Snapshot => GetSnapshot(forceRefresh: false);

    /// <summary>
    /// Builds a current result even when full-duration live summaries are being
    /// throttled. Call this after the capture stream has been quiesced and before
    /// resetting the analyzer so the final packet cannot be omitted.
    /// </summary>
    public RecordingLevelSnapshot GetFreshSnapshot() => GetSnapshot(forceRefresh: true);

    private RecordingLevelSnapshot GetSnapshot(bool forceRefresh)
    {
        AnalysisState? synchronousState = null;
        AnalysisState? backgroundState = null;
        RecordingLevelSnapshot? result = null;

        lock (_sync)
        {
            if (forceRefresh || _cachedSnapshot == null)
            {
                if (forceRefresh)
                {
                    // Commit a meaningful trailing partial block so the loudest
                    // material in the last fraction of a second still reaches the
                    // history. Shorter tails are dropped: their zero-crossing rate
                    // is statistically meaningless and the trimmed-peak rank
                    // degenerates to the block's exact peak.
                    if (_blockFill * 4 >= _blockFrames) CompleteBlock();
                    // Ring out the true-peak interpolator's remaining taps. This
                    // must not run on the live path, where it would interpolate the
                    // current tail against zeros on every poll and bias the reading.
                    _loudness.FlushTruePeak();
                }
                synchronousState = CaptureAnalysisStateLocked();
            }
            else
            {
                result = _cachedSnapshot;
                bool summaryDue = _fullDurationScanEnabled
                    && _framesProcessed - _cachedSnapshotFrame >= _sampleRate;
                if (summaryDue && _snapshotBuildGeneration == 0)
                {
                    backgroundState = CaptureAnalysisStateLocked();
                    _snapshotBuildGeneration = backgroundState.Generation;
                }
            }
        }

        if (backgroundState != null)
            _ = RebuildCachedSnapshotAsync(backgroundState);
        if (result != null) return result;

        RecordingLevelSnapshot snapshot = BuildSnapshot(synchronousState!);
        PublishSnapshot(synchronousState!, snapshot);
        return snapshot;
    }

    private AnalysisState CaptureAnalysisStateLocked() => new(
        _analysisGeneration,
        _sampleRate,
        _channels,
        _blockFrames,
        _framesProcessed,
        _clippedSamples,
        _invalidSamples,
        _flatTopCount,
        _peakLeft,
        _peakRight,
        _overallPeak,
        _overallPeak > 0
            ? Math.Max(AmplitudeToDb(_overallPeak), _loudness.TruePeakDb)
            : double.NegativeInfinity,
        _loudness.IntegratedLufs,
        _fullDurationScanEnabled,
        _targetCeilingDb,
        HasRepeatedFlatTops(),
        _blocks.Publish());

    private async Task RebuildCachedSnapshotAsync(AnalysisState state)
    {
        try
        {
            RecordingLevelSnapshot snapshot = await Task.Run(() => BuildSnapshot(state))
                .ConfigureAwait(false);
            PublishSnapshot(state, snapshot);
        }
        catch (Exception ex)
        {
            // Live summaries are best effort — a background failure must never
            // reach the capture thread. A forced final snapshot remains available
            // and will report synchronously before the analyzer resets. Recording
            // the failure keeps it from degrading silently to the last good
            // snapshot forever, which is indistinguishable from "nothing changed".
            Volatile.Write(ref _lastSnapshotBuildError, ex);
        }
        finally
        {
            lock (_sync)
            {
                if (_snapshotBuildGeneration == state.Generation)
                    _snapshotBuildGeneration = 0;
            }
        }
    }

    private void PublishSnapshot(AnalysisState state, RecordingLevelSnapshot snapshot)
    {
        lock (_sync)
        {
            if (state.Generation != _analysisGeneration) return;
            if (_cachedSnapshot != null && _cachedSnapshotFrame > state.FramesProcessed) return;
            _cachedSnapshot = snapshot;
            _cachedSnapshotFrame = state.FramesProcessed;
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

    /// <summary>
    /// Processes complete interleaved sample frames from a buffer range.
    /// <paramref name="sourceClippedSamples"/> is the caller's own pre-attenuation
    /// clip count for this buffer, and <paramref name="sourceGain"/> the scalar
    /// attenuation already applied to it — together they let a caller that trims
    /// the signal keep both the total and its per-block distribution honest.
    /// </summary>
    public void Process(
        float[] interleaved,
        int offset,
        int count,
        long? sourceClippedSamples = null,
        double sourceGain = 1)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (offset < 0 || offset > interleaved.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || count > interleaved.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (sourceClippedSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceClippedSamples));

        lock (_sync)
        {
            if (count % _channels != 0)
                throw new ArgumentException("Sample count must contain complete channel frames.", nameof(count));
            if (count == 0) return;
            // The caller owns the total, because only it saw the signal before its
            // own attenuation. The per-block distribution is measured here instead
            // of being apportioned from that total: a capture buffer may be up to
            // 500 ms — five blocks — and crediting a whole packet's clicks to
            // whichever block happened to be open would invent sustained overload
            // out of scattered stylus hits.
            if (sourceClippedSamples is long clipped) _clippedSamples += clipped;
            double clipLevel = sourceGain > 0 && sourceGain <= 1
                ? DigitalClipLevel * sourceGain
                : DigitalClipLevel;

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
                    if (magnitude >= clipLevel) _blockClippedSamples++;
                    if (sourceClippedSamples is null && magnitude >= DigitalClipLevel) _clippedSamples++;
                    if (magnitude > _overallPeak) _overallPeak = magnitude;
                    if (magnitude > _blockPeak) _blockPeak = magnitude;
                    if (magnitude > 1e-9) _peakHistogram[PeakHistogramBin(magnitude)]++;
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
            _blockLeftSum / frames, _blockRightSum / frames,
            _blockFill, _blockClippedSamples));
        int maximumBlocks = _fullDurationScanEnabled
            ? MaximumFullScanBlocks
            : MaximumAnalysisBlocks;
        _blocks.Trim(maximumBlocks);

        // Percentile-derived content only changes when a block completes, so the
        // live cache is invalidated here rather than once per capture packet. The
        // scalar passthroughs (elapsed, clip counts, peaks) then lag by under one
        // block; the visible transport clock reads the engine, not the snapshot.
        if (!_fullDurationScanEnabled) _cachedSnapshot = null;

        _blockFill = 0;
        _blockPower = 0;
        _blockActivityPower = 0;
        _blockLeftPower = 0;
        _blockRightPower = 0;
        _blockLeftSum = 0;
        _blockRightSum = 0;
        _blockPeak = 0;
        _blockClippedSamples = 0;
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

    private static RecordingLevelSnapshot BuildSnapshot(AnalysisState state)
    {
        // Everything below runs on pooled scratch and index sets rather than LINQ
        // over copied summaries. A whole-side rebuild classifies up to 72 000
        // 88-byte blocks; materializing those into lists and sorting nine separate
        // LINQ projections moved several megabytes per second through the large
        // object heap, once per second, for a readout that changes at 10 Hz.
        int blockCount = state.Blocks.Count;
        double[] scratch = blockCount > 0 ? ArrayPool<double>.Shared.Rent(blockCount) : [];
        var active = new BlockIndexSet(blockCount);
        var quiet = new BlockIndexSet(blockCount);
        try
        {
            return BuildSnapshotCore(state, scratch, ref active, ref quiet);
        }
        finally
        {
            active.Dispose();
            quiet.Dispose();
            if (scratch.Length > 0) ArrayPool<double>.Shared.Return(scratch);
        }
    }

    private static RecordingLevelSnapshot BuildSnapshotCore(
        AnalysisState state,
        double[] scratch,
        ref BlockIndexSet active,
        ref BlockIndexSet quiet)
    {
        double elapsed = state.FramesProcessed / (double)state.SampleRate;
        double peakLeftDb = AmplitudeToDb(state.PeakLeft);
        double peakRightDb = state.Channels == 1 ? peakLeftDb : AmplitudeToDb(state.PeakRight);
        double truePeakDb = state.TruePeakDb;

        ClassifyBlocks(state.Blocks, state.SampleRate, scratch,
            ref active, ref quiet, out double noiseFloorDb);
        ReadOnlySpan<int> activeBlocks = active.Span;
        ReadOnlySpan<int> quietBlocks = quiet.Span;

        // Real frames, not whole blocks: a committed trailing partial block is
        // shorter than the rest. Identical arithmetic while every block is full.
        long activeFrames = 0;
        foreach (int index in activeBlocks) activeFrames += state.Blocks[index].Frames;
        double activeSeconds = activeFrames / (double)state.SampleRate;
        double programRmsDb = activeBlocks.Length == 0
            ? double.NegativeInfinity
            : PercentileOf(state.Blocks, activeBlocks, BlockKey.Rms, scratch, 0.5);

        // The recommendation is based on the programme peak: a high percentile of
        // per-block peaks after narrow-artifact rejection, plus the material's
        // (capped) intersample premium. Isolated clicks still show in TruePeakDb
        // but no longer dictate the gain for the whole side.
        double programSamplePeakDb;
        if (activeBlocks.Length == 0)
        {
            programSamplePeakDb = double.NegativeInfinity;
        }
        else if (state.FullDurationScanEnabled)
        {
            // A whole-side scan has seen everything, so it uses the maximum and
            // needs no sort at all.
            programSamplePeakDb = double.NegativeInfinity;
            foreach (int index in activeBlocks)
            {
                double peak = RobustBlockPeakDb(state.Blocks[index]);
                if (peak > programSamplePeakDb) programSamplePeakDb = peak;
            }
        }
        else
        {
            programSamplePeakDb = PercentileOf(
                state.Blocks, activeBlocks, BlockKey.RobustPeak, scratch, RobustPeakPercentile);
        }
        double samplePeakDb = AmplitudeToDb(state.OverallPeak);
        // The intersample premium only describes the programme when the loudest
        // sample was programme itself. When a click owns the global peak, its
        // interpolator ringing says nothing about the music, so no premium is
        // applied (the unseen-transient reserve covers ordinary overs).
        bool narrowArtifactOwnsAbsolutePeak = double.IsFinite(programSamplePeakDb)
            && double.IsFinite(samplePeakDb)
            && samplePeakDb - programSamplePeakDb > ClickNarrownessDb;
        double intersamplePremiumDb = double.IsFinite(truePeakDb)
            && state.OverallPeak > 0
            && !narrowArtifactOwnsAbsolutePeak
            ? Math.Clamp(truePeakDb - samplePeakDb, 0, MaximumIntersamplePremiumDb)
            : 0;
        double programPeakDb = double.IsFinite(programSamplePeakDb)
            ? programSamplePeakDb + intersamplePremiumDb
            : double.NegativeInfinity;

        double crestFactorDb = double.IsFinite(programPeakDb) && double.IsFinite(programRmsDb)
            ? Math.Max(0, programPeakDb - programRmsDb)
            : double.NaN;
        double balanceDb = MeasureBalance(state.Blocks, activeBlocks, state.Channels);
        double dcOffsetDb = MeasureDcOffset(state.Blocks, activeBlocks);
        AssessHum(state.Blocks, quietBlocks, scratch, out double humLevelDb, out int humFrequencyHz);

        MeasurePeakEvidence(state.Blocks, activeBlocks, activeFrames, programSamplePeakDb,
            out double tailDb, out double noveltyDb);

        bool settled = activeSeconds + 1e-9 >= MinimumActiveSeconds;
        double reserveDb = settled
            ? Math.Clamp(
                Math.Max(ReserveFor(activeSeconds), tailDb + noveltyDb)
                    + CrestReservePremiumDb(crestFactorDb),
                0,
                MaximumReserveDb)
            : 0;
        double projectedPeakDb = double.IsFinite(programPeakDb)
            ? programPeakDb + reserveDb
            : double.NegativeInfinity;
        double suggestedGainDb = 0;
        if (settled && double.IsFinite(projectedPeakDb))
        {
            double projectedAdjustmentDb = state.TargetCeilingDb - projectedPeakDb;
            // Reserve protects an optional increase from unseen peaks, but it
            // must not force an already-safe measured programme even lower.
            // Required attenuation is based on the programme itself; this keeps
            // real takes near the ceiling while still leaving optional increases
            // conservative when only a short passage has been sampled.
            double adjustmentDb = projectedAdjustmentDb >= 0
                ? projectedAdjustmentDb
                : double.IsFinite(programPeakDb) && programPeakDb > state.TargetCeilingDb
                    ? state.TargetCeilingDb - programPeakDb
                    : 0;
            suggestedGainDb = RoundHalfDb(Math.Clamp(adjustmentDb, -18, 6));
        }
        // Time still sets the ceiling on confidence, but a programme whose maximum
        // is still climbing has not been characterised however long it has run.
        double confidence = Math.Min(0.95, 1 - Math.Exp(-activeSeconds / 30.0))
            / (1 + noveltyDb / ConfidenceNoveltyHalfLifeDb);

        // Evidence that the rail was held, not merely touched: without this the
        // narrow-artifact test below suppresses every warning as soon as one
        // isolated click outranks the programme, however hard the converter is
        // actually being driven.
        bool hasSustainedRailHits = false;
        foreach (BlockSummary block in state.Blocks)
        {
            long threshold = Math.Max(
                MinimumSustainedClipSamples,
                (long)(block.Frames * (long)state.Channels * TrimmedPeakFraction));
            if (block.ClippedSamples >= threshold) { hasSustainedRailHits = true; break; }
        }

        RecordingLevelStatus status;
        // A stylus click can touch (or interpolate above) full scale while the
        // musical programme remains conservative. Lowering the whole side to
        // preserve a handful of samples that declicking will replace produces a
        // needlessly quiet transfer. Only let rail hits or true-peak overs latch
        // the warning when the robust programme peak says they are not a narrow
        // artifact. Clipping that occupies a meaningful part of any single block
        // is sustained overload by definition and overrides that amnesty.
        bool artifactAmnesty = narrowArtifactOwnsAbsolutePeak && !hasSustainedRailHits;
        if (state.ClippedSamples > 0 && !artifactAmnesty)
            status = RecordingLevelStatus.Clipping;
        else if (state.HasRepeatedFlatTops && !artifactAmnesty)
            status = RecordingLevelStatus.UpstreamClipping;
        else if (truePeakDb >= 0 && !artifactAmnesty)
            // An intersample over is not the same as a sample hitting the
            // converter rail, but it is an immediate headroom warning.
            status = RecordingLevelStatus.Hot;
        else if (activeBlocks.Length == 0)
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
            state.ClippedSamples,
            state.InvalidSamples,
            state.FlatTopCount,
            programPeakDb,
            state.IntegratedLufs,
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

    private static void ClassifyBlocks(
        ArraySegment<BlockSummary> blocks,
        int sampleRate,
        double[] scratch,
        ref BlockIndexSet active,
        ref BlockIndexSet quiet,
        out double noiseFloorDb)
    {
        if (blocks.Count == 0)
        {
            noiseFloorDb = double.NaN;
            return;
        }

        // One sort serves all three percentiles.
        bool anyEnergy = false;
        for (int index = 0; index < blocks.Count; index++)
        {
            double activityDb = blocks[index].ActivityRmsDb;
            scratch[index] = activityDb;
            if (double.IsFinite(activityDb)) anyEnergy = true;
        }
        if (!anyEnergy)
        {
            noiseFloorDb = double.NegativeInfinity;
            return;
        }

        Array.Sort(scratch, 0, blocks.Count);
        ReadOnlySpan<double> sorted = scratch.AsSpan(0, blocks.Count);
        double p10 = PercentileOfSorted(sorted, 0.10);
        double p50 = PercentileOfSorted(sorted, 0.50);
        double p90 = PercentileOfSorted(sorted, 0.90);

        if (ProgramBlockClassifier.HasSeparableFloor(p10, p90))
        {
            noiseFloorDb = p10;
            PartitionBlocks(blocks, sampleRate,
                ProgramBlockClassifier.ActivityThresholdDb(p10, p90), ref active, ref quiet);
            return;
        }

        // A steady passage has no low percentile that can honestly be called its
        // noise floor. Treat it as programme when it is plainly above silence.
        noiseFloorDb = double.NaN;
        if (p50 > MinimumProgramBlockDb)
            PartitionBlocks(blocks, sampleRate, MinimumProgramBlockDb, ref active, ref quiet);
    }

    private static void PartitionBlocks(
        ArraySegment<BlockSummary> blocks,
        int sampleRate,
        double activityThresholdDb,
        ref BlockIndexSet active,
        ref BlockIndexSet quiet)
    {
        for (int index = 0; index < blocks.Count; index++)
        {
            BlockSummary block = blocks[index];
            if (IsProgramBlock(block, sampleRate, activityThresholdDb))
                active.Add(index);
            else if (double.IsFinite(block.RmsDb) && block.RmsDb > HumQuietFloorDb)
                quiet.Add(index);
        }
    }

    private enum BlockKey { Rms, RobustPeak, Hum50, Hum60, Hum50Dominance, Hum60Dominance }

    private static double KeyOf(in BlockSummary block, BlockKey key) => key switch
    {
        BlockKey.Rms => block.RmsDb,
        BlockKey.RobustPeak => RobustBlockPeakDb(block),
        BlockKey.Hum50 => block.Hum50Db,
        BlockKey.Hum60 => block.Hum60Db,
        BlockKey.Hum50Dominance => block.Hum50Db - block.RmsDb,
        _ => block.Hum60Db - block.RmsDb,
    };

    private static double PercentileOf(
        ArraySegment<BlockSummary> blocks,
        ReadOnlySpan<int> indices,
        BlockKey key,
        double[] scratch,
        double percentile)
    {
        if (indices.Length == 0) return double.NaN;
        for (int position = 0; position < indices.Length; position++)
            scratch[position] = KeyOf(blocks[indices[position]], key);
        Array.Sort(scratch, 0, indices.Length);
        return PercentileOfSorted(scratch.AsSpan(0, indices.Length), percentile);
    }

    /// <summary>
    /// Indices into the published block window, backed by the shared array pool.
    /// Holding indices rather than copies keeps the 88-byte summaries out of the
    /// classification entirely.
    /// </summary>
    private struct BlockIndexSet
    {
        private int[] _buffer;
        private int _count;

        public BlockIndexSet(int capacity)
        {
            _buffer = capacity > 0 ? ArrayPool<int>.Shared.Rent(capacity) : [];
            _count = 0;
        }

        public readonly ReadOnlySpan<int> Span => _buffer.AsSpan(0, _count);

        public void Add(int index) => _buffer[_count++] = index;

        public void Dispose()
        {
            if (_buffer.Length > 0) ArrayPool<int>.Shared.Return(_buffer);
            _buffer = [];
            _count = 0;
        }
    }

    private static bool IsProgramBlock(BlockSummary block, int sampleRate, double activityThresholdDb) =>
        ProgramBlockClassifier.IsProgram(
            block.ActivityRmsDb, activityThresholdDb,
            block.PeakDb, MinimumProgramPeakDb,
            block.ZeroCrossingsPerSecond, sampleRate);

    /// <summary>
    /// Flags mains hum when one frequency dominates the quiet passages (lead-in
    /// groove, pauses) and is strong enough to matter for a vinyl transfer.
    /// </summary>
    private static void AssessHum(
        ArraySegment<BlockSummary> blocks,
        ReadOnlySpan<int> quiet,
        double[] scratch,
        out double humLevelDb,
        out int humFrequencyHz)
    {
        humLevelDb = double.NegativeInfinity;
        humFrequencyHz = 0;
        if (quiet.Length < MinimumHumQuietBlocks) return;

        double level50 = PercentileOf(blocks, quiet, BlockKey.Hum50, scratch, 0.5);
        double level60 = PercentileOf(blocks, quiet, BlockKey.Hum60, scratch, 0.5);
        double dominance50 = PercentileOf(blocks, quiet, BlockKey.Hum50Dominance, scratch, 0.5);
        double dominance60 = PercentileOf(blocks, quiet, BlockKey.Hum60Dominance, scratch, 0.5);
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
    private static double MeasureDcOffset(
        ArraySegment<BlockSummary> blocks,
        ReadOnlySpan<int> active)
    {
        if (active.Length == 0) return double.NegativeInfinity;
        double left = 0;
        double right = 0;
        foreach (int index in active)
        {
            left += blocks[index].LeftMean;
            right += blocks[index].RightMean;
        }
        double dc = Math.Max(Math.Abs(left), Math.Abs(right)) / active.Length;
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

    private static double MeasureBalance(
        ArraySegment<BlockSummary> blocks,
        ReadOnlySpan<int> active,
        int channels)
    {
        if (channels < 2 || active.Length == 0) return 0;
        double left = 0;
        double right = 0;
        foreach (int index in active)
        {
            left += blocks[index].LeftMeanSquare;
            right += blocks[index].RightMeanSquare;
        }
        if (left <= 0 && right <= 0) return 0;
        if (right <= 0) return 60;
        if (left <= 0) return -60;
        // Positive means the left channel is louder.
        return Math.Clamp(10 * Math.Log10(left / right), -60, 60);
    }

    /// <summary>
    /// Two views of how completely the programme's ceiling has been sampled.
    /// <paramref name="tailDb"/> is how far the loudest block already sits above
    /// the peak the recommendation is built on; <paramref name="noveltyDb"/> is how
    /// much the running maximum rose over the later part of the scan. Both are zero
    /// for material whose peak never moves.
    /// </summary>
    private static void MeasurePeakEvidence(
        ArraySegment<BlockSummary> blocks,
        ReadOnlySpan<int> active,
        long activeFrames,
        double programSamplePeakDb,
        out double tailDb,
        out double noveltyDb)
    {
        tailDb = 0;
        noveltyDb = 0;
        if (active.Length == 0 || !double.IsFinite(programSamplePeakDb)) return;

        long lookbackFrames = (long)(activeFrames * NoveltyLookbackFraction);
        long seenFrames = 0;
        double runningMaximumDb = double.NegativeInfinity;
        double lookbackMaximumDb = double.NegativeInfinity;
        bool lookbackCaptured = false;

        foreach (int index in active)
        {
            BlockSummary block = blocks[index];
            double peakDb = RobustBlockPeakDb(block);
            if (peakDb > runningMaximumDb) runningMaximumDb = peakDb;
            seenFrames += block.Frames;
            if (!lookbackCaptured && seenFrames >= lookbackFrames)
            {
                lookbackMaximumDb = runningMaximumDb;
                lookbackCaptured = true;
            }
        }

        if (!double.IsFinite(runningMaximumDb)) return;
        tailDb = Math.Max(0, runningMaximumDb - programSamplePeakDb);
        if (lookbackCaptured && double.IsFinite(lookbackMaximumDb))
        {
            noveltyDb = Math.Clamp(
                runningMaximumDb - lookbackMaximumDb, 0, NoveltyReserveMaximumDb);
        }
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

    /// <summary>
    /// Linear-interpolated percentile of an already-sorted span. Infinities are
    /// never interpolated across — a run that reaches the boundary takes the
    /// endpoint rather than producing NaN from (∞ − ∞).
    /// </summary>
    private static double PercentileOfSorted(ReadOnlySpan<double> sorted, double percentile)
    {
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
        _blockClippedSamples = 0;
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
        _cachedSnapshotFrame = 0;
        _analysisGeneration++;
        _snapshotBuildGeneration = 0;
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
