using System.Diagnostics;
using System.IO;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// Lists and toggles startup items the same ways Task Manager (and Task
/// Scheduler) do: the StartupApproved registry flag for Run-key entries and for
/// Startup-folder shortcuts (nothing is renamed or deleted), and schtasks.exe for
/// Scheduled Tasks - the mechanism most OEM bundleware (ASUS, Dell, HP, ...)
/// actually uses to relaunch its background agents, which Run keys and the
/// Startup folder never see.
///
/// Also lists, read-only (they are consumed or managed by Windows, so switching them here
/// would do nothing useful): RunOnce entries, Group-Policy Run entries and Store-app startup tasks.
/// Disabling is refused for security software, Windows components and this app (see <see cref="StartupSafety"/>).
/// Registry access is always the 64-bit view, so a 32-bit build never sees a different (or duplicated) set.
/// </summary>
public class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32KeyPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string PolicyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    private const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string ApprovedRunKeyPath = ApprovedBase + "Run";
    private const string ApprovedRun32KeyPath = ApprovedBase + "Run32";
    private const string ApprovedFolderKeyPath = ApprovedBase + "StartupFolder";
    private const string StoreTasksKeyPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private static readonly RegistryKey Hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    private static readonly RegistryKey Hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public List<StartupEntry> GetAll()
    {
        var results = new List<StartupEntry>();

        results.AddRange(ReadRunKey(Hkcu, RunKeyPath, ApprovedRunKeyPath, StartupSource.RegistryRunHkcu));
        results.AddRange(ReadRunKey(Hklm, RunKeyPath, ApprovedRunKeyPath, StartupSource.RegistryRunHklm));
        results.AddRange(ReadRunKey(Hklm, Run32KeyPath, ApprovedRun32KeyPath, StartupSource.RegistryRunHklm32));
        results.AddRange(ReadRunKey(Hkcu, RunOnceKeyPath, null, StartupSource.RunOnceHkcu));
        results.AddRange(ReadRunKey(Hklm, RunOnceKeyPath, null, StartupSource.RunOnceHklm));
        results.AddRange(ReadRunKey(Hkcu, PolicyRunKeyPath, null, StartupSource.PolicyHkcu));
        results.AddRange(ReadRunKey(Hklm, PolicyRunKeyPath, null, StartupSource.PolicyHklm));
        results.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.StartupFolderUser, Hkcu));
        results.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.StartupFolderCommon, Hklm));
        results.AddRange(ReadScheduledTasks());
        results.AddRange(ReadStoreApps());

        var publishers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in results)
        {
            entry.Category = ItemClassifier.ClassifyStartup(entry.Name, entry.Command);
            Enrich(entry, publishers);
        }

        return results.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What the entry runs, whether that file still exists, who made it, and whether it may be disabled. Read-only checks.</summary>
    internal static void Enrich(StartupEntry entry, Dictionary<string, string?> publishers)
    {
        entry.TargetPath ??= entry.Source switch
        {
            StartupSource.StartupFolderUser or StartupSource.StartupFolderCommon => FolderItemTarget(entry.Command),
            StartupSource.ScheduledTask or StartupSource.StoreApp => null,
            _ => CommandPath.ExtractFilePath(entry.Command),
        };

        if (entry.TargetPath != null)
        {
            entry.TargetMissing = PathProbe.DriveIsPresent(entry.TargetPath) && PathProbe.IsMissing(entry.TargetPath);
            if (!entry.TargetMissing)
            {
                if (!publishers.TryGetValue(entry.TargetPath, out var publisher))
                    publishers[entry.TargetPath] = publisher = PublisherInfo.Get(entry.TargetPath);
                entry.Publisher = publisher;
            }
        }
        entry.Lock = StartupSafety.Evaluate(entry.Name, entry.TargetPath);
    }

    // A shortcut runs what it points to; any other file in the folder (.cmd, .bat, .exe) runs itself.
    // A copy renamed by an older version of this app ("x.lnk.disabled") can't be resolved, and does not run anyway.
    private static string? FolderItemTarget(string file)
    {
        if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return null;
        return file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? CommandPath.ShortcutTarget(file) : file;
    }

    /// <param name="approvedPath">Where Task Manager keeps the on/off flag for this key; null for keys that have none (read-only ones).</param>
    private static List<StartupEntry> ReadRunKey(RegistryKey hive, string path, string? approvedPath, StartupSource source)
    {
        var list = new List<StartupEntry>();
        using var runKey = hive.OpenSubKey(path, writable: false);
        if (runKey == null) return list;

        using var approvedKey = approvedPath == null ? null : hive.OpenSubKey(approvedPath, writable: false);

        foreach (var name in runKey.GetValueNames())
        {
            if (name.Length == 0) continue; // the key's unnamed default value is not a startup entry
            var command = runKey.GetValue(name)?.ToString() ?? "";
            var enabled = approvedKey == null || StartupApprovedValue.IsEnabled(approvedKey.GetValue(name));
            list.Add(new StartupEntry { Name = name, Command = command, Source = source, Enabled = enabled });
        }
        return list;
    }

    internal static List<StartupEntry> ReadFolder(string folder, StartupSource source, RegistryKey approvalHive, string approvedPath = ApprovedFolderKeyPath)
    {
        var list = new List<StartupEntry>();
        if (!Directory.Exists(folder)) return list;

        using var approvedKey = approvalHive.OpenSubKey(approvedPath, writable: false);
        foreach (var file in Directory.GetFiles(folder))
        {
            // desktop.ini and similar are hidden/system bookkeeping files, not startup items.
            if ((File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

            // "x.lnk.disabled" is how earlier versions of this app switched a shortcut off. It is still listed
            // (as off) so it can be switched back on, which also moves it back to its real name.
            var legacyDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var realName = Path.GetFileName(legacyDisabled ? file[..^".disabled".Length] : file);
            var name = Path.GetFileNameWithoutExtension(realName);
            var enabled = !legacyDisabled && StartupApprovedValue.IsEnabled(approvedKey?.GetValue(realName));
            list.Add(new StartupEntry { Name = name, Command = file, Source = source, Enabled = enabled });
        }
        return list;
    }

    // Store apps register startup tasks under the user's AppModel keys; the value mirrors the documented
    // StartupTaskState enum (0 Disabled, 1 DisabledByUser, 2 Enabled, 3 DisabledByPolicy, 4 EnabledByPolicy).
    // Shown read-only: the supported place to change them is Windows Settings > Apps > Startup.
    private static List<StartupEntry> ReadStoreApps()
    {
        var list = new List<StartupEntry>();
        try
        {
            using var root = Hkcu.OpenSubKey(StoreTasksKeyPath, writable: false);
            if (root == null) return list;
            foreach (var package in root.GetSubKeyNames())
            {
                try
                {
                    using var packageKey = root.OpenSubKey(package, writable: false);
                    if (packageKey == null) continue;
                    foreach (var task in packageKey.GetSubKeyNames())
                    {
                        using var taskKey = packageKey.OpenSubKey(task, writable: false);
                        if (taskKey?.GetValue("State") is not int state) continue; // PSR, HAM, SplashScreen... are not startup tasks
                        var underscore = package.IndexOf('_');
                        list.Add(new StartupEntry
                        {
                            Name = underscore > 0 ? package[..underscore] : package,
                            Command = $"{package}\\{task}",
                            Source = StartupSource.StoreApp,
                            Enabled = state is 2 or 4,
                        });
                    }
                }
                catch { /* one unreadable package must not hide the rest */ }
            }
        }
        catch { /* key not readable */ }
        return list;
    }

    /// <summary>
    /// Switches an item off or on. Never deletes anything: the Run value, shortcut or task stays where it is and
    /// only its on/off flag changes. Turning ON is always allowed; turning OFF is refused for locked items.
    /// Every call is written to the action log (STARTUP_ENABLE / STARTUP_DISABLE), whatever the outcome.
    /// </summary>
    public ActionResult SetEnabled(StartupEntry entry, bool enabled)
    {
        if (ViewOnlyMode.Block(entry.Name) is { } viewOnly) return viewOnly;

        var result = Toggle(entry, enabled);
        ActionLog.Write($"STARTUP_{(enabled ? "ENABLE" : "DISABLE")} name=\"{entry.Name}\" source={entry.Source} ok={result.Ok} ({result.MessageKey})");
        return result;
    }

    internal static ActionResult Toggle(StartupEntry entry, bool enabled)
    {
        if (entry.IsReadOnly)
            return new ActionResult(false, "Msg_StartupReadOnly", entry.Name);
        if (!enabled && entry.Lock != StartupLock.None)
            return new ActionResult(false, "Msg_StartupLocked", entry.Name);

        try
        {
            switch (entry.Source)
            {
                case StartupSource.RegistryRunHkcu:
                    return SetRegistryApproval(Hkcu, ApprovedRunKeyPath, entry.Name, entry, enabled);
                case StartupSource.RegistryRunHklm:
                    return SetRegistryApproval(Hklm, ApprovedRunKeyPath, entry.Name, entry, enabled);
                case StartupSource.RegistryRunHklm32:
                    return SetRegistryApproval(Hklm, ApprovedRun32KeyPath, entry.Name, entry, enabled);
                case StartupSource.StartupFolderUser:
                    return SetFolderItemEnabled(Hkcu, entry, enabled);
                case StartupSource.StartupFolderCommon:
                    return SetFolderItemEnabled(Hklm, entry, enabled);
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

    private static ActionResult SetRegistryApproval(RegistryKey hive, string approvedPath, string valueName, StartupEntry entry, bool enabled)
    {
        using var approvedKey = hive.CreateSubKey(approvedPath, writable: true);
        if (approvedKey == null) return new ActionResult(false, "Msg_StartupApprovedKeyError");

        approvedKey.SetValue(valueName, StartupApprovedValue.Encode(enabled, DateTime.UtcNow), RegistryValueKind.Binary);

        entry.Enabled = enabled;
        return new ActionResult(true, enabled ? "Msg_StartupEnabled" : "Msg_StartupDisabled", entry.Name);
    }

    // Same flag Task Manager uses for Startup-folder items, so the two always agree and nothing is renamed.
    // A copy that an older version of this app renamed to "*.disabled" is moved back to its real name when
    // switched on; switching one off that is already off changes nothing.
    internal static ActionResult SetFolderItemEnabled(RegistryKey hive, StartupEntry entry, bool enabled)
    {
        var current = entry.Command;
        var legacyDisabled = current.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var realPath = legacyDisabled ? current[..^".disabled".Length] : current;

        if (legacyDisabled)
        {
            if (!enabled) return new ActionResult(true, "Msg_StartupNoChange");
            File.Move(current, realPath, overwrite: false);
            entry.Command = realPath;
        }

        using var approvedKey = hive.CreateSubKey(ApprovedFolderKeyPath, writable: true);
        if (approvedKey == null) return new ActionResult(false, "Msg_StartupApprovedKeyError");
        approvedKey.SetValue(Path.GetFileName(realPath), StartupApprovedValue.Encode(enabled, DateTime.UtcNow), RegistryValueKind.Binary);

        entry.Enabled = enabled;
        return new ActionResult(true, enabled ? "Msg_StartupEnabled" : "Msg_StartupDisabled", entry.Name);
    }

    // ---------- Copies renamed to "*.disabled" by versions up to 1.2.x ----------

    /// <summary>What <see cref="RepairLegacyDisabled"/> did: copies moved back and switched off, stale ones set aside, ones it could not fix.</summary>
    internal readonly record struct LegacyRepair(int Repaired, int SetAside, int Failed)
    {
        public int Total => Repaired + SetAside;
    }

    // What the old versions could have disabled: files that run by themselves from a Startup folder.
    private static readonly HashSet<string> LegacyStartupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".cmd", ".bat", ".exe", ".vbs", ".js", ".ps1", ".appref-ms",
    };

    /// <summary>
    /// Windows tries to open every file in a Startup folder at sign-in, so the "x.lnk.disabled" copies that versions
    /// up to 1.2.x left behind make it ask "Which app do you want to use to open this .disabled file?" on every
    /// start. Each one is switched off the way Task Manager does (the StartupApproved flag) and moved back to its
    /// real name, so the entry stays exactly as off as the user left it and the question stops. Done once per
    /// refresh, silently when there is nothing to do, and never in view-only mode (it changes files).
    /// </summary>
    internal static LegacyRepair RepairLegacyDisabled()
    {
        if (ViewOnlyMode.Enabled) return default;
        var setAside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgOptimizer", "legacy-startup");
        var user = RepairLegacyFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Hkcu, ApprovedFolderKeyPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), setAside);
        var common = RepairLegacyFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Hklm, ApprovedFolderKeyPath,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), setAside);
        return new LegacyRepair(user.Repaired + common.Repaired, user.SetAside + common.SetAside, user.Failed + common.Failed);
    }

    /// <param name="requiredRoot">The folder must lie inside this one. The Startup folder's location is a per-user setting, so without
    /// this a redirected one could make the elevated app rename files in a place only administrators can write.</param>
    /// <param name="log">Where each file's outcome is recorded; the action log unless a test passes its own.</param>
    internal static LegacyRepair RepairLegacyFolder(string folder, RegistryKey approvalHive, string approvedPath, string requiredRoot, string setAsideDir, Action<string>? log = null)
    {
        log ??= message => ActionLog.Write(message);
        int repaired = 0, setAside = 0, failed = 0;
        try
        {
            if (!IsPlainFolderUnder(folder, requiredRoot)) return default;
            foreach (var file in Directory.GetFiles(folder))
            {
                if (!file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;
                var realPath = file[..^".disabled".Length];
                // Only what the old versions produced: a startup-type name, not hidden bookkeeping, not a link.
                if (!LegacyStartupExtensions.Contains(Path.GetExtension(realPath))) continue;
                if ((File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;

                try
                {
                    if (File.Exists(realPath))
                    {
                        // A real copy exists already, so this one is a stale duplicate: set aside, never deleted or overwritten.
                        if (SetAsideStale(file, setAsideDir)) { setAside++; log($"STARTUP_REPAIR_LEGACY file=\"{Path.GetFileName(file)}\" result=set-aside"); }
                        else failed++;
                        continue;
                    }

                    // Flag first, move second: if the flag cannot be written nothing has moved, and if the move then
                    // fails the leftover flag only describes a file that is not there. The other order could briefly
                    // turn an item the user switched off back on.
                    using (var approvedKey = approvalHive.CreateSubKey(approvedPath, writable: true))
                    {
                        if (approvedKey == null) { failed++; continue; }
                        approvedKey.SetValue(Path.GetFileName(realPath), StartupApprovedValue.Encode(false, DateTime.UtcNow), RegistryValueKind.Binary);
                    }
                    File.Move(file, realPath, overwrite: false);
                    repaired++;
                    log($"STARTUP_REPAIR_LEGACY file=\"{Path.GetFileName(file)}\" result=moved-back-and-kept-off");
                }
                catch (Exception ex)
                {
                    failed++;
                    log($"STARTUP_REPAIR_LEGACY file=\"{Path.GetFileName(file)}\" result=failed ({ex.GetType().Name})");
                }
            }
        }
        catch { failed++; /* an unreadable folder just means there is nothing to repair here */ }
        return new LegacyRepair(repaired, setAside, failed);
    }

    private static bool SetAsideStale(string file, string setAsideDir)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var target = Path.Combine(setAsideDir, $"{Path.GetFileName(file)}.{DateTime.UtcNow:yyyyMMddHHmmss}.{unique}");
        // The data folder is the user's: never move into it through a junction or link.
        if (!SafeAppData.IsSafeToWrite(target)) return false;
        Directory.CreateDirectory(setAsideDir);
        if (!SafeAppData.IsSafeToWrite(target)) return false;
        File.Move(file, target, overwrite: false);
        return true;
    }

    internal static bool IsPlainFolderUnder(string folder, string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(root)) return false;
            var inside = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            var outside = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!inside.StartsWith(outside, StringComparison.OrdinalIgnoreCase)) return false;
            var dir = new DirectoryInfo(folder);
            return dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
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
                        TargetPath = FirstActionPath(task),
                    });
                }
                catch (Exception) { /* one unreadable task must not hide the rest */ }
            }
            foreach (dynamic sub in folder.GetFolders(0))
                WalkTaskFolder(sub, into);
        }
        catch (Exception) { /* folder not readable */ }
    }

    // The program the task starts (its first "execute" action); null when it has none or can't be read.
    private static string? FirstActionPath(dynamic task)
    {
        try
        {
            foreach (dynamic action in task.Definition.Actions)
            {
                string? path = action.Path as string;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                return CommandPath.IsDrivePath(expanded) ? expanded : null;
            }
        }
        catch (Exception) { /* a task whose definition can't be read has no known target */ }
        return null;
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
