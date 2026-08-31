using System.Buffers.Binary;
using System.IO;
using System.Text;
using WaveLab.Audio.Dsp;

namespace WaveLab.Audio;

/// <summary>
/// Imports uncompressed DSF and DSDIFF audio as 44.1 kHz floating-point PCM.
/// </summary>
/// <remarks>
/// Deep Groove is a PCM editor: opening DSD is deliberately a one-way working conversion and
/// never makes the source file eligible for in-place Save. The converter first removes the
/// ultrasonic noise before decimation; treating each group of 64 DSD bits as a plain average
/// would fold shaped noise back into the audible band.
/// </remarks>
public static class DsdCodec
{
    public const int EditingSampleRate = 44_100;
    private const int Dsd64Rate = 2_822_400;
    private const int Dsd128Rate = 5_644_800;
    private const long MaximumDecodedBytes = 1536L * 1024 * 1024;
    // 0 dB-SACD is 50% of the 1-bit stream's theoretical modulation range. AudioGate and the
    // other converters Korg users compare against map that reference to 0 dBFS, leaving the
    // theoretical +6 dB-SACD limit representable above full scale in the float working copy.
    private const float DsdReferenceGain = 2f;

    public static AudioDocument Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.SequentialScan);
        if (stream.Length < 12) throw new InvalidDataException("The DSD file is truncated.");

        Span<byte> signature = stackalloc byte[4];
        ReadExactly(stream, signature);
        stream.Position = 0;

        DecodedDsd decoded = Encoding.ASCII.GetString(signature) switch
        {
            "DSD " => LoadDsf(stream, cancellationToken),
            "FRM8" => LoadDsdiff(stream, cancellationToken),
            _ => throw new InvalidDataException("The file is not a DSF or DSDIFF stream."),
        };

        string family = decoded.SampleRate == Dsd64Rate ? "DSD64" : "DSD128";
        var document = new AudioDocument(decoded.Channels, EditingSampleRate, 32)
        {
            FilePath = null,
            Title = $"{Path.GetFileNameWithoutExtension(path)} (DSD import).wav",
            CaptureNote = $"Imported from {family} {decoded.Container} "
                + $"({decoded.SampleRate / 1_000_000.0:0.0000} MHz, 1-bit); converted once to "
                + $"{EditingSampleRate / 1000.0:0.0} kHz / 32-bit float for editing. "
                + "The original DSD file was not changed.",
        };
        return document;
    }

    private static DecodedDsd LoadDsf(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        Expect(reader, "DSD ");
        ulong headerSize = reader.ReadUInt64();
        ulong statedFileSize = reader.ReadUInt64();
        _ = reader.ReadUInt64(); // ID3 offset; metadata is not carried into the PCM working copy.
        if (headerSize < 28 || headerSize > (ulong)stream.Length)
            throw new InvalidDataException("The DSF header size is invalid.");
        if (statedFileSize > (ulong)stream.Length)
            throw new InvalidDataException("The DSF file is shorter than its header states.");
        stream.Position = checked((long)headerSize);

        DsfFormat? format = null;
        long dataOffset = -1;
        ulong dataBytes = 0;
        while (stream.Position + 12 <= stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = FourCc(reader);
            ulong size = reader.ReadUInt64();
            if (size < 12 || size - 12 > (ulong)(stream.Length - stream.Position))
                throw new InvalidDataException($"The DSF {id} chunk extends past the file.");
            long payloadStart = stream.Position;
            ulong payloadSize = size - 12;

            if (id == "fmt ")
            {
                if (payloadSize < 40)
                    throw new InvalidDataException("The DSF format chunk is truncated.");
                uint version = reader.ReadUInt32();
                uint formatId = reader.ReadUInt32();
                _ = reader.ReadUInt32(); // channel layout; channel count drives the PCM document.
                uint channels = reader.ReadUInt32();
                uint sampleRate = reader.ReadUInt32();
                uint bitsPerSample = reader.ReadUInt32();
                ulong sampleCount = reader.ReadUInt64();
                uint blockSize = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                if (version != 1 || formatId != 0)
                    throw new NotSupportedException("Only uncompressed DSF version 1 audio is supported.");
                format = new DsfFormat(CheckedChannels(channels), CheckedRate(sampleRate),
                    bitsPerSample, sampleCount, CheckedBlockSize(blockSize));
            }
            else if (id == "data")
            {
                dataOffset = payloadStart;
                dataBytes = payloadSize;
            }

            stream.Position = checked(payloadStart + (long)payloadSize);
            if (format != null && dataOffset >= 0) break;
        }

        if (format == null || dataOffset < 0)
            throw new InvalidDataException("The DSF file has no format or audio-data chunk.");
        if (format.BitsPerSample is not (1u or 8u))
            throw new NotSupportedException(
                $"The DSF bit-order value {format.BitsPerSample} is not supported.");

        int outputFrames = CheckedOutputFrames(format.SampleCount, format.SampleRate, format.Channels);
        var decoders = CreateDecoders(format.Channels, format.SampleRate,
            leastSignificantBitFirst: format.BitsPerSample == 1, outputFrames);

        ulong bytesPerChannel = (format.SampleCount + 7) / 8;
        ulong blocks = (bytesPerChannel + (uint)format.BlockSize - 1) / (uint)format.BlockSize;
        ulong expectedDataBytes = checked(blocks * (uint)format.BlockSize * (uint)format.Channels);
        if (expectedDataBytes > dataBytes)
            throw new InvalidDataException("The DSF audio-data chunk is shorter than the format states.");

        stream.Position = dataOffset;
        var block = new byte[format.BlockSize];
        ulong consumedPerChannel = 0;
        for (ulong blockIndex = 0; blockIndex < blocks; blockIndex++)
        {
            int valid = checked((int)Math.Min((ulong)format.BlockSize,
                bytesPerChannel - consumedPerChannel));
            for (int channel = 0; channel < format.Channels; channel++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadExactly(stream, block);
                decoders[channel].Feed(block.AsSpan(0, valid), cancellationToken);
            }
            consumedPerChannel += (uint)valid;
        }

        return new DecodedDsd(Complete(decoders, cancellationToken), format.SampleRate, "DSF");
    }

    private static DecodedDsd LoadDsdiff(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        Expect(reader, "FRM8");
        ulong formSize = ReadUInt64BigEndian(reader);
        Expect(reader, "DSD ");
        long formEnd = checked(12L + (long)formSize);
        if (formEnd > stream.Length)
            throw new InvalidDataException("The DSDIFF file is shorter than its header states.");

        int sampleRate = 0;
        int channels = 0;
        string? compression = null;
        long dataOffset = -1;
        ulong dataBytes = 0;
        bool hasDst = false;

        while (stream.Position + 12 <= formEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = FourCc(reader);
            ulong size = ReadUInt64BigEndian(reader);
            if (size > (ulong)(formEnd - stream.Position))
                throw new InvalidDataException($"The DSDIFF {id} chunk extends past the form.");
            long payloadStart = stream.Position;

            if (id == "PROP")
            {
                ParseDsdiffProperties(reader, size, ref sampleRate, ref channels, ref compression);
            }
            else if (id == "DSD ")
            {
                dataOffset = payloadStart;
                dataBytes = size;
            }
            else if (id == "DST ")
            {
                hasDst = true;
            }

            stream.Position = checked(payloadStart + (long)size + (long)(size & 1));
        }

        if (hasDst || string.Equals(compression, "DST ", StringComparison.Ordinal))
            throw new NotSupportedException("DST-compressed DSDIFF is not supported; export uncompressed DSD first.");
        if (compression != null && compression != "DSD ")
            throw new NotSupportedException($"DSDIFF compression '{compression.Trim()}' is not supported.");
        sampleRate = CheckedRate(sampleRate);
        channels = CheckedChannels(channels);
        if (dataOffset < 0)
            throw new InvalidDataException("The DSDIFF file has no uncompressed DSD audio chunk.");
        if (dataBytes % (uint)channels != 0)
            throw new InvalidDataException("The DSDIFF audio data ends in a partial channel frame.");

        ulong bytesPerChannel = dataBytes / (uint)channels;
        ulong sampleCount = checked(bytesPerChannel * 8);
        int outputFrames = CheckedOutputFrames(sampleCount, sampleRate, channels);
        var decoders = CreateDecoders(channels, sampleRate,
            leastSignificantBitFirst: false, outputFrames);

        stream.Position = dataOffset;
        var buffer = new byte[128 * 1024];
        ulong remaining = dataBytes;
        int channelIndex = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = checked((int)Math.Min((ulong)buffer.Length, remaining));
            ReadExactly(stream, buffer.AsSpan(0, take));
            for (int index = 0; index < take; index++)
            {
                decoders[channelIndex].Feed(buffer[index]);
                if (++channelIndex == channels) channelIndex = 0;
            }
            remaining -= (uint)take;
        }

        return new DecodedDsd(Complete(decoders, cancellationToken), sampleRate, "DSDIFF");
    }

    private static void ParseDsdiffProperties(
        BinaryReader reader,
        ulong size,
        ref int sampleRate,
        ref int channels,
        ref string? compression)
    {
        if (size < 4) throw new InvalidDataException("The DSDIFF property chunk is truncated.");
        long start = reader.BaseStream.Position;
        long end = checked(start + (long)size);
        Expect(reader, "SND ");

        while (reader.BaseStream.Position + 12 <= end)
        {
            string id = FourCc(reader);
            ulong subSize = ReadUInt64BigEndian(reader);
            if (subSize > (ulong)(end - reader.BaseStream.Position))
                throw new InvalidDataException($"The DSDIFF {id} property extends past its chunk.");
            long payloadStart = reader.BaseStream.Position;
            if (id == "FS  ")
            {
                if (subSize < 4) throw new InvalidDataException("The DSDIFF sample-rate property is truncated.");
                sampleRate = checked((int)ReadUInt32BigEndian(reader));
            }
            else if (id == "CHNL")
            {
                if (subSize < 2) throw new InvalidDataException("The DSDIFF channel property is truncated.");
                channels = ReadUInt16BigEndian(reader);
                if (subSize < 2u + (uint)channels * 4u)
                    throw new InvalidDataException("The DSDIFF channel identifiers are truncated.");
            }
            else if (id == "CMPR")
            {
                if (subSize < 4) throw new InvalidDataException("The DSDIFF compression property is truncated.");
                compression = FourCc(reader);
            }
            reader.BaseStream.Position = checked(payloadStart + (long)subSize + (long)(subSize & 1));
        }
    }

    private static DsdPcmDecoder[] CreateDecoders(
        int channels,
        int sampleRate,
        bool leastSignificantBitFirst,
        int outputFrames)
    {
        var result = new DsdPcmDecoder[channels];
        for (int channel = 0; channel < channels; channel++)
            result[channel] = new DsdPcmDecoder(sampleRate, leastSignificantBitFirst, outputFrames);
        return result;
    }

    private static float[][] Complete(DsdPcmDecoder[] decoders, CancellationToken cancellationToken)
    {
        var channels = new float[decoders.Length][];
        for (int channel = 0; channel < channels.Length; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            channels[channel] = decoders[channel].Complete(cancellationToken);
        }
        return channels;
    }

    private static int CheckedRate(long sampleRate)
    {
        if (sampleRate is not (Dsd64Rate or Dsd128Rate))
            throw new NotSupportedException(
                $"DSD sample rate {sampleRate} Hz is not supported; Deep Groove currently imports DSD64 and DSD128.");
        return (int)sampleRate;
    }

    private static int CheckedChannels(long channels)
    {
        if (channels is < 1 or > 8)
            throw new NotSupportedException($"DSD channel count {channels} is not supported.");
        return (int)channels;
    }

    private static int CheckedBlockSize(uint blockSize)
    {
        if (blockSize is 0 or > 1024 * 1024)
            throw new InvalidDataException("The DSF channel block size is invalid.");
        return (int)blockSize;
    }

    private static int CheckedOutputFrames(ulong sampleCount, int sampleRate, int channels)
    {
        int ratio = sampleRate / EditingSampleRate;
        ulong frames = sampleCount / (uint)ratio;
        if (frames == 0) throw new InvalidDataException("The DSD file contains no complete PCM frame.");
        if (frames > (ulong)Array.MaxLength)
            throw new InvalidDataException("The converted DSD audio exceeds the maximum supported sample count.");
        ulong decodedBytes = checked(frames * (uint)channels * sizeof(float));
        if (decodedBytes > MaximumDecodedBytes)
            throw new InvalidDataException(
                "The converted DSD audio would exceed 1.5 GB; divide the recording before importing it.");
        return (int)frames;
    }

    private static void Expect(BinaryReader reader, string expected)
    {
        string actual = FourCc(reader);
        if (actual != expected)
            throw new InvalidDataException($"Expected DSD chunk '{expected}', found '{actual}'.");
    }

    private static string FourCc(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new EndOfStreamException("The DSD chunk identifier is truncated.");
        return Encoding.ASCII.GetString(bytes);
    }

    private static ushort ReadUInt16BigEndian(BinaryReader reader)
    {
        Span<byte> value = stackalloc byte[2];
        ReadExactly(reader.BaseStream, value);
        return BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    private static uint ReadUInt32BigEndian(BinaryReader reader)
    {
        Span<byte> value = stackalloc byte[4];
        ReadExactly(reader.BaseStream, value);
        return BinaryPrimitives.ReadUInt32BigEndian(value);
    }

    private static ulong ReadUInt64BigEndian(BinaryReader reader)
    {
        Span<byte> value = stackalloc byte[8];
        ReadExactly(reader.BaseStream, value);
        return BinaryPrimitives.ReadUInt64BigEndian(value);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer[read..]);
            if (count == 0) throw new EndOfStreamException("The DSD file is truncated.");
            read += count;
        }
    }

    private sealed record DecodedDsd(float[][] Channels, int SampleRate, string Container);
    private sealed record DsfFormat(
        int Channels,
        int SampleRate,
        uint BitsPerSample,
        ulong SampleCount,
        int BlockSize);

    /// <summary>
    /// Streaming two-stage decimator. A short bit-rate FIR rejects everything that would alias
    /// while reducing eight 1-bit samples to one float; a long FFT FIR then makes the narrow
    /// 20–22.05 kHz transition before the final integer decimation.
    /// </summary>
    private sealed class DsdPcmDecoder
    {
        private const int BitFilterTaps = 128;
        private const int BitFilterBytes = BitFilterTaps / 8;
        private const int ConvolutionBlockSize = 8192;
        private const byte DsdSilence = 0xAA;

        private readonly int _decimation;
        private readonly float[] _output;
        private readonly float[][] _byteTables;
        private readonly byte[] _bitHistory = new byte[BitFilterBytes];
        private readonly PartitionedConvolver _lowPass;
        private readonly float[] _block = new float[ConvolutionBlockSize];
        private readonly long _totalDelay;
        private int _historyPosition;
        private int _blockFill;
        private long _filteredSamples;
        private int _outputIndex;

        public DsdPcmDecoder(int dsdRate, bool leastSignificantBitFirst, int outputFrames)
        {
            int intermediateRate = dsdRate / 8;
            _decimation = intermediateRate / EditingSampleRate;
            _output = new float[outputFrames];
            _byteTables = BuildByteTables(dsdRate, leastSignificantBitFirst);
            Array.Fill(_bitHistory, DsdSilence);

            int audioTaps = dsdRate == Dsd64Rate ? 2049 : 4097;
            // DesignKernel's cutoff is the transition midpoint (-6 dB). Centre it at 21 kHz so
            // 20 kHz remains in the passband and the stopband still begins below 22.05 kHz.
            float[] kernel = DesignLowPass(intermediateRate, 21_000, audioTaps);
            _lowPass = new PartitionedConvolver(kernel, channels: 1, ConvolutionBlockSize);
            _totalDelay = (long)Math.Round((BitFilterTaps - 1) / 16.0)
                + (audioTaps - 1) / 2;
        }

        public void Feed(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                if ((index & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                Feed(bytes[index]);
            }
        }

        public void Feed(byte value)
        {
            _bitHistory[_historyPosition] = value;
            double sum = 0;
            int ring = _historyPosition;
            for (int byteOffset = 0; byteOffset < BitFilterBytes; byteOffset++)
            {
                sum += _byteTables[byteOffset][_bitHistory[ring]];
                if (--ring < 0) ring = BitFilterBytes - 1;
            }
            if (++_historyPosition == BitFilterBytes) _historyPosition = 0;

            _block[_blockFill++] = (float)sum;
            if (_blockFill == _block.Length) ProcessBlock();
        }

        public float[] Complete(CancellationToken cancellationToken)
        {
            // Silence supplies the linear-phase tail. Completing a
            // convolution block can require more than the mathematical delay, but output stops at
            // the exact duration stated by the container.
            long maximumFlush = _totalDelay + _block.Length * 2L;
            long flushed = 0;
            while (_outputIndex < _output.Length && flushed++ < maximumFlush)
            {
                if ((flushed & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                Feed(DsdSilence);
            }
            if (_outputIndex != _output.Length)
                throw new InvalidDataException("The DSD stream ended before its stated duration could be decoded.");
            return _output;
        }

        private void ProcessBlock()
        {
            _lowPass.ProcessBlock(0, _block);
            long blockStart = _filteredSamples;
            for (int index = 0; index < _block.Length && _outputIndex < _output.Length; index++)
            {
                long aligned = blockStart + index - _totalDelay;
                if (aligned >= 0 && aligned % _decimation == 0)
                {
                    float sample = _block[index] * DsdReferenceGain;
                    _output[_outputIndex++] = float.IsFinite(sample) ? sample : 0f;
                }
            }
            _filteredSamples += _block.Length;
            _blockFill = 0;
            Array.Clear(_block);
        }

        private static float[][] BuildByteTables(int dsdRate, bool lsbFirst)
        {
            int intermediateRate = dsdRate / 8;
            // Stop comfortably before the byte-rate Nyquist: the long second stage owns the final
            // audible-band edge, while this one only prevents the first eight-to-one fold.
            double cutoff = intermediateRate * 0.45;
            double[] taps = DesignKernel(dsdRate, cutoff, BitFilterTaps);
            var tables = new float[BitFilterBytes][];

            for (int byteOffset = 0; byteOffset < BitFilterBytes; byteOffset++)
            {
                var table = new float[256];
                for (int value = 0; value < table.Length; value++)
                {
                    double sum = 0;
                    for (int chronologicalBit = 0; chronologicalBit < 8; chronologicalBit++)
                    {
                        int physicalBit = lsbFirst ? chronologicalBit : 7 - chronologicalBit;
                        double sample = (value & (1 << physicalBit)) != 0 ? 1.0 : -1.0;
                        int lag = byteOffset * 8 + 7 - chronologicalBit;
                        sum += taps[lag] * sample;
                    }
                    table[value] = (float)sum;
                }
                tables[byteOffset] = table;
            }
            return tables;
        }

        private static float[] DesignLowPass(int sampleRate, double cutoff, int taps)
        {
            double[] designed = DesignKernel(sampleRate, cutoff, taps);
            var result = new float[designed.Length];
            for (int index = 0; index < result.Length; index++) result[index] = (float)designed[index];
            return result;
        }

        private static double[] DesignKernel(int sampleRate, double cutoff, int taps)
        {
            var kernel = new double[taps];
            double center = (taps - 1) / 2.0;
            double normalizedCutoff = 2 * cutoff / sampleRate;
            double sum = 0;
            for (int index = 0; index < taps; index++)
            {
                double distance = index - center;
                double sinc = distance == 0
                    ? normalizedCutoff
                    : Math.Sin(Math.PI * normalizedCutoff * distance) / (Math.PI * distance);
                double phase = 2 * Math.PI * index / (taps - 1);
                double window = 0.35875 - 0.48829 * Math.Cos(phase)
                    + 0.14128 * Math.Cos(2 * phase) - 0.01168 * Math.Cos(3 * phase);
                kernel[index] = sinc * window;
                sum += kernel[index];
            }
            if (!double.IsFinite(sum) || Math.Abs(sum) < 1e-15)
                throw new InvalidOperationException("Unable to design the DSD decimation filter.");
            for (int index = 0; index < kernel.Length; index++) kernel[index] /= sum;
            return kernel;
        }
    }
}
