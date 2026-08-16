using WaveLab.Audio;
using WaveLab.ViewModels;
using WaveLab.Views.Controls;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

public sealed class FrequencyRulerTests(ITestOutputHelper output)
{
    private const double Nyquist = 24_000;
    private static SpectrogramImageSettings Settings => SpectrogramImageSettings.Default;

    // ── the ladder ───────────────────────────────────────────────

    [Theory]
    [InlineData(600)]
    [InlineData(300)]
    [InlineData(160)]
    [InlineData(90)]
    [InlineData(46)]
    public void LabelsNeverCrowdHoweverShortThePaneIs(int height)
    {
        var scale = FrequencyRuler.BuildScale(height, Settings, Nyquist);

        output.WriteLine($"{height,4} px: " + string.Join("  ",
            scale.ConvertAll(entry => $"{FrequencyRuler.Format(entry.Frequency)}@{entry.Y:0}")));

        for (int i = 1; i < scale.Count; i++)
            Assert.True(scale[i].Y - scale[i - 1].Y >= 12 - 1e-6,
                $"{FrequencyRuler.Format(scale[i - 1].Frequency)} and " +
                $"{FrequencyRuler.Format(scale[i].Frequency)} overlap");
    }

    /// <summary>
    /// The roundest landmarks are offered first, so a short pane keeps 1k and 100 rather than
    /// whichever happened to be tried first.
    /// </summary>
    [Fact]
    public void ARoomOnlyForOneOrTwoKeepsTheRoundestOnes()
    {
        var scale = FrequencyRuler.BuildScale(40, Settings, Nyquist);
        var labels = scale.ConvertAll(entry => FrequencyRuler.Format(entry.Frequency));

        output.WriteLine("40 px keeps: " + string.Join(", ", labels));
        Assert.NotEmpty(labels);
        Assert.Contains("1k", labels);
    }

    [Fact]
    public void ATallPaneShowsTheWholeDecadeLadder()
    {
        var labels = FrequencyRuler.BuildScale(700, Settings, Nyquist)
            .ConvertAll(entry => FrequencyRuler.Format(entry.Frequency));

        foreach (string expected in new[] { "50", "100", "200", "500", "1k", "2k", "5k", "10k" })
            Assert.Contains(expected, labels);
    }

    [Fact]
    public void LabelsRunDownThePaneAsFrequencyFalls()
    {
        var scale = FrequencyRuler.BuildScale(500, Settings, Nyquist);

        for (int i = 1; i < scale.Count; i++)
        {
            Assert.True(scale[i].Y > scale[i - 1].Y, "entries must be ordered down the pane");
            Assert.True(scale[i].Frequency < scale[i - 1].Frequency,
                "and frequency must fall as they go");
        }
    }

    [Fact]
    public void NothingIsDrawnOutsideThePane()
    {
        const int height = 220;
        foreach (var (_, y) in FrequencyRuler.BuildScale(height, Settings, Nyquist))
            Assert.InRange(y, 0, height);
    }

    [Fact]
    public void LandmarksAboveNyquistAreLeftOut()
    {
        // An 8 kHz file: nothing above 4 kHz exists to label.
        var labels = FrequencyRuler.BuildScale(500, Settings, 4_000)
            .ConvertAll(entry => FrequencyRuler.Format(entry.Frequency));

        output.WriteLine("4 kHz Nyquist: " + string.Join(", ", labels));
        Assert.DoesNotContain("5k", labels);
        Assert.DoesNotContain("10k", labels);
        Assert.DoesNotContain("20k", labels);
        Assert.Contains("1k", labels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void APaneTooShortToLabelIsEmptyRatherThanCrowded(int height)
    {
        Assert.Empty(FrequencyRuler.BuildScale(height, Settings, Nyquist));
    }

    [Fact]
    public void EachLandmarkAppearsOnlyOnce()
    {
        var scale = FrequencyRuler.BuildScale(900, Settings, Nyquist);
        var seen = new HashSet<double>();

        foreach (var (frequency, _) in scale)
            Assert.True(seen.Add(frequency), $"{frequency} Hz was listed twice");
    }

    // ── formatting ───────────────────────────────────────────────

    [Theory]
    [InlineData(20, "20")]
    [InlineData(500, "500")]
    [InlineData(1_000, "1k")]
    [InlineData(2_000, "2k")]
    [InlineData(20_000, "20k")]
    [InlineData(1_500, "1.5k")]
    public void FrequenciesAreLabelledInTheShortestUnambiguousForm(double frequency, string expected)
    {
        Assert.Equal(expected, FrequencyRuler.Format(frequency));
    }

    // ── view-mode state ──────────────────────────────────────────

    [Fact]
    public void TheEditorStartsOnTheWaveformSoNothingChangesUntilAsked()
    {
        var vm = new MainViewModel();

        Assert.Equal(EditorViewMode.Waveform, vm.EditorView);
        Assert.True(vm.IsWaveformView);
        Assert.False(vm.IsSplitView);
        Assert.False(vm.IsSpectrogramView);
    }

    [Fact]
    public void ChoosingAModeSelectsItAndDeselectsTheOthers()
    {
        var vm = new MainViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        int layouts = 0;
        vm.EditorViewChanged += () => layouts++;

        vm.ShowSplitCommand.Execute(null);

        Assert.Equal(EditorViewMode.Split, vm.EditorView);
        Assert.True(vm.IsSplitView);
        Assert.False(vm.IsWaveformView);
        Assert.Equal(1, layouts);
        foreach (string name in new[] { nameof(vm.IsWaveformView), nameof(vm.IsSplitView), nameof(vm.IsSpectrogramView) })
            Assert.Contains(name, raised);

        vm.ShowSpectrogramCommand.Execute(null);
        Assert.True(vm.IsSpectrogramView);
        Assert.False(vm.IsSplitView);

        vm.ShowWaveformCommand.Execute(null);
        Assert.True(vm.IsWaveformView);
        Assert.Equal(3, layouts);
    }

    [Fact]
    public void ReSelectingTheCurrentModeDoesNotRelayout()
    {
        var vm = new MainViewModel();
        int layouts = 0;
        vm.EditorViewChanged += () => layouts++;

        vm.ShowWaveformCommand.Execute(null);   // already the current mode

        Assert.Equal(0, layouts);
        Assert.True(vm.IsWaveformView);
    }
}
