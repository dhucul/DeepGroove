using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WaveLab.Audio;

/// <summary>Parsing and normalization for persisted WASAPI hardware choices.</summary>
internal static class AudioHardwareOptions
{
    public static AudioClientShareMode ParseShareMode(string? value) =>
        string.Equals(value, "exclusive", StringComparison.OrdinalIgnoreCase)
            ? AudioClientShareMode.Exclusive
            : AudioClientShareMode.Shared;

    public static Role ParseRole(string? value, Role fallback) => value?.ToLowerInvariant() switch
    {
        "console" => Role.Console,
        "communications" => Role.Communications,
        "multimedia" => Role.Multimedia,
        _ => fallback,
    };

    public static string NormalizeShareMode(string? value) =>
        ParseShareMode(value) == AudioClientShareMode.Exclusive ? "exclusive" : "shared";

    public static string NormalizeRole(string? value, Role fallback) =>
        ParseRole(value, fallback) switch
        {
            Role.Console => "console",
            Role.Communications => "communications",
            _ => "multimedia",
        };
}

public sealed record AudioEndpointInfo(
    string Id,
    string Name,
    DeviceState State,
    string MixFormat,
    double DefaultPeriodMs,
    double MinimumPeriodMs,
    string EndpointLevel,
    string HardwareSupport,
    string ExclusiveFormats,
    string? Error = null)
{
    public string Details
    {
        get
        {
            if (Error != null) return Error;
            return $"{Name}\n"
                + $"Mix format  {MixFormat}\n"
                + $"Engine period  {DefaultPeriodMs:0.###} ms default · {MinimumPeriodMs:0.###} ms minimum\n"
                + $"Endpoint  {EndpointLevel} · hardware {HardwareSupport}\n"
                + $"Exclusive formats  {ExclusiveFormats}\n"
                + $"ID  {Id}";
        }
    }
}

public sealed record AudioInputTestResult(double PeakDbFs, double RmsDbFs, string Format);

/// <summary>The Windows endpoint-level control exposed by a capture device.</summary>
public sealed record AudioInputLevelInfo(
    bool IsAvailable,
    double LevelDb,
    double MinimumDb,
    double MaximumDb,
    double IncrementDb,
    bool IsMuted,
    string? Error = null);

public sealed record AudioInputSettingPlan(
    double DeviceLevelDb,
    double FineTrimDb,
    double TotalLevelDb);

/// <summary>Read-only endpoint diagnostics and a short output-path test.</summary>
public static class AudioHardware
{
    private static readonly int[] ProbeSampleRates = [44100, 48000, 88200, 96000, 176400, 192000];
    private static readonly ConcurrentDictionary<string, bool> ExclusiveEventSupport = new();
    private const int AudioClientBufferSizeError = unchecked((int)0x88890016);
    private const int AudioClientBufferSizeNotAligned = unchecked((int)0x88890019);
    private const int InvalidArgumentError = unchecked((int)0x80070057);

