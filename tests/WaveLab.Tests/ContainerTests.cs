using System.IO;
using System.Text;
using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// RF64 (the container that lifts WAV's 2 GB ceiling) and the AIFF metadata chunks.
/// </summary>
public sealed class ContainerTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-container").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private string At(string name) => System.IO.Path.Combine(_directory, name);

    private static AudioDocument Document(int frames = 2_048, int channels = 2)
    {
        var data = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(0.4 * Math.Sin(2 * Math.PI * (330 + c * 110) * i / Rate));
        }
        return new AudioDocument(data, Rate, 32);
    }

    private static string Id(byte[] file, int at) => Encoding.ASCII.GetString(file, at, 4);

    private static long ReadInt64Pair(byte[] file, int at) =>
        BitConverter.ToUInt32(file, at) | ((long)BitConverter.ToUInt32(file, at + 4) << 32);

    // ── RF64 ─────────────────────────────────────────────────────

    /// <summary>
    /// The header a real RF64 file has: the RF64 form, the escape in the 32-bit size, and a ds64
    /// chunk first in the form carrying the sizes that no longer fit.
    /// </summary>
    [Fact]
    public void AnRf64FileStatesItsSizesInDs64()
    {
        AudioDocument doc = Document(3_000);
        string path = At("big.wav");
        WavCodec.Save(doc, path, 24, dither: false, container: WavContainer.Rf64);

        byte[] file = File.ReadAllBytes(path);
        output.WriteLine($"{Id(file, 0)} / {Id(file, 8)} / {Id(file, 12)}, {file.Length} bytes");

        Assert.Equal("RF64", Id(file, 0));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(file, 4));
        Assert.Equal("WAVE", Id(file, 8));

        // ds64 must be the first chunk: every escaped size after it is meaningless until it is read.
        Assert.Equal("ds64", Id(file, 12));
        Assert.Equal(28, BitConverter.ToInt32(file, 16));

        long riffSize = ReadInt64Pair(file, 20);
        long dataSize = ReadInt64Pair(file, 28);
        long sampleCount = ReadInt64Pair(file, 36);
        output.WriteLine($"ds64 riff {riffSize}, data {dataSize}, frames {sampleCount}");

        Assert.Equal(file.Length - 8, riffSize);
        Assert.Equal(3_000L * 2 * 3, dataSize);
        Assert.Equal(3_000, sampleCount);
        Assert.Equal(0, BitConverter.ToInt32(file, 44));   // no table: only data needed the escape
    }

    [Fact]
    public void TheDataChunkOfAnRf64FileEscapesItsSize()
    {
        AudioDocument doc = Document(1_000);
        string path = At("escaped.wav");
        WavCodec.Save(doc, path, 16, dither: false, container: WavContainer.Rf64);

        byte[] file = File.ReadAllBytes(path);
        int at = 48;                                        // past RF64/WAVE/ds64
        Assert.Equal("fmt ", Id(file, at));
        at += 8 + 16;
        Assert.Equal("data", Id(file, at));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(file, at + 4));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void AnRf64FileReadsBackSampleForSample(int bitDepth)
    {
        AudioDocument doc = Document(2_500);
        string path = At($"round-{bitDepth}.wav");
        WavCodec.Save(doc, path, bitDepth, dither: false, container: WavContainer.Rf64);

        AudioDocument loaded = WavCodec.Load(path);
        Assert.Equal(doc.Length, loaded.Length);
        Assert.Equal(doc.ChannelCount, loaded.ChannelCount);
        Assert.Equal(Rate, loaded.SampleRate);

        double worst = 0;
        for (int c = 0; c < doc.ChannelCount; c++)
            for (int i = 0; i < doc.Length; i++)
                worst = Math.Max(worst, Math.Abs(doc.Channels[c][i] - loaded.Channels[c][i]));

        output.WriteLine($"{bitDepth}-bit RF64 round trip, worst error {worst:0.0000000}");
        Assert.True(worst <= 1.0 / (1 << (bitDepth == 32 ? 24 : bitDepth - 1)), $"worst error {worst}");
    }

    /// <summary>Carried chunks are written after the audio in RF64 exactly as in plain RIFF.</summary>
    [Fact]
    public void MetadataSurvivesAnRf64RoundTrip()
    {
        AudioDocument doc = Document(512);
        doc.Riff.Set("iXML", Encoding.ASCII.GetBytes("<BWFXML>scene 4</BWFXML>"));

        string path = At("meta.wav");
        WavCodec.Save(doc, path, 24, dither: false, container: WavContainer.Rf64);

        AudioDocument loaded = WavCodec.Load(path);
        Assert.Equal("<BWFXML>scene 4</BWFXML>",
            Encoding.ASCII.GetString(loaded.Riff.Find("iXML")!.Value.Data));
    }

    /// <summary>
    /// A file that fits stays plain RIFF. RF64 is understood by far fewer programs, so stepping up
    /// when there is no need would cost compatibility for nothing.
    /// </summary>
    [Fact]
    public void AFileThatFitsIsStillWrittenAsPlainRiff()
    {
        string path = At("small.wav");
        WavCodec.Save(Document(1_024), path, 16, dither: false);

        byte[] file = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Id(file, 0));
        Assert.Equal("fmt ", Id(file, 12));
    }

    /// <summary>
    /// The choice is the caller's when they make one. A file over the ceiling has to be written by
    /// an actual 2 GB of audio, which is not something a test can allocate, so what is asserted here
    /// is that an explicit container is honoured in both directions.
    /// </summary>
    [Fact]
    public void AnExplicitContainerIsHonouredEitherWay()
    {
        string riff = At("forced-riff.wav");
        string rf64 = At("forced-rf64.wav");
        WavCodec.Save(Document(700), riff, 16, dither: false, container: WavContainer.Riff);
        WavCodec.Save(Document(700), rf64, 16, dither: false, container: WavContainer.Rf64);

        Assert.Equal("RIFF", Id(File.ReadAllBytes(riff), 0));
        Assert.Equal("RF64", Id(File.ReadAllBytes(rf64), 0));

        // Both hold the same audio, whichever way the sizes are stated.
        Assert.Equal(WavCodec.Load(riff).Channels[0], WavCodec.Load(rf64).Channels[0]);
    }

    /// <summary>A hand-built RF64 file, as another program would write one.</summary>
    [Fact]
    public void AnRf64FileFromElsewhereIsRead()
    {
        string path = At("foreign.wav");
        File.WriteAllBytes(path, BuildRf64(frames: 4, includeTable: false));

        AudioDocument loaded = WavCodec.Load(path);
        Assert.Equal(4, loaded.Length);
        Assert.Equal(1, loaded.ChannelCount);
        Assert.Equal(48_000, loaded.SampleRate);
    }

    /// <summary>
    /// Only <c>data</c> has a field of its own in ds64; anything else that escapes its size has to
    /// be named in the table, and the reader has to honour it.
    /// </summary>
    [Fact]
    public void TheDs64TableIsHonouredForOtherChunks()
    {
        string path = At("tabled.wav");
        File.WriteAllBytes(path, BuildRf64(frames: 4, includeTable: true));

        AudioDocument loaded = WavCodec.Load(path);
        Assert.Equal(4, loaded.Length);
        Assert.Equal("hello", Encoding.ASCII.GetString(loaded.Riff.Find("note")!.Value.Data));
    }

    [Fact]
    public void AnRf64FileWithNoDs64IsRefused()
    {
        byte[] file = BuildRf64(frames: 4, includeTable: false);

        // Rename the ds64 chunk, so the sizes it states can no longer be found.
        Encoding.ASCII.GetBytes("XXXX").CopyTo(file, 12);
        string path = At("no-ds64.wav");
        File.WriteAllBytes(path, file);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => WavCodec.Load(path));
        output.WriteLine(error.Message);
        Assert.Contains("ds64", error.Message);
    }

    /// <summary>
    /// A ds64 table that claims more entries than the chunk holds must not be believed, or the
    /// reader walks off the end of it and reads sizes out of the audio.
    /// </summary>
    [Fact]
    public void ALyingDs64TableIsRefused()
    {
        byte[] file = BuildRf64(frames: 4, includeTable: false);
        BitConverter.GetBytes(4096).CopyTo(file, 44);       // table length, in a 28-byte ds64

        string path = At("lying.wav");
        File.WriteAllBytes(path, file);
        Assert.Throws<InvalidDataException>(() => WavCodec.Load(path));
    }

    /// <summary>
    /// Builds an RF64 file by hand: 8-bit-per-sample mono 16-bit PCM at 48 kHz, optionally with a
    /// <c>note</c> chunk whose size is escaped and stated in the ds64 table.
    /// </summary>
    private static byte[] BuildRf64(int frames, bool includeTable)
    {
        byte[] note = Encoding.ASCII.GetBytes("hello");
        int notePadded = note.Length + (note.Length & 1);
        int dataSize = frames * 2;
        int ds64Size = includeTable ? 28 + 12 : 28;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RF64"));
        writer.Write(uint.MaxValue);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        long riffSize = 4 + (8 + ds64Size) + (8 + 16) + (8 + dataSize) + (dataSize & 1)
                        + (includeTable ? 8 + notePadded : 0);
        writer.Write(Encoding.ASCII.GetBytes("ds64"));
        writer.Write(ds64Size);
        WritePair(writer, riffSize);
        WritePair(writer, dataSize);
        WritePair(writer, frames);
        writer.Write(includeTable ? 1 : 0);
        if (includeTable)
        {
            writer.Write(Encoding.ASCII.GetBytes("note"));
            WritePair(writer, note.Length);
        }

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);                            // PCM
        writer.Write((ushort)1);                            // mono
        writer.Write(48_000);
        writer.Write(48_000 * 2);                           // byte rate
        writer.Write((ushort)2);                            // block align
        writer.Write((ushort)16);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(uint.MaxValue);
        for (int i = 0; i < frames; i++) writer.Write((short)(i * 1000));
        if ((dataSize & 1) != 0) writer.Write((byte)0);

        if (includeTable)
        {
            writer.Write(Encoding.ASCII.GetBytes("note"));
            writer.Write(uint.MaxValue);                    // escaped; the real size is in the table
            writer.Write(note);
            if (note.Length != notePadded) writer.Write((byte)0);
        }

        writer.Flush();
        return memory.ToArray();

        static void WritePair(BinaryWriter writer, long value)
        {
            writer.Write((uint)value);
            writer.Write((uint)(value >> 32));
        }
    }

    // ── AIFF marks and text ──────────────────────────────────────

    [Fact]
    public void MarksRoundTripThroughAnAiffFile()
    {
        var doc = new AudioDocument([[0.1f, 0.2f, 0.3f, 0.4f], [0.1f, 0.2f, 0.3f, 0.4f]], Rate, 16);
        List<Marker> marks =
        [
            new() { Name = "Side A start", Position = 0 },
            new() { Name = "Track 2", Position = 3 },
        ];

        string path = At("marked.aiff");
        AiffCodec.Save(doc, path, 16, dither: false, markers: marks);

        AudioDocument loaded = AiffCodec.Load(path);
        List<Marker> read = MarkerStore.FromRiff(loaded.Riff);

        output.WriteLine(string.Join(", ", read.Select(m => $"{m.Name}@{m.Position}")));
        Assert.Equal(2, read.Count);
        Assert.Equal("Side A start", read[0].Name);
        Assert.Equal(0, read[0].Position);
        Assert.Equal("Track 2", read[1].Name);
        Assert.Equal(3, read[1].Position);
    }

    /// <summary>
    /// A mark's name is a Pascal string — a count byte, then the characters, the pair padded to an
    /// even length. Reading it as a C string takes the count for the first character.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("odd")]
    [InlineData("even name")]
    public void MarkNamesArePascalStringsWhateverTheirLength(string name)
    {
        List<BroadcastMetadata.CuePoint> marks = [new(1, 100, name), new(2, 200, "after")];
        byte[] chunk = AiffMetadata.WriteMarkChunk(marks);

        Assert.Equal(0, chunk.Length & 1);
        List<BroadcastMetadata.CuePoint> read = AiffMetadata.ReadMarkChunk(chunk);

        Assert.Equal(2, read.Count);
        Assert.Equal(name, read[0].Label);
        Assert.Equal(100, read[0].Position);

        // The one that matters: the mark after a name of this length is still found.
        Assert.Equal("after", read[1].Label);
        Assert.Equal(200, read[1].Position);
    }

    [Fact]
    public void ADamagedMarkChunkYieldsWhatItHasRatherThanThrowing()
    {
        byte[] chunk = AiffMetadata.WriteMarkChunk([new(1, 10, "one"), new(2, 20, "two")]);
        byte[] truncated = chunk[..(chunk.Length - 3)];

        List<BroadcastMetadata.CuePoint> read = AiffMetadata.ReadMarkChunk(truncated);
        output.WriteLine($"{read.Count} of 2 marks recovered from a truncated chunk");
        Assert.Single(read);
        Assert.Equal("one", read[0].Label);
    }

    [Fact]
    public void CommentsRoundTripWithTheirMarkAndTimestamp()
    {
        List<AiffMetadata.Comment> comments =
        [
            new("Surface noise through the intro", MarkerId: 1, TimeStamp: 3_000_000_000),
            new("Whole side", MarkerId: 0),
        ];

        List<AiffMetadata.Comment> read =
            AiffMetadata.ReadCommentChunk(AiffMetadata.WriteCommentChunk(comments));

        Assert.Equal(2, read.Count);
        Assert.Equal("Surface noise through the intro", read[0].Text);
        Assert.Equal(1, read[0].MarkerId);
        Assert.Equal(3_000_000_000u, read[0].TimeStamp);
        Assert.Equal("Whole side", read[1].Text);
        Assert.Equal(0, read[1].MarkerId);
    }

    [Fact]
    public void AiffTextChunksAreWrittenWithoutATerminator()
    {
        byte[] chunk = AiffMetadata.WriteTextChunk("The Transfer");
        Assert.Equal(12, chunk.Length);
        Assert.Equal("The Transfer", AiffMetadata.ReadTextChunk(chunk));
    }

    /// <summary>
    /// The two containers say the same thing differently — cue points and a label list in a WAV,
    /// one MARK chunk in an AIFF — so a mark placed in either arrives in the other.
    /// </summary>
    [Fact]
    public void MarksCrossFromWavToAiff()
    {
        var doc = new AudioDocument([[0.1f, 0.2f, 0.3f, 0.4f]], Rate, 16);
        List<Marker> marks = [new() { Name = "Lead-in", Position = 1 }];

        string wav = At("cross.wav");
        WavCodec.Save(doc, wav, 16, dither: false, markers: marks);

        AudioDocument fromWav = WavCodec.Load(wav);
        List<Marker> viaWav = MarkerStore.FromRiff(fromWav.Riff);
        Assert.Single(viaWav);

        string aiff = At("cross.aiff");
        AiffCodec.Save(fromWav, aiff, 16, dither: false, markers: viaWav);

        List<Marker> viaAiff = MarkerStore.FromRiff(AiffCodec.Load(aiff).Riff);
        Assert.Single(viaAiff);
        Assert.Equal("Lead-in", viaAiff[0].Name);
        Assert.Equal(1, viaAiff[0].Position);
    }
}
