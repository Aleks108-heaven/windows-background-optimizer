using System.Runtime.InteropServices;

namespace BgOptimizer.Services;

/// <summary>
/// Tells the workload optimizer which processes the user is actively driving right
/// now, so automatic throttling never touches them even if they briefly sit idle
/// (e.g. reading a page before the next click).
/// </summary>
internal static class ForegroundWindow
{
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Every PID that owns the foreground window or a window inside it. A Store (UWP) app's
    /// top-level window belongs to ApplicationFrameHost while the app's own process owns a
    /// child window, so the owner PID alone would leave the app the user is using unprotected.
    /// </summary>
    public static HashSet<int> GetForegroundPids()
    {
        var pids = new HashSet<int>();
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return pids;

        GetWindowThreadProcessId(hWnd, out var owner);
        if (owner != 0) pids.Add((int)owner);

        EnumChildWindows(hWnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid != 0) pids.Add((int)childPid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }
}
