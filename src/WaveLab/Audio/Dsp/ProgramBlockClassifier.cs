namespace WaveLab.Audio.Dsp;

/// <summary>
/// Shared rules for deciding whether a short analysis block carries programme or
/// only the quiet around it — lead-in groove, inter-track gaps, run-out.
///
/// Vinyl "silence" is never silent, so the test is relative rather than an
/// absolute dBFS gate. Three things have to agree: the block must rise clearly
/// above the transfer's own learned floor when measured <em>above 150 Hz</em> (so
/// turntable rumble and mains hum cannot pass for music), it must reach a minimum
/// peak, and its zero-crossing rate must sit inside a musical band (so broadband
/// hiss cannot pass either).
///
/// <see cref="RecordingLevelAnalyzer"/> applies this offline over its whole block
/// history; <see cref="Audio.RunOutDetector"/> applies it live over a sliding
/// window. They differ only in their minimum-peak floor, which each passes in.
/// </summary>
internal static class ProgramBlockClassifier
{
    /// <summary>Rumble and mains hum are excluded from the activity measurement.</summary>
    public const double ActivityHighPassHz = 150;

    /// <summary>Absolute floor below which no block is programme, whatever the noise floor.</summary>
    public const double MinimumProgramBlockDb = -55;

    /// <summary>How far above the learned floor programme has to sit.</summary>
    public const double QuietProgramSeparationDb = 10;

    public const double MinimumZeroCrossingsPerSecond = 150;
    public const double MaximumZeroCrossingFraction = 0.40;

    public static bool IsProgram(
        double activityRmsDb,
        double activityThresholdDb,
        double peakDb,
        double minimumPeakDb,
        double zeroCrossingsPerSecond,
        int sampleRate) =>
        activityRmsDb > activityThresholdDb
        && peakDb > minimumPeakDb
        && zeroCrossingsPerSecond >= MinimumZeroCrossingsPerSecond
        && zeroCrossingsPerSecond <= sampleRate * MaximumZeroCrossingFraction;

    /// <summary>
    /// True when the material separates into a quiet floor and a loud programme.
    /// When it does not — the passage is all music, or all run-out — there is no
    /// low percentile that can honestly be called its noise floor.
    /// </summary>
    public static bool HasSeparableFloor(double quietPercentileDb, double programPercentileDb)
    {
        double spread = programPercentileDb - quietPercentileDb;
        return double.IsPositiveInfinity(spread)
            || double.IsFinite(spread) && spread >= QuietProgramSeparationDb;
    }

    /// <summary>
    /// Activity level a block must clear to count as programme: 10 dB above the
    /// learned floor when there is one, otherwise the absolute minimum.
    /// </summary>
    public static double ActivityThresholdDb(double quietPercentileDb, double programPercentileDb) =>
        HasSeparableFloor(quietPercentileDb, programPercentileDb)
            ? ThresholdAboveFloor(quietPercentileDb)
            : MinimumProgramBlockDb;

    /// <summary>
    /// The same gate, for a caller that has already established that what it is
    /// handing over is a floor rather than a low percentile of whatever played
    /// recently. <see cref="Audio.RunOutDetector"/> establishes it by admitting
    /// only blocks below <see cref="MinimumProgramBlockDb"/>, so the result can
    /// never rise more than the separation above that.
    /// </summary>
    public static double ThresholdAboveFloor(double floorDb) =>
        Math.Max(MinimumProgramBlockDb, floorDb + QuietProgramSeparationDb);
}
