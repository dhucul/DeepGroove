using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>
/// Finds the end of a side: the point where programme content stops and only
/// the run-out groove — surface noise, rumble and the odd click — is left.
///
/// The decision is deliberately relative rather than a fixed dBFS gate. Vinyl
/// "silence" is never silent, so the detector learns this transfer's own noise
/// floor from a sliding window and calls a block programme only when it rises
/// clearly above that floor. The measurement mirrors
/// <see cref="Dsp.RecordingLevelAnalyzer"/>: a 150 Hz high-passed RMS (so
/// turntable rumble and mains hum cannot pass for music), a zero-crossing sanity
/// range (so hiss cannot either), and the same 10 dB quiet-to-programme
/// separation. The block level is the median of its ten sub-blocks, so a single
/// click cannot lift a run-out block into looking like music.
///
/// Nothing triggers until programme has been heard at least once, so arming the
/// recorder before the stylus is down can never end the take on its own.
/// </summary>
internal sealed class RunOutDetector
{
    private const double SubBlockSeconds = 0.01;
    private const int SubBlocksPerBlock = 10;
    private const double BlockSeconds = SubBlockSeconds * SubBlocksPerBlock;
    private const double ActivityHighPassHz = 150;
    private const double MinimumProgramBlockDb = -55;
    private const double MinimumProgramPeakDb = -60;
    private const double QuietProgramSeparationDb = 10;
    private const double MinimumZeroCrossingsPerSecond = 150;
    private const double MaximumZeroCrossingFraction = 0.40;

    /// <summary>The floor is read as a low percentile of the recent window, as the offline analyzer does.</summary>
    private const double NoiseFloorPercentile = 0.10;
    private const double ProgramPercentile = 0.90;
    private const double MinimumWindowSeconds = 30;

    /// <summary>The window must outlast the hold, or the run-out eventually becomes the whole window.</summary>
    private const double WindowHoldMultiple = 2.5;

    public const double MinimumHoldSeconds = 5;
    public const double MaximumHoldSeconds = 60;
    public const double DefaultHoldSeconds = 12;

    /// <summary>Audio kept after the last programme block, so a fade tail survives the trim.</summary>
    public const double KeepAfterProgramSeconds = 2;

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
    private readonly double[] _window;
    private readonly double[] _windowScratch;

    private int _windowCount;
    private int _windowNext;
    private int _subBlockFill;
    private int _subBlockIndex;
    private double _subBlockActivityPower;
    private int _blockFill;
    private double _blockPeak;

    private long _samplesSinceProgram;
    private long _pendingBlockSamples;

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

        int windowBlocks = (int)Math.Ceiling(
            Math.Max(MinimumWindowSeconds, _holdSeconds * WindowHoldMultiple) / BlockSeconds);
        _window = new double[windowBlocks];
        _windowScratch = new double[windowBlocks];
    }

    /// <summary>True once programme content has been heard at least once.</summary>
    public bool HasHeardProgram { get; private set; }

    /// <summary>True once the run-out hold has elapsed; latched.</summary>
    public bool IsTriggered { get; private set; }

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
            return Math.Max(0, _samplesSinceProgram - keep);
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

            if (CompleteBlock())
            {
                // The block just ended, so everything up to its end is
                // programme; anything read past it starts the next hold.
                _samplesSinceProgram = 0;
                _pendingBlockSamples = 0;
                HasHeardProgram = true;
            }
            else
            {
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

    /// <summary>Classifies the finished block against the sliding window, then clears the accumulators.</summary>
    private bool CompleteBlock()
    {
        int levels = Math.Min(_subBlockIndex, SubBlocksPerBlock);
        double activityDb = levels > 0 ? Median(_subBlockLevelsDb, levels) : double.NegativeInfinity;
        double peakDb = ToDb(_blockPeak);
        double crossingsPerSecond = _blockZeroCrossings.Max() / (_blockFill / (double)_sampleRate);

        bool program = IsProgram(activityDb, peakDb, crossingsPerSecond);
        PushWindow(activityDb);

        _blockFill = 0;
        _blockPeak = 0;
        _subBlockIndex = 0;
        _subBlockFill = 0;
        _subBlockActivityPower = 0;
        Array.Clear(_blockZeroCrossings);
        return program;
    }

    private bool IsProgram(double activityDb, double peakDb, double crossingsPerSecond) =>
        activityDb > ActivityThresholdDb()
        && peakDb > MinimumProgramPeakDb
        && crossingsPerSecond >= MinimumZeroCrossingsPerSecond
        && crossingsPerSecond <= _sampleRate * MaximumZeroCrossingFraction;

    /// <summary>
    /// Mirrors the offline classifier: when the recent window separates into a
    /// quiet floor and a loud programme, sit 10 dB above the floor. When it does
    /// not — the window is all music, or all run-out — there is no honest floor
    /// to measure, so fall back to the absolute programme minimum.
    /// </summary>
    private double ActivityThresholdDb()
    {
        if (_windowCount == 0) return MinimumProgramBlockDb;

        Array.Copy(_window, _windowScratch, _windowCount);
        Array.Sort(_windowScratch, 0, _windowCount);
        double low = Percentile(_windowScratch, _windowCount, NoiseFloorPercentile);
        double high = Percentile(_windowScratch, _windowCount, ProgramPercentile);
        double spread = high - low;

        if (double.IsPositiveInfinity(spread)
            || (double.IsFinite(spread) && spread >= QuietProgramSeparationDb))
        {
            return Math.Max(MinimumProgramBlockDb, low + QuietProgramSeparationDb);
        }
        return MinimumProgramBlockDb;
    }

    private void PushWindow(double activityDb)
    {
        _window[_windowNext] = activityDb;
        _windowNext = (_windowNext + 1) % _window.Length;
        if (_windowCount < _window.Length) _windowCount++;
    }

    private static double Percentile(double[] sorted, int count, double fraction)
    {
        if (count == 1) return sorted[0];
        double position = fraction * (count - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, count - 1);
        double weight = position - lower;
        double a = sorted[lower];
        double b = sorted[upper];
        if (double.IsNegativeInfinity(a) && double.IsNegativeInfinity(b)) return double.NegativeInfinity;
        if (double.IsNegativeInfinity(a)) return weight >= 1 ? b : double.NegativeInfinity;
        return a + (b - a) * weight;
    }

    private static double Median(double[] values, int count)
    {
        Span<double> copy = stackalloc double[SubBlocksPerBlock];
        values.AsSpan(0, count).CopyTo(copy);
        Span<double> used = copy[..count];
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
