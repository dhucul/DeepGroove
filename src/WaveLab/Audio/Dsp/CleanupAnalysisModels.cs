using WaveLab.Audio.Effects;

namespace WaveLab.Audio.Dsp;

public enum CleanupProfile
{
    VinylCleanup,
    CleanTransfer,
}

public sealed record CleanupMetric(string Label, string Value, string Detail);

public sealed record CleanupRecommendation(
    string TypeId,
    string DisplayName,
    string Evidence,
    string CurrentText,
    string RecommendedText,
    double Confidence,
    bool ApplyByDefault);

public readonly record struct CleanupAnalysisProgress(string Stage, double Fraction);

/// <summary>
/// Non-destructive analysis result containing both the factory baseline and a fresh
/// analyzed chain. Recommendation selection always starts from the factory baseline;
/// it never reads or overwrites a user's saved preset file.
/// </summary>
public sealed class CleanupAnalysisResult
{
    public required CleanupProfile Profile { get; init; }
    public required IReadOnlyList<CleanupMetric> Metrics { get; init; }
    public required IReadOnlyList<CleanupRecommendation> Recommendations { get; init; }
    public required EffectFactory.ChainPreset BaselinePreset { get; init; }
    public required EffectFactory.ChainPreset RecommendedPreset { get; init; }
    public required int WindowsAnalyzed { get; init; }

    public EffectFactory.ChainPreset BuildSelectedPreset(IEnumerable<string> selectedTypeIds)
    {
        ArgumentNullException.ThrowIfNull(selectedTypeIds);
        var selected = selectedTypeIds.ToHashSet(StringComparer.Ordinal);
        var recommended = RecommendedPreset.Effects.ToDictionary(
            effect => effect.TypeId, CloneState, StringComparer.Ordinal);
        var effects = new List<EffectFactory.EffectState>(BaselinePreset.Effects.Count);
        foreach (EffectFactory.EffectState baseline in BaselinePreset.Effects)
        {
            effects.Add(selected.Contains(baseline.TypeId) && recommended.TryGetValue(baseline.TypeId, out var analyzed)
                ? CloneState(analyzed)
                : CloneState(baseline));
        }

        string profileName = Profile == CleanupProfile.VinylCleanup ? "Vinyl Cleanup" : "Clean Transfer";
        return new EffectFactory.ChainPreset
        {
            Name = $"{profileName} · Analyzed",
            Effects = effects,
        };
    }

    internal static EffectFactory.ChainPreset ClonePreset(EffectFactory.ChainPreset preset, string? name = null) => new()
    {
        Name = name ?? preset.Name,
        Effects = preset.Effects.Select(CloneState).ToList(),
    };

    internal static EffectFactory.EffectState CloneState(EffectFactory.EffectState state) => new()
    {
        TypeId = state.TypeId,
        Enabled = state.Enabled,
        Params = new Dictionary<string, double>(state.Params, StringComparer.Ordinal),
    };
}
