using System.Text;

namespace WaveLab.Audio.Dsp;

/// <summary>What a set of tracks is being levelled against.</summary>
public enum LoudnessMatchMode
{
    /// <summary>A stated delivery target, with that target's own true-peak ceiling.</summary>
    Target,
    /// <summary>The quietest track in the selection, so nothing is ever boosted.</summary>
    Quietest,
    /// <summary>The average of the selection, which moves the loud down and the quiet up.</summary>
    Average,
    /// <summary>One track chosen as the reference; it is left exactly as it is.</summary>
    Reference,
}

/// <summary>What one track measured, independent of any target.</summary>
/// <param name="Name">How the track is known — its tab title.</param>
/// <param name="IntegratedLufs">BS.1770 gated programme loudness, or negative infinity when nothing passed the gate.</param>
/// <param name="TruePeakDbtp">The 4× oversampled true peak.</param>
/// <param name="LoudnessRangeLu">EBU Tech 3342 loudness range.</param>
/// <param name="SampleRate">The rate it was measured at.</param>
/// <param name="Frames">How long it is, in frames.</param>
public readonly record struct LoudnessMeasurement(
    string Name,
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRangeLu,
    int SampleRate,
    int Frames);

/// <summary>What will be done to one track, and why.</summary>
/// <param name="Measurement">What it measured.</param>
/// <param name="CanApply">Whether there is a gain worth applying.</param>
/// <param name="RequestedGainDb">What loudness alone asks for.</param>
/// <param name="GainDb">What the true-peak ceiling permits — the gain that is actually applied.</param>
/// <param name="ShortfallDb">How far short of the request that lands. Never negative.</param>
/// <param name="ResultingLufs">Where the track ends up.</param>
/// <param name="ResultingTruePeakDbtp">Where its true peak ends up.</param>
/// <param name="Note">One phrase saying what happened to this track.</param>
public readonly record struct LoudnessMatchStep(
    LoudnessMeasurement Measurement,
    bool CanApply,
    double RequestedGainDb,
    double GainDb,
    double ShortfallDb,
    double ResultingLufs,
    double ResultingTruePeakDbtp,
    string Note);

/// <summary>A complete decision about a set of tracks: what each one gets, and what it was for.</summary>
public readonly record struct LoudnessMatchPlan(
    LoudnessMatchMode Mode,
    LoudnessTarget Target,
    double TargetLufs,
    double CeilingDbtp,
    IReadOnlyList<LoudnessMatchStep> Steps,
    string Summary);

/// <summary>
/// Brings several tracks to a common programme loudness by gain alone.
/// </summary>
/// <remarks>
/// <para>
/// The measurement is <see cref="LoudnessCompliance.Measure"/> — the same meter, the same gating,
/// the same true peak. Nothing is re-derived here; what is added is the arithmetic that turns a set
/// of measurements into a set of gains, which is the part worth testing without a window in front
/// of it.
/// </para>
/// <para>
/// <b>Gain is all this applies.</b> Where loudness asks for more than the true-peak ceiling allows,
/// the gain is cut back to the ceiling and the difference is reported as the amount of limiting the
/// master would need. That is the rule <see cref="LoudnessCompliance"/> already states for one file,
/// and the reason it matters across a set is that the batch converter's LUFS normalisation has never
/// applied it: that path will push a track past 0 dBTP and say nothing.
/// </para>
/// <para>
/// The relative modes average in the loudness domain — the arithmetic mean of the LUFS figures, not
/// of their power. LUFS is already a perceptual scale; a power mean is dominated by the loudest
/// track and lands several LU above where a listener puts the average of a record.
/// </para>
/// </remarks>
public static class LoudnessMatch
{
    /// <summary>
    /// The ceiling the relative modes work to. They are not delivering to a specification, so there
    /// is no stated ceiling to take; −1 dBTP is what the rest of the app reserves.
    /// </summary>
    public const double RelativeCeilingDbtp = -1.0;

    /// <summary>Below this there is nothing worth writing an undo entry for.</summary>
    public const double NegligibleGainDb = 0.05;

    /// <summary>Measures one track. A thin pass through the compliance meter.</summary>
    /// <remarks>
    /// <paramref name="target"/> only reaches the compliance checks, which are discarded here: the
    /// three numbers kept are properties of the audio, not of what it is being matched to, which is
    /// what lets the mode and target be changed afterwards without measuring again.
    /// </remarks>
    public static LoudnessMeasurement Measure(
        string name,
        IReadOnlyList<float[]> channels,
        int sampleRate,
        LoudnessTarget target,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var report = LoudnessCompliance.Measure(channels, sampleRate, target, cancellationToken, progress);
        return new LoudnessMeasurement(
            name,
            report.IntegratedLufs,
            report.TruePeakDbtp,
            report.LoudnessRangeLu,
            sampleRate,
            channels.Count > 0 ? channels[0].Length : 0);
    }

