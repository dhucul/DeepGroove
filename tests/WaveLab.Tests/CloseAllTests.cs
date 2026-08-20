using System.Threading;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Close All Files, which a CD import is the reason for: one disc opens a tab per track.
/// </summary>
/// <remarks>
/// In the shared UI thread rather than a bare STA one, because closing a tab awaits a marker flush
/// and the continuation has to come back to the thread that owns <c>Documents</c> — without a
/// dispatcher it resumes on the pool and mutates an <c>ObservableCollection</c> from the wrong
/// thread. In the app-settings collection because <c>MainViewModel</c> reads <c>AppSettings</c>,
/// whose root is static.
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class CloseAllTests : IDisposable
{
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public CloseAllTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    private static AudioDocument Track(int number) =>
        new([new float[4_800], new float[4_800]], 48_000, 16) { Title = $"Audio CD - Track {number:00}" };

    [Fact]
    public void ClosingAllLeavesNoTabsOpen()
    {
        (int remaining, bool canCloseAfterwards) = Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            for (int track = 1; track <= 12; track++) viewModel.AddDocument(Track(track));
            Assert.Equal(12, viewModel.Documents.Count);

            viewModel.CloseAllCommand.Execute(null);
            Wpf.Pump();

            return (viewModel.Documents.Count, viewModel.CloseAllCommand.CanExecute(null));
        });

        Assert.Equal(0, remaining);
        Assert.False(canCloseAfterwards);
    }

    /// <summary>With nothing open there is nothing to close, and the menu item says so.</summary>
    [Fact]
    public void ItIsUnavailableWithNoFilesOpen()
    {
        bool available = Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            return viewModel.CloseAllCommand.CanExecute(null);
        });

        Assert.False(available);
    }

    /// <summary>
    /// Closing a tab still does its real work — the active tab follows the collection down to
    /// nothing rather than being left pointing at a document that has gone.
    /// </summary>
    [Fact]
    public void TheActiveTabIsReleasedRatherThanLeftDangling()
    {
        object? active = Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            for (int track = 1; track <= 3; track++) viewModel.AddDocument(Track(track));

            viewModel.CloseAllCommand.Execute(null);
            Wpf.Pump();

            return (object?)viewModel.ActiveTab;
        });

        Assert.Null(active);
    }
}
