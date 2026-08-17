using System.IO;
using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Sony's Wave64 container.
/// </summary>
/// <remarks>
/// A round trip through one's own writer and reader proves only that they agree with each other,
/// and two implementations agreeing on the wrong layout is exactly how an interchange format goes
/// wrong. So the bytes are checked directly against the two rules that are easy to get backwards —
/// a chunk's size includes its own header, and chunks are padded to eight — and the samples are
/// checked against what the WAV writer produces from the same audio.
/// </remarks>
public sealed class Wave64Tests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-w64").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    private static AudioDocument Programme(int frames = 5_000, int channels = 2, int rate = 48_000)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(0.5 * Math.Sin(2 * Math.PI * (220 + 110 * c) * i / rate));
        }
        return new AudioDocument(data, rate, 24);
    }

    // ── the layout, byte for byte ────────────────────────────────

    private static readonly Guid RiffGuid = new("66666972-912E-11CF-A5D6-28DB04C10000");
    private static readonly Guid WaveGuid = new("65766177-ACF3-11D3-8CD1-00C04F8EDB8A");
    private static readonly Guid FmtGuid = new("20746D66-ACF3-11D3-8CD1-00C04F8EDB8A");
    private static readonly Guid DataGuid = new("61746164-ACF3-11D3-8CD1-00C04F8EDB8A");

    /// <summary>
    /// The GUIDs are not arbitrary: the first four bytes of each are the chunk's ASCII name, little
    /// endian. If that does not hold, the constants were transcribed rather than understood.
    /// </summary>
    [Theory]
    [InlineData("riff")]
    [InlineData("wave")]
    [InlineData("fmt ")]
    [InlineData("data")]
    public void EachIdentifierBeginsWithItsOwnName(string name)
    {
        Guid id = name switch
        {
            "riff" => RiffGuid,
            "wave" => WaveGuid,
            "fmt " => FmtGuid,
            _ => DataGuid,
        };

        byte[] bytes = id.ToByteArray();
        string leading = System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
        output.WriteLine($"{id} starts with '{leading}'");
        Assert.Equal(name, leading);
    }

    /// <summary>
    /// Walks the file as a reader that is not this one would: every chunk size must include its own
    /// twenty-four byte header, every chunk must start on an eight-byte boundary, and the total the
    /// header states must be the length of the file.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void TheChunkLayoutIsWhatAnotherReaderWouldExpect(int bitDepth)
    {
        string path = Path($"layout{bitDepth}.w64");
        AudioDocument doc = Programme();
        Wave64Codec.Save(doc, path, bitDepth, dither: false);

        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(RiffGuid, new Guid(file.AsSpan(0, 16)));

        long stated = BitConverter.ToInt64(file, 16);
        Assert.Equal(file.LongLength, stated);
        Assert.Equal(WaveGuid, new Guid(file.AsSpan(24, 16)));

        long position = 40;
        var seen = new List<string>();
        while (position + 24 <= file.LongLength)
        {
            Assert.True(position % 8 == 0, $"a chunk starts at {position}, which is not a multiple of 8");

            var id = new Guid(file.AsSpan((int)position, 16));

            // The size follows the whole sixteen-byte identifier, not a four-byte one. Reading it
            // eight bytes in — where a RIFF chunk keeps it — lands in the middle of the GUID and
            // produces a plausible-looking enormous number, which is how this was first written.
            long size = BitConverter.ToInt64(file, (int)position + 16);

            Assert.True(size >= 24, $"a chunk states {size} bytes, which cannot include its own header");
            Assert.True(position + size <= file.LongLength, "a chunk runs past the end of the file");

            seen.Add(System.Text.Encoding.ASCII.GetString(id.ToByteArray(), 0, 4));
            if (id == DataGuid)
            {
                long payload = size - 24;
                Assert.Equal((long)doc.Length * doc.Channels.Count * (bitDepth / 8), payload);
            }

            position += size + (8 - size % 8) % 8;
        }

        output.WriteLine($"{bitDepth}-bit: {file.Length} bytes, chunks {string.Join(", ", seen)}");
        Assert.Contains("fmt ", seen);
        Assert.Contains("data", seen);
        Assert.Equal(file.LongLength, position);
    }

    // ── the samples, against the WAV writer ──────────────────────

    /// <summary>
    /// The two containers describe where the samples are differently and agree exactly on what a
    /// sample is. Writing the same audio both ways must therefore give byte-identical sample data,
    /// which is what makes sharing the frame encoder worth doing.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void TheSamplesAreTheSameBytesTheWavWriterWould(int bitDepth)
    {
        AudioDocument doc = Programme();
        string wav = Path($"same{bitDepth}.wav"), w64 = Path($"same{bitDepth}.w64");

        WavCodec.Save(doc, wav, bitDepth, dither: false);
        Wave64Codec.Save(doc, w64, bitDepth, dither: false);

        AudioDocument fromWav = WavCodec.Load(wav);
        AudioDocument fromW64 = Wave64Codec.Load(w64);

        Assert.Equal(fromWav.Length, fromW64.Length);
        Assert.Equal(fromWav.Channels.Count, fromW64.Channels.Count);
        for (int c = 0; c < fromWav.Channels.Count; c++)
            Assert.Equal(fromWav.Channels[c], fromW64.Channels[c]);
    }

    [Theory]
    [InlineData(16, 1, 44_100)]
    [InlineData(24, 2, 48_000)]
    [InlineData(32, 2, 96_000)]
    [InlineData(24, 6, 48_000)]
    public void WhatIsWrittenComesBack(int bitDepth, int channels, int rate)
    {
        AudioDocument doc = Programme(3_333, channels, rate);
        string path = Path($"trip{bitDepth}_{channels}_{rate}.w64");

        Wave64Codec.Save(doc, path, bitDepth, dither: false);
        AudioDocument back = Wave64Codec.Load(path);

        Assert.Equal(doc.SampleRate, back.SampleRate);
        Assert.Equal(doc.Length, back.Length);
        Assert.Equal(channels, back.Channels.Count);

        double tolerance = bitDepth switch { 16 => 1.0 / 32768, 24 => 1.0 / 8388608, _ => 1e-7 };
        for (int c = 0; c < channels; c++)
            for (int i = 0; i < doc.Length; i++)
                Assert.True(Math.Abs(doc.Channels[c][i] - back.Channels[c][i]) <= tolerance,
                    $"channel {c} sample {i} moved by {doc.Channels[c][i] - back.Channels[c][i]}");
    }

    // ── recognising one ──────────────────────────────────────────

    /// <summary>
    /// Recognised by its opening identifier, not its extension. The format is written to .wav by
    /// more than one application, and the two containers share nothing but their samples.
    /// </summary>
    [Fact]
    public void AWave64FileNamedWavIsStillOpened()
    {
        AudioDocument doc = Programme();
        string misnamed = Path("actually-wave64.wav");
        Wave64Codec.Save(doc, misnamed, 24, dither: false);

        Assert.True(Wave64Codec.IsWave64(misnamed));

        AudioDocument back = AudioImporter.Load(misnamed);
        Assert.Equal(doc.Length, back.Length);
        Assert.Equal(doc.SampleRate, back.SampleRate);
    }

    [Fact]
    public void AnOrdinaryWavIsNotMistakenForOne()
    {
        string path = Path("ordinary.wav");
        WavCodec.Save(Programme(), path, 24, dither: false);

        Assert.False(Wave64Codec.IsWave64(path));
        Assert.Throws<InvalidDataException>(() => Wave64Codec.Load(path));
    }

    [Fact]
    public void TheImporterOpensAW64ByExtension()
    {
        AudioDocument doc = Programme();
        string path = Path("byextension.w64");
        Wave64Codec.Save(doc, path, 32, dither: false);

        AudioDocument back = AudioImporter.Load(path);
        Assert.Equal(doc.Length, back.Length);
    }

    // ── damage ───────────────────────────────────────────────────

    /// <summary>
    /// A file that ends inside its data is worth what survived. One that ends inside anything else
    /// has not said enough to be read, and says so rather than guessing.
    /// </summary>
    [Fact]
    public void ATruncatedFileYieldsWhatSurvivedOfIt()
    {
        AudioDocument doc = Programme(4_000);
        string path = Path("truncated.w64");
        Wave64Codec.Save(doc, path, 24, dither: false);

        byte[] whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole[..(whole.Length / 2)]);

        AudioDocument back = Wave64Codec.Load(path);
        output.WriteLine($"{doc.Length} frames written, {back.Length} recovered from half the file");

        Assert.True(back.Length > 0);
        Assert.True(back.Length < doc.Length);
        for (int i = 0; i < back.Length; i++)
            Assert.Equal(doc.Channels[0][i], back.Channels[0][i], 0.0001);
    }

    [Fact]
    public void SomethingThatIsNotWave64IsRejected()
    {
        string path = Path("nonsense.w64");
        File.WriteAllBytes(path, new byte[128]);

        Assert.False(Wave64Codec.IsWave64(path));
        Assert.Throws<InvalidDataException>(() => Wave64Codec.Load(path));
    }

    [Fact]
    public void AnImpossibleBitDepthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Wave64Codec.Save(Programme(), Path("bad.w64"), 20));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Wave64Codec.Save(Programme(200_000), Path("cancelled.w64"), 24, dither: false,
                cancelled.Token));
    }

    /// <summary>A failed write leaves no half-file behind, and no stray staging file either.</summary>
    [Fact]
    public void AFailedWriteLeavesNothingBehind()
    {
        string path = Path("aborted.w64");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        try { Wave64Codec.Save(Programme(200_000), path, 24, dither: false, cancelled.Token); }
        catch (OperationCanceledException) { }

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }
}
