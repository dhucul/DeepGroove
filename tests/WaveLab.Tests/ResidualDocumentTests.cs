using System.Threading;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// What arrives when a restoration pass keeps what it removed: a second tab, named after the
/// source, holding the exact difference and lifted only on its way to the speakers.
/// </summary>
/// <remarks>
/// Driven on an STA thread with a sandboxed settings root, like <see cref="GuiActionStatusTests"/>:
/// <see cref="MainViewModel"/> loads settings and publishes factory presets on construction.
/// </remarks>
[Collection(AppSettingsCollection.Name)]
public sealed class ResidualDocumentTests : IDisposable
{
    private readonly string _originalAppDataDir = AppSettings.AppDataDir;
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");

    public ResidualDocumentTests() => AppSettings.AppDataDir = _sandbox;

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

    private static void OnUi(Action<MainViewModel> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainViewModel? viewModel = null;
            try
            {
                viewModel = new MainViewModel();
                body(viewModel);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The residual document test timed out.");
        if (failure != null) throw failure;
    }

    private static AudioDocument Source() =>
        new([new float[4096], new float[4096]], 44_100, 24) { Title = "side one.aif" };

    private static float[][] Removed(float peak)
    {
        var removed = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            removed[c] = new float[4096];
            removed[c][100 + c] = peak;
        }
        return removed;
    }

    [Fact]
    public void ItOpensASecondTabNamedAfterTheSourceWithoutTakingTheFocus()
    {
        OnUi(viewModel =>
        {
            AudioDocument source = Source();
            viewModel.AddDocument(source);
            TabViewModel? before = viewModel.ActiveTab;

            bool opened = viewModel.AddResidualDocument(source, Removed(0.01f), "The restoration");

            Assert.True(opened);
            Assert.Equal(2, viewModel.Documents.Count);
            // The user stays on the file they just restored.
            Assert.Same(before, viewModel.ActiveTab);

            DocumentViewModel residual = viewModel.AudioDocuments.Last();
            Assert.Equal("side one (removed).wav", residual.Doc.Title);
            Assert.True(residual.Doc.IsResidual);
            Assert.Null(residual.Doc.FilePath);
            Assert.True(residual.Doc.Dirty, "an unsaved computed file must offer to be saved");
            Assert.Equal(44_100, residual.Doc.SampleRate);
            Assert.Equal(2, residual.Doc.ChannelCount);
            // Computed rather than captured, so float — the only depth that holds a residual
            // without dithering the thing being examined.
            Assert.Equal(32, residual.Doc.SourceBitDepth);
        });
    }

    [Fact]
    public void ItArrivesLoudEnoughToHearWithoutTheSamplesBeingTouched()
    {
        OnUi(viewModel =>
        {
            AudioDocument source = Source();
            viewModel.AddDocument(source);
            float[][] removed = Removed(0.01f);

            viewModel.AddResidualDocument(source, removed, "The restoration");
            AudioDocument residual = viewModel.AudioDocuments.Last().Doc;

            Assert.Equal(0.01f, residual.Channels[0][100]);          // untouched
            Assert.Equal(ResidualSummary.MonitorGainFor(0.01f, RestorationPreview.RmsOf(removed)), residual.MonitorGain);
            Assert.True(residual.MonitorGain > 1f);
            // A single loud sample in an otherwise empty buffer: its body would take +52 dB, and
            // the peak anchor holds it to the +39 that lands that sample at -1 dBFS.
            Assert.Contains("monitoring at +39 dB", viewModel.ActionStatusText);
            Assert.Contains("side one (removed).wav", viewModel.ActionStatusText);
        });
    }

    /// <summary>
    /// A residual is only as long as the range it came from, so two selections restored in one
    /// session would otherwise arrive as two identically named tabs with nothing to tell them
    /// apart or say where either belongs.
    /// </summary>
    [Fact]
    public void ASubRangeResidualSaysWhereInTheSourceItCameFrom()
    {
        OnUi(viewModel =>
        {
            AudioDocument source = Source();
            viewModel.AddDocument(source);

            viewModel.AddResidualDocument(source, Removed(0.01f), "The restoration",
                rangeStart: 44_100 * 83);
            Assert.Equal("side one (removed at 1:23).wav", viewModel.AudioDocuments.Last().Doc.Title);

            viewModel.AddResidualDocument(source, Removed(0.02f), "The restoration");
            Assert.Equal("side one (removed).wav", viewModel.AudioDocuments.Last().Doc.Title);
        });
    }

