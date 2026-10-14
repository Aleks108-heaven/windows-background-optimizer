using System.IO;
using System.Text;

namespace BgOptimizer.Services;

/// <summary>
/// Append-only record of what the app did (throttle, trim, suspend, resume, memory-pressure changes,
/// start/exit, unhandled errors) in %LocalAppData%\BgOptimizer\actions.log. The on-screen log is capped
/// and lost on exit; this one survives a crash or a hard reset, which is the point: after a freeze it
/// shows whether the app was running and what it had touched. Lines are written through to disk, the file
/// rolls over to actions.log.1 at 1 MB, and writing never throws.
/// </summary>
internal static class ActionLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgOptimizer", "actions.log");

    public static void Write(string message, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!SafeAppData.IsSafeToWrite(path)) return; // a junction/symlink in the data folder: never write through it as admin
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RollOver(path);
                // WriteThrough: the interesting lines are the last ones before a hang, so don't leave them in the cache.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                stream.Write(Encoding.UTF8.GetBytes(line));
            }
        }
        catch { /* diagnostics must never break the app */ }
    }

    private static void RollOver(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length >= MaxBytes) File.Move(path, path + ".1", overwrite: true);
    }
}
