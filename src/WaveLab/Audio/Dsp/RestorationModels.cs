namespace WaveLab.Audio.Dsp;

/// <summary>The kind of short impulsive defect found by click analysis.</summary>
public enum ImpulseDefectKind
{
    Click,
    Pop
}

/// <summary>
/// A detected impulsive defect. <see cref="EndSample"/> is exclusive and all sample
/// positions are relative to the buffers supplied to the analyser.
/// </summary>
public readonly record struct ClickEvent(
    int Channel,
    int StartSample,
    int EndSample,
    int PeakSample,
    ImpulseDefectKind Kind,
    float Confidence,
    float Severity,
    float PeakAmplitude,
    float DetectionThreshold)
{
    public int Length => Math.Max(0, EndSample - StartSample);
}

/// <summary>Controls conservative, signal-adaptive click and pop detection.</summary>
public sealed class ClickAnalysisOptions
{
    /// <summary>1 is conservative, 10 finds quieter defects. Default is 5.</summary>
    public double Sensitivity { get; init; } = 5.0;

    /// <summary>Events below this confidence are left untouched. Range 0..1.</summary>
    public double MinimumConfidence { get; init; } = 0.58;

    /// <summary>Maximum duration classified as a click.</summary>
    public double MaximumClickLengthMs { get; init; } = 0.35;

    /// <summary>Maximum duration considered repairable as an impulsive pop.</summary>
    public double MaximumPopLengthMs { get; init; } = 2.5;

    /// <summary>
    /// Protect sustained attacks whose samples do not return to the local trend after
    /// the candidate impulse. This should normally remain enabled.
    /// </summary>
    public bool PreserveTransients { get; init; } = true;
}

/// <summary>Metadata returned by automatic click/pop analysis.</summary>
public sealed record ClickAnalysisResult(
    IReadOnlyList<ClickEvent> Events,
    int SamplesAnalyzed,
    int ChannelCount,
    int SampleRate)
{
    public int ClickCount => Events.Count(e => e.Kind == ImpulseDefectKind.Click);
    public int PopCount => Events.Count(e => e.Kind == ImpulseDefectKind.Pop);
}

/// <summary>Controls interpolation and the amount of click repair applied.</summary>
public sealed class ClickRepairOptions
{
    /// <summary>Repair amount from 0 (dry) to 1 (fully repaired).</summary>
    public double Strength { get; init; } = 1.0;

    /// <summary>
    /// Maximum permitted interpolated overshoot relative to the clean local context.
    /// The default is deliberately conservative.
    /// </summary>
    public double MaximumOvershoot { get; init; } = 1.2;
}

/// <summary>Polarity of a detected clipped plateau.</summary>
public enum ClipPolarity
{
    Positive,
    Negative
}

/// <summary>
/// A clipped peak detected in one channel. <see cref="EndSample"/> is exclusive.
/// </summary>
public readonly record struct ClippedPeakEvent(
    int Channel,
    int StartSample,
    int EndSample,
    int PeakSample,
    ClipPolarity Polarity,
    float ClipLevel,
    float EstimatedTruePeak,
    float Confidence,
    float Severity)
{
    /// <summary>The absolute plateau/threshold magnitude (always non-negative).</summary>
    public float AbsoluteClipLevel => Math.Abs(ClipLevel);

    /// <summary>The signed estimated peak before clipping.</summary>
    public float SignedEstimatedTruePeak => EstimatedTruePeak;

    public int Length => Math.Max(0, EndSample - StartSample);
}

/// <summary>Controls automatic or threshold-guided clipping analysis.</summary>
public sealed class ClippingAnalysisOptions
{
    /// <summary>
    /// Optional absolute clipping threshold. Leave null for conservative automatic
    /// plateau detection. A manual threshold also finds rounded/limited peaks.
    /// </summary>
    public double? AbsoluteThreshold { get; init; }

    /// <summary>Ignore channels whose absolute peak is below this level in auto mode.</summary>
    public double MinimumPeakLevel { get; init; } = 0.1;

    /// <summary>Relative tolerance used to recognise a flat digital clipping plateau.</summary>
    public double PlateauTolerance { get; init; } = 0.0001;

    /// <summary>
    /// Minimum number of threshold samples in a clipped run. Set to 1 with a manual
    /// threshold when inspecting isolated high-frequency peaks; auto mode still requires
    /// at least two adjacent samples and flat-top evidence.
    /// </summary>
    public int MinimumConsecutiveSamples { get; init; } = 3;

    /// <summary>Bridge this many clean samples inside an otherwise clipped plateau.</summary>
    public int MaximumGapSamples { get; init; } = 1;

    /// <summary>Events below this confidence are omitted. Range 0..1.</summary>
    public double MinimumConfidence { get; init; } = 0.62;
}

/// <summary>Metadata returned by clipping analysis.</summary>
public sealed record ClippingAnalysisResult(
    IReadOnlyList<ClippedPeakEvent> Events,
    int SamplesAnalyzed,
    int ChannelCount,
    int SampleRate,
    bool UsedAutomaticThreshold);

/// <summary>Controls clipped-waveform reconstruction.</summary>
public sealed class DeclippingOptions
{
    /// <summary>Repair amount from 0 (dry) to 1 (fully reconstructed).</summary>
    public double Strength { get; init; } = 1.0;

    /// <summary>Clean samples on each shoulder used to estimate boundary slopes.</summary>
    public int PredictionSamples { get; init; } = 6;

    /// <summary>Maximum reconstructed overshoot above the detected clip level, in dB.</summary>
    public double MaximumReconstructionDb { get; init; } = 6.0;
}

/// <summary>Result of pure, non-destructive click restoration.</summary>
public sealed record ClickRestorationResult(float[][] Audio, ClickAnalysisResult Analysis);

/// <summary>Result of pure, non-destructive declipping.</summary>
public sealed record DeclippingResult(float[][] Audio, ClippingAnalysisResult Analysis);

/// <summary>Stages reported by long-running restoration analysis and repair.</summary>
public enum RestorationStage
{
    AnalyzingClicks,
    RepairingClicks,
    AnalyzingClipping,
    RepairingClipping,
    RenderingPreview
}

/// <summary>Progress snapshot suitable for a background-tool progress display.</summary>
public readonly record struct RestorationProgress(
    RestorationStage Stage,
    double Fraction,
    int Channel,
    int ChannelCount,
    int EventsProcessed = 0,
    int EventCount = 0);
