using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using WaveLab.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The state the spectral repair toolbar binds to, and the conversion from what the user drew to
/// the grid the repair works on.
/// </summary>
public sealed class SpectralToolbarTests(ITestOutputHelper output)
{
    private const int Rate = 44_100, Fft = 2048, Hop = 512;

    // ── selection state ──────────────────────────────────────────

    [Fact]
    public void NothingIsSelectedUntilSomethingIsDrawn()
    {
        var vm = new MainViewModel();

        Assert.False(vm.HasSpectralSelection);
        Assert.Equal("—", vm.SpectralSpanText);
        Assert.Equal("—", vm.SpectralBandText);
    }

    /// <summary>A selection covering a time span and a frequency band, as a tool would produce it.</summary>
    private static SpectralSelection Selection(int from, int to, double low, double high,
        SpectralTool tool = SpectralTool.Rectangle) =>
        new(tool, SpectralMask.ForRegion(from, to, low, high, Rate, Fft, Hop), Rate, Fft, Hop);

    [Fact]
    public void SelectingARegionEnablesTheActionsAndFillsTheReadout()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.SpectralSelection = Selection(44_100, 66_150, 410, 3_200);

        Assert.True(vm.HasSpectralSelection);
        output.WriteLine($"span {vm.SpectralSpanText}, band {vm.SpectralBandText}");
        Assert.Contains("→", vm.SpectralSpanText);
        Assert.Contains("kHz", vm.SpectralBandText);

