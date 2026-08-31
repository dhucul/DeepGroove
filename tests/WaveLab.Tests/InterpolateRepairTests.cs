using WaveLab.Audio;
using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

/// <summary>The manual Restore-menu repair, including every boundary branch.</summary>
public sealed class InterpolateRepairTests
{
    private const int Rate = 48_000;

    [Fact]
    public void InteriorSelectionBridgesTheCleanSamplesOnEachChannel()
    {
        var document = new AudioDocument(
        [
            [9, 0, 99, -99, 99, 4, 8],
            [-9, 4, -99, 99, -99, 0, -8],
        ], Rate, 32);

        Assert.True(Processing.InterpolateRepair(document, 2, 3));

        Assert.Equal([9, 0, 1, 2, 3, 4, 8], document.Channels[0]);
        Assert.Equal([-9, 4, 3, 2, 1, 0, -8], document.Channels[1]);
        Assert.Equal("Interpolate Repair", document.NextUndoName);
    }

    [Fact]
    public void AOneSampleDefectBecomesTheMeanOfItsNeighbours()
    {
        var document = new AudioDocument([[2, 100, 6]], Rate, 32);

        Assert.True(Processing.InterpolateRepair(document, 1, 1));

        Assert.Equal([2, 4, 6], document.Channels[0]);
    }

    [Fact]
    public void SelectionAtTheStartUsesTheCleanRightBoundaryWithoutExtrapolating()
    {
        var document = new AudioDocument([[99, -99, 3, 4]], Rate, 32);

        Assert.True(Processing.InterpolateRepair(document, 0, 2));

        Assert.Equal([3, 3, 3, 4], document.Channels[0]);
    }

    [Fact]
    public void SelectionAtTheEndUsesTheCleanLeftBoundaryWithoutExtrapolating()
    {
        var document = new AudioDocument([[1, 2, 99, -99]], Rate, 32);

        Assert.True(Processing.InterpolateRepair(document, 2, 2));

        Assert.Equal([1, 2, 2, 2], document.Channels[0]);
    }

    [Fact]
    public void WholeFileSelectionHasNoCleanAnchorAndCreatesNoEdit()
    {
        var document = new AudioDocument([[1, 2, 3]], Rate, 32);

        Assert.False(Processing.InterpolateRepair(document, 0, document.Length));

        Assert.Equal([1, 2, 3], document.Channels[0]);
        Assert.Equal(0, document.HistoryCount);
        Assert.False(document.Dirty);
    }

    [Fact]
    public void RepairIsOneUndoableEditThatRestoresTheDefectExactly()
    {
        float[] damaged = [0, 20, -20, 3];
        var document = new AudioDocument([(float[])damaged.Clone()], Rate, 32);

        Processing.InterpolateRepair(document, 1, 2);
        document.Undo();

        Assert.Equal(damaged, document.Channels[0]);
    }

    [Fact]
    public void ModelBasedRepairCarriesAnOscillatingWaveformThroughTheGap()
    {
        var clean = new float[Rate * 2];
        for (int i = 0; i < clean.Length; i++)
            clean[i] = (float)(0.45 * Math.Sin(2 * Math.PI * 997 * i / Rate)
                             + 0.15 * Math.Sin(2 * Math.PI * 2_311 * i / Rate));
        float[] damaged = (float[])clean.Clone();
        int start = Rate;
        const int count = 96;
        Array.Fill(damaged, 0.9f, start, count);
        float left = damaged[start - 1], right = damaged[start + count];
        double linearError = 0;
        for (int i = 0; i < count; i++)
        {
            double t = (i + 1.0) / (count + 1.0);
            double linear = left + (right - left) * t;
            linearError += Math.Pow(linear - clean[start + i], 2);
        }
        var document = new AudioDocument([damaged], Rate, 32);

        Processing.InterpolateRepair(document, start, count);

        double repairedError = 0;
        for (int i = 0; i < count; i++)
            repairedError += Math.Pow(document.Channels[0][start + i] - clean[start + i], 2);
        Assert.True(repairedError < linearError * 0.2,
            $"model error was {repairedError / linearError:P1} of a straight bridge");
    }

    [Fact]
    public void RoughSelectionIsNarrowedToTheShortDefectInsideIt()
    {
        float[][] audio = [Program()];
        int defectStart = Rate;
        const int defectLength = 24;
        for (int i = defectStart; i < defectStart + defectLength; i++) audio[0][i] += 0.75f;
        int roughStart = Rate / 2;
        int roughCount = Rate;

        InterpolateRepairTarget? target = Processing.LocateInterpolateRepairTarget(
            audio, Rate, roughStart, roughCount);

        Assert.NotNull(target);
        Assert.True(target.Value.Start <= defectStart + 1,
            $"repair starts at {target.Value.Start}, defect starts at {defectStart}");
        Assert.True(target.Value.Start + target.Value.Count >= defectStart + defectLength - 1,
            $"repair ends at {target.Value.Start + target.Value.Count}, "
            + $"defect ends at {defectStart + defectLength}");
        Assert.True(target.Value.Count <= Processing.MaximumDirectInterpolationSamples(Rate));
        Assert.True(target.Value.Count < roughCount / 100);
    }

    [Fact]
    public void RoughSelectionWithNoShortDefectIsNotFlattened()
    {
        float[][] clean = [new float[Rate]];

        InterpolateRepairTarget? target = Processing.LocateInterpolateRepairTarget(
            clean, Rate, Rate / 4, Rate / 2);

        Assert.Null(target);
    }

    [Fact]
    public void RoughSelectionSearchHonoursCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Processing.LocateInterpolateRepairTarget(
                [Program()], Rate, Rate / 4, Rate, cancelled.Token));
    }

    [Fact]
    public void SearchAreaLongerThanTenSecondsIsRejectedBeforeItIsCopied()
    {
        int limit = Processing.MaximumAutomaticInterpolationSearchSamples(Rate);
        float[][] audio = [new float[limit + 2]];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Processing.LocateInterpolateRepairTarget(audio, Rate, 0, limit + 1));
    }

    [Fact]
    public void TransitiveEventClusterMergesRegardlessOfDetectorOrder()
    {
        // The leftmost event is deliberately first. A one-pass merge examined it before the bridge
        // event expanded the repair leftward, skipped it, and never came back.
        ClickEvent[] events =
        [
            Event(80, 89),
            Event(90, 99),
            Event(100, 110),
        ];

        (int start, int end) = Processing.MergeInterpolateRepairEvents(
            events, analysisStart: 0, selectionStart: 0, selectionEnd: 200,
            repairStart: 100, repairEnd: 110, directLimit: 100, mergeDistance: 5);

        Assert.Equal(80, start);
        Assert.Equal(110, end);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(5, 0)]
    [InlineData(1, -1)]
    [InlineData(2, 3)]
    public void InvalidRangesAreRejected(int start, int count)
    {
        var document = new AudioDocument([[0, 1, 2, 3]], Rate, 32);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Processing.InterpolateRepair(document, start, count));
    }

    private static float[] Program()
    {
        var audio = new float[Rate * 2];
        for (int i = 0; i < audio.Length; i++)
            audio[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / Rate)
                             + 0.08 * Math.Sin(2 * Math.PI * 1_731 * i / Rate));
        return audio;
    }

    private static ClickEvent Event(int start, int end) => new(
        Channel: 0,
        StartSample: start,
        EndSample: end,
        PeakSample: start,
        Kind: ImpulseDefectKind.Click,
        Confidence: 1,
        Severity: 1,
        PeakAmplitude: 1,
        DetectionThreshold: 0.1f);
}
