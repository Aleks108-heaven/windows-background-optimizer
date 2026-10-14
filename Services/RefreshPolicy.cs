namespace BgOptimizer.Services;

/// <summary>
/// When the periodic refresh (process enumeration, WMI battery query, stats) needs to run. A tray app that
/// nobody is looking at and that has no standing job should cost nothing, yet stay one click away.
/// </summary>
internal static class RefreshPolicy
{
    /// <summary>
    /// Runs while the window is visible (the user is looking at live data) or while a standing job needs it:
    /// Auto-Optimize (it has to keep watching memory and releasing what it changed) or the load alert (it has
    /// to keep watching the load). Hidden in the tray with neither on, there is nothing to do, so the app idles.
    /// </summary>
    public static bool ShouldRun(bool windowVisible, bool autoOptimizeOn, bool loadAlertOn = false) =>
        windowVisible || autoOptimizeOn || loadAlertOn;
}
