using BgOptimizer.Localization;
using BgOptimizer.Services;

namespace BgOptimizer.Models;

public sealed record LeftoverRow(string Kind, string Name, string Path, string Size, string Note);

public sealed record DuplicateRow(int Group, string Name, string Folder, string Size, string Modified, string Path);

public sealed record DriverRow(string Driver, string Version, string Date, string Status, string PublishedName, string Size, string Provider);

/// <summary>
/// Turns scan results into grid rows. Rows hold finished text, so the window rebuilds them from the kept
/// results when the language changes instead of every row listening to the language singleton.
/// </summary>
internal static class CheckRows
{
    private const string None = "—";

    public static IReadOnlyList<LeftoverRow> ForLeftovers(IEnumerable<LeftoverFinding> findings) =>
        findings
            .OrderBy(f => f.Kind)
            .ThenByDescending(f => f.Bytes)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new LeftoverRow(
                Loc.Instance[$"Ck_Kind_{f.Kind}"], f.Name, f.Path,
                f.Bytes > 0 ? CleanupService.FormatBytes(f.Bytes) : None, f.Note))
            .ToList();

    public static IReadOnlyList<DuplicateRow> ForDuplicates(IEnumerable<DuplicateGroup> groups)
    {
        var rows = new List<DuplicateRow>();
        var number = 0;
        foreach (var group in groups)
        {
            number++;
            foreach (var file in group.Files.OrderBy(f => f.ModifiedUtc))
                rows.Add(new DuplicateRow(
                    number, System.IO.Path.GetFileName(file.Path), System.IO.Path.GetDirectoryName(file.Path) ?? "",
                    CleanupService.FormatBytes(group.Size), file.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), file.Path));
        }
        return rows;
    }

    public static IReadOnlyList<DriverRow> ForDrivers(IEnumerable<DriverCopy> copies) =>
        copies.Select(c => new DriverRow(
                c.Package.InfName,
                c.Package.Version?.ToString() ?? None,
                c.Package.Date?.ToString("yyyy-MM-dd") ?? None,
                Loc.Instance[$"Ck_Drv_{c.Status}"],
                c.Package.PublishedName ?? None,
                CleanupService.FormatBytes(c.Package.Bytes),
                c.Package.Provider.Length > 0 ? c.Package.Provider : None))
            .ToList();

    public static string DuplicateSummary(IReadOnlyCollection<DuplicateGroup> groups) =>
        groups.Count == 0
            ? Loc.Instance["Ck_NothingFound"]
            : Loc.Instance.Format("Ck_DupSummary", groups.Count, CleanupService.FormatBytes(groups.Sum(g => g.WastedBytes)));

    public static string DriverSummary(IReadOnlyCollection<DriverCopy> copies) =>
        copies.Count == 0
            ? Loc.Instance["Ck_NothingFound"]
            : Loc.Instance.Format("Ck_DrvSummary",
                copies.Select(c => c.Package.InfName.ToLowerInvariant()).Distinct().Count(),
                CleanupService.FormatBytes(copies.Where(c => c.Status == DriverCopyStatus.OlderUnused).Sum(c => c.Package.Bytes)));
}
