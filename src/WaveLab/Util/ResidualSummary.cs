namespace WaveLab.Util;

/// <summary>
/// The account the user gets of what a restoration pass took out, and the monitor gain that
/// makes it audible. Both surfaces that can capture removed material — the restoration
/// workbench and the Restore menu tools — report through here, so the two cannot end up
/// describing the same thing differently.
/// </summary>
/// <remarks>
/// Pure by design, for the reason <c>DescribeChoices</c> and <c>DescribeNoiseDepth</c> are:
/// wording that needs a window to test is wording that goes untested.
/// </remarks>
public static class ResidualSummary
{
    /// <summary>
    /// Below this peak nothing was really removed and no tab is worth opening. It is a
    /// float epsilon rather than zero because a dry/wet blend and an interpolation can leave
    /// a few last-bit differences where the repair decided to do nothing.
    /// </summary>
    public const float SilenceThreshold = 1e-7f;

    /// <summary>Where the body of a residual is placed for monitoring.</summary>
    public const double MonitorRmsTargetDbfs = -24.0;

    /// <summary>The highest the lift may put the residual's loudest moment.</summary>
    public const double MonitorPeakCeilingDbfs = -1.0;

    /// <summary>The most a residual is lifted. Past this the noise floor of the lift itself dominates.</summary>
    public const double MaximumMonitorGainDb = 60.0;

    /// <summary>
    /// The largest residual worth keeping in memory, matching the ceiling the clipboard already
    /// sets for the same reason.
    /// </summary>
    /// <remarks>
    /// A residual is a whole extra copy of the range, on top of the source, the tool's working
    /// copy, the audio that was spliced and the undo entry it displaced. Stating the cost in the
    /// caption is not the same as declining an impossible one: without a ceiling a three-hour
    /// 96 kHz transfer throws <see cref="OutOfMemoryException"/> out of a background task after the
    /// repair has already committed.
    /// </remarks>
    public const long MaximumResidualBytes = 512L * 1024 * 1024;

    /// <summary>Whether a range of this size is past what may be kept.</summary>
    public static bool ExceedsBudget(long sampleCount, int channelCount) =>
        sampleCount > 0 && channelCount > 0 &&
        sampleCount * (long)channelCount * sizeof(float) > MaximumResidualBytes;

    /// <summary>
    /// The playback-only lift for a residual: enough to bring its body up to −24 dBFS, held back
    /// so its loudest moment still lands under full scale, never a cut, and never more than 60 dB.
    /// </summary>
    /// <remarks>
    /// <b>Both anchors are needed and measured, and either alone gets a real case wrong.</b> Peak
    /// alone under-lifts the residual the feature exists for: a spectral gate leaves transient
    /// artefacts at the programme's own peaks, so a hiss residual whose body sits 40 dB down still
    /// peaks near the programme and a peak rule offers it about 5 dB — measured, not supposed. RMS
    /// alone clips the other case: what click repair removes is a click planted above the local
    /// level, which comes out <i>louder than the programme it came out of</i> — a real declick
    /// residual measured here peaks above full scale while the music sits at −7.8 dBFS, and an RMS
    /// rule would lift it further. Taking the smaller of the two lifts hiss properly and leaves
    /// clicks alone.
    /// </remarks>
    public static float MonitorGainFor(float peak, float rms)
    {
        if (!float.IsFinite(peak) || peak <= SilenceThreshold) return 1f;
        double headroom = Math.Pow(10, MonitorPeakCeilingDbfs / 20.0) / peak;
        double body = float.IsFinite(rms) && rms > SilenceThreshold
            ? Math.Pow(10, MonitorRmsTargetDbfs / 20.0) / rms
            : headroom;
        double gain = Math.Min(body, headroom);
        double ceiling = Math.Pow(10, MaximumMonitorGainDb / 20.0);
        return (float)Math.Clamp(gain, 1.0, ceiling);
    }

    /// <summary>Decibels for a linear gain, for the readouts. 0 dB for unity or anything smaller.</summary>
    public static double GainToDb(float gain) =>
        !float.IsFinite(gain) || gain <= 1f ? 0 : 20 * Math.Log10(gain);

    /// <summary>Full-scale decibels for a peak magnitude; negative infinity reads as silence.</summary>
    public static string PeakText(float peak) =>
        !float.IsFinite(peak) || peak <= 0 ? "silent" : $"{20 * Math.Log10(peak):0.0} dBFS";

    /// <summary>
    /// The status line after a pass that kept what it removed. The peak is the point of the
    /// sentence: it is the one number that says how much was taken, and it is the number the
    /// monitor gain is derived from, so the two are reported together or neither means anything.
    /// </summary>
    public static string Describe(string title, float peak, float monitorGain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        double db = GainToDb(monitorGain);
        string monitoring = db < 0.05
            ? "no lift needed"
            : $"monitoring at +{db:0} dB";
        return $"{title} · peak {PeakText(peak)} · {monitoring}.";
    }

    /// <summary>What to say when the tool ran and took nothing out.</summary>
    public static string DescribeNothingRemoved(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return $"{toolName} removed nothing audible · no separate file was made.";
    }

    /// <summary>
    /// The caption under the option, stating what keeping the removed material will cost in
    /// memory for the range in front of the user. A restoration range is a whole record side
    /// more often than not, so the figure is worth showing before the box is ticked rather
    /// than after.
    /// </summary>
    public static string DescribeCost(long sampleCount, int channelCount)
    {
        if (sampleCount <= 0 || channelCount <= 0)
            return "Opens what was removed in a second tab so you can hear it.";
        string size = SizeText(sampleCount * (double)channelCount * sizeof(float));
        if (ExceedsBudget(sampleCount, channelCount))
        {
            return $"This range would need about {size} of memory, past the " +
                   $"{SizeText(MaximumResidualBytes)} limit — select less of it to keep what was removed.";
        }
        return $"Opens what was removed in a second tab so you can hear it. About {size} of memory.";
    }

    /// <summary>What to say when a range turned out to be past the ceiling after all.</summary>
    public static string DescribeTooLarge(string toolName, long sampleCount, int channelCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return $"{toolName} applied · what it removed needed about " +
               $"{SizeText(sampleCount * (double)channelCount * sizeof(float))} and was not kept.";
    }

    private static string SizeText(double bytes)
    {
        double megabytes = bytes / (1024 * 1024);
        return megabytes >= 1024
            ? $"{megabytes / 1024:0.0} GB"
            : $"{Math.Max(1, Math.Round(megabytes)):0} MB";
    }
}