    /// <summary>
    /// Recognizes both the product name and the endpoint name published by Korg's Windows driver.
    /// Endpoint IDs are intentionally ignored: Windows can replace them after a driver reinstall.
    /// </summary>
    public static bool IsKorgDsDac10R(string? endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName)) return false;
        string compact = new(endpointName.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());
        return compact.Contains("DSDAC10R", StringComparison.Ordinal)
            || compact.Contains("KORG2CH1BITAUDIO", StringComparison.Ordinal)
            || compact.Contains("KORG2CHAUDIODEVICE", StringComparison.Ordinal);
    }

    /// <summary>Opens the configuration shortcut installed by Korg's Windows setup package.</summary>
    public static bool TryOpenKorgDsDac10RSettingTool(
        out Process? process,
        out string? error)
    {
        process = null;
        error = null;
        try
        {
            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.CommonStartMenu,
                Environment.SpecialFolder.StartMenu,
            })
            {
                string root = Environment.GetFolderPath(folder);
                if (root.Length == 0) continue;
                string shortcut = Path.Combine(root, "Programs", "KORG", "USB Audio Device",
                    "DS-DAC-10R Setting Tool.lnk");
                if (!File.Exists(shortcut)) continue;
                process = Process.Start(new ProcessStartInfo(shortcut) { UseShellExecute = true });
                return true;
            }

            error = "KORG's DS-DAC-10R Setting Tool shortcut was not found. Install it from the "
                + "KORG AudioGate and USB Audio Device Setup package, or open it from Start > KORG "
                + "> USB Audio Device.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static AudioEndpointInfo Inspect(string? deviceId, DataFlow flow, Role defaultRole)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = deviceId == null
                ? enumerator.GetDefaultAudioEndpoint(flow, defaultRole)
                : enumerator.GetDevice(deviceId);
            using AudioClient client = device.CreateAudioClient();
            WaveFormat mix = client.MixFormat;

            string level = "level unavailable";
            string hardware = "unknown";
            try
            {
                using AudioEndpointVolume volume = device.AudioEndpointVolume;
                level = volume.Mute
                    ? $"muted ({volume.MasterVolumeLevelScalar * 100:0}%)"
                    : $"{volume.MasterVolumeLevelScalar * 100:0}%";
                hardware = FormatHardwareSupport(volume.HardwareSupport);
            }
            catch { }

            int probeChannels = Math.Clamp(mix.Channels, 1, 2);
            var supported = new List<string>();
            foreach (int sampleRate in ProbeSampleRates)
            {
                foreach (WaveFormat candidate in ExclusiveFormatCandidates(sampleRate, probeChannels))
                {
                    if (!IsFormatSupported(client, AudioClientShareMode.Exclusive, candidate)) continue;
                    supported.Add(
                        $"{FormatSampleRate(sampleRate)} {candidate.BitsPerSample}-bit {DescribeEncoding(candidate)}");
                }
            }

            return new AudioEndpointInfo(
                device.ID,
                device.FriendlyName,
                device.State,
                DescribeFormat(mix),
                ToMilliseconds(client.DefaultDevicePeriod),
                ToMilliseconds(client.MinimumDevicePeriod),
                level,
                hardware,
                supported.Count == 0
                    ? $"none of the standard {probeChannels}-channel formats"
                    : string.Join(", ", supported) + $" ({probeChannels} ch)");
        }
        catch (Exception ex)
        {
            return new AudioEndpointInfo(
                deviceId ?? "Windows default",
                "Endpoint unavailable",
                DeviceState.NotPresent,
                "Unknown",
                0,
                0,
                "unavailable",
                "unknown",
                "not tested",
                ex.Message);
        }
    }

    public static (int Active, int Disabled, int Unplugged) Inventory(DataFlow flow)
    {
        int active = 0, disabled = 0, unplugged = 0;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
            foreach (MMDevice device in devices)
            {
                using (device)
                {
                    if ((device.State & DeviceState.Active) != 0) active++;
                    if ((device.State & DeviceState.Disabled) != 0) disabled++;
                    if ((device.State & (DeviceState.Unplugged | DeviceState.NotPresent)) != 0) unplugged++;
                }
            }
        }
        catch { }
        return (active, disabled, unplugged);
    }

    /// <summary>Reads the selected capture endpoint's Windows input-level control.</summary>
    public static AudioInputLevelInfo GetInputLevel(string? deviceId, Role defaultRole) =>
        AccessInputLevel(deviceId, defaultRole, requestedLevelDb: null);

    /// <summary>
    /// Changes the selected capture endpoint's Windows input level and returns
    /// the value accepted by its driver. This does not add post-capture gain.
    /// </summary>
    public static AudioInputLevelInfo SetInputLevel(
        string? deviceId,
        Role defaultRole,
        double levelDb) =>
        AccessInputLevel(deviceId, defaultRole, levelDb);

    private static AudioInputLevelInfo AccessInputLevel(
        string? deviceId,
        Role defaultRole,
        double? requestedLevelDb)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = deviceId == null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, defaultRole)
                : enumerator.GetDevice(deviceId);
            using AudioEndpointVolume volume = device.AudioEndpointVolume;
            AudioEndpointVolumeVolumeRange range = volume.VolumeRange;
            double minimum = range.MinDecibels;
            double maximum = range.MaxDecibels;
            double increment = range.IncrementDecibels;
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
                throw new InvalidOperationException("The input driver reported an invalid level range.");

            if (requestedLevelDb is double requested)
                volume.MasterVolumeLevel = (float)NormalizeInputLevelDb(
                    requested, minimum, maximum, increment);

            return new AudioInputLevelInfo(
                true,
                Math.Clamp(volume.MasterVolumeLevel, minimum, maximum),
                minimum,
                maximum,
                double.IsFinite(increment) && increment > 0 ? increment : 0.5,
                volume.Mute);
        }
        catch (Exception ex)
        {
            return new AudioInputLevelInfo(false, 0, -96, 0, 0.5, false, ex.Message);
        }
    }

    internal static double NormalizeInputLevelDb(
        double levelDb,
        double minimumDb,
        double maximumDb,
        double incrementDb)
    {
        if (!double.IsFinite(levelDb)) levelDb = minimumDb;
        double normalized = Math.Clamp(levelDb, minimumDb, maximumDb);
        if (double.IsFinite(incrementDb) && incrementDb > 0)
        {
            normalized = minimumDb + Math.Round(
                (normalized - minimumDb) / incrementDb,
                MidpointRounding.AwayFromZero) * incrementDb;
        }
        return Math.Clamp(normalized, minimumDb, maximumDb);
    }

    internal static AudioInputSettingPlan PlanInputSetting(
        double targetTotalDb,
        double minimumDeviceDb,
        double maximumDeviceDb,
        double deviceIncrementDb)
    {
        double increment = double.IsFinite(deviceIncrementDb) && deviceIncrementDb > 0
            ? deviceIncrementDb
            : 0.5;
        double target = double.IsFinite(targetTotalDb) ? targetTotalDb : maximumDeviceDb;
        double steps = Math.Ceiling((target - minimumDeviceDb) / increment - 1e-9);
        double device = Math.Clamp(
            minimumDeviceDb + steps * increment,
            minimumDeviceDb,
            maximumDeviceDb);
        double fine = RecordingEngine.NormalizeInputFineTrimDb(target - device);
        double total = device + fine;

        // Fine Trim rounds to 0.1 dB. Bias a half-step rounding result downward
        // rather than allowing the displayed "safe" setting to exceed its target.
        if (total > target + 1e-9 && fine > -3)
        {
            fine = RecordingEngine.NormalizeInputFineTrimDb(fine - 0.1);
            total = device + fine;
        }

        if (total > target + 1e-9 && device > minimumDeviceDb)
        {
            // A driver step can be wider than Fine Trim's 3 dB range. In that
            // case the next higher device step is unsafe, so use the greatest
            // hardware step at or below the requested total.
            double lowerSteps = Math.Floor(
                (target - minimumDeviceDb) / increment + 1e-9);
            double lowerDevice = Math.Clamp(
                minimumDeviceDb + lowerSteps * increment,
                minimumDeviceDb,
                maximumDeviceDb);
            double lowerFine = RecordingEngine.NormalizeInputFineTrimDb(target - lowerDevice);
            double lowerTotal = lowerDevice + lowerFine;
            if (lowerTotal <= target + 1e-9)
                return new AudioInputSettingPlan(lowerDevice, lowerFine, lowerTotal);
        }

        return new AudioInputSettingPlan(device, fine, total);
    }

    /// <summary>
    /// Which half of a plan to apply first so the input is never transiently hotter
    /// than both the setting it left and the one it is going to.
    ///
    /// Fine Trim is attenuation only, so if the plan attenuates further, doing that
    /// first is unambiguously safe. Otherwise the device step goes first: the new
    /// fine trim is the less negative of the two, so the intermediate total
    /// (new device + old fine) cannot exceed the plan's own total.
    /// </summary>
    internal static bool ApplyFineTrimFirst(double currentFineDb, AudioInputSettingPlan plan) =>
        plan.FineTrimDb < currentFineDb - 1e-9;

    public static async Task TestOutputAsync(
        string? deviceId,
        Role defaultRole,
        AudioClientShareMode shareMode,
        bool eventSync,
        int bufferMs,
        CancellationToken cancellationToken = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = deviceId == null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, defaultRole)
            : enumerator.GetDevice(deviceId);
        using AudioClient client = device.CreateAudioClient();
        WaveFormat mix = client.MixFormat;
        WaveFormat toneFormat = shareMode == AudioClientShareMode.Exclusive
            ? SelectExclusiveFormat(
                mix,
                candidate => IsFormatSupported(client, AudioClientShareMode.Exclusive, candidate),
                allowSampleRateFallback: true)
                ?? throw new NotSupportedException(
                    "The selected output does not expose a supported format for exclusive mode. "
                    + "Use shared mode or choose another endpoint.")
            : WaveFormat.CreateIeeeFloatWaveFormat(
                mix.SampleRate, Math.Clamp(mix.Channels, 1, 2));
        var tone = new DiagnosticToneProvider(toneFormat.SampleRate, toneFormat.Channels, 0.7);
        IWaveProvider toneWave = ConvertOutput(tone, toneFormat);
        bool effectiveEventSync = ResolveOutputEventScheduling(
            device, shareMode, eventSync, bufferMs, toneWave.WaveFormat);
        IWavePlayer output = CreatePlayer(
            device, shareMode, effectiveEventSync, Math.Clamp(bufferMs, 3, 500));
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> handler = (_, args) => stopped.TrySetResult(args);
        try
        {
            output.Init(toneWave);
            output.PlaybackStopped += handler;
            output.Play();
            StoppedEventArgs result = await stopped.Task
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken)
                .ConfigureAwait(false);
            if (result.Exception != null) throw result.Exception;
        }
        finally
        {
            output.PlaybackStopped -= handler;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }
    }

    public static async Task<AudioInputTestResult> TestInputAsync(
        string? deviceId,
        Role defaultRole,
        AudioClientShareMode shareMode,
        bool eventSync,
        int bufferMs,
        CancellationToken cancellationToken = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = deviceId == null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, defaultRole)
            : enumerator.GetDevice(deviceId);
        using WasapiRecorder capture = CreateRecorder(
            device, shareMode, eventSync, Math.Clamp(bufferMs, 3, 500));

        WaveFormat format = capture.WaveFormat;
        if (!IsSupportedCaptureFormat(format))
            throw new NotSupportedException(
                $"The input supplies {DescribeFormat(format)}. Deep Groove recording requires "
                + "32-bit float, 24-bit PCM, or 16-bit PCM audio.");

        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statisticsLock = new object();
        double sumSquares = 0;
        long sampleCount = 0;
        float peak = 0;
        CaptureDataAvailableHandler dataHandler = (data, _, _, _) =>
        {
            float[] samples = DecodeCaptureSamples(data, format);
            double localSquares = 0;
            float localPeak = 0;
            long localCount = 0;
            foreach (float sample in samples)
            {
                if (!float.IsFinite(sample)) continue;
                localPeak = Math.Max(localPeak, Math.Abs(sample));
                localSquares += sample * sample;
                localCount++;
            }
            lock (statisticsLock)
            {
                peak = Math.Max(peak, localPeak);
                sumSquares += localSquares;
                sampleCount += localCount;
            }
        };
        EventHandler<StoppedEventArgs> stoppedHandler = (_, args) => stopped.TrySetResult(args);
        capture.DataAvailable += dataHandler;
        capture.RecordingStopped += stoppedHandler;
        try
        {
            StartCapture(capture, device, shareMode);
            Task delay = Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            Task first = await Task.WhenAny(stopped.Task, delay).ConfigureAwait(false);
            if (ReferenceEquals(first, delay))
            {
                cancellationToken.ThrowIfCancellationRequested();
                capture.StopRecording();
            }
            StoppedEventArgs result = await stopped.Task
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken)
                .ConfigureAwait(false);
            if (result.Exception != null) throw result.Exception;

            lock (statisticsLock)
            {
                double rms = sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount);
                return new AudioInputTestResult(ToDecibels(peak), ToDecibels(rms), DescribeFormat(format));
            }
        }
        finally
        {
            capture.DataAvailable -= dataHandler;
            capture.RecordingStopped -= stoppedHandler;
            try { capture.StopRecording(); } catch { }
        }
    }

    internal static bool IsSupportedCaptureFormat(WaveFormat format)
    {
        bool supportedEncoding = (IsFloatFormat(format) && format.BitsPerSample == sizeof(float) * 8)
            || (IsPcmFormat(format) && format.BitsPerSample is 16 or 24);
        return supportedEncoding
            && format.Channels > 0
            && format.BlockAlign == format.Channels * (format.BitsPerSample / 8);
    }

    /// <summary>
    /// WASAPI's mix format describes shared mode and is not necessarily accepted by the
    /// endpoint in exclusive mode. Choose an explicitly supported stream format, keeping
    /// the requested rate and channel count whenever possible. Float is preferred, then
    /// 24-bit and 16-bit PCM, all of which the app can adapt to or from its float pipeline.
    /// </summary>
    internal static WaveFormat? SelectExclusiveFormat(
        WaveFormat preferred,
        Func<WaveFormat, bool> isSupported,
        bool allowSampleRateFallback)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(isSupported);

        int[] rates = allowSampleRateFallback
            ? [preferred.SampleRate, .. ProbeSampleRates.Where(rate => rate != preferred.SampleRate)]
            : [preferred.SampleRate];
        int[] channels = [preferred.Channels, .. new[] { 2, 1 }.Where(count => count != preferred.Channels)];
        foreach (int sampleRate in rates)
        {
            foreach (int channelCount in channels)
            {
                if (sampleRate <= 0 || channelCount <= 0) continue;
                foreach (WaveFormat candidate in ExclusiveFormatCandidates(sampleRate, channelCount))
                {
                    if (isSupported(candidate)) return candidate;
                }
            }
        }
        return null;
    }

    internal static WaveFormat GetExclusiveCaptureFormat(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        using AudioClient client = device.CreateAudioClient();
        WaveFormat mix = client.MixFormat;
        return SelectExclusiveFormat(
            mix,
            candidate => IsFormatSupported(client, AudioClientShareMode.Exclusive, candidate),
            allowSampleRateFallback: true)
            ?? throw new NotSupportedException(
                $"{device.FriendlyName} does not expose a supported capture format "
                + "for exclusive mode. Use shared capture or choose another input.");
    }

    internal static void StartCapture(
        WasapiRecorder capture,
        MMDevice device,
        AudioClientShareMode shareMode)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(device);
        try
        {
            capture.StartRecording();
        }
        catch (Exception ex) when (
            shareMode == AudioClientShareMode.Exclusive
            && ex is AudioDeviceInUseException or AudioExclusiveModeNotAllowedException)
        {
            throw new InvalidOperationException(
                $"{device.FriendlyName} rejected exclusive capture initialization. In Windows "
                + "Sound properties, enable 'Allow applications to take exclusive control' for "
                + "this input and close other audio applications, or use Shared capture.",
                ex);
        }
        catch (CoreAudioException ex) when (IsCaptureParameterError(ex.HResult))
        {
            throw new InvalidOperationException(
                $"{device.FriendlyName} rejected the requested capture stream parameters. "
                + "Check the endpoint's Windows default format and try a larger buffer or the "
                + "other sharing mode. The driver returned E_INVALIDARG.",
                ex);
        }
    }

    internal static WasapiRecorder CreateRecorder(
        MMDevice device,
        AudioClientShareMode shareMode,
        bool eventSync,
        int bufferMs)
    {
        ArgumentNullException.ThrowIfNull(device);
        var builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithBufferLength(Math.Clamp(bufferMs, 3, 500))
            .WithMmcssThreadPriority();
        builder = shareMode == AudioClientShareMode.Exclusive
            ? builder.WithExclusiveMode().WithFormat(GetExclusiveCaptureFormat(device))
            : builder.WithSharedMode();
        builder = eventSync ? builder.WithEventSync() : builder.WithPollingSync();
        return builder.Build();
    }

    internal static IWaveProvider CreateExclusiveOutputProvider(
        MMDevice device,
        ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(source);
        using AudioClient client = device.CreateAudioClient();
        WaveFormat target = SelectExclusiveFormat(
            source.WaveFormat,
            candidate => IsFormatSupported(client, AudioClientShareMode.Exclusive, candidate),
            allowSampleRateFallback: false)
            ?? throw new NotSupportedException(
                $"{device.FriendlyName} does not accept {FormatSampleRate(source.WaveFormat.SampleRate)} "
                + "in exclusive mode. Resample the document, use shared playback, or choose another output.");

        ISampleProvider adapted = source;
        if (source.WaveFormat.Channels != target.Channels)
        {
            if (!CanAdaptOutputChannels(source.WaveFormat.Channels, target.Channels))
            {
                throw new NotSupportedException(
                    $"{device.FriendlyName} requires {target.Channels}-channel exclusive playback, "
                    + $"but an automatic {source.WaveFormat.Channels}-to-{target.Channels} channel "
                    + "conversion would discard audio. Downmix the document first or use shared playback.");
            }
            adapted = new SoftwareInputMonitor.ChannelMappingSampleProvider(source, target.Channels);
        }
        return ConvertOutput(adapted, target);
    }

    internal static bool CanAdaptOutputChannels(int sourceChannels, int targetChannels) =>
        sourceChannels == targetChannels
        || (sourceChannels == 1 && targetChannels == 2)
        || (sourceChannels == 2 && targetChannels == 1);

    internal static WasapiPlayer CreatePlayer(
        MMDevice device,
        AudioClientShareMode shareMode,
        bool eventSync,
        int bufferMs)
    {
        ArgumentNullException.ThrowIfNull(device);
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithLatency(Math.Clamp(bufferMs, 3, 500))
            .WithMmcssThreadPriority();
        builder = shareMode == AudioClientShareMode.Exclusive
            ? builder.WithExclusiveMode()
            : builder.WithSharedMode();
        builder = eventSync ? builder.WithEventSync() : builder.WithPollingSync();
        return builder.Build();
    }

    /// <summary>
    /// NAudio 3 initializes the real stream on its worker thread. Probe an exclusive
    /// event stream synchronously with the exact provider format so a driver-specific
    /// buffer rejection can fall back to polling before playback is published as running.
    /// </summary>
    internal static bool ResolveOutputEventScheduling(
        MMDevice device,
        AudioClientShareMode shareMode,
        bool requestedEventSync,
        int bufferMs,
        WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(format);
        if (!requestedEventSync || shareMode == AudioClientShareMode.Shared)
            return requestedEventSync;

        string key = $"{device.ID}\n{format.SampleRate}\n{format.Channels}\n"
            + $"{format.BitsPerSample}\n{format.Encoding}\n{Math.Clamp(bufferMs, 3, 500)}";
        return ExclusiveEventSupport.GetOrAdd(
            key,
            _ => Task.Run(() => ProbeExclusiveEventPlayback(
                    device.ID, bufferMs, format))
                .GetAwaiter().GetResult());
    }

    internal static bool ShouldFallbackExclusiveEvent(int hresult) =>
        hresult is AudioClientBufferSizeError or AudioClientBufferSizeNotAligned;

    internal static bool IsCaptureParameterError(int hresult) =>
        hresult == InvalidArgumentError;

    private static bool ProbeExclusiveEventPlayback(
        string deviceId,
        int bufferMs,
        WaveFormat format)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDevice(deviceId);
        IWavePlayer player = CreatePlayer(
            device, AudioClientShareMode.Exclusive, eventSync: true, bufferMs);
        var stopped = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> handler = (_, args) => stopped.TrySetResult(args.Exception);
        try
        {
            player.Init(new SilentWaveProvider(format));
            player.PlaybackStopped += handler;
            player.Play();
            int probeTimeoutMs = Math.Clamp(Math.Clamp(bufferMs, 3, 500) * 3 + 100, 250, 1600);
            if (!stopped.Task.Wait(TimeSpan.FromMilliseconds(probeTimeoutMs))) return true;
            Exception? error = stopped.Task.Result;
            if (error is CoreAudioException coreAudio
                && ShouldFallbackExclusiveEvent(coreAudio.HResult)) return false;
            if (error != null) throw error;
            return true;
        }
        finally
        {
            player.PlaybackStopped -= handler;
            try
            {
                if (player is IAsyncDisposable asyncDisposable)
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                else
                    player.Dispose();
            }
            catch { }
        }
    }

    internal static float[] DecodeCaptureSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int available = Math.Min(buffer.Length, Math.Max(0, bytesRecorded));
        return DecodeCaptureSamples(buffer.AsSpan(0, available), format);
    }

    internal static float[] DecodeCaptureSamples(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (!IsSupportedCaptureFormat(format))
            throw new NotSupportedException($"Unsupported capture format: {DescribeFormat(format)}.");

        int bytesPerSample = format.BitsPerSample / 8;
        int completeBytes = buffer.Length;
        completeBytes -= completeBytes % format.BlockAlign;
        int sampleCount = completeBytes / bytesPerSample;
        var samples = new float[sampleCount];

        if (IsFloatFormat(format))
        {
            buffer[..completeBytes].CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
            return samples;
        }

        if (format.BitsPerSample == 16)
        {
            for (int index = 0, offset = 0; index < sampleCount; index++, offset += 2)
                samples[index] = BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(offset, 2)) / 32768f;
            return samples;
        }

        for (int index = 0, offset = 0; index < sampleCount; index++, offset += 3)
        {
            int value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            samples[index] = value / 8388608f;
        }
        return samples;
    }

    private static IEnumerable<WaveFormat> ExclusiveFormatCandidates(int sampleRate, int channels)
    {
        yield return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        yield return new WaveFormat(sampleRate, 24, channels);
        yield return new WaveFormat(sampleRate, 16, channels);
    }

    private static IWaveProvider ConvertOutput(ISampleProvider source, WaveFormat target)
    {
        if (IsFloatFormat(target)) return source.ToWaveProvider();
        return target.BitsPerSample switch
        {
            24 => new SampleToWaveProvider24(source),
            16 => new SampleToWaveProvider16(source),
            _ => throw new NotSupportedException($"Unsupported output format: {DescribeFormat(target)}."),
        };
    }

    private static bool IsFloatFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format is WaveFormatExtensible extensible
            && extensible.SubFormat == AudioSubtypes.MFAudioFormat_Float);

    private static bool IsPcmFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.Pcm
        || (format is WaveFormatExtensible extensible
            && extensible.SubFormat == AudioSubtypes.MFAudioFormat_PCM);

    private static bool IsFormatSupported(
        AudioClient client,
        AudioClientShareMode shareMode,
        WaveFormat format)
    {
        try { return client.IsFormatSupported(shareMode, format); }
        catch { return false; }
    }

    private static string DescribeFormat(WaveFormat format)
    {
        string encoding = DescribeEncoding(format);
        return $"{FormatSampleRate(format.SampleRate)} · {format.BitsPerSample}-bit {encoding} · {format.Channels} ch";
    }

    internal static string DescribeEncoding(WaveFormat format)
    {
        if (IsFloatFormat(format)) return "float";
        if (IsPcmFormat(format)) return "PCM";
        return format.Encoding.ToString();
    }

    private static string FormatSampleRate(int sampleRate) =>
        sampleRate % 1000 == 0 ? $"{sampleRate / 1000} kHz" : $"{sampleRate / 1000.0:0.0} kHz";

    private static double ToMilliseconds(long referenceTime) =>
        referenceTime / (double)TimeSpan.TicksPerMillisecond;

    private static double ToDecibels(double amplitude) =>
        amplitude <= 0 ? -120 : Math.Max(-120, 20 * Math.Log10(amplitude));

    private static string FormatHardwareSupport(EEndpointHardwareSupport support)
    {
        if (support == 0) return "none reported";
        var parts = new List<string>();
        if ((support & EEndpointHardwareSupport.Volume) != 0) parts.Add("volume");
        if ((support & EEndpointHardwareSupport.Mute) != 0) parts.Add("mute");
        if ((support & EEndpointHardwareSupport.Meter) != 0) parts.Add("meter");
        return parts.Count == 0 ? support.ToString() : string.Join("/", parts);
    }

    private sealed class SilentWaveProvider : IWaveProvider
    {
        private int _remainingBytes;

        public SilentWaveProvider(WaveFormat format)
        {
            WaveFormat = format;
            _remainingBytes = Math.Max(
                format.BlockAlign,
                format.AverageBytesPerSecond / 50); // 20 ms, then a deliberate end-of-stream.
            _remainingBytes -= _remainingBytes % format.BlockAlign;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<byte> buffer)
        {
            if (_remainingBytes <= 0) return 0;
            int bytes = Math.Min(buffer.Length, _remainingBytes);
            bytes -= bytes % WaveFormat.BlockAlign;
            buffer[..bytes].Clear();
            _remainingBytes -= bytes;
            return bytes;
        }
    }

    private sealed class DiagnosticToneProvider : ISampleProvider
    {
        private readonly int _totalFrames;
        private int _frame;

        public DiagnosticToneProvider(int sampleRate, int channels, double seconds)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _totalFrames = Math.Max(1, (int)(sampleRate * seconds));
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            int channels = WaveFormat.Channels;
            int frames = Math.Min(buffer.Length / channels, _totalFrames - _frame);
            for (int frame = 0; frame < frames; frame++)
            {
                int absolute = _frame + frame;
                int fadeFrames = Math.Max(1, WaveFormat.SampleRate / 100);
                double envelope = Math.Min(1.0, Math.Min(
                    absolute / (double)fadeFrames,
                    (_totalFrames - absolute - 1) / (double)fadeFrames));
                float sample = (float)(0.12 * Math.Max(0, envelope)
                    * Math.Sin(2 * Math.PI * 440 * absolute / WaveFormat.SampleRate));
                for (int channel = 0; channel < channels; channel++)
                    buffer[frame * channels + channel] = sample;
            }
            _frame += frames;
            return frames * channels;
        }
    }
}
