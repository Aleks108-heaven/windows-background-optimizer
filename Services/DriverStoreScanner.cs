using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BgOptimizer.Services;

internal enum DriverCopyStatus { NewestInUse, OlderInUse, NewestUnused, OlderUnused }

/// <param name="InfName">The package's original INF name (e.g. "netwtw10.inf"); two packages with the same one are copies of one driver.</param>
/// <param name="PublishedName">Name Windows gave it in the driver store (oem12.inf); null for inbox drivers.</param>
internal sealed record DriverPackage(
    string FolderName, string InfName, string Architecture, string? PublishedName,
    DateTime? Date, Version? Version, string Provider, long Bytes);

internal sealed record DriverCopy(DriverPackage Package, DriverCopyStatus Status);

/// <summary>
/// Reads the driver store (Windows keeps every version of a driver it has installed, so one driver can
/// be stored several times) and reports drivers stored more than once. Read-only: it never removes
/// anything. Everything comes from folder names, INF text and file hashes rather than from
/// pnputil's output, so it does not depend on the Windows display language.
/// </summary>
internal static class DriverStoreScanner
{
    private static readonly Regex PackageFolder = new(
        @"^(?<inf>.+?\.inf)_(?<arch>[A-Za-z0-9]+)_(?<hash>[0-9a-fA-F]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DriverVer = new(
        @"^\s*DriverVer\s*=\s*(?<m>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{4})\s*(?:,\s*(?<v>[\d.]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex ProviderLine = new(
        @"^\s*Provider\s*=\s*(?<p>[^;\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static string DriverStorePath =>
        Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");

    public static IReadOnlyList<DriverCopy> Scan(CancellationToken ct)
    {
        var published = ReadPublishedNamesByHash(ct);
        var inUse = ReadInUseInfNames();
        var packages = new List<DriverPackage>();

        IEnumerable<DirectoryInfo> folders;
        try { folders = new DirectoryInfo(DriverStorePath).EnumerateDirectories().ToList(); }
        catch { return Array.Empty<DriverCopy>(); }

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            var parsed = ParseFolderName(folder.Name);
            if (parsed == null) continue;

            string text = "", hash = "";
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(folder.FullName, parsed.Value.Inf));
                hash = Convert.ToHexString(SHA256.HashData(bytes));
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch { /* INF missing or unreadable: still counted, just without date/version */ }

            var (date, version) = ParseDriverVer(text);
            packages.Add(new DriverPackage(
                folder.Name, parsed.Value.Inf, parsed.Value.Arch, published.GetValueOrDefault(hash),
                date, version, ParseProvider(text), FolderSize(folder.FullName, ct)));
        }

        return FindDuplicates(packages, inUse);
    }

    internal static (string Inf, string Arch)? ParseFolderName(string folderName)
    {
        var m = PackageFolder.Match(folderName);
        return m.Success ? (m.Groups["inf"].Value, m.Groups["arch"].Value) : null;
    }

    internal static (DateTime? Date, Version? Version) ParseDriverVer(string infText)
    {
        var m = DriverVer.Match(infText);
        if (!m.Success) return (null, null);
        DateTime? date = null;
        try { date = new DateTime(int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["d"].Value)); }
        catch { /* impossible date in a hand-written INF */ }
        Version.TryParse(m.Groups["v"].Value, out var version);
        return (date, version);
    }

    // "Provider = %Vendor%" points at the [Strings] section; "Provider = Acme" is literal.
    internal static string ParseProvider(string infText)
    {
        var m = ProviderLine.Match(infText);
        if (!m.Success) return "";
        var value = m.Groups["p"].Value.Trim().Trim('"');
        if (value.Length > 2 && value[0] == '%' && value[^1] == '%')
        {
            var token = Regex.Match(infText,
                @"^\s*" + Regex.Escape(value.Trim('%')) + @"\s*=\s*""?(?<s>[^"";\r\n]+)""?",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return token.Success ? token.Groups["s"].Value.Trim() : "";
        }
        return value;
    }

    /// <summary>
    /// Groups packages that are copies of one driver (same INF name and architecture), newest first, and
    /// labels each by whether a device uses it. Drivers stored once are not duplicates and are left out.
    /// </summary>
    internal static IReadOnlyList<DriverCopy> FindDuplicates(IEnumerable<DriverPackage> packages, ISet<string> inUseInfNames)
    {
        var result = new List<DriverCopy>();
        var groups = packages
            .GroupBy(p => (p.InfName.ToLowerInvariant(), p.Architecture.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(p => p.Date ?? DateTime.MinValue)
                .ThenByDescending(p => p.Version ?? new Version(0, 0))
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                // Inbox drivers have no oemNN.inf name, so they are matched by their own INF name instead.
                var used = p.PublishedName != null ? inUseInfNames.Contains(p.PublishedName) : inUseInfNames.Contains(p.InfName);
                var newest = i == 0;
                result.Add(new DriverCopy(p, (newest, used) switch
                {
                    (true, true) => DriverCopyStatus.NewestInUse,
                    (false, true) => DriverCopyStatus.OlderInUse,
                    (true, false) => DriverCopyStatus.NewestUnused,
                    _ => DriverCopyStatus.OlderUnused,
                }));
            }
        }
        return result;
    }

    // When Windows installs a third-party driver it keeps a byte-identical copy of the package's INF as
    // %windir%\INF\oemNN.inf, and that oemNN.inf is the name devices report as theirs. Matching the copy by
    // its hash is exact for every package; the DriverDatabase registry keys were found to list only some.
    private static Dictionary<string, string> ReadPublishedNamesByHash(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var infFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            foreach (var file in Directory.EnumerateFiles(infFolder, "oem*.inf"))
            {
                ct.ThrowIfCancellationRequested();
                try { map[Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))] = Path.GetFileName(file); }
                catch (OperationCanceledException) { throw; }
                catch { /* one unreadable copy only leaves that package without a published name */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* without names the list still works, just without the oemNN.inf column */ }
        return map;
    }

    private static HashSet<string> ReadInUseInfNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT InfName FROM Win32_PnPSignedDriver");
            using var results = searcher.Get();
            foreach (ManagementBaseObject driver in results)
            {
                using (driver)
                    if (driver["InfName"] is string inf && inf.Length > 0) set.Add(inf);
            }
        }
        catch { /* WMI unavailable: every copy then shows as not in use, which the hint warns about */ }
        return set;
    }

    private static long FolderSize(string folder, CancellationToken ct)
    {
        long total = 0;
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
                    if (entry is DirectoryInfo dir) pending.Push(dir.FullName);
                    else if (entry is FileInfo file) total += file.Length;
                }
                catch { }
            }
        }
        return total;
    }
}
