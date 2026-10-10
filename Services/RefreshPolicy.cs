namespace BgOptimizer.Services;

/// <summary>
/// When the periodic refresh (process enumeration, WMI battery query, stats) needs to run. A tray app that
/// nobody is looking at and that has no standing job should cost nothing, yet stay one click away.
/// </summary>
internal static class RefreshPolicy
{
    /// <summary>
    /// Runs while the window is visible (the user is looking at live data) or while Auto-Optimize is on
    /// (it is a standing job that has to keep watching memory and releasing what it changed). Hidden in the
    /// tray with Auto-Optimize off, there is nothing to do, so the app idles.
    /// </summary>
    public static bool ShouldRun(bool windowVisible, bool autoOptimizeOn) => windowVisible || autoOptimizeOn;
}
