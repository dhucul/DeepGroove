namespace WaveLab.Audio.Dsp;

/// <summary>The kind of short impulsive defect found by click analysis.</summary>
public enum ImpulseDefectKind
{
    Click,
    Pop
}

/// <summary>
/// A detected impulsive defect. <see cref="EndSample"/> is exclusive and all sample
/// positions are relative to the buffers supplied to the analyser. <see cref="SampleRate"/>
/// travels with the event so later repair guards remain the same duration at every source rate.
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
    float DetectionThreshold,
    int SampleRate = 0)
{
    public int Length => Math.Max(0, EndSample - StartSample);
}

/// <summary>Controls conservative, signal-adaptive click and pop detection.</summary>
public sealed class ClickAnalysisOptions
{
    /// <summary>1 is conservative, 10 finds quieter defects. Default is 5.</summary>
    public double Sensitivity { get; init; } = 5.0;

    /// <summary>Events below this confidence are left untouched. Range 0..1.</summary>
    /// <remarks>
    /// <b>Raised from 0.58 with <see cref="TrendRelativeRecovery"/>, which is what made it work.</b>
    /// Before that change this gate did nothing measurable — dropping it from 0.60 to 0.30 moved
    /// recall by not one point at any severity, because the recovery term feeding it was near zero
    /// for every quiet event whatever it did. With recovery measured against the trend the score
    /// separates events again, and the floor became the effective control over false alarms:
    /// swept, it trades false alarms on clean material against recall at the quiet end. It sits at
    /// 0.65 rather than higher because <b>0.70 loses pop detection outright</b> - the two synthetic
    /// pop tests fail there and pass at 0.68, and a threshold one hundredth from a cliff is not a
    /// threshold. 0.65 keeps a margin.
    /// </remarks>
    public double MinimumConfidence { get; init; } = 0.65;

    /// <summary>Maximum duration classified as a click.</summary>
    public double MaximumClickLengthMs { get; init; } = 0.35;

    /// <summary>Maximum duration considered repairable as an impulsive pop.</summary>
    public double MaximumPopLengthMs { get; init; } = 5.0;

    /// <summary>
    /// Protect sustained attacks whose samples do not return to the local trend after
    /// the candidate impulse. This should normally remain enabled.
    /// </summary>
    public bool PreserveTransients { get; init; } = true;

    /// <summary>
    /// Judge an event's high-frequency content against its own surroundings rather than on its own.
    /// </summary>
    /// <remarks>
    /// The absolute form asks "is there a lot of fast movement here", which is a property of the
    /// <i>material</i>: a chime, a cymbal or a sibilant answers yes everywhere, click or no click.
    /// Measured on undamaged recordings that scored 0.05 events a second on classical but 3.7 on
    /// speech and 45.9 on one Windows alarm, all of them false. The contrast form asks whether this
    /// event is faster-moving than the audio either side of it, which is what actually distinguishes
    /// a surface defect from an instrument. Set false for the old behaviour.
    /// </remarks>
    public bool LocalHighFrequencyContrast { get; init; } = true;

    /// <summary>
    /// Also nominate candidates from the prediction residual of a high-order model, not only from
    /// the waveform's curvature.
    /// </summary>
    /// <remarks>
    /// Curvature is the error of a one-sample linear predictor, and music has plenty of it, so a
    /// quiet click does not stand out: measured, recall was 39% for clicks 6 dB above the local
    /// level, and the limit was the curvature gate rather than any of the confidence tests —
    /// dropping the confidence floor from 0.60 to 0.30 changed the recall by nothing at all, at any
    /// severity. A high-order residual is the standard answer and the one <see cref="Decrackle"/>
    /// already uses: the music is largely predicted away and what is left is mostly defect.
    /// Candidates from here face the same acceptance tests as any other.
    /// </remarks>
    public bool PredictiveDetection { get; init; } = true;

    /// <summary>Order of that predictor. Higher follows the music more closely and costs more.</summary>
    public int PredictorOrder { get; init; } = 24;

