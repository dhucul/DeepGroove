using WaveLab.Util;

namespace WaveLab.Audio.Montage;

/// <summary>One piece of a source, placed on the montage's timeline.</summary>
/// <remarks>
/// Mutable, like <see cref="NamedRegion"/> and unlike <c>CdTrackPlan</c>, because a clip is dragged
/// and trimmed interactively rather than rebuilt from a plan each time.
/// </remarks>
public sealed class MontageClip
{
    /// <summary>Index into the montage's source list.</summary>
    public int SourceIndex { get; set; }

    public string Name { get; set; } = "Clip";

    /// <summary>Where the clip starts inside its source, in samples.</summary>
    public int SourceStart { get; set; }

    /// <summary>How much of the source it plays, in samples.</summary>
    public int Length { get; set; }

    /// <summary>Where it starts on the montage timeline, in samples.</summary>
    public int TimelineStart { get; set; }

    /// <summary>Level trim for this clip alone.</summary>
    public double GainDb { get; set; }

    public int FadeInSamples { get; set; }
    public int FadeOutSamples { get; set; }
    public FadeShape FadeInShape { get; set; } = FadeShape.EqualPower;
    public FadeShape FadeOutShape { get; set; } = FadeShape.EqualPower;

    /// <summary>One past the last sample this clip occupies on the timeline.</summary>
    public int TimelineEnd => TimelineStart + Math.Max(0, Length);

    public double Gain => Math.Pow(10, GainDb / 20);

    public MontageClip Clone() => new()
    {
        SourceIndex = SourceIndex,
        Name = Name,
        SourceStart = SourceStart,
        Length = Length,
        TimelineStart = TimelineStart,
        GainDb = GainDb,
        FadeInSamples = FadeInSamples,
        FadeOutSamples = FadeOutSamples,
        FadeInShape = FadeInShape,
        FadeOutShape = FadeOutShape,
    };
}

public enum MontageIssueSeverity { Information, Warning, Error }

public sealed record MontageIssue(MontageIssueSeverity Severity, string Message, int ClipIndex = -1);

/// <summary>
/// A single-lane clip timeline: sources, clips placed on one lane, and the arithmetic of where they
/// sit relative to each other.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>one lane</b>. This is not the multitrack rewrite <c>docs/ROADMAP.md</c> declined
/// — there is no mixer, no bus, no send and no automation lane, and adding any of them would be a
/// different product. What one lane buys is that <b>an overlap is unambiguously a crossfade</b>:
/// there is only ever one thing a region where two clips meet can mean, which is what lets the
/// renderer measure the join and pick the right law for it.
/// </para>
/// <para>
/// Clips are kept sorted by timeline position, because every operation that matters — finding the
/// overlaps, rendering in order, reporting the running order — is a walk over neighbours.
/// </para>
/// </remarks>
public sealed class MontageDocument
{
    private readonly List<MontageSource> _sources = [];
    private readonly List<MontageClip> _clips = [];

