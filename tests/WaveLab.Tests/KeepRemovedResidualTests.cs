using System.Reflection;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// "Keep what was removed", driven through the shell's own range-tool runner rather than through
/// the pieces it composes.
/// </summary>
/// <remarks>
/// <para>
/// Reported as: Restore ▸ Remove Clicks &amp; Pops with the box ticked repairs the clicks, says so,
/// and opens no second tab. Every piece underneath was already covered — the difference, the
/// levels, the residual document, the memory ceiling, the caption — and every one of them passed.
/// What nothing tested was the line that calls them, where
/// <c>residualOpened?.Invoke(await CaptureRemovedAsync(...))</c> short-circuited the await along
/// with the invocation whenever the caller passed no callback. Three of the four tools that offer
/// the option pass none.
/// </para>
/// <para>
/// So the callback is deliberately null here: that is the shape that was broken, and a test that
/// passed one would still pass against the bug. Driven by reflection because the runner is private
/// to the window, which is the honest cost of testing a composition rather than its parts.
/// </para>
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class KeepRemovedResidualTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    /// <summary>A tone with clicks planted on it, loud enough that the detector cannot miss them.</summary>
    private static AudioDocument Clicky()
    {
        const int rate = 44_100, length = rate * 4;
        var left = new float[length];
        var right = new float[length];
        for (int i = 0; i < length; i++)
        {
            float sample = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / (double)rate));
            left[i] = sample;
            right[i] = sample;
        }
        for (int click = 1; click <= 20; click++)
        {
            int at = click * (length / 24);
            left[at] += 0.85f;
            right[at] -= 0.85f;
            left[at + 1] -= 0.6f;
            right[at + 1] += 0.6f;
        }
        return new AudioDocument([left, right], rate, 24) { Title = "clicky.wav" };
    }

    [Fact]
    public void ARangeToolThatPassesNoCallbackStillKeepsWhatItRemoved()
    {
        AppSettings.AppDataDir = _sandbox;

        (bool applied, string[] titles) = Wpf.Run(() =>
        {
            var window = new MainWindow();
            var viewModel = (MainViewModel)typeof(MainWindow)
                .GetField("_vm", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;
            viewModel.AddDocument(Clicky());

            MethodInfo runner = typeof(MainWindow)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(method => method.Name == "RunRangeTool" && method.GetParameters().Length == 5);

            int repaired = 0;
            Func<float[][], int, float[][]?> transform = (data, rate) =>
            {
                repaired = Restoration.RemoveClicks(data, rate, 5);
                return repaired > 0 ? data : null;
            };

            // target, keepRemoved: true, residualOpened: null -- the shape the three broken tools use.
            var run = (Task<bool>)runner.Invoke(window,
                ["Remove Clicks", transform, viewModel.ActiveDocument, true, null])!;

            long deadline = Environment.TickCount64 + 30_000;
            while (!run.IsCompleted && Environment.TickCount64 < deadline) Wpf.Pump();
            Assert.True(run.IsCompleted, "the range tool never finished.");
            Assert.True(repaired > 0, "the planted clicks were not detected.");

            return (run.Result, viewModel.Documents.Select(tab => tab.Title).ToArray());
        });

        foreach (string title in titles) output.WriteLine(title);

        Assert.True(applied, "the repair was not applied.");
        Assert.Equal(2, titles.Length);
        Assert.Contains(titles, title => title.Contains("removed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMeasuredWholeFileToolCannotApplyToSameLengthNewerAudio()
    {
        AppSettings.AppDataDir = _sandbox;

        Wpf.Run(() =>
        {
            var window = new MainWindow();
            var viewModel = (MainViewModel)typeof(MainWindow)
                .GetField("_vm", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;
            viewModel.AddDocument(Clicky());
            DocumentViewModel target = viewModel.ActiveDocument!;
            int measuredAt = target.Doc.EditVersion;

            float[][] oneFrame = target.Doc.Channels.Select(_ => new float[1]).ToArray();
            target.Doc.ReplaceRange(0, 1, oneFrame, "Intervening edit");

            MethodInfo runner = typeof(MainWindow)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(method => method.Name == "RunWholeFileTool" &&
                                  method.GetParameters().Length == 6);
            bool transformed = false;
            Func<float[][], int, IProgress<double>, CancellationToken, float[][]?> transform =
                (data, _, _, _) =>
                {
                    transformed = true;
                    return data;
                };

            var run = (Task<bool>)runner.Invoke(window,
                ["Measured repair", null, transform, target, false, measuredAt])!;

            Assert.True(run.IsCompleted);
            Assert.False(run.Result);
            Assert.False(transformed);
        });
    }
}
