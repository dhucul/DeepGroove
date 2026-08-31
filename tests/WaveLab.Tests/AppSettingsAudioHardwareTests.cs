using NAudio.Wave;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class AppSettingsAudioHardwareTests
{
    [Theory]
    [InlineData("DS-DAC-10R")]
    [InlineData("KORG DS-DAC-10R Audio")]
    [InlineData("KORG 2ch 1bit Audio Device")]
    [InlineData("KORG 2ch Audio Device")]
    [InlineData("Speakers (KORG 2CH 1BIT AUDIO DEVICE)")]
    public void KorgDsDac10RRecognizesProductAndDriverEndpointNames(string name)
    {
        Assert.True(AudioHardware.IsKorgDsDac10R(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("KORG USB Audio Device Driver")]
    [InlineData("Creative SB/SBX AE DSD ASIO Device")]
    public void KorgDsDac10RRecognitionDoesNotClaimOtherDsdDevices(string? name)
    {
        Assert.False(AudioHardware.IsKorgDsDac10R(name));
    }

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

    [Theory]
    [InlineData(16, 16)]
    [InlineData(24, 24)]
    [InlineData(32, 32)]
    [InlineData(20, 24)]
    [InlineData(0, 24)]
    public void RecordingBitDepthOffersPcm16Pcm24AndFloat32(int requested, int expected)
    {
        var settings = new AppSettings { RecordingBitDepth = requested };

        Assert.Equal(expected, AppSettings.Normalize(settings).RecordingBitDepth);
        Assert.Equal(expected, RecordViewModel.NormalizeRecordingBitDepth(requested));
    }

    [Fact]
    public void ArmedRecordingSelectorOffersEverySupportedDepth()
    {
        Assert.Equal(
            [16, 24, 32],
            RecordViewModel.AvailableRecordingBitDepthChoices.Select(choice => choice.Bits));
        Assert.Equal(
            ["16 BIT", "24 BIT", "32 FLOAT"],
            RecordViewModel.AvailableRecordingBitDepthChoices.Select(choice => choice.ToolbarLabel));
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    public void CompletedTakeUsesTheSelectedBitDepth(int bitDepth, bool ditherOnSave)
    {
        var document = new AudioDocument([[0.1234567f]], 48_000, 32);

        RecordViewModel.ApplyRecordingBitDepth(document, bitDepth);

        Assert.Equal(bitDepth, document.SourceBitDepth);
        Assert.Equal(ditherOnSave, document.Dither16BitOnSave);
    }

    [Fact]
    public void RestoreDefaultsResetsEveryAdvancedAudioHardwareSetting()
    {
        var settings = new AppSettings
        {
            RecordingBitDepth = 16,
            BufferMs = 12,
            CaptureBufferMs = 7,
            OutputShareMode = "exclusive",
            InputShareMode = "exclusive",
            OutputEventSync = false,
            InputEventSync = false,
            OutputDefaultRole = "communications",
            InputDefaultRole = "multimedia",
        };

        settings.RestoreDefaults();

        Assert.Equal(24, settings.RecordingBitDepth);
        Assert.Equal(60, settings.BufferMs);
        Assert.Equal(100, settings.CaptureBufferMs);
        Assert.Equal("shared", settings.OutputShareMode);
        Assert.Equal("shared", settings.InputShareMode);
        Assert.True(settings.OutputEventSync);
        Assert.True(settings.InputEventSync);
        Assert.Equal("multimedia", settings.OutputDefaultRole);
        Assert.Equal("console", settings.InputDefaultRole);
    }

    [Theory]
    // Plan attenuates further than the current trim: drop the trim first.
    [InlineData(0.0, -1.5, true)]
    [InlineData(-1.0, -2.5, true)]
    // Plan relaxes or keeps the trim: the device step is the safe one to move first.
    [InlineData(-2.5, -1.0, false)]
    [InlineData(-1.5, -1.5, false)]
    public void ApplyOrderAttenuatesBeforeItRelaxes(
        double currentFineDb,
        double plannedFineDb,
        bool expectFineFirst)
    {
        var plan = new AudioInputSettingPlan(-12, plannedFineDb, -12 + plannedFineDb);

        Assert.Equal(expectFineFirst, AudioHardware.ApplyFineTrimFirst(currentFineDb, plan));
    }

    [Theory]
    [InlineData(-17.3, 0.5)]
    [InlineData(-6.0, 0.5)]
    [InlineData(-17.24, 2)]
    public void ApplyingThePlanLeavesNothingFurtherToSuggest(double target, double increment)
    {
        AudioInputSettingPlan plan = AudioHardware.PlanInputSetting(target, -96, 0, increment);

        // Never hotter than asked for, and once applied the ratchet has converged.
        Assert.True(plan.TotalLevelDb <= target + 1e-9);
        Assert.Equal(
            plan.TotalLevelDb,
            RecordViewModel.HoldSafeInputSetting(plan.TotalLevelDb, plan.TotalLevelDb, 0),
            6);
    }

    [Fact]
    public void CoarseDriverStepsLeaveAResidualOnTheSafeSide()
    {
        // A 6 dB endpoint step is wider than Fine Trim's 3 dB range, so the plan
        // cannot land exactly on the target — it must undershoot, never overshoot.
        AudioInputSettingPlan plan = AudioHardware.PlanInputSetting(-17.3, -96, 0, 6);

        double residual = plan.TotalLevelDb - (-17.3);
        Assert.True(residual < 0);
        Assert.True(Math.Abs(residual) > 0.05);
    }

    [Fact]
    public void CalibrationMemoryDropsStaleAndInvalidEntries()
    {
        DateTime now = DateTime.UtcNow;
        var settings = new AppSettings
        {
            InputCalibrations = new Dictionary<string, AppSettings.InputCalibrationInfo>
            {
                ["fresh"] = new(-2.5, -5.4, now.AddDays(-3)),
                ["stale"] = new(-2.5, -5.4, now.AddDays(-AppSettings.CalibrationMemoryDays - 1)),
                ["future"] = new(-2.5, -5.4, now.AddDays(3)),
                ["undated"] = new(-2.5, -5.4, default),
                ["notFinite"] = new(double.NaN, -5.4, now),
                ["  "] = new(-2.5, -5.4, now),
            },
        };

        AppSettings normalized = AppSettings.Normalize(settings);

        Assert.Equal(["fresh"], normalized.InputCalibrations.Keys);
    }

    [Fact]
    public void CalibrationMemoryIsCappedToTheMostRecentEntries()
    {
        DateTime now = DateTime.UtcNow;
        var settings = new AppSettings();
        for (int index = 0; index < AppSettings.MaximumRememberedCalibrations + 8; index++)
        {
            settings.InputCalibrations[$"device{index}"] =
                new AppSettings.InputCalibrationInfo(-2.5, -5.4, now.AddDays(-index));
        }

        AppSettings normalized = AppSettings.Normalize(settings);

        Assert.Equal(AppSettings.MaximumRememberedCalibrations, normalized.InputCalibrations.Count);
        Assert.Contains("device0", normalized.InputCalibrations.Keys);
        Assert.DoesNotContain(
            $"device{AppSettings.MaximumRememberedCalibrations}",
            normalized.InputCalibrations.Keys);
    }

    [Fact]
    public void LegacyCalibrationEntriesSurviveNormalizationWithoutAnAppliedSetting()
    {
        var settings = new AppSettings
        {
            InputCalibrations = new Dictionary<string, AppSettings.InputCalibrationInfo>
            {
                // As written before the applied-setting fields existed.
                ["legacy"] = new(-2.5, -5.4, DateTime.UtcNow.AddDays(-1)),
                // Half-written: a device level with no trim cannot be replayed.
                ["partial"] = new(-2.5, -5.4, DateTime.UtcNow, DeviceLevelDb: -12),
                ["applied"] = new(-2.5, -5.4, DateTime.UtcNow, -12, -0.44, -12.44),
            },
        };

        AppSettings normalized = AppSettings.Normalize(settings);

        Assert.False(normalized.InputCalibrations["legacy"].HasAppliedSetting);
        Assert.Null(normalized.InputCalibrations["legacy"].DeviceLevelDb);
        Assert.Equal(-2.5, normalized.InputCalibrations["legacy"].SuggestedGainDb, 6);
        Assert.False(normalized.InputCalibrations["partial"].HasAppliedSetting);

        AppSettings.InputCalibrationInfo applied = normalized.InputCalibrations["applied"];
        Assert.True(applied.HasAppliedSetting);
        // Fine trim is re-normalized to the tenth-dB grid the engine accepts.
        Assert.Equal(-0.4, applied.FineTrimDb!.Value, 6);
        Assert.Equal(
            applied.DeviceLevelDb!.Value + applied.FineTrimDb!.Value, applied.TotalLevelDb!.Value, 6);
    }

    /// <summary>
    /// An NTP correction or a clock set backwards leaves recent entries stamped in the
    /// future. Discarding them would throw away a valid calibration permanently, since
    /// Normalize rewrites the dictionary it filters.
    /// </summary>
    [Fact]
    public void ACalibrationStampedSlightlyAheadOfNowSurvivesAndIsPulledBack()
    {
        var settings = new AppSettings();
        settings.InputCalibrations["skewed"] =
            new AppSettings.InputCalibrationInfo(-2.5, -5.4, DateTime.UtcNow.AddMinutes(90));

        AppSettings normalized = AppSettings.Normalize(settings);

        Assert.True(normalized.InputCalibrations.ContainsKey("skewed"));
        // Pulled back to the scan's "now", so nothing downstream sees a negative age.
        Assert.True(normalized.InputCalibrations["skewed"].CheckedUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void ACalibrationStampedFarInTheFutureIsStillDiscarded()
    {
        var settings = new AppSettings();
        settings.InputCalibrations["bogus"] = new AppSettings.InputCalibrationInfo(
            -2.5, -5.4, DateTime.UtcNow.AddDays(AppSettings.CalibrationClockSkewDays + 2));

        Assert.Empty(AppSettings.Normalize(settings).InputCalibrations);
    }

    [Theory]
    // The landmarks, and any value between them, are all kept as given.
    [InlineData(-3.0, -3.0)]
    [InlineData(-6.0, -6.0)]
    [InlineData(-10.0, -10.0)]
    [InlineData(-4.5, -4.5)]
    [InlineData(-7.5, -7.5)]
    // Below the slider's floor but inside the analyzer's range: honoured, not raised.
    [InlineData(-18.0, -18.0)]
    // Off the half-decibel grid: snapped rather than discarded.
    [InlineData(-7.3, -7.5)]
    [InlineData(-7.2, -7.0)]
    // Outside the analyzer's range: clamped to the nearest end.
    [InlineData(-40.0, -24.0)]
    [InlineData(-0.25, -1.0)]
    [InlineData(3.0, -1.0)]
    // Only a value that is not a number has nothing to clamp, so it falls back.
    [InlineData(double.NaN, AppSettings.DefaultRecordingTargetCeilingDb)]
    [InlineData(double.PositiveInfinity, AppSettings.DefaultRecordingTargetCeilingDb)]
    public void RecordingTargetCeilingIsClampedAndSnappedRatherThanDiscarded(
        double stored,
        double expected)
    {
        var settings = new AppSettings { RecordingTargetCeilingDb = stored };

        Assert.Equal(expected, AppSettings.Normalize(settings).RecordingTargetCeilingDb, 6);
        // Normalize must agree with the helper the dialogs write through.
        Assert.Equal(expected, AppSettings.NormalizeTargetCeilingDb(stored), 6);
    }

    [Fact]
    public void RecordingTargetCeilingLandmarksAreInsideTheAdjustableRange()
    {
        foreach (double landmark in AppSettings.RecordingTargetCeilingLandmarksDb)
        {
            Assert.InRange(
                landmark,
                AppSettings.AdjustableRecordingTargetCeilingFloorDb,
                RecordingLevelAnalyzer.MaximumTargetCeilingDb);
            // A landmark the slider cannot land on would be a mark you can never hit.
            Assert.Equal(landmark, AppSettings.NormalizeTargetCeilingDb(landmark), 6);
        }
    }

    [Fact]
    public void RestoreDefaultsForgetsRememberedInputCalibrationsAndTheTargetCeiling()
    {
        var settings = new AppSettings { RecordingTargetCeilingDb = -3 };
        settings.InputCalibrations["device"] =
            new AppSettings.InputCalibrationInfo(-2.5, -5.4, DateTime.UtcNow);

        settings.RestoreDefaults();

        Assert.Empty(settings.InputCalibrations);
        Assert.Equal(
            AppSettings.DefaultRecordingTargetCeilingDb, settings.RecordingTargetCeilingDb, 6);
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
