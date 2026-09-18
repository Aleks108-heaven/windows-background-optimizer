using System.IO;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// Lists and toggles startup items the same two ways Task Manager does:
/// the StartupApproved registry flag for Run-key entries, and a
/// rename-to-.disabled trick for Startup-folder shortcuts (both reversible,
/// unlike deleting the Run value or shortcut outright).
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
}
