using System.IO;
using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The same disc written as one image, and a cue sheet whose INDEX times climb.
/// </summary>
/// <remarks>
/// <para>
/// Asked for after reading a track-per-file cue sheet and finding every track's INDEX identical:
/// "why are all the indexes the same". They were right that it looks wrong and right that nothing
/// was: in that form each track is its own <c>FILE</c> and its INDEX times are measured from the
/// start of that file, so every track carrying a two-second pregap reads <c>INDEX 00 00:00:00</c> /
/// <c>INDEX 01 00:02:00</c> and the column never moves.
/// </para>
/// <para>
/// The single-image form measures from the start of the disc instead, so the numbers are the times a
/// player will show. Some burners only parse one <c>FILE</c> line, which is the other reason to have
/// it. Both forms must describe the same disc; the test that matters here is the one that proves it
/// sample by sample.
/// </para>
/// </remarks>
public sealed class CdImageTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("wavelab-cd-image").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* A temp directory the OS will reclaim. */ }
    }

    /// <summary>A programme where every sample says where it came from, so a misplacement shows.</summary>
    private static AudioDocument Programme(int seconds)
    {
        int frames = seconds * Rate;
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            // On the 16-bit grid, so nothing is dithered and the comparison is exact.
            left[i] = (i % 20_001 - 10_000) / 32768f;
            right[i] = (10_000 - i % 20_001) / 32768f;
        }
        return new AudioDocument([left, right], Rate, sourceBitDepth: 16);
    }

    private static List<CdTrackPlan> Plan() =>
    [
        new(0, Rate * 20, "One"),
        new(Rate * 20, Rate * 45, "Two", PregapSeconds: 2),
        new(Rate * 45, Rate * 75, "Three", PregapSeconds: 2),
    ];

    /// <summary>
    /// The INDEX times are positions on the disc, so they climb: track two's music starts where
    /// track one ended plus its own two-second pregap, and track three's after that.
    /// </summary>
    [Fact]
    public async Task TheImageCueIndexesEveryTrackByItsPositionOnTheDisc()
    {
        CdPackageResult result = await CdTransfer.ExportImageAsync(
            Programme(75), Plan(), Path.Combine(_directory, "image"), "Side A");
        string cue = File.ReadAllText(result.CueFile);
        output.WriteLine(cue);

        Assert.Single(result.WaveFiles);
        Assert.Equal(1, cue.Split("FILE ").Length - 1);
        Assert.Contains("FILE \"Side A.wav\" WAVE", cue, StringComparison.Ordinal);

        // 20 s, then 2 s of gap, then 25 s, then 2 s of gap.
        Assert.Contains("    INDEX 01 00:00:00", cue, StringComparison.Ordinal);   // track 01 opens the disc
        Assert.Contains("    INDEX 00 00:20:00", cue, StringComparison.Ordinal);   // track 02's gap
        Assert.Contains("    INDEX 01 00:22:00", cue, StringComparison.Ordinal);   // track 02's music
        Assert.Contains("    INDEX 00 00:47:00", cue, StringComparison.Ordinal);   // track 03's gap
        Assert.Contains("    INDEX 01 00:49:00", cue, StringComparison.Ordinal);   // track 03's music

        // Track 01 has no pregap and so no INDEX 00, exactly as in the per-file sheet.
        Assert.Equal(2, cue.Split("INDEX 00").Length - 1);
    }

    /// <summary>
    /// The whole point of having both: they are the same disc. Laying the package's per-track files
    /// end to end has to give the image back, sample for sample - pregaps, sector padding and all.
    /// </summary>
    [Fact]
    public async Task TheImageAndThePerTrackPackageAreTheSameDisc()
    {
        AudioDocument programme = Programme(75);
        List<CdTrackPlan> plan = Plan();

        CdPackageResult image = await CdTransfer.ExportImageAsync(
            programme, plan, Path.Combine(_directory, "one"), "Side A");
        CdPackageResult package = await CdTransfer.ExportPackageAsync(
            programme, plan, Path.Combine(_directory, "many"), "Side A");

        AudioDocument written = WavCodec.Load(image.WaveFiles[0]);
        List<AudioDocument> parts = [.. package.WaveFiles.Select(f => WavCodec.Load(f))];

        Assert.Equal(parts.Sum(p => p.Length), written.Length);
        Assert.Equal(CdTransfer.CdSampleRate, written.SampleRate);

        int at = 0;
        foreach (AudioDocument part in parts)
        {
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < part.Length; i++)
                    if (part.Channels[c][i] != written.Channels[c][at + i])
                        Assert.Fail($"Channel {c} differs at image sample {at + i}: " +
                                    $"the image holds {written.Channels[c][at + i]}, the package {part.Channels[c][i]}.");
            at += part.Length;
        }
    }

    /// <summary>
    /// The image is the programme the plan describes, not the document's own order. A running order
    /// arranged with the window's arrows has to reach the disc arranged.
    /// </summary>
    [Fact]
    public async Task ReorderingThePlanReordersTheImage()
    {
        AudioDocument programme = Programme(75);
        List<CdTrackPlan> plan = Plan();
        List<CdTrackPlan> reversed = [plan[2] with { PregapSeconds = 0 }, plan[1], plan[0] with { PregapSeconds = 2 }];

        CdPackageResult forward = await CdTransfer.ExportImageAsync(
            programme, plan, Path.Combine(_directory, "fwd"), "Side A");
        CdPackageResult backward = await CdTransfer.ExportImageAsync(
            programme, reversed, Path.Combine(_directory, "back"), "Side A");

        string cue = File.ReadAllText(backward.CueFile);
        output.WriteLine(cue);
        // Environment.NewLine, because that is what StringBuilder.AppendLine writes and the project
        // sets EnableWindowsTargeting so it also builds away from Windows.
        Assert.Contains($"  TRACK 01 AUDIO{Environment.NewLine}    TITLE \"Three\"",
            cue, StringComparison.Ordinal);
        Assert.Contains($"  TRACK 03 AUDIO{Environment.NewLine}    TITLE \"One\"",
            cue, StringComparison.Ordinal);

        // Same total programme either way - the same three tracks and the same two gaps.
        Assert.Equal(WavCodec.Load(forward.WaveFiles[0]).Length, WavCodec.Load(backward.WaveFiles[0]).Length);

        // But not the same audio, or the arrows would be doing nothing.
        AudioDocument first = WavCodec.Load(forward.WaveFiles[0]);
        AudioDocument second = WavCodec.Load(backward.WaveFiles[0]);
        Assert.NotEqual(first.Channels[0][Rate * 5], second.Channels[0][Rate * 5]);
    }

    /// <summary>
    /// A disc built from separate files, written as an image: the case the option was added for.
    /// The gap is real silence inside the one WAV, sitting where the sheet says it does.
    /// </summary>
    [Fact]
    public async Task FilesAssembledIntoAnImageCarryTheirGapsAsSilenceInsideIt()
    {
        static float[][] Song(double seconds, float level)
        {
            int frames = (int)(seconds * Rate);
            var left = new float[frames];
            var right = new float[frames];
            Array.Fill(left, level);
            Array.Fill(right, level);
            return [left, right];
        }

        List<(string Name, float[][] Channels)> files =
            [("One", Song(10, 0.25f)), ("Two", Song(12, 0.5f))];
        CdTransfer.CdAssembly assembled = CdTransfer.Assemble(files, Rate);

        CdPackageResult result = await CdTransfer.ExportImageAsync(
            assembled.Document, assembled.Tracks, Path.Combine(_directory, "files"), "The Record");
        AudioDocument image = WavCodec.Load(result.WaveFiles[0]);

        // Track one's ten seconds, then two seconds of digital silence, then track two.
        int gapStart = 10 * Rate;
        Assert.Equal(0.25f, image.Channels[0][gapStart - 1]);
        for (int i = gapStart; i < gapStart + 2 * Rate; i++)
            Assert.Equal(0f, image.Channels[0][i]);
        Assert.Equal(0.5f, image.Channels[0][gapStart + 2 * Rate]);

        Assert.Contains("    INDEX 00 00:10:00", File.ReadAllText(result.CueFile), StringComparison.Ordinal);
        Assert.Contains("    INDEX 01 00:12:00", File.ReadAllText(result.CueFile), StringComparison.Ordinal);
    }

    /// <summary>Neither file appears until both are complete, and a failed export leaves nothing.</summary>
    [Fact]
    public async Task TheFolderMustBeEmptyAndNothingIsLeftBehindWhenItIsNot()
    {
        string folder = Path.Combine(_directory, "occupied");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "already here");

        await Assert.ThrowsAsync<IOException>(() => CdTransfer.ExportImageAsync(
            Programme(75), Plan(), folder, "Side A"));
        Assert.Single(Directory.GetFileSystemEntries(folder));
    }
}
