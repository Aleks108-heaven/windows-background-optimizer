using System.Diagnostics;
using System.IO;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// Lists and toggles startup items the same ways Task Manager (and Task
/// Scheduler) do: the StartupApproved registry flag for Run-key entries, a
/// rename-to-.disabled trick for Startup-folder shortcuts (both reversible,
/// unlike deleting the Run value or shortcut outright), and schtasks.exe for
/// Scheduled Tasks - the mechanism most OEM bundleware (ASUS, Dell, HP, ...)
/// actually uses to relaunch its background agents, which Run keys and the
/// Startup folder never see.
/// </summary>
public class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public List<StartupEntry> GetAll()
    {
        var results = new List<StartupEntry>();

        results.AddRange(ReadRunKey(Registry.CurrentUser, StartupSource.RegistryRunHkcu));
        results.AddRange(ReadRunKey(Registry.LocalMachine, StartupSource.RegistryRunHklm));
        results.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.StartupFolderUser));
        results.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.StartupFolderCommon));
        results.AddRange(ReadScheduledTasks());

        foreach (var entry in results)
            entry.Category = ItemClassifier.ClassifyStartup(entry.Name, entry.Command);

        return results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<StartupEntry> ReadRunKey(RegistryKey hive, StartupSource source)
    {
        var list = new List<StartupEntry>();
        using var runKey = hive.OpenSubKey(RunKeyPath, writable: false);
        if (runKey == null) return list;

        using var approvedKey = hive.OpenSubKey(ApprovedRunKeyPath, writable: false);

        foreach (var name in runKey.GetValueNames())
        {
            var command = runKey.GetValue(name)?.ToString() ?? "";
            bool enabled = true;
            var approvedValue = approvedKey?.GetValue(name) as byte[];
            if (approvedValue is { Length: > 0 })
                enabled = approvedValue[0] == 0x02; // 0x02 = enabled, 0x03 = disabled (Task Manager convention)

            list.Add(new StartupEntry { Name = name, Command = command, Source = source, Enabled = enabled });
        }
        return list;
    }

    private static List<StartupEntry> ReadFolder(string folder, StartupSource source)
    {
        var list = new List<StartupEntry>();
        if (!Directory.Exists(folder)) return list;

        foreach (var file in Directory.GetFiles(folder))
        {
            // desktop.ini and similar are hidden/system bookkeeping files, not startup items.
            if ((File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            bool disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var name = Path.GetFileNameWithoutExtension(disabled ? Path.GetFileNameWithoutExtension(file) : file);
            list.Add(new StartupEntry { Name = name, Command = file, Source = source, Enabled = !disabled });
        }
        return list;
    }

    public ActionResult SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            switch (entry.Source)
            {
                case StartupSource.RegistryRunHkcu:
                    return SetRegistryApproval(Registry.CurrentUser, entry, enabled);
                case StartupSource.RegistryRunHklm:
                    return SetRegistryApproval(Registry.LocalMachine, entry, enabled);
                case StartupSource.StartupFolderUser:
                case StartupSource.StartupFolderCommon:
                    return SetFolderShortcutEnabled(entry, enabled);
                case StartupSource.ScheduledTask:
                    return SetScheduledTaskEnabled(entry, enabled);
                default:
                    return new ActionResult(false, "Msg_StartupUnknownSource");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new ActionResult(false, "Msg_StartupAccessDenied", entry.Name);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_StartupFailed", entry.Name, ex.Message);
        }
    }

    private static ActionResult SetRegistryApproval(RegistryKey hive, StartupEntry entry, bool enabled)
    {
        using var approvedKey = hive.CreateSubKey(ApprovedRunKeyPath, writable: true);
        if (approvedKey == null) return new ActionResult(false, "Msg_StartupApprovedKeyError");

        // 12-byte value; only the first byte (enabled flag) matters for our purposes.
        var value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        approvedKey.SetValue(entry.Name, value, RegistryValueKind.Binary);

        entry.Enabled = enabled;
        return new ActionResult(true, enabled ? "Msg_StartupEnabled" : "Msg_StartupDisabled", entry.Name);
    }

    private static ActionResult SetFolderShortcutEnabled(StartupEntry entry, bool enabled)
    {
        var current = entry.Command;
        string target;

        if (enabled && current.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            target = current[..^".disabled".Length];
        else if (!enabled && !current.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            target = current + ".disabled";
        else
            return new ActionResult(true, "Msg_StartupNoChange");

        File.Move(current, target, overwrite: false);
        entry.Command = target;
        entry.Enabled = enabled;
        return new ActionResult(true, enabled ? "Msg_StartupEnabled" : "Msg_StartupDisabled", entry.Name);
    }

    // Tasks under \Microsoft\... are Windows-internal plumbing (Office click-to-run,
    // OneCore diagnostics, etc.); surfacing those alongside OEM bloatware would make
    // the list unusable and risks the user disabling something the OS depends on.
    private const string MicrosoftTaskPrefix = @"\Microsoft\";

    // Read through the Task Scheduler COM API rather than parsing `schtasks /query` text: that text
    // (including the "Disabled" status word and the CSV header) is localized, so on a non-English
    // Windows disabled tasks would be shown as enabled. COM returns the Enabled flag directly and
    // is much faster than spawning a process.
    private static List<StartupEntry> ReadScheduledTasks()
    {
        var list = new List<StartupEntry>();
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return list;
            service = Activator.CreateInstance(type);
            if (service == null) return list;
            dynamic svc = service;
            svc.Connect();
            WalkTaskFolder(svc.GetFolder("\\"), list);
        }
        catch (Exception)
        {
            // Task Scheduler unavailable; scheduled tasks just won't appear this round.
        }
        finally
        {
            if (service != null && System.Runtime.InteropServices.Marshal.IsComObject(service))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
        }
        return list.OrderBy(e => e.Command, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void WalkTaskFolder(dynamic folder, List<StartupEntry> into)
    {
        string folderPath = folder.Path;
        // Windows-internal plumbing is skipped wholesale, folder and all.
        if (IsMicrosoftTaskPath(folderPath + "\\")) return;

        try
        {
            foreach (dynamic task in folder.GetTasks(1 /* TASK_ENUM_HIDDEN */))
            {
                try
                {
                    string taskPath = task.Path;
                    if (IsMicrosoftTaskPath(taskPath)) continue;
                    into.Add(new StartupEntry
                    {
                        Name = taskPath[(taskPath.LastIndexOf('\\') + 1)..],
                        Command = taskPath,
                        Source = StartupSource.ScheduledTask,
                        Enabled = (bool)task.Enabled,
                    });
                }
                catch (Exception) { /* one unreadable task must not hide the rest */ }
            }
            foreach (dynamic sub in folder.GetFolders(0))
                WalkTaskFolder(sub, into);
        }
        catch (Exception) { /* folder not readable */ }
    }

    internal static bool IsMicrosoftTaskPath(string taskPath) =>
        taskPath.StartsWith(MicrosoftTaskPrefix, StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskPath, MicrosoftTaskPrefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private const int SchtasksTimeoutMs = 15_000;

    private static ActionResult SetScheduledTaskEnabled(StartupEntry entry, bool enabled)
    {
        var psi = new ProcessStartInfo(SystemTools.Path("schtasks.exe"))
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/change");
        psi.ArgumentList.Add("/tn");
        psi.ArgumentList.Add(entry.Command); // full task path, e.g. \ASUS\ASUS Survey
        psi.ArgumentList.Add(enabled ? "/enable" : "/disable");

        using var proc = Process.Start(psi);
        if (proc == null) return new ActionResult(false, "Msg_StartupFailed", entry.Name, "schtasks.exe failed to start");

        // Bounded wait: schtasks can stall on a busy Task Scheduler service, and an unbounded wait here
        // used to hang whatever was waiting on this call. stderr is read concurrently so a full pipe
        // can't block the child either.
        var errorTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(SchtasksTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exiting */ }
            return new ActionResult(false, "Msg_StartupFailed", entry.Name, "schtasks.exe timed out");
        }
        proc.WaitForExit(); // flush the redirected stream after a timed wait
        var error = errorTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            return new ActionResult(false, "Msg_StartupFailed", entry.Name, error.Trim());

        entry.Enabled = enabled;
        return new ActionResult(true, enabled ? "Msg_StartupEnabled" : "Msg_StartupDisabled", entry.Name);
    }
}
