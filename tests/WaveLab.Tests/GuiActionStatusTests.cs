using System.Threading;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class GuiActionStatusTests : IDisposable
{
    // MainViewModel loads AppSettings and publishes factory presets, and
    // MasterSectionViewModel enumerates the same preset directory. Both are rooted at
    // AppSettings.AppDataDir, so each test runs against its own temp directory instead
    // of the developer's %AppData%\WaveLab. xUnit builds one instance of this class per
    // test, so the sandbox is created and removed around every test in it.
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public GuiActionStatusTests() => AppSettings.AppDataDir = _sandbox;

    public void Dispose()
    {
        AppSettings.AppDataDir = _originalAppDataDir;
        try
        {
            if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true);
        }
        catch
        {
            // Cleanup of a private temp directory must never fail a test.
        }
    }

    [Fact]
    public void RemoveDcOffsetReportsAppliedAndUndoable()
    {
        Exception? failure = null;
        string? status = null;
        bool canUndo = false;
        var thread = new Thread(() =>
        {
            MainViewModel? viewModel = null;
            try
            {
                viewModel = new MainViewModel();
                var document = new AudioDocument([[0.5f, -0.25f, 0.75f, 0.25f]], 48_000, 32)
                {
                    Title = "Status test.wav",
                };
                viewModel.AddDocument(document);

                viewModel.RemoveDcCommand.Execute(null);

                status = viewModel.ActionStatusText;
                canUndo = document.CanUndo;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                viewModel?.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Applied-action status test timed out.");
        Assert.Null(failure);
        Assert.True(canUndo);
        Assert.Contains("Remove DC Offset applied", status);
        Assert.Contains("Undo available", status);
    }

    [Fact]
    public void EffectAdjustmentReportsLiveRackAndUnchangedSource()
    {
        var master = new MasterSection();
        var viewModel = new MasterSectionViewModel(master);
        string? status = null;
        viewModel.StatusChanged += message => status = message;
        EffectViewModel effect = Assert.Single(viewModel.Effects,
            candidate => candidate.Effect.TypeId == "eq");

        Assert.NotEmpty(effect.Params);
        EffectParamViewModel parameter = effect.Params.First();
        parameter.Value = Math.Min(parameter.Max, parameter.Value + 0.5);

        Assert.Contains(effect.DisplayName, status);
        Assert.Contains("active in rack", status);
        Assert.Contains("source unchanged until render", status);
    }
}
