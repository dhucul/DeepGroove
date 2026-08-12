using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Util;
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
}