    public MontageDocument(int sampleRate = 44_100, int channelCount = 2)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelCount is <= 0 or > 8) throw new ArgumentOutOfRangeException(nameof(channelCount));
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    public int SampleRate { get; }
    public int ChannelCount { get; }
    public string Title { get; set; } = "Untitled montage";
    public string? FilePath { get; set; }

    public IReadOnlyList<MontageSource> Sources => _sources;
    public IReadOnlyList<MontageClip> Clips => _clips;

    /// <summary>One past the last sample any clip occupies.</summary>
    public int Length
    {
        get
        {
            int end = 0;
            foreach (MontageClip clip in _clips) end = Math.Max(end, clip.TimelineEnd);
            return end;
        }
    }

    public double Duration => SampleRate > 0 ? (double)Length / SampleRate : 0;

    // ── sources ──────────────────────────────────────────────────

    /// <summary>Adds a source, or returns the index of the one already loaded from that path.</summary>
    public int AddSource(MontageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SampleRate != SampleRate)
            throw new ArgumentException("A source must be on the montage's clock.", nameof(source));
        if (source.ChannelCount != ChannelCount)
            throw new ArgumentException("A source must have the montage's channel count.", nameof(source));

        if (source.Path is { } path)
        {
            int existing = _sources.FindIndex(s =>
                s.Path is { } other && string.Equals(other, path, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) return existing;
        }

        _sources.Add(source);
        return _sources.Count - 1;
    }

    // ── clips ────────────────────────────────────────────────────

    /// <summary>Places a clip and returns it, keeping the lane in timeline order.</summary>
    public MontageClip Add(MontageClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.SourceIndex < 0 || clip.SourceIndex >= _sources.Count)
            throw new ArgumentOutOfRangeException(nameof(clip), "The clip names no loaded source.");

        clip.TimelineStart = Math.Max(0, clip.TimelineStart);
        clip.SourceStart = Math.Max(0, clip.SourceStart);
        clip.Length = Math.Max(0, clip.Length);
        _clips.Add(clip);
        Sort();
        return clip;
    }

    /// <summary>Appends a whole source at the end of the lane, optionally overlapping what is there.</summary>
    public MontageClip Append(int sourceIndex, int overlapSamples = 0, string? name = null)
    {
        if (sourceIndex < 0 || sourceIndex >= _sources.Count)
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));

        MontageSource source = _sources[sourceIndex];
        int start = Math.Max(0, Length - Math.Max(0, overlapSamples));
        return Add(new MontageClip
        {
            SourceIndex = sourceIndex,
            Name = name ?? source.Name,
            SourceStart = 0,
            Length = source.Length,
            TimelineStart = start,
        });
    }

    public bool Remove(MontageClip clip) => _clips.Remove(clip);

    public void Clear() => _clips.Clear();

    /// <summary>Re-sorts after a clip has been moved. Cheap, and the alternative is a stale order.</summary>
    public void Sort() => _clips.Sort((a, b) =>
    {
        int byStart = a.TimelineStart.CompareTo(b.TimelineStart);
        return byStart != 0 ? byStart : a.TimelineEnd.CompareTo(b.TimelineEnd);
    });

    /// <summary>
    /// How far two neighbours overlap, in samples. Zero means they butt or there is a gap.
    /// </summary>
    public static int Overlap(MontageClip first, MontageClip second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return Math.Max(0, Math.Min(first.TimelineEnd, second.TimelineEnd) -
                           Math.Max(first.TimelineStart, second.TimelineStart));
    }

    // ── validation ───────────────────────────────────────────────

    /// <summary>
    /// What is wrong, or worth saying, about the lane as it stands.
    /// </summary>
    public List<MontageIssue> Validate()
    {
        var issues = new List<MontageIssue>();
        if (_clips.Count == 0)
        {
            issues.Add(new(MontageIssueSeverity.Error, "The montage has no clips."));
            return issues;
        }

        Sort();
        for (int i = 0; i < _clips.Count; i++)
        {
            MontageClip clip = _clips[i];
            if (clip.TimelineStart < 0 || clip.SourceStart < 0 || (long)clip.TimelineStart + clip.Length > Array.MaxLength)
            {
                issues.Add(new(MontageIssueSeverity.Error,
                    $"Clip {i + 1} has an invalid timeline or source position.", i));
                continue;
            }
            if (clip.SourceIndex < 0 || clip.SourceIndex >= _sources.Count)
            {
                issues.Add(new(MontageIssueSeverity.Error,
                    $"Clip {i + 1} names a source that is not loaded.", i));
                continue;
            }
            if (clip.Length <= 0)
            {
                issues.Add(new(MontageIssueSeverity.Error, $"Clip {i + 1} is empty.", i));
                continue;
            }

            MontageSource source = _sources[clip.SourceIndex];
            if (clip.SourceStart + clip.Length > source.Length)
                issues.Add(new(MontageIssueSeverity.Warning,
                    $"Clip {i + 1} reads past the end of {source.Name}; the tail will be silence.", i));

            if (clip.FadeInSamples + clip.FadeOutSamples > clip.Length)
                issues.Add(new(MontageIssueSeverity.Warning,
                    $"Clip {i + 1}'s fades are longer than the clip; they will be shortened to fit.", i));

            // Three clips over one sample is not a crossfade, because a crossfade is a statement
            // about two things. The renderer resolves it in a stated order rather than guessing.
            if (i + 2 < _clips.Count && Overlap(clip, _clips[i + 2]) > 0)
                issues.Add(new(MontageIssueSeverity.Warning,
                    $"Clips {i + 1}, {i + 2} and {i + 3} all overlap; only neighbouring pairs are crossfaded.", i));
        }

        int gaps = 0;
        for (int i = 0; i + 1 < _clips.Count; i++)
            if (_clips[i + 1].TimelineStart > _clips[i].TimelineEnd) gaps++;

        int crossfades = 0;
        for (int i = 0; i + 1 < _clips.Count; i++)
            if (Overlap(_clips[i], _clips[i + 1]) > 0) crossfades++;

        issues.Add(new(MontageIssueSeverity.Information,
            $"{_clips.Count} clip(s), {crossfades} crossfade(s), {gaps} gap(s), " +
            $"{TimeFormat.Compact(Duration)} long."));
        return issues;
    }

    public MontageDocument Clone()
    {
        var copy = new MontageDocument(SampleRate, ChannelCount) { Title = Title, FilePath = FilePath };
        copy._sources.AddRange(_sources);
        foreach (MontageClip clip in _clips) copy._clips.Add(clip.Clone());
        return copy;
    }
}
