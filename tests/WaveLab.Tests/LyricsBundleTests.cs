using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WaveLab.Audio;
using WaveLab.Audio.Transcription;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

public sealed class LyricsBundleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WaveLab.BundleTests." + Guid.NewGuid().ToString("N"));
    private string Assets => Path.Combine(_directory, "Transcription");
    private string Bundle => Path.Combine(Assets, "Engine");
    public LyricsBundleTests()
    {
        Directory.CreateDirectory(Assets);
        File.WriteAllText(Path.Combine(Assets, "requirements.txt"), "test requirements");
        var files = new Dictionary<string, long>();
        foreach (string relative in new[] { "python/python.exe", "python/python311.dll", "models/model.bin" })
        {
            string path = Path.Combine(Bundle, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
            files.Add(relative, 3);
        }
        File.WriteAllText(Path.Combine(Bundle, "bundle.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1,
            requirements_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Assets, "requirements.txt")))),
            required_files = files,
        }));
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ACompleteApplicationIsReadyWithoutAnyPerUserSetup()
    {
        string userData = Path.Combine(_directory, "unused-user-data");
        var engine = new LyricsEngine(userData, Assets);
        Assert.True(engine.IsReady);
        Assert.False(Directory.Exists(userData));
        Assert.Equal("1", engine.EnvironmentVariables()["HF_HUB_OFFLINE"]);
        Assert.Equal(Path.Combine(Bundle, "models"), engine.EnvironmentVariables()["HF_HOME"]);
    }

    [Fact]
    public void RelocatingTheProgramDoesNotDependOnAbsoluteSetupPaths()
    {
        string relocated = Path.Combine(_directory, "another folder");
        Directory.Move(Assets, relocated);
        Assert.True(new LyricsEngine(Path.Combine(_directory, "user"), relocated).IsReady);
    }

    [Theory]
    [InlineData("python/python.exe")]
    [InlineData("models/model.bin")]
    public void MissingOrTruncatedPayloadCannotBeReportedReady(string relative)
    {
        string file = Path.Combine(Bundle, relative);
        File.WriteAllBytes(file, [1]);
        Assert.False(new LyricsEngine(assets: Assets).IsReady);
        File.Delete(file);
        Assert.False(new LyricsEngine(assets: Assets).IsReady);
    }

    [Fact]
    public void AStaleBundleIsRejectedAfterDependencyChanges()
    {
        File.AppendAllText(Path.Combine(Assets, "requirements.txt"), "\nnew dependency");
        Assert.False(new LyricsEngine(assets: Assets).IsReady);
    }

    [Fact]
    public void TheDialogOffersTranscribeImmediatelyAndHasNoSetupButton()
    {
        Wpf.Run(() =>
        {
            var document = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32));
            Wpf.Show(new LyricsDialog(document, engine: new LyricsEngine(assets: Assets)), window =>
            {
                Assert.Null(window.FindName("setupButton"));
                Assert.True(((Button)window.FindName("transcribeButton")).IsEnabled);
                Assert.Contains("built into", ((TextBlock)window.FindName("engineLabel")).Text);
            });
        });
    }
}
