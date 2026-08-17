using System.IO;
using System.Text;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>
/// Sample-accurate RIFF/WAVE reader and writer.
/// Reads: PCM 16/24/32-bit int and 32/64-bit IEEE float, plus WAVE_FORMAT_EXTENSIBLE.
/// Writes: PCM 16-bit (with optional TPDF dither), PCM 24-bit, and 32-bit IEEE float.
/// </summary>
public static class WavCodec
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    public static AudioDocument Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (fs.Length < 12) throw new InvalidDataException("The WAV header is truncated.");
        if (br.ReadUInt32() != 0x46464952) throw new InvalidDataException("Not a RIFF file."); // "RIFF"
        br.ReadUInt32(); // RIFF size
        if (br.ReadUInt32() != 0x45564157) throw new InvalidDataException("Not a WAVE file."); // "WAVE"

        ushort format = 0, channels = 0, bits = 0;
        uint sampleRate = 0, declaredByteRate = 0;
        ushort declaredBlockAlign = 0;
        long dataStart = -1;
        int dataSize = 0;
        var metadata = new RiffMetadata();

        while (fs.Position + 8 <= fs.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint chunkId = br.ReadUInt32();
            uint chunkSize = br.ReadUInt32();
            long chunkStart = fs.Position;
            long chunkEnd = checked(chunkStart + chunkSize);
            long nextChunk = checked(chunkEnd + (chunkSize & 1));
            if (chunkEnd > fs.Length || nextChunk > fs.Length)
                throw new InvalidDataException("The WAV contains a truncated chunk.");

            if (chunkId == 0x20746D66) // "fmt "
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
            else if (chunkId == 0x61746164) // "data"
            {
                if ((ulong)chunkSize > (ulong)Array.MaxLength)
                    throw new InvalidDataException("The WAV data chunk is too large to load into memory.");
                // Located now, decoded block by block below: buffering the whole
                // chunk would hold the raw bytes and the float output at once.
                dataStart = chunkStart;
                dataSize = (int)chunkSize;
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
        int frameCount = dataSize / blockAlign;
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
    public static void Save(AudioDocument doc, string path, int bitDepth, bool dither = true,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null,
        DitherKind ditherKind = DitherKind.FlatTpdf, IReadOnlyList<Marker>? markers = null)
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
        long dataSizeLong = (long)frames * blockAlign;
        if (dataSizeLong > int.MaxValue - 1024)
            throw new InvalidOperationException("Audio exceeds the 2 GB WAV limit; export a selection or lower bit depth.");
        long byteRate = (long)sampleRate * blockAlign;
        if (byteRate > uint.MaxValue)
            throw new InvalidOperationException("The WAV byte rate exceeds the format limit.");

        int dataSize = (int)dataSizeLong;
        bool fact = formatTag == FormatIeeeFloat;

        // Whatever else the source file carried, written back after the audio. Ancillary chunks sit
        // after data by convention, and a reader that does not recognise one skips it by its length.
        RiffMetadata metadata = doc.Riff ?? new RiffMetadata();
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

        long extraLength = metadata.ByteLength;
        if (extraLength > int.MaxValue / 4)
            throw new InvalidOperationException("The metadata carried with this file is too large to write.");
        var extra = (int)extraLength;

        int riffSize = 4 + (8 + 16) + (fact ? 8 + 4 : 0) + (8 + dataSize) + (dataSize & 1) + extra;

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
                writer.Write(0x46464952u);            // RIFF
                writer.Write(riffSize);
                writer.Write(0x45564157u);            // WAVE
                writer.Write(0x20746D66u);            // fmt_
                writer.Write(16);
                writer.Write(formatTag);
                writer.Write((ushort)channels);
                writer.Write(sampleRate);
                writer.Write((uint)byteRate);
                writer.Write((ushort)blockAlign);
                writer.Write((ushort)bitDepth);
                if (fact)
                {
                    writer.Write(0x74636166u);        // fact
                    writer.Write(4);
                    writer.Write(frames);
                }
                writer.Write(0x61746164u);            // data
                writer.Write(dataSize);

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
}


