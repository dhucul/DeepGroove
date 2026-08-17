using System.Windows;

namespace WaveLab.Util;

/// <summary>
/// Whether a remembered window position still lands somewhere the user can reach it.
/// </summary>
/// <remarks>
/// <para>
/// A stored position is a claim about a monitor arrangement, and the arrangement can change after
/// the claim was written: undock a laptop, unplug a second display, or park the window off the
/// desktop for an offscreen render. The window then opens where no screen covers — and it still
/// opens, still takes the keyboard, still appears in the task bar, so <b>the app reads as failing
/// to start rather than as misplaced</b>. Worse, the position is written back on the way out, so it
/// survives a restart, a reinstall, and every other thing a user would reasonably try.
/// </para>
/// <para>
/// <b>The test is on the caption band, not the window.</b> A window whose body is on screen but
/// whose title bar sits above it cannot be dragged back, so body overlap is not what makes a
/// placement recoverable — being able to grab it is.
/// </para>
/// </remarks>
public static class WindowPlacement
{
    /// <summary>Height of the draggable caption, matching <c>WindowChrome.CaptionHeight</c>.</summary>
    public const double CaptionHeight = 40;

    /// <summary>
    /// How much of the caption has to be on screen to count as reachable. A few stray pixels are
    /// not a handle; this is about a title bar's worth of something to aim at.
    /// </summary>
    public const double MinimumVisible = 120;

    /// <summary>The whole desktop across every monitor, in device-independent units.</summary>
    public static Rect VirtualScreen => new(
        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// Whether enough of <paramref name="window"/>'s caption falls inside
    /// <paramref name="virtualScreen"/> for the user to see and drag it.
    /// </summary>
    public static bool IsReachable(Rect window, Rect virtualScreen)
    {
        if (window.IsEmpty || window.Width <= 0 || window.Height <= 0) return false;
        if (virtualScreen.IsEmpty || virtualScreen.Width <= 0 || virtualScreen.Height <= 0) return false;

        // A window shorter than the caption is all caption.
        Rect caption = new(
            window.Left, window.Top, window.Width, Math.Min(CaptionHeight, window.Height));
        Rect visible = Rect.Intersect(caption, virtualScreen);

        return !visible.IsEmpty
            && visible.Width >= MinimumVisible
            && visible.Height >= Math.Min(CaptionHeight, window.Height) / 2;
    }
}
