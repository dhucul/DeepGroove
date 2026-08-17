using System.IO;
using System.Text;

namespace WaveLab.Audio;

/// <summary>An ancillary RIFF chunk, kept exactly as it was found.</summary>
/// <param name="Id">The four-character identifier, e.g. <c>bext</c>.</param>
/// <param name="Data">The chunk's payload, without the header or its pad byte.</param>
public readonly record struct RiffChunk(string Id, byte[] Data)
{
    public override string ToString() => $"{Id} ({Data.Length} bytes)";
}

/// <summary>
/// The chunks of a RIFF file that this app does not itself interpret, carried through a load and a
/// save unchanged.
/// </summary>
/// <remarks>
/// <para>
/// A WAV is a container, and audio is only one of the things in it. Broadcast metadata, iXML from a
/// field recorder, loop points from a sampler, a producer's notes, the timestamp a take was recorded
/// at — all of it lives in chunks alongside the audio, and all of it was previously discarded the
/// moment a file was opened, because the loader read <c>fmt </c> and <c>data</c> and skipped
/// everything else. Saving then wrote a file with only those two, silently losing whatever had been
/// there.
/// </para>
/// <para>
/// That is the reason <c>RequiresSaveAs</c> exists: overwriting a file whose contents you have
/// partly forgotten is destructive, so the app refused to. Keeping the chunks removes the cause
/// rather than the symptom.
/// </para>
/// <para>
/// Chunks are held as raw bytes and written back byte for byte. Interpreting them would mean
/// understanding every format a WAV can carry, and getting one wrong would corrupt data that
/// passing it through untouched preserves perfectly.
/// </para>
/// </remarks>
public sealed class RiffMetadata
{
    /// <summary>
    /// Chunks derived from the audio itself, which the writer always produces afresh and which must
    /// therefore not also be carried through as opaque copies.
    /// </summary>
    /// <remarks>
    /// Only these three. Cue points and information lists are <em>not</em> here even though this app
    /// writes them: they have to survive being read as well, or a file's markers could never be
    /// imported — and when markers are written they replace the carried chunk through
    /// <see cref="Set"/> rather than being added beside it.
    /// </remarks>
    private static readonly HashSet<string> Owned =
        new(StringComparer.Ordinal) { "fmt ", "data", "fact" };

    private readonly List<RiffChunk> _chunks = [];

    public IReadOnlyList<RiffChunk> Chunks => _chunks;

    public bool IsEmpty => _chunks.Count == 0;

    /// <summary>Total bytes these chunks add to a file, headers and padding included.</summary>
    public long ByteLength
    {
        get
        {
            long total = 0;
            foreach (RiffChunk chunk in _chunks) total += 8 + chunk.Data.Length + (chunk.Data.Length & 1);
            return total;
        }
    }

    /// <summary>Records a chunk unless this app writes that kind itself.</summary>
    public void Add(string id, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(data);
        if (id.Length != 4 || Owned.Contains(id)) return;

        // A sane ceiling: a stray length field in a damaged file should not be allowed to ask for
        // hundreds of megabytes of metadata.
        if (data.Length > MaximumChunkBytes) return;
        _chunks.Add(new RiffChunk(id, data));
    }

    /// <summary>Largest ancillary chunk carried. Beyond this it is almost certainly a damaged header.</summary>
    public const int MaximumChunkBytes = 16 * 1024 * 1024;

    public RiffChunk? Find(string id)
    {
        foreach (RiffChunk chunk in _chunks)
            if (string.Equals(chunk.Id, id, StringComparison.Ordinal)) return chunk;
        return null;
    }

    /// <summary>Replaces a chunk of this id, or adds one if there is none.</summary>
    public void Set(string id, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(data);
        if (id.Length != 4) throw new ArgumentException("A chunk id is four characters.", nameof(id));

        for (int i = 0; i < _chunks.Count; i++)
        {
            if (!string.Equals(_chunks[i].Id, id, StringComparison.Ordinal)) continue;
            _chunks[i] = new RiffChunk(id, data);
            return;
        }
        _chunks.Add(new RiffChunk(id, data));
    }

    public bool Remove(string id) => _chunks.RemoveAll(c => string.Equals(c.Id, id, StringComparison.Ordinal)) > 0;

    /// <summary>Writes every carried chunk, each padded to an even length as RIFF requires.</summary>
    public void WriteTo(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        foreach (RiffChunk chunk in _chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(chunk.Id));
            writer.Write(chunk.Data.Length);
            writer.Write(chunk.Data);
            if ((chunk.Data.Length & 1) != 0) writer.Write((byte)0);
        }
    }

    public RiffMetadata Clone()
    {
        var copy = new RiffMetadata();
        foreach (RiffChunk chunk in _chunks) copy._chunks.Add(new RiffChunk(chunk.Id, (byte[])chunk.Data.Clone()));
        return copy;
    }

    /// <summary>A four-character id from a little-endian identifier as read from the file.</summary>
    public static string IdFrom(uint value) =>
        new([(char)(value & 0xFF), (char)((value >> 8) & 0xFF),
             (char)((value >> 16) & 0xFF), (char)((value >> 24) & 0xFF)]);
}
