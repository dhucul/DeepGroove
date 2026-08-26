using System.IO;
using System.Security.Cryptography;
using System.Text;
using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class DdpImageTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-ddp").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private static float[][] Track(int frames, double frequency = 440, double amplitude = 0.5)
    {
        var data = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            data[c] = new float[frames];
            for (int i = 0; i < frames; i++)
                data[c][i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
        }
        return data;
    }

    private DdpResult WriteThree(params int[] lengths)
    {
        var tracks = new List<float[][]>();
        var info = new List<DdpTrackInfo>();
        for (int t = 0; t < lengths.Length; t++)
        {
            tracks.Add(Track(lengths[t], 220 * (t + 1)));
            info.Add(new DdpTrackInfo($"Track {t + 1}", "The Transfer", "A. Writer",
                $"GBAAA24{t:D5}", PreEmphasis: t == 1));
        }

        return DdpImage.Write(_directory, tracks, info,
            new DdpDiscInfo("Side A", "The Transfer", "5012345678900", "Needle drop"), Rate);
    }

    // ── the file set ─────────────────────────────────────────────

    [Fact]
    public void TheImageSetHasEveryFileAPlantExpects()
    {
        DdpResult result = WriteThree(Rate * 3, Rate * 2, Rate * 4);

        output.WriteLine($"{result.Tracks} tracks, {result.ImageBytes} bytes, md5 {result.ImageMd5}");
        foreach (string file in result.Files) output.WriteLine($"   {Path.GetFileName(file)}");

        foreach (string name in new[] { "IMAGE.DAT", "DDPID", "DDPMS", "PQDESCR", "CDTEXT.BIN" })
            Assert.True(File.Exists(Path.Combine(_directory, name)), $"{name} was not written");
    }

    /// <summary>
    /// The checksum is what lets a plant prove it received what was sent, so it has to be the
    /// checksum of the file actually on disk.
    /// </summary>
    [Fact]
    public void TheChecksumMatchesTheImageOnDisk()
    {
        DdpResult result = WriteThree(Rate * 2, Rate);

        using var stream = File.OpenRead(Path.Combine(_directory, "IMAGE.DAT"));
        string actual = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();

        output.WriteLine($"reported {result.ImageMd5}, on disk {actual}");
        Assert.Equal(actual, result.ImageMd5);

        string sidecar = File.ReadAllText(Path.Combine(_directory, "IMAGE.DAT.md5"));
        Assert.Contains(actual, sidecar);
        Assert.Contains("IMAGE.DAT", sidecar);
    }

    /// <summary>
    /// A DDP image is CD-DA byte order, which is the opposite of a WAV's. Getting it backwards
    /// produces a file of exactly the right length full of noise, which no length or checksum test
    /// would catch.
    /// </summary>
    [Fact]
    public void TheImageIsBigEndian()
    {
        // A known ramp, so the byte order is unambiguous in the file.
        var track = new float[2][];
        for (int c = 0; c < 2; c++) track[c] = new float[DdpImage.SamplesPerFrame];
        track[0][0] = 0.5f;      // +16384 = 0x4000
        track[1][0] = -0.5f;     // -16384 = 0xC000

        DdpImage.Write(_directory, [track], [new DdpTrackInfo("One")], new DdpDiscInfo("Disc"), Rate);
        byte[] image = File.ReadAllBytes(Path.Combine(_directory, "IMAGE.DAT"));

        output.WriteLine($"first four bytes: {image[0]:X2} {image[1]:X2} {image[2]:X2} {image[3]:X2}");
        Assert.Equal(0x40, image[0]);     // high byte first
        Assert.Equal(0x00, image[1]);
        Assert.Equal(0xC0, image[2]);
        Assert.Equal(0x00, image[3]);
    }

    /// <summary>
    /// Every track must begin on a CD frame. A track that does not fill its last frame pushes
    /// everything after it off the grid, which a CD cannot represent.
    /// </summary>
    [Fact]
    public void EveryTrackStartsOnAFrameBoundary()
    {
        // Lengths deliberately not multiples of 588.
        DdpResult result = WriteThree(1_000, 2_345, 777);

        long bytes = new FileInfo(Path.Combine(_directory, "IMAGE.DAT")).Length;
        output.WriteLine($"image is {bytes} bytes, {bytes / (DdpImage.SamplesPerFrame * 4.0):0.000} frames");

        Assert.Equal(0, bytes % (DdpImage.SamplesPerFrame * 4));
        Assert.Equal(bytes, result.ImageBytes);
    }

    // ── the PQ sheet ─────────────────────────────────────────────

    [Fact]
    public void ThePqSheetStatesEveryTrackWithItsCatalogueInformation()
    {
        WriteThree(Rate * 3, Rate * 2, Rate * 4);
        string sheet = File.ReadAllText(Path.Combine(_directory, "PQDESCR"));
        output.WriteLine(sheet);

        Assert.Contains("UPC/EAN  5012345678900", sheet);
        Assert.Contains("GBAAA2400000", sheet);
        Assert.Contains("LEAD-OUT", sheet);

        // The second track was marked as pre-emphasised and no other was.
        string[] lines = sheet.Split('\n');
        Assert.Single(lines, l => l.Contains(" ON ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Offsets start from the two-second lead-in, not from zero: that is where the plant's timeline
    /// begins, and a sheet that starts at 00:00:00 puts every track two seconds early.
    /// </summary>
    [Fact]
    public void TrackOneStartsAfterTheStandardTwoSecondPause()
    {
        WriteThree(Rate * 3, Rate * 2);
        string sheet = File.ReadAllText(Path.Combine(_directory, "PQDESCR"));

        Assert.Contains("00:02:00", sheet);
        Assert.DoesNotContain(" 00:00:00 ", sheet);
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(75, "00:01:00")]
    [InlineData(74, "00:00:74")]
    [InlineData(75 * 60, "01:00:00")]
    [InlineData(75 * 60 * 74 + 75 * 33 + 12, "74:33:12")]
    public void TimecodesAreMinutesSecondsAndFrames(int frames, string expected) =>
        Assert.Equal(expected, DdpImage.Timecode(frames));

    // ── CD-TEXT ──────────────────────────────────────────────────

    [Fact]
    public void CdTextIsWrittenInWholePacksWithValidChecksums()
    {
        WriteThree(Rate * 2, Rate * 2);
        byte[] text = File.ReadAllBytes(Path.Combine(_directory, "CDTEXT.BIN"));

        output.WriteLine($"{text.Length} bytes, {text.Length / 18} packs");
        Assert.True(text.Length > 0);
        Assert.Equal(0, text.Length % 18);

        for (int at = 0; at < text.Length; at += 18)
        {
            ushort expected = DdpImage.Crc16(text, at, 16);
            var actual = (ushort)((text[at + 16] << 8) | text[at + 17]);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ATitleLongerThanOnePackIsSplitAcrossPacks()
    {
        var track = Track(DdpImage.SamplesPerFrame);
        string title = new('A', 30);

        DdpImage.Write(_directory, [track], [new DdpTrackInfo(title)], new DdpDiscInfo("Disc"), Rate);
        byte[] text = File.ReadAllBytes(Path.Combine(_directory, "CDTEXT.BIN"));

        // Thirty characters cannot fit one twelve-byte pack.
        int titlePacks = 0;
        for (int at = 0; at < text.Length; at += 18) if (text[at] == 0x80) titlePacks++;
        output.WriteLine($"{titlePacks} title packs for a {title.Length}-character title");
        Assert.True(titlePacks >= 3);
    }

    // ── catalogue numbers ────────────────────────────────────────

    [Theory]
    [InlineData("GB-AAA-24-00001", "GBAAA2400001")]
    [InlineData("gbaaa2400001", "GBAAA2400001")]
    [InlineData("too short", "")]
    [InlineData("", "")]
    public void AnIsrcIsNormalisedOrRejected(string input, string expected) =>
        Assert.Equal(expected, new DdpTrackInfo("t", Isrc: input).NormalisedIsrc);

    [Theory]
    [InlineData("5012345678900", "5012345678900")]
    [InlineData("5 012345 678900", "5012345678900")]
    [InlineData("012345678905", "0012345678905")]
    [InlineData("nonsense", "")]
    public void AUpcIsNormalisedOrRejected(string input, string expected) =>
        Assert.Equal(expected, new DdpDiscInfo("d", Upc: input).NormalisedUpc);

    // ── refusals ─────────────────────────────────────────────────

    [Fact]
    public void ARateOtherThanFortyFourOneIsRejected()
    {
        var track = Track(1_000);
        Assert.Throws<ArgumentException>(() =>
            DdpImage.Write(_directory, [track], [new DdpTrackInfo("t")], new DdpDiscInfo("d"), 48_000));
    }

    [Fact]
    public void AMonoTrackIsRejected()
    {
        float[][] mono = [new float[1_000]];
        Assert.Throws<ArgumentException>(() =>
            DdpImage.Write(_directory, [mono], [new DdpTrackInfo("t")], new DdpDiscInfo("d"), Rate));
    }

    [Fact]
    public void MismatchedTrackInformationIsRejected()
    {
        var track = Track(1_000);
        Assert.Throws<ArgumentException>(() =>
            DdpImage.Write(_directory, [track, track], [new DdpTrackInfo("t")], new DdpDiscInfo("d"), Rate));
    }

    [Fact]
    public void MoreThanNinetyNineTracksIsRejected()
    {
        var tracks = new List<float[][]>();
        var info = new List<DdpTrackInfo>();
        for (int i = 0; i < 100; i++) { tracks.Add(Track(588)); info.Add(new DdpTrackInfo($"{i}")); }

        Assert.Throws<ArgumentException>(() =>
            DdpImage.Write(_directory, tracks, info, new DdpDiscInfo("d"), Rate));
    }

    // ── the export path ──────────────────────────────────────────

    /// <summary>
    /// The transfer path cuts the DDP image from the same sector-aligned continuous programme the
    /// WAV package is cut from, and publishes nothing until every file is complete.
    /// </summary>
    [Fact]
    public async Task TheTransferPathPublishesAWholeImageSetAndLeavesNoStagingBehind()
    {
        var document = new AudioDocument(Track(Rate * 15), Rate, sourceBitDepth: 16);
        List<CdTrackPlan> plan =
        [
            new(0, Rate * 5, "One", Isrc: "GBAAA2400001"),
            new(Rate * 5, Rate * 10, "Two", PreEmphasis: true),
            new(Rate * 10, Rate * 15, "Three"),
        ];

        string folder = Path.Combine(_directory, "out");
        DdpResult result = await CdTransfer.ExportDdpAsync(document, plan, folder,
            new DdpDiscInfo("Side A", "The Transfer", "5012345678900"));

        output.WriteLine($"{result.Tracks} tracks, {result.ImageBytes} bytes into {result.Folder}");
        Assert.Equal(3, result.Tracks);
        Assert.Equal(folder, result.Folder);
        Assert.All(result.Files, f => Assert.True(File.Exists(f), $"{f} is missing"));

        // Nothing left over: a staging folder surviving the export would look like part of the set.
        Assert.Empty(Directory.EnumerateDirectories(folder));
        Assert.Equal(result.Files.Count, Directory.EnumerateFiles(folder).Count());

        // Fifteen seconds of audio at CD rate, to the frame.
        Assert.Equal(Rate * 15L * 4, result.ImageBytes);
    }

    [Fact]
    public async Task AFolderThatAlreadyHasFilesInItIsRefused()
    {
        var document = new AudioDocument(Track(Rate * 10), Rate, sourceBitDepth: 16);
        List<CdTrackPlan> plan = [new(0, Rate * 5, "One"), new(Rate * 5, Rate * 10, "Two")];

        File.WriteAllText(Path.Combine(_directory, "IMAGE.DAT"), "not mine");

        await Assert.ThrowsAsync<IOException>(() => CdTransfer.ExportDdpAsync(
            document, plan, _directory, new DdpDiscInfo("Side A")));
    }

    /// <summary>
    /// The other deliverable, which had no test at all — and the cue sheet it writes carried a
    /// fixed <c>PERFORMER "Deep Groove Transfer"</c>, so every disc burned from one was credited to
    /// the application. The dialog's own DISC PERFORMER field is what belongs there.
    /// </summary>
    [Fact]
    public async Task TheCueSheetCarriesThePerformersThatWereTypedAndInventsNone()
    {
        var document = new AudioDocument(Track(Rate * 15), Rate, sourceBitDepth: 16);
        List<CdTrackPlan> plan =
        [
            new(0, Rate * 5, "One", Performer: "The Band"),
            new(Rate * 5, Rate * 10, "Two"),
            new(Rate * 10, Rate * 15, "Three"),
        ];

        string named = Path.Combine(_directory, "named");
        CdPackageResult withPerformer = await CdTransfer.ExportPackageAsync(
            document, plan, named, "Side A", "The Transfer");

        Assert.Equal(3, withPerformer.WaveFiles.Count);
        Assert.All(withPerformer.WaveFiles, f => Assert.True(File.Exists(f), $"{f} is missing"));
        Assert.Empty(Directory.EnumerateDirectories(named));

        string cue = File.ReadAllText(withPerformer.CueFile);
        output.WriteLine(cue);
        Assert.Contains("TITLE \"Side A\"", cue, StringComparison.Ordinal);
        Assert.Contains("PERFORMER \"The Transfer\"", cue, StringComparison.Ordinal);
        Assert.Contains("PERFORMER \"The Band\"", cue, StringComparison.Ordinal);
        Assert.DoesNotContain("Deep Groove", cue, StringComparison.Ordinal);
        // Two of the three tracks have no performer, so the disc line plus one track line is all.
        Assert.Equal(2, cue.Split("PERFORMER").Length - 1);

        // Blank stays blank rather than becoming something invented, which is the rule the PQ sheet
        // already states about a track's performer.
        CdPackageResult anonymous = await CdTransfer.ExportPackageAsync(
            document, [new(0, Rate * 15, "Only")], Path.Combine(_directory, "anon"), "Side B");
        Assert.DoesNotContain("PERFORMER", File.ReadAllText(anonymous.CueFile), StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var tracks = new List<float[][]>();
        var info = new List<DdpTrackInfo>();
        for (int i = 0; i < 4; i++) { tracks.Add(Track(Rate)); info.Add(new DdpTrackInfo($"{i}")); }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DdpImage.Write(_directory, tracks, info, new DdpDiscInfo("d"), Rate, cancellation.Token));
    }
}
