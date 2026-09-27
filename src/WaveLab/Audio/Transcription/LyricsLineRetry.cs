namespace WaveLab.Audio.Transcription;

internal static class LyricsLineRetry
{
    internal static (int Start, int Count) Range(LyricsLine line, LyricsTranscript transcript, int rate, int length)
    {
        double lower = Math.Max(0, transcript.RangeStart);
        double upper = Math.Min((double)length / rate, transcript.RangeEnd);
        int start = Math.Clamp((int)Math.Floor(Math.Max(lower, line.Start - 3) * rate), 0, length);
        int end = Math.Clamp((int)Math.Ceiling(Math.Min(upper, line.End + 3) * rate), start, length);
        return (start, end - start);
    }

    /// <summary>Only propose words belonging to this line; surrounding phrases are context.</summary>
    internal static string Suggestion(LyricsLine target, LyricsTranscript retry, IReadOnlyList<LyricsLine> existing)
    {
        var words = retry.Lines.SelectMany(line => line.Words)
            .Where(word => word.End > word.Start
                && (word.Start + word.End) / 2 >= target.Start
                && (word.Start + word.End) / 2 < target.End
                && !existing.Any(other => !ReferenceEquals(other, target)
                    && (word.Start + word.End) / 2 >= other.Start
                    && (word.Start + word.End) / 2 < other.End))
            .OrderBy(word => word.Start).Select(word => word.Word);
        // Whisper word tokens include their language-appropriate spacing (including no spaces).
        return string.Concat(words).Trim();
    }
}
