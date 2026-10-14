using System.Diagnostics;
using BgOptimizer.Localization;
using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// Makes a process-priority choice survive reboots via the same Image File
/// Execution Options mechanism Task Manager's "Efficiency Mode" uses: Windows
/// re-reads PerfOptions\CpuPriorityClass every time an exe with that name
/// launches, so the value doesn't need to be reapplied by anything running.
/// This is semi-documented behavior (confirmed by independent sources, not
/// an official Microsoft API) with two real caveats: it matches by bare exe
/// filename, so it applies to every instance of that exe system-wide, not
/// just the one selected in the grid; and IFEO is also how debuggers get
/// attached to a process at launch, so some security software watches
/// writes under this key.
/// </summary>
internal static class PriorityPersistence
{
    private const string IfeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static readonly Dictionary<ProcessPriorityClass, int> PriorityToCpuPriorityClass = new()
    {
        [ProcessPriorityClass.Idle] = 1,
        [ProcessPriorityClass.Normal] = 2,
        [ProcessPriorityClass.High] = 3,
        [ProcessPriorityClass.RealTime] = 4,
        [ProcessPriorityClass.BelowNormal] = 5,
        [ProcessPriorityClass.AboveNormal] = 6,
    };

    private static string ExeName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";

    public static ActionResult SetPersistent(string processName, ProcessPriorityClass priority)
    {
        if (ViewOnlyMode.Block(processName) is { } viewOnly) return viewOnly;
        if (ProtectedItems.IsProcessProtected(processName))
            return new ActionResult(false, "Msg_ProcessProtectedModify", processName);

        if (priority is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal
            && ProtectedItems.IsAiTool(processName))
            return new ActionResult(false, "Msg_ProcessAiTool", processName);

        if (priority is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal
            && ProtectedItems.IsInteractive(processName))
            return new ActionResult(false, "Msg_ProcessInteractive", processName);

        if (!PriorityToCpuPriorityClass.TryGetValue(priority, out var value))
            return new ActionResult(false, "Msg_PriorityPersistFailed", processName, "unsupported priority class");

        try
        {
            var exe = ExeName(processName);
            using var ifeoKey = Registry.LocalMachine.CreateSubKey($@"{IfeoBase}\{exe}", writable: true);
            using var perfKey = ifeoKey!.CreateSubKey("PerfOptions", writable: true);
            perfKey!.SetValue("CpuPriorityClass", value, RegistryValueKind.DWord);
            return new ActionResult(true, "Msg_PriorityPersisted", processName, priority);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_PriorityPersistFailed", processName, ex.Message);
        }
    }

    public static ActionResult ClearPersistent(string processName)
    {
        if (ViewOnlyMode.Block(processName) is { } viewOnly) return viewOnly;
        try
        {
            var exe = ExeName(processName);
            using var perfKey = Registry.LocalMachine.OpenSubKey($@"{IfeoBase}\{exe}\PerfOptions", writable: true);
            perfKey?.DeleteValue("CpuPriorityClass", throwOnMissingValue: false);
            return new ActionResult(true, "Msg_PriorityPersistCleared", processName);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_PriorityPersistFailed", processName, ex.Message);
        }
    }

    /// <summary>Removes any leftover persistent low-priority rule on interactive apps (needs admin).</summary>
    public static void ClearInteractiveRules()
    {
        foreach (var name in ProtectedItems.InteractiveProcessNames)
            if (IsPersisted(name)) ClearPersistent(name);
    }

    public static bool IsPersisted(string processName)
    {
        try
        {
            var exe = ExeName(processName);
            using var perfKey = Registry.LocalMachine.OpenSubKey($@"{IfeoBase}\{exe}\PerfOptions", writable: false);
            return perfKey?.GetValue("CpuPriorityClass") != null;
        }
        catch
        {
            return false;
        }
    }
}
