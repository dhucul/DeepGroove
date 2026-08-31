using System.Threading;
using System.Windows.Threading;
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

    [Fact]
    public void DocumentMutatingCommandsAreDisabledWhileAnOperationOwnsTheDocument()
    {
        Exception? failure = null;
        bool[]? enabled = null;
        int versionBefore = -1, versionAfter = -1;

        var thread = new Thread(() =>
        {
            MainViewModel? viewModel = null;
            try
            {
                viewModel = new MainViewModel();
                var document = new AudioDocument([[0.25f, -0.25f]], 48_000, 32);
                viewModel.AddDocument(document);
                viewModel.ActiveDocument!.SelectAll();
                viewModel.SetDocumentOperationRunning(true);

                enabled =
                [
                    viewModel.UndoCommand.CanExecute(null),
                    viewModel.CutCommand.CanExecute(null),
                    viewModel.DeleteCommand.CanExecute(null),
                    viewModel.GainUpCommand.CanExecute(null),
                    viewModel.InterpolateRepairCommand.CanExecute(null),
                    viewModel.ApplyChainCommand.CanExecute(null),
                    viewModel.CloseTabCommand.CanExecute(null),
                ];
                versionBefore = document.EditVersion;
                viewModel.GainUpCommand.Execute(null);
                versionAfter = document.EditVersion;
            }
            catch (Exception ex) { failure = ex; }
            finally { viewModel?.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Document command guard test timed out.");
        Assert.Null(failure);
        Assert.NotNull(enabled);
        Assert.All(enabled!, value => Assert.False(value));
        Assert.Equal(versionBefore, versionAfter);
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
    public void InterpolateRepairRequiresAUsableSelectionAndCommitsThroughTheCommand()
    {
        Exception? failure = null;
        bool withoutSelection = true, wholeFile = true, interior = false, canUndo = false;
        string? status = null;
        float[]? repaired = null;
        var thread = new Thread(() =>
        {
            MainViewModel? viewModel = null;
            try
            {
                viewModel = new MainViewModel();
                var document = new AudioDocument([[0, 100, -100, 3]], 48_000, 32);
                viewModel.AddDocument(document);

                withoutSelection = viewModel.InterpolateRepairCommand.CanExecute(null);
                viewModel.ActiveDocument!.SelectAll();
                wholeFile = viewModel.InterpolateRepairCommand.CanExecute(null);
                viewModel.ActiveDocument.SetSelection(1, 3);
                interior = viewModel.InterpolateRepairCommand.CanExecute(null);
                viewModel.InterpolateRepairCommand.Execute(null);

                repaired = [.. document.Channels[0]];
                canUndo = document.CanUndo;
                status = viewModel.ActionStatusText;
            }
            catch (Exception ex) { failure = ex; }
            finally { viewModel?.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Interpolate command test timed out.");
        Assert.Null(failure);
        Assert.False(withoutSelection);
        Assert.False(wholeFile);
        Assert.True(interior);
        Assert.Equal([0, 1, 2, 3], repaired!);
        Assert.True(canUndo);
        Assert.Contains("Interpolate Repair applied", status);
        Assert.Contains("Undo available", status);
    }

    [Fact]
    public void RoughInterpolateCommandRepairsOnlyTheLocatedDefectAndReleasesItsGuard()
    {
        Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            float[] audio = InterpolationProgram();
            int defect = 48_000;
            for (int i = defect; i < defect + 24; i++) audio[i] += 0.75f;
            var document = new AudioDocument([audio], 48_000, 32);
            viewModel.AddDocument(document);
            DocumentViewModel tab = viewModel.ActiveDocument!;
            tab.SetSelection(24_000, 72_000);

            CompleteOnDispatcher(viewModel.InterpolateRepairAsync());

            Assert.Equal(1, document.HistoryCount);
            Assert.True(tab.SelStart <= defect + 1);
            Assert.True(tab.SelEnd >= defect + 23);
            Assert.True(tab.SelEnd - tab.SelStart < 480);
            Assert.Contains("auto-located", viewModel.ActionStatusText);
            Assert.False(viewModel.IsDocumentOperationRunning);
        });
    }

    [Fact]
    public void RoughInterpolateWithNoDefectZoomsWithoutCreatingAnEdit()
    {
        Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            var document = new AudioDocument([new float[96_000]], 48_000, 32);
            viewModel.AddDocument(document);
            DocumentViewModel tab = viewModel.ActiveDocument!;
            tab.ViewWidthPixels = 800;
            tab.ZoomFull();
            tab.SetSelection(24_000, 72_000);
            double before = tab.SamplesPerPixel;

            CompleteOnDispatcher(viewModel.InterpolateRepairAsync());

            Assert.Equal(0, document.HistoryCount);
            Assert.True(tab.SamplesPerPixel < before);
            Assert.Contains("No short defect was isolated", viewModel.ActionStatusText);
            Assert.False(viewModel.IsDocumentOperationRunning);
        });
    }

    [Fact]
    public void SelectionChangedDuringRoughAnalysisPreventsTheCommitAndReleasesItsGuard()
    {
        Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            float[] audio = InterpolationProgram();
            for (int i = 48_000; i < 48_024; i++) audio[i] += 0.75f;
            var document = new AudioDocument([audio], 48_000, 32);
            viewModel.AddDocument(document);
            DocumentViewModel tab = viewModel.ActiveDocument!;
            tab.SetSelection(24_000, 72_000);

            Task operation = viewModel.InterpolateRepairAsync();
            // The command has reached its first await and the detector owns the old selection. A
            // modeless surface can still move the selection while the main overlay is up.
            tab.SetSelection(30_000, 60_000);
            CompleteOnDispatcher(operation);

            Assert.Equal(0, document.HistoryCount);
            Assert.Equal(30_000, tab.SelStart);
            Assert.Equal(60_000, tab.SelEnd);
            Assert.Contains("source or selection changed", viewModel.ActionStatusText);
            Assert.False(viewModel.IsDocumentOperationRunning);
        });
    }

    [Fact]
    public void CancellationAfterDetectorCompletionStillPreventsTheCommit()
    {
        Wpf.Run(() =>
        {
            using var viewModel = new MainViewModel();
            float[] audio = InterpolationProgram();
            for (int i = 48_000; i < 48_024; i++) audio[i] += 0.75f;
            var document = new AudioDocument([audio], 48_000, 32);
            viewModel.AddDocument(document);
            viewModel.ActiveDocument!.SetSelection(24_000, 72_000);

            Task operation = viewModel.InterpolateRepairAsync();
            // Hold the dispatcher while the worker finishes. Its continuation — including the
            // commit — cannot run yet, which makes this the late-cancel window the old flow missed.
            Thread.Sleep(500);
            viewModel.Progress.Tick();
            OperationProgress visible = Assert.IsType<OperationProgress>(viewModel.Progress.Blocking);
            visible.CancelCommand.Execute(null);
            CompleteOnDispatcher(operation);

            Assert.Equal(0, document.HistoryCount);
            Assert.Contains("cancelled", viewModel.ActionStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.False(viewModel.IsDocumentOperationRunning);
        });
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

    private static float[] InterpolationProgram()
    {
        const int rate = 48_000;
        var audio = new float[rate * 2];
        for (int i = 0; i < audio.Length; i++)
            audio[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / rate)
                             + 0.08 * Math.Sin(2 * Math.PI * 1_731 * i / rate));
        return audio;
    }

    private static void CompleteOnDispatcher(Task task)
    {
        long deadline = Environment.TickCount64 + 10_000;
        while (!task.IsCompleted && Environment.TickCount64 < deadline)
        {
            Wpf.Pump(DispatcherPriority.Background);
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "Interpolation operation did not finish.");
        task.GetAwaiter().GetResult();
    }
}
