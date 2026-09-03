using System.IO;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>
/// Sony's Wave64 container: the same audio as a WAV, described in a way that cannot run out of
/// numbers.
/// </summary>
/// <remarks>
/// <para>
/// Wave64 solves the problem RF64 solves, and solves it earlier and differently. RIFF states every
/// size in 32 bits and so cannot describe a file past four gigabytes; RF64 keeps RIFF's shape and
/// escapes the sizes that overflow into a side table, while Wave64 simply widens every size field to
/// 64 bits and, having broken compatibility anyway, widens the four-character chunk identifiers to
/// sixteen-byte GUIDs at the same time. <b>This app writes RF64 by default and reads both</b>, which
/// is the useful arrangement: RF64 is what the broadcast world standardised on, and Wave64 is what
/// comes out of Sound Forge and Sonar.
/// </para>
/// <para>
/// The GUIDs are not arbitrary. The first four bytes of each are the ASCII chunk name in
/// little-endian order — <c>fmt </c>, <c>data</c>, <c>riff</c>, <c>wave</c> — followed by a fixed
/// twelve-byte suffix, so a Wave64 file is recognisably a RIFF file that has been let out.
/// </para>
/// <para>
/// <b>Two counting traps, and both are easy to get backwards.</b> A chunk's stated size
/// <em>includes</em> its own twenty-four byte header, where a RIFF chunk's size counts only the
/// payload. And chunks are padded to eight-byte boundaries rather than two. Getting either wrong
/// produces a file that opens in the writer's own reader and nowhere else, which is why the tests
/// here check the offsets rather than only the round trip.
/// </para>
/// </remarks>
public static class Wave64Codec
{
    // The first four bytes of each are the chunk name, little-endian.
    private static readonly Guid RiffGuid = new("66666972-912E-11CF-A5D6-28DB04C10000");
    private static readonly Guid WaveGuid = new("65766177-ACF3-11D3-8CD1-00C04F8EDB8A");
    private static readonly Guid FmtGuid = new("20746D66-ACF3-11D3-8CD1-00C04F8EDB8A");
    private static readonly Guid DataGuid = new("61746164-ACF3-11D3-8CD1-00C04F8EDB8A");
    private static readonly Guid FactGuid = new("74636166-ACF3-11D3-8CD1-00C04F8EDB8A");
    private static readonly Guid DiscSignalGuid = new("51456C77-5DA1-4E70-972D-4AE4D5385361");

    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>Bytes of header every chunk's stated size includes: the GUID and the size itself.</summary>
    private const int ChunkHeaderBytes = 24;

    /// <summary>The whole file's header: the riff GUID, the file size, and the wave GUID.</summary>
    private const int FileHeaderBytes = 16 + 8 + 16;

    /// <summary>Chunks start on eight-byte boundaries, not the two a RIFF file uses.</summary>
    private const int Alignment = 8;

    private static long PadTo(long size) => (Alignment - size % Alignment) % Alignment;