    /// <summary>Turns a set of measurements into a set of gains.</summary>
    /// <remarks>
    /// Pure, total, and never throws for a state the user can select: an empty selection, a
    /// selection of nothing but silence, and a reference index pointing at a track that measured
    /// nothing all return a plan that changes nothing and says why.
    /// </remarks>
    public static LoudnessMatchPlan Plan(
        IReadOnlyList<LoudnessMeasurement> measurements,
        LoudnessMatchMode mode,
        LoudnessTarget target,
        int referenceIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        double ceiling = mode == LoudnessMatchMode.Target ? target.TruePeakDbtp : RelativeCeilingDbtp;
        double targetLufs = ReferenceLevel(measurements, mode, target, referenceIndex, out string? blocked);

        var steps = new LoudnessMatchStep[measurements.Count];
        int changed = 0, limited = 0, silent = 0;

        for (int i = 0; i < measurements.Count; i++)
        {
            var m = measurements[i];

            if (!double.IsFinite(m.IntegratedLufs))
            {
                silent++;
                steps[i] = Unchanged(m, "silent below the −70 LUFS gate — nothing to match");
                continue;
            }

            if (blocked != null)
            {
                steps[i] = Unchanged(m, blocked);
                continue;
            }

            if (mode == LoudnessMatchMode.Reference && i == referenceIndex)
            {
                steps[i] = Unchanged(m, "reference");
                continue;
            }

            double requested = targetLufs - m.IntegratedLufs;
            double allowed = ceiling - m.TruePeakDbtp;
            double gain = Math.Min(requested, allowed);
            double shortfall = Math.Max(0, requested - gain);
            bool canApply = Math.Abs(gain) > NegligibleGainDb;

            string note;
            if (shortfall > NegligibleGainDb)
            {
                limited++;
                note = allowed < 0 && requested > 0
                    // Loudness asks for a boost and the track still gets cut. Saying "true-peak
                    // limited" alone would read as "left alone", which is the opposite of what
                    // happens.
                    ? $"already {-allowed:0.0} dB over the ceiling — brought down instead"
                    : $"true-peak limited · {shortfall:0.0} dB of limiting would be needed to reach {targetLufs:0.0} LUFS";
            }
            else if (!canApply)
            {
                note = "already there";
            }
            else
            {
                note = "—";
            }

            if (canApply) changed++;

            steps[i] = new LoudnessMatchStep(
                m, canApply, requested, gain, shortfall,
                m.IntegratedLufs + gain, m.TruePeakDbtp + gain, note);
        }

        return new LoudnessMatchPlan(mode, target, targetLufs, ceiling, steps,
            Summarize(mode, target, measurements, targetLufs, ceiling, changed, limited, silent, blocked));
    }

    private static LoudnessMatchStep Unchanged(in LoudnessMeasurement m, string note) =>
        new(m, false, 0, 0, 0, m.IntegratedLufs, m.TruePeakDbtp, note);

    private static double ReferenceLevel(
        IReadOnlyList<LoudnessMeasurement> measurements,
        LoudnessMatchMode mode,
        LoudnessTarget target,
        int referenceIndex,
        out string? blocked)
    {
        blocked = null;
        if (mode == LoudnessMatchMode.Target) return target.IntegratedLufs;

        if (mode == LoudnessMatchMode.Reference)
        {
            if (referenceIndex < 0 || referenceIndex >= measurements.Count)
            {
                blocked = "no reference track chosen";
                return double.NaN;
            }
            double level = measurements[referenceIndex].IntegratedLufs;
            if (!double.IsFinite(level))
            {
                blocked = "the reference track measured no loudness";
                return double.NaN;
            }
            return level;
        }

        double total = 0, quietest = double.PositiveInfinity;
        int count = 0;
        foreach (var m in measurements)
        {
            if (!double.IsFinite(m.IntegratedLufs)) continue;
            total += m.IntegratedLufs;
            quietest = Math.Min(quietest, m.IntegratedLufs);
            count++;
        }

        if (count == 0)
        {
            blocked = "nothing in the selection measured a loudness";
            return double.NaN;
        }

        return mode == LoudnessMatchMode.Quietest ? quietest : total / count;
    }

    private static string Summarize(
        LoudnessMatchMode mode, LoudnessTarget target, IReadOnlyList<LoudnessMeasurement> measurements,
        double targetLufs, double ceiling, int changed, int limited, int silent, string? blocked)
    {
        if (measurements.Count == 0) return "Match Loudness · nothing selected.";
        if (blocked != null) return $"Match Loudness · {blocked} · nothing would be applied.";

        string against = mode switch
        {
            LoudnessMatchMode.Target => target.Name,
            LoudnessMatchMode.Quietest => "the quietest track",
            LoudnessMatchMode.Average => $"the average of {measurements.Count} track(s)",
            _ => "the reference track",
        };

        var text = new StringBuilder();
        text.Append($"Match Loudness · {measurements.Count} track(s) to {targetLufs:0.0} LUFS ")
            .Append($"({against}), ceiling {ceiling:0.0} dBTP · {changed} changed");
        if (limited > 0) text.Append($", {limited} true-peak limited");
        if (silent > 0) text.Append($", {silent} silent");
        text.Append('.');
        return text.ToString();
    }

