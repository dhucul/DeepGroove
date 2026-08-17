namespace WaveLab.Audio.Dsp;

/// <summary>A delivery specification: what a platform or broadcaster asks for.</summary>
/// <param name="Name">How the target is known.</param>
/// <param name="IntegratedLufs">Programme loudness asked for.</param>
/// <param name="TruePeakDbtp">Highest true peak permitted.</param>
/// <param name="ToleranceLu">How far from the target is still considered compliant.</param>
/// <param name="MaximumRangeLu">Loudness range above which the delivery is questioned, or zero for none.</param>
public readonly record struct LoudnessTarget(
    string Name, double IntegratedLufs, double TruePeakDbtp, double ToleranceLu, double MaximumRangeLu = 0)
{
    /// <summary>EBU R128: European broadcast, and the strictest about tolerance.</summary>
    public static LoudnessTarget Ebu { get; } = new("EBU R128", -23, -1, 0.5, 20);

    /// <summary>ATSC A/85: North American broadcast.</summary>
    public static LoudnessTarget Atsc { get; } = new("ATSC A/85", -24, -2, 2.0);

    /// <summary>Apple Music, which normalises to -16 and asks for a true peak of -1.</summary>
    public static LoudnessTarget Apple { get; } = new("Apple Music", -16, -1, 1.0);

    /// <summary>Spotify and YouTube both normalise to about -14.</summary>
    public static LoudnessTarget Streaming { get; } = new("Spotify / YouTube", -14, -1, 1.0);

    /// <summary>A CD master, where loudness is a choice but true peak still is not.</summary>
    public static LoudnessTarget CompactDisc { get; } = new("CD master", -12, -0.3, 6.0);

    public static IReadOnlyList<LoudnessTarget> All { get; } = [Ebu, Atsc, Apple, Streaming, CompactDisc];
}

/// <summary>How one measurement compares with what was asked for.</summary>
public readonly record struct ComplianceCheck(string Name, string Measured, string Required, bool Passed);

/// <summary>A full compliance measurement of a programme.</summary>
public readonly record struct LoudnessReport(
    LoudnessTarget Target,
    double IntegratedLufs,
    double LoudnessRangeLu,
    double TruePeakDbtp,
    double MaximumShortTermLufs,
    double MaximumMomentaryLufs,
    double SuggestedGainDb,
    IReadOnlyList<ComplianceCheck> Checks)
{
    public bool Passed
    {
        get
        {
            foreach (ComplianceCheck check in Checks) if (!check.Passed) return false;
            return Checks.Count > 0;
        }
    }
}

