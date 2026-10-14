using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BgOptimizer.Services;

internal enum LeftoverKind
{
    RunningDeleted,         // a running program whose exe was deleted from disk
    LoadedDeleted,          // a running program holding DLLs that were deleted from disk
    BrokenRegistration,     // listed in Installed apps, but its files are gone
    EmptyFolder,            // a folder left in Program Files with nothing in it
    DataOnlyFolder,         // only data/config/log files, no program files
    UnregisteredFolder,     // program files that no installed app, service or running process owns
}

/// <param name="Bytes">Size on disk for folders; 0 where it does not apply.</param>
/// <param name="Note">Non-localised detail: PID and memory for processes, DLL names, publisher.</param>
internal sealed record LeftoverFinding(LeftoverKind Kind, string Name, string Path, long Bytes, string Note = "");

internal sealed record InstalledProgram(
    string DisplayName, string Publisher, string? InstallLocation, string? ExePath, bool IsSystemComponent);

internal sealed record RunningProgram(string Name, int Pid, string? ExePath, long WorkingSet, IReadOnlyList<string> MissingLibraries);

/// <summary>
/// Looks for what a deleted program leaves behind, read-only. Three independent sources, each judged
/// conservatively because a wrong "this is leftover" is worse than a missed one:
///  - running programs / loaded DLLs whose file is no longer on disk (still in memory until restarted),
///  - Installed-apps entries whose install folder or uninstaller is gone,
///  - folders in Program Files that no installed app, service or running process owns.
/// "Cannot tell" (access denied, unplugged drive) never counts as missing.
/// </summary>
internal static class LeftoverScanner
{
    private static readonly string[] UninstallKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    // Folders Windows or its components own without an Installed-apps entry.
    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Internet Explorer", "ModifiableWindowsApps", "MSBuild", "Reference Assemblies",
        "Uninstall Information", "WindowsApps", "WindowsPowerShell", "dotnet", "Application Verifier",
    };

    private static readonly string[] SystemFolderPrefixes = { "Windows", "Microsoft" };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".sys", ".msi", ".jar", ".bat", ".cmd", ".ps1", ".py", ".node", ".so" };

    public static IReadOnlyList<LeftoverFinding> Scan(CancellationToken ct)
    {
        var findings = new List<LeftoverFinding>();

        var running = ReadRunningPrograms(ct);
        foreach (var p in running)
        {
            if (p.ExePath != null)
                findings.Add(new LeftoverFinding(LeftoverKind.RunningDeleted, p.Name, p.ExePath, 0,
                    $"PID {p.Pid} · {CleanupService.FormatBytes(p.WorkingSet)}"));
            if (p.MissingLibraries.Count > 0)
                findings.Add(new LeftoverFinding(LeftoverKind.LoadedDeleted, p.Name, p.MissingLibraries[0], 0,
                    $"PID {p.Pid} · {p.MissingLibraries.Count}: " + string.Join(", ",
                        p.MissingLibraries.Take(3).Select(f => System.IO.Path.GetFileName(f))) + (p.MissingLibraries.Count > 3 ? ", …" : "")));
        }

        ct.ThrowIfCancellationRequested();
        var installed = ReadInstalledPrograms();
        findings.AddRange(FindBrokenRegistrations(installed));

        ct.ThrowIfCancellationRequested();
        var claimedPaths = installed.SelectMany(i => new[] { i.InstallLocation, i.ExePath })
            .Concat(running.Select(r => r.ExePath))
            .Concat(ReadServicePaths())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();
        var claimedNames = installed.SelectMany(i => new[] { i.DisplayName, i.Publisher })
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        findings.AddRange(FindOrphanFolders(ProgramFolderRoots(), claimedPaths, claimedNames, ct));

        return findings;
    }

    // ---------- Running programs ----------

    private static List<RunningProgram> ReadRunningPrograms(CancellationToken ct)
    {
        var result = new List<RunningProgram>();
        var fileState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // path -> missing; most DLLs are shared by many processes

        bool Missing(string path)
        {
            if (!fileState.TryGetValue(path, out var missing)) fileState[path] = missing = PathProbe.IsMissing(path);
            return missing;
        }

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                ct.ThrowIfCancellationRequested();
                if (proc.Id is 0 or 4) continue;
                try
                {
                    string? exe = null;
                    var libraries = new List<string>();
                    string? exePath;
                    try { exePath = proc.MainModule?.FileName; }
                    catch { continue; } // protected process or already exited: nothing readable to judge
                    if (string.IsNullOrEmpty(exePath) || !PathProbe.IsOnLocalFixedDrive(exePath)) continue;
                    if (Missing(exePath)) exe = exePath;

                    try
                    {
                        foreach (ProcessModule module in proc.Modules)
                        {
                            var path = module.FileName;
                            if (string.IsNullOrEmpty(path) || !PathProbe.IsOnLocalFixedDrive(path)) continue;
                            if (string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                            if (Missing(path)) libraries.Add(path);
                        }
                    }
                    catch { /* module list not readable (anti-cheat, protected, exiting): judge the exe only */ }

                    if (exe != null || libraries.Count > 0)
                        result.Add(new RunningProgram(proc.ProcessName, proc.Id, exe, SafeWorkingSet(proc), libraries));
                }
                catch { /* the process ended while being read */ }
            }
        }
        return result;
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    // Device paths (\\?\, \Device\...) and anything without a drive letter or share cannot be probed meaningfully.
    private static bool IsDrivePath(string path) => CommandPath.IsDrivePath(path);

    // ---------- Installed apps ----------

    internal static List<InstalledProgram> ReadInstalledPrograms()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, subKey) in UninstallKeys.Select(k => (Registry.LocalMachine, k))
                     .Append((Registry.CurrentUser, UninstallKeys[0])))
        {
            try
            {
                using var root = hive.OpenSubKey(subKey, writable: false);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var key = root.OpenSubKey(name, writable: false);
                        var display = key?.GetValue("DisplayName") as string;
                        if (key == null || string.IsNullOrWhiteSpace(display)) continue;
                        if (key.GetValue("ParentKeyName") != null) continue; // a patch/update of another entry

                        var location = Clean(key.GetValue("InstallLocation") as string);
                        var exe = ExtractFilePath(key.GetValue("UninstallString") as string)
                                  ?? ExtractFilePath(key.GetValue("DisplayIcon") as string);
                        list.Add(new InstalledProgram(
                            display, key.GetValue("Publisher") as string ?? "",
                            location != null && Path.IsPathRooted(location) ? location : null,
                            exe, key.GetValue("SystemComponent") is int sc && sc == 1));
                    }
                    catch { /* one unreadable entry must not hide the rest */ }
                }
            }
            catch { /* hive section not readable */ }
        }
        return list;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\'); }
        catch { return null; }
    }

    /// <summary>The file named at the start of an UninstallString/DisplayIcon value ("C:\x\a.exe" /S, C:\x\a.exe,0), or null.</summary>
    internal static string? ExtractFilePath(string? command) => CommandPath.ExtractFilePath(command);

    internal static IEnumerable<LeftoverFinding> FindBrokenRegistrations(IEnumerable<InstalledProgram> installed)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in installed)
        {
            if (p.IsSystemComponent) continue;

            string? missingPath = null;
            if (p.InstallLocation != null)
            {
                if (PathProbe.DriveIsPresent(p.InstallLocation) && PathProbe.IsMissing(p.InstallLocation))
                    missingPath = p.InstallLocation;
            }
            else if (p.ExePath != null && !PathProbe.IsUnder(p.ExePath, windows)  // msiexec, rundll32...: not the program's own file
                     && PathProbe.DriveIsPresent(p.ExePath) && PathProbe.IsMissing(p.ExePath))
            {
                missingPath = p.ExePath;
            }

            if (missingPath != null)
                yield return new LeftoverFinding(LeftoverKind.BrokenRegistration, p.DisplayName, missingPath, 0, p.Publisher);
        }
    }

    // ---------- Folders left in Program Files ----------

    private static IEnumerable<string> ProgramFolderRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadServicePaths()
    {
        var paths = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PathName FROM Win32_Service");
            using var results = searcher.Get();
            foreach (ManagementBaseObject service in results)
            {
                using (service)
                    if (ExtractFilePath(service["PathName"] as string) is { } path) paths.Add(path);
            }
        }
        catch { /* WMI unavailable: folders are then judged without services, which only makes the check stricter */ }
        return paths;
    }

    internal static bool IsSystemFolder(string name, FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
        || SystemFolderNames.Contains(name)
        || SystemFolderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string Normalise(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// A folder is "owned" when an installed app, service or running process lives in it, or when its name
    /// matches an installed app's or publisher's name (so "Docker" is owned by "Docker Desktop").
    /// </summary>
    internal static bool IsClaimed(string folderPath, IEnumerable<string> claimedPaths, IEnumerable<string> claimedNames)
    {
        if (claimedPaths.Any(p => string.Equals(p.TrimEnd('\\'), folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                  || PathProbe.IsUnder(p, folderPath)))
            return true;

        var folder = Normalise(Path.GetFileName(folderPath.TrimEnd('\\')));
        if (folder.Length < 3) return true; // too short to judge by name; do not guess
        foreach (var name in claimedNames)
        {
            var n = Normalise(name);
            if (n.Length < 3) continue;
            if (n.Contains(folder) || (folder.Length >= 4 && folder.Contains(n))) return true;
        }
        return false;
    }

    internal static IEnumerable<LeftoverFinding> FindOrphanFolders(
        IEnumerable<string> roots, IReadOnlyCollection<string> claimedPaths, IReadOnlyCollection<string> claimedNames, CancellationToken ct)
    {
        foreach (var root in roots)
        {
            IEnumerable<DirectoryInfo> folders;
            try { folders = new DirectoryInfo(root).EnumerateDirectories().ToList(); }
            catch { continue; }

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = folder.Attributes; } catch { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (IsSystemFolder(folder.Name, attributes)) continue;
                if (IsClaimed(folder.FullName, claimedPaths, claimedNames)) continue;

                var (files, programFiles, bytes) = Measure(folder.FullName, ct);
                var kind = files == 0 ? LeftoverKind.EmptyFolder
                    : programFiles == 0 ? LeftoverKind.DataOnlyFolder
                    : LeftoverKind.UnregisteredFolder;
                yield return new LeftoverFinding(kind, folder.Name, folder.FullName, bytes, $"{files}");
            }
        }
    }

    private static (int Files, int ProgramFiles, long Bytes) Measure(string folder, CancellationToken ct)
    {
        int files = 0, programFiles = 0; long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos().ToList(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo dir) { pending.Push(dir.FullName); continue; }
                    if (entry is not FileInfo file) continue;
                    files++;
                    bytes += file.Length;
                    if (ProgramExtensions.Contains(file.Extension)) programFiles++;
                }
                catch { /* vanished or unreadable */ }
            }
        }
        return (files, programFiles, bytes);
    }
}