    /// <summary>
    /// How many robust deviations of the residual a sample must exceed to be nominated, at the
    /// strict end of the sensitivity slider. Four fewer at the permissive end.
    /// </summary>
    /// <remarks>
    /// <b>This is a false-alarm control, not a recall control</b>, which is the opposite of what a
    /// threshold usually is and worth knowing before turning it. Swept from 8 to 14, recall at 6 dB
    /// above the local level moved from 52% to 51% and the repair gain not at all — but false
    /// detections on clean digital material fell by two thirds, 0.22 events a second to 0.06 on
    /// classical. What limits recall is further down, in the acceptance tests, not here.
    /// </remarks>
    public double PredictiveSigma { get; init; } = 12.0;

    /// <summary>
    /// Judge the return to baseline against what the audio was already doing, rather than against
    /// the size of the candidate.
    /// </summary>
    /// <remarks>
    /// The test exists to separate a defect, which the waveform returns from, from a musical attack,
    /// which it does not. The original form divided the post-event deviation by the candidate's own
    /// amplitude — fine for a loud click, where the music around it is negligible, and wrong for a
    /// quiet one, where the music <i>is</i> the deviation and the ratio approaches one however
    /// cleanly the waveform recovered. Measured, that gate rejected <b>14,053 of 36,685 candidates
    /// at 6 dB above the local level against 770 at 24 dB</b>: it was the single reason quiet clicks
    /// were missed. Comparing the audio after the event against the audio before it asks the
    /// intended question and does not depend on how loud the event was.
    /// </remarks>
    public bool TrendRelativeRecovery { get; init; } = true;

    /// <summary>
    /// How completely the waveform must return to what it was doing before the candidate. Raised
    /// with <see cref="TrendRelativeRecovery"/>, because that changed what the score means.
    /// </summary>
    /// <remarks>
    /// The old score divided by the candidate's own amplitude, so a quiet click scored near zero
    /// whatever it did and 0.2 was as much as could be asked. Measured against the trend instead,
    /// the same clean recovery scores high, and the threshold has to rise or the gate stops
    /// separating anything: it was suppressing false alarms as a side effect of being too strict on
    /// quiet events, and removing that strictness without replacing it doubled them.
    /// </remarks>
    public double MinimumRecovery { get; init; } = 0.55;
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
    /// Treat coincident defects as one multichannel event. When enabled, a click found
    /// in any channel repairs the same sample span in every channel, using each channel's
    /// own clean context for reconstruction. Disable for unrelated multichannel stems.
    /// </summary>
    public bool LinkChannels { get; init; } = true;

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
    public double MinimumConfidence { get; init; } = 0.65;
}

/// <summary>Metadata returned by clipping analysis.</summary>
public sealed record ClippingAnalysisResult(
    IReadOnlyList<ClippedPeakEvent> Events,
    int SamplesAnalyzed,
    int ChannelCount,
    int SampleRate,
    bool UsedAutomaticThreshold);

/// <summary>Which reconstruction a declipping pass uses.</summary>
public enum DeclipMethod
{
    /// <summary>
    /// Choose per channel from how much of it is clipped and how long the plateaus are. Neither
    /// method dominates; see <see cref="DeclipMethodChooser"/> for what was measured.
    /// </summary>
    Automatic,

    /// <summary>
    /// An arch drawn between the shoulders either side of each flattened peak. Degrades gracefully
    /// when there is little left to infer from, which is why it wins on heavily crushed material.
    /// </summary>
    PeakReconstruction,

    /// <summary>
    /// A-SPADE sparse declipping (<see cref="Spade"/>). Reconstructs the waveform rather than
    /// drawing over it, and is far better while enough reliable samples survive to fit a model to.
    /// </summary>
    Sparse,
}

/// <summary>
/// What a declipping pass decided for one channel, and the two measurements it decided from.
/// </summary>
/// <remarks>
/// A-SPADE costs roughly 700× the peak reconstruction, so a side that takes minutes where the last
/// one took seconds needs an explanation that is checkable rather than magic. Reporting the numbers
/// the choice was made from is that explanation.
/// </remarks>
public readonly record struct DeclipChannelChoice(
    int Channel,
    DeclipMethod Method,
    double ClippedFraction,
    double MeanRunSamples);

/// <summary>Controls clipped-waveform reconstruction.</summary>
public sealed class DeclippingOptions
{
    /// <summary>Repair amount from 0 (dry) to 1 (fully reconstructed).</summary>
    public double Strength { get; init; } = 1.0;

    /// <summary>Which reconstruction to use. Automatic decides per channel from the audio.</summary>
    public DeclipMethod Method { get; init; } = DeclipMethod.Automatic;

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
