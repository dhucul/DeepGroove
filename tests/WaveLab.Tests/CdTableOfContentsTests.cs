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
/// 2's lead-in and the following pregap — 11,400 of them on this fixture, and not one is audio.
/// The full TOC gives session 1's exact lead-out; using that boundary keeps the extractor out of
/// these sectors without assuming every mixed-mode disc has the same layout.
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

    private static CdAudioTableOfContents TocWithSessions(
        CdAudioTocEntry[] entries, params CdAudioSession[] sessions) =>
        new(1, entries.Count(entry => entry.TrackNumber != LeadOutTrackNumber), entries, sessions);

    /// <summary>Two audio tracks, then a data track in a second full-TOC session.</summary>
    private static CdAudioTableOfContents CdExtraToc() => TocWithSessions(
        [
            Track(1, Track1Start, AudioControl),
            Track(2, Track2Start, AudioControl),
            Track(3, DataStart, DataControl),
            Track(LeadOutTrackNumber, LeadOutStart, AudioControl),
        ],
        new CdAudioSession(1, 1, 2, Track2EndAfterGap),
        new CdAudioSession(2, 3, 3, LeadOutStart));

    /// <summary>A sample value unique to its sector, so a decoded document can be traced back to one.</summary>
    private static short SampleForSector(int sector) => (short)(sector % 8_000);

    /// <summary>
    /// A drive that answers from a table of contents the test wrote, fills every sector with a
    /// value derived from its own address, and remembers which sectors it was asked for.
    /// </summary>
    private sealed class FakeDevice(CdAudioTableOfContents toc) : ICdAudioDevice
    {
        private int _readSerial;
        public List<(int Start, int Count)> Reads { get; } = [];
        public bool NeverRepeatReadData { get; set; }

        public CdAudioTableOfContents ReadTableOfContents() => toc;

        public int ReadAudioSectors(int startSector, int sectorCount, byte[] destination)
        {
            Reads.Add((startSector, sectorCount));
            int serial = ++_readSerial;
            for (int s = 0; s < sectorCount; s++)
            {
                short sample = (short)(SampleForSector(startSector + s) +
                    (NeverRepeatReadData ? serial : 0));
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

    private sealed class RepeatingReadDevice : ICdAudioDevice
    {
        public CdAudioTableOfContents ReadTableOfContents() =>
            throw new NotSupportedException();

        public int ReadAudioSectors(int startSector, int sectorCount, byte[] destination) =>
            sectorCount * CdAudioFormat.BytesPerSector;

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

    [Fact]
    public async Task AnAudioTrackStopsAtItsSessionLeadOutBeforeAnotherAudioSession()
    {
        const int sessionOneLeadOut = 900;
        var toc = TocWithSessions(
            [
                Track(1, Track1Start, AudioControl),
                Track(2, DataStart, AudioControl),
                Track(LeadOutTrackNumber, LeadOutStart, AudioControl),
            ],
            new CdAudioSession(1, 1, 1, sessionOneLeadOut),
            new CdAudioSession(2, 2, 2, LeadOutStart));
        var (service, _) = DriveWith(toc);

        CdAudioDisc disc = await service.ReadDiscAsync(DevicePath);

        Assert.Equal(sessionOneLeadOut, disc.Tracks[0].EndSector);
        Assert.Equal(DataStart, disc.Tracks[1].StartSector);
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
    /// Without full-session data the safe choice is to preserve the range stated by the standard
    /// TOC. Guessing a fixed inter-session gap could remove legal audio.
    /// </summary>
    [Fact]
    public async Task MissingSessionDataNeverShortensTheDeclaredAudioRange()
    {
        var (service, _) = DriveWith(Toc(
            Track(1, Track1Start, AudioControl),
            Track(2, SessionGapSectors, DataControl),
            Track(LeadOutTrackNumber, LeadOutStart, AudioControl)));

        CdAudioDisc disc = await service.ReadDiscAsync(DevicePath);
        Assert.Equal(SessionGapSectors - Track1Start, disc.Tracks[0].SectorCount);
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

        // Every sector of the track, in order, is read twice for verification, and not one sector
        // further. Each adjacent pair must describe the same raw-read batch.
        int expectedStart = Track2Start;
        Assert.Equal(0, platform.Device.Reads.Count % 2);
        for (int i = 0; i < platform.Device.Reads.Count; i += 2)
        {
            (int start, int count) = platform.Device.Reads[i];
            Assert.Equal(platform.Device.Reads[i], platform.Device.Reads[i + 1]);
            Assert.Equal(expectedStart, start);
            Assert.InRange(count, 1, 16);
            expectedStart += count;
        }
        Assert.Equal(Track2EndAfterGap, expectedStart);
        Assert.Equal(sectors * 2, platform.Device.Reads.Sum(read => read.Count));

        // And the samples are the ones those sectors hold, so the range was not merely the right
        // length but in the right place.
        var document = import.Document;
        Assert.Equal(sectors * CdAudioFormat.FramesPerSector, document.Channels[0].Length);
        Assert.Equal(SampleForSector(Track2Start) / 32768f, document.Channels[0][0]);
        Assert.Equal(SampleForSector(Track2EndAfterGap - 1) / 32768f, document.Channels[1][^1]);
    }

    /// <summary>
    /// A data-last single-session disc must retain every sector before the data track.
    /// </summary>
    /// <remarks>
    /// The full TOC identifies both tracks as belonging to session 1, so the data track's start is
    /// the exact end of the audio track. No guessed CD-Extra subtraction is permitted.
    /// </remarks>
    [Fact]
    public async Task ADataTrackLastOnASingleSessionDiscPreservesTheAudioBeforeIt()
    {
        var toc = TocWithSessions(
            [
                Track(1, Track1Start, AudioControl),
                Track(2, Track2Start, AudioControl),
                Track(3, DataStart, DataControl),
                Track(LeadOutTrackNumber, LeadOutStart, AudioControl),
            ],
            new CdAudioSession(1, 1, 3, LeadOutStart));
        var (service, _) = DriveWith(toc);

        var disc = await service.ReadDiscAsync(DevicePath);

        int reported = disc.Tracks[1].SectorCount;
        int onTheDisc = DataStart - Track2Start;

        Assert.Equal(onTheDisc, reported);
    }

    [Fact]
    public void FullTocParsingReturnsExactSessionLeadOuts()
    {
        byte[] Descriptor(int session, int point, int pMinute, int pSecond = 0, int pFrame = 0)
        {
            var value = new byte[11];
            value[0] = (byte)session;
            value[3] = (byte)point;
            value[8] = (byte)pMinute;
            value[9] = (byte)pSecond;
            value[10] = (byte)pFrame;
            return value;
        }

        byte[][] descriptors =
        [
            Descriptor(1, 0xA0, 1),
            Descriptor(1, 0xA1, 2),
            Descriptor(1, 0xA2, 0, 10, 0), // absolute 750, logical sector 600
            Descriptor(2, 0xA0, 3),
            Descriptor(2, 0xA1, 3),
            Descriptor(2, 0xA2, 6, 42, 0), // absolute 30,150, logical sector 30,000
        ];
        int bytes = 4 + descriptors.Length * 11;
        var buffer = new byte[bytes];
        int payloadLength = bytes - 2;
        buffer[0] = (byte)(payloadLength >> 8);
        buffer[1] = (byte)payloadLength;
        buffer[2] = 1;
        buffer[3] = 2;
        for (int i = 0; i < descriptors.Length; i++)
            Buffer.BlockCopy(descriptors[i], 0, buffer, 4 + i * 11, 11);

        IReadOnlyList<CdAudioSession> sessions =
            WindowsCdAudioDevice.ParseFullTableOfContents(buffer, bytes);

        Assert.Equal(
            [new CdAudioSession(1, 1, 2, 600), new CdAudioSession(2, 3, 3, 30_000)],
            sessions);
    }

    [Fact]
    public void OptionalFullTocTimeoutFallsBackButCancellationDoesNot()
    {
        Assert.True(WindowsCdAudioDevice.IsOptionalFullTocFailure(new TimeoutException()));
        Assert.False(WindowsCdAudioDevice.IsOptionalFullTocFailure(
            new OperationCanceledException()));
    }

    [Fact]
    public void VerifiedReadsReuseCallerOwnedBuffersWithoutPerBatchAllocation()
    {
        const int sectors = 16;
        int bytes = sectors * CdAudioFormat.BytesPerSector;
        byte[][] buffers = Enumerable.Range(0, 5)
            .Select(_ => new byte[bytes]).ToArray();
        using var device = new RepeatingReadDevice();

        CdAudioService.ReadVerifiedAudioSectors(
            device, 150, sectors, buffers, DevicePath, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int batch = 0; batch < 1_000; batch++)
        {
            CdAudioService.ReadVerifiedAudioSectors(
                device, 150 + batch * sectors, sectors, buffers,
                DevicePath, CancellationToken.None);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 1_024);
    }

    [Fact]
    public async Task ExtractionRefusesRawReadsThatNeverVerify()
    {
        CdAudioTableOfContents toc = Toc(
            Track(1, 150, AudioControl),
            Track(LeadOutTrackNumber, 151, AudioControl));
        var (service, platform) = DriveWith(toc);
        platform.Device.NeverRepeatReadData = true;

        CdAudioException failure = await Assert.ThrowsAsync<CdAudioException>(
            () => service.ExtractTracksAsync(DevicePath, [1]));

        Assert.Equal(CdAudioFailureReason.ReadFailed, failure.Reason);
        Assert.Contains("did not agree", failure.Message, StringComparison.Ordinal);
        Assert.Equal(5, platform.Device.Reads.Count);
    }
}
