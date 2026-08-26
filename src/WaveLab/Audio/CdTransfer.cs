using System.Globalization;
using System.IO;
using System.Text;
using WaveLab.Audio.Dsp;
using WaveLab.Util;

namespace WaveLab.Audio;

/// <summary>A source range and its position in an audio-CD transfer package.</summary>
/// <remarks>
/// Everything past <paramref name="Title"/> exists for the DDP deliverable, which states catalogue
/// information a cue sheet has no place for. A burner-bound package ignores it, and nothing in the
/// app fills it yet — the PQ sheet editor that does is still a design away — so it defaults to
/// absent rather than to something invented.
/// </remarks>
public sealed record CdTrackPlan(
    int SourceStart, int SourceEnd, string Title,
    string Performer = "", string Songwriter = "", string Isrc = "", bool PreEmphasis = false,
    double PregapSeconds = 0)
{
    public int Length => Math.Max(0, SourceEnd - SourceStart);
    public double DurationSeconds(int sampleRate) => sampleRate > 0 ? (double)Length / sampleRate : 0;

    /// <summary>
    /// The silence written ahead of this track, in whole CD sectors.
    /// </summary>
    /// <remarks>
    /// It belongs to this track as its pregap — the stretch between INDEX 00 and INDEX 01 — so a
    /// player counts it down during continuous listening and skips it when the track is chosen
    /// directly, which is what a shop-bought CD does. Track 01 never has one: the two-second
    /// lead-in every disc begins with already is it.
    /// </remarks>
    public int PregapSectors => PregapSeconds <= 0
        ? 0
        : (int)Math.Round(PregapSeconds * CdAudioFormat.SampleRate / CdAudioFormat.FramesPerSector);
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
/// One answer the auto-split sweep found: a run of thresholds that all agree about where the
/// tracks are, and the setting in the middle of it.
/// </summary>
/// <param name="Tracks">How many tracks this answer proposes.</param>
/// <param name="LowestDb">The quietest threshold that gives this answer.</param>
/// <param name="HighestDb">The loudest threshold that gives it.</param>
/// <param name="ChosenDb">The middle of that run — the point furthest from where the answer changes.</param>
/// <param name="MinimumGapSeconds">The shortest quiet stretch this pass was willing to call a gap.</param>
/// <param name="Boundaries">Where the tracks divide, first at 0 and last at the length.</param>
public sealed record CdSplitCandidate(
    int Tracks,
    double LowestDb,
    double HighestDb,
    double ChosenDb,
    double MinimumGapSeconds,
    IReadOnlyList<int> Boundaries);

/// <summary>Every answer a sweep found, widest run of thresholds first.</summary>
/// <param name="Best">
/// The one to apply, or null when a track count was asked for that no setting produces.
/// </param>
/// <param name="GapRelaxed">
/// Whether nothing was found until the shortest gap looked for was shortened.
/// </param>
public sealed record CdSplitSweep(
    IReadOnlyList<CdSplitCandidate> Candidates,
    CdSplitCandidate? Best,
    double LowestSweptDb,
    double HighestSweptDb,
    bool GapRelaxed);

/// <summary>Where one track sits on the disc, in CD frames from the start of the programme.</summary>
public sealed record CdPqEntry(int Track, int StartFrame, int LengthFrames)
{
    public string StartTimecode => DdpImage.Timecode(StartFrame);
    public string LengthTimecode => DdpImage.Timecode(LengthFrames);
}

/// <summary>The whole disc's PQ timing, as the plant would read it.</summary>
public sealed record CdPqLayout(IReadOnlyList<CdPqEntry> Tracks, int LeadOutFrame)
{
    public string LeadOutTimecode => DdpImage.Timecode(LeadOutFrame);
}

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

    private sealed record PreparedTrack(CdTrackPlan Plan, string Title, int Start, int End, int PregapSamples)
    {
        public int Length => End - Start;

        /// <summary>The pregap plus the music: what this track occupies on the disc.</summary>
        public int Occupies => PregapSamples + Length;
        public int PregapFrames => PregapSamples / CdAudioFormat.FramesPerSector;
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>What the AUTO SPLIT panel looks for, and the range its slider covers.</summary>
    public const double DefaultSilenceThresholdDb = -45;
    public const double LowestSilenceThresholdDb = -70;
    public const double HighestSilenceThresholdDb = -25;
    public const double DefaultMinimumGapSeconds = 1.25;
    public const double AutoSplitMinimumTrackSeconds = 20;

    /// <summary>
    /// Suggest track ranges from sustained quiet gaps. Boundaries are placed at
    /// gap midpoints, so ambience and decay are retained on both sides.
    /// </summary>
    public static List<CdTrackPlan> SuggestTracks(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        double silenceThresholdDb = DefaultSilenceThresholdDb,
        double minimumSilenceSeconds = DefaultMinimumGapSeconds,
        double minimumTrackSeconds = AutoSplitMinimumTrackSeconds,
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

        return PlansFrom(BoundariesFrom(
            silences, length, MinimumTrackSamples(minimumTrackSeconds, sampleRate), cancellationToken));
    }

    private static int MinimumTrackSamples(double seconds, int sampleRate) =>
        Math.Max(1, (int)Math.Round(seconds * sampleRate));

    /// <summary>
    /// Where the tracks divide, given the quiet stretches. Shared so that the sweep and a single
    /// analysis cannot disagree about the same threshold, which is the fault this repo records for
    /// every readout computed separately from the thing it describes.
    /// </summary>
    private static List<int> BoundariesFrom(
        IReadOnlyList<(int Start, int End)> silences, int length, int minimumTrack,
        CancellationToken cancellationToken)
    {
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
        return boundaries;
    }

    private static List<CdTrackPlan> PlansFrom(IReadOnlyList<int> boundaries)
    {
        var result = new List<CdTrackPlan>(Math.Max(0, boundaries.Count - 1));
        for (int i = 0; i + 1 < boundaries.Count; i++)
            result.Add(new CdTrackPlan(boundaries[i], boundaries[i + 1], $"Track {i + 1:00}"));
        return result;
    }

    // ── an even gap between every pair of tracks ─────────────────

    /// <summary>What a CD normally leaves between two songs.</summary>
    public const double DefaultGapSeconds = 2;
    public const double MaximumGapSeconds = 10;

    /// <summary>
    /// Trim each split back to the music either side of it and declare a fixed gap, so the silence
    /// between every pair of tracks is the same length whatever the record did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The splits land at the middle of the quiet between two songs, so each track already carries
    /// half of whatever quiet the record had there — three seconds on one side of the disc and six
    /// on the other. Adding a fixed silence on top of that would preserve the unevenness and make
    /// it worse. Taking the quiet off both ends and putting back exactly what was asked for is what
    /// makes every gap the same.
    /// </para>
    /// <para>
    /// <b>Nothing above the threshold is ever trimmed</b>, so a fade is only shortened where it has
    /// already fallen below the level the user called quiet — inaudible by that definition. A track
    /// with nothing above the threshold anywhere is left exactly as it is rather than collapsed.
    /// </para>
    /// <para>
    /// The first track's head and the last track's tail are untouched. This sets what is
    /// <i>between</i> tracks, and the lead-in and run-out are not between anything.
    /// </para>
    /// <para>
    /// It is idempotent: trimming a range to its music and trimming it again gives the same range,
    /// so changing the gap from two seconds to three re-runs it harmlessly rather than eating
    /// another slice each time.
    /// </para>
    /// </remarks>
    /// <param name="quietBelowDb">
    /// What counts as quiet — the same level the AUTO SPLIT slider used to find the gaps, so the
    /// two halves of the window cannot disagree about where a song ends.
    /// </param>
    public static List<CdTrackPlan> ApplyGaps(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        IReadOnlyList<CdTrackPlan> tracks,
        double gapSeconds,
        double quietBelowDb,
        float[]? blockPeaks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0) return [];

        double gap = SnapGapSeconds(gapSeconds);
        var result = new List<CdTrackPlan>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++) result.Add(tracks[i] with { PregapSeconds = i == 0 ? 0 : gap });

        if (channels.Count == 0 || sampleRate <= 0 || channels[0].Length == 0 || gap <= 0)
            return result;

        int length = channels[0].Length;
        double threshold = Math.Pow(10, quietBelowDb / 20.0);

        // Searched through the block envelope rather than sample by sample. The scan runs inward
        // from a track end until it meets music, so a track holding nothing above the threshold - a
        // run-out, a quiet interlude - makes it walk the whole track, and RefreshOrder calls this
        // on every arrow press. A stale envelope of the wrong length is rebuilt, not trusted.
        int blocks = (length + Restoration.SilenceBlock - 1) / Restoration.SilenceBlock;
        float[] envelope = blockPeaks is { } given && given.Length == blocks
            ? given
            : Restoration.BlockPeaks(channels, sampleRate, cancellationToken);

        for (int i = 0; i < result.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CdTrackPlan plan = result[i];
            int start = Math.Clamp(plan.SourceStart, 0, length);
            int end = Math.Clamp(plan.SourceEnd, 0, length);
            if (end <= start) continue;

            // Only the ends that face another track are trimmed.
            int music = i == 0 ? start : FirstAbove(channels, envelope, start, end, threshold, cancellationToken);
            int quiet = i == result.Count - 1
                ? end
                : LastAbove(channels, envelope, start, end, threshold, cancellationToken) + 1;

            // A track holding nothing above the threshold has no music to trim back to, and
            // collapsing it would delete a row the user can see. Leave it alone.
            if (music >= quiet) continue;
            result[i] = plan with { SourceStart = music, SourceEnd = quiet };
        }
        return result;
    }

    /// <summary>
    /// The first sample at or above <paramref name="threshold"/>, or <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// Exact despite reading the envelope first: a block's entry is the largest magnitude in it, so
    /// a block under the threshold cannot hold a sample at or above one. Only a block that clears
    /// it is read sample by sample, which makes this a walk over a two-hundred-and-fifty-sixth of
    /// the audio plus one block.
    /// </remarks>
    private static int FirstAbove(
        IReadOnlyList<float[]> channels, float[] envelope, int from, int to, double threshold,
        CancellationToken token)
    {
        if (to <= from) return to;
        int lastBlock = Math.Min(envelope.Length - 1, (to - 1) / Restoration.SilenceBlock);
        for (int block = from / Restoration.SilenceBlock; block <= lastBlock; block++)
        {
            if ((block & 0x3FF) == 0) token.ThrowIfCancellationRequested();
            if (envelope[block] < threshold) continue;
            int at = block * Restoration.SilenceBlock;
            int stop = Math.Min(to, at + Restoration.SilenceBlock);
            // A block's peak can belong to a sample outside this track's range, so finding nothing
            // inside the range is ordinary rather than a contradiction.
            for (int i = Math.Max(from, at); i < stop; i++)
                foreach (float[] channel in channels)
                    if (Math.Abs(channel[i]) >= threshold) return i;
        }
        return to;
    }

    /// <summary>The last sample at or above <paramref name="threshold"/>, or <c>from - 1</c>.</summary>
    private static int LastAbove(
        IReadOnlyList<float[]> channels, float[] envelope, int from, int to, double threshold,
        CancellationToken token)
    {
        if (to <= from) return from - 1;
        int firstBlock = from / Restoration.SilenceBlock;
        for (int block = Math.Min(envelope.Length - 1, (to - 1) / Restoration.SilenceBlock);
             block >= firstBlock; block--)
        {
            if ((block & 0x3FF) == 0) token.ThrowIfCancellationRequested();
            if (envelope[block] < threshold) continue;
            int at = block * Restoration.SilenceBlock;
            int start = Math.Max(from, at);
            for (int i = Math.Min(to, at + Restoration.SilenceBlock) - 1; i >= start; i--)
                foreach (float[] channel in channels)
                    if (Math.Abs(channel[i]) >= threshold) return i;
        }
        return from - 1;
    }

    /// <summary>
    /// A gap rounded to the only lengths a pregap can be: whole CD frames, seventy-five a second.
    /// </summary>
    /// <remarks>
    /// Without it a box accepting tenths lets 0.1 s through, which is seven and a half frames and
    /// reaches the disc as eight - 0.107 s under a readout saying 0.1. Whole seconds are exact
    /// either way, which is why it took a review to notice.
    /// </remarks>
    public static double SnapGapSeconds(double seconds) =>
        Math.Round(Math.Clamp(seconds, 0, MaximumGapSeconds) * DdpImage.FramesPerSecond,
            MidpointRounding.AwayFromZero) / DdpImage.FramesPerSecond;

    /// <summary>What setting a gap did, in the plain voice the rest of this window reports in.</summary>
    public static string DescribeGap(double seconds, int tracks, int trimmed)
    {
        if (seconds <= 0)
            return "Gap removed. The quiet between tracks is whatever the record left there.";

        string ends = trimmed switch
        {
            0 => "Nothing needed trimming.",
            1 => "One track trimmed back to its music to make room.",
            _ => $"{trimmed} tracks trimmed back to their music to make room.",
        };
        return $"{seconds:0.###} s between every pair of tracks. {ends} " +
               "Choosing a track still starts on the music.";
    }

    /// <summary>How long the gaps add up to, for the line that reports what a plan comes to.</summary>
    public static double TotalGapSeconds(IReadOnlyList<CdTrackPlan> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return tracks.Sum(t => Math.Max(0, t.PregapSeconds));
    }

    // ── finding the setting instead of hunting for it ────────────

    /// <summary>How far apart two thresholds' splits may sit and still count as the same answer.</summary>
    internal const double PlateauToleranceSeconds = 0.5;

    /// <summary>
    /// How many decibels a multi-track answer has to hold over before it beats "one long track".
    /// One track is the <i>absence</i> of a finding, so a split that survives only a setting or two
    /// is not evidence of one.
    /// </summary>
    public const double MinimumPlateauDb = 3;

    /// <summary>Tried only when nothing at all is found at <see cref="DefaultMinimumGapSeconds"/>.</summary>
    internal const double RelaxedMinimumGapSeconds = 0.6;

    /// <summary>
    /// Run a sweep and pick a setting, instead of leaving the user to hunt for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported as "it is hard to determine how to fix the problem with the slider". The window was
    /// asking for the answer to an inverse problem — which level produces the right tracks — and
    /// answering it by hand means guess, count the rows, guess again, with nothing on screen saying
    /// which way to go.
    /// </para>
    /// <para>
    /// It is answerable because <b>a real gap structure is robust to the threshold and a spurious
    /// one is not</b>. Measured on a real three-track side, every setting from −55 to −40 dB
    /// proposes the same three tracks with the splits steady within 0.07 s; past −40 they slide, by
    /// 7.6 s at −30, because a looser threshold calls the fade-out quiet sooner and the split lands
    /// inside the music. So the setting to use is the middle of the widest run of thresholds that
    /// agree, and that is a property the program can measure and the user cannot see.
    /// </para>
    /// <para>
    /// The sweep is affordable because <see cref="Restoration.BlockPeaks"/> is the whole cost of a
    /// silence pass and none of it depends on the threshold: the envelope is measured once and
    /// forty-six thresholds are run against it.
    /// </para>
    /// </remarks>
    /// <param name="targetTracks">
    /// How many tracks the side is known to hold, or null to take the steadiest answer. When it is
    /// given and no setting produces it, <see cref="CdSplitSweep.Best"/> is null and the counts that
    /// <i>are</i> reachable are what the caller reports.
    /// </param>
    public static CdSplitSweep SweepTracks(
        IReadOnlyList<float[]> channels,
        int sampleRate,
        int? targetTracks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var empty = new CdSplitSweep([], null, LowestSilenceThresholdDb, HighestSilenceThresholdDb, false);
        if (channels.Count == 0 || sampleRate <= 0 || channels[0].Length == 0) return empty;

        int length = channels[0].Length;
        if (channels.Any(c => c.Length != length))
            throw new ArgumentException("All source channels must have the same length.", nameof(channels));

        float[] envelope = Restoration.BlockPeaks(channels, sampleRate, cancellationToken);
        var candidates = Plateaus(envelope, length, sampleRate, DefaultMinimumGapSeconds, cancellationToken);
        bool relaxed = false;

        // Nothing at any setting usually means the quiet between the songs is shorter than the gap
        // being looked for, which no threshold can fix. One more sweep against the same envelope is
        // nearly free, and it is the difference between an answer and a dead end.
        if (!candidates.Any(c => c.Tracks > 1))
        {
            var shorter = Plateaus(envelope, length, sampleRate, RelaxedMinimumGapSeconds, cancellationToken);
            if (shorter.Any(c => c.Tracks > 1)) { candidates = shorter; relaxed = true; }
        }

        List<CdSplitCandidate> ranked =
            [.. candidates.OrderByDescending(c => c.HighestDb - c.LowestDb).ThenBy(c => c.LowestDb)];
        return new CdSplitSweep(ranked, Choose(ranked, targetTracks),
            LowestSilenceThresholdDb, HighestSilenceThresholdDb, relaxed);
    }

    /// <summary>Every run of thresholds that agrees about where the tracks are.</summary>
    private static List<CdSplitCandidate> Plateaus(
        float[] envelope, int length, int sampleRate, double minimumGapSeconds,
        CancellationToken cancellationToken)
    {
        int minimumTrack = MinimumTrackSamples(AutoSplitMinimumTrackSeconds, sampleRate);
        int tolerance = Math.Max(1, (int)Math.Round(PlateauToleranceSeconds * sampleRate));
        var result = new List<CdSplitCandidate>();

        // Every answer in the run is kept, because the one that ships is the one at the *chosen*
        // setting rather than the one at the edge the run started from. Within a run they differ by
        // less than the tolerance, but not by nothing — and the slider is left at the chosen
        // setting, so pressing Analyze straight afterwards has to re-derive exactly this list.
        var run = new List<List<int>>();
        double low = 0, high = 0;
        void Close()
        {
            if (run.Count == 0) return;
            // The middle of the run is the point furthest from where the answer changes.
            double chosen = Math.Round((low + high) / 2, MidpointRounding.AwayFromZero);
            List<int> boundaries = run[(int)(chosen - low)];
            result.Add(new CdSplitCandidate(
                boundaries.Count - 1, low, high, chosen, minimumGapSeconds, boundaries));
            run.Clear();
        }

        for (double db = LowestSilenceThresholdDb; db <= HighestSilenceThresholdDb; db++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var silences = Restoration.DetectSilences(
                envelope, length, sampleRate, db, minimumGapSeconds * 1000);
            List<int> boundaries = BoundariesFrom(silences, length, minimumTrack, cancellationToken);

            // Compared against the run's *first* answer rather than the previous one, so a slow
            // drift over many settings cannot creep past the tolerance a step at a time.
            if (run.Count > 0 && Agrees(run[0], boundaries, tolerance))
            {
                run.Add(boundaries);
                high = db;
                continue;
            }
            Close();
            run.Add(boundaries);
            low = high = db;
        }
        Close();
        return result;
    }

    private static bool Agrees(List<int> held, List<int> found, int tolerance)
    {
        if (held.Count != found.Count) return false;
        for (int i = 0; i < held.Count; i++)
            if (Math.Abs(held[i] - found[i]) > tolerance) return false;
        return true;
    }

    private static CdSplitCandidate? Choose(List<CdSplitCandidate> ranked, int? targetTracks)
    {
        if (ranked.Count == 0) return null;
        if (targetTracks is int want) return ranked.FirstOrDefault(c => c.Tracks == want);

        return ranked.FirstOrDefault(c => c.Tracks > 1 && c.HighestDb - c.LowestDb >= MinimumPlateauDb)
               ?? ranked.FirstOrDefault(c => c.Tracks == 1)
               ?? ranked[0];
    }

    /// <summary>The plans a chosen candidate stands for.</summary>
    public static List<CdTrackPlan> PlansFor(CdSplitCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return PlansFrom(candidate.Boundaries);
    }

    /// <summary>
    /// What the sweep found, for the line under the track list. Pure, so the wording is tested
    /// without a window, and in the same voice as <see cref="DescribeProposal"/>.
    /// </summary>
    public static string DescribeSweep(CdSplitSweep sweep, int? targetTracks)
    {
        ArgumentNullException.ThrowIfNull(sweep);

        if (targetTracks is int want && sweep.Best == null)
        {
            int[] reachable = [.. sweep.Candidates.Select(c => c.Tracks).Distinct().Order()];
            return reachable.Length == 0
                ? NoGaps
                : $"This side splits into {Counts(reachable)} tracks, never {want}. " +
                  $"Add the missing ones with Split - tracks under {AutoSplitMinimumTrackSeconds:0} s are merged.";
        }

        if (sweep.Best is not { } best || best.Tracks <= 1) return NoGaps;

        if (sweep.GapRelaxed)
            return $"Found {Tracks(best.Tracks)} at {Db(best.ChosenDb)}, once the shortest gap it " +
                   $"looks for was relaxed to {best.MinimumGapSeconds:0.0} s.";

        string found = best.LowestDb < best.HighestDb
            ? $"Found {Tracks(best.Tracks)} - steady from {best.LowestDb:0} to {Db(best.HighestDb)}."
            : $"Found {Tracks(best.Tracks)} at {Db(best.ChosenDb)}.";

        CdSplitCandidate? other = sweep.Candidates
            .FirstOrDefault(c => c.Tracks > 1 && c.Tracks != best.Tracks);
        return other != null
            ? $"{found} There is also a {other.Tracks}-track answer near {Db(other.ChosenDb)}."
            : $"{found} Preview each one to check where it starts.";
    }

    private const string NoGaps =
        "No gaps found at any setting. The songs may run together, or the quiet between them may be too short.";

    private static string Db(double value) => $"{value:0} dB";

    private static string Counts(IReadOnlyList<int> values) => values.Count switch
    {
        1 => $"{values[0]}",
        _ => string.Join(", ", values.Take(values.Count - 1)) + $" or {values[^1]}",
    };

    /// <summary>
    /// What an analysis pass did, for the line under the track list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported twice, and the second report is the one that settled the shape of this: the line
    /// used to read "Found 3 probable tracks at −45 dB", which is a count and the setting that
    /// produced it — and the setting is printed beside the slider that set it, so the only new fact
    /// was a count that had not moved. It said nothing a person could act on.
    /// </para>
    /// <para>
    /// The count staying the same does not mean nothing happened. Measured on three real transfers
    /// butted into one 504 s side, −45 dB and −30 dB both propose three tracks with the splits
    /// <b>7.6 s apart</b>: a split is the midpoint of a detected gap, so a looser threshold calls
    /// the fade-out quiet sooner and the split lands inside the music. That is the fact worth
    /// reporting, and it is reported in seconds and in plain words rather than as a boundary diff.
    /// </para>
    /// <para>
    /// Every line names the next thing to do, because a status line arrives once and is read by
    /// somebody who has not read this file. No decibel figure appears in any of them — the slider
    /// prints its own — and nothing is called a boundary. Pure, so the wording is tested without a
    /// window, the arrangement <c>DescribeDeclipChoices</c> and <c>DescribeNoiseDepth</c> use.
    /// </para>
    /// </remarks>
    /// <param name="proposed">Tracks this pass proposes.</param>
    /// <param name="previous">Tracks listed before it ran; zero when the list was empty.</param>
    /// <param name="worstMoveSeconds">
    /// How far the split that moved furthest moved, when the count did not change — <b>signed</b>,
    /// negative for earlier in the recording. <see cref="double.NaN"/> when the counts differ and
    /// there is no split-to-split comparison to make.
    /// </param>
    public static string DescribeProposal(int proposed, int previous, double worstMoveSeconds)
    {
        if (proposed <= 0) return "No tracks were proposed.";

        // One track is one track however many there were before: there is nothing to rename,
        // reorder or preview, so the line spends its room on the way out rather than on a count the
        // collapsed list has already shown. Guarding this on the previous count sent a side that
        // dropped from three tracks to one away with "Preview each one to check where it starts".
        if (proposed == 1)
            return "No gaps found - this is all one track. " +
                   "Drag Quiet below to the right, then Analyze again.";

        if (previous == proposed)
        {
            if (!double.IsFinite(worstMoveSeconds) || worstMoveSeconds == 0)
                return $"Same {Tracks(proposed)}, in the same places.";

            // Earlier eats the end of the song before the gap; later eats the start of the one
            // after it. Both are the same fault from opposite sides, and naming which one it is
            // tells the listener where to listen.
            bool earlier = worstMoveSeconds < 0;
            return $"Still {Tracks(proposed)}, but the splits moved up to " +
                   $"{Span(Math.Abs(worstMoveSeconds))} {(earlier ? "earlier" : "later")} and may now cut into the " +
                   $"{(earlier ? "end" : "start")} of a song. Preview them to check.";
        }

        if (previous > 0)
            return $"Now {Tracks(proposed)} - there {(previous == 1 ? "was" : "were")} {previous}. " +
                   "Preview each one to check where it starts.";

        return $"{Tracks(proposed)} found. Select one and press Preview Track to hear where it starts.";
    }

    private static string Tracks(int count) => count == 1 ? "1 track" : $"{count} tracks";

    private static string Span(double seconds) =>
        seconds < 60 ? $"{seconds:0.0} s" : TimeFormat.Compact(seconds);

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
            issues.Add(new(CdPlanIssueSeverity.Error, $"A CD holds at most {MaximumTracks} tracks."));

        long totalFrames = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (track.SourceStart < 0 || track.SourceEnd > documentLength || track.SourceEnd <= track.SourceStart)
            {
                issues.Add(new(CdPlanIssueSeverity.Error,
                    $"Track {i + 1:00} covers no audio - check its SOURCE IN and SOURCE OUT."));
                continue;
            }

            int start = MapBoundary(track.SourceStart, sampleRate, documentLength, isEnd: false);
            int end = MapBoundary(track.SourceEnd, sampleRate, documentLength, isEnd: true);
            int frames = Math.Max(0, end - start);
            totalFrames += frames;
            if (i > 0) totalFrames += track.PregapSectors * CdAudioFormat.FramesPerSector;
            double duration = frames / (double)CdSampleRate;
            if (duration < MinimumTrackSeconds)
                issues.Add(new(CdPlanIssueSeverity.Error,
                    $"Track {i + 1:00} comes out {duration:0.0} s long on the disc. " +
                    $"A CD track has to run for at least {MinimumTrackSeconds:0} seconds."));
            if (string.IsNullOrWhiteSpace(track.Title))
                issues.Add(new(CdPlanIssueSeverity.Warning, $"Track {i + 1:00} has no title."));
        }

        double total = totalFrames / (double)CdSampleRate;
        if (total > MaximumDurationSeconds)
            issues.Add(new(CdPlanIssueSeverity.Error,
                $"These tracks run {FormatDuration(total)} on the disc. A CD holds at most " +
                $"{FormatDuration(MaximumDurationSeconds)} - shorten one or take one out."));
        else if (total > 74 * 60)
            issues.Add(new(CdPlanIssueSeverity.Warning,
                $"These tracks run {FormatDuration(total)} on the disc. Check your blank discs hold more than 74 minutes."));
        else
            issues.Add(new(CdPlanIssueSeverity.Information,
                $"{(tracks.Count == 1 ? "1 track" : $"{tracks.Count} tracks")}, {FormatDuration(total)} on the disc."));
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
        string? discPerformer = null,
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
        // Blank stays blank rather than becoming something invented, which is the same rule the PQ
        // sheet already states about a track's performer: a deliverable is read as fact.
        string performer = discPerformer?.Trim() ?? string.Empty;

        return Task.Run(() => ExportPackage(snapshot, plans, folder, title, performer, progress, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// The track's audio with its pregap ahead of it. The silence is real samples rather than a
    /// note in the sheet, because a DDP image has to carry it and both deliverables are cut from
    /// the same programme — a gap that existed in one and not the other would be two different
    /// discs described by one window.
    /// </summary>
    private static float[][] WithPregap(float[][] audio, int pregapSamples)
    {
        if (pregapSamples <= 0 || audio.Length == 0) return audio;
        var padded = new float[audio.Length][];
        for (int c = 0; c < audio.Length; c++)
        {
            padded[c] = new float[pregapSamples + audio[c].Length];
            Array.Copy(audio[c], 0, padded[c], pregapSamples, audio[c].Length);
        }
        return padded;
    }

    private static CdPackageResult ExportPackage(
        SourceSnapshot source,
        IReadOnlyList<CdTrackPlan> orderedTracks,
        string outputFolder,
        string discTitle,
        string discPerformer,
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
            float[][] continuous = PrepareContinuous(source, prepared.Count, progress, cancellationToken);

            // Metadata alone cannot prove that a nominally 16-bit document is
            // still on the signed-16 PCM grid (a generated tab or Save As can
            // retain/inherit that metadata). Inspect the final CD-rate stereo
            // program instead: bit-exact samples need no dither; every
            // mathematically processed value does.
            bool dither = !IsExact16BitPcm(continuous, cancellationToken);
            var cue = new StringBuilder();
            cue.AppendLine($"TITLE \"{CueEscape(discTitle)}\"");
            // The dialog's DISC PERFORMER field is what belongs here. A fixed "Deep Groove Transfer"
            // shipped for a while, which credited the application on every disc burned from a cue
            // sheet it wrote — a statement about the release that nobody had made.
            if (discPerformer.Length > 0)
                cue.AppendLine($"PERFORMER \"{CueEscape(discPerformer)}\"");

            for (int i = 0; i < prepared.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = prepared[i];
                double trackBase = 0.2 + 0.75 * i / Math.Max(1, prepared.Count);
                progress?.Report(new CdPackageProgress(i, prepared.Count, track.Title, trackBase));

                float[][] data = WithPregap(
                    CopyRange(continuous, track.Start, track.Length, cancellationToken), track.PregapSamples);
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
                if (!string.IsNullOrWhiteSpace(track.Plan.Performer))
                    cue.AppendLine($"    PERFORMER \"{CueEscape(track.Plan.Performer.Trim())}\"");
                // INDEX 00 is where the pregap starts and INDEX 01 where the music does, which is
                // what lets a player count the gap down in continuous listening and skip it when
                // the track is chosen. Written only when there is a gap: a lone INDEX 00 at the
                // same place as INDEX 01 is noise in the sheet.
                if (track.PregapFrames > 0) cue.AppendLine("    INDEX 00 00:00:00");
                cue.AppendLine($"    INDEX 01 {DdpImage.Timecode(track.PregapFrames)}");
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

    /// <summary>
    /// Where each track lands on the disc once the plan is sector-aligned: the timing a PQ sheet
    /// states, and the only honest thing to show a user arranging a DDP.
    /// </summary>
    /// <remarks>
    /// Offsets run from the two-second lead-in, because that is where the plant's timeline begins.
    /// A sheet that starts at 00:00:00 puts every track two seconds early. The lengths are already
    /// whole CD frames — that is what the sector alignment is for — so the padding the image writer
    /// applies is a no-op here and the two agree by construction.
    /// </remarks>
    public static CdPqLayout PqSheet(
        IReadOnlyList<CdTrackPlan> tracks, int sampleRate, int documentLength)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (sampleRate <= 0) return new CdPqLayout([], LeadInFrames);

        var entries = new List<CdPqEntry>(tracks.Count);
        int frame = LeadInFrames;
        foreach (PreparedTrack track in PrepareTrackBoundaries(tracks, sampleRate, documentLength))
        {
            // The pregap sits before the track and is counted in the timeline, but a track's
            // stated length is what plays when it is chosen — which begins at INDEX 01.
            frame += track.PregapFrames;
            int length = track.Length / CdAudioFormat.FramesPerSector;
            entries.Add(new CdPqEntry(entries.Count + 1, frame, length));
            frame += length;
        }
        return new CdPqLayout(entries, frame);
    }

    /// <summary>Frames of lead-in before the first track — the same two seconds the image writes.</summary>
    public const int LeadInFrames = DdpImage.LeadInFrames;

    /// <summary>
    /// The one continuous CD-rate stereo programme both deliverables are cut from. Resampling once
    /// and cutting afterwards is what keeps a gapless transition gapless: converting each track on
    /// its own would give every boundary its own filter transient.
    /// </summary>
    private static float[][] PrepareContinuous(SourceSnapshot source, int trackCount,
        IProgress<CdPackageProgress>? progress, CancellationToken cancellationToken)
    {
        float[][] continuous = ToStereo(source.Channels, cancellationToken);
        if (source.SampleRate == CdSampleRate) return continuous;

        progress?.Report(new CdPackageProgress(0, trackCount, "Converting continuous master", 0));
        var srcProgress = progress == null ? null : new CallbackProgress<double>(fraction =>
            progress.Report(new CdPackageProgress(0, trackCount,
                "Converting continuous master", fraction * 0.2)));
        return Resampler.Resample(continuous, source.SampleRate, CdSampleRate,
            cancellationToken, srcProgress);
    }

    // ── DDP ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes the same programme as a DDP 2.00 image set: what a pressing plant takes, where the
    /// WAV+CUE package is what a duplicator takes.
    /// </summary>
    /// <remarks>
    /// Snapshot rules are the WAV path's, for the same reason: the channel arrays are captured on
    /// the caller's thread before any background work starts, because an edit replaces those arrays
    /// rather than mutating them and a package must not span two versions of the document.
    /// </remarks>
    public static Task<DdpResult> ExportDdpAsync(
        AudioDocument document,
        IReadOnlyList<CdTrackPlan> orderedTracks,
        string outputFolder,
        DdpDiscInfo disc,
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

        return Task.Run(() => ExportDdp(snapshot, plans, folder, disc, progress, cancellationToken),
            cancellationToken);
    }

    private static DdpResult ExportDdp(
        SourceSnapshot source,
        IReadOnlyList<CdTrackPlan> orderedTracks,
        string outputFolder,
        DdpDiscInfo disc,
        IProgress<CdPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        var issues = Validate(orderedTracks, source.SampleRate, source.Length);
        var errors = issues.Where(i => i.Severity == CdPlanIssueSeverity.Error).Select(i => i.Message).ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputFolder);
        if (Directory.EnumerateFileSystemEntries(outputFolder).Any())
            throw new IOException("The DDP folder must be empty. Choose a new or empty folder so existing files cannot be overwritten.");

        var prepared = PrepareTrackBoundaries(orderedTracks, source.SampleRate, source.Length);
        string stageFolder = Path.Combine(outputFolder, ".wavelab-ddp-" + Guid.NewGuid().ToString("N"));
        var published = new List<string>();
        try
        {
            float[][] continuous = PrepareContinuous(source, prepared.Count, progress, cancellationToken);

            var audio = new List<float[][]>(prepared.Count);
            var info = new List<DdpTrackInfo>(prepared.Count);
            for (int i = 0; i < prepared.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new CdPackageProgress(i, prepared.Count, prepared[i].Title,
                    0.2 + 0.5 * i / Math.Max(1, prepared.Count)));

                audio.Add(WithPregap(
                    CopyRange(continuous, prepared[i].Start, prepared[i].Length, cancellationToken),
                    prepared[i].PregapSamples));
                CdTrackPlan plan = prepared[i].Plan;
                info.Add(new DdpTrackInfo(prepared[i].Title, plan.Performer, plan.Songwriter,
                    plan.Isrc, plan.PreEmphasis, prepared[i].PregapFrames));
            }

            Directory.CreateDirectory(stageFolder);
            var imageProgress = progress == null ? null : new CallbackProgress<double>(fraction =>
                progress.Report(new CdPackageProgress(prepared.Count, prepared.Count, "Writing the image",
                    0.7 + 0.3 * fraction)));
            DdpResult staged = DdpImage.Write(stageFolder, audio, info, disc, CdSampleRate,
                cancellationToken, imageProgress);

            // Nothing takes its final name until every file is complete, so an interrupted export
            // cannot leave a folder that looks like a deliverable.
            foreach (string file in staged.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(outputFolder, Path.GetFileName(file));
                File.Move(file, destination, overwrite: false);
                published.Add(destination);
            }

            progress?.Report(new CdPackageProgress(prepared.Count, prepared.Count, "Complete", 1));
            return staged with { Folder = outputFolder, Files = published };
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
            // Track 01 never carries one however the plan is written: the two-second lead-in every
            // disc begins with already is its pregap.
            int pregap = i == 0 ? 0 : plan.PregapSectors * CdAudioFormat.FramesPerSector;
            result.Add(new PreparedTrack(plan, title, start, end, pregap));
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

    /// <summary>
    /// Windows resolves these as DOS devices no matter which directory or
    /// extension is used, so a file named after one is never actually created.
    /// </summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static string SafeName(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Audio CD" : value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        value = value.TrimEnd('.', ' ');
        if (value.Length > 96) value = value[..96].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(value)) return "Track";

        // "NUL.cue" addresses the null device exactly as "NUL" does, and callers
        // append their own extension, so the reserved test uses the base name.
        int dot = value.IndexOf('.');
        string baseName = (dot >= 0 ? value[..dot] : value).TrimEnd(' ');
        if (ReservedDeviceNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
            value = "_" + value;
        return value;
    }

    private static string CueEscape(string? value)
    {
        // Titles are free-form user text: a quote breaks the enclosing cue string
        // and any control character (a newline above all) injects whole commands
        // into the sheet, describing a track layout that was never written.
        string text = string.IsNullOrWhiteSpace(value) ? "Audio CD" : value;
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
            builder.Append(c == '"' ? '\'' : char.IsControl(c) ? ' ' : c);
        return builder.ToString().Trim();
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
