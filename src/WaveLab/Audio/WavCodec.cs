using System.IO;
using System.Text;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>Which RIFF container a WAV is written in.</summary>
public enum WavContainer
{
    /// <summary>Plain RIFF while it fits, RF64 once it does not. What every caller should use.</summary>
    Automatic,

    /// <summary>Plain RIFF, refusing anything past the 32-bit size field.</summary>
    Riff,

    /// <summary>RF64 (EBU Tech 3306), whose real sizes live in a <c>ds64</c> chunk.</summary>
    Rf64,
}

/// <summary>
/// Sample-accurate RIFF/WAVE reader and writer.
/// Reads: PCM 16/24/32-bit int and 32/64-bit IEEE float, plus WAVE_FORMAT_EXTENSIBLE,
/// in RIFF, RF64 (EBU Tech 3306) and BW64 (ITU-R BS.2088) containers.
/// Writes: PCM 16-bit (with optional TPDF dither), PCM 24-bit, and 32-bit IEEE float.
/// </summary>
public static class WavCodec
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private const uint IdRiff = 0x46464952;   // "RIFF"
    private const uint IdRf64 = 0x34364652;   // "RF64"
    private const uint IdBw64 = 0x34365742;   // "BW64"
    private const uint IdWave = 0x45564157;   // "WAVE"
    private const uint IdDs64 = 0x34367364;   // "ds64"
    private const uint IdFmt = 0x20746D66;    // "fmt "
    private const uint IdData = 0x61746164;   // "data"
    private const uint IdFact = 0x74636166;   // "fact"

    /// <summary>The fixed part of a <c>ds64</c> chunk, before its optional chunk-size table.</summary>
    private const int Ds64FixedBytes = 28;

    /// <summary>
    /// Everything a <c>ds64</c> chunk says. In an RF64 file every 32-bit size field that would
    /// overflow is written as 0xFFFFFFFF and its real value is found here instead.
    /// </summary>
    private sealed class Ds64
    {
        public long RiffSize;
        public long DataSize = -1;
        public long SampleCount;
        public readonly Dictionary<uint, long> Table = [];

        /// <summary>
        /// The real size of a chunk whose 32-bit field is the escape value. Only <c>data</c> has a
        /// dedicated field; anything else has to be named in the table, and a file that escapes a
        /// size without doing so has not said how long the chunk is at all.
        /// </summary>
        public long SizeFor(uint chunkId) =>
            chunkId == IdData ? DataSize
            : Table.TryGetValue(chunkId, out long size) ? size
            : throw new InvalidDataException(
                $"The RF64 file escapes the size of '{RiffMetadata.IdFrom(chunkId)}' but does not state it.");
    }

    public static AudioDocument Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (fs.Length < 12) throw new InvalidDataException("The WAV header is truncated.");
        uint form = br.ReadUInt32();
        bool rf64 = form is IdRf64 or IdBw64;
        if (form != IdRiff && !rf64) throw new InvalidDataException("Not a RIFF file.");
        br.ReadUInt32(); // RIFF size; 0xFFFFFFFF in an RF64 file, whose real size is in ds64
        if (br.ReadUInt32() != IdWave) throw new InvalidDataException("Not a WAVE file.");

        ushort format = 0, channels = 0, bits = 0;
        uint sampleRate = 0, declaredByteRate = 0;
        ushort declaredBlockAlign = 0;
        long dataStart = -1;
        long dataSize = 0;
        Ds64? ds64 = null;
        var metadata = new RiffMetadata();

        while (fs.Position + 8 <= fs.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint chunkId = br.ReadUInt32();
            uint declaredSize = br.ReadUInt32();

            // ds64 is required to come first, so by the time an escaped size is met it is known.
            long chunkSize = rf64 && declaredSize == uint.MaxValue
                ? (ds64 ?? throw new InvalidDataException(
                       "The RF64 file escapes a chunk size before its ds64 chunk.")).SizeFor(chunkId)
                : declaredSize;
            if (chunkSize < 0) throw new InvalidDataException("The WAV contains a negative chunk size.");

            long chunkStart = fs.Position;
            long chunkEnd = checked(chunkStart + chunkSize);
            long nextChunk = checked(chunkEnd + (chunkSize & 1));
            if (chunkEnd > fs.Length || nextChunk > fs.Length)
                throw new InvalidDataException("The WAV contains a truncated chunk.");

            if (chunkId == IdDs64)
            {
                if (!rf64) throw new InvalidDataException("A plain RIFF file cannot carry a ds64 chunk.");
                if (ds64 != null) throw new InvalidDataException("The RF64 file has more than one ds64 chunk.");
                ds64 = ReadDs64(br, chunkSize);
            }
            else if (chunkId == IdFmt)
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("The WAV format chunk is truncated.");
                format = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = br.ReadUInt32();
                declaredByteRate = br.ReadUInt32();
                declaredBlockAlign = br.ReadUInt16();
                bits = br.ReadUInt16();
                if (format == FormatExtensible)
                {
                    if (chunkSize < 18)
                        throw new InvalidDataException("The extensible WAV format chunk has no extension size.");
                    ushort extensionSize = br.ReadUInt16();
                    if (extensionSize < 22)
                        throw new InvalidDataException("The extensible WAV format extension must be at least 22 bytes.");
                    if (chunkSize < 18u + extensionSize)
                        throw new InvalidDataException("The extensible WAV format extension is truncated.");

                    ushort validBits = br.ReadUInt16();
                    br.ReadUInt32(); // channel mask
                    byte[] subFormatBytes = br.ReadBytes(16);
                    if (subFormatBytes.Length != 16)
                        throw new InvalidDataException("The extensible WAV subformat GUID is truncated.");
                    Guid subFormat = new(subFormatBytes);
                    format = subFormat == PcmSubFormat
                        ? FormatPcm
                        : subFormat == IeeeFloatSubFormat
                            ? FormatIeeeFloat
                            : throw new InvalidDataException($"Unsupported extensible WAV subformat {subFormat}.");
                    // Reduced-valid-bit PCM is left-aligned in its container and
                    // needs a different decoder/scaling path. Reject it instead
                    // of silently interpreting padding bits as signal.
                    if (validBits != bits)
                        throw new InvalidDataException(
                            "Extensible WAV valid bits must match the container size.");
                }
            }
            else if (chunkId == IdData)
            {
                // Located now, decoded block by block below: buffering the whole
                // chunk would hold the raw bytes and the float output at once.
                dataStart = chunkStart;
                dataSize = chunkSize;
            }
            else
            {
                // Everything else is carried through verbatim. Reading only fmt and data and
                // discarding the rest is what silently lost broadcast metadata, loop points and
                // field-recorder notes on every save, and is the reason a file opened here could
                // not be written back over itself.
                string id = RiffMetadata.IdFrom(chunkId);
                if (chunkSize <= RiffMetadata.MaximumChunkBytes)
                {
                    fs.Position = chunkStart;
                    metadata.Add(id, br.ReadBytes((int)chunkSize));
                }
            }

            fs.Position = nextChunk;
        }

        if (rf64 && ds64 == null)
            throw new InvalidDataException("The RF64 file has no ds64 chunk, so its real sizes are unstated.");
        if (dataStart < 0 || channels == 0 || sampleRate == 0 || sampleRate > int.MaxValue)
            throw new InvalidDataException("Missing or invalid fmt/data chunk.");
        if (format != FormatPcm && format != FormatIeeeFloat)
            throw new InvalidDataException($"Unsupported WAV format tag {format}.");
        bool supportedSamples = format == FormatPcm && bits is 16 or 24 or 32 ||
                                format == FormatIeeeFloat && bits is 32 or 64;
        if (!supportedSamples)
            throw new InvalidDataException($"Unsupported sample format: tag {format}, {bits}-bit.");

        int bytesPerSample = bits / 8;
        long expectedBlockAlign = (long)bytesPerSample * channels;
        if (expectedBlockAlign > ushort.MaxValue || declaredBlockAlign != expectedBlockAlign)
            throw new InvalidDataException("The WAV block alignment does not match its channel and sample format.");
        ulong expectedByteRate = (ulong)sampleRate * declaredBlockAlign;
        if (expectedByteRate > uint.MaxValue || declaredByteRate != expectedByteRate)
            throw new InvalidDataException("The WAV byte rate does not match its sample rate and block alignment.");

        int blockAlign = declaredBlockAlign;
        if (dataSize % blockAlign != 0)
            throw new InvalidDataException("The WAV data chunk ends in a partial sample frame.");

        // The ceiling is one channel's float array, not the file: an RF64 data chunk may be many
        // gigabytes, and what has to fit in memory is `frames` floats per channel.
        long frameCountLong = dataSize / blockAlign;
        if (frameCountLong > Array.MaxLength)
            throw new InvalidDataException("The WAV holds more sample frames than can be loaded into memory.");
        var frameCount = (int)frameCountLong;
        var channelData = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
            channelData[channel] = new float[frameCount];

        fs.Position = dataStart;
        DecodeStreaming(br, format, bits, channels, blockAlign, frameCount, channelData, cancellationToken);

        int sourceBits = format == FormatIeeeFloat ? 32 : Math.Min((int)bits, 32);
        return new AudioDocument(channelData, (int)sampleRate, sourceBits)
        {
            FilePath = path,
            Title = Path.GetFileName(path),
            Riff = metadata,
        };
    }

    /// <summary>
    /// Reads a <c>ds64</c> chunk: three 64-bit sizes as low/high pairs, then an optional table
    /// naming any other chunk that needed the escape.
    /// </summary>
    private static Ds64 ReadDs64(BinaryReader reader, long chunkSize)
    {
        if (chunkSize < Ds64FixedBytes)
            throw new InvalidDataException("The RF64 ds64 chunk is truncated.");

        var ds64 = new Ds64
        {
            RiffSize = ReadInt64Pair(reader),
            DataSize = ReadInt64Pair(reader),
            SampleCount = ReadInt64Pair(reader),
        };
        uint entries = reader.ReadUInt32();

        // The table is trusted only as far as the chunk's own length allows, so a wrong count
        // cannot make the reader walk off into the audio and read sizes out of it.
        long available = (chunkSize - Ds64FixedBytes) / 12;
        if (entries > available)
            throw new InvalidDataException("The RF64 ds64 table claims more entries than it contains.");

        for (uint i = 0; i < entries; i++)
        {
            uint id = reader.ReadUInt32();
            long size = ReadInt64Pair(reader);
            ds64.Table[id] = size;
        }
        return ds64;

        static long ReadInt64Pair(BinaryReader reader)
        {
            ulong low = reader.ReadUInt32();
            ulong high = reader.ReadUInt32();
            ulong value = low | (high << 32);
            if (value > long.MaxValue)
                throw new InvalidDataException("The RF64 file states a size larger than a file can be.");
            return (long)value;
        }
    }

    /// <summary>
    /// Decodes the data chunk one whole-frame block at a time, so only a modest
    /// block buffer is live alongside the float output instead of the entire chunk.
    /// </summary>
    private static void DecodeStreaming(
        BinaryReader reader,
        ushort format,
        int bits,
        int channels,
        int blockAlign,
        int frames,
        float[][] destination,
        CancellationToken cancellationToken)
    {
        bool supported = format == FormatPcm && bits is 16 or 24 or 32 ||
                         format == FormatIeeeFloat && bits is 32 or 64;
        if (!supported)
            throw new InvalidDataException($"Unsupported sample format: tag {format}, {bits}-bit.");
        if (frames == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        const int targetBlockBytes = 1 << 20;
        int framesPerBlock = Math.Max(1, Math.Min(frames, targetBlockBytes / blockAlign));
        var block = new byte[framesPerBlock * blockAlign];

        for (int frameOffset = 0; frameOffset < frames; frameOffset += framesPerBlock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int blockFrames = Math.Min(framesPerBlock, frames - frameOffset);
            FillBlock(reader, block, blockFrames * blockAlign);
            Decode(block, format, bits, channels, blockFrames, frameOffset, destination, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void FillBlock(BinaryReader reader, byte[] block, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = reader.Read(block, offset, count - offset);
            if (read == 0)
                throw new InvalidDataException("The WAV data chunk is truncated.");
            offset += read;
        }
    }

    private static void Decode(
        byte[] data,
        ushort format,
        int bits,
        int channels,
        int frames,
        int frameOffset,
        float[][] destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        if (format == FormatPcm && bits == 16)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 2)
                    destination[channel][frameOffset + frame] = BitConverter.ToInt16(data, offset) / 32768f;
            }
        }
        else if (format == FormatPcm && bits == 24)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 3)
                {
                    int value = (data[offset + 2] << 24 | data[offset + 1] << 16 | data[offset] << 8) >> 8;
                    destination[channel][frameOffset + frame] = value / 8388608f;
                }
            }
        }
        else if (format == FormatPcm && bits == 32)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 4)
                    destination[channel][frameOffset + frame] = BitConverter.ToInt32(data, offset) / 2147483648f;
            }
        }
        else if (format == FormatIeeeFloat && bits == 32)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 4)
                    destination[channel][frameOffset + frame] = BitConverter.ToSingle(data, offset);
            }
        }
        else if (format == FormatIeeeFloat && bits == 64)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 8)
                    destination[channel][frameOffset + frame] = (float)BitConverter.ToDouble(data, offset);
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported sample format: tag {format}, {bits}-bit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Write the document. bitDepth: 16, 24, or 32 (IEEE float). Dither applies to 16-bit only.
    /// </summary>
    /// <param name="ditherKind">
    /// Which dither, when <paramref name="dither"/> is set. Flat triangular is the safe default;
    /// the shaped curves trade more noise above 10 kHz for less across the rest of the band.
    /// </param>
    /// <param name="markers">
    /// Markers to embed as cue points, so they travel inside the file rather than only in a sidecar.
    /// </param>
    /// <param name="container">
    /// Which container to write. The default steps up to RF64 exactly when plain RIFF's 32-bit size
    /// fields stop being able to describe the file.
    /// </param>
    public static void Save(AudioDocument doc, string path, int bitDepth, bool dither = true,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null,
        DitherKind ditherKind = DitherKind.FlatTpdf, IReadOnlyList<Marker>? markers = null,
        WavContainer container = WavContainer.Automatic)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (bitDepth is not (16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitDepth), "WAV output must be 16, 24, or 32-bit.");
        cancellationToken.ThrowIfCancellationRequested();

        var sourceChannels = doc.Channels.ToArray();
        int channels = sourceChannels.Length;
        if (channels is <= 0 or > ushort.MaxValue)
            throw new InvalidOperationException("WAV output requires between 1 and 65,535 channels.");
        int frames = sourceChannels[0].Length;
        if (sourceChannels.Any(channel => channel == null || channel.Length != frames))
            throw new InvalidOperationException("All document channels must have the same sample count.");
        int sampleRate = doc.SampleRate;
        if (sampleRate <= 0)
            throw new InvalidOperationException("The document sample rate must be positive.");

        ushort formatTag = bitDepth == 32 ? FormatIeeeFloat : FormatPcm;
        int bytesPerSample = bitDepth / 8;
        int blockAlign = checked(bytesPerSample * channels);
        if (blockAlign > ushort.MaxValue)
            throw new InvalidOperationException("The WAV channel layout exceeds the block-alignment limit.");
        long dataSize = (long)frames * blockAlign;
        long byteRate = (long)sampleRate * blockAlign;
        if (byteRate > uint.MaxValue)
            throw new InvalidOperationException("The WAV byte rate exceeds the format limit.");

        bool fact = formatTag == FormatIeeeFloat;

        // Whatever else the source file carried, written back after the audio. Ancillary chunks sit
        // after data by convention, and a reader that does not recognise one skips it by its length.
        RiffMetadata metadata = doc.Riff is { IsAiff: false } carried ? carried : new RiffMetadata();
        if (markers is { Count: > 0 })
        {
            // Written into the file rather than only into the sidecar. A .wlmeta.json is invisible
            // to every other program and is lost the moment the WAV is copied on its own, so a set
            // of track marks placed here reached a CD authoring tool only by accident.
            var points = new List<BroadcastMetadata.CuePoint>(markers.Count);
            for (int i = 0; i < markers.Count; i++)
            {
                Marker marker = markers[i];
                points.Add(new BroadcastMetadata.CuePoint(i + 1,
                    Math.Clamp(marker.Position, 0, frames), marker.Name));
            }
            metadata = metadata.Clone();
            metadata.Set("cue ", BroadcastMetadata.WriteCueChunk(points));
            metadata.Set("LIST", BroadcastMetadata.WriteLabelList(points));
        }

        long extra = metadata.ByteLength;
        if (extra > int.MaxValue / 4)
            throw new InvalidOperationException("The metadata carried with this file is too large to write.");

        long bodySize = 4 + (8 + 16) + (fact ? 8 + 4 : 0) + (8 + dataSize) + (dataSize & 1) + extra;

        // Plain RIFF describes everything in 32 bits. Many readers take those fields as *signed*,
        // so 2 GB rather than 4 is where a file stops being safely readable — which is why the
        // automatic step-up happens there and not at the arithmetic limit.
        const long RiffCeiling = int.MaxValue - 1024;
        bool rf64 = container switch
        {
            WavContainer.Rf64 => true,
            WavContainer.Riff => false,
            _ => bodySize > RiffCeiling,
        };
        if (!rf64 && bodySize > RiffCeiling)
            throw new InvalidOperationException(
                "Audio exceeds the 2 GB WAV limit; save as RF64, export a selection, or lower the bit depth.");

        // ds64 is part of the form, so it counts toward the size ds64 itself states.
        long riffSize = rf64 ? bodySize + 8 + Ds64FixedBytes : bodySize;

        string finalPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The WAV output path has no directory.");
        string stagePath = Path.Combine(directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(rf64 ? IdRf64 : IdRiff);
                writer.Write(rf64 ? uint.MaxValue : (uint)riffSize);
                writer.Write(IdWave);

                if (rf64)
                {
                    // First chunk in the form, by definition: every escaped size after it is only
                    // meaningful once this has been read.
                    writer.Write(IdDs64);
                    writer.Write(Ds64FixedBytes);
                    WriteInt64Pair(writer, riffSize);
                    WriteInt64Pair(writer, dataSize);
                    WriteInt64Pair(writer, frames);
                    writer.Write(0);                  // no table: only data needed the escape
                }

                writer.Write(IdFmt);
                writer.Write(16);
                writer.Write(formatTag);
                writer.Write((ushort)channels);
                writer.Write(sampleRate);
                writer.Write((uint)byteRate);
                writer.Write((ushort)blockAlign);
                writer.Write((ushort)bitDepth);
                if (fact)
                {
                    writer.Write(IdFact);
                    writer.Write(4);
                    writer.Write(frames);
                }
                writer.Write(IdData);
                writer.Write(rf64 ? uint.MaxValue : (uint)dataSize);

                // One shaper for the whole file, holding per-channel error state: noise shaping is a
                // feedback loop, so it cannot be created per sample or shared across channels.
                var shaper = new Dither(dither ? ditherKind : DitherKind.None, 16, channels,
                    doc.SampleRate, autoBlank: true);
                var buffer = new byte[blockAlign];
                for (int frame = 0; frame < frames; frame++)
                {
                    if ((frame & 4095) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(frames > 0 ? (double)frame / frames : 1);
                    }

                    int output = 0;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        float sample = sourceChannels[channel][frame];
                        switch (bitDepth)
                        {
                            case 16:
                            {
                                if (!float.IsFinite(sample)) sample = 0;
                                sample = Math.Clamp(sample, -1f, 1f);

                                // The shaper owns the quantiser: there is no error to feed back
                                // until the rounding has happened, so it cannot be bolted on
                                // beforehand the way a plain noise source can.
                                double value = shaper.Process(channel, sample) * 32768.0;
                                int quantized = Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
                                buffer[output++] = (byte)quantized;
                                buffer[output++] = (byte)(quantized >> 8);
                                break;
                            }
                            case 24:
                            {
                                if (!float.IsFinite(sample)) sample = 0;
                                int quantized = (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 8388608.0);
                                quantized = Math.Clamp(quantized, -8388608, 8388607);
                                buffer[output++] = (byte)quantized;
                                buffer[output++] = (byte)(quantized >> 8);
                                buffer[output++] = (byte)(quantized >> 16);
                                break;
                            }
                            default:
                            {
                                int value = BitConverter.SingleToInt32Bits(sample);
                                buffer[output++] = (byte)value;
                                buffer[output++] = (byte)(value >> 8);
                                buffer[output++] = (byte)(value >> 16);
                                buffer[output++] = (byte)(value >> 24);
                                break;
                            }
                        }
                    }
                    writer.Write(buffer);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if ((dataSize & 1) == 1) writer.Write((byte)0);
                metadata.WriteTo(writer);
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

    /// <summary>A 64-bit size as the low/high 32-bit pair an RF64 file states it in.</summary>
    private static void WriteInt64Pair(BinaryWriter writer, long value)
    {
        writer.Write((uint)value);
        writer.Write((uint)(value >> 32));
    }
}


