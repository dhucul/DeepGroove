using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaveLab.Audio;
using WaveLab.Audio.Transcription;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

public sealed class LyricsTests
{
    private static LyricsTranscript Transcript() => new()
    {
        Lines = [new LyricsLine { Start = 1.25, End = 3.5, Text = "The river calls", ModelText = "The river calls",
            Words = [new LyricsWord(1.25, 2.5, "The river", .9), new LyricsWord(2.5, 3.5, "calls", .8)] }],
    };

    [Fact]
    public void SelectionOffsetsAreAppliedToLinesAndWordsExactlyOnce()
    {
        var result = Transcript();
        result.MapToSource(60, 10);
        Assert.Equal(61.25, result.Lines[0].Start);
        Assert.Equal(63.5, result.Lines[0].End);
        Assert.Equal(61.25, result.Lines[0].Words[0].Start);
        Assert.Equal(70, result.RangeEnd);
        Assert.Contains("[01:01.25]The river calls", result.Export(".lrc"));
        Assert.Contains("00:01:01,250 --> 00:01:03,500", result.Export(".srt"));
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(double.NaN, 3)]
    [InlineData(2, double.PositiveInfinity)]
    [InlineData(2, 2)]
    [InlineData(2, 14)]
    public void MalformedWorkerTimesCannotReachReplay(double start, double end)
    {
        var result = Transcript();
        result.Lines[0].Start = start;
        result.Lines[0].End = end;
        Assert.Throws<InvalidDataException>(() => result.MapToSource(0, 10));
    }

    [Fact]
    public void MalformedWordConfidenceIsRejected()
    {
        var result = Transcript();
        result.Lines[0].Words = [new LyricsWord(1.25, 2.0, "river", double.NaN)];
        Assert.Throws<InvalidDataException>(() => result.MapToSource(0, 10));
    }

    [Fact]
    public void ExportUsesCorrectedTextAndKeepsOriginalWordEvidenceExplicit()
    {
        var result = Transcript();
        result.Lines[0].Text = "The evening\r\nriver calls";
        Assert.Equal("The evening river calls" + Environment.NewLine, result.Export(".txt"));
        Assert.Contains("The evening river calls", result.Export(".srt"));
        Assert.Contains("\"model_text\": \"The river calls\"", result.Export(".json"));
    }