    /// <summary>Renders the plan as a report, in the shape <see cref="LoudnessCompliance"/> uses.</summary>
    public static string Format(in LoudnessMatchPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine($"Match loudness — {plan.Summary}");
        text.AppendLine(new string('─', 96));
        text.AppendLine($"{"Track",-32}{"Measured",12}{"True peak",12}{"Gain",10}{"Result",12}  Note");
        text.AppendLine(new string('─', 96));

        foreach (var step in plan.Steps)
        {
            text.AppendLine(
                $"{Trim(step.Measurement.Name, 31),-32}"
                + $"{Level(step.Measurement.IntegratedLufs, "LUFS"),12}"
                + $"{Level(step.Measurement.TruePeakDbtp, "dBTP"),12}"
                + $"{(step.CanApply ? $"{step.GainDb:+0.0;-0.0}" : "—"),10}"
                + $"{(step.CanApply ? Level(step.ResultingLufs, "LUFS") : "—"),12}"
                + $"  {step.Note}");
        }

        text.AppendLine(new string('─', 96));
        foreach (var step in plan.Steps)
        {
            if (step.ShortfallDb <= NegligibleGainDb) continue;
            text.AppendLine();
            text.AppendLine(
                $"{step.Measurement.Name}: loudness alone asks for {step.RequestedGainDb:+0.0;-0.0} dB, but the "
                + $"{plan.CeilingDbtp:0.0} dBTP ceiling allows only {step.GainDb:+0.0;-0.0} dB. Reaching "
                + $"{plan.TargetLufs:0.0} LUFS would need {step.ShortfallDb:0.0} dB of limiting, which is a "
                + "decision rather than an adjustment and is not applied here.");
        }

        return text.ToString();
    }

    private static string Trim(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    private static string Level(double value, string unit) =>
        double.IsFinite(value) ? $"{value:0.0} {unit}" : "—";

    /// <summary>
    /// Words a true-peak shortfall as the decision it is, for a prompt with someone in front of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure, so the wording is unit-tested without a window — the arrangement
    /// <c>DescribeDeclipChoices</c>, <c>DescribeNoiseDepth</c> and <c>DescribeOutputMix</c> all use.
    /// </para>
    /// <para>
    /// It leads with the number the wording it replaced never printed: <b>where the file actually
    /// ends up</b>. "Loudness alone asks for +9.8 dB but the ceiling allows only +5.2" states the
    /// arithmetic behind the decision and leaves the reader to do the subtraction that matters,
    /// which is the one thing they would act on.
    /// </para>
    /// <para>
    /// Both costs of the limiter route are stated rather than discovered. It lands a little under
    /// the target, because limiting removes energy as well as peaks; and the full gain leaves the
    /// document above full scale until the rack is rendered, which is fine in 32-bit float and is
    /// hard clipping the moment it is saved at 16 or 24 bits.
    /// </para>
    /// </remarks>
    public static CeilingChoice DescribeCeilingChoice(in LoudnessMatchPlan plan, in LoudnessMatchStep step) => new(
        $"This file can only reach {step.ResultingLufs:0.0} LUFS, not the {plan.TargetLufs:0.0} you asked for. "
        + $"Going louder would push the true peak past {plan.CeilingDbtp:0.0} dBTP."
        + Environment.NewLine + Environment.NewLine
        + $"Closing the last {step.ShortfallDb:0.0} dB needs limiting. A limiter takes the full "
        + $"{step.RequestedGainDb:+0.0;-0.0} dB and holds the ceiling, landing a little under "
        + $"{plan.TargetLufs:0.0} because it removes energy as well as peaks — and the file sits above "
        + "full scale until you render the rack.",
        $"Apply {step.GainDb:+0.0;-0.0} dB · stop at {step.ResultingLufs:0.0} LUFS",
        $"Apply {step.RequestedGainDb:+0.0;-0.0} dB and add a limiter",
        "Cancel");
}

/// <summary>What the true-peak ceiling did to a step, worded for the person deciding what to do.</summary>
/// <param name="Message">Leads with where the file lands, because that is what is being decided.</param>
/// <param name="StopShortLabel">Take the gain the ceiling allows and stop under the target.</param>
/// <param name="AddLimiterLabel">Take the full gain and let a limiter hold the ceiling.</param>
/// <param name="CancelLabel">Apply nothing.</param>
public readonly record struct CeilingChoice(
    string Message,
    string StopShortLabel,
    string AddLimiterLabel,
    string CancelLabel)
{
    /// <summary>The three labels in the order they are offered, safest first.</summary>
    public string[] Labels => [StopShortLabel, AddLimiterLabel, CancelLabel];
}
