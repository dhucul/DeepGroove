using System.IO;
using System.Text;

namespace WaveLab.Audio;

/// <summary>
/// The EBU broadcast extension: who made a file, when, and from where in a longer timeline.
/// </summary>
/// <param name="Description">Free text, up to 256 characters.</param>
/// <param name="Originator">Who or what produced it, up to 32.</param>
/// <param name="OriginatorReference">An unambiguous reference to this take, up to 32.</param>
/// <param name="OriginationDate">Local date, <c>yyyy-MM-dd</c>.</param>
/// <param name="OriginationTime">Local time, <c>HH:mm:ss</c>.</param>
/// <param name="TimeReference">
/// Position of the first sample on the recording day's timeline, in samples since midnight. This is
/// what lets a transfer be laid back against picture or against other takes.
/// </param>
public readonly record struct BroadcastInfo(
    string Description,
    string Originator,
    string OriginatorReference,
    string OriginationDate,
    string OriginationTime,
    ulong TimeReference)
{
    /// <summary>A description of a transfer made now, which is the common case for this app.</summary>
    public static BroadcastInfo For(string description, DateTime when, ulong timeReference = 0) =>
        new(description, "Deep Groove", string.Empty,
            when.ToString("yyyy-MM-dd"), when.ToString("HH:mm:ss"), timeReference);
}

/// <summary>
/// Reads and writes the metadata chunks this app understands: the broadcast extension, RIFF
/// information tags, and cue points with their labels.
/// </summary>
/// <remarks>
/// Markers written as <c>cue </c> points travel <em>in the file</em>. Until now they lived only in a
/// <c>.wlmeta.json</c> sidecar, which every other program ignores and which is lost the moment the
/// WAV is copied on its own — so a set of track marks placed here survived being carried to a CD
/// authoring tool only if the sidecar happened to come too.
/// </remarks>
public static class BroadcastMetadata
{
    private const int BextSize = 602;   // the fixed part, before any coding history

    // ── bext ─────────────────────────────────────────────────────

    public static byte[] WriteBext(in BroadcastInfo info, string codingHistory = "")
    {
        byte[] history = Encoding.ASCII.GetBytes(codingHistory ?? string.Empty);
        var data = new byte[BextSize + history.Length];

        WriteAscii(data, 0, info.Description, 256);
        WriteAscii(data, 256, info.Originator, 32);
        WriteAscii(data, 288, info.OriginatorReference, 32);
        WriteAscii(data, 320, info.OriginationDate, 10);
        WriteAscii(data, 330, info.OriginationTime, 8);

        BitConverter.TryWriteBytes(data.AsSpan(338), (uint)(info.TimeReference & 0xFFFFFFFF));
        BitConverter.TryWriteBytes(data.AsSpan(342), (uint)(info.TimeReference >> 32));
        BitConverter.TryWriteBytes(data.AsSpan(346), (ushort)1);   // version

        history.CopyTo(data, BextSize);
        return data;
    }

    public static BroadcastInfo? ReadBext(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 348) return null;

