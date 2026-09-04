using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.Util;
using WaveLab.ViewModels;
using WaveLab.Views;
using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

[Collection(AppSettingsCollection.Name)]
public sealed class FindSpectralDefectWorkflowTests : IDisposable
{
    private readonly string _previous = AppSettings.AppDataDir;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.FindDefect.{Guid.NewGuid():N}");
    public FindSpectralDefectWorkflowTests() => AppSettings.AppDataDir = _sandbox;
    public void Dispose()
    {
        AppSettings.AppDataDir = _previous;
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    private static AudioDocument Document() => new(
        [SpectralDefectFinderTests.Ringing(SpectralDefectFinderTests.Programme())], 44_100, 24);

    internal static void Complete(Task task)
    {
        long deadline = Environment.TickCount64 + 10_000;
        while (!task.IsCompleted && Environment.TickCount64 < deadline) Wpf.Pump();
        Assert.True(task.IsCompleted, "Find Defect did not finish.");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void RoughSelectionBecomesAVisibleSpectralTargetWithoutEditingTheDocument()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document();
            vm.AddDocument(doc);
            Assert.False(vm.FindSpectralDefectCommand.CanExecute(null));
            DocumentViewModel tab = vm.ActiveDocument!;
            tab.SetSelection(0, doc.Length);
            Assert.True(vm.FindSpectralDefectCommand.CanExecute(null));
            Complete(vm.FindSpectralDefectAsync());

            Assert.False(vm.SpectralSelection.IsEmpty);
            Assert.Same(vm.SpectralSelection, vm.ResolveSpectralSelection());
            Assert.True(vm.ShowsSpectrogram);
            Assert.Equal(0, tab.SelStart);
            Assert.Equal(doc.Length, tab.SelEnd);
            Assert.Equal(0, doc.HistoryCount);
            Assert.False(doc.Dirty);
            Assert.False(vm.IsDocumentOperationRunning);
            Assert.Contains("press Heal", vm.ActionStatusText);
            Assert.True(vm.SpectralSelection.Bounds.StartSample / 44_100.0 > .65);
            Assert.True(vm.SpectralSelection.Bounds.EndSample / 44_100.0 < .76);
            Assert.True(vm.SpectralSelection.Bounds.LowFrequency > 1000);
            Assert.Equal(512, vm.SpectralSelection.FftSize);
            Assert.Equal(64, vm.SpectralSelection.Hop);
        });
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("tab")]
    [InlineData("audio")]
    [InlineData("cancel")]
    public void AChangedContextOrCancellationCannotPublishTheOldSelection(string change)
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document(); vm.AddDocument(doc);
            DocumentViewModel tab = vm.ActiveDocument!; tab.SetSelection(0, doc.Length);
            Task task = vm.FindSpectralDefectAsync();
            switch (change)
            {
                case "selection": tab.SetSelection(0, 10_000); break;
                case "tab": vm.AddDocument(new AudioDocument([new float[44_100]], 44_100, 24)); break;
                case "audio": Processing.Gain(doc, 0, doc.Length, -1); break;
                case "cancel": vm.Progress.CancelAll(); break;
            }
            Complete(task);
            Assert.True(vm.SpectralSelection.IsEmpty);
            Assert.False(vm.IsDocumentOperationRunning);
            Assert.Equal(change == "audio" ? 1 : 0, doc.HistoryCount);
            Assert.Contains(change == "cancel" ? "cancelled" : "changed", vm.ActionStatusText);
        });
    }

    [Fact]
    public void NoCandidateDisablesHealInsteadOfFallingBackToTheWholePassage()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            var doc = new AudioDocument([new float[44_100]], 44_100, 24);
            vm.AddDocument(doc); vm.ActiveDocument!.SetSelection(0, doc.Length);
            Complete(vm.FindSpectralDefectAsync());
            Assert.True(vm.SpectralSelection.IsEmpty);
            Assert.False(vm.HasSpectralSelection);
            Assert.True(vm.ResolveSpectralSelection().IsEmpty);
            Assert.Equal(0, doc.HistoryCount);
            Assert.Contains("No clear ringing defect", vm.ActionStatusText);
        });
    }

    [Fact]
    public void AFailedSecondSearchCannotRetainTheFirstRepairTarget()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document(); vm.AddDocument(doc);
            DocumentViewModel tab = vm.ActiveDocument!; tab.SetSelection(0, doc.Length);
            Complete(vm.FindSpectralDefectAsync());
            Assert.False(vm.SpectralSelection.IsEmpty);

            tab.SetSelection(4410, 17640);
            Assert.True(vm.SpectralSelection.IsEmpty, "Changing the search area left an old automatic target.");
            Complete(vm.FindSpectralDefectAsync());
            Assert.Contains("No clear ringing defect", vm.ActionStatusText);
            Assert.False(vm.HasSpectralSelection);
            Assert.True(vm.ResolveSpectralSelection().IsEmpty);
            Assert.Equal(4410, tab.SelStart); Assert.Equal(17640, tab.SelEnd);
            Assert.Equal(0, doc.HistoryCount);
        });
    }

    [Fact]
    public void ACancelledRepeatSearchCannotRestoreTheOldTargetOrFullBandFallback()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document(); vm.AddDocument(doc); vm.ActiveDocument!.SetSelection(0, doc.Length);
            Complete(vm.FindSpectralDefectAsync());
            Task second = vm.FindSpectralDefectAsync(); vm.Progress.CancelAll(); Complete(second);
            Assert.False(vm.HasSpectralSelection);
            Assert.True(vm.ResolveSpectralSelection().IsEmpty);
            Assert.Equal(0, doc.HistoryCount);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeliberateManualSelectionRestoresTheNormalRepairWorkflow(bool spectral)
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            var doc = new AudioDocument([new float[44_100]], 44_100, 24);
            vm.AddDocument(doc); vm.ActiveDocument!.SetSelection(0, doc.Length);
            Complete(vm.FindSpectralDefectAsync()); Assert.False(vm.HasSpectralSelection);
            if (spectral)
                vm.SpectralSelection = new SpectralSelection(SpectralTool.Rectangle,
                    SpectralMask.ForRegion(5000, 7000, 2000, 4000, 44_100, 512, 64), 44_100, 512, 64);
            else
                vm.ActiveDocument.SetSelection(5000, 7000);
            Assert.True(vm.HasSpectralSelection);
            Assert.False(vm.ResolveSpectralSelection().IsEmpty);
            Assert.Equal(0, doc.HistoryCount);
        });
    }

    [Fact]
    public void AManualRegionDrawnDuringTheSearchIsPreservedWhenTheOldSearchFinishes()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document(); vm.AddDocument(doc); vm.ActiveDocument!.SetSelection(0, doc.Length);
            Task task = vm.FindSpectralDefectAsync();
            var manual = new SpectralSelection(SpectralTool.Rectangle,
                SpectralMask.ForRegion(5000, 7000, 2000, 4000, 44_100, 512, 64), 44_100, 512, 64);
            vm.SpectralSelection = manual; Complete(task);
            Assert.Same(manual, vm.ResolveSpectralSelection());
            Assert.True(vm.HasSpectralSelection);
            Assert.Contains("changed", vm.ActionStatusText);
        });
    }

    [Fact]
    public void EditingAudioInvalidatesTheAutomaticTargetWithoutEnablingFullBandHeal()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document(); vm.AddDocument(doc); vm.ActiveDocument!.SetSelection(0, doc.Length);
            Complete(vm.FindSpectralDefectAsync()); Assert.True(vm.HasSpectralSelection);
            Processing.Gain(doc, 0, doc.Length, -1);
            Assert.True(vm.SpectralSelection.IsEmpty);
            Assert.False(vm.HasSpectralSelection);
            Assert.True(vm.ResolveSpectralSelection().IsEmpty);
            Complete(vm.FindSpectralDefectAsync()); Assert.True(vm.HasSpectralSelection);
        });
    }

    [Fact]
    public void SwitchingTabsDoesNotReenableHealOnAFailedSearchArea()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            var firstDoc = new AudioDocument([new float[44_100]], 44_100, 24);
            vm.AddDocument(firstDoc); DocumentViewModel first = vm.ActiveDocument!;
            first.SetSelection(0, firstDoc.Length);
            Complete(vm.FindSpectralDefectAsync()); Assert.False(vm.HasSpectralSelection);
            vm.AddDocument(new AudioDocument([new float[44_100]], 44_100, 24));
            vm.ActiveDocument!.SetSelection(1000, 2000);
            Assert.True(vm.HasSpectralSelection, "Another document's manual selection must still work.");
            vm.ActiveDocument = first;
            Assert.False(vm.HasSpectralSelection);
            Assert.True(vm.ResolveSpectralSelection().IsEmpty);
            first.SetSelection(1000, 2000);
            Assert.True(vm.HasSpectralSelection);
        });
    }

    [Fact]
    public void MovingAwayAndBackToTheSameRangeStillAbandonsThePendingSearch()
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document(); vm.AddDocument(doc);
            DocumentViewModel tab = vm.ActiveDocument!; tab.SetSelection(0, doc.Length);
            Task task = vm.FindSpectralDefectAsync();
            tab.SetSelection(4410, 17640); tab.SetSelection(0, doc.Length);
            Complete(task);
            Assert.True(vm.SpectralSelection.IsEmpty);
            Assert.Contains("changed", vm.ActionStatusText);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingTheAutomaticPatchCannotExposeTheEntireRoughAreaToHeal(bool distinctEmptyObject)
    {
        Wpf.Run(() =>
        {
            using var vm = new MainViewModel();
            AudioDocument doc = Document(); vm.AddDocument(doc); vm.ActiveDocument!.SetSelection(0, doc.Length);
            Complete(vm.FindSpectralDefectAsync()); Assert.True(vm.HasSpectralSelection);
            vm.SpectralSelection = distinctEmptyObject
                ? new SpectralSelection(SpectralTool.Rectangle, SpectralMask.Empty, 44_100, 512, 64)
                : SpectralSelection.None;
            Assert.False(vm.HasSpectralSelection);
            Assert.True(vm.ResolveSpectralSelection().IsEmpty);
            Assert.Equal(0, doc.HistoryCount);
        });
    }

    [Fact]
    public void TheFindButtonIsBoundAndVisibleBesideHealAtTheMinimumWindowWidth()
    {
        Wpf.Run(() =>
        {
            using var errors = new BindingErrors();
            bool disabledBefore = false, enabledAfter = false, displayed = false;
            double gap = -1, given = 0, wanted = 0, readoutGiven = 0, readoutWanted = 0, readoutRight = 0, barWidth = 0;
            double pictureErrorSeconds = double.PositiveInfinity, displayScale = 0;
            Wpf.Show(new MainWindow(), window =>
            {
                var shell = (MainWindow)window;
                var vm = (MainViewModel)shell.DataContext;
                try
                {
                    shell.Width = 1180;
                    vm.AddDocument(Document());
                    shell.UpdateLayout(); Wpf.Pump();
                    disabledBefore = !shell.spectralFindDefect.IsEnabled;
                    vm.ActiveDocument!.SetSelection(0, vm.ActiveDocument.Doc.Length);
                    shell.UpdateLayout(); Wpf.Pump();
                    enabledAfter = shell.spectralFindDefect.IsEnabled;
                    shell.spectralFindDefect.Command.Execute(null);
                    long deadline = Environment.TickCount64 + 10_000;
                    while (vm.IsDocumentOperationRunning && Environment.TickCount64 < deadline) Wpf.Pump();
                    shell.UpdateLayout(); Wpf.Pump();
                    // A real painted image is necessary here: a rectangle can have correct
                    // coordinates while the pixels underneath it show a different time span.
                    long paintDeadline = Environment.TickCount64 + 600;
                    while (Environment.TickCount64 < paintDeadline) { Wpf.Pump(); Thread.Sleep(5); }
                    displayed = vm.ShowsSpectrogram && !vm.SpectralSelection.IsEmpty;
                    var find = shell.spectralFindDefect;
                    Point a = find.TranslatePoint(new Point(find.ActualWidth, 0), shell);
                    Point b = shell.spectralHeal.TranslatePoint(new Point(0, 0), shell);
                    gap = b.X - a.X;
                    given = find.ActualWidth;
                    wanted = find.DesiredSize.Width - find.Margin.Left - find.Margin.Right;
                    readoutGiven = shell.spectralBandText.ActualWidth;
                    readoutWanted = shell.spectralBandText.DesiredSize.Width - shell.spectralBandText.Margin.Left - shell.spectralBandText.Margin.Right;
                    readoutRight = shell.spectralBandText.TranslatePoint(new Point(shell.spectralBandText.ActualWidth, 0), shell.spectralBar).X;
                    barWidth = shell.spectralBar.ActualWidth;
                    var picture = (WriteableBitmap?)typeof(SpectralEditorView)
                        .GetField("_bitmap", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(shell.spectralEditor);
                    if (picture is not null)
                    {
                        int width = picture.PixelWidth, height = picture.PixelHeight;
                        var pixels = new uint[width * height]; picture.CopyPixels(pixels, width * 4, 0);
                        int lowRow = (int)((1 - Math.Log(3300.0 / 20) / Math.Log(20000.0 / 20)) * height);
                        int highRow = (int)((1 - Math.Log(2500.0 / 20) / Math.Log(20000.0 / 20)) * height);
                        double strongest = -1; int brightest = 0;
                        for (int x = 0; x < width; x++)
                        {
                            double light = 0;
                            for (int y = lowRow; y <= highRow; y++)
                            {
                                uint pixel = pixels[y * width + x];
                                light += (pixel & 255) + ((pixel >> 8) & 255) + ((pixel >> 16) & 255);
                            }
                            if (light > strongest) { strongest = light; brightest = x; }
                        }
                        DocumentViewModel tab = vm.ActiveDocument!;
                        double at = tab.ViewStart + brightest / (double)width *
                            shell.spectralEditor.ActualWidth * tab.SamplesPerPixel;
                        pictureErrorSeconds = Math.Abs(at / 44_100 - .702);
                        displayScale = VisualTreeHelper.GetDpi(shell.spectralEditor).DpiScaleX;
                    }
                    string? capture = Environment.GetEnvironmentVariable("WAVELAB_FIND_DEFECT_CAPTURE");
                    if (!string.IsNullOrWhiteSpace(capture))
                    {
                        var target = new RenderTargetBitmap((int)Math.Ceiling(shell.ActualWidth),
                            (int)Math.Ceiling(shell.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        target.Render(shell);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                        using var stream = File.Create(capture); encoder.Save(stream);
                    }
                }
                finally { foreach (DocumentViewModel open in vm.AudioDocuments) open.Doc.MarkSaved(); }
            });
            Assert.Empty(errors.Messages);
            Assert.True(disabledBefore && enabledAfter && displayed);
            Assert.True(gap >= 0 && gap < 15, $"Find and Heal are {gap:0.0} px apart.");
            Assert.True(given >= wanted - .5 && given >= 100);
            Assert.True(readoutGiven >= readoutWanted - .5, "The selected band readout was clipped.");
            Assert.True(readoutRight <= barWidth, $"Band ends at {readoutRight:0} px, beyond its {barWidth:0} px toolbar.");
            Assert.True(pictureErrorSeconds < .012,
                $"At {displayScale:0.0}x display scaling the spectral picture is {pictureErrorSeconds * 1000:0.0} ms away from the actual defect.");
        });
    }
}