    [Fact]
    public void SubtitleRoundingCarriesAcrossMinuteAndHourBoundariesInEveryLocale()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = new LyricsTranscript { Lines = [new() { Start = 3599.9999, End = 3601, Text = "hello" }] };
            Assert.Contains("01:00:00,000 --> 01:00:01,000", result.Export(".srt"));
            Assert.Contains("[60:00.00]hello", result.Export(".lrc"));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void WorkingAudioPreservesStereoAndSelectionDurationWithoutChangingTheSource(int rate)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            var left = Enumerable.Range(0, rate).Select(i => (float)(.3 * Math.Sin(i * 2 * Math.PI * 440 / rate))).ToArray();
            var right = left.Select(x => -x).ToArray();
            var saved = left.ToArray();
            LyricsAudio.Write([left, right], rate, rate / 4, rate / 2, path, CancellationToken.None);
            var audio = WavCodec.Load(path);
            Assert.Equal(44100, audio.SampleRate);
            Assert.Equal(2, audio.ChannelCount);
            Assert.InRange(audio.Length, 22040, 22060);
            Assert.True(audio.Channels[0].Max() > .25f);
            Assert.All(Enumerable.Range(100, 500), i => Assert.InRange(Math.Abs(audio.Channels[0][i] + audio.Channels[1][i]), 0, 1e-6f));
            Assert.Equal(saved, left);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WorkingAudioRejectsInvalidRangesAndSanitizesNonFiniteSamples()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            Assert.Throws<ArgumentException>(() => LyricsAudio.Write([new float[50]], 44100, 30, 30, path, default));
            LyricsAudio.Write([[float.NaN, float.PositiveInfinity, .5f]], 44100, 0, 3, path, default);
            Assert.Equal(new float[] { 0, 0, .5f }, WavCodec.Load(path).Channels[0]);
            var token = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => LyricsAudio.Write([new float[100]], 44100, 0, 100, path, token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancellationTerminatesTheWorkerAndReturnsPromptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LyricsEngine.RunProcessAsync(powershell,
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"], new Dictionary<string, string>(),
            new Progress<LyricsProgress>(), cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task IndeterminateProgressStillReportsItsMessage()
    {
        var messages = new List<LyricsProgress>();
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        await LyricsEngine.RunProcessAsync(powershell,
            ["-NoProfile", "-NonInteractive", "-Command", "Write-Output '{\"message\":\"Loading model\",\"fraction\":null}'"],
            new Dictionary<string, string>(), new InlineProgress(messages.Add), default, jsonProgress: true);
        Assert.Equal("Loading model", Assert.Single(messages).Message);
        Assert.Null(messages[0].Fraction);
    }

    private sealed class InlineProgress(Action<LyricsProgress> report) : IProgress<LyricsProgress>
    {
        public void Report(LyricsProgress value) => report(value);
    }

    [Fact]
    public void DialogRetainsEditsAndReplaysOnlyTheSelectedLineFromItsOriginalTime()
    {
        Wpf.Run(() =>
        {
            var source = new AudioDocument([new float[5 * 44100]], 44100, 32);
            var doc = new DocumentViewModel(source) { LyricsTranscript = Transcript() };
            doc.LyricsTranscript.SourceEditVersion = source.EditVersion;
            int replayLength = 0;
            bool stopped = false;
            var dialog = new LyricsDialog(doc, (audio, loop) => { replayLength = audio.Length; return true; }, () => stopped = true);
            Wpf.Show(dialog, window =>
            {
                window.Width = 920;
                window.Height = 740;
                window.UpdateLayout();
                Wpf.Pump();
                var grid = (DataGrid)window.FindName("linesGrid");
                grid.SelectedIndex = 0;
                var play = (Button)window.FindName("playButton");
                Assert.True(play.IsEnabled);
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.InRange(replayLength, (int)(2.59 * 44100), (int)(2.61 * 44100));
                doc.LyricsTranscript.Lines[0].Text = "A corrected line";
                Assert.True(grid.ActualHeight > 80, "The transcript needs a usable reading area at minimum size.");
                Assert.True(grid.Columns.Sum(c => c.ActualWidth) <= grid.ActualWidth,
                    "Time, words, and review status must all fit without horizontal scrolling.");
                string? renderPath = Environment.GetEnvironmentVariable("WAVELAB_LYRICS_RENDER");
                if (!string.IsNullOrEmpty(renderPath))
                {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(renderPath);
                    png.Save(stream);
                }
            });
            Assert.True(stopped);
            Assert.Equal("A corrected line", doc.LyricsTranscript.Lines[0].Text);
            Assert.False(source.Dirty);
        });
    }

    [Fact]
    public void AudioEditsDisableReplayOfStaleLyricsWithoutDiscardingCorrections()
    {
        Wpf.Run(() =>
        {
            var doc = new DocumentViewModel(new AudioDocument([new float[44100]], 44100, 32))
            { LyricsTranscript = Transcript() };
            doc.LyricsTranscript.SourceEditVersion = -1;
            Wpf.Show(new LyricsDialog(doc, (_, _) => throw new Exception("Stale audio must never play")), window =>
            {
                ((DataGrid)window.FindName("linesGrid")).SelectedIndex = 0;
                Assert.False(((Button)window.FindName("playButton")).IsEnabled);
                Assert.Contains("audio has changed", ((TextBlock)window.FindName("statusLabel")).Text);
            });
            Assert.Equal("The river calls", doc.LyricsTranscript.Lines[0].Text);
        });
    }
}
