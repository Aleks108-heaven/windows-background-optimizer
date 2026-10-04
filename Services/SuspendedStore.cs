using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BgOptimizer.Services;

/// <summary>
/// Auto-Optimize freezes processes with NtSuspendProcess, and only this app can thaw them. If it
/// crashes, is killed, or the session ends while something is frozen, that process would stay frozen
/// until reboot. So the frozen set is written to disk on every change and released on the next start.
/// Each entry carries the process start time, so a recycled PID is never resumed by mistake.
/// </summary>
internal static class SuspendedStore
{
    internal record Item(int Pid, string Name, long StartTimeUtcTicks);

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgOptimizer", "suspended.json");

    /// <summary>Replaces the stored list; an empty list removes the file. Never throws.</summary>
    public static void Save(IEnumerable<(int Pid, string Name)> frozen, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var items = new List<Item>();
            foreach (var (pid, name) in frozen)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    items.Add(new Item(pid, name, p.StartTime.ToUniversalTime().Ticks));
                }
                catch { /* already gone: nothing to thaw later */ }
            }

            if (items.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(items));
        }
        catch { /* best effort: a failed save must never break the optimize tick */ }
    }

    internal static List<Item> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    /// <summary>
    /// Resumes every stored process that is still the same process (name and start time match),
    /// then clears the store. Returns how many were resumed.
    /// </summary>
    public static int ResumeLeftovers(string? path = null)
    {
        path ??= DefaultPath;
        var resumed = 0;
        foreach (var item in Load(path))
        {
            try
            {
                using var p = Process.GetProcessById(item.Pid);
                if (!string.Equals(p.ProcessName, item.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.StartTime.ToUniversalTime().Ticks != item.StartTimeUtcTicks) continue;
                if (ProcessMonitor.ResumeProcess(item.Pid, item.Name).Ok) resumed++;
            }
            catch { /* exited, or not accessible: nothing to resume */ }
        }
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        return resumed;
    }
}
