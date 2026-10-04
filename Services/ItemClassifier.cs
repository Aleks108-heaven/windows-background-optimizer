using BgOptimizer.Models;

namespace BgOptimizer.Services;

/// <summary>
/// Best-effort label for Startup Apps and Services, based on name/path
/// patterns. This is a hint to help a user decide, not something the app
/// acts on automatically - the same task can be safe-to-disable for one
/// person and load-bearing for another (e.g. a vendor helper that also
/// drives a hotkey the user relies on), so it's surfaced as a column, never
/// as an auto-selected checkbox.
/// </summary>
internal static class ItemClassifier
{
    // Names the prompt this feature was built from called out explicitly as
    // "keep enabled" even though they don't match ProtectedItems (those are
    // Windows-internal names, not this machine's task names).
    private static readonly HashSet<string> ExplicitCore = new(StringComparer.OrdinalIgnoreCase)
    {
        "Keep-WSLService-Enabled",
    };

    private static readonly string[] TelemetryPatterns =
    {
        "telemetry", "ceip", "crashreport", "crash reporting", "consolidator",
        "usbceip", "compattelrunner", "feedback", "watson", "diagnostic",
        "customer experience", "reporting task", "bugreport",
    };

    private static readonly string[] DevToolPatterns =
    {
        "docker", "wsl", "node.js", "nodejs", "python", "git", "vscode",
        "visual studio", "npm", "yarn", "kubernetes", "postgres", "mysql",
        "redis", "openjdk", "jdk",
    };

    private static readonly string[] VendorDiagPatterns =
    {
        "asus", "amd ", "amdsco", "nvidia", "realtek", "dell", "lenovo",
        "driver", "survey", "systemanalysis", "systemdiagnos", "optimization",
        "supporttypeupdate", "install manager", "update checker",
    };

    // Audio components are labelled Core so neither the Startup/Services tabs nor Laptop Saver
    // treat a vendor audio helper ("realtek", "driver" in its name) as disposable bloat.
    public static ItemCategory ClassifyStartup(string name, string command) =>
        ProtectedItems.IsProcessProtected(name) || ProtectedItems.IsAudioApp(name) ? ItemCategory.Core : Classify(name, command);

    public static ItemCategory ClassifyService(string serviceName, string displayName) =>
        ProtectedItems.IsServiceProtected(serviceName) || ProtectedItems.IsAudioApp(serviceName) || ProtectedItems.IsAudioApp(displayName)
            ? ItemCategory.Core : Classify(serviceName, displayName);

    // Short dev-tool words must start a word: plain Contains put "Digital Editions" and
    // "Legitimate Updater" in DevTool because both contain "git". "GitHubDesktop" still matches.
    private static bool ContainsAtWordStart(string text, string pattern)
    {
        for (var i = text.IndexOf(pattern, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(pattern, i + 1, StringComparison.Ordinal))
            if (i == 0 || !char.IsLetter(text[i - 1])) return true;
        return false;
    }

    private static ItemCategory Classify(string a, string b)
    {
        if (ExplicitCore.Contains(a) || ExplicitCore.Contains(b)) return ItemCategory.Core;

        var text = $"{a} {b}".ToLowerInvariant();
        if (Array.Exists(TelemetryPatterns, p => text.Contains(p))) return ItemCategory.Telemetry;
        if (Array.Exists(DevToolPatterns, p => ContainsAtWordStart(text, p))) return ItemCategory.DevTool;
        if (Array.Exists(VendorDiagPatterns, p => text.Contains(p))) return ItemCategory.VendorDiag;
        return ItemCategory.Unknown;
    }
}
