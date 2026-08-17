using System.Windows;
using WaveLab.Util;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The rule that decides whether a remembered window position is still worth honouring.
/// </summary>
/// <remarks>
/// This exists because of a real failure: the settings file held <c>-4000, -4000</c> — written by
/// an offscreen render probe that parked the window off the desktop and then let the ordinary
/// close path save it — and the app opened there on every subsequent run. It looked exactly like a
/// program that would not start, and because the position outlived the process it survived
/// reinstalling an older build too.
/// </remarks>
public sealed class WindowPlacementTests
{
    /// <summary>One 2560×1440 monitor at the origin: the machine this was found on.</summary>
    private static readonly Rect SingleScreen = new(0, 0, 2560, 1440);

    [Fact]
    public void TheRenderProbePositionIsRejected()
    {
        Assert.False(WindowPlacement.IsReachable(new Rect(-4000, -4000, 1800, 1000), SingleScreen));
    }

    [Fact]
    public void AnOrdinaryPositionIsKept()
    {
        Assert.True(WindowPlacement.IsReachable(new Rect(380, 220, 1800, 1000), SingleScreen));
    }

    [Fact]
    public void APositionOnAMonitorThatHasBeenUnpluggedIsRejected()
    {
        // Yesterday there was a second display to the right; today there is not.
        Assert.False(WindowPlacement.IsReachable(new Rect(2600, 300, 1800, 1000), SingleScreen));
    }

    [Fact]
    public void APositionOnAMonitorToTheLeftIsKeptWhenThatMonitorIsStillThere()
    {
        // A left-hand display puts the virtual screen's origin at a negative x. Negative is not by
        // itself wrong, which is why the test is against the desktop rather than against zero.
        Rect twoScreens = new(-2560, 0, 5120, 1440);
        Assert.True(WindowPlacement.IsReachable(new Rect(-2000, 200, 1800, 1000), twoScreens));
    }

    [Fact]
    public void ACaptionAboveTheDesktopIsRejectedEvenThoughTheBodyIsVisible()
    {
        // Most of this window is on screen. None of the title bar is, so it cannot be dragged back
        // — which is the thing that makes a placement recoverable.
        Assert.False(WindowPlacement.IsReachable(new Rect(400, -60, 1800, 1000), SingleScreen));
    }

    [Fact]
    public void ASliverOfCaptionIsNotAHandle()
    {
        double left = SingleScreen.Right - (WindowPlacement.MinimumVisible - 1);
        Assert.False(WindowPlacement.IsReachable(new Rect(left, 300, 1800, 1000), SingleScreen));
    }

    [Fact]
    public void ExactlyTheMinimumIsEnough()
    {
        double left = SingleScreen.Right - WindowPlacement.MinimumVisible;
        Assert.True(WindowPlacement.IsReachable(new Rect(left, 300, 1800, 1000), SingleScreen));
    }

    [Fact]
    public void ADegenerateWindowOrDesktopIsRejectedRatherThanTrusted()
    {
        Assert.False(WindowPlacement.IsReachable(new Rect(0, 0, 0, 0), SingleScreen));
        Assert.False(WindowPlacement.IsReachable(Rect.Empty, SingleScreen));
        Assert.False(WindowPlacement.IsReachable(new Rect(0, 0, 1800, 1000), Rect.Empty));
    }
}
