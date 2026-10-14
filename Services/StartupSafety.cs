using System.IO;

namespace BgOptimizer.Services;

/// <summary>Why a startup item cannot be disabled from this app. Turning one back ON is always allowed.</summary>
public enum StartupLock
{
    None,
    SecurityTool,
    WindowsComponent,
    ThisApp,
}

/// <summary>
/// The Startup tab's "locked" rule. Disabling is refused for security software (Windows Security, Defender, and
/// the third-party AV/EDR names the rest of the app already protects), for components that live inside Windows
/// itself, and for this app; all of them are things whose absence the user would not notice until it mattered.
/// Decided from the entry's name and the file it runs, never by running anything.
/// </summary>
internal static class StartupSafety
{
    // Startup-entry names for security software that are not process names in ProtectedItems.
    private static readonly HashSet<string> SecurityEntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealth", "SecurityHealthSystray", "Windows Defender", "WindowsDefender", "WindowsDefenderNotification",
        "MsMpEng", "egui", "ekrn", "AVGUI", "avast", "MBAMService",
    };

    public static StartupLock Evaluate(string name, string? targetPath)
    {
        var exe = string.IsNullOrEmpty(targetPath) ? "" : Path.GetFileNameWithoutExtension(targetPath);

        if (IsThisApp(name, exe, targetPath)) return StartupLock.ThisApp;

        if (SecurityEntryNames.Contains(name) || SecurityEntryNames.Contains(exe)
            || ProtectedItems.IsSecurityTool(name) || ProtectedItems.IsSecurityTool(exe))
            return StartupLock.SecurityTool;

        if (exe.Length > 0 && ProtectedItems.IsProcessProtected(exe)) return StartupLock.WindowsComponent;
        if (IsInsideWindows(targetPath)) return StartupLock.WindowsComponent;
        return StartupLock.None;
    }

    private static bool IsThisApp(string name, string exe, string? targetPath)
    {
        if (exe.Equals("BgOptimizer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("BgOptimizer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Background Optimizer", StringComparison.OrdinalIgnoreCase))
            return true;
        var self = Environment.ProcessPath;
        return !string.IsNullOrEmpty(self) && !string.IsNullOrEmpty(targetPath)
               && string.Equals(self, targetPath, StringComparison.OrdinalIgnoreCase);
    }

    // The driver store sits inside Windows but holds vendor helpers (a Realtek audio service, for example);
    // those are ordinary startup items that the user may reasonably switch off.
    internal static bool IsInsideWindows(string? targetPath)
    {
        if (string.IsNullOrEmpty(targetPath)) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!PathProbe.IsUnder(targetPath, windows)) return false;
        return !PathProbe.IsUnder(targetPath, Path.Combine(windows, "System32", "DriverStore"))
               && !PathProbe.IsUnder(targetPath, Path.Combine(windows, "SysWOW64", "DriverStore"));
    }
}
