using WaveLab.Audio;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class DocumentViewModelTests
{
    [Fact]
    public void MarkerEditsRemainDirtyUntilThatExactVersionIsEmbedded()
    {
        var vm = CreateDocument(length: 1_000);
        vm.Doc.MarkSaved();
        Assert.False(vm.IsDirty);

        vm.AddMarker(100, "Cut");
        int changedVersion = vm.MarkersVersion;
        Assert.True(vm.IsDirty);

        vm.MarkMarkersEmbedded(changedVersion - 1);
        Assert.True(vm.IsDirty);

        vm.MarkMarkersEmbedded(changedVersion);
        Assert.False(vm.IsDirty);
    }

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

    /// <summary>
    /// The Edit History panel is modeless and re-reads on this, exactly as the markers panel
    /// re-reads on MarkersVersion. A jump is one change, so it must be one notification — not one
    /// per step crossed.
    /// </summary>
    [Fact]
    public void AHistoryJumpRaisesHistoryVersionExactlyOnce()
    {
        var vm = CreateDocument(length: 10_000);
        for (int i = 0; i < 4; i++)
            vm.Doc.ReplaceRange(0, 100, [new float[100]], $"edit {i}");

        int notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.HistoryVersion)) notifications++;
        };

        vm.Doc.JumpToHistoryPosition(0);

        Assert.Equal(1, notifications);
    }

    /// <summary>
    /// Saving moves no samples, but it does move the mark the history draws beside the saved step.
    /// </summary>
    [Fact]
    public void SavingMovesTheHistorySavepointMark()
    {
        var vm = CreateDocument(length: 10_000);
        vm.Doc.ReplaceRange(0, 100, [new float[100]], "edit");

        int notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.HistoryVersion)) notifications++;
        };

        vm.Doc.MarkSaved();
        vm.NotifySaved();

        Assert.Equal(1, notifications);
        Assert.True(vm.Doc.GetHistory().Entries[0].IsSavepoint);
    }

    /// <summary>
    /// The coalesced change a jump raises is what re-anchors everything on the timeline. A cursor
    /// past the end of the restored document is the cheapest way to see it arrive.
    /// </summary>
    [Fact]
    public void AHistoryJumpReAnchorsTheCursorThroughTheCoalescedChange()
    {
        var vm = CreateDocument(length: 1_000);
        vm.Doc.ReplaceRange(200, 0, [new float[4_000]], "Insert Silence");
        vm.SetCursor(4_500, clearSelection: true);

        vm.Doc.JumpToHistoryPosition(0);

        Assert.Equal(1_000, vm.Doc.Length);
        Assert.True(vm.Cursor < vm.Doc.Length, $"the cursor was left at {vm.Cursor} in a 1000 sample document.");
    }

    [Fact]
    public void InsertionAtAnAnchorUsesTheSameBoundaryForMarkersAndRegions()
    {
        var vm = CreateDocument(length: 1_000);
        vm.SetCursor(100, clearSelection: true);
        vm.Markers.Add(new Marker { Name = "Boundary", Position = 100 });
        vm.Regions.Add(new NamedRegion { Name = "Following", Start = 100, End = 200 });

        vm.Doc.ReplaceRange(100, 0, [new float[25]], "Insert");

        Assert.Equal(100, vm.Cursor);
        Assert.Equal(100, Assert.Single(vm.Markers).Position);
        NamedRegion region = Assert.Single(vm.Regions);
        Assert.Equal(100, region.Start);
        Assert.Equal(225, region.End);
    }

    [Fact]
    public void RecoveryMetadataReplacesAnOlderSidecarSnapshotWithoutSchedulingASave()
    {
        var vm = CreateDocument(length: 1_000);
        vm.Markers.Add(new Marker { Name = "Old", Position = 10 });

        vm.RestoreAutosavedMarkers(
            [new Marker { Name = "Recovered", Position = 150 }],
            [new NamedRegion { Name = "Recovered region", Start = 100, End = 300 }]);

        Assert.Equal("Recovered", Assert.Single(vm.Markers).Name);
        Assert.Equal((100, 300), (Assert.Single(vm.Regions).Start, Assert.Single(vm.Regions).End));
    }

    private static DocumentViewModel CreateDocument(int length) =>
        new(new AudioDocument([new float[length]], 48_000, 32));
}
