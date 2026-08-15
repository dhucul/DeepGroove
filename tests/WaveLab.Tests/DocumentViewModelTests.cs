using WaveLab.Audio;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class DocumentViewModelTests
{
    [Fact]
    public void EnsurePlayheadVisible_FollowsContinuouslyAfterTrailingAnchor()
    {
        var vm = CreateDocument(length: 10_000);
        vm.ViewWidthPixels = 100;
        vm.SamplesPerPixel = 10;
        vm.ViewStart = 2_000;

        vm.PlayheadSample = 2_750;
        vm.EnsurePlayheadVisible();
        Assert.Equal(2_000, vm.ViewStart);

        vm.PlayheadSample = 2_780;
        vm.EnsurePlayheadVisible();
        Assert.Equal(2_030, vm.ViewStart);

        vm.PlayheadSample = 2_815;
        vm.EnsurePlayheadVisible();
        Assert.Equal(2_065, vm.ViewStart);
    }

    [Fact]
    public void EnsurePlayheadVisibleRaisesOneViewChangePerFollowStep()
    {
        var vm = CreateDocument(length: 10_000);
        vm.ViewWidthPixels = 100;
        vm.SamplesPerPixel = 10;
        vm.ViewStart = 2_000;
        int notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.ViewStart)) notifications++;
        };

        vm.PlayheadSample = 2_800;
        vm.EnsurePlayheadVisible();

        Assert.Equal(1, notifications);
        Assert.Equal(2_050, vm.ViewStart);
    }

    [Fact]
    public void EnsurePlayheadVisible_LeavesAheadOfPlayheadVisibleAndClampsAtEnd()
    {
        var vm = CreateDocument(length: 10_000);
        vm.ViewWidthPixels = 100;
        vm.SamplesPerPixel = 10;
        vm.ViewStart = 8_900;

        vm.PlayheadSample = 9_900;
        vm.EnsurePlayheadVisible();

        Assert.Equal(9_000, vm.ViewStart);
    }

    [Fact]
    public void EnsurePlayheadVisible_BringsAPlayheadBehindTheViewToItsLeftEdge()
    {
        var vm = CreateDocument(length: 10_000);
        vm.ViewWidthPixels = 100;
        vm.SamplesPerPixel = 10;
        vm.ViewStart = 5_000;

        vm.PlayheadSample = 4_500;
        vm.EnsurePlayheadVisible();

        Assert.Equal(4_500, vm.ViewStart);
    }

    [Fact]
    public void ZoomBy_WithVisibleAnchorKeepsItsScreenPosition()
    {
        var vm = CreateDocument(length: 100_000);
        vm.ViewWidthPixels = 100;
        vm.SamplesPerPixel = 10;
        vm.ViewStart = 2_000;
        const int playhead = 2_750;
        double originalPixel = (playhead - vm.ViewStart) / vm.SamplesPerPixel;

        vm.ZoomBy(0.5, playhead);

        double zoomedPixel = (playhead - vm.ViewStart) / vm.SamplesPerPixel;
        Assert.Equal(originalPixel, zoomedPixel, precision: 10);
    }

    [Fact]
    public void ZoomBy_WithOffscreenAnchorCentersTheRequestedSample()
    {
        var vm = CreateDocument(length: 100_000);
        vm.ViewWidthPixels = 100;
        vm.SamplesPerPixel = 10;
        vm.ViewStart = 2_000;
        vm.PlayheadSample = 8_000;

        vm.ZoomBy(0.5, anchorSample: 8_000);

        double anchorPixel = (8_000 - vm.ViewStart) / vm.SamplesPerPixel;
        Assert.Equal(50, anchorPixel, precision: 10);

        double zoomedViewStart = vm.ViewStart;
        vm.EnsurePlayheadVisible();
        Assert.Equal(zoomedViewStart, vm.ViewStart, precision: 10);
    }

    private static DocumentViewModel CreateDocument(int length) =>
        new(new AudioDocument([new float[length]], 48_000, 32));
}
