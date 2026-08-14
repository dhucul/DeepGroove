using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class AppSettingsAudioHardwareTests
{
    [Fact]
    public void CaptureFormatValidationRequiresInterleaved32BitFloat()
    {
        WaveFormat valid = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        WaveFormat float64 = WaveFormat.CreateCustomFormat(
            WaveFormatEncoding.IeeeFloat, 48_000, 2, 48_000 * 16, 16, 64);
        WaveFormat invalidBlockAlignment = WaveFormat.CreateCustomFormat(
            WaveFormatEncoding.IeeeFloat, 48_000, 2, 48_000 * 4, 4, 32);
        var pcm32 = new WaveFormat(48_000, 32, 2);

        Assert.True(AudioHardware.IsSupportedCaptureFormat(valid));
        Assert.False(AudioHardware.IsSupportedCaptureFormat(float64));
        Assert.False(AudioHardware.IsSupportedCaptureFormat(invalidBlockAlignment));
        Assert.False(AudioHardware.IsSupportedCaptureFormat(pcm32));
    }

    [Theory]
    [InlineData(-27.24, -96, 0, 0.5, -27.0)]
    [InlineData(-120, -96, 0, 0.5, -96.0)]
    [InlineData(8, -96, 0, 0.5, 0.0)]
    [InlineData(4.74, -12, 12, 0.5, 4.5)]
    public void InputLevelNormalizationClampsAndUsesDriverSteps(
        double requested,
        double minimum,
        double maximum,
        double increment,
        double expected)
    {
        Assert.Equal(expected, AudioHardware.NormalizeInputLevelDb(
            requested, minimum, maximum, increment), 6);
    }

    [Theory]
    [InlineData(-1.26, -1.3)]
    [InlineData(-0.04, 0.0)]
    [InlineData(-8.0, -3.0)]
    [InlineData(2.0, 0.0)]
    public void FineTrimUsesTenthDbAttenuationOnly(double requested, double expected)
    {
        Assert.Equal(expected, RecordingEngine.NormalizeInputFineTrimDb(requested), 6);
    }

    [Theory]
    [InlineData(-17.3, -96, 0, 2.0, -16.0, -1.3, -17.3)]
    [InlineData(-14.7, -96, 0, 1.5, -13.5, -1.2, -14.7)]
    public void InputSettingPlanUsesCoarseDeviceAndFineAttenuation(
        double target,
        double minimum,
        double maximum,
        double increment,
        double expectedDevice,
        double expectedFine,
        double expectedTotal)
    {
        AudioInputSettingPlan plan = AudioHardware.PlanInputSetting(
            target, minimum, maximum, increment);

        Assert.Equal(expectedDevice, plan.DeviceLevelDb, 6);
        Assert.Equal(expectedFine, plan.FineTrimDb, 6);
        Assert.Equal(expectedTotal, plan.TotalLevelDb, 6);
    }

    [Fact]
    public void InputSettingPlanUsesLowerDeviceStepWhenFineTrimCannotReachTarget()
    {
        AudioInputSettingPlan plan = AudioHardware.PlanInputSetting(
            targetTotalDb: -17.3,
            minimumDeviceDb: -96,
            maximumDeviceDb: 0,
            deviceIncrementDb: 6);

        Assert.Equal(-18, plan.DeviceLevelDb, 6);
        Assert.Equal(0, plan.FineTrimDb, 6);
        Assert.Equal(-18, plan.TotalLevelDb, 6);
        Assert.True(plan.TotalLevelDb <= -17.3);
    }

    [Fact]
    public void InputSettingPlanRoundsFineTrimTowardTheSafeSide()
    {
        AudioInputSettingPlan plan = AudioHardware.PlanInputSetting(
            targetTotalDb: -17.24,
            minimumDeviceDb: -96,
            maximumDeviceDb: 0,
            deviceIncrementDb: 2);

        Assert.Equal(-16, plan.DeviceLevelDb, 6);
        Assert.Equal(-1.3, plan.FineTrimDb, 6);
        Assert.Equal(-17.3, plan.TotalLevelDb, 6);
        Assert.True(plan.TotalLevelDb <= -17.24);
    }

    [Fact]
    public void SafeInputSettingCanOnlyMoveDownDuringAWholeSideScan()
    {
        double first = RecordViewModel.HoldSafeInputSetting(double.NaN, -12, -2);
        double quieterPassage = RecordViewModel.HoldSafeInputSetting(first, -12, 1);
        double louderPassage = RecordViewModel.HoldSafeInputSetting(quieterPassage, -12, -4);

        Assert.Equal(-14, first);
        Assert.Equal(-14, quieterPassage);
        Assert.Equal(-16, louderPassage);
    }

    [Fact]
    public void InputMonitorDuplicatesMonoAcrossStereoOutput()
    {
        var source = new ArraySampleProvider([0.25f, -0.5f], channels: 1);
        var mapper = new SoftwareInputMonitor.ChannelMappingSampleProvider(source, outputChannels: 2);
        var output = new float[4];

        int read = mapper.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.Equal([0.25f, 0.25f, -0.5f, -0.5f], output);
    }

    [Fact]
    public void InputMonitorAveragesStereoForMonoOutput()
    {
        var source = new ArraySampleProvider([0.5f, -0.25f, 0.2f, 0.4f], channels: 2);
        var mapper = new SoftwareInputMonitor.ChannelMappingSampleProvider(source, outputChannels: 1);
        var output = new float[2];

        int read = mapper.Read(output, 0, output.Length);

        Assert.Equal(2, read);
        Assert.Equal(0.125f, output[0], 6);
        Assert.Equal(0.3f, output[1], 6);
    }

    [Fact]
    public void NormalizeClampsAndCanonicalizesAdvancedAudioHardwareSettings()
    {
        var settings = new AppSettings
        {
            BufferMs = -20,
            CaptureBufferMs = 900,
            OutputShareMode = "EXCLUSIVE",
            InputShareMode = "unsupported",
            OutputDefaultRole = "COMMUNICATIONS",
            InputDefaultRole = "unsupported",
        };

        AppSettings normalized = AppSettings.Normalize(settings);

        Assert.Equal(3, normalized.BufferMs);
        Assert.Equal(500, normalized.CaptureBufferMs);
        Assert.Equal("exclusive", normalized.OutputShareMode);
        Assert.Equal("shared", normalized.InputShareMode);
        Assert.Equal("communications", normalized.OutputDefaultRole);
        Assert.Equal("console", normalized.InputDefaultRole);
    }

    [Fact]
    public void RestoreDefaultsResetsEveryAdvancedAudioHardwareSetting()
    {
        var settings = new AppSettings
        {
            CaptureBufferMs = 7,
            OutputShareMode = "exclusive",
            InputShareMode = "exclusive",
            OutputEventSync = false,
            InputEventSync = false,
            OutputDefaultRole = "communications",
            InputDefaultRole = "multimedia",
        };

        settings.RestoreDefaults();

        Assert.Equal(60, settings.BufferMs);
        Assert.Equal(100, settings.CaptureBufferMs);
        Assert.Equal("shared", settings.OutputShareMode);
        Assert.Equal("shared", settings.InputShareMode);
        Assert.True(settings.OutputEventSync);
        Assert.True(settings.InputEventSync);
        Assert.Equal("multimedia", settings.OutputDefaultRole);
        Assert.Equal("console", settings.InputDefaultRole);
    }

    private sealed class ArraySampleProvider(float[] samples, int channels) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
