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
        double averageClickSeverity = Average(clicks.Events, item => item.Severity);
        double popRatio = clicks.Events.Count == 0
            ? 0
            : clicks.PopCount / (double)clicks.Events.Count;

        // A dense candidate population needs a more conservative detector; a sparse,
        // high-confidence population can safely retain more sensitivity. The exploratory
        // pass uses 7/10 so quiet clicks are represented before this choice is made.
        double clickSensitivity = eventsPerMinute switch
        {
            <= 0 => ExploratoryClickSensitivity,
            > 120 => 4.5,
            > 45 => 5.5,
            > 12 => 6.0,
            > 3 => 6.5,
            _ => 7.0,
        };
        if (clicks.Events.Count > 0 && averageClickConfidence < 0.68)
            clickSensitivity -= 0.5;
        clickSensitivity = Quantize(Math.Clamp(clickSensitivity, 4.0, 7.5), 0.5);

        double clickStrength = clicks.Events.Count == 0
            ? 0.65
            : Quantize(Math.Clamp(
                0.52 + averageClickSeverity * 0.34 + popRatio * 0.10,
                0.55, 0.92), 0.05);

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
