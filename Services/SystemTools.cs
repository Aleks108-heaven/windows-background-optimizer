using System.IO;

namespace BgOptimizer.Services;

/// <summary>
/// The app runs elevated, and a bare executable name is looked up in the app's own folder before
/// System32. Launching Windows tools by full System32 path means a look-alike netsh.exe/powercfg.exe
/// dropped next to the app (e.g. when running from a user-writable folder) can never run as admin.
/// </summary>
internal static class SystemTools
{
    public static string Path(string exeName) => System.IO.Path.Combine(Environment.SystemDirectory, exeName);
}
