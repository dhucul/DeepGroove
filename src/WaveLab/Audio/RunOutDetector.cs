using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>
/// Finds the end of a side: the point where programme content stops and only
/// the run-out groove — surface noise, rumble and the odd click — is left.
///
/// The decision is deliberately relative rather than a fixed dBFS gate. Vinyl
/// "silence" is never silent, so the detector learns this transfer's own noise
/// floor and calls a block programme only when it rises clearly above that
/// floor. The measurement mirrors <see cref="Dsp.RecordingLevelAnalyzer"/>: a
/// 150 Hz high-passed RMS (so turntable rumble and mains hum cannot pass for
/// music), a zero-crossing sanity range (so hiss cannot either), and the same
/// 10 dB quiet-to-programme separation. The block level is the median of its ten
/// sub-blocks, so a single click cannot lift a run-out block into looking like
/// music.
///
/// <para>The floor is learned over the <em>whole take</em>, from blocks quiet
/// enough that no programme could be that quiet, and never from a sliding window
/// of whatever has just played. A window that has scrolled past the lead-in
/// holds nothing but music, and the low percentile of music is quiet music: on a
/// real transfer that put the threshold at −18 dB when the groove noise was at
/// −66, i.e. within a decibel of the median level of the song itself. A fade-out
/// then reads as run-out from the moment it drops below its own average, and the
/// take is trimmed back to there — which is what this replaced.</para>
///
/// Nothing triggers until programme has been heard at least once, so arming the
/// recorder before the stylus is down can never end the take on its own.
/// </summary>
internal sealed class RunOutDetector
{
    private const double SubBlockSeconds = 0.01;
    private const int SubBlocksPerBlock = 10;
    // Three one-second medians make a falling tail a sequence, not a comparison
    // between two possibly unrepresentative endpoints.
    private const int TailTrendSegmentBlocks = 10;
    private const int TailTrendBlocks = TailTrendSegmentBlocks * 3;
    private const double MinimumFadeSegmentDropDb = 0.15;
    private const double FadeFloorMarginDb = 1.5;
    private const double FadeSettlementSeconds = 3;
    // Classification rules live in ProgramBlockClassifier, shared with the offline
    // RecordingLevelAnalyzer. Only the minimum peak is local: this detector merely
    // has to notice that the music stopped, so it accepts quieter programme than
    // the analyzer will build a gain recommendation from.
    private const double ActivityHighPassHz = ProgramBlockClassifier.ActivityHighPassHz;
    private const double MinimumProgramBlockDb = ProgramBlockClassifier.MinimumProgramBlockDb;
    private const double MinimumProgramPeakDb = -60;

    /// <summary>
    /// How many of the take's quietest blocks the floor is read from. Taking the
    /// loudest of them rather than the single quietest keeps one dropout, or the
    /// silence before the stylus lands, from defining the floor on its own; one
    /// second of it is enough, because any lead-in or inter-track gap is longer
    /// than that.
    /// </summary>
    private const int FloorBlocks = 10;

    /// <summary>
    /// Below this a block is a dead input rather than a groove, and must not be
    /// admitted to the floor. Nothing rises above it again — the floor only
    /// ratchets down — so silence between arming the recorder and cueing the
    /// stylus would otherwise latch the floor for the whole take and pin the
    /// gate at <see cref="MinimumProgramBlockDb"/>, which a transfer whose
    /// groove noise is louder than that never stops against. The measured
    /// separation is wide: the quietest real lead-in in the corpus reads −73.8
    /// and digital silence reads −86 to −90.
    /// </summary>
    private const double MinimumMediumFloorDb = -80;

    public const double MinimumHoldSeconds = 5;
    public const double MaximumHoldSeconds = 60;
    public const double DefaultHoldSeconds = 12;

    /// <summary>Safety margin kept after the last programme block or detected fade endpoint.</summary>
    public const double KeepAfterProgramSeconds = 4;

    private readonly double _holdSeconds;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _subBlockFrames;
    private readonly int _blockFrames;
    private readonly Biquad[] _activityFilters;
    private readonly float[] _previousActivitySamples;
    private readonly bool[] _hasPreviousActivitySample;
    private readonly long[] _blockZeroCrossings;
    private readonly double[] _subBlockLevelsDb = new double[SubBlocksPerBlock];
    private readonly double[] _tailLevelsDb = new double[TailTrendBlocks];

