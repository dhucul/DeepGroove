using System.Buffers.Binary;
using System.Text;
using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class DsdCodecTests : IDisposable
{
    private const int Dsd64Rate = 2_822_400;
    private const int PcmFrames = 4_096;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-dsd").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void DsfChannelBlocksAndLsbBitOrderBecomeAnUnsavedPcmWorkingCopy()
    {
        string path = Path.Combine(_directory, "korg-transfer.dsf");
        byte[][] channels = ConstantDsd(PcmFrames, left: 0xFF, right: 0x00);
        File.WriteAllBytes(path, MakeDsf(channels, bitsPerSample: 1));

        AudioDocument document = AudioImporter.Load(path);

        AssertImportedWorkingCopy(document, "DSF");
        // The theoretical all-one/all-zero limits are +6/-infinity dB-SACD. A working float
        // copy keeps that headroom above 0 dBFS rather than attenuating 0 dB-SACD by six decibels.
        AssertNear(2f, Middle(document.Channels[0]), 0.03f);
        AssertNear(-2f, Middle(document.Channels[1]), 0.03f);
        Assert.Contains("*.dsf", AudioImporter.OpenFilter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.dff", AudioImporter.OpenFilter, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DsdiffByteInterleavingKeepsChannelsSeparate()
    {
        string path = Path.Combine(_directory, "korg-transfer.dff");
        byte[][] channels = ConstantDsd(PcmFrames, left: 0xFF, right: 0x00);
        File.WriteAllBytes(path, MakeDsdiff(channels));

        AudioDocument document = AudioImporter.Load(path);

        AssertImportedWorkingCopy(document, "DSDIFF");
        AssertNear(2f, Middle(document.Channels[0]), 0.03f);
        AssertNear(-2f, Middle(document.Channels[1]), 0.03f);
    }

    [Fact]
    public void BalancedDsdSilenceDecimatesNearZeroRatherThanAliasingUltrasonicEnergy()
    {
        string path = Path.Combine(_directory, "silence.dsf");
        byte[][] channels = ConstantDsd(PcmFrames, left: 0xAA, right: 0x55);
        File.WriteAllBytes(path, MakeDsf(channels, bitsPerSample: 1));

        AudioDocument document = DsdCodec.Load(path);

        Assert.All(Middle(document.Channels[0]), sample => Assert.InRange(sample, -0.001f, 0.001f));
        Assert.All(Middle(document.Channels[1]), sample => Assert.InRange(sample, -0.001f, 0.001f));
    }

    [Fact]
    public void DstCompressedDsdiffIsRejectedExplicitly()
    {
        string path = Path.Combine(_directory, "compressed.dff");
        File.WriteAllBytes(path, MakeDsdiff(ConstantDsd(64, 0xAA, 0x55), compression: "DST "));

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => DsdCodec.Load(path));

        Assert.Contains("DST-compressed", error.Message);
    }

    [Fact]
    public void Dsd128UsesTheSameDurationAndEditingRate()
    {
        const int frames = 2_048;
        string path = Path.Combine(_directory, "double-rate.dsf");
        byte[][] channels = ConstantDsd(frames, left: 0xFF, right: 0x00, sampleRate: 5_644_800);
        File.WriteAllBytes(path, MakeDsf(channels, bitsPerSample: 1, sampleRate: 5_644_800));

        AudioDocument document = DsdCodec.Load(path);

        Assert.Equal(frames, document.Length);
        Assert.Equal(DsdCodec.EditingSampleRate, document.SampleRate);
        Assert.Contains("DSD128", document.CaptureNote);
        AssertNear(2f, Middle(document.Channels[0]), 0.03f);
        AssertNear(-2f, Middle(document.Channels[1]), 0.03f);
    }

    [Fact]
    public void AModulatedAudioToneKeepsItsFrequencyAndLevel()
    {
        const int frames = 8_192;
        const double frequency = 997;
        const double amplitude = 0.4;
        string path = Path.Combine(_directory, "tone.dsf");
        byte[] tone = ModulateSine(frames, Dsd64Rate, frequency, amplitude, lsbFirst: true);
        File.WriteAllBytes(path, MakeDsf([tone, tone], bitsPerSample: 1));

        AudioDocument document = DsdCodec.Load(path);
        float[] samples = document.Channels[0];
        int start = 1_024;
        int count = samples.Length - start * 2;
        double re = 0, im = 0, dc = 0;
        for (int index = 0; index < count; index++)
        {
            double sample = samples[start + index];
            double phase = 2 * Math.PI * frequency * (start + index) / document.SampleRate;
            re += sample * Math.Cos(phase);
            im -= sample * Math.Sin(phase);
            dc += sample;
        }
        double measuredAmplitude = 2 * Math.Sqrt(re * re + im * im) / count;

        Assert.InRange(measuredAmplitude, amplitude * 2 - 0.03, amplitude * 2 + 0.03);
        Assert.InRange(dc / count, -0.004, 0.004);
    }

    [Fact]
    public void DsfAndDsdiffBitOrderBranchesDecodeTheSameChronologicalStream()
    {
        const int frames = 8_192;
        byte[] leftLsb = ModulateSine(frames, Dsd64Rate, 997, 0.25, lsbFirst: true);
        byte[] rightLsb = ModulateSine(frames, Dsd64Rate, 7_901, 0.2, lsbFirst: true);
        byte[] leftMsb = ModulateSine(frames, Dsd64Rate, 997, 0.25, lsbFirst: false);
        byte[] rightMsb = ModulateSine(frames, Dsd64Rate, 7_901, 0.2, lsbFirst: false);

        string lsbPath = Path.Combine(_directory, "bit-order-lsb.dsf");
        string msbPath = Path.Combine(_directory, "bit-order-msb.dsf");
        string dffPath = Path.Combine(_directory, "bit-order-msb.dff");
        File.WriteAllBytes(lsbPath, MakeDsf([leftLsb, rightLsb], bitsPerSample: 1));
        File.WriteAllBytes(msbPath, MakeDsf([leftMsb, rightMsb], bitsPerSample: 8));
        File.WriteAllBytes(dffPath, MakeDsdiff([leftMsb, rightMsb]));

        AudioDocument lsb = DsdCodec.Load(lsbPath);
        AudioDocument msb = DsdCodec.Load(msbPath);
        AudioDocument dff = DsdCodec.Load(dffPath);

        // Startup padding uses the same physical 0xAA byte, whose chronological phase reverses
        // with bit order. Once the linear-phase filters have settled, the decoded programme must
        // be byte-for-byte identical.
        Assert.Equal(Middle(lsb.Channels[0]), Middle(msb.Channels[0]));
        Assert.Equal(Middle(lsb.Channels[1]), Middle(msb.Channels[1]));
        Assert.Equal(Middle(lsb.Channels[0]), Middle(dff.Channels[0]));
        Assert.Equal(Middle(lsb.Channels[1]), Middle(dff.Channels[1]));
    }

    [Fact]
    public void EditingLowPassKeepsTwentyKilohertzAndRejectsTheNyquistGuardBand()
    {
        const int frames = 16_384;
        const double amplitude = 0.2;
        byte[] pass = ModulateSine(frames, Dsd64Rate, 20_000, amplitude, lsbFirst: true);
        byte[] stop = ModulateSine(frames, Dsd64Rate, 21_800, amplitude, lsbFirst: true);
        string passPath = Path.Combine(_directory, "passband.dsf");
        string stopPath = Path.Combine(_directory, "stopband.dsf");
        File.WriteAllBytes(passPath, MakeDsf([pass, pass], bitsPerSample: 1));
        File.WriteAllBytes(stopPath, MakeDsf([stop, stop], bitsPerSample: 1));

        AudioDocument passed = DsdCodec.Load(passPath);
        AudioDocument stopped = DsdCodec.Load(stopPath);

        Assert.InRange(MeasureToneAmplitude(passed.Channels[0], 20_000),
            amplitude * 2 - 0.03, amplitude * 2 + 0.03);
        Assert.InRange(MeasureToneAmplitude(stopped.Channels[0], 21_800), 0, 0.01);
    }

    private static void AssertImportedWorkingCopy(AudioDocument document, string container)
    {
        Assert.Equal(DsdCodec.EditingSampleRate, document.SampleRate);
        Assert.Equal(32, document.SourceBitDepth);
        Assert.Equal(2, document.ChannelCount);
        Assert.Equal(PcmFrames, document.Length);
        Assert.Null(document.FilePath);
        Assert.Equal("korg-transfer (DSD import).wav", document.Title);
        Assert.Contains(container, document.CaptureNote);
        Assert.Contains("original DSD file was not changed", document.CaptureNote);
    }

    private static float[] Middle(float[] samples) =>
        samples.AsSpan(samples.Length / 3, samples.Length / 3).ToArray();

    private static void AssertNear(float expected, IEnumerable<float> samples, float tolerance) =>
        Assert.All(samples, sample => Assert.InRange(sample, expected - tolerance, expected + tolerance));

    private static double MeasureToneAmplitude(float[] samples, double frequency)
    {
        int margin = Math.Min(2_048, samples.Length / 4);
        int count = samples.Length - margin * 2;
        double re = 0, im = 0;
        for (int index = 0; index < count; index++)
        {
            int position = margin + index;
            double phase = 2 * Math.PI * frequency * position / DsdCodec.EditingSampleRate;
            re += samples[position] * Math.Cos(phase);
            im -= samples[position] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(re * re + im * im) / count;
    }

    private static byte[][] ConstantDsd(
        int pcmFrames,
        byte left,
        byte right,
        int sampleRate = Dsd64Rate)
    {
        int bytes = checked(pcmFrames * (sampleRate / DsdCodec.EditingSampleRate) / 8);
        return
        [
            Enumerable.Repeat(left, bytes).ToArray(),
            Enumerable.Repeat(right, bytes).ToArray(),
        ];
    }

    private static byte[] MakeDsf(
        byte[][] channels,
        uint bitsPerSample,
        int sampleRate = Dsd64Rate)
    {
        const int blockSize = 4096;
        ulong sampleCount = checked((ulong)channels[0].Length * 8);
        int blocks = (channels[0].Length + blockSize - 1) / blockSize;
        int dataPayload = checked(blocks * blockSize * channels.Length);
        int fileSize = 28 + 52 + 12 + dataPayload;
        using var stream = new MemoryStream(fileSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        FourCc(writer, "DSD ");
        writer.Write(28UL);
        writer.Write((ulong)fileSize);
        writer.Write(0UL);

        FourCc(writer, "fmt ");
        writer.Write(52UL);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(2u);
        writer.Write((uint)channels.Length);
        writer.Write((uint)sampleRate);
        writer.Write(bitsPerSample);
        writer.Write(sampleCount);
        writer.Write((uint)blockSize);
        writer.Write(0u);

        FourCc(writer, "data");
        writer.Write((ulong)(12 + dataPayload));
        byte[] padding = new byte[blockSize];
        for (int block = 0; block < blocks; block++)
        {
            for (int channel = 0; channel < channels.Length; channel++)
            {
                int start = block * blockSize;
                int take = Math.Min(blockSize, channels[channel].Length - start);
                writer.Write(channels[channel], start, take);
                if (take < blockSize) writer.Write(padding, 0, blockSize - take);
            }
        }
        return stream.ToArray();
    }

    private static byte[] ModulateSine(
        int pcmFrames,
        int sampleRate,
        double frequency,
        double amplitude,
        bool lsbFirst)
    {
        int bitCount = checked(pcmFrames * (sampleRate / DsdCodec.EditingSampleRate));
        var bytes = new byte[(bitCount + 7) / 8];
        double error = 0;
        for (int bit = 0; bit < bitCount; bit++)
        {
            double target = amplitude * Math.Sin(2 * Math.PI * frequency * bit / sampleRate);
            error += target;
            int quantized = error >= 0 ? 1 : -1;
            error -= quantized;
            if (quantized <= 0) continue;
            int physical = lsbFirst ? bit & 7 : 7 - (bit & 7);
            bytes[bit >> 3] |= (byte)(1 << physical);
        }
        return bytes;
    }

    private static byte[] MakeDsdiff(byte[][] channels, string compression = "DSD ")
    {
        using var properties = new MemoryStream();
        using (var writer = new BinaryWriter(properties, Encoding.ASCII, leaveOpen: true))
        {
            FourCc(writer, "SND ");
            ChunkBigEndian(writer, "FS  ", payload => WriteUInt32BigEndian(payload, Dsd64Rate));
            ChunkBigEndian(writer, "CHNL", payload =>
            {
                WriteUInt16BigEndian(payload, (ushort)channels.Length);
                FourCc(payload, "SLFT");
                FourCc(payload, "SRGT");
            });
            ChunkBigEndian(writer, "CMPR", payload =>
            {
                FourCc(payload, compression);
                payload.Write((byte)0);
            });
        }

        using var audio = new MemoryStream();
        for (int index = 0; index < channels[0].Length; index++)
            for (int channel = 0; channel < channels.Length; channel++)
                audio.WriteByte(channels[channel][index]);

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
        {
            FourCc(writer, "DSD ");
            ChunkBigEndian(writer, "PROP", payload => payload.Write(properties.ToArray()));
            if (compression == "DST ")
                ChunkBigEndian(writer, "DST ", payload => payload.Write(audio.ToArray()));
            else
                ChunkBigEndian(writer, "DSD ", payload => payload.Write(audio.ToArray()));
        }

        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true))
        {
            FourCc(writer, "FRM8");
            WriteUInt64BigEndian(writer, checked((ulong)body.Length));
            writer.Write(body.ToArray());
        }
        return file.ToArray();
    }

    private static void ChunkBigEndian(BinaryWriter writer, string id, Action<BinaryWriter> writePayload)
    {
        using var payload = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true))
            writePayload(payloadWriter);
        FourCc(writer, id);
        WriteUInt64BigEndian(writer, (ulong)payload.Length);
        writer.Write(payload.ToArray());
        if ((payload.Length & 1) != 0) writer.Write((byte)0);
    }

    private static void FourCc(BinaryWriter writer, string value) =>
        writer.Write(Encoding.ASCII.GetBytes(value));

    private static void WriteUInt16BigEndian(BinaryWriter writer, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteUInt64BigEndian(BinaryWriter writer, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        writer.Write(bytes);
    }
}
