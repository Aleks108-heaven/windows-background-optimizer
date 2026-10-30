namespace BgOptimizer.Services;

/// <summary>
/// What the window's close button does. A tray app that quits when its window is closed silently stops the load
/// alert and Auto-Optimize (the tray icon vanishes with it), so by default closing only hides the window and the
/// tray menu's Exit is what quits. Three things must still really close the window: Exit from the tray, Windows
/// signing out or shutting down, and an installer asking the app to close for an update. Hiding in any of those
/// would make Windows report the app as one that blocks shutdown.
/// </summary>
internal static class CloseBehavior
{
    public static bool ShouldHide(bool keepRunningInTray, bool exiting, bool sessionEnding) =>
        keepRunningInTray && !exiting && !sessionEnding;
}
