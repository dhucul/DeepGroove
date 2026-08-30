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
    public void FloatRecordingBitDepthRoundTripsAndInvalidValuesReturnTo24Bit()
    {
        AppSettings settings = AppSettings.Instance;
        settings.RecordingBitDepth = 32;
        Assert.True(settings.Save(), settings.LastSaveError);

        AppSettings.AppDataDir = _sandbox;
        Assert.Equal(32, AppSettings.Instance.RecordingBitDepth);

        AppSettings.Instance.RecordingBitDepth = 20;
        Assert.True(AppSettings.Instance.Save(), AppSettings.Instance.LastSaveError);

        AppSettings.AppDataDir = _sandbox;
        Assert.Equal(24, AppSettings.Instance.RecordingBitDepth);
    }

    /// <summary>
    /// The peak ceiling survives a real write and reload. It has to: the command asks once and then
    /// offers the answer back, so a ceiling that does not persist turns Normalize Peak from one
    /// keypress into a retyped number every time.
    /// </summary>
    [Fact]
    public void TheNormalizePeakCeilingRoundTripsAndIsCorrectedOnReload()
    {
        AppSettings settings = AppSettings.Instance;
        settings.NormalizePeakCeilingDb = -6;
        Assert.True(settings.Save(), settings.LastSaveError);

        AppSettings.AppDataDir = _sandbox;                 // drops the cached instance
        Assert.Equal(-6, AppSettings.Instance.NormalizePeakCeilingDb, 6);

        // A ceiling no slider can produce, as a hand-edited file would carry. Corrected on the way
        // back in rather than failing the write of the whole file.
        AppSettings.Instance.NormalizePeakCeilingDb = 400;
        Assert.True(AppSettings.Instance.Save(), AppSettings.Instance.LastSaveError);

        AppSettings.AppDataDir = _sandbox;
        Assert.Equal(AppSettings.MaximumNormalizePeakCeilingDb,
            AppSettings.Instance.NormalizePeakCeilingDb, 6);
    }

    [Theory]
    [InlineData(-0.3, -0.3)]
    [InlineData(-0.34, -0.3)]                                          // snapped to the slider's tenth
    [InlineData(-0.36, -0.4)]
    [InlineData(-500, AppSettings.MinimumNormalizePeakCeilingDb)]
    [InlineData(12, AppSettings.MaximumNormalizePeakCeilingDb)]        // above full scale is not a ceiling
    [InlineData(double.NaN, AppSettings.DefaultNormalizePeakCeilingDb)]
    [InlineData(double.PositiveInfinity, AppSettings.DefaultNormalizePeakCeilingDb)]
    public void TheNormalizePeakCeilingIsClampedAndSnappedRatherThanDiscarded(
        double stored, double expected) =>
        Assert.Equal(expected, AppSettings.NormalizePeakCeiling(stored), 6);

    /// <summary>
    /// The depth ceiling survives a real write and reload, and an out-of-range one is corrected on
    /// the way back in rather than failing the whole file.
    /// </summary>
    [Fact]
    public void TheNoiseDepthCeilingRoundTripsAndIsCorrectedOnReload()
    {
        AppSettings settings = AppSettings.Instance;
        settings.NoiseDepthCeilingDb = 30;
        Assert.True(settings.Save(), settings.LastSaveError);

        AppSettings.AppDataDir = _sandbox;                 // drops the cached instance
        Assert.Equal(30, AppSettings.Instance.NoiseDepthCeilingDb);

        // A value no slider can produce, as a hand-edited file would carry. It is corrected on the
        // way back in rather than failing the write of the whole file.
        AppSettings.Instance.NoiseDepthCeilingDb = 4_000;
        Assert.True(AppSettings.Instance.Save(), AppSettings.Instance.LastSaveError);

        AppSettings.AppDataDir = _sandbox;
        Assert.Equal(WaveLab.Audio.Dsp.Restoration.MaximumNoiseDepthCeilingDb,
            AppSettings.Instance.NoiseDepthCeilingDb);
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

    /// <summary>
    /// Whether a restoration pass keeps what it removed. Remembered because the people who want
    /// it want it for a whole collection, and off by default because it costs a second copy of
    /// the range and nobody should pay that without asking.
    /// </summary>
    [Fact]
    public void KeepingRemovedMaterialIsOffByDefaultAndSurvivesAReload()
    {
        AppSettings settings = AppSettings.Instance;
        Assert.False(settings.KeepRemovedMaterial);

        settings.KeepRemovedMaterial = true;
        Assert.True(settings.Save(), settings.LastSaveError);

        AppSettings.AppDataDir = _sandbox; // drops the cached instance
        Assert.True(AppSettings.Instance.KeepRemovedMaterial);
    }

    [Fact]
    public void RestoreDefaultsPutsThatOptionBack()
    {
        AppSettings settings = AppSettings.Instance;
        settings.KeepRemovedMaterial = true;
        settings.RestoreDefaults();
        Assert.False(settings.KeepRemovedMaterial);
    }

    /// <summary>
    /// Clearing the recent list has to reach the file. A clear that only emptied the list in memory
    /// would look right until the next launch put every path back, which is the failure a user
    /// clearing the list is specifically trying to avoid.
    /// </summary>
    [Fact]
    public void ClearingTheRecentListSurvivesAReload()
    {
        AppSettings settings = AppSettings.Instance;
        Assert.True(settings.AddRecentFile(@"C:\audio\one.wav"), settings.LastSaveError);
        Assert.True(settings.AddRecentFile(@"C:\audio\two.wav"), settings.LastSaveError);
        Assert.Equal(2, settings.RecentFilesSnapshot().Count);

        Assert.True(settings.ClearRecentFiles(), settings.LastSaveError);
        Assert.Empty(settings.RecentFilesSnapshot());

        AppSettings.AppDataDir = _sandbox;                 // drops the cached instance
        Assert.Empty(AppSettings.Instance.RecentFilesSnapshot());

        // Clearing an already-empty list is a no-op rather than a failed write.
        Assert.True(AppSettings.Instance.ClearRecentFiles());
    }
}
