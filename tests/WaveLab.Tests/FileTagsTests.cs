using System.IO;
using System.Text;
using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The tag model, the ISRC helpers and the PQ timing — everything the two delivery dialogs edit.
/// </summary>
public sealed class FileTagsTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-tags").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private string At(string name) => Path.Combine(_directory, name);

    private static FileTags Sample() => new()
    {
        Title = "Blue Monk",
        Artist = "The Transfer",
        Album = "Side A",
        Track = "1/4",
        Genre = "Jazz",
        Year = "1962",
        Comment = "Needle drop, minor surface noise through the intro",
    };

    private static AudioDocument Document(int frames = 1_024)
    {
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        }
        return new AudioDocument(data, Rate, 24);
    }

    // ── the model ────────────────────────────────────────────────

    [Fact]
    public void TagsRoundTripThroughAWavFile()
    {
        AudioDocument doc = Document();
        Sample().WriteTo(doc.Riff);

        string path = At("tagged.wav");
        WavCodec.Save(doc, path, 24, dither: false);

        FileTags read = FileTags.ReadFrom(WavCodec.Load(path).Riff);
        output.WriteLine($"{read.Title} / {read.Artist} / {read.Album} / {read.Track} / {read.Year}");

        Assert.Equal("Blue Monk", read.Title);
        Assert.Equal("The Transfer", read.Artist);
        Assert.Equal("Side A", read.Album);
        Assert.Equal("1/4", read.Track);
        Assert.Equal("Jazz", read.Genre);
        Assert.Equal("1962", read.Year);
        Assert.Equal("Needle drop, minor surface noise through the intro", read.Comment);
    }

    /// <summary>
    /// AIFF has a title, an author and free annotation and nowhere else to put anything. The extras
    /// go into the annotation rather than being dropped: a round trip through a weaker container
    /// should lose formatting, not facts.
    /// </summary>
    [Fact]
    public void TagsRoundTripThroughAnAiffFile()
    {
        AudioDocument doc = Document();
        doc.Riff = RiffMetadata.ForAiff();
        Sample().WriteTo(doc.Riff);

        string path = At("tagged.aiff");
        AiffCodec.Save(doc, path, 24, dither: false);

        FileTags read = FileTags.ReadFrom(AiffCodec.Load(path).Riff);
        output.WriteLine($"{read.Title} / {read.Artist} / {read.Album} / {read.Track} / {read.Year}");
        output.WriteLine($"comment: {read.Comment}");

        Assert.Equal("Blue Monk", read.Title);
        Assert.Equal("The Transfer", read.Artist);
        Assert.Equal("Side A", read.Album);
        Assert.Equal("1/4", read.Track);
        Assert.Equal("Jazz", read.Genre);
        Assert.Equal("1962", read.Year);
        Assert.Equal("Needle drop, minor surface noise through the intro", read.Comment);
    }

    /// <summary>
    /// The one that would have shipped broken: a WAV's marker labels and its information tags are
    /// both <c>LIST</c> chunks, told apart only by the type in their first four bytes.
    /// </summary>
    [Fact]
    public void TagsAndMarkerLabelsCoexistInOneFile()
    {
        AudioDocument doc = Document();
        Sample().WriteTo(doc.Riff);
        List<Marker> markers =
        [
            new() { Name = "Side A start", Position = 0 },
            new() { Name = "Second track", Position = 512 },
        ];

        string path = At("both.wav");
        WavCodec.Save(doc, path, 24, dither: false, markers: markers);

        AudioDocument loaded = WavCodec.Load(path);
        FileTags tags = FileTags.ReadFrom(loaded.Riff);
        List<Marker> read = MarkerStore.FromRiff(loaded.Riff);

        output.WriteLine($"title '{tags.Title}', {read.Count} marker(s): " +
                         string.Join(", ", read.Select(m => $"{m.Name}@{m.Position}")));

        Assert.Equal("Blue Monk", tags.Title);
        Assert.Equal(2, read.Count);
        Assert.Equal("Side A start", read[0].Name);
        Assert.Equal("Second track", read[1].Name);
    }

    [Fact]
    public void WritingMarkersDoesNotDeleteTheTagsAndViceVersa()
    {
        var riff = new RiffMetadata();
        Sample().WriteTo(riff);
        riff.SetList("adtl", BroadcastMetadata.WriteLabelList([new(1, 0, "Mark")]));

        Assert.NotNull(riff.FindList("INFO"));
        Assert.NotNull(riff.FindList("adtl"));

        // Rewriting either replaces only its own.
        new FileTags { Title = "Different" }.WriteTo(riff);
        riff.SetList("adtl", BroadcastMetadata.WriteLabelList([new(1, 0, "Changed")]));

        Assert.Equal("Different", FileTags.ReadFrom(riff).Title);
        Assert.Equal(2, riff.Chunks.Count(c => c.Id == "LIST"));
    }

    /// <summary>
    /// An empty tag set removes the chunk. A present-but-blank title makes a reader show nothing
    /// where it would otherwise fall back to the file name.
    /// </summary>
    [Fact]
    public void ClearingEveryFieldRemovesTheChunk()
    {
        var riff = new RiffMetadata();
        Sample().WriteTo(riff);
        Assert.NotNull(riff.FindList("INFO"));

        new FileTags().WriteTo(riff);
        Assert.Null(riff.FindList("INFO"));
    }

    [Fact]
    public void TagsBecomeId3Frames()
    {
        Id3Tags id3 = Sample().ToId3();
        Assert.Equal("Blue Monk", id3.Title);
        Assert.Equal("The Transfer", id3.Artist);
        Assert.Equal("Side A", id3.Album);
        Assert.Equal("1/4", id3.Track);
        Assert.Equal("1962", id3.Year);
        Assert.False(id3.IsEmpty);
    }

    [Fact]
    public void ADocumentWithNoMetadataReadsAsEmptyRatherThanThrowing()
    {
        Assert.True(FileTags.ReadFrom(null).IsEmpty);
        Assert.True(FileTags.ReadFrom(new RiffMetadata()).IsEmpty);
        Assert.True(FileTags.ReadFrom(RiffMetadata.ForAiff()).IsEmpty);
    }

    /// <summary>
    /// A metadata edit has to reach the next Save, but nothing about the samples moved — so the
    /// peak pyramid and the marker anchors must not be told to rebuild.
    /// </summary>
    [Fact]
    public void AMetadataEditMarksTheDocumentWithoutClaimingTheAudioChanged()
    {
        AudioDocument doc = Document();
        doc.MarkSaved();
        int version = doc.EditVersion;
        int contentChanges = 0;
        doc.Changed += (_, _, _) => contentChanges++;

        doc.MarkMetadataChanged();

        Assert.True(doc.Dirty);
        Assert.Equal(version + 1, doc.EditVersion);
        Assert.Equal(0, contentChanges);
    }

    // ── ISRC ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("GB-AAA-24-00001", "GBAAA2400001")]
    [InlineData("gb aaa 24 00001", "GBAAA2400001")]
    [InlineData("GBAAA2400001", "GBAAA2400001")]
    [InlineData("GBAAA240000", "")]
    [InlineData("GBAAA24000012", "")]
    [InlineData("", "")]
    public void AnIsrcIsNormalisedOrRejected(string input, string expected) =>
        Assert.Equal(expected, Isrc.Normalise(input));

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("GB-AAA-24-00001", true)]
    [InlineData("GBAAA240001", false)]
    public void BlankAndValidAreTheTwoStatesThatAreNotAnError(string input, bool acceptable) =>
        Assert.Equal(acceptable, Isrc.IsAcceptable(input));

    [Theory]
    [InlineData("GBAAA2400001", 0, "GBAAA2400001")]
    [InlineData("GBAAA2400001", 1, "GBAAA2400002")]
    [InlineData("GBAAA2400001", 11, "GBAAA2400012")]
    [InlineData("GB-AAA-24-00099", 1, "GBAAA2400100")]
    public void AdvancingMovesOnlyTheDesignationCode(string seed, int steps, string expected) =>
        Assert.Equal(expected, Isrc.Advance(seed, steps));

    /// <summary>
    /// Past 99999 the designation code has nowhere to go. Rolling over would carry into the year of
    /// reference and start numbering somebody else's releases.
    /// </summary>
    [Fact]
    public void AdvancingPastTheDesignationRangeIsRefused()
    {
        Assert.Equal("GBAAA2499999", Isrc.Advance("GBAAA2499998", 1));
        Assert.Equal("", Isrc.Advance("GBAAA2499999", 1));
        Assert.Equal("", Isrc.Advance("not an isrc", 1));
    }

    /// <summary>
    /// A line that is not an ISRC becomes an empty entry rather than disappearing, so every number
    /// after it still lands on the track it was meant for.
    /// </summary>
    [Fact]
    public void ImportingKeepsItsPlaceWhenALineIsUnusable()
    {
        List<string> codes = Isrc.Parse(
            "# side A\nGB-AAA-24-00001\n\nnonsense\nGBAAA2400003\n");

        output.WriteLine(string.Join(" | ", codes.Select(c => c.Length == 0 ? "(skip)" : c)));
        Assert.Equal(3, codes.Count);
        Assert.Equal("GBAAA2400001", codes[0]);
        Assert.Equal("", codes[1]);
        Assert.Equal("GBAAA2400003", codes[2]);
    }

    // ── PQ timing ────────────────────────────────────────────────

    /// <summary>
    /// The disc timeline starts at the two-second lead-in, and each track follows the last with no
    /// gap — a sheet that starts at 00:00:00 puts every track two seconds early.
    /// </summary>
    [Fact]
    public void ThePqSheetRunsFromTheLeadInWithNoGaps()
    {
        List<CdTrackPlan> plan =
        [
            new(0, Rate * 5, "One"),
            new(Rate * 5, Rate * 11, "Two"),
            new(Rate * 11, Rate * 15, "Three"),
        ];

        CdPqLayout layout = CdTransfer.PqSheet(plan, Rate, Rate * 15);
        foreach (CdPqEntry entry in layout.Tracks)
            output.WriteLine($"{entry.Track:00}  {entry.StartTimecode}  {entry.LengthTimecode}");
        output.WriteLine($"lead-out {layout.LeadOutTimecode}");

        Assert.Equal(3, layout.Tracks.Count);
        Assert.Equal("00:02:00", layout.Tracks[0].StartTimecode);
        Assert.Equal("00:07:00", layout.Tracks[1].StartTimecode);
        Assert.Equal("00:13:00", layout.Tracks[2].StartTimecode);
        Assert.Equal("00:17:00", layout.LeadOutTimecode);

        Assert.Equal("00:05:00", layout.Tracks[0].LengthTimecode);
        Assert.Equal("00:06:00", layout.Tracks[1].LengthTimecode);
        Assert.Equal("00:04:00", layout.Tracks[2].LengthTimecode);
    }

    /// <summary>
    /// The lengths are whole CD frames because the plan is sector-aligned first — which is also why
    /// the sheet's lengths are not simply the source durations.
    /// </summary>
    [Fact]
    public void LengthsAreWholeFramesEvenWhenTheSourceRangesAreNot()
    {
        List<CdTrackPlan> plan = [new(0, Rate * 5 + 137, "One"), new(Rate * 5 + 137, Rate * 10, "Two")];
        CdPqLayout layout = CdTransfer.PqSheet(plan, Rate, Rate * 10);

        int total = layout.Tracks.Sum(t => t.LengthFrames);
        output.WriteLine($"{total} frames, lead-out {layout.LeadOutTimecode}");
        Assert.Equal(layout.LeadOutFrame - CdTransfer.LeadInFrames, total);
        Assert.All(layout.Tracks, t => Assert.True(t.LengthFrames > 0));
    }

    [Fact]
    public void AnEmptyPlanStillStartsAtTheLeadIn()
    {
        CdPqLayout layout = CdTransfer.PqSheet([], Rate, 0);
        Assert.Empty(layout.Tracks);
        Assert.Equal("00:02:00", layout.LeadOutTimecode);
    }

    /// <summary>
    /// The dialog's timing and the image writer's must agree, or the sheet describes a disc other
    /// than the one in IMAGE.DAT.
    /// </summary>
    [Fact]
    public async Task TheSheetAgreesWithWhatTheImageWriterProduces()
    {
        AudioDocument document = Document(Rate * 12);
        List<CdTrackPlan> plan = [new(0, Rate * 5, "One"), new(Rate * 5, Rate * 12, "Two")];

        CdPqLayout layout = CdTransfer.PqSheet(plan, Rate, document.Length);
        string folder = At("ddp");
        DdpResult result = await CdTransfer.ExportDdpAsync(
            document, plan, folder, new DdpDiscInfo("Disc"));

        long imageFrames = result.ImageBytes / (DdpImage.SamplesPerFrame * 4L);
        output.WriteLine($"sheet says {layout.LeadOutFrame - CdTransfer.LeadInFrames}, image holds {imageFrames}");
        Assert.Equal(imageFrames, layout.LeadOutFrame - CdTransfer.LeadInFrames);

        string sheet = File.ReadAllText(Path.Combine(folder, "PQDESCR"));
        Assert.Contains(layout.Tracks[1].StartTimecode, sheet);
        Assert.Contains(layout.LeadOutTimecode, sheet);
    }
}
