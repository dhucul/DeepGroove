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
}
