namespace WaveLab.Audio;

/// <summary>A raw TOC descriptor returned by an optical drive.</summary>
public sealed record CdAudioTocEntry(
    int TrackNumber,
    int StartSector,
    byte Control)
{
    public bool IsLeadOut => TrackNumber == 0xAA;
    public bool IsData => (Control & 0x04) != 0;
}

/// <summary>Low-level table of contents, including the 0xAA lead-out entry.</summary>
public sealed class CdAudioTableOfContents
{
    public CdAudioTableOfContents(
        int firstTrackNumber,
        int lastTrackNumber,
        IEnumerable<CdAudioTocEntry> entries)
    {
        FirstTrackNumber = firstTrackNumber;
        LastTrackNumber = lastTrackNumber;
        Entries = entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries));
    }

    public int FirstTrackNumber { get; }
    public int LastTrackNumber { get; }
    public IReadOnlyList<CdAudioTocEntry> Entries { get; }
}

/// <summary>
/// Open optical-drive handle. Implement this interface in tests to supply a
/// synthetic TOC and deterministic raw sectors.
/// </summary>
public interface ICdAudioDevice : IDisposable
{
    CdAudioTableOfContents ReadTableOfContents();

    /// <summary>
    /// Reads <paramref name="sectorCount"/> CD-DA sectors starting at an absolute
    /// logical sector. Returns the number of bytes placed in destination.
    /// </summary>
    int ReadAudioSectors(int startSector, int sectorCount, byte[] destination);
}

/// <summary>Platform boundary used by <see cref="CdAudioService"/>.</summary>
public interface ICdAudioPlatform
{
    IReadOnlyList<CdAudioDeviceIdentity> EnumerateOpticalDrives();
    ICdAudioDevice OpenDevice(string devicePath);
}

public interface ICdAudioService
{
    Task<IReadOnlyList<CdAudioDrive>> EnumerateDrivesAsync(
        CancellationToken cancellationToken = default);

    Task<CdAudioDisc> ReadDiscAsync(
        string devicePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the requested audio tracks. A null track list selects every audio
    /// track; output order follows TOC order.
    /// </summary>
    Task<IReadOnlyList<CdAudioTrackImport>> ExtractTracksAsync(
        string devicePath,
        IEnumerable<int>? trackNumbers = null,
        IProgress<CdAudioExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
