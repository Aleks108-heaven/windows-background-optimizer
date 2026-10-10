using System.IO;
using System.IO.Enumeration;

namespace BgOptimizer.Services;

/// <summary>One cleanable location group: fixed roots, which files in them, and how old they must be.</summary>
/// <param name="Id">Also the suffix of the localisation keys Clean_{Id} (name) and Clean_{Id}_D (description).</param>
/// <param name="Roots">Directories to clean inside. Resolved lazily so profiles (browser "Default", "Profile 1") are found at scan time.</param>
/// <param name="Patterns">File-name wildcards; "*" means every file.</param>
/// <param name="MinAgeDays">Only files last written at least this many days ago. 0 = any age (caches the app rebuilds).</param>
/// <param name="SelectedByDefault">Whether the box is ticked on first show; riskier or costlier-to-rebuild groups start unticked.</param>
internal sealed record CleanupTarget(
    string Id, Func<IEnumerable<string>> Roots, string[] Patterns, int MinAgeDays, bool SelectedByDefault);

internal readonly record struct CleanupScan(long Bytes, int Files);

internal readonly record struct CleanupRun(long BytesFreed, int Deleted, int Skipped);

/// <summary>
/// Deletes old, regenerable files: temp files, old logs, app caches. Deliberately conservative: it only
/// ever works inside the fixed roots below, only on files older than the target's age, never follows
/// junctions/symlinks, never deletes a root itself, and skips (rather than fights) files that are in use.
/// Crash dumps, the Recycle Bin and anything in Documents are not covered at all: dumps are exactly what
/// is needed to diagnose a freeze.
/// </summary>
internal static class CleanupService
{
    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static IReadOnlyList<CleanupTarget> Targets()
    {
        var local = Env(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Env(Environment.SpecialFolder.ApplicationData);
        var profile = Env(Environment.SpecialFolder.UserProfile);
        var windows = Env(Environment.SpecialFolder.Windows);
        var programData = Env(Environment.SpecialFolder.CommonApplicationData);
        var userTemp = Path.GetTempPath();

        return new[]
        {
            new CleanupTarget("UserTemp", () => new[] { userTemp }, new[] { "*" }, 7, true),
            new CleanupTarget("WindowsTemp", () => new[] { Path.Combine(windows, "Temp") }, new[] { "*" }, 7, true),
            new CleanupTarget("ErrorReports", () => new[]
            {
                Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
            }, new[] { "*" }, 7, true),
            new CleanupTarget("SystemLogs", () => new[]
            {
                Path.Combine(windows, "Logs", "CBS"),
                Path.Combine(windows, "Logs", "WindowsUpdate"),
            }, new[] { "*.log", "*.cab", "*.etl" }, 30, true),
            new CleanupTarget("AppLogs", () => new[]
            {
                Path.Combine(roaming, "LM Studio", "logs"),
                Path.Combine(profile, ".lmstudio", "server-logs"),
                Path.Combine(roaming, "Code", "logs"),
                // BgOptimizer's own actions.log is deliberately not listed: it is the freeze evidence, is
                // size-capped by its own rollover, and must never be aged out by a cleanup.
            }, new[] { "*.log" }, 30, true),
            new CleanupTarget("ShaderCache", () => new[]
            {
                Path.Combine(local, "D3DSCache"),
                Path.Combine(local, "AMD", "DxCache"),
                Path.Combine(local, "AMD", "GLCache"),
                Path.Combine(local, "AMD", "VkCache"),
                Path.Combine(local, "NVIDIA", "DXCache"),
                Path.Combine(local, "NVIDIA", "GLCache"),
            }, new[] { "*" }, 0, false),
            new CleanupTarget("BrowserCache", () => BrowserCacheDirs(local), new[] { "*" }, 0, false),
            new CleanupTarget("DevCache", () => new[]
            {
                Path.Combine(local, "pip", "Cache"),
                Path.Combine(local, "npm-cache"),
                Path.Combine(local, "NuGet", "v3-cache"),
                Path.Combine(profile, "scoop", "cache"),
            }, new[] { "*" }, 0, false),
        };
    }

    // Chromium keeps one cache set per profile ("Default", "Profile 1", ...); only the cache folders, never cookies or history.
    private static IEnumerable<string> BrowserCacheDirs(string local)
    {
        foreach (var userData in new[]
                 {
                     Path.Combine(local, "Microsoft", "Edge", "User Data"),
                     Path.Combine(local, "Google", "Chrome", "User Data"),
                 })
        {
            if (!Directory.Exists(userData)) continue;
            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(userData).ToList(); }
            catch { continue; }
            foreach (var p in profiles)
            {
                var name = Path.GetFileName(p);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
                    yield return Path.Combine(p, cache);
            }
        }
    }