    /// <summary>
    /// Whether a file is Wave64, by its opening GUID rather than by its extension.
    /// </summary>
    /// <remarks>
    /// Worth asking rather than trusting the name: the format is written to <c>.w64</c> by
    /// convention and to <c>.wav</c> by more than one application that ought to know better.
    /// </remarks>
    public static bool IsWave64(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[16];
            return stream.ReadAtLeast(head, 16, throwOnEndOfStream: false) == 16
                   && new Guid(head) == RiffGuid;
        }
        catch { return false; }
    }

    // ── reading ──────────────────────────────────────────────────

    public static AudioDocument Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16);
        using var reader = new BinaryReader(stream);

        if (ReadGuid(reader) != RiffGuid)
            throw new InvalidDataException("Not a Wave64 file: the opening identifier is wrong.");

        long statedSize = reader.ReadInt64();
        if (ReadGuid(reader) != WaveGuid)
            throw new InvalidDataException("Not a Wave64 file: the form identifier is not 'wave'.");

        ushort format = 0;
        int channels = 0, sampleRate = 0, bits = 0;
        long dataStart = -1, dataBytes = 0;
        DiscSignalState discSignalState = DiscSignalState.Unknown;

        long position = FileHeaderBytes;
        long limit = statedSize > 0 ? Math.Min(statedSize, stream.Length) : stream.Length;

        while (position + ChunkHeaderBytes <= limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = position;

            Guid id = ReadGuid(reader);
            long size = reader.ReadInt64();

            // The size counts its own header, so anything below that is not a chunk at all — and a
            // size past the end of the file is a truncated one, which is worth saying plainly
            // rather than reading off the end of the stream to find out.
            if (size < ChunkHeaderBytes)
                throw new InvalidDataException($"A Wave64 chunk states an impossible size of {size}.");
            long payload = size - ChunkHeaderBytes;
            if (position + size > stream.Length)
            {
                // A truncated data chunk is still worth what survived of it; anything else is not.
                if (id == DataGuid)
                {
                    dataStart = position + ChunkHeaderBytes;
                    dataBytes = stream.Length - dataStart;
                    break;
                }
                throw new InvalidDataException("The Wave64 file ends inside a chunk.");
            }

            if (id == FmtGuid)
            {
                if (payload < 16) throw new InvalidDataException("The Wave64 'fmt ' chunk is too short.");
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();                 // average bytes per second, derivable
                reader.ReadUInt16();                // block align, derivable
                bits = reader.ReadUInt16();

                if (format == FormatExtensible && payload >= 40)
                {
                    reader.ReadUInt16();            // cbSize
                    reader.ReadUInt16();            // valid bits per sample
                    reader.ReadUInt32();            // channel mask
                    Guid sub = ReadGuid(reader);
                    format = sub == IeeeFloatSubFormat ? FormatIeeeFloat
                        : sub == PcmSubFormat ? FormatPcm
                        : throw new InvalidDataException(
                            "The Wave64 file uses a sample format this app does not read.");
                }
            }
            else if (id == DataGuid)
            {
                dataStart = position + ChunkHeaderBytes;
                dataBytes = payload;
            }
            else if (id == DiscSignalGuid && payload >= 2)
            {
                byte version = reader.ReadByte();
                byte state = reader.ReadByte();
                if (version == 1 && Enum.IsDefined((DiscSignalState)state))
                    discSignalState = (DiscSignalState)state;
            }

            position += size + PadTo(size);
        }

        if (channels <= 0 || sampleRate <= 0 || bits <= 0)
            throw new InvalidDataException("The Wave64 file has no usable 'fmt ' chunk.");
        if (dataStart < 0)
            throw new InvalidDataException("The Wave64 file has no 'data' chunk.");

        int blockAlign = channels * (bits / 8);
        if (blockAlign <= 0) throw new InvalidDataException("The Wave64 format describes no samples.");

        long frames = dataBytes / blockAlign;
        if (frames > Array.MaxLength)
            throw new InvalidDataException("The Wave64 file holds more samples than this app can open.");

        var output = new float[channels][];
        for (int c = 0; c < channels; c++) output[c] = new float[frames];

        stream.Position = dataStart;
        var block = new byte[Math.Min(dataBytes, 1 << 20) is var chunk && chunk > 0
            ? (int)(chunk - chunk % blockAlign + blockAlign)
            : blockAlign];

        long done = 0;
        while (done < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(frames - done, block.Length / blockAlign);
            int wanted = want * blockAlign;

            int got = stream.ReadAtLeast(block.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            int usable = got / blockAlign;
            if (usable <= 0) break;

            WavCodec.Decode(block, format, bits, channels, usable, (int)done, output, cancellationToken);
            done += usable;
        }

        // Normalised to the document's domain, as WavCodec.Load and AiffCodec.Load both
        // do. Passing `bits` straight through meant a 64-bit float Wave64 file decoded in
        // full and then threw out of AudioDocument's setter, which accepts 16, 24 or 32.
        int sourceBits = format == FormatIeeeFloat ? 32 : Math.Min(bits, 32);
        return new AudioDocument(output, sampleRate, sourceBits)
        {
            // Set for the same reason the other two codecs set them: without a path the
            // file opened as "Untitled" and Save silently became Save As.
            FilePath = path,
            Title = Path.GetFileName(path),
            DiscSignalState = discSignalState,
        };
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (reader.Read(bytes) != 16) throw new EndOfStreamException("The Wave64 file ends in a header.");
        return new Guid(bytes);
    }

    // ── writing ──────────────────────────────────────────────────

    /// <summary>
    /// Writes a Wave64 file. Depth 16, 24, or 32 (IEEE float); dither applies to 16-bit only.
    /// </summary>
    public static void Save(AudioDocument doc, string path, int bitDepth, bool dither = true,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null,
        DitherKind ditherKind = DitherKind.FlatTpdf)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (bitDepth is not (16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitDepth), "Bit depth must be 16, 24 or 32.");

        IReadOnlyList<float[]> source = doc.Channels;
        int channels = source.Count;
        if (channels <= 0) throw new ArgumentException("The document has no channels.", nameof(doc));

        int frames = doc.Length;
        int bytesPerSample = bitDepth / 8;
        int blockAlign = channels * bytesPerSample;
        long dataBytes = (long)frames * blockAlign;
        bool isFloat = bitDepth == 32;

        string finalPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The Wave64 output path has no directory.");

        // Unique and CreateNew, as every other writer here does. A fixed ".part" opened
        // with FileMode.Create let a second export to the same destination truncate the
        // first one's staging file instead of failing.
        string stagePath = Path.Combine(directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 1 << 16))
            using (var writer = new BinaryWriter(stream))
            {
                const int fmtPayload = 18;                     // WAVEFORMATEX with cbSize = 0
                long fmtSize = ChunkHeaderBytes + fmtPayload;
                long factSize = isFloat ? ChunkHeaderBytes + 4 : 0;
                long discSignalSize = doc.DiscSignalState == DiscSignalState.Unknown
                    ? 0
                    : ChunkHeaderBytes + 2;
                long dataSize = ChunkHeaderBytes + dataBytes;

                long total = FileHeaderBytes
                             + fmtSize + PadTo(fmtSize)
                             + (isFloat ? factSize + PadTo(factSize) : 0)
                             + (discSignalSize > 0 ? discSignalSize + PadTo(discSignalSize) : 0)
                             + dataSize + PadTo(dataSize);

                WriteGuid(writer, RiffGuid);
                writer.Write(total);
                WriteGuid(writer, WaveGuid);

                WriteGuid(writer, FmtGuid);
                writer.Write(fmtSize);
                writer.Write(isFloat ? FormatIeeeFloat : FormatPcm);
                writer.Write((ushort)channels);
                writer.Write(doc.SampleRate);
                writer.Write(doc.SampleRate * blockAlign);
                writer.Write((ushort)blockAlign);
                writer.Write((ushort)bitDepth);
                writer.Write((ushort)0);                       // cbSize
                Pad(writer, fmtSize);

                if (isFloat)
                {
                    // Conventional for non-PCM, and the one place a reader can learn the frame count
                    // without dividing the data size by the block alignment.
                    WriteGuid(writer, FactGuid);
                    writer.Write(factSize);
                    writer.Write(frames);
                    Pad(writer, factSize);
                }

                if (discSignalSize > 0)
                {
                    WriteGuid(writer, DiscSignalGuid);
                    writer.Write(discSignalSize);
                    writer.Write((byte)1);
                    writer.Write((byte)doc.DiscSignalState);
                    Pad(writer, discSignalSize);
                }

                WriteGuid(writer, DataGuid);
                writer.Write(dataSize);

                // One shaper for the whole file, holding per-channel error state, and the same frame
                // encoder the WAV writer uses — the containers disagree about how to describe where
                // the samples are and agree exactly about what a sample is.
                var shaper = new Dither(dither ? ditherKind : DitherKind.None, 16, channels,
                    doc.SampleRate, autoBlank: true);
                // Batched, for the same reason WavCodec.Save is.
                const int targetBlockBytes = 1 << 20;
                int framesPerBlock = Math.Max(1, targetBlockBytes / blockAlign);
                var block = new byte[framesPerBlock * blockAlign];
                int inBlock = 0;

                for (int frame = 0; frame < frames; frame++)
                {
                    if ((frame & 4095) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report((double)frame / frames);
                    }

                    WavCodec.EncodeFrame(source, frame, channels, bitDepth, shaper,
                        block.AsSpan(inBlock * blockAlign, blockAlign));
                    if (++inBlock == framesPerBlock)
                    {
                        writer.Write(block, 0, block.Length);
                        inBlock = 0;
                    }
                }
                if (inBlock > 0) writer.Write(block, 0, inBlock * blockAlign);

                Pad(writer, dataSize);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagePath, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(stagePath); } catch { }
            throw;
        }

        progress?.Report(1);
    }

    private static void WriteGuid(BinaryWriter writer, Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        writer.Write(bytes);
    }

    private static void Pad(BinaryWriter writer, long size)
    {
        for (long i = 0; i < PadTo(size); i++) writer.Write((byte)0);
    }
}
