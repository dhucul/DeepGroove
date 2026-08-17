using System.IO;
using System.Text;

namespace WaveLab.Audio;

/// <summary>
/// The AIFF chunks that carry marks and text: <c>MARK</c>, <c>COMT</c>, <c>NAME</c>, <c>AUTH</c>,
/// <c>ANNO</c> and <c>(c) </c>.
/// </summary>
/// <remarks>
/// <para>
/// AIFF is the same container shape as RIFF — a four-character id, a length, an even-padded payload
/// — with two differences that matter here. Lengths are <b>big-endian</b>, and text inside a
/// <c>MARK</c> chunk is a <b>Pascal string</b>: a leading count byte rather than a trailing zero,
/// with the count and the characters together padded to an even length. A C-string reader run over
/// a pstring reads the count as the first character and then runs to whatever zero it finds next.
/// </para>
/// <para>
/// Marks are the same information as a WAV's cue points, so they are expressed with the same type.
/// A file that arrives with either can leave with the other.
/// </para>
/// </remarks>
public static class AiffMetadata
{
    /// <summary>How many marks a single chunk may declare. Beyond this it is a damaged header.</summary>
    private const int MaximumMarkers = 65_535;

    // ── MARK ─────────────────────────────────────────────────────

    /// <summary>Builds a <c>MARK</c> chunk payload from a set of marks.</summary>
    public static byte[] WriteMarkChunk(IReadOnlyList<BroadcastMetadata.CuePoint> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        using var memory = new MemoryStream();

        int count = Math.Min(marks.Count, MaximumMarkers);
        WriteUInt16(memory, (ushort)count);
        for (int i = 0; i < count; i++)
        {
            BroadcastMetadata.CuePoint mark = marks[i];

            // Mark ids are positive by definition — zero is how a comment says it refers to no
            // mark at all — so an id that arrived as zero is renumbered rather than written.
            int id = mark.Id > 0 ? mark.Id : i + 1;
            WriteUInt16(memory, (ushort)Math.Clamp(id, 1, ushort.MaxValue));
            WriteUInt32(memory, (uint)Math.Max(0, mark.Position));
            WritePascalString(memory, mark.Label);
        }
        return memory.ToArray();
    }

    /// <summary>Reads a <c>MARK</c> chunk. A damaged one yields what could be read, not an exception.</summary>
    public static List<BroadcastMetadata.CuePoint> ReadMarkChunk(byte[]? data)
    {
        var marks = new List<BroadcastMetadata.CuePoint>();
        if (data is not { Length: >= 2 }) return marks;

        int declared = data[0] << 8 | data[1];
        int at = 2;
        for (int i = 0; i < declared; i++)
        {
            if (at + 7 > data.Length) break;        // 2 id + 4 position + at least the count byte
            int id = data[at] << 8 | data[at + 1];
            uint position = (uint)(data[at + 2] << 24 | data[at + 3] << 16 |
                                   data[at + 4] << 8 | data[at + 5]);
            at += 6;

            int length = data[at];
            if (at + 1 + length > data.Length) break;
            string label = Encoding.ASCII.GetString(data, at + 1, length);

            // The count byte and the characters are padded together, so the step is even.
            at += 1 + length;
            if (((1 + length) & 1) != 0) at++;

            marks.Add(new BroadcastMetadata.CuePoint(id,
                position > int.MaxValue ? int.MaxValue : (int)position, label));
        }
        return marks;
    }

    // ── text chunks ──────────────────────────────────────────────

    /// <summary>The text chunks an AIFF carries, in the order a reader expects to meet them.</summary>
    public static readonly string[] TextChunkIds = ["NAME", "AUTH", "(c) ", "ANNO"];

    /// <summary>
    /// An AIFF text chunk is plain characters with no terminator — its length is the chunk's length.
    /// </summary>
    public static byte[] WriteTextChunk(string? text) =>
        Encoding.ASCII.GetBytes(text ?? string.Empty);

    public static string ReadTextChunk(byte[]? data) =>
        data is { Length: > 0 } ? Encoding.ASCII.GetString(data).TrimEnd('\0') : string.Empty;

    // ── COMT ─────────────────────────────────────────────────────

    /// <summary>One comment, optionally attached to a mark.</summary>
    /// <param name="Text">The comment itself.</param>
    /// <param name="MarkerId">The mark it belongs to, or zero for a comment about the whole file.</param>
    /// <param name="TimeStamp">Seconds since 1 January 1904, the Macintosh epoch AIFF was defined in.</param>
    public readonly record struct Comment(string Text, int MarkerId = 0, uint TimeStamp = 0);

    public static byte[] WriteCommentChunk(IReadOnlyList<Comment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        using var memory = new MemoryStream();

        int count = Math.Min(comments.Count, MaximumMarkers);
        WriteUInt16(memory, (ushort)count);
        for (int i = 0; i < count; i++)
        {
            Comment comment = comments[i];
            byte[] text = Encoding.ASCII.GetBytes(comment.Text ?? string.Empty);
            if (text.Length > ushort.MaxValue) text = text[..ushort.MaxValue];

            WriteUInt32(memory, comment.TimeStamp);
            WriteUInt16(memory, (ushort)Math.Clamp(comment.MarkerId, 0, ushort.MaxValue));
            WriteUInt16(memory, (ushort)text.Length);
            memory.Write(text, 0, text.Length);

            // Unlike a mark's pstring, only the text is padded — the fixed part is already even.
            if ((text.Length & 1) != 0) memory.WriteByte(0);
        }
        return memory.ToArray();
    }

    public static List<Comment> ReadCommentChunk(byte[]? data)
    {
        var comments = new List<Comment>();
        if (data is not { Length: >= 2 }) return comments;

        int declared = data[0] << 8 | data[1];
        int at = 2;
        for (int i = 0; i < declared; i++)
        {
            if (at + 8 > data.Length) break;
            uint stamp = (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);
            int marker = data[at + 4] << 8 | data[at + 5];
            int length = data[at + 6] << 8 | data[at + 7];
            at += 8;

            if (at + length > data.Length) break;
            comments.Add(new Comment(Encoding.ASCII.GetString(data, at, length), marker, stamp));
            at += length + (length & 1);
        }
        return comments;
    }

    // ── helpers ──────────────────────────────────────────────────

    private static void WritePascalString(Stream stream, string? value)
    {
        byte[] text = Encoding.ASCII.GetBytes(value ?? string.Empty);
        if (text.Length > byte.MaxValue) text = text[..byte.MaxValue];

        stream.WriteByte((byte)text.Length);
        stream.Write(text, 0, text.Length);
        if (((1 + text.Length) & 1) != 0) stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        for (int shift = 24; shift >= 0; shift -= 8) stream.WriteByte((byte)(value >> shift));
    }
}
