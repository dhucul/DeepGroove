using System.IO;
using System.Text;

namespace WaveLab.Audio;

/// <summary>What an MP3 says about itself.</summary>
/// <param name="Title">TIT2.</param>
/// <param name="Artist">TPE1.</param>
/// <param name="Album">TALB.</param>
/// <param name="AlbumArtist">TPE2.</param>
/// <param name="Composer">TCOM.</param>
/// <param name="Genre">TCON.</param>
/// <param name="Year">TDRC — a year, or a fuller ISO 8601 date.</param>
/// <param name="Track">TRCK, either <c>3</c> or <c>3/12</c>.</param>
/// <param name="Comment">COMM.</param>
public readonly record struct Id3Tags(
    string Title = "",
    string Artist = "",
    string Album = "",
    string AlbumArtist = "",
    string Composer = "",
    string Genre = "",
    string Year = "",
    string Track = "",
    string Comment = "")
{
    /// <summary>Whether there is anything here worth writing.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Artist) &&
        string.IsNullOrWhiteSpace(Album) && string.IsNullOrWhiteSpace(AlbumArtist) &&
        string.IsNullOrWhiteSpace(Composer) && string.IsNullOrWhiteSpace(Genre) &&
        string.IsNullOrWhiteSpace(Year) && string.IsNullOrWhiteSpace(Track) &&
        string.IsNullOrWhiteSpace(Comment);
}

/// <summary>
/// Writes an ID3v2.4 tag and prepends it to an encoded MP3.
/// </summary>
/// <remarks>
/// <para>
/// The tag goes at the <b>front</b> of the file, before the first MPEG frame, and a decoder skips it
/// by the length the header states. That length is a <b>synchsafe</b> integer: seven bits per byte,
/// with the top bit of each always clear, so no part of a tag can ever look like the eleven set bits
/// an MPEG frame starts with. Writing a plain 32-bit length there is the classic way to produce a
/// file that plays but whose tag is invisible, or worse, a file whose length field is mistaken for
/// audio.
/// </para>
/// <para>
/// Text is UTF-8, which is what version 2.4 added and is the reason to write 2.4 rather than 2.3:
/// 2.3's options were Latin-1 or UTF-16 with a byte-order mark, and Latin-1 cannot spell most of
/// the names a record has on it.
/// </para>
/// </remarks>
public static class Id3v2
{
    /// <summary>The fixed header, which is not counted in the size the header states.</summary>
    public const int HeaderBytes = 10;

    /// <summary>UTF-8, the encoding byte every text frame here begins with.</summary>
    private const byte Utf8 = 0x03;

    /// <summary>Builds a complete ID3v2.4 tag, header included. Empty tags produce no bytes.</summary>
    public static byte[] Write(Id3Tags tags)
    {
        if (tags.IsEmpty) return [];

        using var frames = new MemoryStream();
        WriteTextFrame(frames, "TIT2", tags.Title);
        WriteTextFrame(frames, "TPE1", tags.Artist);
        WriteTextFrame(frames, "TALB", tags.Album);
        WriteTextFrame(frames, "TPE2", tags.AlbumArtist);
        WriteTextFrame(frames, "TCOM", tags.Composer);
        WriteTextFrame(frames, "TCON", tags.Genre);
        WriteTextFrame(frames, "TDRC", tags.Year);
        WriteTextFrame(frames, "TRCK", tags.Track);
        WriteCommentFrame(frames, tags.Comment);

        byte[] body = frames.ToArray();
        var tag = new byte[HeaderBytes + body.Length];
        tag[0] = (byte)'I';
        tag[1] = (byte)'D';
        tag[2] = (byte)'3';
        tag[3] = 4;                                  // version 2.4
        tag[4] = 0;                                  // revision
        tag[5] = 0;                                  // no unsynchronisation, no extended header
        WriteSynchsafe(tag, 6, body.Length);
        body.CopyTo(tag, HeaderBytes);
        return tag;
    }

