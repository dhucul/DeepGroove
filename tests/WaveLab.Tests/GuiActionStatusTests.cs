using System.Threading;
using WaveLab.Audio;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class GuiActionStatusTests
{
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
