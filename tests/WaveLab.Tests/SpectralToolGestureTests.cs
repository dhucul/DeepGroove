using System.Windows;
using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.ViewModels;
using WaveLab.Views.Controls;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The gestures themselves, driven through a real control on an STA thread. These are what turn a
/// drag into something repairable, and nothing else in the suite exercises them.
/// </summary>
public sealed class SpectralToolGestureTests(ITestOutputHelper output)
{
    private const int Rate = 44_100;
    private const int Length = 441_000;      // 10 s
    private const double Width = 800, Height = 320;

    /// <summary>
    /// Zoom and scroll for every gesture test. The zoom has to leave somewhere to scroll to:
    /// <c>ViewStart</c> is clamped against the visible width, so at a zoom where the whole file fits
    /// it silently stays at zero and every screen coordinate means something other than intended.
    /// </summary>
    private const int Spp = 128, ViewStart = 180_000;

    private static double SampleAtX(double x) => ViewStart + x * Spp;

    /// <summary>Screen column of the burst's middle, which the wand is aimed at.</summary>
    private const double BurstX = (220_000 - ViewStart) / (double)Spp;

    /// <summary>Tonal programme with a loud band-limited burst in the middle, for the wand to find.</summary>
    private static AudioDocument Damaged()
    {
        var left = new float[Length];
        var random = new Random(9);
        for (int i = 0; i < Length; i++)
        {
            double t = i / (double)Rate;
            double value = 0.30 * Math.Sin(2 * Math.PI * 220 * t)
                         + 0.20 * Math.Sin(2 * Math.PI * 440 * t)
                         + (random.NextDouble() - 0.5) * 0.001;
            if (i >= 210_000 && i < 230_000)
            {
                double envelope = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i - 210_000) / 20_000.0);
                for (int p = 0; p < 24; p++)
                    value += Math.Sin(2 * Math.PI * (1_500 + p * 60) * t) / 24 * envelope * 2.5;
            }
            left[i] = (float)value;
        }
        return new AudioDocument([left, (float[])left.Clone()], Rate, 32);
    }

    private static SpectralSelection Drag(SpectralTool tool, Point from, Point to,
        IReadOnlyList<Point>? path = null)
    {
        SpectralSelection? result = null;
        RunOnUiThread(() =>
        {
            var vm = new DocumentViewModel(Damaged())
            {
                ViewWidthPixels = Width,
                SamplesPerPixel = Spp,
            };
            vm.ViewStart = ViewStart;
            Assert.Equal(ViewStart, vm.ViewStart, 3);   // the clamp did not eat it

            var view = new SpectralEditorView
            {
                Document = vm, Tool = tool, Width = Width, Height = Height,
            };
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));

            result = view.PerformGesture(from, to, path);
        });
        return result ?? SpectralSelection.None;
    }

    // ── rectangle ────────────────────────────────────────────────

    [Fact]
    public void ARectangleDragSelectsTheTimeAndFrequencyItCovers()
    {
        SpectralSelection selection = Drag(SpectralTool.Rectangle, new Point(100, 80), new Point(300, 200));

        Assert.False(selection.IsEmpty);
        Assert.Equal(SpectralTool.Rectangle, selection.Tool);
        SpectralRegion bounds = selection.Bounds;
        output.WriteLine($"{bounds.StartSample}..{bounds.EndSample} samples, " +
                         $"{bounds.LowFrequency:0}..{bounds.HighFrequency:0} Hz");

        Assert.InRange(bounds.StartSample, SampleAtX(100) - 1_024, SampleAtX(100) + 1_024);
        Assert.InRange(bounds.EndSample, SampleAtX(300) - 1_024, SampleAtX(300) + 2_048);
        Assert.True(bounds.HighFrequency > bounds.LowFrequency);
    }

    /// <summary>
    /// A click that never became a drag clears the selection. Treating a slip as a region would put
    /// an accidental repair one stray click away.
    /// </summary>
    [Fact]
    public void AClickWithoutADragSelectsNothing()
    {
        Assert.True(Drag(SpectralTool.Rectangle, new Point(200, 150), new Point(200, 150)).IsEmpty);
        Assert.True(Drag(SpectralTool.Rectangle, new Point(200, 150), new Point(201, 151)).IsEmpty);
    }

    [Fact]
    public void DraggingBackwardsSelectsTheSameRegion()
    {
        SpectralSelection forward = Drag(SpectralTool.Rectangle, new Point(120, 90), new Point(280, 210));
        SpectralSelection backward = Drag(SpectralTool.Rectangle, new Point(280, 210), new Point(120, 90));

        Assert.Equal(forward.Bounds.StartSample, backward.Bounds.StartSample);
        Assert.Equal(forward.Bounds.EndSample, backward.Bounds.EndSample);
        Assert.Equal(forward.Bounds.LowFrequency, backward.Bounds.LowFrequency, 3);
        Assert.Equal(forward.Bounds.HighFrequency, backward.Bounds.HighFrequency, 3);
    }

    // ── lasso ────────────────────────────────────────────────────

    /// <summary>
    /// The lasso has to follow the outline, not fill its bounding box — that is the whole reason to
    /// have it rather than a rectangle.
    /// </summary>
    [Fact]
    public void ALassoFollowsItsOutlineRatherThanItsBoundingBox()
    {
        // A right triangle: the area below the hypotenuse is in, the top-right corner is out.
        var path = new List<Point>();
        for (int i = 0; i <= 40; i++) path.Add(new Point(100 + i * 4, 100));           // top edge
        for (int i = 0; i <= 40; i++) path.Add(new Point(260 - i * 4, 100 + i * 2.5));  // hypotenuse
        for (int i = 0; i <= 20; i++) path.Add(new Point(100, 200 - i * 5));            // left edge

        SpectralSelection selection = Drag(SpectralTool.Lasso, path[0], path[^1], path);

        Assert.False(selection.IsEmpty);
        Assert.Equal(SpectralTool.Lasso, selection.Tool);

        double covered = selection.Mask.Coverage();
        double box = selection.Mask.Frames * (double)selection.Mask.Bins;
        output.WriteLine($"covered {covered:0} of {box:0} cells in the bounding box ({covered / box:P0})");

        Assert.True(covered > 0, "the lasso selected nothing");
        Assert.True(covered < box * 0.75, "the lasso filled its bounding box instead of its outline");
    }

    [Fact]
    public void ALassoOfTwoPointsIsNotASelection()
    {
        var path = new List<Point> { new(100, 100), new(160, 140) };
        Assert.True(Drag(SpectralTool.Lasso, path[0], path[^1], path).IsEmpty);
    }

    // ── magic wand ───────────────────────────────────────────────

    /// <summary>
    /// Clicking on the burst grows a region through it. The wand is a single click, so this also
    /// covers the one tool that commits without a drag at all.
    /// </summary>
    [Fact]
    public void TheWandGrowsARegionFromASingleClick()
    {
        SpectralSelection selection = SelectionAt(new Point(BurstX, YForHz(2_000)));

        Assert.False(selection.IsEmpty);
        Assert.Equal(SpectralTool.MagicWand, selection.Tool);

        SpectralRegion bounds = selection.Bounds;
        output.WriteLine($"grew {selection.Mask.Coverage():0} cells over " +
                         $"{bounds.StartSample}..{bounds.EndSample} samples, " +
                         $"{bounds.LowFrequency:0}..{bounds.HighFrequency:0} Hz");

        // It should have found the burst rather than a single cell or the whole file.
        Assert.True(selection.Mask.Coverage() > 20, "the wand grew almost nothing");
        Assert.True(bounds.StartSample < 230_000 && bounds.EndSample > 210_000,
            "the grown region does not overlap the defect");
    }

    [Fact]
    public void TheWandOnSilenceDoesNotRunAway()
    {
        SpectralSelection selection = SelectionAt(new Point(600, YForHz(9_000)));

        // Whatever it finds in quiet material, it must stay bounded rather than taking the file.
        output.WriteLine($"quiet click grew {selection.Mask.Coverage():0} cells");
        Assert.True(selection.Mask.Frames < 4_000, $"the wand spread over {selection.Mask.Frames} frames");
    }

    private static SpectralSelection SelectionAt(Point point) =>
        Drag(SpectralTool.MagicWand, point, point);

    /// <summary>Screen row for a frequency, matching the view's own mapping.</summary>
    private static double YForHz(double frequency) =>
        SpectrogramImage.RowForFrequency(frequency, (int)Height, SpectrogramImageSettings.Default, Rate / 2.0);

    // ── harmonic ─────────────────────────────────────────────────

    /// <summary>
    /// A buzz is a comb, not a block. The drag's vertical position picks the fundamental and its
    /// horizontal travel sets the span; the music between the partials has to survive.
    /// </summary>
    [Fact]
    public void TheHarmonicToolTakesAFundamentalAndItsPartials()
    {
        double y = YForHz(220);
        SpectralSelection selection = Drag(SpectralTool.Harmonic, new Point(100, y), new Point(400, y + 40));

        Assert.False(selection.IsEmpty);
        Assert.Equal(SpectralTool.Harmonic, selection.Tool);

        SpectralMask mask = selection.Mask;
        int frame = mask.FrameOffset + mask.Frames / 2;
        double perBin = Rate / 2048.0;

        int onFundamental = (int)Math.Round(220 / perBin);
        int onSecond = (int)Math.Round(440 / perBin);
        int between = (int)Math.Round(330 / perBin);
        output.WriteLine($"220 Hz {mask.At(frame, onFundamental):0.00}, " +
                         $"440 Hz {mask.At(frame, onSecond):0.00}, " +
                         $"330 Hz {mask.At(frame, between):0.00}");

        Assert.True(mask.At(frame, onFundamental) > 0, "the fundamental must be selected");
        Assert.True(mask.At(frame, onSecond) > 0, "the second partial must be selected");
        Assert.Equal(0f, mask.At(frame, between));
    }

    [Fact]
    public void AHarmonicClickWithoutTravelSelectsNothing()
    {
        double y = YForHz(220);
        Assert.True(Drag(SpectralTool.Harmonic, new Point(200, y), new Point(200, y)).IsEmpty);
    }

    // ── every tool ───────────────────────────────────────────────

    [Fact]
    public void EveryToolProducesAMaskInTheRepairGrid()
    {
        var path = new List<Point> { new(100, 100), new(200, 100), new(200, 180), new(100, 180) };

        foreach (SpectralTool tool in Enum.GetValues<SpectralTool>())
        {
            SpectralSelection selection = tool switch
            {
                SpectralTool.Lasso => Drag(tool, path[0], path[^1], path),
                SpectralTool.MagicWand => SelectionAt(new Point(BurstX, YForHz(2_000))),
                _ => Drag(tool, new Point(100, YForHz(300)), new Point(300, YForHz(3_000))),
            };

            output.WriteLine($"{tool}: {selection.Mask.Coverage():0} cells, " +
                             $"fft {selection.FftSize}, hop {selection.Hop}");
            Assert.False(selection.IsEmpty, $"{tool} selected nothing");
            Assert.Equal(SpectrogramSettings.Default.FftSize, selection.FftSize);
            Assert.Equal(SpectrogramSettings.Default.Hop, selection.Hop);
            Assert.Equal(Rate, selection.SampleRate);
            Assert.True(selection.Mask.FrameOffset >= 0, $"{tool} produced a negative frame offset");
        }
    }

    /// <summary>
    /// Whatever the tool drew has to survive as far as an actual repair, in the grid the repair
    /// works in. This is the join the toolbar depends on.
    /// </summary>
    [Fact]
    public void AToolSelectionRepairsTheAudioUnderIt()
    {
        SpectralSelection selection = SelectionAt(new Point(BurstX, YForHz(2_000)));
        Assert.False(selection.IsEmpty);

        AudioDocument document = Damaged();
        float[] channel = document.Channels[0];

        SpectralRepairResult result = SpectralRepair.Heal(channel, 0, selection.Mask,
            new SpectralRepairOptions(selection.FftSize, selection.Hop,
                SpectralRepairOptions.Default.PartialDriftRadians));

        Assert.False(result.IsEmpty);
        Assert.InRange(result.Start, 0, Length);
        Assert.InRange(result.End, result.Start, Length);
        Assert.All(result.Samples, sample => Assert.True(float.IsFinite(sample)));

        // The burst was far louder than the programme, so removing it must lower the level here.
        double before = Energy(channel, result.Start, result.Samples.Length);
        double after = Energy(result.Samples, 0, result.Samples.Length);
        output.WriteLine($"level {10 * Math.Log10(after / before):0.0} dB after the repair");
        Assert.True(after < before, "the repair did not remove any of the defect");

        static double Energy(float[] signal, int start, int count)
        {
            double total = 0;
            for (int i = start; i < start + count && i < signal.Length; i++) total += (double)signal[i] * signal[i];
            return total;
        }
    }

    private static void RunOnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the UI thread did not finish");
        if (failure != null) throw failure;
    }
}
