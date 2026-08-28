using System.IO;
using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// A disc made from separate files: a running order, a gap, and a cue sheet that says where each
/// track's music starts.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "take a bunch of separate files and burn them to disc with the ability to add
/// pregaps". Four songs went onto a montage lane, were rendered through Prepare CD, and came out as
/// a cue sheet whose every track read <c>INDEX 01 00:00:00</c> - four songs butted hard against each
/// other with no countdown anywhere. Nothing on that path was broken: the montage never asks about a
/// gap, and the CD window's box starts at zero because a <i>transferred side</i> arrives with the
/// record's own quiet already between its songs. A set of finished files does not.
/// </para>
/// <para>
/// The two cases need different arithmetic, not just a different default. Evening out a side's own
/// quiet is a subtraction before it is an addition - see <see cref="CdGapTests"/>. Between finished
/// masters there is nothing to subtract, and trimming their heads and tails back to the first sample
/// above a threshold to make room for silence would edit them to no purpose.
/// </para>
/// </remarks>
public sealed class CdFromFilesTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-cd-files").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* A temp directory the OS will reclaim. */ }
    }

    /// <summary>
    /// A finished master: a tone that fades in over a second and out over a second, so both ends sit
    /// below any sane "quiet" threshold. That is exactly what a trimming gap pass would eat.
    /// </summary>
    private static float[][] Song(double seconds, double hz)
    {
        int frames = (int)(seconds * Rate);
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double fade = Math.Min(1, Math.Min(i, frames - 1 - i) / (double)Rate);
            float value = (float)(0.5 * fade * Math.Sin(2 * Math.PI * hz * i / Rate));
            left[i] = right[i] = value;
        }
        return [left, right];
    }

    private static List<(string Name, float[][] Channels)> Files() =>
    [
        ("Super Do Nothing Day", Song(10, 220)),
        ("One More Chance", Song(12, 330)),
        ("Watching The World Go By", Song(8, 440)),
    ];

    /// <summary>
    /// The whole point: separate files in, a cue sheet with real pregaps out. This is the assertion
    /// the reported cue fails - every one of its tracks said <c>INDEX 01 00:00:00</c>.
    /// </summary>
    [Fact]
    public async Task SeparateFilesReachTheCueSheetWithAPregapBetweenEveryPair()
    {
        CdTransfer.CdAssembly assembled = CdTransfer.Assemble(Files(), Rate);

        string folder = Path.Combine(_directory, "package");
        CdPackageResult package = await CdTransfer.ExportPackageAsync(
            assembled.Document, assembled.Tracks, folder, "The Record");
        string cue = File.ReadAllText(package.CueFile);
        output.WriteLine(cue);

        // Track 01 opens the disc, so it has no pregap and no INDEX 00. Tracks 02 and 03 each get
        // one, and their music starts two seconds into their own file.
        Assert.Equal(2, cue.Split("INDEX 00").Length - 1);
        Assert.Equal(3, cue.Split("INDEX 01").Length - 1);
        Assert.Equal(2, cue.Split("INDEX 01 00:02:00").Length - 1);
        Assert.Equal(1, cue.Split("INDEX 01 00:00:00").Length - 1);

        // Each file is named and titled from the file it came from, in the order given.
        Assert.Contains("FILE \"01 - Super Do Nothing Day.wav\" WAVE", cue, StringComparison.Ordinal);
        Assert.Contains("TITLE \"One More Chance\"", cue, StringComparison.Ordinal);
        Assert.Contains("  TRACK 03 AUDIO", cue, StringComparison.Ordinal);
    }

    /// <summary>
    /// The silence is samples in the file, not a note in the sheet - a burner that ignored the cue
    /// entirely would still cut the gap. Two seconds of CD-rate 16-bit stereo is 2 x 44100 x 4 bytes.
    /// </summary>
    [Fact]
    public async Task ThePregapIsRealSilenceAtTheHeadOfTheFile()
    {
        CdTransfer.CdAssembly assembled = CdTransfer.Assemble(Files(), Rate);
        CdPackageResult package = await CdTransfer.ExportPackageAsync(
            assembled.Document, assembled.Tracks, Path.Combine(_directory, "silence"), "The Record");

        long[] lengths = [.. package.WaveFiles.Select(f => new FileInfo(f).Length)];
        long gapBytes = 2L * Rate * 4;
        var files = Files();

        // Sector alignment moves a boundary by under half a sector, so each track's music is
        // compared against the file it came from rather than against its neighbours.
        for (int i = 1; i < lengths.Length; i++)
        {
            long music = lengths[i] - gapBytes;
            long expected = (long)files[i].Channels[0].Length * 4;
            Assert.True(Math.Abs(music - expected) <= CdAudioFormat.BytesPerSector,
                $"Track {i + 1:00} holds {music} bytes of music where the file has {expected}.");
        }
    }

    /// <summary>
    /// A finished master keeps its own opening and ending. The gap is added ahead of the track and
    /// nothing is taken off either end of it to make room - which is the opposite of what the same
    /// control does to a transferred side, and deliberately so.
    /// </summary>
    [Fact]
    public void TheGapAddsAndNeverTrimsAFadeOffTheEndsOfAFinishedFile()
    {
        var files = Files();
        CdTransfer.CdAssembly assembled = CdTransfer.Assemble(files, Rate);

        int at = 0;
        for (int i = 0; i < files.Count; i++)
        {
            Assert.Equal(at, assembled.Tracks[i].SourceStart);
            at += files[i].Channels[0].Length;
            Assert.Equal(at, assembled.Tracks[i].SourceEnd);
        }
        Assert.Equal(at, assembled.Document.Length);

        // What the trimming rule would have done to the same material, for contrast: a fade that
        // passes below -40 dBFS at both ends is exactly what it reclaims.
        List<CdTrackPlan> trimmed = CdTransfer.ApplyGaps(
            assembled.Document.Channels.ToArray(), Rate, assembled.Tracks, 2, -40);
        Assert.True(trimmed[1].SourceStart > assembled.Tracks[1].SourceStart,
            "The trimming rule was expected to move a boundary here; if it no longer does, this " +
            "test has stopped contrasting anything and the add-only rule needs a different witness.");
    }

    /// <summary>Zero is still zero: asking for no gap gives a cue sheet with no INDEX 00 in it.</summary>
    [Fact]
    public async Task NoGapAskedForIsNoGapWritten()
    {
        CdTransfer.CdAssembly assembled = CdTransfer.Assemble(Files(), Rate, gapSeconds: 0);
        CdPackageResult package = await CdTransfer.ExportPackageAsync(
            assembled.Document, assembled.Tracks, Path.Combine(_directory, "nogap"), "The Record");
        string cue = File.ReadAllText(package.CueFile);

        Assert.DoesNotContain("INDEX 00", cue, StringComparison.Ordinal);
        Assert.Equal(3, cue.Split("INDEX 01 00:00:00").Length - 1);
    }

    /// <summary>
    /// The gap is declared, not written into the programme. Baking it in here and letting the
    /// packager add it again would give every gap twice over.
    /// </summary>
    [Fact]
    public void TheProgrammeItselfHoldsNoGap()
    {
        var files = Files();
        int total = files.Sum(f => f.Channels[0].Length);
        Assert.Equal(total, CdTransfer.Assemble(files, Rate).Document.Length);
        Assert.Equal(total, CdTransfer.Assemble(files, Rate, gapSeconds: 0).Document.Length);
    }

    /// <summary>A file that does not match the others' channel layout is named, not silently mixed.</summary>
    [Fact]
    public void AFileWithADifferentChannelCountIsRefusedByName()
    {
        List<(string Name, float[][] Channels)> mixed =
        [
            ("Stereo", Song(5, 220)),
            ("Mono", [new float[5 * Rate]]),
        ];
        var error = Assert.Throws<ArgumentException>(() => CdTransfer.Assemble(mixed, Rate));
        Assert.Contains("Mono", error.Message, StringComparison.Ordinal);
    }
}
