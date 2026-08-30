using System.IO;
using System.Windows.Controls;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// Exercises the state transitions around the two recording entry points. The audio engine itself
/// has separate session tests; these tests keep the selected output depth attached to that session.
/// </summary>
[Collection(AppSettingsCollection.Name)]
public sealed class RecordingBitDepthFlowTests : IDisposable
{
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public RecordingBitDepthFlowTests()
    {
        AppSettings.AppDataDir = _sandbox;
    }

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void ArmedDepthCanChangeOnlyWhileNoCaptureIsOwned(
        bool recording,
        bool finalizing,
        bool pending,
        bool expected)
    {
        Assert.Equal(expected,
            MainViewModel.CanChangeRecordArmForState(recording, finalizing, pending));
    }

    [Fact]
    public void ArmedTakeKeepsTheDepthFrozenAtSessionStartAndRefreshesAfterward()
    {
        Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            viewModel.RecordingBitDepthPreference = 16;

            Assert.Equal(16, viewModel.FreezeTransportRecordingBitDepth());

            // Simulate a later preference change before a delayed finalizer completes. The take
            // belongs to the earlier session and must not inherit this new choice.
            viewModel.RecordingBitDepthPreference = 32;
            var take = new AudioDocument([[0.25f]], 48_000, 32);
            viewModel.ApplyTransportRecordingBitDepth(take);

            Assert.Equal(16, take.SourceBitDepth);
            Assert.True(take.Dither16BitOnSave);

            AppSettings.Instance.RecordingBitDepth = 24;
            Assert.True(AppSettings.Instance.Save(), AppSettings.Instance.LastSaveError);
            viewModel.RefreshEngineStatus();
            Assert.Equal(24, viewModel.RecordingBitDepthPreference);
        });
    }

    [Fact]
    public void ToolbarSelectorBindsAllDepthsAndPersistsTheChoice()
    {
        Wpf.Run(() => Wpf.Show(new MainWindow(), window =>
        {
            var selector = Assert.IsType<ComboBox>(window.FindName("armedBitDepthCombo"));
            var viewModel = Assert.IsType<MainViewModel>(window.DataContext);

            Assert.Equal(3, selector.Items.Count);
            Assert.Equal(24, selector.SelectedValue);

            selector.SelectedValue = 32;
            Wpf.Pump();

            Assert.Equal(32, viewModel.RecordingBitDepthPreference);
            Assert.Equal(32, AppSettings.Instance.RecordingBitDepth);
        }));
    }

    [Fact]
    public void PunchBranchLocksAndReleasesTheStandaloneDepthSelector()
    {
        Wpf.Run(() => Wpf.Show(new RecordDialog(punchAvailable: true), window =>
        {
            var selector = Assert.IsType<ComboBox>(window.FindName("bitDepthCombo"));
            var punch = Assert.IsType<CheckBox>(window.FindName("chkPunch"));

            Assert.True(punch.IsChecked);
            Assert.False(selector.IsEnabled);

            punch.IsChecked = false;
            Assert.True(selector.IsEnabled);

            punch.IsChecked = true;
            Assert.False(selector.IsEnabled);
        }));
    }

    [Fact]
    public void DialogDepthSaveFailureRollsBackAndReportsTheReason()
    {
        Directory.CreateDirectory(_sandbox);
        string blockedSettingsRoot = Path.Combine(_sandbox, "not-a-directory");
        File.WriteAllText(blockedSettingsRoot, "blocks Directory.CreateDirectory");
        AppSettings.AppDataDir = blockedSettingsRoot;

        using var viewModel = new RecordViewModel();
        string? reportedError = null;
        viewModel.RecordingBitDepthSaveFailed += error => reportedError = error;

        viewModel.RecordingBitDepth = 32;

        Assert.Equal(24, viewModel.RecordingBitDepth);
        Assert.Equal(24, AppSettings.Instance.RecordingBitDepth);
        Assert.False(string.IsNullOrWhiteSpace(reportedError));
        Assert.Equal(AppSettings.Instance.LastSaveError, reportedError);
    }
}