        ulong low = BitConverter.ToUInt32(data, 338);
        ulong high = BitConverter.ToUInt32(data, 342);
        return new BroadcastInfo(
            ReadAscii(data, 0, 256),
            ReadAscii(data, 256, 32),
            ReadAscii(data, 288, 32),
            ReadAscii(data, 320, 10),
            ReadAscii(data, 330, 8),
            low | (high << 32));
    }

    // ── LIST/INFO ────────────────────────────────────────────────

    /// <summary>
    /// The RIFF information tags — title, artist, comment and the rest — as a <c>LIST</c> chunk.
    /// </summary>
    public static byte[] WriteInfoList(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("INFO"));
        foreach ((string key, string value) in tags)
        {
            if (key.Length != 4 || string.IsNullOrEmpty(value)) continue;

            // Null-terminated, and padded to an even length as every RIFF chunk must be.
            byte[] text = Encoding.UTF8.GetBytes(value);
            writer.Write(Encoding.ASCII.GetBytes(key));
            writer.Write(text.Length + 1);
            writer.Write(text);
            writer.Write((byte)0);
            if (((text.Length + 1) & 1) != 0) writer.Write((byte)0);
        }

        writer.Flush();
        return memory.ToArray();
    }

    public static Dictionary<string, string> ReadInfoList(byte[] data)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "INFO") return tags;

        int position = 4;
        while (position + 8 <= data.Length)
        {
            string key = Encoding.ASCII.GetString(data, position, 4);
            int size = BitConverter.ToInt32(data, position + 4);
            position += 8;
            if (size < 0 || position + size > data.Length) break;

            int length = size;
            while (length > 0 && data[position + length - 1] == 0) length--;
            tags[key] = Encoding.UTF8.GetString(data, position, length);

            position += size + (size & 1);
        }
        return tags;
    }

    // ── cue points ───────────────────────────────────────────────

    /// <summary>A marker as it is written into the file.</summary>
    public readonly record struct CuePoint(int Id, int Position, string Label);

    public static byte[] WriteCueChunk(IReadOnlyList<CuePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);

        writer.Write(points.Count);
        foreach (CuePoint point in points)
        {
            writer.Write(point.Id);
            writer.Write(point.Position);          // play order position
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(0);                       // chunk start
            writer.Write(0);                       // block start
            writer.Write(point.Position);          // sample offset
        }

        writer.Flush();
        return memory.ToArray();
    }

    /// <summary>The labels for those cue points, as an associated-data <c>LIST</c>.</summary>
    public static byte[] WriteLabelList(IReadOnlyList<CuePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("adtl"));
        foreach (CuePoint point in points)
        {
            if (string.IsNullOrEmpty(point.Label)) continue;
            byte[] text = Encoding.UTF8.GetBytes(point.Label);
            int size = 4 + text.Length + 1;

            writer.Write(Encoding.ASCII.GetBytes("labl"));
            writer.Write(size);
            writer.Write(point.Id);
            writer.Write(text);
            writer.Write((byte)0);
            if ((size & 1) != 0) writer.Write((byte)0);
        }

        writer.Flush();
        return memory.ToArray();
    }

    public static List<CuePoint> ReadCuePoints(byte[] cueData, byte[]? labelData)
    {
        var points = new List<CuePoint>();
        ArgumentNullException.ThrowIfNull(cueData);
        if (cueData.Length < 4) return points;

        int count = BitConverter.ToInt32(cueData, 0);
        if (count < 0 || count > 10_000) return points;

        for (int i = 0; i < count; i++)
        {
            int at = 4 + i * 24;
            if (at + 24 > cueData.Length) break;
            points.Add(new CuePoint(
                BitConverter.ToInt32(cueData, at),
                BitConverter.ToInt32(cueData, at + 20),
                string.Empty));
        }

        if (labelData is null || labelData.Length < 4) return points;
        Dictionary<int, string> labels = ReadLabels(labelData);
        for (int i = 0; i < points.Count; i++)
            if (labels.TryGetValue(points[i].Id, out string? label)) points[i] = points[i] with { Label = label };

        return points;
    }

    private static Dictionary<int, string> ReadLabels(byte[] data)
    {
        var labels = new Dictionary<int, string>();
        if (Encoding.ASCII.GetString(data, 0, 4) != "adtl") return labels;

        int position = 4;
        while (position + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, position, 4);
            int size = BitConverter.ToInt32(data, position + 4);
            position += 8;
            if (size < 4 || position + size > data.Length) break;

            if (string.Equals(id, "labl", StringComparison.Ordinal))
            {
                int cueId = BitConverter.ToInt32(data, position);
                int length = size - 4;
                while (length > 0 && data[position + 4 + length - 1] == 0) length--;
                labels[cueId] = Encoding.UTF8.GetString(data, position + 4, length);
            }

            position += size + (size & 1);
        }
        return labels;
    }

    // ── helpers ──────────────────────────────────────────────────

    private static void WriteAscii(byte[] destination, int offset, string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return;
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, destination, offset, Math.Min(bytes.Length, length));
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        int end = offset;
        while (end < offset + length && end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset).TrimEnd();
    }
}
