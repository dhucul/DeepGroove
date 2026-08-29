using System.IO;

namespace WaveLab.Audio;

/// <summary>Physical constants for Red Book CD digital audio.</summary>
public static class CdAudioFormat
{
    public const int SampleRate = 44_100;
    public const int ChannelCount = 2;
    public const int BitsPerSample = 16;
    public const int SectorsPerSecond = 75;
    public const int FramesPerSector = 588;
    public const int BytesPerSector = 2_352;
}

/// <summary>A drive reported by the operating-system optical-drive catalog.</summary>
public sealed record CdAudioDeviceIdentity(
    string RootPath,
    string DevicePath,
    string DisplayName);

public enum CdTrackKind
{
    Audio,
    Data,
}

/// <summary>
/// One table-of-contents track. EndSector is exclusive and addresses a raw CD
/// sector (75 sectors per second).
/// </summary>
public sealed record CdAudioTrack(
    int Number,
    int StartSector,
    int EndSector,
    CdTrackKind Kind,
    byte Control)
{
    public int SectorCount => EndSector - StartSector;
    public long FrameCount => (long)SectorCount * CdAudioFormat.FramesPerSector;
    public TimeSpan Duration => TimeSpan.FromSeconds(SectorCount / (double)CdAudioFormat.SectorsPerSecond);
    public bool IsAudio => Kind == CdTrackKind.Audio;
    public bool PreEmphasis => IsAudio && (Control & 0x01) != 0;
}

/// <summary>An immutable snapshot of an audio disc's table of contents.</summary>
public sealed class CdAudioDisc
{
    public CdAudioDisc(CdAudioDeviceIdentity device, IEnumerable<CdAudioTrack> tracks)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Tracks = tracks?.OrderBy(track => track.Number).ToArray()
            ?? throw new ArgumentNullException(nameof(tracks));
        AudioTracks = Tracks.Where(track => track.IsAudio).ToArray();
    }

    public CdAudioDeviceIdentity Device { get; }
    public IReadOnlyList<CdAudioTrack> Tracks { get; }
    public IReadOnlyList<CdAudioTrack> AudioTracks { get; }
    public TimeSpan AudioDuration => TimeSpan.FromSeconds(
        AudioTracks.Sum(track => (long)track.SectorCount) /
        (double)CdAudioFormat.SectorsPerSecond);
}

public enum CdAudioDriveStatus
{
    Ready,
    NoMedia,
    NoAudioTracks,
    Unsupported,
    AccessDenied,
    Error,
}

/// <summary>Optical-drive discovery result, including a usable TOC when available.</summary>
public sealed record CdAudioDrive(
    CdAudioDeviceIdentity Device,
    CdAudioDriveStatus Status,
    CdAudioDisc? Disc,
    string? ErrorMessage);

/// <summary>
/// Progress for a multi-track extraction operation. TrackIndex is one-based.
/// </summary>
public sealed record CdAudioExtractionProgress(
    int TrackNumber,
    int TrackIndex,
    int TrackCount,
    long TrackSectorsRead,
    long TrackSectorCount,
    long TotalSectorsRead,
    long TotalSectorCount)
{
    public double TrackFraction => TrackSectorCount == 0
        ? 1d
        : Math.Clamp(TrackSectorsRead / (double)TrackSectorCount, 0d, 1d);

    public double TotalFraction => TotalSectorCount == 0
        ? 1d
        : Math.Clamp(TotalSectorsRead / (double)TotalSectorCount, 0d, 1d);
}

/// <summary>
/// An imported track and its source provenance. The document contains stereo 44.1 kHz samples
/// decoded from verified 16-bit CD-DA sectors, optionally with the flagged playback de-emphasis.
/// </summary>
public sealed record CdAudioTrackImport(
    CdAudioDeviceIdentity SourceDevice,
    CdAudioTrack SourceTrack,
    AudioDocument Document,
    bool DeEmphasisApplied = false);

public enum CdAudioFailureReason
{
    NoOpticalDrive,
    NoMedia,
    NoAudioTracks,
    UnsupportedDrive,
    AccessDenied,
    TrackNotFound,
    TrackIsData,
    DiscChanged,
    InvalidTableOfContents,
    AudioTooLarge,
    ReadFailed,
}

/// <summary>An actionable error raised while inspecting or extracting a CD.</summary>
public sealed class CdAudioException : IOException
{
    public CdAudioException(
        CdAudioFailureReason reason,
        string message,
        string? devicePath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        DevicePath = devicePath;
    }

    public CdAudioFailureReason Reason { get; }
    public string? DevicePath { get; }
}
