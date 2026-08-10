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
        double HumQ);

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
            Math.Clamp(Param(hum, "q", 35), 8, 60));
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
