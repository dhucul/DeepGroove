using System.Diagnostics;
using System.Reflection;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class LogicAuditWorkflowTests : IDisposable
{
    private readonly string _previous = AppSettings.AppDataDir;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.AuditWorkflow.{Guid.NewGuid():N}");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public LogicAuditWorkflowTests() => AppSettings.AppDataDir = _sandbox;
    public void Dispose()
    {
        AppSettings.AppDataDir = _previous;
        if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true);
    }

    [Fact]
    public void ApplyChainHonoursCancellationWhileItsCommitIsQueued()
    {
        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            var doc = new AudioDocument([Enumerable.Repeat(.1f, 4800).ToArray()], 48000, 32);
            main.AddDocument(doc);
            var trim = new TrimEffect(); trim.SetParam("gain", 6);
            main.Engine.Master.ReplaceChain([trim]);
            main.ApplyChainCommand.Execute(null);
            var operation = Assert.IsType<OperationProgress>(main.Progress.ActiveBlockingOperation);
            // Keep the dispatcher occupied until the worker has passed its final progress
            // report. Cancellation then arrives before the queued UI commit can execute.
            var timeout = Stopwatch.StartNew();
            while (operation.Fraction < 1 && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                operation.Refresh(DateTime.UtcNow);
                Thread.Sleep(1);
            }
            Assert.Equal(1, operation.Fraction);
            operation.Cancel();
            while (main.IsDocumentOperationRunning && timeout.Elapsed < TimeSpan.FromSeconds(10))
                Wpf.Pump();
            Assert.False(main.IsDocumentOperationRunning);
            Assert.Equal(0, doc.EditVersion);
            Assert.All(doc.Channels[0], sample => Assert.Equal(.1f, sample));
        });
    }

    [Fact]
    public void CdDialogWorkIsIncludedInMainShutdown()
    {
        Wpf.Run(() =>
        {
            using var main = new MainViewModel();
            var document = main.AddDocument(new AudioDocument([new float[4800]], 48000, 32));
            var dialog = new CdTransferDialog(document, main);
            using var operation = (CancellationTokenSource)typeof(CdTransferDialog)
                .GetMethod("StartOperation", Private)!.Invoke(dialog, null)!;
            var shutdown = main.OnCleanExitAsync();
            Assert.True(operation.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
            // The same completion path used by every CD operation releases its lifecycle lease.
            typeof(CdTransferDialog).GetMethod("SetBusy", Private)!.Invoke(dialog, [false, "Finished"]);
            var timeout = Stopwatch.StartNew();
            while (!shutdown.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5)) Wpf.Pump();
            Assert.True(shutdown.IsCompleted);
            shutdown.GetAwaiter().GetResult();
            dialog.Close();
        });
    }

    [Fact]
    public void OrdinarySavePersistsTheAudioAndItsRemappedRegions()
    {
        Wpf.Run(() =>
        {
            Directory.CreateDirectory(_sandbox);
            string path = Path.Combine(_sandbox, "source.wav");
            WavCodec.Save(new AudioDocument([new float[1000]], 48000, 32), path, 32, false);
            using var main = new MainViewModel();
            var document = main.AddDocument(WavCodec.Load(path));
            document.Regions.Add(new NamedRegion { Start = 400, End = 600, Name = "Track" });
            document.NotifyMarkersChanged();
            document.Doc.ReplaceRange(0, 100, [[]], "Delete");
            var save = (Task)typeof(MainViewModel).GetMethod("SaveCoreAsync", Private)!.Invoke(main, [document])!;
            var timeout = Stopwatch.StartNew();
            while (!save.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5)) Wpf.Pump();
            Assert.True(save.IsCompleted);
            save.GetAwaiter().GetResult();
            Assert.False(document.IsDirty);
            Assert.Equal(900, WavCodec.Load(path).Length);
            Assert.Equal(300, Assert.Single(MarkerStore.Load(path).Regions).Start);
        });
    }
}
