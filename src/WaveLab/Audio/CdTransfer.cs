using System.Globalization;
using System.IO;
using System.Text;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>A source range and its position in an audio-CD transfer package.</summary>
public sealed record CdTrackPlan(int SourceStart, int SourceEnd, string Title)
{
    public int Length => Math.Max(0, SourceEnd - SourceStart);
    public double DurationSeconds(int sampleRate) => sampleRate > 0 ? (double)Length / sampleRate : 0;
}

public enum CdPlanIssueSeverity { Information, Warning, Error }

public sealed record CdPlanIssue(CdPlanIssueSeverity Severity, string Message);

public sealed record CdPackageProgress(
    int CompletedTracks,
    int TotalTracks,
    string CurrentTrack,
    double? OverallFraction = null)
{
    public double Fraction => Math.Clamp(
        OverallFraction ?? (TotalTracks > 0 ? (double)CompletedTracks / TotalTracks : 0), 0, 1);
}

public sealed record CdPackageResult(string Folder, string CueFile, IReadOnlyList<string> WaveFiles);

/// <summary>
/// Non-destructive helpers for turning a long capture into ordered CD-compatible
/// tracks. The source document is never mutated. A stable continuous snapshot is
/// converted once, then cut on 588-frame CD-sector boundaries to preserve gapless
/// transitions between adjacent tracks.
/// </summary>
public static class CdTransfer
{
    public const int CdSampleRate = CdAudioFormat.SampleRate;
    public const int CdChannels = CdAudioFormat.ChannelCount;
    public const int CdBitDepth = CdAudioFormat.BitsPerSample;
    public const int MaximumTracks = 99;
    public const double MaximumDurationSeconds = 80 * 60;
    public const double MinimumTrackSeconds = 4;

    private sealed record SourceSnapshot(
        float[][] Channels,
        int SampleRate,
        int SourceBitDepth,
        int EditVersion,
        int Length);

    private sealed record PreparedTrack(CdTrackPlan Plan, string Title, int Start, int End)
    {
        public int Length => End - Start;
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// Suggest track ranges from sustained quiet gaps. Boundaries are placed at
    /// gap midpoints, so ambience and decay are retained on both sides.
    /// </summary>
    public static List<CdTrackPlan> SuggestTracks(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        double silenceThresholdDb = -45,
        double minimumSilenceSeconds = 1.25,
        double minimumTrackSeconds = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0 || sampleRate <= 0 || channels[0].Length == 0) return [];
        int length = channels[0].Length;
        if (channels.Any(c => c.Length != length))
            throw new ArgumentException("All source channels must have the same length.", nameof(channels));

        cancellationToken.ThrowIfCancellationRequested();
        var silences = Restoration.DetectSilences(
            channels, sampleRate, silenceThresholdDb, minimumSilenceSeconds * 1000,
            cancellationToken);

        int minimumTrack = Math.Max(1, (int)Math.Round(minimumTrackSeconds * sampleRate));
        var boundaries = new List<int> { 0 };
        foreach (var (start, end) in silences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int boundary = start + (end - start) / 2;
            if (boundary - boundaries[^1] < minimumTrack) continue;
            if (length - boundary < minimumTrack) continue;
            boundaries.Add(boundary);
        }
        boundaries.Add(length);

        var result = new List<CdTrackPlan>(boundaries.Count - 1);
        for (int i = 0; i + 1 < boundaries.Count; i++)
            result.Add(new CdTrackPlan(boundaries[i], boundaries[i + 1], $"Track {i + 1:00}"));
        return result;
    }

    public static List<CdTrackPlan> FromRegions(IEnumerable<NamedRegion> regions, int documentLength)
    {
        return FromRegionsWithSources(regions, documentLength)
            .Select(item => item.Plan)
            .ToList();
    }

    /// <summary>
    /// Retains region identity for the transfer dialog. Once a CD plan has been
    /// explicitly synchronized, only tagged track regions participate and their
    /// stored order wins over timeline order. With a legacy sidecar containing no
    /// tags, every valid region remains a candidate in its existing collection order.
    /// </summary>
    internal static List<(CdTrackPlan Plan, NamedRegion Source)> FromRegionsWithSources(
        IEnumerable<NamedRegion> regions, int documentLength)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var indexed = regions.Select((region, index) => (Region: region, Index: index)).ToList();
        var tagged = indexed.Where(item => item.Region.CdTrackOrder is > 0).ToList();
        IEnumerable<(NamedRegion Region, int Index)> selected = tagged.Count > 0
            ? tagged.OrderBy(item => item.Region.CdTrackOrder).ThenBy(item => item.Index)
            : indexed;

