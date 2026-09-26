using System.Reflection;
using WaveLab.Audio;
using WaveLab.Util;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class ReviewDocumentRegressionTests
{
    [Fact]
    public void TrimCommandKeepsAnchorsOnTheirAudioAndUsesOneHistoryStep()
    {
        string old = AppSettings.AppDataDir;
        string folder = Directory.CreateTempSubdirectory("wavelab-trim-test-").FullName;
        AppSettings.AppDataDir = folder;
        try
        {
            Wpf.Run(() =>
            {
                using var main = new MainViewModel();
                var doc = new AudioDocument([Enumerable.Range(0, 1000).Select(x => x / 1000f).ToArray()], 48000, 32);
                var view = main.AddDocument(doc);
                var marker = new Marker { Position = 500, Name = "Retained midpoint" };
                var region = new NamedRegion { Start = 450, End = 550, Name = "Retained region", CdTrackOrder = 2 };
                view.Markers.Add(marker);
                view.Regions.Add(region);
                view.SetSelection(400, 600);
                main.TrimCommand.Execute(null);
                long deadline = Environment.TickCount64 + 5000;
                while (doc.Length == 1000 && Environment.TickCount64 < deadline) { Wpf.Pump(); Thread.Sleep(1); }
                Assert.Equal(200, doc.Length);
                Assert.Equal(.4f, doc.Channels[0][0]);
                Assert.Equal(1, doc.HistoryCount);
                for (int i = 0; i < 3; i++)
                {
                    Assert.Equal(100, marker.Position);
                    Assert.Same(region, Assert.Single(view.Regions));
                    Assert.Equal((50, 150, 2), (region.Start, region.End, region.CdTrackOrder));
                    doc.Undo();
                    Assert.Equal(1000, doc.Length);
                    Assert.Equal(500, marker.Position);
                    Assert.Equal((450, 550), (region.Start, region.End));
                    doc.Redo();
                }
                doc.JumpToHistoryPosition(0);
                Assert.Equal(500, marker.Position);
                doc.JumpToHistoryPosition(1);
                Assert.Equal(100, marker.Position);
                string path = Path.Combine(folder, "trimmed.wav");
                WavCodec.Save(doc, path, 32, false, markers: view.Markers.ToArray());
                Assert.Equal(100, Assert.Single(MarkerStore.FromRiff(WavCodec.Load(path).Riff)).Position);
                view.Unhook();
            });
        }
        finally { AppSettings.AppDataDir = old; Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(400, 200)]
    [InlineData(800, 200)]
    public void TrimClipsCrossingRegionsAndRestoresRemovedRegions(int start, int count)
    {
        var doc = new AudioDocument([new float[1000]], 48000, 32);
        var view = new DocumentViewModel(doc);
        var crossing = new NamedRegion { Start = Math.Max(0, start - 50), End = start + 100 };
        var outside = new NamedRegion { Start = start == 0 ? 500 : 0, End = start == 0 ? 600 : 50 };
        view.Regions.Add(crossing);
        view.Regions.Add(outside);
        doc.TrimRangeOwned(start, doc.CopyRange(start, count));
        Assert.Same(crossing, Assert.Single(view.Regions));
        Assert.Equal((0, 100), (crossing.Start, crossing.End));
        doc.Undo();
        Assert.Contains(outside, view.Regions);
        Assert.Equal((Math.Max(0, start - 50), start + 100), (crossing.Start, crossing.End));
        view.Unhook();
    }

    [Fact]
    public void TrimmingTheWholeFileDoesNotCreateAnEdit()
    {
        var doc = new AudioDocument([new float[1000]], 48000, 32);
        doc.TrimRangeOwned(0, doc.CopyRange(0, 1000));
        Assert.False(doc.Dirty);
        Assert.Equal(0, doc.HistoryCount);
    }

    [Theory]
    [InlineData(".w64")]
    [InlineData(".wav")]
    public void ImportedWave64SelectsSaveAsAndCanSaveAnEditableCopy(string extension)
    {
        string folder = Directory.CreateTempSubdirectory("wavelab-wave64-save-").FullName;
        try
        {
            string sourcePath = Path.Combine(folder, "source" + extension);
            Wave64Codec.Save(new AudioDocument([[.25f, -.5f]], 44100, 24), sourcePath, 24, false);
            byte[] original = File.ReadAllBytes(sourcePath);
            AudioDocument loaded = AudioImporter.Load(sourcePath);
            Assert.True(loaded.RequiresSaveAs); // SaveCoreAsync must route to Save As before the writer.
            Assert.Equal(sourcePath, loaded.FilePath);
            Assert.True(MainViewModel.SnapshotDoc(loaded).RequiresSaveAs);
            Processing.Gain(loaded, 0, loaded.Length, -6);
            string copyPath = Path.Combine(folder, "edited.wav");
            var save = typeof(MainViewModel).GetMethod("SaveEditableDocument", BindingFlags.Static | BindingFlags.NonPublic)!;
            var error = Assert.Throws<TargetInvocationException>(() => save.Invoke(null,
                [loaded, sourcePath, 32, false, false, CancellationToken.None, null, null]));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            save.Invoke(null, [loaded, copyPath, 32, false, false, CancellationToken.None, null, null]);
            Assert.Equal(loaded.Channels[0], WavCodec.Load(copyPath).Channels[0]);
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
        }
        finally { Directory.Delete(folder, true); }
    }
}
