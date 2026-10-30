using System.Diagnostics;
using System.IO;
using BgOptimizer.Localization;
using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// Restarts the Windows shell (explorer.exe: taskbar, Start, desktop and File Explorer windows). Explorer grows over
/// days of use (thousands of handles, hundreds of MB) and only a restart gives that memory back; trimming its working
/// set just lets the pages fault back in. It is its own action because "explorer" is on the protected list, where
/// kill, priority and freeze are all refused. The shell is not started by this app: Windows (Winlogon) starts a new
/// one within a second or two, as the same user and without administrator rights. Starting explorer.exe from this
/// elevated app instead would run the taskbar and desktop with administrator rights, which is why it is only done
/// when Windows is set up to restart the shell itself and is otherwise refused.
/// </summary>
internal static class ExplorerRestarter
{
    private static readonly TimeSpan WaitForShell = TimeSpan.FromSeconds(15);

    /// <summary>True for the real shell: Windows' own explorer.exe, in the given session. A look-alike elsewhere is not ours to kill.</summary>
    internal static bool IsShell(string? imagePath, int sessionId, string windowsDirectory, int mySessionId) =>
        imagePath is not null && sessionId == mySessionId &&
        string.Equals(imagePath, Path.Combine(windowsDirectory, "explorer.exe"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows brings the shell back by itself only with the default Winlogon settings (the shell is explorer.exe and
    /// AutoRestartShell is not 0). With a custom shell, or restart switched off, killing it would leave no desktop.
    /// </summary>
    internal static bool WindowsRestartsTheShell(object? shell, object? autoRestartShell) =>
        (shell is null || (shell is string s && s.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)))
        && (autoRestartShell is null || (autoRestartShell is int restart && restart != 0));

    public static ActionResult Restart()
    {
        if (ViewOnlyMode.Block("explorer") is { } viewOnly) return viewOnly;
        try
        {
            var (shell, autoRestart) = ReadWinlogon();
            if (!WindowsRestartsTheShell(shell, autoRestart)) return new ActionResult(false, "Msg_ExplorerNoAutoRestart");

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            int session;
            using (var self = Process.GetCurrentProcess()) session = self.SessionId;

            var oldIds = new HashSet<int>();
            long usedBytes = 0;
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                using (process)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { /* not readable: not ours */ }
                    if (!IsShell(path, process.SessionId, windows, session)) continue;
                    try
                    {
                        usedBytes += process.WorkingSet64;
                        process.Kill();
                        oldIds.Add(process.Id);
                    }
                    catch (Exception ex)
                    {
                        ActionLog.Write($"EXPLORER_RESTART could not end PID {process.Id}: {ex.Message}");
                    }
                }
            }
            if (oldIds.Count == 0) return new ActionResult(false, "Msg_ExplorerNotFound");

            var deadline = DateTime.UtcNow + WaitForShell;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                if (!ShellIsBack(windows, session, oldIds)) continue;
                var mb = (usedBytes / 1024.0 / 1024.0).ToString("F0");
                ActionLog.Write($"EXPLORER_RESTART ok (was {mb} MB in {oldIds.Count} process(es))");
                return new ActionResult(true, "Msg_ExplorerRestarted", mb);
            }
            ActionLog.Write("EXPLORER_RESTART the shell did not come back within the wait");
            return new ActionResult(false, "Msg_ExplorerNotBack");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_ExplorerFailed", ex.Message);
        }
    }

    private static bool ShellIsBack(string windows, int session, ISet<int> oldIds)
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (oldIds.Contains(process.Id)) continue;
                try
                {
                    if (IsShell(process.MainModule?.FileName, process.SessionId, windows, session)) return true;
                }
                catch { /* still starting */ }
            }
        }
        return false;
    }

    private static (object? Shell, object? AutoRestart) ReadWinlogon()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            return (key?.GetValue("Shell"), key?.GetValue("AutoRestartShell"));
        }
        catch { return (null, null); } // unreadable: Windows' defaults are the safe assumption
    }
}
