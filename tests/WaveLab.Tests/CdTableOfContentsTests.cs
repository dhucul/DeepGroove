using WaveLab.Audio;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Where a CD track ends is not in the table of contents. A drive reports start sectors and a
/// lead-out; every end is inferred, and the inference is the part that can be wrong.
/// </summary>
/// <remarks>
/// <para>
/// The case that forced the inference is a CD-Extra disc: an audio session, then a data session.
/// The sectors between the last audio track and the data track are session 1's lead-out, session
/// 2's lead-in and the following pregap — 11,400 of them, and not one is audio. Charged to the
/// audio track they make the extractor read past the programme, which the drive rejects; taken
/// off, the track ends where the music does.
/// </para>
/// <para>
/// <see cref="ICdAudioDevice"/> is the seam that lets any of this be exercised without a disc in a
/// drive, which matters because the alternative is that none of it is exercised at all. What a
/// synthetic TOC cannot reach is the drive I/O underneath it — <c>WindowsCdAudioPlatform</c> still
/// needs a real disc before anyone should trust it.
/// </para>
/// </remarks>
public sealed class CdTableOfContentsTests
{
    private const string DevicePath = @"\\.\D:";
    private const string RootPath = @"D:\";

    /// <summary>Session 1's lead-out (90 s), session 2's lead-in (60 s) and a 2 s pregap, at 75 sectors/s.</summary>
    private const int SessionGapSectors = 11_400;

    private const byte AudioControl = 0x00;
    private const byte DataControl = 0x04;
    private const int LeadOutTrackNumber = 0xAA;

    // One disc shape serves the whole file. The sector numbers are deliberately small: the
    // extraction test decodes every sector it reads into a document, and a realistic audio
    // session would be the better part of a gigabyte of float.
    private const int Track1Start = 150;        // the 2 s pregap every disc opens with
    private const int Track2Start = 500;
    private const int DataStart = 12_000;
    private const int LeadOutStart = 30_000;

    /// <summary>Track 2 is the one that meets the data track, so it is the one the gap comes off.</summary>
    private const int Track2EndAfterGap = DataStart - SessionGapSectors;

    private static CdAudioTocEntry Track(int number, int start, byte control) => new(number, start, control);

    private static CdAudioTableOfContents Toc(params CdAudioTocEntry[] entries) =>
        new(1, entries.Count(entry => entry.TrackNumber != LeadOutTrackNumber), entries);

    /// <summary>Two audio tracks, then a data track: the disc the gap rule was written for.</summary>
    private static CdAudioTableOfContents CdExtraToc() => Toc(
        Track(1, Track1Start, AudioControl),
        Track(2, Track2Start, AudioControl),
        Track(3, DataStart, DataControl),
        Track(LeadOutTrackNumber, LeadOutStart, AudioControl));

    /// <summary>A sample value unique to its sector, so a decoded document can be traced back to one.</summary>
    private static short SampleForSector(int sector) => (short)(sector % 8_000);

    /// <summary>
    /// A drive that answers from a table of contents the test wrote, fills every sector with a
    /// value derived from its own address, and remembers which sectors it was asked for.
    /// </summary>
    private sealed class FakeDevice(CdAudioTableOfContents toc) : ICdAudioDevice
    {
        public List<(int Start, int Count)> Reads { get; } = [];

        public CdAudioTableOfContents ReadTableOfContents() => toc;

        public int ReadAudioSectors(int startSector, int sectorCount, byte[] destination)
        {
            Reads.Add((startSector, sectorCount));
            for (int s = 0; s < sectorCount; s++)
            {
                short sample = SampleForSector(startSector + s);
                int offset = s * CdAudioFormat.BytesPerSector;
                for (int i = 0; i < CdAudioFormat.BytesPerSector; i += 2)
                {
                    destination[offset + i] = (byte)(sample & 0xFF);
                    destination[offset + i + 1] = (byte)((sample >> 8) & 0xFF);
                }
            }
            return sectorCount * CdAudioFormat.BytesPerSector;
        }

        // The service opens the device with `using`, so this runs between calls. The fake holds
        // nothing a real one would have to release.
        public void Dispose() { }
    }

    private sealed class FakePlatform(CdAudioTableOfContents toc) : ICdAudioPlatform
    {
        public FakeDevice Device { get; } = new(toc);

        public IReadOnlyList<CdAudioDeviceIdentity> EnumerateOpticalDrives() =>
            [new CdAudioDeviceIdentity(RootPath, DevicePath, "Fake optical drive")];

        public ICdAudioDevice OpenDevice(string devicePath) => Device;
    }

    private static (CdAudioService Service, FakePlatform Platform) DriveWith(CdAudioTableOfContents toc)
    {
        var platform = new FakePlatform(toc);
        return (new CdAudioService(platform), platform);
    }

    [Fact]
    public async Task TheSessionGapComesOffOnlyTheAudioTrackThatMeetsTheData()
    {
        var (service, _) = DriveWith(CdExtraToc());

        var disc = await service.ReadDiscAsync(DevicePath);

        Assert.Equal(3, disc.Tracks.Count);

        // Track 1 hands over to another audio track, so there is nothing between them.
        Assert.Equal(Track1Start, disc.Tracks[0].StartSector);
        Assert.Equal(Track2Start, disc.Tracks[0].EndSector);

        // Track 2 is the last of its session, and stops 11,400 sectors short of the data track.
        Assert.Equal(Track2Start, disc.Tracks[1].StartSector);
        Assert.Equal(Track2EndAfterGap, disc.Tracks[1].EndSector);

        // The data track keeps its full range: the gap is behind it, not in front of it.
        Assert.Equal(CdTrackKind.Data, disc.Tracks[2].Kind);
        Assert.Equal(DataStart, disc.Tracks[2].StartSector);
        Assert.Equal(LeadOutStart, disc.Tracks[2].EndSector);
    }

