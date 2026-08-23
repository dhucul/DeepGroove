using System.Windows;
using System.Windows.Media;
using WaveLab.Audio;
using WaveLab.ViewModels;
using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The waveform is painted per device-pixel column into a bitmap, so the pieces worth
/// pinning down are the column→row mapping and the colour compositing — and, since a
/// selection can be resized by its edges, what a press takes hold of and what a drag
/// then does to the selection.
/// </summary>
public sealed class WaveformViewTests
{
    [Theory]
    [InlineData(-5.0, 0, 10, 0)]     // above the band clamps to the top row
    [InlineData(50.0, 0, 10, 10)]    // below the band clamps to the bottom row
    [InlineData(3.4, 0, 10, 3)]
    [InlineData(3.6, 0, 10, 4)]
    [InlineData(11.2, 8, 20, 11)]    // second channel band keeps its own top offset
    public void ClampRowRoundsAndStaysInsideTheBand(double y, int top, int bottom, int expected) =>
        Assert.Equal(expected, WaveformView.ClampRow(y, top, bottom));

    [Fact]
    public void OpaqueArgbKeepsTheBrushColourAndForcesFullAlpha()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x31, 0xA9, 0x98));
        Assert.Equal(unchecked((int)0xFF31A998), WaveformView.OpaqueArgb(brush));
    }

    [Fact]
    public void BlendOverCompositesTheTranslucentRmsColourOntoThePeakBand()
    {
        var over = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x00, 0x00));
        var under = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF));

        int argb = WaveformView.BlendOver(over, under);

        Assert.Equal(0xFF, (argb >> 24) & 0xFF); // the painted pixel is always opaque
        Assert.Equal(128, (argb >> 16) & 0xFF);
        Assert.Equal(0, (argb >> 8) & 0xFF);
        Assert.Equal(127, argb & 0xFF);
    }

    [Fact]
    public void BlendOverWithAnOpaqueBrushIsThatBrush()
    {
        var over = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56));
        var under = new SolidColorBrush(Colors.White);

        Assert.Equal(WaveformView.OpaqueArgb(over), WaveformView.BlendOver(over, under));
    }

    // ── what a press takes hold of ───────────────────────────────

    private const double Width = 800, Height = 320;

    /// <summary>Below the playhead's grip band, so the pointer is in the body of the wave.</summary>
    private const double Body = 160;

    private static WaveformGrab Grab(double x, double y, double playheadX,
        double selStartX = 100, double selEndX = 300, bool hasSelection = true) =>
        WaveformView.GrabAt(new Point(x, y), Width, playheadX, hasSelection, selStartX, selEndX);

    [Theory]
    [InlineData(500, WaveformGrab.NewSelection)]  // clear of everything
    [InlineData(100, WaveformGrab.SelectionStart)]
    [InlineData(300, WaveformGrab.SelectionEnd)]
    [InlineData(105, WaveformGrab.SelectionStart)]  // inside the 6 px grab zone
    [InlineData(107, WaveformGrab.NewSelection)]    // just outside it
    [InlineData(700, WaveformGrab.Playhead)]
    public void APressTakesHoldOfWhateverIsNearestUnderIt(double x, WaveformGrab expected) =>
        Assert.Equal(expected, Grab(x, Body, playheadX: 700));

    /// <summary>
    /// Building a selection puts the playhead on the anchor, so the edge the user just drew always
    /// has the playhead underneath it. In the body of the wave the edge wins, or that edge would be
    /// the one edge that could never be grabbed again.
    /// </summary>
    [Fact]
    public void ASelectionEdgeOutranksThePlayheadSharingItsColumn() =>
        Assert.Equal(WaveformGrab.SelectionStart, Grab(100, Body, playheadX: 100));

    /// <summary>...and the drawn triangle at the top is where the playhead is still reachable.</summary>
    [Fact]
    public void ThePlayheadKeepsItsGripBandAtTheTop() =>
        Assert.Equal(WaveformGrab.Playhead, Grab(100, 4, playheadX: 100));

    [Fact]
    public void WithNoSelectionThereAreNoEdgesToGrab() =>
        Assert.Equal(WaveformGrab.NewSelection, Grab(100, Body, playheadX: 700, hasSelection: false));

    /// <summary>An edge scrolled out of the view is not something the pointer can be near.</summary>
    [Fact]
    public void AnEdgeOffTheViewIsNotGrabbable()
    {
        Assert.Equal(WaveformGrab.NewSelection, Grab(2, Body, playheadX: 700, selStartX: -4, selEndX: 900));
        Assert.Equal(WaveformGrab.NewSelection, Grab(798, Body, playheadX: 700, selStartX: -4, selEndX: 900));
    }

    /// <summary>Between two edges within reach, the nearer one wins.</summary>
    [Fact]
    public void ANarrowSelectionHandsTheNearerEdgeOver()
    {
        Assert.Equal(WaveformGrab.SelectionStart, Grab(201, Body, playheadX: 700, selStartX: 200, selEndX: 204));
        Assert.Equal(WaveformGrab.SelectionEnd, Grab(203, Body, playheadX: 700, selStartX: 200, selEndX: 204));
    }

    // ── dragging an edge ─────────────────────────────────────────

    private const int Rate = 44_100, Length = 441_000, Spp = 128;

    /// <summary>Sample under a screen column, with the view at sample zero.</summary>
    private static int SampleAtX(double x) => (int)(x * Spp);

    /// <summary>
    /// Drives one gesture through a real control and returns what the document was left holding.
    /// Unless the caller says otherwise the selection starts at x 100..300, the playhead sits at
    /// sample zero and shift is not held.
    /// </summary>
    private static DocumentViewModel Gesture(Action<WaveformView> gesture, int playhead = 0,
        bool selection = true, int cursor = 0)
    {
        DocumentViewModel? result = null;
        RunOnUiThread(() =>
        {
            var channels = new[] { new float[Length], new float[Length] };
            var vm = new DocumentViewModel(new AudioDocument(channels, Rate, 32))
            {
                ViewWidthPixels = Width,
                SamplesPerPixel = Spp,
            };
            if (selection) vm.SetSelection(SampleAtX(100), SampleAtX(300));
            vm.SetCursor(cursor, clearSelection: false);
            vm.PlayheadSample = playhead;

            var view = new WaveformView { Document = vm, Width = Width, Height = Height };
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));

            gesture(view);
            result = vm;
        });
        return result!;
    }

    private static DocumentViewModel Drag(Point from, Point to, int playhead = 0,
        bool extend = false, bool selection = true, int cursor = 0) =>
        Gesture(view => view.PerformDrag(from, to, extend), playhead, selection, cursor);

    /// <summary>
    /// Press and release with no movement between, which a drag whose two points coincide is not:
    /// a real click raises no <c>MouseMove</c>, so anything only reached from the move handler never
    /// runs. Shift-click depends on that difference, so the tests have to express it.
    /// </summary>
    private static DocumentViewModel Click(Point point, int playhead = 0,
        bool extend = false, bool selection = true, int cursor = 0) =>
        Gesture(view => view.PerformClick(point, extend), playhead, selection, cursor);

    [Fact]
    public void DraggingTheEndEdgeLengthensTheSelectionAndLeavesTheStartAlone()
    {
        DocumentViewModel vm = Drag(new Point(300, Body), new Point(500, Body));

        Assert.Equal(SampleAtX(100), vm.SelStart);
        Assert.Equal(SampleAtX(500), vm.SelEnd);
    }

    [Fact]
    public void DraggingTheStartEdgeShortensTheSelectionFromTheFront()
    {
        DocumentViewModel vm = Drag(new Point(100, Body), new Point(200, Body));

        Assert.Equal(SampleAtX(200), vm.SelStart);
        Assert.Equal(SampleAtX(300), vm.SelEnd);
    }

    /// <summary>
    /// The anchor is the far edge, so passing it flips the selection exactly as building one does.
    /// </summary>
    [Fact]
    public void DraggingOneEdgePastTheOtherFlipsTheSelection()
    {
        DocumentViewModel vm = Drag(new Point(100, Body), new Point(500, Body));

        Assert.Equal(SampleAtX(300), vm.SelStart);
        Assert.Equal(SampleAtX(500), vm.SelEnd);
    }

    /// <summary>
    /// Dragging an edge onto the other collapses the selection rather than sticking at its last
    /// width, which is what the new-selection travel threshold would have done if it applied here.
    /// </summary>
    [Fact]
    public void DraggingOneEdgeOntoTheOtherClearsTheSelection() =>
        Assert.False(Drag(new Point(100, Body), new Point(300, Body)).HasSelection);

    /// <summary>Resizing is not seeking: the playhead on the grabbed edge stays where it was.</summary>
    [Fact]
    public void ResizingDoesNotMoveThePlayheadSharingTheEdge()
    {
        DocumentViewModel vm = Drag(new Point(100, Body), new Point(200, Body), playhead: SampleAtX(100));

        Assert.Equal(SampleAtX(100), vm.PlayheadSample);
        Assert.Equal(SampleAtX(200), vm.SelStart);
    }

    /// <summary>...and in the grip band the same press is a seek, so the selection does not move.</summary>
    [Fact]
    public void APressInTheGripBandLeavesTheSelectionAlone()
    {
        DocumentViewModel vm = Drag(new Point(100, 4), new Point(200, 4), playhead: SampleAtX(100));

        Assert.Equal(SampleAtX(100), vm.SelStart);
        Assert.Equal(SampleAtX(300), vm.SelEnd);
    }

    [Fact]
    public void APressClearOfTheEdgesStillStartsANewSelection()
    {
        DocumentViewModel vm = Drag(new Point(600, Body), new Point(700, Body));

        Assert.Equal(SampleAtX(600), vm.SelStart);
        Assert.Equal(SampleAtX(700), vm.SelEnd);
    }

    // ── shift+click to extend ────────────────────────────────────

    /// <summary>
    /// The nearer edge moves to the click, so the anchor is the other one — and a click past either
    /// end is nearer the end it is past, which is what makes shift-clicking beyond the selection
    /// lengthen it rather than collapse it.
    /// </summary>
    [Theory]
    [InlineData(500, 100)]   // past the end: the start holds
    [InlineData(50, 300)]    // past the start: the end holds
    [InlineData(140, 300)]   // inside, nearer the start: the start moves in
    [InlineData(260, 100)]   // inside, nearer the end: the end moves in
    [InlineData(200, 100)]   // exactly the midpoint goes to the end, and has to go somewhere
    public void ShiftClickAnchorsOnTheFarSideOfTheSelection(double clickX, double expectedAnchorX) =>
        Assert.Equal(SampleAtX(expectedAnchorX), WaveformView.ExtendAnchor(
            SampleAtX(clickX), hasSelection: true,
            SampleAtX(100), SampleAtX(300), cursor: SampleAtX(700)));

    /// <summary>With nothing selected there is no far edge, so the cursor is the anchor.</summary>
    [Fact]
    public void WithNothingSelectedShiftClickExtendsFromTheCursor() =>
        Assert.Equal(SampleAtX(700), WaveformView.ExtendAnchor(
            SampleAtX(500), hasSelection: false, selStart: -1, selEnd: -1, cursor: SampleAtX(700)));

    /// <summary>
    /// A shift-click is a click: it takes effect on the press, with no movement at all. Driven
    /// through <c>PerformClick</c> rather than a zero-length drag, because a zero-length drag still
    /// runs the move handler and would pass whether or not the press did anything.
    /// </summary>
    [Fact]
    public void ShiftClickingPastTheEndLengthensTheSelectionWithoutAnyDrag()
    {
        DocumentViewModel vm = Click(new Point(500, Body), extend: true);

        Assert.Equal(SampleAtX(100), vm.SelStart);
        Assert.Equal(SampleAtX(500), vm.SelEnd);
    }

    [Fact]
    public void ShiftClickingBeforeTheStartLengthensTheSelectionFromTheFront()
    {
        DocumentViewModel vm = Click(new Point(50, Body), extend: true);

        Assert.Equal(SampleAtX(50), vm.SelStart);
        Assert.Equal(SampleAtX(300), vm.SelEnd);
    }

    [Fact]
    public void ShiftClickingInsideTheSelectionShortensTheNearerEnd()
    {
        DocumentViewModel vm = Click(new Point(260, Body), extend: true);

        Assert.Equal(SampleAtX(100), vm.SelStart);
        Assert.Equal(SampleAtX(260), vm.SelEnd);
    }

    [Fact]
    public void ShiftClickingWithNothingSelectedSelectsBackToTheCursor()
    {
        DocumentViewModel vm = Click(new Point(500, Body),
            extend: true, selection: false, cursor: SampleAtX(200));

        Assert.Equal(SampleAtX(200), vm.SelStart);
        Assert.Equal(SampleAtX(500), vm.SelEnd);
    }

    /// <summary>
    /// The other half of that: without shift, a click that never moves clears the selection and
    /// leaves a cursor. Selecting a sample's width on every stray click is what the travel threshold
    /// in the move handler exists to prevent, and shift-click is the deliberate exception to it.
    /// </summary>
    [Fact]
    public void APlainClickThatNeverMovesSelectsNothing()
    {
        DocumentViewModel vm = Click(new Point(500, Body));

        Assert.False(vm.HasSelection);
        Assert.Equal(SampleAtX(500), vm.Cursor);
    }

    /// <summary>
    /// A shift-click leaves the drag armed on the same anchor, so it can be kept dragging rather
    /// than being a one-shot.
    /// </summary>
    [Fact]
    public void AShiftClickCanBeDraggedOnFromWhereItLanded()
    {
        DocumentViewModel vm = Drag(new Point(500, Body), new Point(600, Body), extend: true);

        Assert.Equal(SampleAtX(100), vm.SelStart);
        Assert.Equal(SampleAtX(600), vm.SelEnd);
    }

    /// <summary>
    /// Shift is read before what is under the pointer, so it means the same thing wherever the click
    /// lands — including on the playhead, which would otherwise take the press as a seek.
    /// </summary>
    [Fact]
    public void ShiftOutranksThePlayheadUnderThePointer()
    {
        DocumentViewModel vm = Click(new Point(500, Body), playhead: SampleAtX(500), extend: true);

        Assert.Equal(SampleAtX(100), vm.SelStart);
        Assert.Equal(SampleAtX(500), vm.SelEnd);
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
