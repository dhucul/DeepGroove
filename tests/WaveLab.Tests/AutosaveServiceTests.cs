using System.Text.Json;
using WaveLab.Audio;
using WaveLab.Util;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class AutosaveServiceTests : IDisposable
{
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.AutosaveTests.{Guid.NewGuid():N}");

    public AutosaveServiceTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AutosaveService.ClearAll();
        AppSettings.AppDataDir = _originalAppDataDir;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void RecoveryManifestCarriesPathlessMarkersAndRegions()
    {
        var document = new AudioDocument([new float[400]], 48_000, 32)
        {
            Title = "Generated.wav",
        };
        var markers = new[] { new Marker { Name = "Drop", Position = 123 } };
        var regions = new[]
        {
            new NamedRegion { Name = "Song", Start = 100, End = 300, CdTrackOrder = 2 },
        };

        int saved = AutosaveService.RunNow(
        [
            new AutosaveService.DocumentSnapshot(
                document, document.SessionId, markers, regions),
        ]);

        Assert.Equal(1, saved);
        AutosaveService.Entry entry = Assert.Single(
            AutosaveService.GetRecoverable(includeCurrentSession: true));
        Marker marker = Assert.Single(entry.Markers!);
        Assert.Equal(("Drop", 123), (marker.Name, marker.Position));
        NamedRegion region = Assert.Single(entry.Regions!);
        Assert.Equal(("Song", 100, 300, 2),
            (region.Name, region.Start, region.End, region.CdTrackOrder));
    }

    [Fact]
    public void CleanExitClearsOnlyThisProcessesSession()
    {
        string otherSession = Path.Combine(AppSettings.AutosaveDir,
            $"session-2147483646-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherSession);
        string recoveryFile = Path.Combine(otherSession, "other.wav");
        File.WriteAllText(recoveryFile, "owned by another process");
        string manifestPath = Path.Combine(otherSession, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new Dictionary<string, AutosaveService.Entry>
        {
            ["other"] = new()
            {
                AutosaveFile = recoveryFile,
                Title = "Other process",
                SavedAt = DateTime.Now,
            },
        }));

        var own = new AudioDocument([new float[8]], 48_000, 32);
        Assert.Equal(1, AutosaveService.RunNow([(own, own.SessionId)]));
        Assert.True(AutosaveService.ClearAll());

        Assert.True(File.Exists(recoveryFile));
        Assert.True(File.Exists(manifestPath));
        AutosaveService.Entry entry = Assert.Single(AutosaveService.GetRecoverable());
        Assert.Equal("Other process", entry.Title);
    }
}
