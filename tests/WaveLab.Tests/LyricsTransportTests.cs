using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class LyricsTransportTests : IDisposable
{
    private readonly string _originalSettings = AppSettings.AppDataDir;
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
    public LyricsTransportTests() => AppSettings.AppDataDir = _settings;
    public void Dispose()
    {
        AppSettings.AppDataDir = _originalSettings;
        try { Directory.Delete(_settings, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LyricsStopReleasesAnExistingNormalTransport(bool paused)
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), shell =>
        {
            var main = Assert.IsType<MainViewModel>(shell.DataContext);
            main.AddDocument(new AudioDocument([new float[44100]], 44100, 32));
            // Seed the public transport state without opening an audio device. There is
            // deliberately no preview owner: StopPreview alone cannot release this state.
            typeof(PlaybackEngine).GetProperty(paused ? "IsPaused" : "IsPlaying")!
                .SetValue(main.Engine, true);
            bool inspected = false;
            shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = shell.OwnedWindows.OfType<LyricsDialog>().Single();
                try
                {
                    ((Button)dialog.FindName("stopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(main.Engine.IsPlaying);
                    Assert.False(main.Engine.IsPaused);
                    inspected = true;
                }
                finally { dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            typeof(MainWindow).GetMethod("OnLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, [shell, new RoutedEventArgs()]);
            Assert.True(inspected, "The actual shell command must open the lyrics dialog and wire its Stop button.");
            Assert.False(main.IsDocumentOperationRunning);
        }));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VocalExtractionOpensOnlyACompletedResultAndPreservesTheSource(bool completed)
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), shell =>
        {
            var main = Assert.IsType<MainViewModel>(shell.DataContext);
            var original = new AudioDocument([new float[44100]], 44100, 32) { Title = "Original.wav" };
            main.AddDocument(original);
            var sourceTab = main.ActiveDocument!;
            var transcript = new WaveLab.Audio.Transcription.LyricsTranscript();
            sourceTab.LyricsTranscript = transcript;
            var result = new AudioDocument([new float[44100], new float[44100]], 44100, 32)
                { Title = "Original - vocals.wav", RequiresSaveAs = true };
            shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = shell.OwnedWindows.OfType<LyricsDialog>().Single();
                if (completed)
                    typeof(LyricsDialog).GetProperty(nameof(LyricsDialog.IsolatedVocals))!.SetValue(dialog, result);
                dialog.Close();
            }), DispatcherPriority.ApplicationIdle);
            typeof(MainWindow).GetMethod("OnLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, [shell, new RoutedEventArgs()]);
            Assert.Equal(completed ? 2 : 1, main.Documents.Count);
            Assert.Same(transcript, sourceTab.LyricsTranscript);
            Assert.False(original.Dirty);
            Assert.Equal(0, original.EditVersion);
            Assert.False(main.IsDocumentOperationRunning);
            if (completed)
            {
                Assert.Same(result, main.ActiveDocument!.Doc);
                Assert.True(result.Dirty);
                Assert.Null(result.FilePath);
                result.MarkSaved(); // The test shell can close without prompting for this generated file.
            }
        }));
    }

}
