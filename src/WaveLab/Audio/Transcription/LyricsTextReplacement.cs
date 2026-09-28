using System.Text.RegularExpressions;

namespace WaveLab.Audio.Transcription;

internal static class LyricsTextReplacement
{
    internal sealed record Change(LyricsLine Line, string Before, string After, int Matches, string? PreviousSavedText)
    {
        public string Time => Line.TimeLabel;
        public string AfterPreview => After.Length == 0 ? "(empty line)" : After;
    }

    public static IReadOnlyList<Change> Preview(IEnumerable<LyricsLine> lines, string find, string replacement,
        bool matchCase = false, bool wholeWords = true)
    {
        if (string.IsNullOrWhiteSpace(find)) return [];
        // Keep surrounding whitespace as required context too. Trimming it would
        // broaden the search and duplicate padding when a whole line is prefilled.
        string pattern = string.Join(@"\s+", Regex.Split(find, @"\s+").Select(Regex.Escape));
        if (wholeWords)
        {
            const string word = @"[\p{L}\p{M}\p{N}_]";
            // A boundary cannot split adjacent word characters or either side of
            // an internal apostrophe. Quotation marks and explicitly entered spaces
            // remain valid boundaries. Keep the checks in the regex so rejecting an
            // overlapping candidate does not skip a later valid phrase match.
            string betweenLetters = $@"(?!(?<={word}){word})";
            string beforeApostrophe = $@"(?!(?<={word})['’]{word})";
            string afterApostrophe = $@"(?!(?<={word}['’]){word})";
            string boundary = betweenLetters + beforeApostrophe + afterApostrophe;
            pattern = boundary + pattern + boundary;
        }
        var regex = new Regex(pattern, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase));
        var changes = new List<Change>();
        foreach (var line in lines)
        {
            int count = 0;
            string after = regex.Replace(line.Text, match =>
            {
                if (match.Value != replacement) count++;
                return replacement;
            });
            if (after != line.Text)
                changes.Add(new(line, line.Text, after, count, line.PreviousRetryText));
        }
        return changes;
    }

    public static bool Apply(IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0 || changes.Any(c => c.Line.Text != c.Before)) return false;
        foreach (var change in changes)
        {
            change.Line.PreviousRetryText = change.Before;
            change.Line.Text = change.After;
        }
        return true;
    }

    public static bool CanUndo(IReadOnlyList<Change> changes) => changes.Count > 0
        && changes.All(c => c.Line.Text == c.After && c.Line.PreviousRetryText == c.Before);

    public static bool Undo(IReadOnlyList<Change> changes)
    {
        // A later manual edit or accepted reading must not be erased by an old undo.
        if (!CanUndo(changes)) return false;
        foreach (var change in changes)
        {
            change.Line.Text = change.Before;
            change.Line.PreviousRetryText = change.PreviousSavedText;
        }
        return true;
    }
}
