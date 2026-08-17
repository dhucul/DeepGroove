namespace WaveLab.Audio;

/// <summary>
/// What a file says about itself, in the one vocabulary every container this app writes can express.
/// </summary>
/// <remarks>
/// <para>
/// Three formats say the same six things three different ways: a WAV in <c>LIST/INFO</c> tags, an
/// AIFF in <c>NAME</c>/<c>AUTH</c>/<c>ANNO</c>, an MP3 in ID3v2.4 frames. Keeping one model and
/// translating at the edge is what lets a title survive being saved as a WAV, exported as an MP3 and
/// opened again — which is the only reason to have tags at all.
/// </para>
/// <para>
/// AIFF is the poorest of the three: it has a title, an author and free annotation, and nowhere to
/// put an album, a track number or a genre. Those are written into the annotation rather than
/// dropped, because a lossy round trip through a weaker container should lose formatting, not facts.
/// </para>
/// </remarks>
public sealed class FileTags
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Track { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Year { get; set; } = "";
    public string Comment { get; set; } = "";

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Artist) &&
        string.IsNullOrWhiteSpace(Album) && string.IsNullOrWhiteSpace(Track) &&
        string.IsNullOrWhiteSpace(Genre) && string.IsNullOrWhiteSpace(Year) &&
        string.IsNullOrWhiteSpace(Comment);

    public FileTags Clone() => new()
    {
        Title = Title, Artist = Artist, Album = Album,
        Track = Track, Genre = Genre, Year = Year, Comment = Comment,
    };

    // ── RIFF LIST/INFO ───────────────────────────────────────────

    /// <summary>The four-character INFO keys, in the order a reader meets them.</summary>
    private const string KeyTitle = "INAM";
    private const string KeyArtist = "IART";
    private const string KeyAlbum = "IPRD";
    private const string KeyTrack = "ITRK";
    private const string KeyGenre = "IGNR";
    private const string KeyYear = "ICRD";
    private const string KeyComment = "ICMT";

    public Dictionary<string, string> ToInfoTags()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        Put(tags, KeyTitle, Title);
        Put(tags, KeyArtist, Artist);
        Put(tags, KeyAlbum, Album);
        Put(tags, KeyTrack, Track);
        Put(tags, KeyGenre, Genre);
        Put(tags, KeyYear, Year);
        Put(tags, KeyComment, Comment);
        return tags;

        static void Put(Dictionary<string, string> tags, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) tags[key] = value.Trim();
        }
    }

    public static FileTags FromInfoTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return new FileTags
        {
            Title = Get(tags, KeyTitle),
            Artist = Get(tags, KeyArtist),
            Album = Get(tags, KeyAlbum),
            Track = Get(tags, KeyTrack),
            Genre = Get(tags, KeyGenre),
            Year = Get(tags, KeyYear),
            Comment = Get(tags, KeyComment),
        };

        static string Get(IReadOnlyDictionary<string, string> tags, string key) =>
            tags.TryGetValue(key, out string? value) ? value : "";
    }

    // ── ID3v2.4 ──────────────────────────────────────────────────

    public Id3Tags ToId3() => new(
        Title: Title, Artist: Artist, Album: Album,
        Genre: Genre, Year: Year, Track: Track, Comment: Comment);

    // ── chunks ───────────────────────────────────────────────────

    /// <summary>Reads whichever of the two vocabularies this metadata set is written in.</summary>
    public static FileTags ReadFrom(RiffMetadata? riff)
    {
        if (riff == null) return new FileTags();
        if (riff.IsAiff) return ReadAiff(riff);

        return riff.FindList("INFO") is { } list
            ? FromInfoTags(BroadcastMetadata.ReadInfoList(list.Data))
            : new FileTags();
    }

    /// <summary>
    /// Writes these tags into a metadata set, replacing what was there. An empty set removes the
    /// chunk rather than writing an empty one, because a reader that finds a present-but-blank title
    /// will show a blank title instead of falling back to the file name.
    /// </summary>
    public void WriteTo(RiffMetadata riff)
    {
        ArgumentNullException.ThrowIfNull(riff);
        if (riff.IsAiff) { WriteAiff(riff); return; }

        // By list type: the cue-point labels are a LIST too, and a file may carry both.
        if (IsEmpty) { riff.RemoveList("INFO"); return; }
        riff.SetList("INFO", BroadcastMetadata.WriteInfoList(ToInfoTags()));
    }

    private static FileTags ReadAiff(RiffMetadata riff)
    {
        var tags = new FileTags
        {
            Title = AiffMetadata.ReadTextChunk(riff.Find("NAME")?.Data),
            Artist = AiffMetadata.ReadTextChunk(riff.Find("AUTH")?.Data),
        };

        // The extras were folded into the annotation on the way out; unfold them on the way back.
        string annotation = AiffMetadata.ReadTextChunk(riff.Find("ANNO")?.Data);
        foreach (string line in annotation.Split('\n'))
        {
            string trimmed = line.Trim();
            if (TryTake(trimmed, "Album: ", out string album)) tags.Album = album;
            else if (TryTake(trimmed, "Track: ", out string track)) tags.Track = track;
            else if (TryTake(trimmed, "Genre: ", out string genre)) tags.Genre = genre;
            else if (TryTake(trimmed, "Year: ", out string year)) tags.Year = year;
            else if (trimmed.Length > 0)
                tags.Comment = tags.Comment.Length == 0 ? trimmed : tags.Comment + "\n" + trimmed;
        }
        return tags;

        static bool TryTake(string line, string prefix, out string value)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = line[prefix.Length..].Trim();
                return true;
            }
            value = "";
            return false;
        }
    }

    private void WriteAiff(RiffMetadata riff)
    {
        SetOrRemove(riff, "NAME", Title);
        SetOrRemove(riff, "AUTH", Artist);

        var annotation = new List<string>();
        if (!string.IsNullOrWhiteSpace(Album)) annotation.Add($"Album: {Album.Trim()}");
        if (!string.IsNullOrWhiteSpace(Track)) annotation.Add($"Track: {Track.Trim()}");
        if (!string.IsNullOrWhiteSpace(Genre)) annotation.Add($"Genre: {Genre.Trim()}");
        if (!string.IsNullOrWhiteSpace(Year)) annotation.Add($"Year: {Year.Trim()}");
        if (!string.IsNullOrWhiteSpace(Comment)) annotation.Add(Comment.Trim());

        SetOrRemove(riff, "ANNO", string.Join("\n", annotation));

        static void SetOrRemove(RiffMetadata riff, string id, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) riff.Remove(id);
            else riff.Set(id, AiffMetadata.WriteTextChunk(value.Trim()));
        }
    }
}
