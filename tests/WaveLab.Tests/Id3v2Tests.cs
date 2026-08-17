using System.IO;
using System.Text;
using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class Id3v2Tests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-id3").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private static readonly Id3Tags Sample = new(
        Title: "Blue Monk",
        Artist: "The Transfer",
        Album: "Side A",
        Year: "1962",
        Track: "3/12",
        Comment: "Needle drop, minor surface noise through the intro");

    /// <summary>Walks the frames of a tag, so the tests read it the way a player would.</summary>
    private static Dictionary<string, string> ReadFrames(byte[] tag)
    {
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        int size = Id3v2.ReadSynchsafe(tag, 6);
        int at = Id3v2.HeaderBytes;

        while (at + 10 <= Id3v2.HeaderBytes + size)
        {
            string id = Encoding.ASCII.GetString(tag, at, 4);
            if (id[0] == '\0') break;
            int length = Id3v2.ReadSynchsafe(tag, at + 4);
            at += 10;
            if (at + length > tag.Length) break;

            // Every frame here begins with an encoding byte; COMM then has a language and an
            // empty description before its text.
            int textAt = at + 1;
            int textLength = length - 1;
            if (id == "COMM") { textAt += 4; textLength -= 4; }
            frames[id] = Encoding.UTF8.GetString(tag, textAt, Math.Max(0, textLength));
            at += length;
        }
        return frames;
    }

    // ── the tag ──────────────────────────────────────────────────

    [Fact]
    public void TheHeaderDeclaresVersionTwoPointFour()
    {
        byte[] tag = Id3v2.Write(Sample);
        output.WriteLine($"{tag.Length} bytes, {ReadFrames(tag).Count} frames");

        Assert.Equal("ID3", Encoding.ASCII.GetString(tag, 0, 3));
        Assert.Equal(4, tag[3]);
        Assert.Equal(0, tag[4]);
        Assert.Equal(0, tag[5]);
    }

    /// <summary>
    /// The size a decoder skips by. Getting this wrong is how a tagged file plays its own metadata
    /// as noise, so it is checked against the bytes actually present.
    /// </summary>
    [Fact]
    public void TheStatedSizeMatchesTheFramesThatFollow()
    {
        byte[] tag = Id3v2.Write(Sample);
        Assert.Equal(tag.Length - Id3v2.HeaderBytes, Id3v2.ReadSynchsafe(tag, 6));
    }

    /// <summary>
    /// No byte of a size field may have its top bit set, or part of a tag can look like the eleven
    /// set bits an MPEG frame begins with.
    /// </summary>
    [Fact]
    public void EverySizeFieldIsSynchsafe()
    {
        byte[] tag = Id3v2.Write(Sample with { Comment = new string('x', 400) });

        for (int i = 6; i < 10; i++) Assert.Equal(0, tag[i] & 0x80);

        int size = Id3v2.ReadSynchsafe(tag, 6);
        int at = Id3v2.HeaderBytes;
        int frames = 0;
        while (at + 10 <= Id3v2.HeaderBytes + size)
        {
            for (int i = 4; i < 8; i++)
                Assert.Equal(0, tag[at + i] & 0x80);
            at += 10 + Id3v2.ReadSynchsafe(tag, at + 4);
            frames++;
        }
        output.WriteLine($"{frames} frames, all sizes synchsafe, longest {size}");
        Assert.True(frames >= 6);
    }

    [Fact]
    public void EveryFieldReachesItsOwnFrame()
    {
        Dictionary<string, string> frames = ReadFrames(Id3v2.Write(Sample));
        foreach (KeyValuePair<string, string> frame in frames) output.WriteLine($"{frame.Key}  {frame.Value}");

        Assert.Equal("Blue Monk", frames["TIT2"]);
        Assert.Equal("The Transfer", frames["TPE1"]);
        Assert.Equal("Side A", frames["TALB"]);
        Assert.Equal("1962", frames["TDRC"]);          // 2.4 replaced TYER with TDRC
        Assert.Equal("3/12", frames["TRCK"]);
        Assert.Equal("Needle drop, minor surface noise through the intro", frames["COMM"]);
    }

    /// <summary>
    /// UTF-8 is what 2.4 added and the reason to write 2.4 at all: 2.3's Latin-1 cannot spell most
    /// of the names a record actually has on it.
    /// </summary>
    [Fact]
    public void TextIsUtf8()
    {
        const string name = "Édith Piaf — Non, je ne regrette rien";
        byte[] tag = Id3v2.Write(new Id3Tags(Title: name));

        Assert.Equal(0x03, tag[Id3v2.HeaderBytes + 10]);   // the encoding byte of the first frame
        Assert.Equal(name, ReadFrames(tag)["TIT2"]);
    }

    [Fact]
    public void AnEmptyTagIsNoBytesAtAll()
    {
        Assert.Empty(Id3v2.Write(default));
        Assert.Empty(Id3v2.Write(new Id3Tags(Title: "   ")));
    }

    [Fact]
    public void BlankFieldsAreOmittedRatherThanWrittenEmpty()
    {
        Dictionary<string, string> frames = ReadFrames(Id3v2.Write(new Id3Tags(Title: "Only this")));
        Assert.Single(frames);
        Assert.Equal("Only this", frames["TIT2"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16_383)]
    [InlineData(1_000_000)]
    [InlineData(0x0FFFFFFF)]
    public void SynchsafeIntegersRoundTrip(int value)
    {
        var bytes = new byte[4];
        Id3v2.WriteSynchsafe(bytes, 0, value);
        Assert.Equal(value, Id3v2.ReadSynchsafe(bytes, 0));
    }

    // ── applying it to a file ────────────────────────────────────

    /// <summary>A stand-in for an encoder's output: no tag, just bytes that must survive intact.</summary>
    private string WriteFakeMp3(byte[]? existingTag = null)
    {
        string path = System.IO.Path.Combine(_directory, "track.mp3");
        using var stream = File.Create(path);
        if (existingTag != null) stream.Write(existingTag, 0, existingTag.Length);
        for (int i = 0; i < 4_096; i++) stream.WriteByte((byte)(i & 0xFF));
        return path;
    }

    private static byte[] Audio(byte[] file, long from) => file[(int)from..];

    [Fact]
    public void TheTagIsPrependedAndTheAudioIsUntouched()
    {
        string path = WriteFakeMp3();
        byte[] before = File.ReadAllBytes(path);

        Id3v2.ApplyTo(path, Sample);
        byte[] after = File.ReadAllBytes(path);

        byte[] tag = Id3v2.Write(Sample);
        output.WriteLine($"{before.Length} → {after.Length} bytes, tag {tag.Length}");

        Assert.Equal(tag, after[..tag.Length]);
        Assert.Equal(before, Audio(after, tag.Length));
    }

    /// <summary>
    /// Two tags at the front is not something a player has to recover from: the first one's length
    /// carries it over the second, so the second becomes audio as far as the decoder is concerned.
    /// </summary>
    [Fact]
    public void AnExistingTagIsReplacedRatherThanStackedOn()
    {
        byte[] old = Id3v2.Write(new Id3Tags(Title: "Wrong title", Artist: "Wrong artist"));
        string path = WriteFakeMp3(old);

        Id3v2.ApplyTo(path, Sample);
        byte[] after = File.ReadAllBytes(path);

        byte[] tag = Id3v2.Write(Sample);
        Assert.Equal(tag, after[..tag.Length]);

        // The old tag is gone, not buried: past the new one there is only audio.
        Assert.DoesNotContain("Wrong title", Encoding.UTF8.GetString(Audio(after, tag.Length)));
        Assert.Equal(4_096, after.Length - tag.Length);
    }

    [Fact]
    public void AFileWithNoTagReportsNoTagLength()
    {
        string path = WriteFakeMp3();
        using var stream = File.OpenRead(path);
        Assert.Equal(0, Id3v2.ExistingTagLength(stream));
    }

    [Fact]
    public void ATagLongerThanTheFileIsNotBelieved()
    {
        byte[] header = Id3v2.Write(new Id3Tags(Title: "x"));
        Id3v2.WriteSynchsafe(header, 6, 1_000_000);       // claims a megabyte in a tiny file

        string path = System.IO.Path.Combine(_directory, "lying.mp3");
        File.WriteAllBytes(path, header);

        using var stream = File.OpenRead(path);
        Assert.Equal(0, Id3v2.ExistingTagLength(stream));
    }

    /// <summary>
    /// Applying an empty tag to an untagged file must not rewrite it — but applying one to a
    /// tagged file has to strip what is there, which is the caller asking for no tag.
    /// </summary>
    [Fact]
    public void AnEmptyTagStripsAnExistingOneAndOtherwiseDoesNothing()
    {
        string untagged = WriteFakeMp3();
        byte[] before = File.ReadAllBytes(untagged);
        Id3v2.ApplyTo(untagged, default);
        Assert.Equal(before, File.ReadAllBytes(untagged));

        string tagged = System.IO.Path.Combine(_directory, "tagged.mp3");
        File.Copy(untagged, tagged);
        Id3v2.ApplyTo(tagged, Sample);
        Assert.NotEqual(before, File.ReadAllBytes(tagged));

        Id3v2.ApplyTo(tagged, default);
        Assert.Equal(before, File.ReadAllBytes(tagged));
    }
}
