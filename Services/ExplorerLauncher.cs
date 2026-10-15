using System.Diagnostics;
using System.IO;

namespace BgOptimizer.Services;

/// <summary>
/// Shows a file or folder in Explorer. Explorer parses its own command line and wants /select,"path" with the
/// quotes around the path only: a path with a space (C:\Program Files\...) passed as one quoted argument
/// ("/select,C:\Program Files\...", which is what ProcessStartInfo.ArgumentList produces) is not understood,
/// and Explorer falls back to opening the Documents folder instead.
/// </summary>
internal static class ExplorerLauncher
{
    /// <summary>The command line for Explorer; null for a path that cannot be quoted safely (Windows paths never contain a quote).</summary>
    public static string? Arguments(string path, bool select)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"') || path.Contains('\0')) return null;
        return select ? $"/select,\"{path}\"" : $"\"{path}\"";
    }

    /// <summary>Starts Explorer by its full path (never looked up next to the app). False when it could not be started.</summary>
    public static bool Open(string path, bool select)
    {
        var arguments = Arguments(path, select);
        if (arguments is null) return false;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using var started = Process.Start(new ProcessStartInfo(explorer) { Arguments = arguments, UseShellExecute = false });
        return started != null;
    }
}
