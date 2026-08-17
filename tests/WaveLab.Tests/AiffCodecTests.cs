using System.Text;
using NAudio.Wave;
using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

public sealed class AiffCodecTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void ClassicPcmRoundTripsWithBigEndianSamples(int bitDepth)
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, $"roundtrip-{bitDepth}.aiff");
        try
        {
            float[][] samples =
            [
                [-1f, -0.25f, 0f, 0.1234567f, 1f],
                [1f, 0.5f, 0f, -0.7654321f, -1f],
            ];
            var source = new AudioDocument(samples, 44_100, 32);

            AiffCodec.Save(source, path, bitDepth, dither: false);
            AudioDocument loaded = AiffCodec.Load(path);
            using var independentReader = new AiffFileReader(path);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal("FORM", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("AIFF", Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal("COMM", Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal("SSND", Encoding.ASCII.GetString(bytes, 38, 4));
            Assert.Equal(0x80, bytes[54]); // first sample is -1 in big-endian PCM

            Assert.Equal(44_100, loaded.SampleRate);
            Assert.Equal(bitDepth, loaded.SourceBitDepth);
            Assert.Equal(2, loaded.ChannelCount);
            Assert.Equal(samples[0].Length, loaded.Length);
            Assert.Equal(path, loaded.FilePath);

            // A classic AIFF now keeps its ancillary chunks through a save, so it can be written
            // back over itself. Only AIFF-C still cannot, because the writer emits classic AIFF.
            Assert.False(loaded.RequiresSaveAs);
            Assert.Equal(44_100, independentReader.WaveFormat.SampleRate);
            Assert.Equal(bitDepth, independentReader.WaveFormat.BitsPerSample);
            Assert.Equal(2, independentReader.WaveFormat.Channels);
            for (int channel = 0; channel < samples.Length; channel++)
                for (int frame = 0; frame < samples[channel].Length; frame++)
                    Assert.Equal(Quantize(samples[channel][frame], bitDepth),
                        loaded.Channels[channel][frame]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImporterLoadsLittleEndianAiffCAsAnUnsavedConversion()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "little-endian.aifc");
        try
        {
            File.WriteAllBytes(path, CreateSowtAiffC());

            AudioDocument loaded = AudioImporter.Load(path);

            Assert.Equal(44_100, loaded.SampleRate);
            Assert.Equal(16, loaded.SourceBitDepth);
            Assert.Single(loaded.Channels);
            Assert.Equal([0x1234 / 32768f, -0.5f], loaded.Channels[0]);
            Assert.Equal(path, loaded.FilePath);
            Assert.True(loaded.RequiresSaveAs);
            Assert.Equal("little-endian.aifc", loaded.Title);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImporterLoadsAppleIntegerAiffCVariants()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var cases = new (string Compression, int Bits, byte[] Data, float[] Expected)[]
            {
                ("raw ", 8, [0x00, 0x80, 0xFF], [-1f, 0f, 127 / 128f]),
                ("in24", 24, [0x40, 0x00, 0x00], [0.5f]),
                ("in32", 32, [0xC0, 0x00, 0x00, 0x00], [-0.5f]),
            };
            foreach (var item in cases)
            {
                string path = Path.Combine(directory, item.Compression.Trim() + ".aifc");
                File.WriteAllBytes(path,
                    CreateAiffC(item.Compression, item.Bits, item.Expected.Length, item.Data));

                AudioDocument loaded = AudioImporter.Load(path);

                Assert.Equal(item.Expected, loaded.Channels[0]);
                Assert.Equal(path, loaded.FilePath);
                Assert.True(loaded.RequiresSaveAs);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClassicWriterRejectsAiffCExtensionWithoutCreatingAFile()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "not-classic.aifc");
        try
        {
            var source = new AudioDocument([[0.25f]], 44_100, 16);

            NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
                AiffCodec.Save(source, path, 16, dither: false));

            Assert.Contains("AIFF-C output is not supported", error.Message);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An AIFF-C's sample format is not one the writer produces, so saving in place would replace
    /// the container as well as the audio. That is a different file, and it still needs a new name.
    /// </summary>
    [Fact]
    public void ImportedAiffCCannotOverwriteItsSource()
    {
        string directory = CreateTemporaryDirectory();
        string sourcePath = Path.Combine(directory, "source.aifc");
        string classicPath = Path.Combine(directory, "source.aiff");
        try
        {
            File.WriteAllBytes(sourcePath, CreateSowtAiffC());
            byte[] original = File.ReadAllBytes(sourcePath);
            AudioDocument imported = AiffCodec.Load(sourcePath);
            Assert.True(imported.RequiresSaveAs);

            // The extension check refuses .aifc outright, so the in-place test uses a .aiff name
            // that the document nonetheless came from.
            imported.FilePath = classicPath;
            AiffCodec.Save(new AudioDocument([[0.25f]], 44_100, 16), classicPath, 16, dither: false);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                AiffCodec.Save(imported, classicPath, 16, dither: false));

            Assert.Contains("saved to a different path", error.Message);
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The other half of that: a classic AIFF can be written back over itself, because everything
    /// it carried is still there afterwards.
    /// </summary>
    [Fact]
    public void AClassicAiffCanBeSavedOverItselfWithItsChunksIntact()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "source.aiff");
        try
        {
            AiffCodec.Save(new AudioDocument([[0.25f, -0.5f]], 44_100, 16), path, 16, dither: false);

            // A chunk this app knows nothing about, inserted after the audio.
            byte[] annotated = WithTrailingChunk(File.ReadAllBytes(path), "ANNO",
                Encoding.ASCII.GetBytes("Needle drop, 1962 pressing."));
            File.WriteAllBytes(path, annotated);

            AudioDocument imported = AiffCodec.Load(path);
            Assert.False(imported.RequiresSaveAs);
            AiffCodec.Save(imported, path, 16, dither: false);

            AudioDocument reloaded = AiffCodec.Load(path);
            Assert.Equal([0.25f, -0.5f], reloaded.Channels[0]);
            Assert.Equal("Needle drop, 1962 pressing.",
                AiffMetadata.ReadTextChunk(reloaded.Riff.Find("ANNO")?.Data));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Appends a chunk to an AIFF and re-states the FORM size, big-endian throughout.</summary>
    private static byte[] WithTrailingChunk(byte[] file, string id, byte[] payload)
    {
        int padded = payload.Length + (payload.Length & 1);
        var result = new byte[file.Length + 8 + padded];
        file.CopyTo(result, 0);

        int at = file.Length;
        Encoding.ASCII.GetBytes(id).CopyTo(result, at);
        at += 4;
        for (int shift = 24; shift >= 0; shift -= 8) result[at++] = (byte)(payload.Length >> shift);
        payload.CopyTo(result, at);

        int formSize = result.Length - 8;
        for (int i = 0, shift = 24; shift >= 0; shift -= 8) result[4 + i++] = (byte)(formSize >> shift);
        return result;
    }

    [Fact]
    public void ExporterCreatesAiffAndImporterRecognizesItsExtensions()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "export.aiff");
        try
        {
            var source = new AudioDocument([[0.2345678f, -0.75f]], 48_000, 32);

            AudioExporter.Export(source, path, ExportFormat.Aiff24,
                bitrateKbps: 0, start: 0, count: source.Length, targetRate: 0);
            AudioDocument loaded = AudioImporter.Load(path);

            Assert.Equal(24, loaded.SourceBitDepth);
            Assert.Equal(48_000, loaded.SampleRate);
            Assert.Contains("*.aif", AudioImporter.OpenFilter, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("*.aiff", AudioImporter.OpenFilter, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("*.aifc", AudioImporter.OpenFilter, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("AIFF|*.aiff", AudioExporter.FilterFor(ExportFormat.Aiff24));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static float Quantize(float sample, int bitDepth) => bitDepth switch
    {
        16 => Math.Clamp((int)Math.Round(sample * 32768.0),
            short.MinValue, short.MaxValue) / 32768f,
        24 => Math.Clamp((int)Math.Round(sample * 8388608.0),
            -8388608, 8388607) / 8388608f,
        _ => (float)(Math.Clamp((long)Math.Round(sample * 2147483648.0),
            int.MinValue, int.MaxValue) / 2147483648.0),
    };

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] CreateSowtAiffC() =>
        CreateAiffC("sowt", 16, 2, [0x34, 0x12, 0x00, 0xC0]);

    private static byte[] CreateAiffC(string compression, int bits, int frames, byte[] data)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        int soundChunkSize = 8 + data.Length;
        int formSize = 4 + (8 + 24) + (8 + soundChunkSize) + (soundChunkSize & 1);
        WriteFourCc(writer, "FORM");
        WriteUInt32Big(writer, (uint)formSize);
        WriteFourCc(writer, "AIFC");
        WriteFourCc(writer, "COMM");
        WriteUInt32Big(writer, 24);
        WriteUInt16Big(writer, 1);
        WriteUInt32Big(writer, (uint)frames);
        WriteUInt16Big(writer, (ushort)bits);
        writer.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }); // 44100 extended
        WriteFourCc(writer, compression);
        writer.Write((byte)0); // empty compression name
        writer.Write((byte)0); // even-byte padding within COMM
        WriteFourCc(writer, "SSND");
        WriteUInt32Big(writer, (uint)soundChunkSize);
        WriteUInt32Big(writer, 0);
        WriteUInt32Big(writer, 0);
        writer.Write(data);
        if ((soundChunkSize & 1) == 1) writer.Write((byte)0);
        return stream.ToArray();
    }

    private static void WriteFourCc(BinaryWriter writer, string value) =>
        writer.Write(Encoding.ASCII.GetBytes(value));

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
}