    /// <summary>
    /// The levels are measured on the worker that built the residual — a full pass over a buffer
    /// the size of the range, which this repo does not ask the UI thread for. Passing them in must
    /// therefore give exactly what measuring here would.
    /// </summary>
    [Fact]
    public void PrecomputedLevelsAgreeWithMeasuringThemHere()
    {
        OnUi(viewModel =>
        {
            AudioDocument source = Source();
            viewModel.AddDocument(source);
            float[][] removed = Removed(0.01f);

            viewModel.AddResidualDocument(source, RestorationPreview.Clone(removed), "A");
            float measuredHere = viewModel.AudioDocuments.Last().Doc.MonitorGain;

            viewModel.AddResidualDocument(source, RestorationPreview.Clone(removed), "B",
                levels: RestorationPreview.MeasureLevels(removed));
            Assert.Equal(measuredHere, viewModel.AudioDocuments.Last().Doc.MonitorGain);
        });
    }

    /// <summary>
    /// <c>activate: false</c> is what stops the residual stealing focus, and it must not be able to
    /// leave a workspace holding documents with nothing selected.
    /// </summary>
    [Fact]
    public void TheFirstTabActivatesEvenWhenTheCallerAskedNotTo()
    {
        OnUi(viewModel =>
        {
            var doc = new AudioDocument([new float[64]], 44_100, 32) { Title = "only.wav" };
            viewModel.AddGeneratedDocument(doc, activate: false);

            Assert.NotNull(viewModel.ActiveTab);
            Assert.Same(viewModel.AudioDocuments.Single(), viewModel.ActiveTab);
        });
    }

    [Fact]
    public void APassThatRemovedNothingOpensNoTabAndSaysSo()
    {
        OnUi(viewModel =>
        {
            AudioDocument source = Source();
            viewModel.AddDocument(source);

            bool opened = viewModel.AddResidualDocument(source, new float[2][]
            {
                new float[4096],
                new float[4096],
            }, "Remove Clicks");

            Assert.False(opened);
            Assert.Single(viewModel.Documents);
            Assert.Contains("removed nothing audible", viewModel.ActionStatusText);
        });
    }

    /// <summary>
    /// The monitor bar is on screen only for a residual, and pulling the lift down to hear the
    /// true level must not take the control away with it.
    /// </summary>
    [Fact]
    public void TheMonitorBarBelongsToTheResidualTabAndSurvivesBeingResetToUnity()
    {
        OnUi(viewModel =>
        {
            AudioDocument source = Source();
            viewModel.AddDocument(source);
            Assert.False(viewModel.ShowsMonitorGain);

            viewModel.AddResidualDocument(source, Removed(0.01f), "The restoration");
            viewModel.ActiveTab = viewModel.AudioDocuments.Last();

            Assert.True(viewModel.ShowsMonitorGain);
            Assert.True(viewModel.MonitorGainDb > 30);

            viewModel.MonitorGainDb = 12;
            Assert.Equal(12, viewModel.MonitorGainDb, 3);
            Assert.Equal("+12.0 dB", viewModel.MonitorGainText);

            viewModel.ResetMonitorGain();
            Assert.Equal(0, viewModel.MonitorGainDb, 6);
            Assert.Equal(1f, viewModel.AudioDocuments.Last().Doc.MonitorGain);
            Assert.True(viewModel.ShowsMonitorGain, "the bar must stay reachable on a residual");

            // Back on the restored file there is nothing to lift.
            viewModel.ActiveTab = viewModel.AudioDocuments.First();
            Assert.False(viewModel.ShowsMonitorGain);
            Assert.Equal(0, viewModel.MonitorGainDb, 6);
        });
    }

    [Fact]
    public void TheLiftIsBoundedByWhatTheBarOffers()
    {
        OnUi(viewModel =>
        {
            AudioDocument source = Source();
            viewModel.AddDocument(source);
            viewModel.AddResidualDocument(source, Removed(0.01f), "The restoration");
            viewModel.ActiveTab = viewModel.AudioDocuments.Last();

            viewModel.MonitorGainDb = 500;
            Assert.Equal(ResidualSummary.MaximumMonitorGainDb, viewModel.MonitorGainDb, 3);

            viewModel.MonitorGainDb = -20;
            Assert.Equal(0, viewModel.MonitorGainDb, 6);
        });
    }
}
