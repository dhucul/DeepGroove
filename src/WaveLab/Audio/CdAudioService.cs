using System.ComponentModel;
using System.IO;

namespace WaveLab.Audio;

/// <summary>
/// Disc discovery and lossless CD-DA extraction. Blocking device calls run on a
/// worker thread; cancellation is observed between small raw-read batches.
/// </summary>
public sealed class CdAudioService : ICdAudioService
{
    private const int SectorsPerRead = 16;
    private readonly ICdAudioPlatform _platform;

    public CdAudioService()
        : this(new WindowsCdAudioPlatform())
    {
    }

    public CdAudioService(ICdAudioPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public Task<IReadOnlyList<CdAudioDrive>> EnumerateDrivesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<CdAudioDrive>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CdAudioDeviceIdentity> devices;
            try
            {
                devices = _platform.EnumerateOpticalDrives();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw MapFailure(ex, "enumerate optical drives", null);
            }

            var results = new List<CdAudioDrive>(devices.Count);
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var reader = _platform.OpenDevice(device.DevicePath);
                    var disc = CreateDisc(device, reader.ReadTableOfContents(cancellationToken));
                    results.Add(disc.AudioTracks.Count == 0
                        ? new CdAudioDrive(
                            device,
                            CdAudioDriveStatus.NoAudioTracks,
                            disc,
                            "The disc contains no CD audio tracks.")
                        : new CdAudioDrive(device, CdAudioDriveStatus.Ready, disc, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failure = MapFailure(ex, "read the disc table of contents", device.DevicePath);
                    results.Add(new CdAudioDrive(
                        device,
                        ToDriveStatus(failure.Reason),
                        null,
                        failure.Message));
                }
            }

            return results;
        }, cancellationToken);
    }

    public Task<CdAudioDisc> ReadDiscAsync(
        string devicePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = ResolveIdentity(devicePath);
            try
            {
                using var reader = _platform.OpenDevice(devicePath);
                var disc = CreateDisc(identity, reader.ReadTableOfContents(cancellationToken));
                EnsureAudioDisc(disc);
                return disc;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw MapFailure(ex, "read the disc table of contents", devicePath);
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CdAudioTrackImport>> ExtractTracksAsync(
        string devicePath,
        IEnumerable<int>? trackNumbers = null,
        IProgress<CdAudioExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        var requestedNumbers = trackNumbers?.Distinct().ToArray();
        if (requestedNumbers is { Length: 0 })
            throw new ArgumentException("Select at least one track to extract.", nameof(trackNumbers));

        return Task.Run<IReadOnlyList<CdAudioTrackImport>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = ResolveIdentity(devicePath);

            try
            {
                using var reader = _platform.OpenDevice(devicePath);
                var initialToc = reader.ReadTableOfContents(cancellationToken);
                var disc = CreateDisc(identity, initialToc);
                EnsureAudioDisc(disc);
                var tracks = SelectTracks(disc, requestedNumbers, devicePath);
                long totalSectors = tracks.Sum(track => (long)track.SectorCount);
                long completedSectors = 0;
                var imports = new List<CdAudioTrackImport>(tracks.Count);

                for (int index = 0; index < tracks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index > 0)
                        EnsureDiscUnchanged(initialToc, reader.ReadTableOfContents(cancellationToken), devicePath);

                    var track = tracks[index];
                    progress?.Report(new CdAudioExtractionProgress(
                        track.Number,
                        index + 1,
                        tracks.Count,
                        0,
                        track.SectorCount,
                        completedSectors,
                        totalSectors));

                    var document = ExtractTrack(
                        reader,
                        track,
                        index + 1,
                        tracks.Count,
                        completedSectors,
                        totalSectors,
                        progress,
                        cancellationToken,
                        devicePath);

                    imports.Add(new CdAudioTrackImport(identity, track, document));
                    completedSectors += track.SectorCount;
                }

                // A final TOC verification also protects a single-track extraction:
                // without it, a disc swapped during the only (or last) track could
                // otherwise be returned as a seemingly valid import.
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDiscUnchanged(initialToc, reader.ReadTableOfContents(cancellationToken), devicePath);

                return imports;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw MapFailure(ex, "extract CD audio", devicePath);
            }
        }, cancellationToken);
    }

    private static AudioDocument ExtractTrack(
        ICdAudioDevice reader,
        CdAudioTrack track,
        int trackIndex,
        int trackCount,
        long completedBeforeTrack,
        long totalSectors,
        IProgress<CdAudioExtractionProgress>? progress,
        CancellationToken cancellationToken,
        string devicePath)
    {
        long frameCount64 = track.FrameCount;
        if (frameCount64 > int.MaxValue)
        {
            throw new CdAudioException(
                CdAudioFailureReason.AudioTooLarge,
                $"Track {track.Number:00} is too long to fit in one audio document.",
                devicePath);
        }

        int frameCount = (int)frameCount64;
        float[][] channels;
        try
        {
            channels = [new float[frameCount], new float[frameCount]];
        }
        catch (OutOfMemoryException ex)
        {
            throw new CdAudioException(
                CdAudioFailureReason.AudioTooLarge,
                $"There is not enough memory to import track {track.Number:00}.",
                devicePath,
                ex);
        }

        var raw = new byte[SectorsPerRead * CdAudioFormat.BytesPerSector];
        int sectorsRead = 0;
        int destinationFrame = 0;

        while (sectorsRead < track.SectorCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batchSectors = Math.Min(SectorsPerRead, track.SectorCount - sectorsRead);
            int expectedBytes = checked(batchSectors * CdAudioFormat.BytesPerSector);
            int startSector = checked(track.StartSector + sectorsRead);
            int bytesRead = ReadAudioSectorsWithRetry(
                reader,
                startSector,
                batchSectors,
                raw,
                cancellationToken);

            if (bytesRead != expectedBytes)
            {
                throw new CdAudioException(
                    CdAudioFailureReason.ReadFailed,
                    $"The drive returned {bytesRead:N0} bytes for track {track.Number:00}; " +
                    $"{expectedBytes:N0} bytes were expected.",
                    devicePath);
            }

            int framesRead = batchSectors * CdAudioFormat.FramesPerSector;
            DecodeCdDa(raw, framesRead, channels, destinationFrame);
            destinationFrame += framesRead;
            sectorsRead += batchSectors;

            progress?.Report(new CdAudioExtractionProgress(
                track.Number,
                trackIndex,
                trackCount,
                sectorsRead,
                track.SectorCount,
                completedBeforeTrack + sectorsRead,
                totalSectors));
        }

        var document = new AudioDocument(channels, CdAudioFormat.SampleRate, CdAudioFormat.BitsPerSample)
        {
            FilePath = null,
            Title = $"Audio CD - Track {track.Number:00}",
        };
        document.MarkUnsaved();
        return document;
    }

    private static int ReadAudioSectorsWithRetry(
        ICdAudioDevice reader,
        int startSector,
        int sectorCount,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return reader.ReadAudioSectors(startSector, sectorCount, destination, cancellationToken);
            }
            catch (Win32Exception error) when (
                attempt < maximumAttempts && IsTransientReadError(error.NativeErrorCode))
            {
                // A drive's own CIRC correction can produce a clean result on a
                // second physical read after a seek/CRC/timeout failure.
                Thread.Yield();
            }
        }
    }

    private static bool IsTransientReadError(int nativeErrorCode) => nativeErrorCode is
        23 or   // ERROR_CRC
        31 or   // ERROR_GEN_FAILURE
        121 or  // ERROR_SEM_TIMEOUT
        170 or  // ERROR_BUSY
        1117;   // ERROR_IO_DEVICE

    private static void DecodeCdDa(byte[] source, int frameCount, float[][] destination, int destinationFrame)
    {
        int sourceOffset = 0;
        for (int frame = 0; frame < frameCount; frame++)
        {
            short left = (short)(source[sourceOffset] | source[sourceOffset + 1] << 8);
            short right = (short)(source[sourceOffset + 2] | source[sourceOffset + 3] << 8);
            destination[0][destinationFrame + frame] = left / 32768f;
            destination[1][destinationFrame + frame] = right / 32768f;
            sourceOffset += 4;
        }
    }

    private static IReadOnlyList<CdAudioTrack> SelectTracks(
        CdAudioDisc disc,
        IReadOnlyCollection<int>? requestedNumbers,
        string devicePath)
    {
        if (requestedNumbers == null)
            return disc.AudioTracks;

        var requested = requestedNumbers.ToHashSet();
        foreach (int number in requested)
        {
            var track = disc.Tracks.FirstOrDefault(candidate => candidate.Number == number);
            if (track == null)
            {
                throw new CdAudioException(
                    CdAudioFailureReason.TrackNotFound,
                    $"Track {number:00} is not present on this disc.",
                    devicePath);
            }

            if (!track.IsAudio)
            {
                throw new CdAudioException(
                    CdAudioFailureReason.TrackIsData,
                    $"Track {number:00} is a data track and cannot be imported as audio.",
                    devicePath);
            }
        }

        return disc.Tracks.Where(track => requested.Contains(track.Number)).ToArray();
    }

    private static CdAudioDisc CreateDisc(
        CdAudioDeviceIdentity device,
        CdAudioTableOfContents toc)
    {
        if (toc.FirstTrackNumber is < 1 or > 99 ||
            toc.LastTrackNumber < toc.FirstTrackNumber ||
            toc.LastTrackNumber > 99)
        {
            throw InvalidToc(device.DevicePath, "The drive returned an invalid first/last track range.");
        }

        var descriptors = new Dictionary<int, CdAudioTocEntry>();
        foreach (var descriptor in toc.Entries)
        {
            if (!descriptors.TryAdd(descriptor.TrackNumber, descriptor))
            {
                throw InvalidToc(
                    device.DevicePath,
                    $"The disc table of contents repeats entry 0x{descriptor.TrackNumber:X2}.");
            }
        }
        if (!descriptors.TryGetValue(0xAA, out var leadOut))
            throw InvalidToc(device.DevicePath, "The disc table of contents has no lead-out entry.");

        var tracks = new List<CdAudioTrack>(toc.LastTrackNumber - toc.FirstTrackNumber + 1);
        for (int number = toc.FirstTrackNumber; number <= toc.LastTrackNumber; number++)
        {
            if (!descriptors.TryGetValue(number, out var current))
                throw InvalidToc(device.DevicePath, $"The disc table of contents is missing track {number:00}.");

            CdAudioTocEntry next;
            if (number == toc.LastTrackNumber)
            {
                next = leadOut;
            }
            else if (!descriptors.TryGetValue(number + 1, out next!))
            {
                throw InvalidToc(device.DevicePath, $"The disc table of contents is missing track {number + 1:00}.");
            }

            if (current.StartSector < 0 || next.StartSector <= current.StartSector)
                throw InvalidToc(device.DevicePath, $"Track {number:00} has an invalid sector range.");

            tracks.Add(new CdAudioTrack(
                number,
                current.StartSector,
                next.StartSector,
                current.IsData ? CdTrackKind.Data : CdTrackKind.Audio,
                current.Control));
        }

        return new CdAudioDisc(device, tracks);
    }

    private static void EnsureAudioDisc(CdAudioDisc disc)
    {
        if (disc.AudioTracks.Count == 0)
        {
            throw new CdAudioException(
                CdAudioFailureReason.NoAudioTracks,
                "The inserted disc contains no CD audio tracks.",
                disc.Device.DevicePath);
        }
    }

    private static void EnsureDiscUnchanged(
        CdAudioTableOfContents original,
        CdAudioTableOfContents current,
        string devicePath)
    {
        bool same = original.FirstTrackNumber == current.FirstTrackNumber &&
                    original.LastTrackNumber == current.LastTrackNumber &&
                    original.Entries.SequenceEqual(current.Entries);
        if (!same)
        {
            throw new CdAudioException(
                CdAudioFailureReason.DiscChanged,
                "The disc changed during extraction. No partial import was returned.",
                devicePath);
        }
    }

    private CdAudioDeviceIdentity ResolveIdentity(string devicePath)
    {
        try
        {
            return _platform.EnumerateOpticalDrives().FirstOrDefault(device =>
                       string.Equals(device.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(device.RootPath.TrimEnd('\\'), devicePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                   ?? new CdAudioDeviceIdentity(devicePath, devicePath, devicePath);
        }
        catch
        {
            // Opening by an explicit device path remains useful even when drive
            // enumeration is unavailable (and keeps injected test platforms small).
            return new CdAudioDeviceIdentity(devicePath, devicePath, devicePath);
        }
    }

    private static CdAudioException InvalidToc(string devicePath, string message) =>
        new(CdAudioFailureReason.InvalidTableOfContents, message, devicePath);

    private static CdAudioDriveStatus ToDriveStatus(CdAudioFailureReason reason) => reason switch
    {
        CdAudioFailureReason.NoMedia => CdAudioDriveStatus.NoMedia,
        CdAudioFailureReason.NoAudioTracks => CdAudioDriveStatus.NoAudioTracks,
        CdAudioFailureReason.UnsupportedDrive => CdAudioDriveStatus.Unsupported,
        CdAudioFailureReason.AccessDenied => CdAudioDriveStatus.AccessDenied,
        _ => CdAudioDriveStatus.Error,
    };

    private static CdAudioException MapFailure(Exception exception, string operation, string? devicePath)
    {
        if (exception is CdAudioException cdError)
            return cdError;

        if (exception is PlatformNotSupportedException)
        {
            return new CdAudioException(
                CdAudioFailureReason.UnsupportedDrive,
                "CD audio extraction is available only on Windows.",
                devicePath,
                exception);
        }

        if (exception is UnauthorizedAccessException)
        {
            return new CdAudioException(
                CdAudioFailureReason.AccessDenied,
                $"Windows denied access while attempting to {operation}.",
                devicePath,
                exception);
        }

        if (exception is InvalidDataException)
        {
            return new CdAudioException(
                CdAudioFailureReason.InvalidTableOfContents,
                $"The drive returned an invalid disc table of contents: {exception.Message}",
                devicePath,
                exception);
        }

        if (exception is Win32Exception win32)
        {
            CdAudioFailureReason reason = win32.NativeErrorCode switch
            {
                5 => CdAudioFailureReason.AccessDenied,             // ERROR_ACCESS_DENIED
                21 or 1112 or 1167 => CdAudioFailureReason.NoMedia, // NOT_READY / NO_MEDIA / DEVICE_NOT_CONNECTED
                1110 => CdAudioFailureReason.DiscChanged,           // ERROR_MEDIA_CHANGED
                1 or 50 or 87 or 120 => CdAudioFailureReason.UnsupportedDrive,
                _ => CdAudioFailureReason.ReadFailed,
            };

            string message = reason switch
            {
                CdAudioFailureReason.NoMedia => "The drive is empty or the inserted disc is not ready.",
                CdAudioFailureReason.DiscChanged => "The disc changed while it was being read.",
                CdAudioFailureReason.UnsupportedDrive => "The selected drive does not support digital CD audio extraction.",
                CdAudioFailureReason.AccessDenied => "Windows denied access to the selected optical drive.",
                _ => $"Windows could not {operation}: {win32.Message}",
            };
            return new CdAudioException(reason, message, devicePath, win32);
        }

        return new CdAudioException(
            CdAudioFailureReason.ReadFailed,
            $"Could not {operation}: {exception.Message}",
            devicePath,
            exception);
    }
}
