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
        int HumHarmonicMask,
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

    /// <summary>Side-to-mid ratio above which the side signal is left entirely alone.</summary>
    /// <remarks>
    /// <b>"At or above" was true of the linear ramp and is not true of the sigmoid.</b> The curve
    /// reaches full only above about −6.6 dB; at this anchor it recommends 0.90, which is the
    /// deliberate softening — a ratio landing exactly on a number measured from five records should
    /// not be the difference between a stage that runs and one that does not.
    /// </remarks>
    internal const double StereoSideToMidDb = -8.0;

    /// <summary>The de-crackle threshold in robust deviations, matching the Restore menu default.</summary>
    internal const double DefaultDecrackleThreshold = 3.5;

    internal readonly record struct CrackleEvidence(
        DecrackleReport Report, int SamplesAnalyzed, int SampleRate);

    internal static Settings Create(
        ClickAnalysisResult clicks,
        ClippingAnalysisResult clipping,
        CleanupAnalysisResult cleanup,
        CrackleEvidence? crackleEvidence = null)
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
        // this uses a sigmoid rather than a linear ramp, with a floor at 0.20 so the side is never
        // fully discarded. A linear ramp was calibrated on five recordings from one collection and
        // could collapse the stereo image on material outside that narrow set; the sigmoid errs
        // toward preserving width while still collapsing the side on a strong mono signal.
        //   −16.5 dB → 0.20   −14 → 0.25   −11 → 0.55   −8 → 0.90   −6 and above → 1.00
        //
        // The 0.80 span is what makes that last column reachable, and it is load-bearing rather
        // than cosmetic. Three things downstream read "1.00" as "there is nothing to do here": the
        // workbench ticks its Vertical Surface Noise card on `SideLevel < 1.0`, the render skips
        // ScaleSide on the same test, and the evidence line has a "leaving the side at full"
        // branch. A span of 0.75 caps this at 0.95, so all three fire on every stereo record —
        // the card switches itself on, and the readout says the image is being narrowed, for
        // 0.4 dB nobody asked for. It also breaks a contract stated in CleanupAnalyzer.SideToMidDb,
        // which returns 0 for "no reading" precisely because 0 used to mean "leave the side alone".
        double x = (cleanup.SideToMidDb - MonoPressingSideToMidDb) /
                   (StereoSideToMidDb - MonoPressingSideToMidDb);
        double sigmoid = 1.0 / (1.0 + Math.Exp(-5.0 * (x - 0.55)));
        double sideLevel = Quantize(Math.Clamp(0.20 + 0.80 * sigmoid, 0.20, 1.0), 0.05);

        // Unlike isolated clicks, surface crackle is a dense population. Recommend this stage only
        // from its own prediction-residual measurement, and require both a useful event density and
        // a reasonable classifier acceptance rate. A single loud click is not crackle evidence.
        DecrackleReport crackle = crackleEvidence?.Report ?? DecrackleReport.None;
        double crackleDensity = crackleEvidence is { SamplesAnalyzed: > 0, SampleRate: > 0 } evidence
            ? crackle.Density(evidence.SampleRate, evidence.SamplesAnalyzed)
            : 0;
        double acceptanceRate = crackle.Candidates > 0
            ? crackle.Events / (double)crackle.Candidates
            : 0;
        bool decrackle = crackle.Events >= 3 && crackleDensity >= 0.25 && acceptanceRate >= 0.20;

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
            Math.Clamp(Param(hum, "frequency", 60), 45, 65),
            (int)Math.Round(Math.Clamp(Param(hum, "harmonics", 4), 1, 8)),
            (int)Math.Round(Math.Clamp(Param(hum, "harmonicMask",
                (1 << (int)Math.Round(Math.Clamp(Param(hum, "harmonics", 4), 1, 8))) - 1),
                1, 0xFF)),
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
