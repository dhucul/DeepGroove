using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WaveLab.Audio;

/// <summary>Windows implementation using IOCTL_CDROM_READ_TOC_EX and IOCTL_CDROM_RAW_READ.</summary>
public sealed class WindowsCdAudioPlatform : ICdAudioPlatform
{
    public IReadOnlyList<CdAudioDeviceIdentity> EnumerateOpticalDrives()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("CD audio extraction requires Windows.");

        var result = new List<CdAudioDeviceIdentity>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.CDRom)
                continue;

            string root = drive.Name;
            string devicePath = ToDevicePath(root);
            string displayName = root.TrimEnd('\\');
            try
            {
                if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                    displayName = $"{displayName} ({drive.VolumeLabel})";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An empty/ejected drive (or a protected volume label) is still a
                // valid discovery result and can be opened through its device path.
            }

            result.Add(new CdAudioDeviceIdentity(root, devicePath, displayName));
        }

        return result;
    }

    public ICdAudioDevice OpenDevice(string devicePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("CD audio extraction requires Windows.");
        return new WindowsCdAudioDevice(ToDevicePath(devicePath));
    }

    internal static string ToDevicePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string value = path.Trim();
        if (value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        value = value.TrimEnd('\\');
        if (value.Length == 1 && char.IsAsciiLetter(value[0]))
            value += ":";

        if (value.Length != 2 || !char.IsAsciiLetter(value[0]) || value[1] != ':')
        {
            throw new ArgumentException(
                "An optical drive letter such as D:, D:\\, or \\\\.\\D: is required.",
                nameof(path));
        }

        return @"\\.\" + char.ToUpperInvariant(value[0]) + ":";
    }
}

internal sealed class WindowsCdAudioDevice : ICdAudioDevice
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlCdromReadTocEx = 0x00024054;
    private const uint IoctlCdromRawRead = 0x0002403E;
    private const int CookedSectorBytes = 2_048;
    private const int MaximumTocBytes = 4 + (100 * 8);

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public WindowsCdAudioDevice(string devicePath)
    {
        DevicePath = devicePath;
        _handle = NativeMethods.CreateFile(
            devicePath,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, $"Could not open optical drive {devicePath}.");
        }
    }

    public string DevicePath { get; }

    public CdAudioTableOfContents ReadTableOfContents()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = new byte[MaximumTocBytes];
        var request = new ReadTocInfo
        {
            // Format 0 (standard TOC), bit 7 set to request minute/second/frame
            // addresses. The remaining bytes select all sessions/tracks.
            FormatAndMsf = 0x80,
        };
        if (!NativeMethods.DeviceIoControl(
                _handle,
                IoctlCdromReadTocEx,
                ref request,
                Marshal.SizeOf<ReadTocInfo>(),
                buffer,
                buffer.Length,
                out int bytesReturned,
                IntPtr.Zero))
        {
            throw LastDeviceError("read the CD table of contents");
        }

        return ParseTableOfContents(buffer, bytesReturned);
    }

    public int ReadAudioSectors(int startSector, int sectorCount, byte[] destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (startSector < 0)
            throw new ArgumentOutOfRangeException(nameof(startSector));
        if (sectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorCount));

        int requiredBytes = checked(sectorCount * CdAudioFormat.BytesPerSector);
        if (destination.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Destination must hold at least {requiredBytes:N0} bytes.",
                nameof(destination));
        }

        var read = new RawReadInfo
        {
            DiskOffset = checked((long)startSector * CookedSectorBytes),
            SectorCount = (uint)sectorCount,
            TrackMode = TrackModeType.CdDa,
        };

        if (!NativeMethods.DeviceIoControl(
                _handle,
                IoctlCdromRawRead,
                ref read,
                Marshal.SizeOf<RawReadInfo>(),
                destination,
                requiredBytes,
                out int bytesReturned,
                IntPtr.Zero))
        {
            throw LastDeviceError($"read audio sector {startSector:N0}");
        }

        return bytesReturned;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle.Dispose();
    }

    private Win32Exception LastDeviceError(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"Could not {operation} from {DevicePath}.");
    }

    private static CdAudioTableOfContents ParseTableOfContents(byte[] buffer, int bytesReturned)
    {
        if (bytesReturned < 4)
            throw InvalidToc("The drive returned a truncated CD table of contents.");

        int tocLength = buffer[0] << 8 | buffer[1];
        if (tocLength < 2)
            throw InvalidToc("The drive returned an invalid CD table-of-contents length.");

        int meaningfulBytes = tocLength + 2;
        if (meaningfulBytes > bytesReturned || meaningfulBytes > buffer.Length)
            throw InvalidToc("The drive returned a truncated CD table of contents.");
        if ((meaningfulBytes - 4) % 8 != 0)
            throw InvalidToc("The drive returned a misaligned CD table of contents.");

        int firstTrack = buffer[2];
        int lastTrack = buffer[3];
        int descriptorCount = (meaningfulBytes - 4) / 8;
        if (descriptorCount <= 0)
            throw InvalidToc("The drive returned an empty CD table of contents.");

        var entries = new List<CdAudioTocEntry>(descriptorCount);
        for (int index = 0; index < descriptorCount; index++)
        {
            int offset = 4 + index * 8;
            byte control = (byte)(buffer[offset + 1] & 0x0F);
            int trackNumber = buffer[offset + 2];
            int minute = buffer[offset + 5];
            int second = buffer[offset + 6];
            int frame = buffer[offset + 7];

            if (second >= 60 || frame >= CdAudioFormat.SectorsPerSecond)
                throw InvalidToc("The drive returned an invalid MSF address in the CD table of contents.");

            int absoluteFrame = checked((minute * 60 + second) * CdAudioFormat.SectorsPerSecond + frame);
            int logicalSector = absoluteFrame - (2 * CdAudioFormat.SectorsPerSecond);
            entries.Add(new CdAudioTocEntry(trackNumber, logicalSector, control));
        }

        return new CdAudioTableOfContents(firstTrack, lastTrack, entries);
    }

    private static InvalidDataException InvalidToc(string message) => new(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadTocInfo
    {
        public byte FormatAndMsf;
        public byte SessionTrack;
        public byte Reserved2;
        public byte Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawReadInfo
    {
        public long DiskOffset;
        public uint SectorCount;
        public TrackModeType TrackMode;
    }

    private enum TrackModeType : uint
    {
        CdDa = 2,
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            ref ReadTocInfo inputBuffer,
            int inputBufferSize,
            [Out] byte[] outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            ref RawReadInfo inputBuffer,
            int inputBufferSize,
            [Out] byte[] outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
