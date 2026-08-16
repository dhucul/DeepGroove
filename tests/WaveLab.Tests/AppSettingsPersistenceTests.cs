using WaveLab.Util;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Round-trips settings through a real <see cref="AppSettings.Save"/> and reload.
/// Testing <see cref="AppSettings.Normalize"/> in memory is not enough: serialization
/// has its own rules, and a value that is merely unusual in memory — a non-finite
/// double, say — fails the write of the entire file rather than the one field.
/// </summary>
[Collection(AppSettingsCollection.Name)]
public sealed class AppSettingsPersistenceTests : IDisposable
{
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public AppSettingsPersistenceTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try
        {
            if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true);
        }
        catch
        {
            // A locked temp directory must not fail the run.
        }
    }

    [Fact]
    public void CalibrationWithoutAnAppliedSettingStillSaves()
    {
        AppSettings settings = AppSettings.Instance;
        settings.InputCalibrations["device"] =
            new AppSettings.InputCalibrationInfo(-2.5, -5.4, DateTime.UtcNow);

        Assert.True(settings.Save(), settings.LastSaveError);

        AppSettings.AppDataDir = _sandbox; // drops the cached instance
        AppSettings reloaded = AppSettings.Instance;
        AppSettings.InputCalibrationInfo entry = reloaded.InputCalibrations["device"];
        Assert.False(entry.HasAppliedSetting);
        Assert.Null(entry.DeviceLevelDb);
        Assert.Equal(-2.5, entry.SuggestedGainDb, 6);
    }

    [Fact]
    public void CalibrationWithAnAppliedSettingRoundTrips()
    {
        AppSettings settings = AppSettings.Instance;
        settings.InputCalibrations["device"] =
            new AppSettings.InputCalibrationInfo(-2.5, -5.4, DateTime.UtcNow, -12, -0.4, -12.4);
        settings.RecordingTargetCeilingDb = -10;

        Assert.True(settings.Save(), settings.LastSaveError);

        AppSettings.AppDataDir = _sandbox;
        AppSettings reloaded = AppSettings.Instance;
        AppSettings.InputCalibrationInfo entry = reloaded.InputCalibrations["device"];
        Assert.True(entry.HasAppliedSetting);
        Assert.Equal(-12, entry.DeviceLevelDb!.Value, 6);
        Assert.Equal(-0.4, entry.FineTrimDb!.Value, 6);
        Assert.Equal(-12.4, entry.TotalLevelDb!.Value, 6);
        Assert.Equal(-10, reloaded.RecordingTargetCeilingDb, 6);
    }

    [Fact]
    public void SavingAfterEveryRecordingSettingIsTouchedProducesReadableJson()
    {
        // A blanket guard for this whole group: any of these becoming non-finite
        // would break persistence for every unrelated setting too.
        AppSettings settings = AppSettings.Instance;
        settings.InputDeviceId = "{0.0.1.00000000}.{test}";
        settings.RecordingTargetCeilingDb = -6;
        settings.InputCalibrations["a"] =
            new AppSettings.InputCalibrationInfo(0, -4.05, DateTime.UtcNow);
        settings.InputCalibrations["b"] =
            new AppSettings.InputCalibrationInfo(-7, -4.63, DateTime.UtcNow, -9, -0.2, -9.2);

        Assert.True(settings.Save(), settings.LastSaveError);
        Assert.Null(settings.LastSaveError);

        AppSettings.AppDataDir = _sandbox;
        AppSettings reloaded = AppSettings.Instance;
        Assert.Equal("{0.0.1.00000000}.{test}", reloaded.InputDeviceId);
        Assert.Equal(2, reloaded.InputCalibrations.Count);
    }
}