        return selected
            .Select(item => (Plan: new CdTrackPlan(
                    Math.Clamp(item.Region.Start, 0, documentLength),
                    Math.Clamp(item.Region.End, 0, documentLength),
                    string.IsNullOrWhiteSpace(item.Region.Name) ? "Track" : item.Region.Name.Trim()),
                Source: item.Region))
            .Where(item => item.Plan.SourceEnd > item.Plan.SourceStart)
            .ToList();
    }

    public static List<CdPlanIssue> Validate(IReadOnlyList<CdTrackPlan> tracks, int sampleRate, int documentLength)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var issues = new List<CdPlanIssue>();
        if (sampleRate <= 0)
        {
            issues.Add(new(CdPlanIssueSeverity.Error, "The source sample rate is invalid."));
            return issues;
        }
        if (tracks.Count == 0)
            issues.Add(new(CdPlanIssueSeverity.Error, "Add at least one track."));
        if (tracks.Count > MaximumTracks)
            issues.Add(new(CdPlanIssueSeverity.Error, $"Audio CDs support at most {MaximumTracks} tracks."));

        long totalFrames = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (track.SourceStart < 0 || track.SourceEnd > documentLength || track.SourceEnd <= track.SourceStart)
            {
                issues.Add(new(CdPlanIssueSeverity.Error, $"Track {i + 1:00} has an invalid source range."));
                continue;
            }

            int start = MapBoundary(track.SourceStart, sampleRate, documentLength, isEnd: false);
            int end = MapBoundary(track.SourceEnd, sampleRate, documentLength, isEnd: true);
            int frames = Math.Max(0, end - start);
            totalFrames += frames;
            double duration = frames / (double)CdSampleRate;
            if (duration < MinimumTrackSeconds)
                issues.Add(new(CdPlanIssueSeverity.Error,
                    $"Track {i + 1:00} is {duration:0.0} s after CD alignment; tracks must be at least {MinimumTrackSeconds:0} s."));
            if (string.IsNullOrWhiteSpace(track.Title))
                issues.Add(new(CdPlanIssueSeverity.Warning, $"Track {i + 1:00} has no title."));
        }

        double total = totalFrames / (double)CdSampleRate;
        if (total > MaximumDurationSeconds)
            issues.Add(new(CdPlanIssueSeverity.Error,
                $"The sector-aligned program is {FormatDuration(total)}; the CD target is at most {FormatDuration(MaximumDurationSeconds)}."));
        else if (total > 74 * 60)
            issues.Add(new(CdPlanIssueSeverity.Warning,
                $"The sector-aligned program is {FormatDuration(total)}. Confirm that the target disc supports 80-minute media."));
        else
            issues.Add(new(CdPlanIssueSeverity.Information,
                $"Program length: {FormatDuration(total)} across {tracks.Count} track(s), aligned to CD sectors."));
        return issues;
    }

    /// <summary>
    /// Capture the document's stable channel-array references and metadata before
    /// scheduling background work. AudioDocument edits replace those arrays rather
    /// than mutating them, so a concurrent edit cannot mix versions in one package.
    /// </summary>
    public static Task<CdPackageResult> ExportPackageAsync(
        AudioDocument document,
        IReadOnlyList<CdTrackPlan> orderedTracks,
        string outputFolder,
        string discTitle,
        IProgress<CdPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(orderedTracks);
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new ArgumentException("Choose an output folder.", nameof(outputFolder));

        var channels = document.Channels.ToArray();
        int length = document.Length;
        if (channels.Any(c => c.Length != length))
            throw new InvalidOperationException("The document channels do not have matching lengths.");
        var snapshot = new SourceSnapshot(channels, document.SampleRate, document.SourceBitDepth,
            document.EditVersion, length);
        var plans = orderedTracks.ToArray();
        string folder = Path.GetFullPath(outputFolder.Trim());
        string title = string.IsNullOrWhiteSpace(discTitle) ? "Audio CD" : discTitle.Trim();

        return Task.Run(() => ExportPackage(snapshot, plans, folder, title, progress, cancellationToken),
            cancellationToken);
    }

    private static CdPackageResult ExportPackage(
        SourceSnapshot source,
        IReadOnlyList<CdTrackPlan> orderedTracks,
        string outputFolder,
        string discTitle,
        IProgress<CdPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var issues = Validate(orderedTracks, source.SampleRate, source.Length);
        var errors = issues.Where(i => i.Severity == CdPlanIssueSeverity.Error).Select(i => i.Message).ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputFolder);
        if (Directory.EnumerateFileSystemEntries(outputFolder).Any())
            throw new IOException("The CD package folder must be empty. Choose a new or empty folder so existing files cannot be overwritten.");

        string safeDiscTitle = SafeName(discTitle);
        var prepared = PrepareTrackBoundaries(orderedTracks, source.SampleRate, source.Length);
        var finalNames = prepared.Select((track, index) =>
            $"{index + 1:00} - {SafeName(track.Title)}.wav").ToArray();
        string finalCuePath = Path.Combine(outputFolder, safeDiscTitle + ".cue");
        var finalWavePaths = finalNames.Select(name => Path.Combine(outputFolder, name)).ToArray();
        if (File.Exists(finalCuePath) || finalWavePaths.Any(File.Exists))
            throw new IOException("One or more package files already exist in the selected folder.");

        string stageFolder = Path.Combine(outputFolder, ".wavelab-cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageFolder);
        var published = new List<string>();
        try
        {
            float[][] continuous = ToStereo(source.Channels, cancellationToken);
            if (source.SampleRate != CdSampleRate)
            {
                progress?.Report(new CdPackageProgress(0, prepared.Count, "Converting continuous master", 0));
                var srcProgress = progress == null ? null : new CallbackProgress<double>(fraction =>
                    progress.Report(new CdPackageProgress(0, prepared.Count,
                        "Converting continuous master", fraction * 0.2)));
                continuous = Resampler.Resample(continuous, source.SampleRate, CdSampleRate,
                    cancellationToken, srcProgress);
            }

            // Metadata alone cannot prove that a nominally 16-bit document is
            // still on the signed-16 PCM grid (a generated tab or Save As can
            // retain/inherit that metadata). Inspect the final CD-rate stereo
            // program instead: bit-exact samples need no dither; every
            // mathematically processed value does.
            bool dither = !IsExact16BitPcm(continuous, cancellationToken);
            var cue = new StringBuilder();
            cue.AppendLine($"TITLE \"{CueEscape(discTitle)}\"");
            cue.AppendLine("PERFORMER \"WaveLab Transfer\"");

            for (int i = 0; i < prepared.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = prepared[i];
                double trackBase = 0.2 + 0.75 * i / Math.Max(1, prepared.Count);
                progress?.Report(new CdPackageProgress(i, prepared.Count, track.Title, trackBase));

                float[][] data = CopyRange(continuous, track.Start, track.Length, cancellationToken);
                string stagePath = Path.Combine(stageFolder, finalNames[i]);
                var output = new AudioDocument(data, CdSampleRate, CdBitDepth)
                {
                    Title = finalNames[i],
                    FilePath = stagePath,
                };
                var wavProgress = progress == null ? null : new CallbackProgress<double>(fraction =>
                    progress.Report(new CdPackageProgress(i, prepared.Count, track.Title,
                        trackBase + 0.75 * fraction / Math.Max(1, prepared.Count))));
                WavCodec.Save(output, stagePath, CdBitDepth, dither, cancellationToken, wavProgress);

                cue.AppendLine($"FILE \"{CueEscape(finalNames[i])}\" WAVE");
                cue.AppendLine($"  TRACK {i + 1:00} AUDIO");
                cue.AppendLine($"    TITLE \"{CueEscape(track.Title)}\"");
                cue.AppendLine("    INDEX 01 00:00:00");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string stageCuePath = Path.Combine(stageFolder, safeDiscTitle + ".cue");
            File.WriteAllText(stageCuePath, cue.ToString(), new UTF8Encoding(false));

            // All files are complete before any final name becomes visible. Moves stay
            // on the same volume and are rolled back if publication is interrupted.
            for (int i = 0; i < finalWavePaths.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(Path.Combine(stageFolder, finalNames[i]), finalWavePaths[i], overwrite: false);
                published.Add(finalWavePaths[i]);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stageCuePath, finalCuePath, overwrite: false);
            published.Add(finalCuePath);

            progress?.Report(new CdPackageProgress(prepared.Count, prepared.Count, "Complete", 1));
            return new CdPackageResult(outputFolder, finalCuePath, finalWavePaths);
        }
        catch
        {
            foreach (string path in published)
            {
                try { File.Delete(path); }
                catch { /* Preserve the original export failure. */ }
            }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(stageFolder)) Directory.Delete(stageFolder, recursive: true); }
            catch { /* A stale uniquely-named staging folder is safer than deleting user files. */ }
        }
    }

    private static List<PreparedTrack> PrepareTrackBoundaries(
        IReadOnlyList<CdTrackPlan> tracks, int sampleRate, int documentLength)
    {
        var result = new List<PreparedTrack>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var plan = tracks[i];
            int start = MapBoundary(plan.SourceStart, sampleRate, documentLength, isEnd: false);
            int end = MapBoundary(plan.SourceEnd, sampleRate, documentLength, isEnd: true);
            string title = string.IsNullOrWhiteSpace(plan.Title) ? $"Track {i + 1:00}" : plan.Title.Trim();
            result.Add(new PreparedTrack(plan, title, start, end));
        }
        return result;
    }

    private static int MapBoundary(int sourceSample, int sampleRate, int documentLength, bool isEnd)
    {
        double exact = sourceSample * (double)CdSampleRate / sampleRate;
        double sectors = exact / CdAudioFormat.FramesPerSector;
        long sector;
        if (sourceSample <= 0) sector = 0;
        else if (sourceSample >= documentLength && isEnd) sector = (long)Math.Ceiling(sectors);
        else sector = (long)Math.Round(sectors, MidpointRounding.AwayFromZero);
        long frames = sector * CdAudioFormat.FramesPerSector;
        if (frames > int.MaxValue)
            throw new InvalidOperationException("The CD program exceeds the supported sample count.");
        return (int)Math.Max(0, frames);
    }

    private static float[][] ToStereo(IReadOnlyList<float[]> source, CancellationToken cancellationToken)
    {
        if (source.Count == 2) return [source[0], source[1]];
        if (source.Count == 1) return [source[0], source[0]];
        if (source.Count == 0) return [[], []];

        int frames = source[0].Length;
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            double sum = 0;
            for (int c = 0; c < source.Count; c++) sum += source[c][i];
            float mono = (float)(sum / source.Count);
            left[i] = mono;
            right[i] = mono;
        }
        return [left, right];
    }

    private static float[][] CopyRange(
        IReadOnlyList<float[]> source, int start, int count, CancellationToken cancellationToken)
    {
        const int block = 1 << 20;
        var result = new float[source.Count][];
        for (int c = 0; c < source.Count; c++)
        {
            result[c] = new float[count];
            for (int offset = 0; offset < count; offset += block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = Math.Min(block, count - offset);
                int available = Math.Clamp(source[c].Length - (start + offset), 0, n);
                if (available > 0)
                    Array.Copy(source[c], start + offset, result[c], offset, available);
                // Newly allocated buffers already contain zeroes for sector padding.
            }
        }
        return result;
    }

    private static bool IsExact16BitPcm(
        IReadOnlyList<float[]> channels, CancellationToken cancellationToken)
    {
        foreach (float[] channel in channels)
        {
            for (int sample = 0; sample < channel.Length; sample++)
            {
                if ((sample & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                float value = channel[sample];
                if (!float.IsFinite(value)) return false;
                double scaled = value * 32768.0;
                if (scaled < short.MinValue || scaled > short.MaxValue || scaled != Math.Truncate(scaled))
                    return false;
            }
        }
        return true;
    }

    private static string SafeName(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Audio CD" : value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        value = value.TrimEnd('.', ' ');
        if (value.Length > 96) value = value[..96].TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(value) ? "Track" : value;
    }

    private static string CueEscape(string? value) =>
        (string.IsNullOrWhiteSpace(value) ? "Audio CD" : value).Replace("\"", "'", StringComparison.Ordinal);

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
