using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

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
    string ExclusiveFloatRates,
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
                + $"Exclusive float probe  {ExclusiveFloatRates}\n"
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

    public static AudioEndpointInfo Inspect(string? deviceId, DataFlow flow, Role defaultRole)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = deviceId == null
                ? enumerator.GetDefaultAudioEndpoint(flow, defaultRole)
                : enumerator.GetDevice(deviceId);
            using AudioClient client = device.AudioClient;
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
                try
                {
                    WaveFormat candidate = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, probeChannels);
                    if (client.IsFormatSupported(AudioClientShareMode.Exclusive, candidate))
                        supported.Add(FormatSampleRate(sampleRate));
                }
                catch { }
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
                    ? $"none of the standard {probeChannels}-channel rates"
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
        using AudioClient client = device.AudioClient;
        WaveFormat mix = client.MixFormat;
        int channels = Math.Clamp(mix.Channels, 1, 2);
        var tone = new DiagnosticToneProvider(mix.SampleRate, channels, 0.7);
        using var output = new WasapiOut(device, shareMode, eventSync, Math.Clamp(bufferMs, 3, 500));
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> handler = (_, args) => stopped.TrySetResult(args);
        output.PlaybackStopped += handler;
        try
        {
            output.Init(tone.ToWaveProvider());
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
        using var capture = new WasapiCapture(device, eventSync, Math.Clamp(bufferMs, 3, 500))
        {
            ShareMode = shareMode,
        };

        WaveFormat format = capture.WaveFormat;
        if (!IsSupportedCaptureFormat(format))
            throw new NotSupportedException(
                $"The input supplies {DescribeFormat(format)}. Deep Groove recording requires a 32-bit float WASAPI format.");

        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statisticsLock = new object();
        double sumSquares = 0;
        long sampleCount = 0;
        float peak = 0;
        EventHandler<WaveInEventArgs> dataHandler = (_, args) =>
        {
            int samples = args.BytesRecorded / sizeof(float);
            double localSquares = 0;
            float localPeak = 0;
            long localCount = 0;
            for (int sampleIndex = 0; sampleIndex < samples; sampleIndex++)
            {
                float sample = BitConverter.ToSingle(args.Buffer, sampleIndex * sizeof(float));
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
            capture.StartRecording();
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
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible
                && extensible.SubFormat == AudioSubtypes.MFAudioFormat_Float);
        return isFloat
            && format.BitsPerSample == sizeof(float) * 8
            && format.Channels > 0
            && format.BlockAlign == format.Channels * sizeof(float);
    }

    private static string DescribeFormat(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible
                && extensible.SubFormat == AudioSubtypes.MFAudioFormat_Float);
        string encoding = isFloat ? "float" : format.Encoding.ToString();
        return $"{FormatSampleRate(format.SampleRate)} · {format.BitsPerSample}-bit {encoding} · {format.Channels} ch";
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

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int frames = Math.Min(count / channels, _totalFrames - _frame);
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
                    buffer[offset + frame * channels + channel] = sample;
            }
            _frame += frames;
            return frames * channels;
        }
    }
}
