using WaveLab.Audio;
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
}