        foreach (string name in new[]
                 {
                     nameof(vm.HasSpectralSelection), nameof(vm.SpectralSpanText), nameof(vm.SpectralBandText),
                 })
        {
            Assert.Contains(name, raised);
        }
    }

    [Theory]
    [InlineData(50, "Hz")]
    [InlineData(3_200, "kHz")]
    [InlineData(12_500, "kHz")]
    public void FrequenciesReadInWholeHertzOrKilohertz(double frequency, string expected)
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.SpectralSelection = Selection(0, 20_000, frequency, frequency + 2_000);

        output.WriteLine($"{frequency} Hz reads as {vm.SpectralBandText}");
        Assert.Contains(expected, vm.SpectralBandText);
    }

    /// <summary>
    /// A region belongs to the file it was drawn on. Carrying it across would offer a repair at a
    /// position that means nothing in the other document.
    /// </summary>
    [Fact]
    public void SwitchingDocumentClearsTheSelection()
    {
        var vm = new MainViewModel();
        DocumentViewModel first = Document(), second = Document();
        vm.Documents.Add(first);
        vm.Documents.Add(second);

        vm.ActiveDocument = first;
        vm.SpectralSelection = Selection(1_000, 20_000, 500, 5_000);
        Assert.True(vm.HasSpectralSelection);

        vm.ActiveDocument = second;
        Assert.False(vm.HasSpectralSelection);
    }

    [Fact]
    public void AnEmptyRegionLeavesTheActionsDisabled()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };

        vm.SpectralSelection = Selection(5_000, 5_000, 500, 5_000);
        Assert.False(vm.HasSpectralSelection);

        vm.SpectralSelection = null!;
        Assert.False(vm.HasSpectralSelection);
        Assert.Equal("—", vm.SpectralSpanText);
    }

    // ── the tools ────────────────────────────────────────────────

    [Fact]
    public void TheRectangleToolIsTheOneSelectedToBeginWith()
    {
        var vm = new MainViewModel();

        Assert.Equal(SpectralTool.Rectangle, vm.SpectralTool);
        Assert.True(vm.IsRectangleTool);
        Assert.False(vm.IsLassoTool);
        Assert.False(vm.IsMagicWandTool);
        Assert.False(vm.IsHarmonicTool);
    }

    [Fact]
    public void ChoosingAToolSelectsItAndDeselectsTheOthers()
    {
        var vm = new MainViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.UseMagicWandToolCommand.Execute(null);

        Assert.True(vm.IsMagicWandTool);
        Assert.False(vm.IsRectangleTool);
        foreach (string name in new[]
                 {
                     nameof(vm.IsRectangleTool), nameof(vm.IsLassoTool),
                     nameof(vm.IsMagicWandTool), nameof(vm.IsHarmonicTool),
                 })
        {
            Assert.Contains(name, raised);
        }

        vm.UseLassoToolCommand.Execute(null);
        Assert.True(vm.IsLassoTool);
        Assert.False(vm.IsMagicWandTool);

        vm.UseHarmonicToolCommand.Execute(null);
        Assert.True(vm.IsHarmonicTool);

        vm.UseRectangleToolCommand.Execute(null);
        Assert.True(vm.IsRectangleTool);
    }

    /// <summary>Each tool wants a different gesture, so the prompt has to say which.</summary>
    [Fact]
    public void EachToolExplainsItsOwnGesture()
    {
        var vm = new MainViewModel();
        vm.ShowSpectrogramCommand.Execute(null);
        var hints = new HashSet<string>();

        foreach (SpectralTool tool in Enum.GetValues<SpectralTool>())
        {
            vm.SpectralTool = tool;
            output.WriteLine($"{tool}: {vm.SpectralToolHint}");
            Assert.False(string.IsNullOrWhiteSpace(vm.SpectralToolHint));
            Assert.True(hints.Add(vm.SpectralToolHint), $"{tool} reuses another tool's prompt");
        }
    }

    /// <summary>
    /// In waveform mode there is no picture to draw on, so naming a drawing gesture would point at
    /// a control that is not on screen.
    /// </summary>
    [Fact]
    public void WithoutTheSpectrogramThePromptAsksForATimeSelection()
    {
        var vm = new MainViewModel();

        foreach (SpectralTool tool in Enum.GetValues<SpectralTool>())
        {
            vm.SpectralTool = tool;
            output.WriteLine($"{tool}: {vm.SpectralToolHint}");
            Assert.DoesNotContain("spectrogram", vm.SpectralToolHint, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("waveform", vm.SpectralToolHint, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── the selection carries the mask ───────────────────────────

    /// <summary>
    /// Only one of the four tools draws something a rectangle can describe. Carrying bounds and
    /// rebuilding a mask from them later would quietly discard everything a lasso drew.
    /// </summary>
    [Fact]
    public void ALassoKeepsItsShapeRatherThanItsBoundingBox()
    {
        // A triangle: the corner opposite the right angle is inside, the far corner is not.
        var outline = new[] { (40.0, 40.0), (90.0, 40.0), (40.0, 90.0) };
        var selection = new SpectralSelection(
            SpectralTool.Lasso, SpectralMask.Lasso(outline), Rate, Fft, Hop);

        Assert.False(selection.IsEmpty);
        Assert.True(selection.Mask.At(50, 50) > 0, "a cell inside the triangle must be selected");
        Assert.Equal(0f, selection.Mask.At(88, 88));

        SpectralRegion bounds = selection.Bounds;
        output.WriteLine($"bounds {bounds.StartSample}..{bounds.EndSample} samples, " +
                         $"{bounds.LowFrequency:0}..{bounds.HighFrequency:0} Hz");
        Assert.True(bounds.EndSample > bounds.StartSample);
        Assert.True(bounds.HighFrequency > bounds.LowFrequency);
    }

    [Fact]
    public void BoundsReadBackTheSamplesAndHertzTheMaskCovers()
    {
        SpectralSelection selection = Selection(44_100, 66_150, 1_000, 4_000);
        SpectralRegion bounds = selection.Bounds;

        output.WriteLine($"{bounds.StartSample}..{bounds.EndSample} samples, " +
                         $"{bounds.LowFrequency:0}..{bounds.HighFrequency:0} Hz");

        Assert.InRange(bounds.StartSample, 44_100 - Hop, 44_100 + Hop);
        Assert.InRange(bounds.EndSample, 66_150 - Hop, 66_150 + 2 * Hop);
        Assert.InRange(bounds.LowFrequency, 900, 1_100);
        Assert.InRange(bounds.HighFrequency, 3_900, 4_200);
    }

    /// <summary>The overlay is drawn from these, so they have to describe the mask exactly.</summary>
    [Fact]
    public void RunsCoverEveryCellTheMaskDoesAndNoOther()
    {
        SpectralSelection selection = Selection(20_000, 40_000, 800, 2_000);
        var covered = new HashSet<(int, int)>();

        foreach (var (frame, fromBin, toBin) in selection.Runs())
        {
            Assert.True(toBin > fromBin, "a run must cover at least one bin");
            for (int b = fromBin; b < toBin; b++) covered.Add((frame, b));
        }

        SpectralMask mask = selection.Mask;
        int mismatches = 0;
        for (int f = 0; f < mask.Frames; f++)
        {
            for (int b = 0; b < mask.Bins; b++)
            {
                bool inMask = mask.Weight[f * mask.Bins + b] > 0.02f;
                bool inRuns = covered.Contains((mask.FrameOffset + f, mask.BinOffset + b));
                if (inMask != inRuns) mismatches++;
            }
        }

        output.WriteLine($"{covered.Count} cells across {selection.Runs().Count} runs");
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void AnEmptySelectionHasNoRunsAndNoBounds()
    {
        Assert.Empty(SpectralSelection.None.Runs());
        Assert.True(SpectralSelection.None.Bounds.IsEmpty);
        Assert.True(SpectralSelection.None.IsEmpty);
    }

    /// <summary>
    /// A harmonic selection is a comb, not a block: the music between the teeth has to survive.
    /// </summary>
    [Fact]
    public void AHarmonicSelectionLeavesGapsBetweenThePartials()
    {
        const double fundamental = 100;
        SpectralMask mask = SpectralMask.Harmonic(Fft / 2 + 1, Fft, Rate, 40, 80, fundamental);
        var selection = new SpectralSelection(SpectralTool.Harmonic, mask, Rate, Fft, Hop);

        Assert.False(selection.IsEmpty);
        double perBin = Rate / (double)Fft;

        // On a partial, and midway between two of them.
        int onFifth = (int)Math.Round(fundamental * 5 / perBin);
        int between = (int)Math.Round(fundamental * 5.5 / perBin);
        output.WriteLine($"bin {onFifth} = {mask.At(60, onFifth):0.00}, " +
                         $"bin {between} = {mask.At(60, between):0.00}");

        Assert.True(mask.At(60, onFifth) > 0, "the partial itself must be selected");
        Assert.Equal(0f, mask.At(60, between));
    }

    // ── toolbar visibility ───────────────────────────────────────

    /// <summary>
    /// The tools draw on the picture, so they follow it. The four actions do not — they work through
    /// a mask, and an ordinary time selection is one.
    /// </summary>
    [Fact]
    public void TheSelectionToolsFollowTheSpectrogramAndTheActionsDoNot()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };

        Assert.False(vm.ShowsSpectrogram);
        Assert.True(vm.ShowsSpectralBar);

        vm.ShowSplitCommand.Execute(null);
        Assert.True(vm.ShowsSpectrogram);
        Assert.True(vm.ShowsSpectralBar);

        vm.ShowSpectrogramCommand.Execute(null);
        Assert.True(vm.ShowsSpectrogram);

        vm.ShowWaveformCommand.Execute(null);
        Assert.False(vm.ShowsSpectrogram);
        Assert.True(vm.ShowsSpectralBar);
    }

    /// <summary>Nothing to act on, so the bar has nothing to say.</summary>
    [Fact]
    public void TheBarStaysAwayUntilThereIsAudio()
    {
        var vm = new MainViewModel();
        Assert.False(vm.ShowsSpectralBar);

        vm.ShowSpectrogramCommand.Execute(null);
        Assert.False(vm.ShowsSpectralBar);

        vm.ActiveDocument = Document();
        Assert.True(vm.ShowsSpectralBar);
    }

    /// <summary>Constant-Q resolution describes the picture, so it goes with the picture.</summary>
    [Fact]
    public void BinsPerOctaveIsOfferedOnlyWithTheSpectrogram()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.UseConstantQScaleCommand.Execute(null);

        Assert.False(vm.ShowsBinsPerOctave);

        vm.ShowSpectrogramCommand.Execute(null);
        Assert.True(vm.ShowsBinsPerOctave);

        vm.ShowWaveformCommand.Execute(null);
        Assert.False(vm.ShowsBinsPerOctave);
    }

    /// <summary>
    /// The bar cannot hold everything in it at the shell's minimum width, so the scale switch is
    /// dropped there rather than cut. A view model that has never been told a width assumes there
    /// is room, which is what every test without a window needs.
    /// </summary>
    [Fact]
    public void TheScaleSwitchIsGivenUpOnceTheBarRunsOutOfRoom()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.ShowSpectrogramCommand.Execute(null);
        Assert.True(vm.ShowsSpectralScale);

        vm.ShellWidthPixels = MainViewModel.SpectralScaleMinimumWidth - 1;
        Assert.False(vm.ShowsSpectralScale);

        vm.ShellWidthPixels = MainViewModel.SpectralScaleMinimumWidth;
        Assert.True(vm.ShowsSpectralScale);

        // It is the picture's control before it is a width question: no spectrogram, no switch,
        // however wide the window.
        vm.ShowWaveformCommand.Execute(null);
        Assert.False(vm.ShowsSpectralScale);
    }

    /// <summary>
    /// Bins per octave sits immediately beside the switch and describes it, so it goes with it —
    /// leaving it behind would strand a control explaining a choice no longer on screen.
    /// </summary>
    [Fact]
    public void BinsPerOctaveGoesWithTheScaleSwitchRatherThanStayingBehind()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.ShowSpectrogramCommand.Execute(null);
        vm.UseConstantQScaleCommand.Execute(null);
        Assert.True(vm.ShowsBinsPerOctave);

        vm.ShellWidthPixels = MainViewModel.SpectralScaleMinimumWidth - 1;

        Assert.False(vm.ShowsSpectralScale);
        Assert.False(vm.ShowsBinsPerOctave);
    }

    /// <summary>
    /// Dropping the switch must not drop the choice. The commands the View menu binds are the same
    /// ones the switch binds, so the scale is still selectable at a width that has no switch.
    /// </summary>
    [Fact]
    public void TheScaleIsStillSelectableAtAWidthThatHasNoSwitch()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.ShowSpectrogramCommand.Execute(null);
        vm.ShellWidthPixels = MainViewModel.SpectralScaleMinimumWidth - 1;
        Assert.False(vm.ShowsSpectralScale);

        vm.UseConstantQScaleCommand.Execute(null);
        Assert.True(vm.IsConstantQScale);

        vm.UseLinearScaleCommand.Execute(null);
        Assert.True(vm.IsLinearScale);
        Assert.False(vm.IsLogarithmicScale);
    }

    /// <summary>
    /// The View menu carries the same three choices as checkable items, and a click flips the
    /// tick off before the command runs; their binding is one-way, so only the view model can put
    /// it back. Choosing the scale already in force is a no-op everywhere else and must still
    /// announce the checked states, or the item sits unticked beside the scale it names.
    /// </summary>
    [Fact]
    public void ChoosingTheScaleAlreadyInForceStillAnnouncesTheTicks()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.UseLogarithmicScaleCommand.Execute(null);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.UseLogarithmicScaleCommand.Execute(null);

        Assert.True(vm.IsLogarithmicScale);
        foreach (string name in new[]
                 {
                     nameof(vm.IsLinearScale), nameof(vm.IsLogarithmicScale), nameof(vm.IsConstantQScale),
                 })
        {
            Assert.Contains(name, raised);
        }
    }

    /// <summary>A width that means nothing is not a width, and must not hide anything.</summary>
    [Fact]
    public void AnUnmeasuredWidthLeavesTheBarAsItWas()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.ShowSpectrogramCommand.Execute(null);

        foreach (double nonsense in new[] { 0, -1, double.NaN })
        {
            vm.ShellWidthPixels = nonsense;
            Assert.True(vm.ShowsSpectralScale, $"a width of {nonsense} hid the scale switch");
        }
    }

    // ── a plain time selection drives the actions ────────────────

    [Fact]
    public void ATimeSelectionEnablesTheActionsAcrossTheWholeBand()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        Assert.False(vm.HasSpectralSelection);

        vm.ActiveDocument!.SetSelection(44_100, 66_150);

        Assert.True(vm.HasSpectralSelection);
        Assert.False(vm.NeedsSpectralSelection);
        output.WriteLine($"span {vm.SpectralSpanText}, band {vm.SpectralBandText}");
        Assert.Contains("→", vm.SpectralSpanText);
        Assert.Contains("full band", vm.SpectralBandText);
        Assert.Contains(nameof(vm.HasSpectralSelection), raised);

        SpectralSelection resolved = vm.ResolveSpectralSelection();
        Assert.False(resolved.IsEmpty);
        Assert.Equal(0, resolved.Mask.BinOffset);
        Assert.Equal(Fft / 2 + 1, resolved.Mask.Bins);
        SpectralRegion bounds = resolved.Bounds;
        Assert.InRange(bounds.StartSample, 44_100 - Hop, 44_100 + Hop);
        Assert.InRange(bounds.EndSample, 66_150 - Hop, 66_150 + 2 * Hop);
    }

    /// <summary>
    /// A drawn region is a narrower claim than "everything in this stretch of time", so it wins.
    /// </summary>
    [Fact]
    public void ADrawnRegionWinsOverTheTimeSelection()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.ActiveDocument!.SetSelection(0, 100_000);
        vm.SpectralSelection = Selection(44_100, 66_150, 410, 3_200);

        SpectralSelection resolved = vm.ResolveSpectralSelection();
        Assert.Same(vm.SpectralSelection, resolved);
        Assert.True(resolved.Mask.BinOffset > 0, "the drawn band's low edge is not DC");
        Assert.DoesNotContain("full band", vm.SpectralBandText);
    }

    /// <summary>Clearing the drawn region falls back to the time selection rather than to nothing.</summary>
    [Fact]
    public void ClearingTheDrawnRegionFallsBackToTheTimeSelection()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.ActiveDocument!.SetSelection(10_000, 40_000);
        vm.SpectralSelection = Selection(44_100, 66_150, 410, 3_200);
        Assert.DoesNotContain("full band", vm.SpectralBandText);

        vm.SpectralSelection = SpectralSelection.None;

        Assert.True(vm.HasSpectralSelection);
        Assert.Contains("full band", vm.SpectralBandText);
    }

    [Fact]
    public void ClearingTheTimeSelectionDisablesTheActionsAgain()
    {
        var vm = new MainViewModel { ActiveDocument = Document() };
        vm.ActiveDocument!.SetSelection(44_100, 66_150);
        Assert.True(vm.HasSpectralSelection);

        vm.ActiveDocument.ClearSelection();

        Assert.False(vm.HasSpectralSelection);
        Assert.Equal("—", vm.SpectralSpanText);
        Assert.Equal("—", vm.SpectralBandText);
        Assert.True(vm.ResolveSpectralSelection().IsEmpty);
    }

    /// <summary>
    /// A repair holds four planes the size of the mask per channel, so a span past the ceiling would
    /// ask for gigabytes. The actions stay disabled rather than the repair failing partway.
    /// </summary>
    [Fact]
    public void ASpanTooLongToBuildAFullBandMaskForLeavesTheActionsDisabled()
    {
        int frames = (int)(SpectralMask.MaximumFullBandCells / (Fft / 2 + 1)) + 2 * Hop;
        int length = frames * Hop;
        var vm = new MainViewModel
        {
            ActiveDocument = new DocumentViewModel(new AudioDocument([new float[length]], Rate, 32)),
        };

        vm.ActiveDocument!.SelectAll();

        output.WriteLine($"{length} samples over {frames} frames");
        Assert.False(vm.HasSpectralSelection);
        Assert.True(vm.ResolveSpectralSelection().IsEmpty);
    }

    // ── region to grid ───────────────────────────────────────────

    [Fact]
    public void ADrawnRegionCoversTheCellsItWasDrawnOver()
    {
        SpectralMask mask = SpectralMask.ForRegion(
            44_100, 66_150, 1_000, 4_000, Rate, Fft, Hop, feather: 0);

        // Frame f is centred on sample f·hop, bin b on b·rate/fft.
        int frameAt = 55_000 / Hop;
        int binAt = (int)(2_000.0 * Fft / Rate);
        output.WriteLine($"frames {mask.FrameOffset}..{mask.FrameOffset + mask.Frames}, " +
                         $"bins {mask.BinOffset}..{mask.BinOffset + mask.Bins}");

        Assert.Equal(1f, mask.At(frameAt, binAt));
        Assert.Equal(0f, mask.At(frameAt, (int)(500.0 * Fft / Rate)));      // below the band
        Assert.Equal(0f, mask.At(frameAt, (int)(9_000.0 * Fft / Rate)));    // above it
        Assert.Equal(0f, mask.At(10, binAt));                               // before the span
    }

    /// <summary>
    /// Rounding outward, so a selection that half covers a cell takes it. Rounding inward would
    /// leave a rim of the defect behind at every edge of every repair.
    /// </summary>
    [Fact]
    public void APartlyCoveredCellIsTakenRatherThanLeft()
    {
        // 1500 Hz falls between bins; 20 000 samples falls between frames.
        SpectralMask mask = SpectralMask.ForRegion(
            20_000, 20_900, 1_500, 1_600, Rate, Fft, Hop, feather: 0);

        Assert.True(mask.At(20_000 / Hop, (int)(1_500.0 * Fft / Rate)) > 0,
            "the cell the selection starts inside must be covered");
        Assert.True(mask.At(20_900 / Hop, (int)(1_600.0 * Fft / Rate)) > 0,
            "the cell the selection ends inside must be covered");
    }

    [Fact]
    public void ABandReachingPastNyquistIsClamped()
    {
        SpectralMask mask = SpectralMask.ForRegion(0, 44_100, 1_000, 40_000, Rate, Fft, Hop, feather: 0);

        Assert.True(mask.BinOffset + mask.Bins <= Fft / 2 + 1,
            $"the mask reached bin {mask.BinOffset + mask.Bins} beyond Nyquist");
    }

    [Fact]
    public void ReversedEdgesAreOrderedRatherThanRejected()
    {
        SpectralMask forward = SpectralMask.ForRegion(10_000, 30_000, 500, 5_000, Rate, Fft, Hop, feather: 0);
        SpectralMask backward = SpectralMask.ForRegion(30_000, 10_000, 5_000, 500, Rate, Fft, Hop, feather: 0);

        Assert.Equal(forward.FrameOffset, backward.FrameOffset);
        Assert.Equal(forward.Frames, backward.Frames);
        Assert.Equal(forward.BinOffset, backward.BinOffset);
        Assert.Equal(forward.Bins, backward.Bins);
    }

    /// <summary>
    /// The end-to-end path the button takes: what the user drew becomes a mask, the mask becomes a
    /// repair, and the repair lands inside the document.
    /// </summary>
    [Fact]
    public void ARegionDrawnOnScreenRepairsTheAudioUnderIt()
    {
        const int length = 66_150;
        var clean = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)Rate;
            clean[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 300 * t) + 0.3 * Math.Sin(2 * Math.PI * 1_500 * t));
        }

        var damaged = (float[])clean.Clone();
        var random = new Random(4);
        for (int i = 28_000; i < 36_000; i++)
        {
            double t = i / (double)Rate;
            double burst = 0;
            for (int p = 0; p < 40; p++) burst += Math.Sin(2 * Math.PI * (1_200 + p * 45) * t + p);
            damaged[i] += (float)(burst / Math.Sqrt(40) * 0.8 + (random.NextDouble() - 0.5) * 0.01);
        }

        SpectralSelection selection = Selection(28_000, 36_000, 1_100, 3_100);
        Assert.False(selection.IsEmpty);

        SpectralRepairResult result = SpectralRepair.Heal(damaged, 0, selection.Mask,
            new SpectralRepairOptions(selection.FftSize, selection.Hop,
                SpectralRepairOptions.Default.PartialDriftRadians));

        Assert.InRange(result.Start, 0, length);
        Assert.InRange(result.End, result.Start, length);

        var merged = (float[])damaged.Clone();
        result.Samples.CopyTo(merged.AsSpan(result.Start));

        double before = Snr(clean, damaged, result.Start, result.Samples.Length);
        double after = Snr(clean, merged, result.Start, result.Samples.Length);
        output.WriteLine($"{before:0.0} dB -> {after:0.0} dB over {result.Start}..{result.End}");
        Assert.True(after > before + 8, $"the repair gained only {after - before:0.0} dB");

        static double Snr(float[] clean, float[] candidate, int start, int count)
        {
            double signal = 0, error = 0;
            for (int i = start; i < start + count && i < clean.Length; i++)
            {
                double d = clean[i] - candidate[i];
                signal += (double)clean[i] * clean[i];
                error += d * d;
            }
            return 10 * Math.Log10(signal / Math.Max(error, 1e-30));
        }
    }

    /// <summary>
    /// The end-to-end path the waveform half takes: a range selected on the wave becomes a full-band
    /// mask, and a repair through it turns that range down and leaves the rest of the file alone.
    /// </summary>
    [Fact]
    public void ARangeSelectedOnTheWaveformAttenuatesTheAudioUnderIt()
    {
        const int length = 132_300;                 // three seconds
        var tone = new float[length];
        for (int i = 0; i < length; i++)
            tone[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 1_000 * i / Rate));

        var vm = new MainViewModel
        {
            ActiveDocument = new DocumentViewModel(new AudioDocument([tone], Rate, 32)),
        };
        vm.ActiveDocument!.SetSelection(Rate, 2 * Rate);

        SpectralSelection selection = vm.ResolveSpectralSelection();
        Assert.False(selection.IsEmpty);

        SpectralRepairResult result = SpectralRepair.Attenuate(tone, 0, selection.Mask, -24,
            new SpectralRepairOptions(selection.FftSize, selection.Hop,
                SpectralRepairOptions.Default.PartialDriftRadians));
        Assert.False(result.IsEmpty);

        var merged = (float[])tone.Clone();
        result.Samples.CopyTo(merged.AsSpan(result.Start));

        // Well inside the selection, and well outside the padded span the repair rewrote.
        double inside = Rms(merged, Rate + Rate / 2, Rate / 4);
        double before = Rms(merged, 0, Rate / 4);
        double reference = Rms(tone, 0, Rate / 4);
        output.WriteLine($"inside {20 * Math.Log10(inside / reference):0.0} dB, " +
                         $"outside {20 * Math.Log10(before / reference):0.0} dB");

        Assert.True(20 * Math.Log10(inside / reference) < -18,
            "the selected range was not turned down");
        Assert.Equal(reference, before, 5);

        static double Rms(float[] signal, int start, int count)
        {
            double sum = 0;
            for (int i = start; i < start + count; i++) sum += (double)signal[i] * signal[i];
            return Math.Sqrt(sum / count);
        }
    }

    private static DocumentViewModel Document() =>
        new(new AudioDocument([new float[132_300], new float[132_300]], Rate, 32));
}
