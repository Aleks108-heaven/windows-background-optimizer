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

    private static List<StartupEntry> ReadScheduledTasks()
    {
        var list = new List<StartupEntry>();
        try
        {
            var output = RunSchtasks("/query", "/fo", "csv");
            if (output == null) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n').Skip(1)) // skip the CSV header row
            {
                var fields = SplitSchtasksCsvLine(line);
                if (fields.Length < 3) continue;

                var taskPath = fields[0].Trim();
                var status = fields[2].Trim();
                if (string.IsNullOrEmpty(taskPath)) continue;
                if (taskPath.StartsWith(MicrosoftTaskPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(taskPath)) continue; // schtasks repeats a row per trigger; keep the first

                var name = taskPath[(taskPath.LastIndexOf('\\') + 1)..];
                list.Add(new StartupEntry
                {
                    Name = name,
                    Command = taskPath,
                    Source = StartupSource.ScheduledTask,
                    Enabled = !string.Equals(status, "Disabled", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch (Exception)
        {
            // schtasks unavailable or failed to run; scheduled tasks just won't appear this round.
        }
        return list;
    }

    private static ActionResult SetScheduledTaskEnabled(StartupEntry entry, bool enabled)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
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

        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            return new ActionResult(false, "Msg_StartupFailed", entry.Name, error.Trim());

        entry.Enabled = enabled;
        return new ActionResult(true, enabled ? "Msg_StartupEnabled" : "Msg_StartupDisabled", entry.Name);
    }

    private static string? RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null) return null;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return output;
    }

    // schtasks' CSV output wraps every field in quotes but doesn't escape quotes that
    // appear inside a value (e.g. a quoted argument in "Task To Run"), so a real CSV
    // parser would misjudge field boundaries. Splitting on the literal `","` separator
    // instead sidesteps that, since a bare `","` sequence essentially never occurs
    // inside a field's own contents.
    private static string[] SplitSchtasksCsvLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return Array.Empty<string>();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            trimmed = trimmed[1..^1];
        return trimmed.Split("\",\"");
    }
}
