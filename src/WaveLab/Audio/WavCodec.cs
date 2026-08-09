using System.IO;
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

    public static AudioDocument Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (br.ReadUInt32() != 0x46464952) throw new InvalidDataException("Not a RIFF file."); // "RIFF"
        br.ReadUInt32(); // riff size
        if (br.ReadUInt32() != 0x45564157) throw new InvalidDataException("Not a WAVE file."); // "WAVE"

        ushort format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        byte[]? data = null;

        while (fs.Position + 8 <= fs.Length)
        {
            uint chunkId = br.ReadUInt32();
            uint chunkSize = br.ReadUInt32();
            long chunkStart = fs.Position;

            if (chunkId == 0x20746D66) // "fmt "
            {
                format = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();  // byte rate
                br.ReadUInt16(); // block align
                bits = br.ReadUInt16();
                if (format == FormatExtensible && chunkSize >= 40)
                {
                    br.ReadUInt16(); // cbSize
                    br.ReadUInt16(); // valid bits
                    br.ReadUInt32(); // channel mask
                    format = br.ReadUInt16(); // first 2 bytes of SubFormat GUID
                }
            }
            else if (chunkId == 0x61746164) // "data"
            {
                long size = Math.Min(chunkSize, fs.Length - fs.Position);
                data = br.ReadBytes((int)size);
            }

            fs.Position = chunkStart + chunkSize + (chunkSize & 1); // chunks are word-aligned
        }

        if (data == null || channels == 0 || sampleRate == 0)
            throw new InvalidDataException("Missing fmt or data chunk.");
        if (format != FormatPcm && format != FormatIeeeFloat)
            throw new InvalidDataException($"Unsupported WAV format tag {format}.");

        int bytesPerSample = bits / 8;
        int frameCount = data.Length / (bytesPerSample * channels);
        var ch = new float[channels][];
        for (int c = 0; c < channels; c++) ch[c] = new float[frameCount];

        Decode(data, format, bits, channels, frameCount, ch);

        int sourceBits = format == FormatIeeeFloat ? 32 : Math.Min((int)bits, 32);
        return new AudioDocument(ch, sampleRate, sourceBits)
        {
            FilePath = path,
            Title = Path.GetFileName(path),
        };
    }

    private static void Decode(byte[] data, ushort format, int bits, int channels, int frames, float[][] ch)
    {
        int i = 0;
        if (format == FormatPcm && bits == 16)
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++, i += 2)
                    ch[c][f] = BitConverter.ToInt16(data, i) / 32768f;
        else if (format == FormatPcm && bits == 24)
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++, i += 3)
                {
                    int v = (data[i + 2] << 24 | data[i + 1] << 16 | data[i] << 8) >> 8;
                    ch[c][f] = v / 8388608f;
                }
        else if (format == FormatPcm && bits == 32)
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++, i += 4)
                    ch[c][f] = BitConverter.ToInt32(data, i) / 2147483648f;
        else if (format == FormatIeeeFloat && bits == 32)
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++, i += 4)
                    ch[c][f] = BitConverter.ToSingle(data, i);
        else if (format == FormatIeeeFloat && bits == 64)
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++, i += 8)
                    ch[c][f] = (float)BitConverter.ToDouble(data, i);
        else
            throw new InvalidDataException($"Unsupported sample format: tag {format}, {bits}-bit.");
    }

    /// <summary>Write the document. bitDepth: 16, 24, or 32 (IEEE float). TPDF dither applies to 16-bit only.</summary>
    public static void Save(AudioDocument doc, string path, int bitDepth, bool dither = true,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        int channels = doc.ChannelCount;
        int frames = doc.Length;
        ushort formatTag = bitDepth == 32 ? FormatIeeeFloat : FormatPcm;
        int bytesPerSample = bitDepth / 8;
        int blockAlign = bytesPerSample * channels;
        long dataSizeL = (long)frames * blockAlign;
        if (dataSizeL > int.MaxValue - 1024)
            throw new InvalidOperationException("Audio exceeds the 2 GB WAV limit — export a selection or a lower bit depth.");
        int dataSize = (int)dataSizeL;
        bool fact = formatTag == FormatIeeeFloat;

        int riffSize = 4 + (8 + 16) + (fact ? 8 + 4 : 0) + (8 + dataSize) + (dataSize & 1);

        bw.Write(0x46464952u);            // RIFF
        bw.Write(riffSize);
        bw.Write(0x45564157u);            // WAVE
        bw.Write(0x20746D66u);            // fmt_
        bw.Write(16);
        bw.Write(formatTag);
        bw.Write((ushort)channels);
        bw.Write(doc.SampleRate);
        bw.Write(doc.SampleRate * blockAlign);
        bw.Write((ushort)blockAlign);
        bw.Write((ushort)bitDepth);
        if (fact)
        {
            bw.Write(0x74636166u);        // fact
            bw.Write(4);
            bw.Write(frames);
        }
        bw.Write(0x61746164u);            // data
        bw.Write(dataSize);

        var tpdf = new TpdfDither();
        var buffer = new byte[blockAlign];
        for (int f = 0; f < frames; f++)
        {
            if ((f & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(frames > 0 ? (double)f / frames : 1);
            }
            int o = 0;
            for (int c = 0; c < channels; c++)
            {
                float s = doc.Channels[c][f];
                switch (bitDepth)
                {
                    case 16:
                    {
                        // Decode uses signed full-scale divisors (32768 / 8388608).
                        // Mirror that here so untouched integer PCM round-trips exactly;
                        // the positive endpoint is handled by the signed clamp.
                        double v = s * 32768.0;
                        if (dither) v += tpdf.Next();
                        int q = (int)Math.Round(v);
                        q = Math.Clamp(q, short.MinValue, short.MaxValue);
                        buffer[o++] = (byte)q;
                        buffer[o++] = (byte)(q >> 8);
                        break;
                    }
                    case 24:
                    {
                        int q = (int)Math.Round(Math.Clamp(s, -1f, 1f) * 8388608.0);
                        q = Math.Clamp(q, -8388608, 8388607);
                        buffer[o++] = (byte)q;
                        buffer[o++] = (byte)(q >> 8);
                        buffer[o++] = (byte)(q >> 16);
                        break;
                    }
                    default:
                    {
                        var b = BitConverter.GetBytes(s);
                        buffer[o++] = b[0]; buffer[o++] = b[1]; buffer[o++] = b[2]; buffer[o++] = b[3];
                        break;
                    }
                }
            }
            bw.Write(buffer);
        }
        if ((dataSize & 1) == 1) bw.Write((byte)0);
        progress?.Report(1);
    }
}
