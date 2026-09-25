using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using Xunit;

namespace WaveLab.Tests;

public sealed class LyricsPublishTests
{
    [Fact]
    public async Task PublishingKeepsRuntimeAndModelFilesAtTheirDistinctRelativePaths()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "WaveLab.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string target = Path.Combine(root.FullName, "src", "WaveLab", "LyricsBundle.targets");
        string temporary = Path.Combine(Path.GetTempPath(), "WaveLab.PublishTests." + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(temporary, "bundle");
        try
        {
            foreach (string file in new[] { "python/python.exe", "models/hub/test/model.bin", "bundle.json" })
            {
                string path = Path.Combine(bundle, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }
            // Import the real packaging target, replacing only its large download prerequisite.
            // The project name deliberately matches the application's target condition.
            string project = Path.Combine(temporary, "WaveLab.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <PropertyGroup><Configuration>Release</Configuration><LyricsBundleDirectory>{SecurityElement.Escape(bundle)}</LyricsBundleDirectory></PropertyGroup>
                  <Import Project="{SecurityElement.Escape(target)}" />
                  <Target Name="PrepareLyricsBundle" />
                </Project>
                """);
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "msbuild", project, "-target:PublishLyricsBundle", "-getItem:ResolvedFileToPublish", "-verbosity:quiet" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await stderr);
            using var data = JsonDocument.Parse(await stdout);
            string[] paths = data.RootElement.GetProperty("Items").GetProperty("ResolvedFileToPublish")
                .EnumerateArray().Select(i => i.GetProperty("RelativePath").GetString()!.Replace('\\', '/')).Order().ToArray();
            Assert.Equal(new[] { "Transcription/Engine/bundle.json", "Transcription/Engine/models/hub/test/model.bin", "Transcription/Engine/python/python.exe" }, paths);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