    /// <summary>The take's quietest qualifying blocks so far, ascending.</summary>
    private readonly double[] _quietestDb = new double[FloorBlocks];
    private int _quietCount;
    private int _subBlockFill;
    private int _subBlockIndex;
    private double _subBlockActivityPower;
    private int _blockFill;
    private double _blockPeak;

    private long _samplesSinceProgram;
    private long _pendingBlockSamples;
    private long _samplesSinceFadeEnd;
    private int _tailLevelCount;
    private bool _fadeSettlementComplete;

    public RunOutDetector(int sampleRate, int channels, double holdSeconds = DefaultHoldSeconds)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _sampleRate = sampleRate;
        _channels = channels;
        _holdSeconds = NormalizeHoldSeconds(holdSeconds);
        _subBlockFrames = Math.Max(1, (int)Math.Round(sampleRate * SubBlockSeconds));
        _blockFrames = _subBlockFrames * SubBlocksPerBlock;
        _activityFilters = Enumerable.Range(0, channels)
            .Select(_ => Biquad.HighPass(sampleRate, ActivityHighPassHz, 0.707))
            .ToArray();
        _previousActivitySamples = new float[channels];
        _hasPreviousActivitySample = new bool[channels];
        _blockZeroCrossings = new long[channels];
    }

    /// <summary>True once programme content has been heard at least once.</summary>
    public bool HasHeardProgram { get; private set; }

    /// <summary>True once the run-out hold has elapsed; latched.</summary>
    public bool IsTriggered { get; private set; }

    /// <summary>
    /// True when the final below-threshold interval contained a continuing fade.
    /// Such blocks defer the run-out hold until their level settles.
    /// </summary>
    public bool PreservedFadingTail { get; private set; }

    public double HoldSeconds => _holdSeconds;

    /// <summary>
    /// Interleaved samples processed since the end of the last programme block.
    /// The caller turns this into an absolute trim point by subtracting it from
    /// its own retained-sample total, so this detector needs no knowledge of any
    /// pre-roll promoted ahead of it.
    /// </summary>
    public long SamplesSinceProgram => _samplesSinceProgram;

    public double SecondsSinceProgram =>
        (double)_samplesSinceProgram / _channels / _sampleRate;

    /// <summary>
    /// Seconds left before the take stops, or NaN when nothing is being held —
    /// no programme heard yet, music still playing, or already triggered.
    /// </summary>
    public double CountdownSeconds
    {
        get
        {
            if (IsTriggered || !HasHeardProgram) return double.NaN;
            double elapsed = SecondsSinceProgram;
            return elapsed <= 0 ? double.NaN : Math.Max(0, _holdSeconds - elapsed);
        }
    }

    /// <summary>
    /// Samples to drop, counted back from the caller's retained-sample total at
    /// the moment this triggered. Zero until the hold has elapsed.
    /// </summary>
    public long TrimBackoffSamples
    {
        get
        {
            if (!IsTriggered) return 0;
            long keep = (long)Math.Round(KeepAfterProgramSeconds * _sampleRate) * _channels;
            long tail = PreservedFadingTail ? _samplesSinceFadeEnd : _samplesSinceProgram;
            return Math.Max(0, tail - keep);
        }
    }

    public static double NormalizeHoldSeconds(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumHoldSeconds, MaximumHoldSeconds)
            : DefaultHoldSeconds;

    /// <summary>
    /// Feed one capture packet. Returns true on the packet that completes the
    /// hold; afterwards it keeps returning true without re-evaluating.
    /// </summary>
    public bool Process(float[] samples, int count, int channels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (count < 0 || count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (channels != _channels)
            throw new ArgumentException("The channel count changed mid-session.", nameof(channels));
        if (IsTriggered) return true;

        int completeCount = count - count % _channels;
        for (int index = 0; index < completeCount; index += _channels)
        {
            AccumulateFrame(samples, index);
            _pendingBlockSamples += _channels;

            if (++_subBlockFill >= _subBlockFrames) CompleteSubBlock();
            if (++_blockFill < _blockFrames) continue;

            bool program = CompleteBlock(out double activityDb);
            // A flat transfer can fall from the programme gate to the groove in less
            // than the three-second trend window. Retain the recent music too, so the
            // first below-gate block can recognize a fade already in progress.
            if (program || HasHeardProgram) ObserveTailLevel(activityDb);

            if (program)
            {
                // The block just ended, so everything up to its end is
                // programme; anything read past it starts the next hold.
                _samplesSinceProgram = 0;
                _pendingBlockSamples = 0;
                HasHeardProgram = true;
                ResetFadeHold();
            }
            else
            {
                bool fading = HasHeardProgram && TailIsStillFading();

                if (fading)
                {
                    // This block remains part of a descending musical tail. Move
                    // the last-content point forward and do not spend any of the
                    // run-out hold until the level has settled near the groove.
                    _samplesSinceProgram = 0;
                    _pendingBlockSamples = 0;
                    _samplesSinceFadeEnd = 0;
                    _fadeSettlementComplete = false;
                    PreservedFadingTail = true;
                    continue;
                }

                if (PreservedFadingTail && !_fadeSettlementComplete)
                {
                    _samplesSinceFadeEnd += _pendingBlockSamples;
                    _samplesSinceProgram = 0;
                    _pendingBlockSamples = 0;
                    if (SamplesToSeconds(_samplesSinceFadeEnd) >= FadeSettlementSeconds)
                        _fadeSettlementComplete = true;
                    continue;
                }

                if (PreservedFadingTail)
                    _samplesSinceFadeEnd += _pendingBlockSamples;
                _samplesSinceProgram += _pendingBlockSamples;
                _pendingBlockSamples = 0;
                if (HasHeardProgram && SecondsSinceProgram >= _holdSeconds)
                {
                    IsTriggered = true;
                    return true;
                }
            }
        }

        return false;
    }

    private void AccumulateFrame(float[] samples, int frameStart)
    {
        for (int channel = 0; channel < _channels; channel++)
        {
            float raw = samples[frameStart + channel];
            float sample = float.IsFinite(raw) ? raw : 0f;
            double magnitude = Math.Abs(sample);
            if (magnitude > _blockPeak) _blockPeak = magnitude;

            float activity = _activityFilters[channel].Process(sample);
            if (!float.IsFinite(activity)) activity = 0f;
            _subBlockActivityPower += (double)activity * activity;

            if (_hasPreviousActivitySample[channel]
                && activity != 0
                && Math.Sign(activity) != Math.Sign(_previousActivitySamples[channel]))
            {
                _blockZeroCrossings[channel]++;
            }
            _previousActivitySamples[channel] = activity;
            _hasPreviousActivitySample[channel] = true;
        }
    }

    private void CompleteSubBlock()
    {
        double rms = Math.Sqrt(_subBlockActivityPower / ((double)_subBlockFill * _channels));
        _subBlockLevelsDb[Math.Min(_subBlockIndex, SubBlocksPerBlock - 1)] = ToDb(rms);
        _subBlockIndex++;
        _subBlockFill = 0;
        _subBlockActivityPower = 0;
    }

    /// <summary>Classifies the finished block against the learned floor, then clears the accumulators.</summary>
    private bool CompleteBlock(out double activityDb)
    {
        int levels = Math.Min(_subBlockIndex, SubBlocksPerBlock);
        activityDb = levels > 0 ? Median(_subBlockLevelsDb, levels) : double.NegativeInfinity;
        double peakDb = ToDb(_blockPeak);
        double crossingsPerSecond = _blockZeroCrossings.Max() / (_blockFill / (double)_sampleRate);

        // Classified against the floor learned from earlier blocks, then offered
        // to it: a block must not be allowed to lower its own threshold.
        bool program = IsProgram(activityDb, peakDb, crossingsPerSecond);
        OfferToFloor(activityDb);

        _blockFill = 0;
        _blockPeak = 0;
        _subBlockIndex = 0;
        _subBlockFill = 0;
        _subBlockActivityPower = 0;
        Array.Clear(_blockZeroCrossings);
        return program;
    }

    private void ObserveTailLevel(double activityDb)
    {
        // Silence occupies real time too: skipping it would leave an old falling
        // window in place indefinitely instead of allowing the trend to settle.
        if (!double.IsFinite(activityDb)) activityDb = double.NegativeInfinity;

        if (_tailLevelCount < TailTrendBlocks)
        {
            _tailLevelsDb[_tailLevelCount++] = activityDb;
            return;
        }

        Array.Copy(_tailLevelsDb, 1, _tailLevelsDb, 0, TailTrendBlocks - 1);
        _tailLevelsDb[TailTrendBlocks - 1] = activityDb;
    }

    private bool TailIsStillFading()
    {
        if (_tailLevelCount < TailTrendBlocks) return false;

        double opening = Median(_tailLevelsDb, 0, TailTrendSegmentBlocks);
        double middle = Median(_tailLevelsDb, TailTrendSegmentBlocks, TailTrendSegmentBlocks);
        double recent = Median(_tailLevelsDb, TailTrendSegmentBlocks * 2, TailTrendSegmentBlocks);
        if (!double.IsFinite(opening) || !double.IsFinite(middle) || !double.IsFinite(recent))
            return false;

        if (_quietCount >= FloorBlocks
            && recent <= _quietestDb[FloorBlocks - 1] + FadeFloorMarginDb)
        {
            return false;
        }

        return opening - middle >= MinimumFadeSegmentDropDb
               && middle - recent >= MinimumFadeSegmentDropDb;
    }

    private void ResetFadeHold()
    {
        _samplesSinceFadeEnd = 0;
        _fadeSettlementComplete = false;
        PreservedFadingTail = false;
    }

    private double SamplesToSeconds(long samples) =>
        (double)samples / _channels / _sampleRate;

    private bool IsProgram(double activityDb, double peakDb, double crossingsPerSecond) =>
        ProgramBlockClassifier.IsProgram(
            activityDb, ActivityThresholdDb(),
            peakDb, MinimumProgramPeakDb,
            crossingsPerSecond, _sampleRate);

    /// <summary>
    /// Sit 10 dB above the floor this take has shown, or at the absolute
    /// programme minimum until it has shown one. Because only blocks below that
    /// minimum are ever admitted to the floor, the threshold can never climb
    /// more than the separation above it — a transfer whose groove noise is
    /// loud earns a higher gate, and a take that never goes quiet gets the
    /// fixed one rather than a gate built out of its own music.
    /// </summary>
    private double ActivityThresholdDb() =>
        _quietCount < FloorBlocks
            ? MinimumProgramBlockDb
            : ProgramBlockClassifier.ThresholdAboveFloor(_quietestDb[FloorBlocks - 1]);

    /// <summary>
    /// Keeps the take's <see cref="FloorBlocks"/> quietest qualifying blocks, in
    /// ascending order. A block qualifies only if it is quiet enough that no
    /// programme could be that quiet — without that the lead-in would eventually
    /// scroll out of reach and the quietest passage of the music would become
    /// the floor — and loud enough to be a groove at all, so a dead input cannot
    /// latch a floor no disc can ever beat.
    /// </summary>
    private void OfferToFloor(double activityDb)
    {
        if (double.IsNaN(activityDb)) return;
        if (activityDb > MinimumProgramBlockDb) return;
        if (activityDb < MinimumMediumFloorDb) return;

        int count = _quietCount;
        if (count == FloorBlocks)
        {
            if (activityDb >= _quietestDb[FloorBlocks - 1]) return;
            count = FloorBlocks - 1;
        }
        else
        {
            _quietCount++;
        }

        int index = count;
        while (index > 0 && _quietestDb[index - 1] > activityDb)
        {
            _quietestDb[index] = _quietestDb[index - 1];
            index--;
        }
        _quietestDb[index] = activityDb;
    }

    private static double Median(double[] values, int count) => Median(values, 0, count);

    private static double Median(double[] values, int offset, int count)
    {
        Span<double> used = stackalloc double[count];
        values.AsSpan(offset, count).CopyTo(used);
        used.Sort();
        return count % 2 == 1
            ? used[count / 2]
            : Midpoint(used[count / 2 - 1], used[count / 2]);
    }

    private static double Midpoint(double a, double b) =>
        double.IsNegativeInfinity(a) && double.IsNegativeInfinity(b)
            ? double.NegativeInfinity
            : double.IsNegativeInfinity(a) || double.IsNegativeInfinity(b)
                ? Math.Min(a, b)
                : (a + b) / 2;

    private static double ToDb(double magnitude) =>
        magnitude <= 1e-12 ? double.NegativeInfinity : 20 * Math.Log10(magnitude);
}
