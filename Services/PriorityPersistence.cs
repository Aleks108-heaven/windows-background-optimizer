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
            && ProtectedItems.NeverTouchMessageKey(processName) is { } neverTouch)
            return new ActionResult(false, neverTouch, processName);

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

    /// <summary>The CpuPriorityClass values that slow a program down (Idle and Below normal); Normal and above are left alone.</summary>
    internal static bool IsLowRule(int value) => value is 1 or 5;

    /// <summary>
    /// Programs whose leftover slow-down rules are removed at start: interactive apps (as before), work agents and
    /// Windows/driver helpers. Not included: audio apps, local-AI tools and the user's own list. The app never
    /// wrote rules for those since it refused them, so one that exists may be somebody's deliberate choice.
    /// </summary>
    internal static IEnumerable<string> UnsafeRuleNames() =>
        ProtectedItems.InteractiveProcessNames
            .Concat(ProtectedItems.AlwaysOnAgentNames)
            .Concat(ProtectedItems.PlatformHelperNames)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Removes leftover permanent slow-down rules (Idle or Below normal) on programs that must not be slowed,
    /// which older versions can leave behind: they apply from the moment the program starts, including during
    /// sleep and wake, and nothing in the window shows them. A Normal-or-higher rule is the user's choice and is
    /// kept. Each removal is written to the action log with the value it had. Needs administrator rights, and
    /// does nothing in view-only mode.
    /// </summary>
    /// <returns>The rules removed, with the priority each one set.</returns>
    public static IReadOnlyList<(string Name, string Was)> ClearUnsafeRules() =>
        ClearUnsafeRules(UnsafeRuleNames(), ReadPersisted, ClearPersistent, message => ActionLog.Write(message));

    /// <summary>The store and the log are passed in so the logic is tested without touching the real registry or action log.</summary>
    internal static IReadOnlyList<(string Name, string Was)> ClearUnsafeRules(
        IEnumerable<string> names, Func<string, int?> read, Func<string, ActionResult> clear, Action<string> log)
    {
        var cleared = new List<(string, string)>();
        foreach (var name in names)
        {
            if (read(name) is not { } value || !IsLowRule(value)) continue;
            if (!clear(name).Ok) continue; // refused (view-only mode) or not allowed to write: nothing was removed, so nothing is claimed
            var was = value == 1 ? nameof(ProcessPriorityClass.Idle) : nameof(ProcessPriorityClass.BelowNormal);
            log($"IFEO_CLEAR name=\"{name}\" was={was} (a program the app never slows down)");
            cleared.Add((name, was));
        }
        return cleared;
    }

    public static bool IsPersisted(string processName) => ReadPersisted(processName) != null;

    internal static int? ReadPersisted(string processName)
    {
        try
        {
            var exe = ExeName(processName);
            using var perfKey = Registry.LocalMachine.OpenSubKey($@"{IfeoBase}\{exe}\PerfOptions", writable: false);
            return perfKey?.GetValue("CpuPriorityClass") is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
