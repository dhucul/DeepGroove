using System.Reflection;
using NAudio.CoreAudioApi;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class RecordingInputControlTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _previousSettingsDir = AppSettings.AppDataDir;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public RecordingInputControlTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _previousSettingsDir;
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData(AudioClientShareMode.Shared, (EEndpointHardwareSupport)0, true)]
    [InlineData(AudioClientShareMode.Exclusive, (EEndpointHardwareSupport)0, false)]
    [InlineData(AudioClientShareMode.Exclusive, EEndpointHardwareSupport.Mute, false)]
    [InlineData(AudioClientShareMode.Exclusive, EEndpointHardwareSupport.Meter, false)]
    [InlineData(AudioClientShareMode.Exclusive, EEndpointHardwareSupport.Volume, true)]
    [InlineData(AudioClientShareMode.Exclusive, EEndpointHardwareSupport.Volume | EEndpointHardwareSupport.Mute, true)]
    public void ExclusiveCaptureRequiresHardwareVolumeSupport(AudioClientShareMode mode, EEndpointHardwareSupport support, bool expected)
    {
        Assert.Equal(expected, AudioHardware.IsEndpointLevelEffective(mode, support));
    }

    [Fact]
    public void BypassedWindowsLevelCannotProduceARepeatedApplyReductionLoop()
    {
        using var vm = new RecordViewModel();
        Set(vm, "_selectedDevice", new CaptureDevice("nonexistent-test-endpoint", "Test interface"));
        ApplyInfo(vm, new AudioInputLevelInfo(false, -15, -96, 0, 1.5, false) { IsBypassed = true });
        vm.InputFineTrimDb = -0.5;
        var result = new RecordingLevelAnalyzer(8_000, 1).Snapshot with
        {
            Status = RecordingLevelStatus.Hot,
            ActiveSeconds = 12,
            ProgramPeakDb = -0.5,
            SuggestedGainDb = -8,
        };
        Set(vm, "_levelSnapshot", result);
        typeof(RecordViewModel).GetMethod("UpdateHeldRecommendation", Private)!.Invoke(vm, [result]);

        Assert.False(vm.HasInputLevelControl);
        Assert.Equal("Bypassed", vm.InputLevelText);
        Assert.Contains("does not affect exclusive capture", vm.InputLevelStatusText);
        Assert.Equal("Adjust on interface", vm.ApplyRecommendationText);
        Assert.Equal("REDUCE 8.0 dB", vm.SuggestedGainText);
        Assert.Equal(-8.5, (double)typeof(RecordViewModel).GetField("_heldRecommendedTotalDb", Private)!.GetValue(vm)!);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.False(vm.CanApplyRecommendedInputSetting);
            Assert.False(vm.ApplyRecommendedInputSetting());
            vm.InputLevelDb = -21;
            Assert.Equal(-15, vm.InputLevelDb); // the disabled value cannot be written
            Assert.Equal(-0.5, vm.InputFineTrimDb);
            Assert.Equal("REDUCE 8.0 dB", vm.SuggestedGainText);
        }
    }

    [Fact]
    public void CapabilityChangeDisablesApplyAndRememberedWindowsSettings()
    {
        using var vm = new RecordViewModel();
        Set(vm, "_selectedDevice", new CaptureDevice("nonexistent-test-endpoint", "Test interface"));
        Assert.True(AppSettings.Instance.SetInputCalibration("nonexistent-test-endpoint",
            new AppSettings.InputCalibrationInfo(-8, -0.5, DateTime.UtcNow, -21, 0, -21)));
        ApplyInfo(vm, new AudioInputLevelInfo(true, 0, -96, 0, 1.5, false));
        Set(vm, "_heldRecommendedTotalDb", -8.0);
        Assert.True(vm.CanApplyRecommendedInputSetting);
        Assert.True(vm.CanUseRememberedSetting);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        ApplyInfo(vm, new AudioInputLevelInfo(false, 0, -96, 0, 1.5, false) { IsBypassed = true });

        Assert.False(vm.CanApplyRecommendedInputSetting);
        Assert.False(vm.CanUseRememberedSetting);
        Assert.False(vm.UseRememberedSetting());
        Assert.Contains("does not apply to this input mode", vm.DeviceMemoryText);
        Assert.Contains(nameof(vm.CanApplyRecommendedInputSetting), notifications);
        Assert.Contains(nameof(vm.CanUseRememberedSetting), notifications);
        Assert.Contains(nameof(vm.ApplyRecommendationText), notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingBypassStateInvalidatesAdviceFromThePreviousGainReference(bool bypassed)
    {
        using var vm = new RecordViewModel();
        ApplyInfo(vm, new AudioInputLevelInfo(bypassed, 0, -96, 0, 1.5, false) { IsBypassed = !bypassed });
        Set(vm, "_heldRecommendedTotalDb", -23.0);
        Set(vm, "_heldProgramPeakDb", -1.0);
        Set(vm, "_recommendationApplied", true);
        Set(vm, "_applyRecommendationStatusText", "Applied");
        Set(vm, "_completedLevelCheckNote", "Old full-scan setting");
        Set(vm, "_calibrationSavedTotalDb", -23.0);

        ApplyInfo(vm, new AudioInputLevelInfo(!bypassed, bypassed ? 0 : -15, -96, 0, 1.5, false) { IsBypassed = bypassed });

        Assert.True(double.IsNaN((double)typeof(RecordViewModel).GetField("_heldRecommendedTotalDb", Private)!.GetValue(vm)!));
        Assert.True(double.IsNaN((double)typeof(RecordViewModel).GetField("_calibrationSavedTotalDb", Private)!.GetValue(vm)!));
        Assert.Null(typeof(RecordViewModel).GetField("_completedLevelCheckNote", Private)!.GetValue(vm));
        Assert.Empty(vm.ApplyRecommendationStatusText);
        Assert.Equal("PROVISIONAL", vm.SuggestedGainText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableInputIsRecheckedBeforeFineTrimChanges(bool remembered)
    {
        using var vm = new RecordViewModel();
        Set(vm, "_selectedDevice", new CaptureDevice("nonexistent-test-endpoint", "Test interface"));
        ApplyInfo(vm, new AudioInputLevelInfo(true, 0, -96, 0, 1.5, false));
        Set(vm, "_heldRecommendedTotalDb", -8.0);
        Assert.True(AppSettings.Instance.SetInputCalibration("nonexistent-test-endpoint",
            new AppSettings.InputCalibrationInfo(-8, -0.5, DateTime.UtcNow, -7.5, -0.5, -8)));
        int trimChanges = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.InputFineTrimDb)) trimChanges++; };

        Assert.False(remembered ? vm.UseRememberedSetting() : vm.ApplyRecommendedInputSetting());

        Assert.Equal(0, trimChanges);
        Assert.Equal(0, vm.InputFineTrimDb);
        Assert.False(vm.CanApplyRecommendedInputSetting);
    }

    [Theory]
    [InlineData(AudioClientShareMode.Exclusive, "shared")]
    [InlineData(AudioClientShareMode.Shared, "exclusive")]
    public void ControlModeFollowsTheOwnedCaptureUntilTeardown(AudioClientShareMode activeMode, string preference)
    {
        using var vm = new RecordViewModel();
        var engine = (RecordingEngine)typeof(RecordViewModel).GetField("_engine", Private)!.GetValue(vm)!;
        var sessionType = typeof(RecordingEngine).GetNestedType("CaptureSession", BindingFlags.NonPublic)!;
        // No recorder is opened. This represents the session ownership and mode
        // that StartCore publishes before the view model refreshes its controls.
        object session = Activator.CreateInstance(sessionType, Private | BindingFlags.Public, null,
            [1L, null, new TaskCompletionSource<NAudio.Wave.StoppedEventArgs>(), false,
                NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), activeMode], null)!;
        Set(engine, "_session", session);
        try
        {
            AppSettings.Instance.InputShareMode = preference;
            Assert.Equal(activeMode, engine.CaptureShareMode);
            Assert.Equal(activeMode, typeof(RecordViewModel).GetProperty("InputControlShareMode", Private)!.GetValue(vm));
        }
        finally
        {
            // Clear the fake session before disposal; it owns no real hardware.
            typeof(RecordingEngine).GetField("_session", Private)!.SetValue(engine, null);
        }
        Assert.Null(engine.CaptureShareMode);
        Assert.Equal(AudioHardwareOptions.ParseShareMode(preference),
            typeof(RecordViewModel).GetProperty("InputControlShareMode", Private)!.GetValue(vm));
    }

    [Fact]
    public void RefreshDoesNotEnableActionsWhilePublishingSliderBounds()
    {
        using var vm = new RecordViewModel();
        Set(vm, "_selectedDevice", new CaptureDevice("nonexistent-test-endpoint", "Test interface"));
        ApplyInfo(vm, new AudioInputLevelInfo(true, 0, -96, 0, 1.5, false));
        Set(vm, "_heldRecommendedTotalDb", -8.0);
        bool sawBounds = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.InputLevelMinimumDb)) return;
            sawBounds = true;
            Assert.False(vm.CanApplyRecommendedInputSetting);
            Assert.Equal(-1.5, vm.InputLevelDb);
        };

        ApplyInfo(vm, new AudioInputLevelInfo(true, -1.5, -96, 0, 1.5, false));

        Assert.True(sawBounds);
        Assert.True(vm.CanApplyRecommendedInputSetting);
    }

    private static void ApplyInfo(RecordViewModel vm, AudioInputLevelInfo info) =>
        typeof(RecordViewModel).GetMethod("ApplyInputLevelInfo", Private)!.Invoke(vm, [info]);

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, Private)!.SetValue(target, value);
}
