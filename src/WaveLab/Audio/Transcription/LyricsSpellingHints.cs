using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WaveLab.Audio.Transcription;

/// <summary>Reviewable text suggestions, never changes to recognition evidence or timing.</summary>
internal static class LyricsSpellingHints
{
    private static readonly Regex Word = new(@"\p{L}[\p{L}\p{M}]*(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.CultureInvariant);

    public static string? Suggest(string text, string hints)
    {
        // A phrase still goes to the recognizer, but must not turn its individual
        // words into independent replacement rules (for example, New York).
        var spellings = hints.Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(h => h.Length <= 64 && Word.Match(h) is { Success: true } match && match.Length == h.Length)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => (Text: h, Key: Key(h))).ToArray();
        if (spellings.Length == 0) return null;

        string suggestion = Word.Replace(text, match =>
        {
            string token = match.Value;
            string suffix = token.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith("’s", StringComparison.OrdinalIgnoreCase) ? token[^2..] : "";
            string stem = suffix.Length == 0 ? token : token[..^2];
            if (stem.Length > 64) return token;
            string key = Key(stem);
            var exact = spellings.Where(h => h.Key == key).ToArray();
            var matches = exact.Length > 0 ? exact : spellings.Where(h => Similar(key, h.Key)).ToArray();
            // Never resolve competing hints by list order or guess between them.
            return matches.Length == 1 ? matches[0].Text + suffix : token;
        });
        return suggestion == text ? null : suggestion;
    }

    private static string Key(string word) => string.Concat(word.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        .ToLowerInvariant().Replace('’', '\'');

    private static bool Similar(string word, string hint)
    {
        if (word.Length < 4 || hint.Length < 4 || Math.Abs(word.Length - hint.Length) > 2) return false;
        // Require substantial shared spelling; the opening letters may differ.
        // This deliberately offers candidates for review, not automatic corrections:
        // audio alone cannot distinguish a name such as Roanne from the word rowan.
        var common = new int[hint.Length + 1];
        foreach (char letter in word)
        {
            int diagonal = 0;
            for (int j = 1; j <= hint.Length; j++)
            {
                int previous = common[j];
                common[j] = letter == hint[j - 1] ? diagonal + 1 : Math.Max(common[j], common[j - 1]);
                diagonal = previous;
            }
        }
        return 2.0 * common[^1] / (word.Length + hint.Length) >= .72;
    }
}
