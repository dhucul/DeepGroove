using System.IO;
using System.Numerics;
using System.Text;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>
/// Sample-accurate AIFF reader and writer.
/// Reads classic AIFF PCM plus uncompressed AIFF-C PCM/float variants.
/// Writes classic AIFF signed PCM at 16, 24, or 32 bits.
/// </summary>
public static class AiffCodec
{
    private const uint Form = 0x464F524D; // FORM
    private const uint Aiff = 0x41494646; // AIFF
    private const uint Aifc = 0x41494643; // AIFC
    private const uint Common = 0x434F4D4D; // COMM
    private const uint SoundData = 0x53534E44; // SSND
    private const uint None = 0x4E4F4E45; // NONE
    private const uint Twos = 0x74776F73; // twos
    private const uint Sowt = 0x736F7774; // sowt
    private const uint Raw = 0x72617720; // raw_
    private const uint Integer24 = 0x696E3234; // in24
    private const uint Integer32 = 0x696E3332; // in32
    private const uint Float32 = 0x666C3332; // fl32
    private const uint Float32Upper = 0x464C3332; // FL32
    private const uint Float64 = 0x666C3634; // fl64
    private const uint Float64Upper = 0x464C3634; // FL64

    public static AudioDocument Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 12 || ReadUInt32Big(reader) != Form)
            throw new InvalidDataException("Not an AIFF FORM file.");

        uint formSize = ReadUInt32Big(reader);
        uint formType = ReadUInt32Big(reader);
        bool compressedContainer = formType == Aifc;
        if (formType != Aiff && !compressedContainer)
            throw new InvalidDataException("Not an AIFF or AIFF-C file.");

        long formEnd = checked(8L + formSize);
        if (formEnd > stream.Length || formEnd < 12)
            throw new InvalidDataException("The AIFF FORM chunk is truncated.");

        ushort channels = 0;
        uint declaredFrames = 0;
        ushort bits = 0;
        int sampleRate = 0;
        uint compression = None;
        long soundPosition = -1;
        long soundLength = 0;
        RiffMetadata metadata = RiffMetadata.ForAiff();

        while (stream.Position + 8 <= formEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint chunkId = ReadUInt32Big(reader);
            uint chunkSize = ReadUInt32Big(reader);
            long chunkStart = stream.Position;
            long chunkEnd = checked(chunkStart + chunkSize);
            long nextChunk = checked(chunkEnd + (chunkSize & 1));
            if (chunkEnd > formEnd || nextChunk > formEnd)
                throw new InvalidDataException("The AIFF contains a truncated chunk.");

            if (chunkId == Common)
            {
                int minimumSize = compressedContainer ? 22 : 18;
                if (chunkSize < minimumSize)
                    throw new InvalidDataException("The AIFF common chunk is truncated.");
                channels = ReadUInt16Big(reader);
                declaredFrames = ReadUInt32Big(reader);
                bits = ReadUInt16Big(reader);
                sampleRate = ReadSampleRate(reader);
                if (compressedContainer) compression = ReadUInt32Big(reader);
            }
            else if (chunkId == SoundData)
            {
                if (chunkSize < 8)
                    throw new InvalidDataException("The AIFF sound-data chunk is truncated.");
                uint offset = ReadUInt32Big(reader);
                ReadUInt32Big(reader); // block size; sample data is still stored contiguously
                long payloadLength = chunkSize - 8L;
                if (offset > payloadLength)
                    throw new InvalidDataException("The AIFF sound-data offset exceeds its chunk.");
                soundPosition = checked(stream.Position + offset);
                soundLength = payloadLength - offset;
            }
            else
            {
                // Marks, comments, the title, the author, an annotation, whatever else the file
                // carried — kept verbatim and written back. Discarding it is what forced an
                // imported AIFF to be saved somewhere else so its metadata could not be destroyed.
                string id = RiffMetadata.IdFromBig(chunkId);
                if (chunkSize <= RiffMetadata.MaximumChunkBytes)
                {
                    stream.Position = chunkStart;
                    metadata.Add(id, reader.ReadBytes((int)chunkSize));
                }
            }

            stream.Position = nextChunk;
        }

        if (channels == 0 || sampleRate <= 0 || soundPosition < 0)
            throw new InvalidDataException("The AIFF is missing a valid COMM or SSND chunk.");
        if (declaredFrames > Array.MaxLength)
            throw new InvalidDataException("The AIFF exceeds the maximum supported sample count.");

        bool floatingPoint = compression is Float32 or Float32Upper or Float64 or Float64Upper;
        bool littleEndian = compression == Sowt;
        bool unsigned8Bit = compression == Raw;
        bool supportedCompression = !compressedContainer ||
            compression is None or Twos or Sowt or Raw or Integer24 or Integer32 or
                Float32 or Float32Upper or Float64 or Float64Upper;
        if (!supportedCompression)
            throw new InvalidDataException(
                $"Unsupported AIFF-C compression type '{FourCc(compression)}'.");
        bool supportedSamples = floatingPoint
            ? bits == 32 && compression is Float32 or Float32Upper ||
              bits == 64 && compression is Float64 or Float64Upper
            : compression == Raw ? bits == 8
            : compression == Integer24 ? bits == 24
            : compression == Integer32 ? bits == 32
            : bits is 8 or 16 or 24 or 32;
        if (!supportedSamples)
            throw new InvalidDataException(
                $"Unsupported AIFF sample format: {FourCc(compression)}, {bits}-bit.");

        int bytesPerSample = bits / 8;
        int blockAlign = checked(bytesPerSample * channels);
        long expectedLength = checked((long)declaredFrames * blockAlign);
        if (expectedLength > soundLength)
            throw new InvalidDataException("The AIFF sound-data chunk ends in a partial sample frame.");
        if (expectedLength > Array.MaxLength)
            throw new InvalidDataException("The AIFF sound-data chunk is too large to load into memory.");

        int frames = (int)declaredFrames;
        var channelData = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
            channelData[channel] = new float[frames];

        stream.Position = soundPosition;
        DecodeStreaming(reader, bits, channels, blockAlign, frames, floatingPoint, littleEndian,
            unsigned8Bit, channelData, cancellationToken);

        int sourceBits = floatingPoint ? 32 : bits <= 16 ? 16 : bits;
        return new AudioDocument(channelData, sampleRate, sourceBits)
        {
            FilePath = path,

            // Ancillary chunks now survive, so a classic AIFF can be written back over itself.
            // An AIFF-C still cannot: the writer produces classic AIFF PCM, so saving in place
            // would replace the container as well as the audio, and that is a different file.
            RequiresSaveAs = compressedContainer,
            Title = Path.GetFileName(path),
            Riff = metadata,
        };
    }

    /// <summary>Writes classic AIFF signed PCM. TPDF dither applies to 16-bit only.</summary>
    /// <param name="markers">
    /// Markers to embed as a <c>MARK</c> chunk, so they travel inside the file rather than only in
    /// a sidecar.
    /// </param>
    public static void Save(
        AudioDocument doc,
        string path,
        int bitDepth,
        bool dither = true,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null,
        DitherKind ditherKind = DitherKind.FlatTpdf,
        IReadOnlyList<Marker>? markers = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetExtension(path).Equals(".aifc", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "AIFF-C output is not supported. Choose a .aif or .aiff file name.");
        string finalPath = Path.GetFullPath(path);
        if (doc.RequiresSaveAs && doc.FilePath is { } sourcePath &&
            Path.GetFullPath(sourcePath).Equals(finalPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Imported AIFF files must be saved to a different path so their original metadata is preserved.");
        }
        if (bitDepth is not (16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitDepth),
                "AIFF output must be 16, 24, or 32-bit PCM.");
        cancellationToken.ThrowIfCancellationRequested();

        float[][] sourceChannels = doc.Channels.ToArray();
        int channels = sourceChannels.Length;
        if (channels is <= 0 or > ushort.MaxValue)
            throw new InvalidOperationException("AIFF output requires between 1 and 65,535 channels.");
        int frames = sourceChannels[0].Length;
        if (sourceChannels.Any(channel => channel == null || channel.Length != frames))
            throw new InvalidOperationException("All document channels must have the same sample count.");
        if (doc.SampleRate <= 0)
            throw new InvalidOperationException("The document sample rate must be positive.");

        int bytesPerSample = bitDepth / 8;
        int blockAlign = checked(bytesPerSample * channels);
        long dataSizeLong = checked((long)frames * blockAlign);
        if (dataSizeLong > int.MaxValue - 1024)
            throw new InvalidOperationException(
                "Audio exceeds the 2 GB AIFF limit; export a selection or lower bit depth.");
        int dataSize = (int)dataSizeLong;
        int soundChunkSize = checked(8 + dataSize);

        // Whatever else the source file carried, written back after the audio — but only if it came
        // from an AIFF. A WAV's chunks are a different vocabulary in a different byte order.
        RiffMetadata metadata = doc.Riff is { IsAiff: true } carried ? carried : RiffMetadata.ForAiff();
        if (markers is { Count: > 0 })
        {
            var marks = new List<BroadcastMetadata.CuePoint>(markers.Count);
            for (int i = 0; i < markers.Count; i++)
                marks.Add(new BroadcastMetadata.CuePoint(i + 1,
                    Math.Clamp(markers[i].Position, 0, frames), markers[i].Name));

            metadata = metadata.Clone();
            metadata.Set("MARK", AiffMetadata.WriteMarkChunk(marks));
        }

        long extra = metadata.ByteLength;
        if (extra > int.MaxValue / 4)
            throw new InvalidOperationException("The metadata carried with this file is too large to write.");

        int formSize = checked(4 + (8 + 18) + (8 + soundChunkSize) + (soundChunkSize & 1) + (int)extra);

        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The AIFF output path has no directory.");
        string stagePath = Path.Combine(directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                WriteUInt32Big(writer, Form);
                WriteUInt32Big(writer, (uint)formSize);
                WriteUInt32Big(writer, Aiff);
                WriteUInt32Big(writer, Common);
                WriteUInt32Big(writer, 18);
                WriteUInt16Big(writer, (ushort)channels);
                WriteUInt32Big(writer, (uint)frames);
                WriteUInt16Big(writer, (ushort)bitDepth);
                WriteSampleRate(writer, doc.SampleRate);
                WriteUInt32Big(writer, SoundData);
                WriteUInt32Big(writer, (uint)soundChunkSize);
                WriteUInt32Big(writer, 0); // offset
                WriteUInt32Big(writer, 0); // block size

                // One shaper for the file, holding per-channel error state: noise shaping is a
                // feedback loop and cannot be shared across channels or rebuilt per sample.
                var shaper = new Dither(dither ? ditherKind : DitherKind.None, 16, channels,
                    doc.SampleRate, autoBlank: true);
                // Batched, for the same reason WavCodec.Save is: one Stream.Write per
                // sample frame is a call per 4 to 12 bytes for the length of the file.
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

                    EncodeFrameBigEndian(sourceChannels, frame, channels, bitDepth, shaper,
                        block.AsSpan(inBlock * blockAlign, blockAlign));
                    if (++inBlock == framesPerBlock)
                    {
                        writer.Write(block, 0, block.Length);
                        inBlock = 0;
                    }
                }
                if (inBlock > 0) writer.Write(block, 0, inBlock * blockAlign);

                cancellationToken.ThrowIfCancellationRequested();
                if ((soundChunkSize & 1) == 1) writer.Write((byte)0);
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

    /// <summary>
    /// Decodes the sound data one whole-frame block at a time, so only a modest
    /// block buffer is live alongside the float output instead of the entire chunk.
    /// </summary>
    private static void DecodeStreaming(
        BinaryReader reader,
        int bits,
        int channels,
        int blockAlign,
        int frames,
        bool floatingPoint,
        bool littleEndian,
        bool unsigned8Bit,
        float[][] destination,
        CancellationToken cancellationToken)
    {
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
            Decode(block, bits, channels, blockFrames, frameOffset, floatingPoint, littleEndian,
                unsigned8Bit, destination, cancellationToken);
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
                throw new InvalidDataException("The AIFF sound-data chunk is truncated.");
            offset += read;
        }
    }

    private static void Decode(
        byte[] data,
        int bits,
        int channels,
        int frames,
        int frameOffset,
        bool floatingPoint,
        bool littleEndian,
        bool unsigned8Bit,
        float[][] destination,
        CancellationToken cancellationToken)
    {
        // The format decision is made once, above the loop, rather than re-evaluated for
        // every sample of the file — which is how WavCodec.Decode has always done it.
        int offset = 0;
        if (floatingPoint && bits == 32)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 4)
                {
                    uint value = ReadUInt32(data, offset, littleEndian);
                    destination[channel][frameOffset + frame] =
                        BitConverter.Int32BitsToSingle(unchecked((int)value));
                }
            }
        }
        else if (floatingPoint)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 8)
                {
                    ulong value = ReadUInt64(data, offset, littleEndian);
                    destination[channel][frameOffset + frame] =
                        (float)BitConverter.Int64BitsToDouble(unchecked((long)value));
                }
            }
        }
        else if (bits == 8)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++)
                {
                    byte value = data[offset++];
                    destination[channel][frameOffset + frame] =
                        unsigned8Bit ? (value - 128) / 128f : unchecked((sbyte)value) / 128f;
                }
            }
        }
        else if (bits == 16)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 2)
                {
                    int value = littleEndian
                        ? (short)(data[offset] | data[offset + 1] << 8)
                        : (short)(data[offset] << 8 | data[offset + 1]);
                    destination[channel][frameOffset + frame] = value / 32768f;
                }
            }
        }
        else if (bits == 24)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 3)
                {
                    int value = littleEndian
                        ? (data[offset + 2] << 24 | data[offset + 1] << 16 | data[offset] << 8) >> 8
                        : (data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8) >> 8;
                    destination[channel][frameOffset + frame] = value / 8388608f;
                }
            }
        }
        else
        {
            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int channel = 0; channel < channels; channel++, offset += 4)
                {
                    uint value = ReadUInt32(data, offset, littleEndian);
                    destination[channel][frameOffset + frame] = unchecked((int)value) / 2147483648f;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Encodes one frame as big-endian signed PCM into <paramref name="buffer"/>, which must be one
    /// block long. The AIFF counterpart to <see cref="WavCodec.EncodeFrame"/>; the two differ only
    /// in byte order.
    /// </summary>
    private static void EncodeFrameBigEndian(IReadOnlyList<float[]> source, int frame, int channels,
        int bitDepth, Dither shaper, Span<byte> buffer)
    {
        int output = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            float sample = source[channel][frame];
            if (!float.IsFinite(sample)) sample = 0;
            sample = Math.Clamp(sample, -1f, 1f);
            switch (bitDepth)
            {
                case 16:
                {
                    // The shaper owns the quantiser: there is no error to feed back
                    // until the rounding has happened.
                    double value = shaper.Process(channel, sample) * 32768.0;
                    int quantized = Math.Clamp((int)Math.Round(value),
                        short.MinValue, short.MaxValue);
                    buffer[output++] = (byte)(quantized >> 8);
                    buffer[output++] = (byte)quantized;
                    break;
                }
                case 24:
                {
                    int quantized = Math.Clamp(
                        (int)Math.Round(sample * 8388608.0), -8388608, 8388607);
                    buffer[output++] = (byte)(quantized >> 16);
                    buffer[output++] = (byte)(quantized >> 8);
                    buffer[output++] = (byte)quantized;
                    break;
                }
                default:
                {
                    long scaled = (long)Math.Round(sample * 2147483648.0);
                    int quantized = (int)Math.Clamp(scaled, int.MinValue, int.MaxValue);
                    buffer[output++] = (byte)(quantized >> 24);
                    buffer[output++] = (byte)(quantized >> 16);
                    buffer[output++] = (byte)(quantized >> 8);
                    buffer[output++] = (byte)quantized;
                    break;
                }
            }
        }
    }


    private static int ReadSampleRate(BinaryReader reader)
    {
        ushort exponentAndSign = ReadUInt16Big(reader);
        ulong significand = ReadUInt64Big(reader);
        bool negative = (exponentAndSign & 0x8000) != 0;
        int exponent = exponentAndSign & 0x7FFF;
        if (negative || exponent == 0x7FFF || exponent == 0 && significand != 0)
            throw new InvalidDataException("The AIFF sample rate is invalid.");
        if (exponent == 0 && significand == 0) return 0;

        double value = Math.ScaleB((double)significand, exponent - 16383 - 63);
        if (!double.IsFinite(value) || value <= 0 || value > int.MaxValue)
            throw new InvalidDataException("The AIFF sample rate is outside the supported range.");
        int rounded = checked((int)Math.Round(value));
        if (Math.Abs(value - rounded) > 0.01)
            throw new InvalidDataException("Deep Groove requires an integer AIFF sample rate.");
        return rounded;
    }

    private static void WriteSampleRate(BinaryWriter writer, int sampleRate)
    {
        int highestBit = BitOperations.Log2((uint)sampleRate);
        WriteUInt16Big(writer, (ushort)(16383 + highestBit));
        WriteUInt64Big(writer, (ulong)(uint)sampleRate << (63 - highestBit));
    }

    private static uint ReadUInt32(byte[] data, int offset, bool littleEndian) => littleEndian
        ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
        : (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static ulong ReadUInt64(byte[] data, int offset, bool littleEndian)
    {
        ulong value = 0;
        if (littleEndian)
            for (int index = 7; index >= 0; index--) value = value << 8 | data[offset + index];
        else
            for (int index = 0; index < 8; index++) value = value << 8 | data[offset + index];
        return value;
    }

    private static ushort ReadUInt16Big(BinaryReader reader)
    {
        int high = reader.ReadByte();
        int low = reader.ReadByte();
        return (ushort)(high << 8 | low);
    }

    private static uint ReadUInt32Big(BinaryReader reader) =>
        (uint)(reader.ReadByte() << 24 | reader.ReadByte() << 16 |
               reader.ReadByte() << 8 | reader.ReadByte());

    private static ulong ReadUInt64Big(BinaryReader reader)
    {
        ulong value = 0;
        for (int index = 0; index < 8; index++) value = value << 8 | reader.ReadByte();
        return value;
    }

    private static void WriteUInt16Big(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteUInt32Big(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteUInt64Big(BinaryWriter writer, ulong value)
    {
        for (int shift = 56; shift >= 0; shift -= 8) writer.Write((byte)(value >> shift));
    }

    private static string FourCc(uint value) => new([
        (char)(value >> 24),
        (char)(value >> 16 & 0xFF),
        (char)(value >> 8 & 0xFF),
        (char)(value & 0xFF),
    ]);
}