    /// <summary>
    /// Rewrites an MP3 with this tag at the front, replacing any tag the encoder left there.
    /// </summary>
    /// <remarks>
    /// Two tags at the front of a file is not an error a player is obliged to recover from — the
    /// first one's length carries it over the second, so the second becomes audio as far as the
    /// decoder is concerned. Whatever was there is removed rather than written around.
    /// </remarks>
    public static void ApplyTo(string path, Id3Tags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] tag = Write(tags);

        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("The MP3 path has no directory.");
        string stagePath = Path.Combine(directory,
            $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long audioStart = ExistingTagLength(source);
                if (tag.Length == 0 && audioStart == 0) return;   // nothing to add, nothing to strip

                using var stage = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1 << 16, FileOptions.SequentialScan);
                if (tag.Length > 0) stage.Write(tag, 0, tag.Length);
                source.Position = audioStart;
                source.CopyTo(stage, 1 << 16);
                stage.Flush(flushToDisk: true);
            }

            File.Move(stagePath, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(stagePath); } catch { }
            throw;
        }
    }

    /// <summary>How many bytes of ID3v2 tag sit at the front of this stream, if any.</summary>
    public static long ExistingTagLength(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length < HeaderBytes) return 0;

        long entry = stream.Position;
        stream.Position = 0;
        var header = new byte[HeaderBytes];
        int read = stream.Read(header, 0, HeaderBytes);
        stream.Position = entry;
        if (read < HeaderBytes) return 0;
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return 0;

        // A synchsafe size never has the top bit of a byte set; one that does is not a tag header.
        for (int i = 6; i < 10; i++) if ((header[i] & 0x80) != 0) return 0;

        long size = ReadSynchsafe(header, 6);
        bool footer = (header[5] & 0x10) != 0;      // 2.4 may repeat the header at the end
        long total = HeaderBytes + size + (footer ? HeaderBytes : 0);
        return total <= stream.Length ? total : 0;
    }

    // ── frames ───────────────────────────────────────────────────

    private static void WriteTextFrame(Stream stream, string id, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        byte[] text = Encoding.UTF8.GetBytes(value.Trim());
        WriteFrameHeader(stream, id, 1 + text.Length);
        stream.WriteByte(Utf8);
        stream.Write(text, 0, text.Length);
    }

    /// <summary>
    /// COMM carries a language and a short description before its text, and both are part of its
    /// identity: two comments differing only in description are two comments, not one overwritten.
    /// </summary>
    private static void WriteCommentFrame(Stream stream, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        byte[] text = Encoding.UTF8.GetBytes(value.Trim());
        WriteFrameHeader(stream, "COMM", 1 + 3 + 1 + text.Length);
        stream.WriteByte(Utf8);
        stream.Write("eng"u8);
        stream.WriteByte(0);                        // empty description, terminated
        stream.Write(text, 0, text.Length);
    }

    private static void WriteFrameHeader(Stream stream, string id, int size)
    {
        stream.Write(Encoding.ASCII.GetBytes(id));

        // Frame sizes are synchsafe in 2.4 as well — they were plain 32-bit in 2.3, and a reader
        // that assumes the older form reads every frame past the first at the wrong offset.
        var size4 = new byte[4];
        WriteSynchsafe(size4, 0, size);
        stream.Write(size4, 0, 4);
        stream.WriteByte(0);                        // status flags
        stream.WriteByte(0);                        // format flags
    }

    // ── synchsafe integers ───────────────────────────────────────

    /// <summary>Writes a 28-bit value as four bytes of seven bits each, most significant first.</summary>
    internal static void WriteSynchsafe(byte[] destination, int offset, int value)
    {
        if (value is < 0 or > 0x0FFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(value), "A synchsafe integer holds 28 bits.");
        destination[offset] = (byte)((value >> 21) & 0x7F);
        destination[offset + 1] = (byte)((value >> 14) & 0x7F);
        destination[offset + 2] = (byte)((value >> 7) & 0x7F);
        destination[offset + 3] = (byte)(value & 0x7F);
    }

    internal static int ReadSynchsafe(byte[] source, int offset) =>
        (source[offset] & 0x7F) << 21 | (source[offset + 1] & 0x7F) << 14 |
        (source[offset + 2] & 0x7F) << 7 | (source[offset + 3] & 0x7F);
}
