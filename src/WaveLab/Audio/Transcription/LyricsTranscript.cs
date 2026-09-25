using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using WaveLab.Util;

namespace WaveLab.Audio.Transcription;

public sealed record LyricsWord(double Start, double End, string Word, double Probability);

public sealed class LyricsLine : ObservableObject
{
    private string _text = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool NeedsReview { get; set; }
    public bool Recovered { get; set; }
    public string RecoveryNote { get; set; } = "";
    public string ModelText { get; set; } = "";
    public string AlternativeText { get; set; } = "";
    public List<LyricsWord> Words { get; set; } = [];
    public string TimeLabel => TimeSpan.FromSeconds(Start).ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
    public string ReviewLabel => Recovered ? "Recovered" : NeedsReview ? "Review" : "";
}

public sealed class LyricsTranscript
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceTitle { get; set; } = "";
    public int SourceEditVersion { get; set; }
    public double RangeStart { get; set; }
    public double RangeEnd { get; set; }
    public string Language { get; set; } = "";
    public string Model { get; set; } = "";
    public string Device { get; set; } = "";
    public bool IsolatedVocals { get; set; }
    public List<LyricsLine> Lines { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>Validate the worker's relative times before mapping them to the document timeline.</summary>
    public void MapToSource(double offset, double duration)
    {
        if (!double.IsFinite(offset) || offset < 0 || !double.IsFinite(duration) || duration <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (SchemaVersion != 1 || Lines == null || Lines.Count > 100_000)
            throw new InvalidDataException("The transcription engine returned an unsupported transcript.");
        double previous = 0;
        foreach (var line in Lines)
        {
            if (line == null || !double.IsFinite(line.Start) || !double.IsFinite(line.End)
                || line.Start < previous || line.Start < 0 || line.End <= line.Start
                || line.End > duration + 0.25 || line.Text == null || line.Words == null)
                throw new InvalidDataException("The transcription engine returned invalid line timings.");
            previous = line.Start;
            line.End = Math.Min(duration, line.End);
            if (line.Start >= line.End) throw new InvalidDataException("A lyric line is outside the audio range.");
            foreach (var word in line.Words)
                if (word == null || !double.IsFinite(word.Start) || !double.IsFinite(word.End)
                    || word.Start < line.Start - 0.25 || word.End > line.End + 0.25
                    || word.End < word.Start || !double.IsFinite(word.Probability)
                    || word.Probability is < 0 or > 1)
                    throw new InvalidDataException("The transcription engine returned invalid word timings.");
            line.Words = line.Words.Select(w => w with { Start = w.Start + offset, End = w.End + offset }).ToList();
            line.Start += offset;
            line.End += offset;
        }
        RangeStart = offset;
        RangeEnd = offset + duration;
    }

    public string Export(string extension)
    {
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(this, JsonOptions);
        var text = new StringBuilder();
        int index = 0;
        foreach (var line in Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)))
        {
            // A line edit may include newlines; do not let it insert fake subtitle cues.
            string content = string.Join(" ", line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            switch (extension.ToLowerInvariant())
            {
                case ".srt":
                    text.AppendLine((++index).ToString(CultureInfo.InvariantCulture));
                    text.AppendLine($"{SrtTime(line.Start)} --> {SrtTime(line.End)}");
                    text.AppendLine(content).AppendLine();
                    break;
                case ".lrc":
                    long ticks = (long)Math.Round(line.Start * 100, MidpointRounding.AwayFromZero);
                    text.Append(CultureInfo.InvariantCulture, $"[{ticks / 6000:00}:{ticks / 100 % 60:00}.{ticks % 100:00}]");
                    text.AppendLine(content);
                    break;
                case ".txt": text.AppendLine(content); break;
                default: throw new ArgumentException("Choose TXT, LRC, SRT, or JSON.", nameof(extension));
            }
        }
        return text.ToString();
    }

    private static string SrtTime(double seconds)
    {
        long ms = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }
}
