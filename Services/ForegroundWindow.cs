using System.Runtime.InteropServices;

namespace BgOptimizer.Services;

/// <summary>
/// Tells the workload optimizer which process the user is actively driving right
/// now, so automatic throttling never touches it even if it briefly sits idle
/// (e.g. reading a page before the next click).
/// </summary>
internal static class ForegroundWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static int? GetForegroundPid()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }
}