    [Fact]
    public async Task AnAllAudioDiscRunsEachTrackToTheNextStart()
    {
        var (service, _) = DriveWith(Toc(
            Track(1, Track1Start, AudioControl),
            Track(2, Track2Start, AudioControl),
            Track(3, DataStart, AudioControl),
            Track(LeadOutTrackNumber, LeadOutStart, AudioControl)));

        var disc = await service.ReadDiscAsync(DevicePath);

        Assert.Equal(Track2Start, disc.Tracks[0].EndSector);
        Assert.Equal(DataStart, disc.Tracks[1].EndSector);
        Assert.Equal(LeadOutStart, disc.Tracks[2].EndSector);
        Assert.All(disc.Tracks, track => Assert.True(track.IsAudio));
    }

    /// <summary>
    /// The lead-out is a descriptor like any other and it carries a control field, which on some
    /// discs has the data bit set. Reading that as "an audio track followed by data" would take
    /// two and a half minutes off the last track of a perfectly ordinary album.
    /// </summary>
    [Fact]
    public async Task ALeadOutFlaggedAsDataDoesNotLookLikeASessionBoundary()
    {
        var (service, _) = DriveWith(Toc(
            Track(1, Track1Start, AudioControl),
            Track(2, Track2Start, AudioControl),
            Track(LeadOutTrackNumber, LeadOutStart, DataControl)));

        var disc = await service.ReadDiscAsync(DevicePath);

        Assert.Equal(LeadOutStart, disc.Tracks[^1].EndSector);
    }

    /// <summary>
    /// Subtracting the gap from a track shorter than the gap would leave it ending before it
    /// began. A negative range is not a short track; it is a TOC nobody can act on.
    /// </summary>
    [Fact]
    public async Task AnAudioTrackShorterThanTheGapIsRejectedRatherThanInverted()
    {
        var (service, _) = DriveWith(Toc(
            Track(1, Track1Start, AudioControl),
            Track(2, SessionGapSectors, DataControl),
            Track(LeadOutTrackNumber, LeadOutStart, AudioControl)));

        var failure = await Assert.ThrowsAsync<CdAudioException>(() => service.ReadDiscAsync(DevicePath));

        Assert.Equal(CdAudioFailureReason.InvalidTableOfContents, failure.Reason);
    }

    /// <summary>
    /// The boundary is only worth anything if the reads honour it. This is the failure the rule
    /// was written to stop: a drive asked for a sector inside the session gap refuses it, and the
    /// import fails on a disc that is not damaged.
    /// </summary>
    [Fact]
    public async Task ExtractionReadsTheTrackSectorsAndNothingBeyondThem()
    {
        var (service, platform) = DriveWith(CdExtraToc());

        var imports = await service.ExtractTracksAsync(DevicePath, [2]);

        var import = Assert.Single(imports);
        int sectors = Track2EndAfterGap - Track2Start;

        // Every sector of the track, in order, once, and not one sector further.
        int expectedStart = Track2Start;
        foreach ((int start, int count) in platform.Device.Reads)
        {
            Assert.Equal(expectedStart, start);
            Assert.InRange(count, 1, 16);
            expectedStart += count;
        }
        Assert.Equal(Track2EndAfterGap, expectedStart);
        Assert.Equal(sectors, platform.Device.Reads.Sum(read => read.Count));

        // And the samples are the ones those sectors hold, so the range was not merely the right
        // length but in the right place.
        var document = import.Document;
        Assert.Equal(sectors * CdAudioFormat.FramesPerSector, document.Channels[0].Length);
        Assert.Equal(SampleForSector(Track2Start) / 32768f, document.Channels[0][0]);
        Assert.Equal(SampleForSector(Track2EndAfterGap - 1) / 32768f, document.Channels[1][^1]);
    }

    /// <summary>
    /// The known cost of the rule, pinned so that it is a decision rather than a surprise.
    /// </summary>
    /// <remarks>
    /// A single-session disc whose data track sits last — rare, but legal — produces the same
    /// table of contents as the CD-Extra disc above, and there is nothing in that TOC to tell the
    /// two apart. So on that disc the audio track before the data track loses 11,400 sectors that
    /// were music. Telling them apart needs the full-TOC session data that
    /// <see cref="ICdAudioDevice"/> does not expose; if it ever does, this is the test that says
    /// what changes.
    /// </remarks>
    [Fact]
    public async Task ADataTrackLastOnASingleSessionDiscCostsTheAudioBeforeIt()
    {
        var (service, _) = DriveWith(CdExtraToc());

        var disc = await service.ReadDiscAsync(DevicePath);

        int reported = disc.Tracks[1].SectorCount;
        int onTheDisc = DataStart - Track2Start;

        Assert.Equal(SessionGapSectors, onTheDisc - reported);
        Assert.Equal(152, (onTheDisc - reported) / CdAudioFormat.SectorsPerSecond);
    }
}
