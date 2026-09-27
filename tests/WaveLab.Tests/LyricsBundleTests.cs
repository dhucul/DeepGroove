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
                Assert.True(((Button)window.FindName("vocalsButton")).IsEnabled);
                Assert.Contains("built into", ((TextBlock)window.FindName("engineLabel")).Text);
            });
        });
    }
    [Fact]
    public async Task CancellingVocalExtractionBeforeLaunchLeavesNoWorkingAudio()
    {
        string cache = Path.Combine(_directory, "user");
        var engine = new LyricsEngine(cache, Assets);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.IsolateVocalsAsync(
            [new float[44100]], 44100, 0, 44100, "Source.wav", true,
            new Progress<LyricsProgress>(), new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(cache, "jobs")));
    }

    private static void WriteWorkerResult(IReadOnlyList<string> arguments, LyricsTranscript? transcript = null)
    {
        var args = arguments.ToList();
        string input = args[args.IndexOf("--input") + 1];
        string output = args[args.IndexOf("--output") + 1];
        bool isolated = args.Contains("--isolate") || args.Contains("--vocals-only");
        if (isolated && !args.Contains("--vocals-input"))
        {
            var audio = WavCodec.Load(input);
            var stem = new AudioDocument([Enumerable.Repeat(.25f, audio.Length).ToArray(), new float[audio.Length]], 44100, 32);
            WavCodec.Save(stem, Path.Combine(Path.GetDirectoryName(output)!, "vocals.wav"), 32);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(transcript ?? new LyricsTranscript
            { IsolatedVocals = isolated, Language = "en" }, LyricsTranscript.JsonOptions));
    }

    [Fact]
    public async Task TranscriptionReusesVocalsAcrossSettingsAndPassageRetriesButNotEdits()
    {
        var jobs = new List<List<string>>();
        var engine = new LyricsEngine(Path.Combine(_directory, "cache-test"), Assets, (args, token) =>
        {
            jobs.Add(args.ToList());
            WriteWorkerResult(args);
            if (args.Contains("--vocals-input"))
            {
                var list = args.ToList();
                var input = WavCodec.Load(list[list.IndexOf("--input") + 1]);
                var cached = WavCodec.Load(list[list.IndexOf("--vocals-input") + 1]);
                Assert.Equal(input.Length, cached.Length);
                Assert.Equal(.25f, cached.Channels[0][0]);
            }
            return Task.CompletedTask;
        });
        float[][] source = [new float[4 * 44100]];
        var cache = new LyricsVocalCache();
        var options = new LyricsOptions(true, false, false, "large-v3", "en", "", false, true);
        var progress = new Progress<LyricsProgress>();
        await engine.TranscribeAsync(source, 44100, 0, source[0].Length, "song", 0, options, progress, default, cache);
        Assert.Contains("--retry-unclear", jobs[0]);
        Assert.DoesNotContain("--compare", jobs[0]);
        await engine.TranscribeAsync(source, 44100, 44100, 44100, "song", 0,
            options with { Language = "fr", Hints = "name", Model = "turbo", RetryUnclear = false, CompareOriginal = true },
            progress, default, cache);
        Assert.Contains("--vocals-input", jobs[1]);
        Assert.Contains("--compare", jobs[1]);
        Assert.Contains("--no-retry-unclear", jobs[1]);
        var exported = await engine.IsolateVocalsAsync(source, 44100, 0, source[0].Length, "song", false,
            progress, default, cache, 0);
        Assert.Equal(2, jobs.Count); // Opening the cached audio does not launch a worker.
        Assert.Equal(source[0].Length, exported.Length);
        await engine.TranscribeAsync(source, 44100, 0, source[0].Length, "song", 1, options, progress, default, cache);
        Assert.DoesNotContain("--vocals-input", jobs[2]);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_directory, "cache-test", "jobs")));
    }

    [Fact]
    public async Task CancellationAfterWorkerOutputDoesNotPublishAVocalCache()
    {
        using var cancellation = new CancellationTokenSource();
        var cache = new LyricsVocalCache();
        var jobs = new List<List<string>>();
        var engine = new LyricsEngine(Path.Combine(_directory, "cancel-cache"), Assets, (args, token) =>
        {
            jobs.Add(args.ToList());
            WriteWorkerResult(args);
            if (jobs.Count == 1) cancellation.Cancel();
            return Task.CompletedTask;
        });
        float[][] source = [new float[44100]];
        var options = new LyricsOptions(true, false, false, "large-v3", "en", "", false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranscribeAsync(source, 44100, 0, 44100,
            "song", 0, options, new Progress<LyricsProgress>(), cancellation.Token, cache));
        await engine.TranscribeAsync(source, 44100, 0, 44100, "song", 0, options, new Progress<LyricsProgress>(), default, cache);
        Assert.All(jobs, job => Assert.DoesNotContain("--vocals-input", job));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_directory, "cancel-cache", "jobs")));
    }

    [Fact]
    public void LineRetryRunsWithContextAndLeavesEditsUntouchedUntilAccepted()
    {
        Wpf.Run(() =>
        {
            var jobs = new List<List<string>>();
            var engine = new LyricsEngine(Path.Combine(_directory, "retry"), Assets, (args, token) =>
            {
                jobs.Add(args.ToList());
                WriteWorkerResult(args, new LyricsTranscript { Language = "en", Lines = [new LyricsLine
                {
                    Start = 1, End = 6, Text = "context better words neighbor", ModelText = "context better words neighbor",
                    Words = [new(1, 2, " context", .9), new(3, 4, " better", .9),
                        new(4, 5, " words", .9), new(5, 6, " neighbor", .9)]
                }] });
                return Task.CompletedTask;
            });
            var target = new LyricsLine { Start = 5, End = 7, Text = "my correction" };
            var next = new LyricsLine { Start = 7, End = 9, Text = "other correction" };
            var transcript = new LyricsTranscript { RangeEnd = 10, Language = "en", Lines = [target, next] };
            var doc = new DocumentViewModel(new AudioDocument([new float[10 * 44100]], 44100, 32))
            {
                LyricsTranscript = transcript,
                LyricsSettings = new LyricsOptions(false, false, true, "large-v3", null, "", false, true),
            };
            Wpf.Show(new LyricsDialog(doc, engine: engine), window =>
            {
                ((DataGrid)window.FindName("linesGrid")).SelectedItem = target;
                ((Button)window.FindName("retryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = Environment.TickCount64 + 5000;
                while (((Button)window.FindName("cancelButton")).IsEnabled && Environment.TickCount64 < deadline)
                {
                    Wpf.Pump();
                    Thread.Sleep(5);
                }
                Assert.False(((Button)window.FindName("cancelButton")).IsEnabled);
                Assert.Same(transcript, doc.LyricsTranscript);
                Assert.Equal("my correction", target.Text);
                Assert.Equal("other correction", next.Text);
                Assert.Equal("better words", target.RetryText);
                window.Width = 920;
                window.Height = 740;
                window.UpdateLayout();
                Assert.True(((DataGrid)window.FindName("linesGrid")).ActualHeight > 80);
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_RETRY_RENDER");
                if (!string.IsNullOrEmpty(renderPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(920, 740, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(renderPath);
                    png.Save(stream);
                }
                Assert.Contains("--no-retry-unclear", Assert.Single(jobs));
                Assert.DoesNotContain("--compare", jobs[0]);
                Assert.Equal("en", jobs[0][jobs[0].IndexOf("--language") + 1]);
                Assert.True(doc.LyricsSettings!.RetryUnclear); // Temporary retry choices are not saved.
                ((Button)window.FindName("acceptRetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("better words", target.Text);
                Assert.Equal("other correction", next.Text);
            });
            doc.Unhook();
        });
    }

    [Theory]
    [InlineData("new suggestion")]
    [InlineData("accepted wording")]
    [InlineData("")]
    public void FurtherRetriesPreserveRestorationAndReplaceOrClearOnlyPendingSuggestions(string nextReading)
    {
        Wpf.Run(() =>
        {
            int requests = 0;
            var engine = new LyricsEngine(Path.Combine(_directory, "retry-history"), Assets, (args, token) =>
            {
                string reading = ++requests == 1 ? "accepted wording" : nextReading;
                var result = new LyricsTranscript { Language = "en" };
                if (reading.Length > 0)
                    result.Lines.Add(new LyricsLine { Start = 3, End = 5, Text = reading,
                        Words = [new(3, 5, " " + reading, .9)] });
                WriteWorkerResult(args, result);
                return Task.CompletedTask;
            });
            var target = new LyricsLine { Start = 5, End = 7, Text = "my original correction" };
            var neighbor = new LyricsLine { Start = 7, End = 9, Text = "another correction" };
            var transcript = new LyricsTranscript { RangeEnd = 10, Language = "en", Lines = [target, neighbor] };
            var doc = new DocumentViewModel(new AudioDocument([new float[10 * 44100]], 44100, 32))
            {
                LyricsTranscript = transcript,
                LyricsSettings = new LyricsOptions(false, false, false, "large-v3", "en", "", false, false),
            };
            Wpf.Show(new LyricsDialog(doc, engine: engine), window =>
            {
                ((DataGrid)window.FindName("linesGrid")).SelectedItem = target;
                var accept = (Button)window.FindName("acceptRetryButton");
                var restore = (Button)window.FindName("restoreRetryButton");
                RetryAndWait(window);
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("accepted wording", target.Text);
                Assert.Equal("my original correction", target.PreviousRetryText);
                // A second, unaccepted suggestion must not erase the first restore point.
                target.RetryText = "outdated suggestion";
                RetryAndWait(window);
                Assert.Same(transcript, doc.LyricsTranscript);
                Assert.Equal("accepted wording", target.Text);
                Assert.Equal("my original correction", target.PreviousRetryText);
                bool hasSuggestion = nextReading == "new suggestion";
                Assert.Equal(hasSuggestion ? nextReading : "", target.RetryText);
                Assert.Equal(hasSuggestion, accept.IsEnabled);
                Assert.True(restore.IsEnabled);
                window.Width = 920;
                window.Height = 740;
                window.UpdateLayout();
                Assert.True(((DataGrid)window.FindName("linesGrid")).ActualHeight > 80);
                Assert.True(restore.TransformToAncestor(window).Transform(new Point(restore.ActualWidth, 0)).X < window.ActualWidth);
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_RETRY_FIX_RENDER");
                if (hasSuggestion && !string.IsNullOrEmpty(renderPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(920, 740, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(renderPath);
                    png.Save(stream);
                }
                Assert.Equal("another correction", neighbor.Text);
                Assert.DoesNotContain("retry_text", transcript.Export(".json"));
                Assert.DoesNotContain("outdated suggestion", ((TextBlock)window.FindName("retryLabel")).Text);
            });
            // Both independent states must survive closing and reopening the dialog.
            Wpf.Show(new LyricsDialog(doc, engine: engine), window =>
            {
                ((DataGrid)window.FindName("linesGrid")).SelectedItem = target;
                var restore = (Button)window.FindName("restoreRetryButton");
                Assert.True(restore.IsEnabled);
                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("my original correction", target.Text);
                Assert.Null(target.PreviousRetryText);
                Assert.Equal(nextReading == "new suggestion" ? nextReading : "", target.RetryText);
                Assert.Equal("another correction", neighbor.Text);
                if (nextReading == "new suggestion")
                {
                    ((Button)window.FindName("acceptRetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("new suggestion", target.Text);
                    Assert.Equal("my original correction", target.PreviousRetryText);
                    Assert.Empty(target.RetryText);
                }
            });
            Assert.Equal(2, requests);
            doc.Unhook();
        });
    }

    [Fact]
    public void CancellingAFurtherRetryKeepsThePendingSuggestionAndRestorePoint()
    {
        Wpf.Run(() =>
        {
            bool started = false;
            bool playing = false;
            int playCalls = 0;
            var engine = new LyricsEngine(Path.Combine(_directory, "cancel-retry"), Assets, async (args, token) =>
            {
                started = true;
                await Task.Delay(Timeout.Infinite, token);
            });
            var target = new LyricsLine { Start = 5, End = 7, Text = "accepted wording",
                RetryText = "pending suggestion", PreviousRetryText = "my correction" };
            var doc = new DocumentViewModel(new AudioDocument([new float[10 * 44100]], 44100, 32))
            {
                LyricsTranscript = new LyricsTranscript { RangeEnd = 10, Language = "en", Lines = [target] },
                LyricsSettings = new LyricsOptions(false, false, false, "large-v3", "en", "", false, false),
            };
            Wpf.Show(new LyricsDialog(doc, (audio, loop) =>
            {
                Assert.Equal(10 * 44100, audio.Length);
                playCalls++;
                playing = true;
                return true;
            }, () => playing = false, engine), window =>
            {
                ((DataGrid)window.FindName("linesGrid")).SelectedItem = target;
                var playAll = (Button)window.FindName("playAllButton");
                playAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(playing);
                ((Button)window.FindName("retryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => started);
                Assert.True(playing); // Starting a worker must not interrupt the recording.
                Assert.True(playAll.IsEnabled);
                ((Button)window.FindName("stopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(playing);
                playAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(playing); // Full playback also starts while the worker is busy.
                Assert.Equal(2, playCalls);
                Assert.False(((Button)window.FindName("restoreRetryButton")).IsEnabled);
                var grid = (DataGrid)window.FindName("linesGrid");
                grid.ScrollIntoView(target);
                grid.UpdateLayout();
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(target));
                var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
                menu.PlacementTarget = row;
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                var edit = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
                Assert.False(edit.IsEnabled);
                edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Wpf.Pump();
                Assert.IsType<TextBlock>(((DataGridTextColumn)window.FindName("wordsColumn")).GetCellContent(target));
                var clear = (Button)window.FindName("clearButton");
                Assert.False(clear.IsEnabled);
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(target, Assert.Single(doc.LyricsTranscript!.Lines));
                ((Button)window.FindName("cancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((Button)window.FindName("retryButton")).IsEnabled);
                Assert.Equal("accepted wording", target.Text);
                Assert.Equal("pending suggestion", target.RetryText);
                Assert.Equal("my correction", target.PreviousRetryText);
                Assert.True(((Button)window.FindName("acceptRetryButton")).IsEnabled);
                Assert.True(((Button)window.FindName("restoreRetryButton")).IsEnabled);
                Assert.Contains("Cancelled", ((TextBlock)window.FindName("statusLabel")).Text);
                Assert.True(clear.IsEnabled);
                Assert.True(playing); // Cancelling inference does not stop audio playback.
            });
            Assert.False(playing);
            doc.Unhook();
        });
    }

    private static void RetryAndWait(Window window)
    {
        ((Button)window.FindName("retryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !((Button)window.FindName("cancelButton")).IsEnabled);
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!complete() && Environment.TickCount64 < deadline)
        {
            Wpf.Pump();
            Thread.Sleep(5);
        }
        Assert.True(complete(), "The lyrics operation must finish without blocking the dialog.");
    }

}