    public static CleanupScan Scan(CleanupTarget target, DateTime? nowUtc = null)
    {
        long bytes = 0; var files = 0;
        foreach (var root in SafeRoots(target))
            foreach (var file in OldFiles(root, target, nowUtc ?? DateTime.UtcNow))
            {
                bytes += file.Length;
                files++;
            }
        return new CleanupScan(bytes, files);
    }

    public static CleanupRun Clean(CleanupTarget target, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        long freed = 0; int deleted = 0, skipped = 0;
        foreach (var root in SafeRoots(target))
        {
            foreach (var file in OldFiles(root, target, now).ToList())
            {
                try
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0) file.Attributes = FileAttributes.Normal;
                    var length = file.Length;
                    file.Delete();
                    freed += length;
                    deleted++;
                }
                catch { skipped++; } // in use, access denied, or already gone: leave it
            }
            PruneEmptyDirectories(root, now.AddDays(-target.MinAgeDays));
        }
        return new CleanupRun(freed, deleted, skipped);
    }

    /// <summary>Roots that exist and are not something too broad or too important to clean inside.</summary>
    internal static IEnumerable<string> SafeRoots(CleanupTarget target)
    {
        IEnumerable<string> roots;
        try { roots = target.Roots().ToList(); }
        catch { yield break; }

        foreach (var root in roots)
            if (IsSafeRoot(root)) yield return Path.GetFullPath(root);
    }

    private static readonly string[] BroadFolders =
    {
        Env(Environment.SpecialFolder.UserProfile), Env(Environment.SpecialFolder.Windows),
        Env(Environment.SpecialFolder.System), Env(Environment.SpecialFolder.ProgramFiles),
        Env(Environment.SpecialFolder.ProgramFilesX86), Env(Environment.SpecialFolder.CommonApplicationData),
        Env(Environment.SpecialFolder.LocalApplicationData), Env(Environment.SpecialFolder.ApplicationData),
        Env(Environment.SpecialFolder.MyDocuments), Env(Environment.SpecialFolder.Desktop),
    };

    internal static bool IsSafeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullPath = Path.GetFullPath(root);
            // A drive (or UNC share) root is never a cleanup root, however it is spelled.
            if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase)) return false;
            var full = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var broad in BroadFolders)
                if (!string.IsNullOrEmpty(broad)
                    && string.Equals(full, broad.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;

            var dir = new DirectoryInfo(full);
            return dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }

    // Manual walk instead of EnumerateFiles(AllDirectories): junctions/symlinks are not entered, and an
    // unreadable subfolder skips just that folder.
    private static IEnumerable<FileInfo> OldFiles(string root, CleanupTarget target, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-target.MinAgeDays);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo sub) { pending.Push(sub.FullName); continue; }
                if (entry is not FileInfo file) continue;
                if (!MatchesAny(file.Name, target.Patterns)) continue;
                DateTime written;
                try { written = file.LastWriteTimeUtc; } catch { continue; }
                if (written <= cutoff) yield return file;
            }
        }
    }

    private static bool MatchesAny(string name, string[] patterns)
    {
        foreach (var pattern in patterns)
            if (pattern == "*" || FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) return true;
        return false;
    }

    // Bottom-up, so a folder emptied by this run goes too. Never the root, never a non-empty folder,
    // never one touched since the cutoff.
    private static void PruneEmptyDirectories(string root, DateTime cutoffUtc)
    {
        // Same junction-safe walk as OldFiles: a linked folder is neither entered nor removed.
        var dirs = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            IEnumerable<DirectoryInfo> subs;
            try { subs = new DirectoryInfo(pending.Pop()).EnumerateDirectories().ToList(); }
            catch { continue; }
            foreach (var sub in subs)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                dirs.Add(sub.FullName);
                pending.Push(sub.FullName);
            }
        }

        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info.LastWriteTimeUtc > cutoffUtc) continue;
                if (info.EnumerateFileSystemInfos().Any()) continue;
                info.Delete();
            }
            catch { /* in use or already gone */ }
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}
