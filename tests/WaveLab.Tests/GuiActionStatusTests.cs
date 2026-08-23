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

    /// <summary>
    /// The Edit History panel is a window of its own, so the progress overlay that stops every other
    /// route to a document does not cover it. The tools commit against a length check — a
    /// same-length jump would slip past it and splice a result computed from audio that is no longer
    /// there — so the history must refuse to move while an operation owns the document.
    /// </summary>
    [Fact]
    public void TheHistoryCannotBeMovedWhileAnOperationOwnsTheDocument()
    {
        Exception? failure = null;
        bool allowedWhenIdle = false, allowedWhenBusy = false, allowedAfterwards = false;
        int positionAfterBlockedJump = -1, stepsAfterBlockedTruncate = -1;
        string? status = null;

        var thread = new Thread(() =>
        {
            MainViewModel? viewModel = null;
            try
            {
                viewModel = new MainViewModel();
                var document = new AudioDocument([[0.5f, -0.25f, 0.75f, 0.25f]], 48_000, 32)
                {
                    Title = "Guard test.wav",
                };
                viewModel.AddDocument(document);
                var tab = viewModel.AudioDocuments.Single();
                document.ReplaceRange(0, 2, [[0.1f, 0.1f]], "Gain +3.0 dB");
                document.ReplaceRange(0, 2, [[0.2f, 0.2f]], "Reverse");

                allowedWhenIdle = viewModel.CanMoveHistory(tab);

                viewModel.SetDocumentOperationRunning(true);
                allowedWhenBusy = viewModel.CanMoveHistory(tab);
                viewModel.JumpToHistoryPosition(tab, 0);
                viewModel.TruncateHistoryFrom(tab, 0);
                positionAfterBlockedJump = document.HistoryPosition;
                stepsAfterBlockedTruncate = document.HistoryCount;

                viewModel.SetDocumentOperationRunning(false);
                allowedAfterwards = viewModel.CanMoveHistory(tab);
                viewModel.JumpToHistoryPosition(tab, 0);
                status = viewModel.ActionStatusText;
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "History guard test timed out.");
        Assert.Null(failure);
        Assert.True(allowedWhenIdle, "the history refused to move with nothing running.");
        Assert.False(allowedWhenBusy, "the history offered to move while an operation owned the document.");
        Assert.Equal(2, positionAfterBlockedJump);
        Assert.Equal(2, stepsAfterBlockedTruncate);
        Assert.True(allowedAfterwards, "the history stayed locked after the operation finished.");
        Assert.Contains("Stepped back 2 steps", status);
    }

    /// <summary>
    /// A stale index is absorbed and reported rather than thrown, because the panel is modeless and
    /// the memory budget can renumber the timeline underneath it between a click and its handler.
    /// </summary>
    [Fact]
    public void AStepThatIsNoLongerInTheHistoryIsReportedRatherThanThrown()
    {
        Exception? failure = null;
        string? status = null;

        var thread = new Thread(() =>
        {
            MainViewModel? viewModel = null;
            try
            {
                viewModel = new MainViewModel();
                var document = new AudioDocument([[0.5f, -0.25f]], 48_000, 32) { Title = "Stale.wav" };
                viewModel.AddDocument(document);
                var tab = viewModel.AudioDocuments.Single();

                viewModel.JumpToHistoryPosition(tab, 40);
                status = viewModel.ActionStatusText;
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Stale history index test timed out.");
        Assert.Null(failure);
        Assert.Contains("no longer in the history", status);
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
