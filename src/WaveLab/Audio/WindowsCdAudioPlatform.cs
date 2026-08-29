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
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint IoctlCdromReadTocEx = 0x00024054;
    private const uint IoctlCdromRawRead = 0x0002403E;
    private const int ErrorIoPending = 997;
    private const int ErrorOperationAborted = 995;
    private const int CookedSectorBytes = 2_048;
    private const int MaximumTocBytes = 8_192;
    private static readonly TimeSpan DeviceIoTimeout = TimeSpan.FromSeconds(15);

    private readonly SafeFileHandle _handle;
    private readonly object _scratchSync = new();
    private DeviceScratch? _scratch;
    private bool _scratchClosed;
    private int _disposed;

    public WindowsCdAudioDevice(string devicePath)
    {
        DevicePath = devicePath;
        _handle = NativeMethods.CreateFile(
            devicePath,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, $"Could not open optical drive {devicePath}.");
        }
    }

    public string DevicePath { get; }

    public CdAudioTableOfContents ReadTableOfContents() =>
        ReadTableOfContents(CancellationToken.None);

    public CdAudioTableOfContents ReadTableOfContents(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var buffer = new byte[MaximumTocBytes];
        var request = new ReadTocInfo
        {
            // Format 0 (standard TOC), bit 7 set to request minute/second/frame
            // addresses. The remaining bytes select all sessions/tracks.
            FormatAndMsf = 0x80,
        };
        int bytesReturned = InvokeIoControl(
            IoctlCdromReadTocEx,
            request,
            buffer,
            buffer.Length,
            "read the CD table of contents",
            cancellationToken);

        CdAudioTableOfContents standard = ParseTableOfContents(buffer, bytesReturned);

        // The standard TOC has track starts but no session boundaries. Ask for the full TOC as a
        // second, optional capability so an audio track followed by data can be ended at session
        // 1's real lead-out rather than by subtracting a fixed 152 seconds. Older drives that do
        // not implement format 2 retain the standard TOC; preserving the whole stated track is
        // safer than silently removing legal single-session audio.
        try
        {
            Array.Clear(buffer);
            request.FormatAndMsf = 0x82; // format 2 (full TOC), MSF addresses
            bytesReturned = InvokeIoControl(
                IoctlCdromReadTocEx,
                request,
                buffer,
                buffer.Length,
                "read the CD session table of contents",
                cancellationToken);
            IReadOnlyList<CdAudioSession> sessions = ParseFullTableOfContents(buffer, bytesReturned);
            return new CdAudioTableOfContents(
                standard.FirstTrackNumber, standard.LastTrackNumber, standard.Entries, sessions);
        }
        catch (Exception error) when (error is Win32Exception or InvalidDataException)
        {
            return standard;
        }
    }

    public int ReadAudioSectors(int startSector, int sectorCount, byte[] destination) =>
        ReadAudioSectors(startSector, sectorCount, destination, CancellationToken.None);

    public int ReadAudioSectors(
        int startSector,
        int sectorCount,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
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

        return InvokeIoControl(
            IoctlCdromRawRead,
            read,
            destination,
            requiredBytes,
            $"read audio sector {startSector:N0}",
            cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _handle.Dispose();

        // Only an idle scratch is ever parked here; one that is still rented is
        // released by the call that owns it, which observes _scratchClosed.
        DeviceScratch? scratch;
        lock (_scratchSync)
        {
            _scratchClosed = true;
            scratch = _scratch;
            _scratch = null;
        }
        scratch?.Dispose();
    }

    private int InvokeIoControl<TInput>(
        uint controlCode,
        TInput input,
        byte[] destination,
        int destinationLength,
        string operation,
        CancellationToken cancellationToken)
        where TInput : struct
    {
        cancellationToken.ThrowIfCancellationRequested();
        // CancellationToken.WaitHandle may throw when its source has already
        // been disposed. Resolve it before starting kernel I/O so that failure
        // cannot strand an OVERLAPPED request whose buffers are then freed.
        WaitHandle? cancellationWaitHandle = cancellationToken.CanBeCanceled
            ? cancellationToken.WaitHandle
            : null;

        IntPtr inputBuffer = IntPtr.Zero;
        IntPtr outputBuffer = IntPtr.Zero;
        IntPtr overlappedBuffer = IntPtr.Zero;
        EventWaitHandle? completionEvent = null;
        bool handleReferenceAdded = false;
        bool cleanupTransferred = false;
        // Non-null only while this call still owns the reusable per-device buffers
        // and event. Every path that can leave a request pending in the kernel
        // clears it first: memory the OS may still write into is never recycled.
        DeviceScratch? scratch = null;

        try
        {
            int inputSize = Marshal.SizeOf<TInput>();
            int overlappedSize = Marshal.SizeOf<IoOverlapped>();
            scratch = RentScratch(inputSize, destinationLength, overlappedSize);
            if (scratch != null)
            {
                inputBuffer = scratch.Input;
                outputBuffer = scratch.Output;
                overlappedBuffer = scratch.Overlapped;
                completionEvent = scratch.CompletionEvent;
                // The event stays signaled after the previous request completed.
                completionEvent.Reset();
            }
            else
            {
                inputBuffer = Marshal.AllocHGlobal(inputSize);
                outputBuffer = Marshal.AllocHGlobal(destinationLength);
                overlappedBuffer = Marshal.AllocHGlobal(overlappedSize);
                completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            }

            // Both structures are rewritten in full, so a reused block carries no
            // state from the previous request. The kernel is told the exact sizes
            // of this request, never the (possibly larger) buffer capacities.
            Marshal.StructureToPtr(input, inputBuffer, false);
            var overlapped = new IoOverlapped
            {
                EventHandle = completionEvent.SafeWaitHandle.DangerousGetHandle(),
            };
            Marshal.StructureToPtr(overlapped, overlappedBuffer, false);

            _handle.DangerousAddRef(ref handleReferenceAdded);
            IntPtr rawHandle = _handle.DangerousGetHandle();
            bool completed = NativeMethods.DeviceIoControl(
                rawHandle,
                controlCode,
                inputBuffer,
                inputSize,
                outputBuffer,
                destinationLength,
                out int bytesReturned,
                overlappedBuffer);

            if (!completed)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                    throw DeviceError(operation, error);

                int signaled;
                try
                {
                    signaled = cancellationWaitHandle != null
                        ? WaitHandle.WaitAny(
                            [completionEvent, cancellationWaitHandle],
                            DeviceIoTimeout)
                        : completionEvent.WaitOne(DeviceIoTimeout) ? 0 : WaitHandle.WaitTimeout;
                }
                catch
                {
                    // A cancellation source can be disposed concurrently with
                    // WaitAny. From this point onward the kernel owns the native
                    // buffers, so either transfer them or synchronously observe
                    // the cancelled request complete before the outer finally.
                    _ = NativeMethods.CancelIoEx(rawHandle, overlappedBuffer);
                    scratch?.Detach();
                    scratch = null;
                    if (RegisterPendingCleanupOrWait(
                            _handle,
                            rawHandle,
                            inputBuffer,
                            outputBuffer,
                            overlappedBuffer,
                            completionEvent))
                    {
                        cleanupTransferred = true;
                        handleReferenceAdded = false;
                        inputBuffer = IntPtr.Zero;
                        outputBuffer = IntPtr.Zero;
                        overlappedBuffer = IntPtr.Zero;
                        completionEvent = null;
                    }
                    throw;
                }

                if (signaled == 1 || signaled == WaitHandle.WaitTimeout)
                {
                    // Cancellation is asynchronous. The OS still owns all three
                    // native buffers until the OVERLAPPED request completes, so
                    // transfer their lifetime to a waiter before returning.
                    _ = NativeMethods.CancelIoEx(rawHandle, overlappedBuffer);
                    scratch?.Detach();
                    scratch = null;
                    if (RegisterPendingCleanupOrWait(
                            _handle,
                            rawHandle,
                            inputBuffer,
                            outputBuffer,
                            overlappedBuffer,
                            completionEvent))
                    {
                        cleanupTransferred = true;
                        handleReferenceAdded = false;
                        inputBuffer = IntPtr.Zero;
                        outputBuffer = IntPtr.Zero;
                        overlappedBuffer = IntPtr.Zero;
                        completionEvent = null;
                    }

                    if (signaled == 1)
                        throw new OperationCanceledException(cancellationToken);
                    throw new TimeoutException(
                        $"Timed out after {DeviceIoTimeout.TotalSeconds:N0} seconds while trying to {operation} from {DevicePath}.");
                }

                if (!NativeMethods.GetOverlappedResult(
                        rawHandle,
                        overlappedBuffer,
                        out bytesReturned,
                        false))
                {
                    int completionError = Marshal.GetLastWin32Error();
                    if (completionError == ErrorOperationAborted && cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    throw DeviceError(operation, completionError);
                }
            }

            if ((uint)bytesReturned > (uint)destinationLength)
                throw new IOException($"The optical drive returned an invalid byte count while trying to {operation}.");

            if (bytesReturned > 0)
                Marshal.Copy(outputBuffer, destination, 0, bytesReturned);
            return bytesReturned;
        }
        finally
        {
            if (!cleanupTransferred)
            {
                if (scratch != null)
                {
                    // Reaching here with the scratch still held means no request
                    // was left pending: the buffers and the event are idle and go
                    // back to the device for the next read.
                    ReturnScratch(scratch);
                }
                else
                {
                    if (overlappedBuffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(overlappedBuffer);
                    if (outputBuffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(outputBuffer);
                    if (inputBuffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(inputBuffer);
                    completionEvent?.Dispose();
                }

                if (handleReferenceAdded)
                    _handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Takes the per-device scratch, allocating it on first use and growing it to
    /// the largest request seen, so a full rip does not create a kernel event and
    /// three unmanaged blocks per 16-sector read. Returns null once the device has
    /// been disposed; the caller then allocates a private set as before.
    /// </summary>
    private DeviceScratch? RentScratch(int inputSize, int outputSize, int overlappedSize)
    {
        DeviceScratch? scratch;
        lock (_scratchSync)
        {
            if (_scratchClosed)
                return null;
            scratch = _scratch;
            _scratch = null;
        }

        scratch ??= new DeviceScratch();
        try
        {
            scratch.EnsureCapacity(inputSize, outputSize, overlappedSize);
        }
        catch
        {
            scratch.Dispose();
            throw;
        }

        return scratch;
    }

    /// <summary>
    /// Parks an idle scratch for the next request. A scratch that cannot be parked
    /// (the device was disposed, or a concurrent call already parked one) is freed
    /// here, so exactly one owner releases it.
    /// </summary>
    private void ReturnScratch(DeviceScratch scratch)
    {
        lock (_scratchSync)
        {
            if (!_scratchClosed && _scratch == null)
            {
                _scratch = scratch;
                return;
            }
        }

        scratch.Dispose();
    }

    /// <summary>
    /// One request's worth of native state, reused across requests on the same
    /// handle: the input structure, the output buffer and the OVERLAPPED block,
    /// plus the manual-reset event the request completes on.
    /// </summary>
    private sealed class DeviceScratch : IDisposable
    {
        private int _inputCapacity;
        private int _outputCapacity;
        private int _overlappedCapacity;
        private bool _detached;

        public DeviceScratch()
        {
            CompletionEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        }

        public IntPtr Input { get; private set; }
        public IntPtr Output { get; private set; }
        public IntPtr Overlapped { get; private set; }
        public EventWaitHandle CompletionEvent { get; }

        public void EnsureCapacity(int inputSize, int outputSize, int overlappedSize)
        {
            Input = Grow(Input, ref _inputCapacity, inputSize);
            Output = Grow(Output, ref _outputCapacity, outputSize);
            Overlapped = Grow(Overlapped, ref _overlappedCapacity, overlappedSize);
        }

        /// <summary>
        /// Gives up every native resource to a caller that has taken over its
        /// lifetime, because a cancelled request may still write into it. The
        /// instance owns nothing afterwards and must simply be dropped.
        /// </summary>
        public void Detach()
        {
            Input = IntPtr.Zero;
            Output = IntPtr.Zero;
            Overlapped = IntPtr.Zero;
            _inputCapacity = 0;
            _outputCapacity = 0;
            _overlappedCapacity = 0;
            _detached = true;
        }

        public void Dispose()
        {
            if (_detached)
                return;

            if (Overlapped != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Overlapped);
                Overlapped = IntPtr.Zero;
            }
            if (Output != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Output);
                Output = IntPtr.Zero;
            }
            if (Input != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Input);
                Input = IntPtr.Zero;
            }

            _inputCapacity = 0;
            _outputCapacity = 0;
            _overlappedCapacity = 0;
            _detached = true;
            CompletionEvent.Dispose();
        }

        private static IntPtr Grow(IntPtr buffer, ref int capacity, int required)
        {
            if (buffer != IntPtr.Zero && capacity >= required)
                return buffer;

            // Allocate first: the existing block stays owned by the caller's field
            // until a replacement exists, so a failure frees nothing twice.
            IntPtr replacement = Marshal.AllocHGlobal(required);
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            capacity = required;
            return replacement;
        }
    }

    private static bool RegisterPendingCleanupOrWait(
        SafeFileHandle handle,
        IntPtr rawHandle,
        IntPtr inputBuffer,
        IntPtr outputBuffer,
        IntPtr overlappedBuffer,
        EventWaitHandle completionEvent)
    {
        try
        {
            RegisterPendingCleanup(
                handle,
                rawHandle,
                inputBuffer,
                outputBuffer,
                overlappedBuffer,
                completionEvent);
            return true;
        }
        catch
        {
            // Registration normally keeps cancellation/timeout prompt. If the
            // runtime cannot register that cleanup, memory safety wins: wait for
            // CancelIoEx (or a racing normal completion) before allowing finally
            // to release the OVERLAPPED request and its native buffers.
            _ = NativeMethods.GetOverlappedResult(
                rawHandle,
                overlappedBuffer,
                out _,
                true);
            return false;
        }
    }

    private static void RegisterPendingCleanup(
        SafeFileHandle handle,
        IntPtr rawHandle,
        IntPtr inputBuffer,
        IntPtr outputBuffer,
        IntPtr overlappedBuffer,
        EventWaitHandle completionEvent)
    {
        var cleanup = new PendingIoCleanup(
            handle,
            rawHandle,
            inputBuffer,
            outputBuffer,
            overlappedBuffer,
            completionEvent);
        cleanup.Register();
    }

    private sealed class PendingIoCleanup(
        SafeFileHandle handle,
        IntPtr rawHandle,
        IntPtr inputBuffer,
        IntPtr outputBuffer,
        IntPtr overlappedBuffer,
        EventWaitHandle completionEvent)
    {
        private readonly ManualResetEventSlim _registrationPublished = new(false);
        private RegisteredWaitHandle? _registration;

        public void Register()
        {
            try
            {
                _registration = ThreadPool.RegisterWaitForSingleObject(
                    completionEvent,
                    static (state, _) => ((PendingIoCleanup)state!).Complete(),
                    this,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
            }
            finally
            {
                // The completion event may already be signaled. Do not let the
                // callback dispose state before RegisterWaitForSingleObject has
                // returned and its handle has been published here.
                _registrationPublished.Set();
            }
        }

        private void Complete()
        {
            _registrationPublished.Wait();
            try
            {
                _ = NativeMethods.GetOverlappedResult(
                    rawHandle,
                    overlappedBuffer,
                    out _,
                    false);
            }
            catch
            {
                // Cleanup must never surface an exception on a ThreadPool thread.
            }
            finally
            {
                try { _registration?.Unregister(null); } catch { }
                _registration = null;
                Marshal.FreeHGlobal(overlappedBuffer);
                Marshal.FreeHGlobal(outputBuffer);
                Marshal.FreeHGlobal(inputBuffer);
                completionEvent.Dispose();
                handle.DangerousRelease();
                _registrationPublished.Dispose();
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private Win32Exception DeviceError(string operation, int error)
    {
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

    internal static IReadOnlyList<CdAudioSession> ParseFullTableOfContents(
        byte[] buffer, int bytesReturned)
    {
        if (bytesReturned < 4)
            throw InvalidToc("The drive returned a truncated full CD table of contents.");
        int tocLength = buffer[0] << 8 | buffer[1];
        int meaningfulBytes = tocLength + 2;
        if (tocLength < 2 || meaningfulBytes > bytesReturned || meaningfulBytes > buffer.Length)
            throw InvalidToc("The drive returned a truncated full CD table of contents.");
        if ((meaningfulBytes - 4) % 11 != 0)
            throw InvalidToc("The drive returned a misaligned full CD table of contents.");

        var first = new Dictionary<int, int>();
        var last = new Dictionary<int, int>();
        var leadOut = new Dictionary<int, int>();
        for (int offset = 4; offset < meaningfulBytes; offset += 11)
        {
            int session = buffer[offset];
            int point = buffer[offset + 3];
            int minute = buffer[offset + 8];
            int second = buffer[offset + 9];
            int frame = buffer[offset + 10];
            if (session <= 0) continue;

            switch (point)
            {
                case 0xA0:
                    first[session] = minute;
                    break;
                case 0xA1:
                    last[session] = minute;
                    break;
                case 0xA2:
                    if (second >= 60 || frame >= CdAudioFormat.SectorsPerSecond)
                        throw InvalidToc("The drive returned an invalid session lead-out address.");
                    int absolute = checked((minute * 60 + second) *
                        CdAudioFormat.SectorsPerSecond + frame);
                    leadOut[session] = absolute - 2 * CdAudioFormat.SectorsPerSecond;
                    break;
            }
        }

        var sessions = new List<CdAudioSession>();
        foreach ((int number, int firstTrack) in first.OrderBy(item => item.Key))
        {
            if (!last.TryGetValue(number, out int lastTrack) ||
                !leadOut.TryGetValue(number, out int endSector) ||
                firstTrack is < 1 or > 99 || lastTrack < firstTrack || lastTrack > 99)
                continue;
            sessions.Add(new CdAudioSession(number, firstTrack, lastTrack, endSector));
        }
        if (sessions.Count == 0)
            throw InvalidToc("The drive returned no complete sessions in the full CD table of contents.");
        return sessions;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct IoOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
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
            IntPtr device,
            uint controlCode,
            IntPtr inputBuffer,
            int inputBufferSize,
            IntPtr outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResult(
            IntPtr handle,
            IntPtr overlapped,
            out int bytesTransferred,
            [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
