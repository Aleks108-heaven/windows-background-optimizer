using System.Diagnostics;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;

namespace BgOptimizer.Services;

/// <summary>
/// Removes old copies of a driver from the driver store with <c>pnputil /delete-driver oemNN.inf</c>. Only a copy
/// the scan labelled "older, not in use" is ever offered, and pnputil is never given /force, so Windows itself
/// still refuses a package a device uses (the scan's "in use" label can be wrong if WMI was unavailable).
/// </summary>
internal static class DriverStoreCleaner
{
    private static readonly Regex PublishedInf = new(@"^oem\d{1,6}\.inf\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private const int ExitSuccess = 0;
    private const int ExitSuccessRebootRequired = 3010; // ERROR_SUCCESS_REBOOT_REQUIRED

    /// <summary>
    /// An older copy that no device uses, with a name pnputil accepts and a date to prove it is older. Inbox
    /// drivers have no oemNN.inf name (Windows owns them) and so are never offered.
    /// </summary>
    public static bool CanDelete(DriverCopy copy) =>
        copy.Status == DriverCopyStatus.OlderUnused
        && copy.Package.Date is not null
        && IsPublishedName(copy.Package.PublishedName);

    /// <summary>Strictly "oemNN.inf": the name is the only thing that reaches pnputil's command line.</summary>
    internal static bool IsPublishedName(string? name) => name is not null && PublishedInf.IsMatch(name);

    internal static IReadOnlyList<string> Arguments(string publishedName) => new[] { "/delete-driver", publishedName };

    /// <summary>Removes one copy. Blocked in view-only mode; every call is written to the action log, whatever the outcome.</summary>
    public static ActionResult Delete(DriverCopy copy)
    {
        var inf = copy.Package.InfName;
        if (ViewOnlyMode.Block(inf) is { } viewOnly) return viewOnly;
        if (!CanDelete(copy)) return new ActionResult(false, "Ck_DrvNotRemovable", inf);

        var published = copy.Package.PublishedName!;
        var result = Run(published, inf);
        ActionLog.Write($"DRIVER_DELETE inf=\"{inf}\" published={published} ok={result.Ok} ({result.MessageKey})");
        return result;
    }

    private static ActionResult Run(string published, string inf)
    {
        try
        {
            var psi = new ProcessStartInfo(SystemTools.Path("pnputil.exe"))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in Arguments(published)) psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null) return new ActionResult(false, "Ck_DrvRemoveFailed", inf, "pnputil did not start");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ActionResult(false, "Ck_DrvRemoveFailed", inf, "pnputil timed out");
            }
            process.WaitForExit(); // lets the redirected output finish
            if (process.ExitCode is ExitSuccess or ExitSuccessRebootRequired)
                return new ActionResult(true, "Ck_DrvRemoved", inf, published);

            // pnputil's own wording is localized text; it is shown as-is, shortened, next to the exit code.
            var text = (output.IsCompletedSuccessfully ? output.Result : "") + (errors.IsCompletedSuccessfully ? errors.Result : "");
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Length > 0) ?? "";
            if (line.Length > 160) line = line[..160] + "…";
            return new ActionResult(false, "Ck_DrvRemoveFailed", inf, line.Length > 0 ? $"{line} (exit code {process.ExitCode})" : $"exit code {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Ck_DrvRemoveFailed", inf, ex.Message);
        }
    }
}