/// <summary>
/// Measures a programme against a delivery target and says whether it complies.
/// </summary>
/// <remarks>
/// <para>
/// The measurements themselves come from <see cref="LoudnessMeter"/>, which already implements
/// BS.1770 gating and the Tech 3342 loudness range. What is added here is the part that makes them
/// usable as a deliverable: comparison against a stated specification, and a gain that would bring
/// the programme into compliance.
/// </para>
/// <para>
/// That suggested gain is <b>true-peak limited</b>. Normalising to a loudness target by gain alone
/// will happily push the true peak past the ceiling — inter-sample peaks are already above the
/// sample peaks before anything is added — so the gain offered is the smaller of what loudness asks
/// for and what the ceiling allows. Where those disagree the report says so rather than quietly
/// applying the smaller one, because the difference is the amount of limiting the master would need,
/// and that is a decision rather than an adjustment.
/// </para>
/// </remarks>
public static class LoudnessCompliance
{
    /// <summary>Measures a document against a target.</summary>
    public static LoudnessReport Measure(IReadOnlyList<float[]> channels, int sampleRate,
        LoudnessTarget target, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var meter = new LoudnessMeter();
        meter.Configure(sampleRate, Math.Max(1, channels.Count));
        int frames = channels.Count > 0 ? channels[0].Length : 0;

        double maximumShortTerm = double.NegativeInfinity;
        double maximumMomentary = double.NegativeInfinity;

        const int block = 4_096;
        var interleaved = new float[block * Math.Max(1, channels.Count)];

        for (int start = 0; start < frames; start += block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(frames > 0 ? (double)start / frames : 1);

            int take = Math.Min(block, frames - start);
            for (int f = 0; f < take; f++)
                for (int c = 0; c < channels.Count; c++)
                    interleaved[f * channels.Count + c] = channels[c][start + f];

            meter.Process(interleaved, 0, take * channels.Count);

            // The maxima are read as the meter goes: they are the highest a moving window ever
            // reached, which cannot be recovered from a final reading.
            if (double.IsFinite(meter.ShortTermLufs)) maximumShortTerm = Math.Max(maximumShortTerm, meter.ShortTermLufs);
            if (double.IsFinite(meter.MomentaryLufs)) maximumMomentary = Math.Max(maximumMomentary, meter.MomentaryLufs);
        }

        double integrated = meter.IntegratedLufs;
        double range = meter.LoudnessRangeLu;

        // Only once the measurement is finished: flushing rings the final taps out against zeros,
        // which on a live path would read high.
        meter.FlushTruePeak();
        double truePeak = meter.TruePeakDb;

        // What loudness alone would ask for, and what the ceiling actually permits.
        double wanted = target.IntegratedLufs - integrated;
        double headroom = target.TruePeakDbtp - truePeak;
        double suggested = double.IsFinite(wanted) ? Math.Min(wanted, headroom) : 0;

        var checks = new List<ComplianceCheck>
        {
            new("Integrated loudness",
                Format(integrated, "LUFS"),
                $"{target.IntegratedLufs:0.0} ± {target.ToleranceLu:0.0} LU",
                double.IsFinite(integrated) &&
                Math.Abs(integrated - target.IntegratedLufs) <= target.ToleranceLu + 1e-9),

            new("True peak",
                Format(truePeak, "dBTP"),
                $"≤ {target.TruePeakDbtp:0.0} dBTP",
                truePeak <= target.TruePeakDbtp + 1e-9),
        };

        if (target.MaximumRangeLu > 0)
        {
            checks.Add(new ComplianceCheck("Loudness range",
                $"{range:0.0} LU",
                $"≤ {target.MaximumRangeLu:0.0} LU",
                range <= target.MaximumRangeLu + 1e-9));
        }

        return new LoudnessReport(target, integrated, range, truePeak,
            maximumShortTerm, maximumMomentary, suggested, checks);
    }

    /// <summary>
    /// The report as text, for pasting into a delivery note or saving beside the master.
    /// </summary>
    public static string Format(in LoudnessReport report)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Loudness compliance — {report.Target.Name}");
        text.AppendLine(new string('─', 44));
        text.AppendLine($"Integrated       {Format(report.IntegratedLufs, "LUFS")}");
        text.AppendLine($"Loudness range   {report.LoudnessRangeLu,6:0.0} LU");
        text.AppendLine($"True peak        {Format(report.TruePeakDbtp, "dBTP")}");
        text.AppendLine($"Max short-term   {Format(report.MaximumShortTermLufs, "LUFS")}");
        text.AppendLine($"Max momentary    {Format(report.MaximumMomentaryLufs, "LUFS")}");
        text.AppendLine();

        foreach (ComplianceCheck check in report.Checks)
            text.AppendLine($"{(check.Passed ? "PASS" : "FAIL")}  {check.Name,-18} {check.Measured,10}   needs {check.Required}");

        text.AppendLine();
        text.AppendLine(report.Passed ? "Compliant." : "Not compliant.");

        if (Math.Abs(report.SuggestedGainDb) > 0.05)
        {
            double wanted = report.Target.IntegratedLufs - report.IntegratedLufs;
            text.AppendLine($"Suggested gain   {report.SuggestedGainDb:+0.0;-0.0} dB");
            if (double.IsFinite(wanted) && wanted - report.SuggestedGainDb > 0.1)
            {
                // Said plainly rather than applied quietly: the shortfall is how much limiting the
                // master would need, and that is a decision.
                text.AppendLine(
                    $"                 loudness alone asks for {wanted:+0.0;-0.0} dB, but the true-peak " +
                    $"ceiling allows only {report.SuggestedGainDb:+0.0;-0.0}.");
                text.AppendLine(
                    $"                 Reaching the target would need {wanted - report.SuggestedGainDb:0.0} dB of limiting.");
            }
        }

        return text.ToString();
    }

    private static string Format(double value, string unit) =>
        double.IsFinite(value) ? $"{value,6:0.0} {unit}" : $"{"—",6} {unit}";
}
