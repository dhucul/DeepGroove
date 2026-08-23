using WaveLab.Audio.Effects;

namespace WaveLab.Audio.Dsp;

/// <summary>
/// Converts non-destructive whole-file measurements into conservative workbench
/// controls. The recommendation is deliberately separate from rendering so analysis
/// can never alter or play the source audio.
/// </summary>
internal static class RestorationRecommendations
{
    internal const double ExploratoryClickSensitivity = 7.0;

    internal sealed record Settings(
        bool RepairClicks,
        double ClickSensitivity,
        double ClickStrength,
        bool Declip,
        double DeclipStrength,
        double DeclipHeadroomDb,
        bool ReduceNoise,
        double NoiseReductionDb,
        double NoiseSensitivityDb,
        bool RemoveHum,
        double HumAmount,
        double HumFrequency,
        int HumHarmonics,
        double HumQ,
        bool HighPass,
        double HighPassCutoffHz,
        double SideLevel,
        bool Decrackle,
        double DecrackleThreshold);

    /// <summary>Side-to-mid ratio at or below which the side signal is treated as noise alone.</summary>
    /// <remarks>
    /// Measured over five transfers from one collection, the programme ratio separated cleanly into
    /// −16.5, −15.2 and −12.3 dB where the disc was cut mono and −9.8 and −6.0 where it was not.
    /// These two anchors sit in that gap. <b>Five files from one collection is not a corpus</b>, which
    /// is why the result is only ever a recommendation on a control the user can see and move: the
    /// declip calibrations were fitted this confidently five times and held out four.
    /// </remarks>
    internal const double MonoPressingSideToMidDb = -14.0;

    /// <summary>Side-to-mid ratio at or above which the side signal is left entirely alone.</summary>
    internal const double StereoSideToMidDb = -8.0;

    /// <summary>The de-crackle threshold in robust deviations, matching the Restore menu default.</summary>
    internal const double DefaultDecrackleThreshold = 3.5;

    internal static Settings Create(
        ClickAnalysisResult clicks,
        ClippingAnalysisResult clipping,
        CleanupAnalysisResult cleanup)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        ArgumentNullException.ThrowIfNull(clipping);
        ArgumentNullException.ThrowIfNull(cleanup);

        double minutes = clicks.SamplesAnalyzed /
            (double)Math.Max(1, clicks.SampleRate) / 60.0;
        double eventsPerMinute = clicks.Events.Count / Math.Max(1.0 / 60.0, minutes);
        double averageClickConfidence = Average(clicks.Events, item => item.Confidence);
        // A damaged record can legitimately contain hundreds of impulses per minute,
        // so density alone must never make auto mode less sensitive. Back off only a
        // half-step when an exceptionally dense population also sits close to the
        // confidence floor; otherwise retain the exploratory 7/10 pass unchanged.
        double clickSensitivity = clicks.Events.Count > 0 &&
                                  eventsPerMinute > 120 &&
                                  averageClickConfidence < 0.68
            ? ExploratoryClickSensitivity - 0.5
            : ExploratoryClickSensitivity;

        // Once an impulse passes the conservative detector, retaining any percentage of
        // its damaged samples leaves a scaled copy of the click behind. Auto restoration
        // therefore performs complete reconstruction; the strength control remains
        // available when the user explicitly wants a partial blend.
        double clickStrength = clicks.Events.Count == 0 ? 0.65 : 1.0;

        double averageClipSeverity = Average(clipping.Events, item => item.Severity);
        double declipStrength = clipping.Events.Count == 0
            ? 0.65
            : Quantize(Math.Clamp(0.58 + averageClipSeverity * 0.34, 0.60, 0.95), 0.05);
        double requiredHeadroom = clipping.Events
            .Select(RequiredHeadroomDb)
            .DefaultIfEmpty(4.0)
            .Max();
        double declipHeadroom = Quantize(Math.Clamp(requiredHeadroom + 0.5, 1.0, 12.0), 0.5);

        EffectFactory.EffectState? hum = FindState(cleanup, "dehum");
        EffectFactory.EffectState? noise = FindState(cleanup, "denoise");
        bool removeHum = hum?.Enabled == true;
        bool reduceNoise = noise?.Enabled == true;
        double noiseReduction = Math.Clamp(Param(noise, "reduction", 8.0), 0, 24);

        // The rumble measurement already exists; it reached the rack chain and not this one. The
        // analyzer writes it into the recommended preset's "filter" state, so it is read back the
        // same way the hum and noise settings are, and no second measurement is taken.
        EffectFactory.EffectState? highPass = FindState(cleanup, "filter");
        bool removeSubsonic = highPass?.Enabled == true;
        double highPassCutoff = Math.Clamp(Param(highPass, "cutoff", 30.0), 20, 60);

        // How far the side may be pulled down is a property of the pressing. Between the anchors
        // this ramps rather than switching, because a ratio landing either side of one number would
        // otherwise decide by itself whether a record keeps its stereo.
        double sideLevel = Quantize(Math.Clamp(
            (cleanup.SideToMidDb - MonoPressingSideToMidDb) /
            (StereoSideToMidDb - MonoPressingSideToMidDb), 0, 1), 0.05);

        // De-crackle rides on the same evidence as click repair: impulses found means a surface
        // that sheds them, and crackle is the population below the click detector's reach rather
        // than a different defect. <b>This is weaker evidence than the other four stages have</b> -
        // a proper crackle-density measurement would be the honest trigger and is not built - so the
        // card names what it went on and the control stays where the user can turn it off.
        bool decrackle = clicks.Events.Count > 0;

        return new Settings(
            clicks.Events.Count > 0,
            clickSensitivity,
            clickStrength,
            clipping.Events.Count > 0,
            declipStrength,
            declipHeadroom,
            reduceNoise,
            noiseReduction,
            Quantize(Math.Clamp(2.5 + noiseReduction * 0.35, 2.5, 8.0), 0.5),
            removeHum,
            Math.Clamp(Param(hum, "amount", 0.65), 0, 1),
            Math.Abs(Param(hum, "frequency", 60) - 50) < 5 ? 50 : 60,
            (int)Math.Round(Math.Clamp(Param(hum, "harmonics", 4), 1, 8)),
            Math.Clamp(Param(hum, "q", 35), 8, 60),
            removeSubsonic,
            highPassCutoff,
            sideLevel,
            decrackle,
            DefaultDecrackleThreshold);
    }

    private static EffectFactory.EffectState? FindState(CleanupAnalysisResult cleanup, string typeId) =>
        cleanup.RecommendedPreset.Effects.FirstOrDefault(item => item.TypeId == typeId);

    private static double Param(EffectFactory.EffectState? state, string name, double fallback) =>
        state != null && state.Params.TryGetValue(name, out double value) && double.IsFinite(value)
            ? value
            : fallback;

    private static double RequiredHeadroomDb(ClippedPeakEvent item)
    {
        double clipped = Math.Max(1e-9, item.AbsoluteClipLevel);
        double estimated = Math.Max(clipped, Math.Abs(item.EstimatedTruePeak));
        return 20 * Math.Log10(estimated / clipped);
    }

    private static double Average<T>(IReadOnlyCollection<T> items, Func<T, double> selector) =>
        items.Count == 0 ? 0 : items.Average(selector);

    private static double Quantize(double value, double step) =>
        Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
}
