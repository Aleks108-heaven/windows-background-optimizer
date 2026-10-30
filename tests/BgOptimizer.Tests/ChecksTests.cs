using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>Duplicate-file search: right files grouped, nothing else, read-only, stoppable.</summary>
public class DuplicateFinderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-dup-{Guid.NewGuid():N}");

    public DuplicateFinderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string Make(string relative, byte[] content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void IdenticalFilesAreGroupedAndWasteIsOneCopyLess()
    {
        var content = Bytes(300_000, 1);
        Make("a/one.bin", content);
        Make("b/two.bin", content);
        Make("c/three.bin", content);
        Make("c/unique.bin", Bytes(300_000, 2));

        var groups = DuplicateFinder.Find(new[] { _dir }, 1024, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(300_000L * 2, group.WastedBytes);
    }

    [Fact]
    public void SameSizeAndSameStartButDifferentEndIsNotADuplicate()
    {
        // Equal size and equal first 64 KB, differing only after it: only the full hash can tell them apart.
        var a = Bytes(200_000, 3);
        var b = (byte[])a.Clone();
        b[^1] ^= 0xFF;
        Make("x.bin", a);
        Make("y.bin", b);

        Assert.Empty(DuplicateFinder.Find(new[] { _dir }, 1024, CancellationToken.None));
    }

    [Fact]
    public void FilesBelowTheMinimumSizeAreIgnored()
    {
        var content = Bytes(500, 4);
        Make("s1.bin", content);
        Make("s2.bin", content);

        Assert.Empty(DuplicateFinder.Find(new[] { _dir }, 1024, CancellationToken.None));
        Assert.Single(DuplicateFinder.Find(new[] { _dir }, 100, CancellationToken.None));
    }

    [Fact]
    public void OverlappingRootsDoNotCountAFileTwice()
    {
        var content = Bytes(50_000, 5);
        Make("top.bin", content);
        Make("sub/copy.bin", content);

        var groups = DuplicateFinder.Find(new[] { _dir, Path.Combine(_dir, "sub") }, 1024, CancellationToken.None);

        Assert.Equal(2, Assert.Single(groups).Files.Count);
    }

    [Fact]
    public void VersionControlAndPackageFoldersAreSkipped()
    {
        var content = Bytes(50_000, 6);
        Make("real.bin", content);
        Make(".git/objects/copy.bin", content);
        Make("node_modules/pkg/copy.bin", content);

        Assert.Empty(DuplicateFinder.Find(new[] { _dir }, 1024, CancellationToken.None));
    }

    [Fact]
    public void FindingDuplicatesNeverChangesOrDeletesAnything()
    {
        var content = Bytes(50_000, 7);
        var a = Make("a.bin", content);
        var b = Make("b.bin", content);

        DuplicateFinder.Find(new[] { _dir }, 1024, CancellationToken.None);

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal(content, File.ReadAllBytes(a));
    }

    [Fact]
    public void AStoppedScanThrowsInsteadOfReturningHalfAnAnswer()
    {
        Make("a.bin", Bytes(50_000, 8));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DuplicateFinder.Find(new[] { _dir }, 1024, cts.Token));
    }

    [Fact]
    public void MissingRootIsSkippedNotAnError()
    {
        Assert.Empty(DuplicateFinder.Find(new[] { Path.Combine(_dir, "nope") }, 1024, CancellationToken.None));
    }

    [Fact]
    public void DefaultMinimumMatchesWhatTheHintPromises() => Assert.Equal(1024 * 1024, DuplicateFinder.DefaultMinBytes);
}

public class PathProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-probe-{Guid.NewGuid():N}");

    public PathProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void ExistingAndMissingPathsAreToldApart()
    {
        var file = Path.Combine(_dir, "here.txt");
        File.WriteAllText(file, "x");

        Assert.False(PathProbe.IsMissing(file));
        Assert.False(PathProbe.IsMissing(_dir));
        Assert.True(PathProbe.IsMissing(Path.Combine(_dir, "gone.txt")));
        Assert.True(PathProbe.IsMissing(Path.Combine(_dir, "gone-folder", "gone.txt")));
    }

    [Fact]
    public void AnUnjudgeablePathIsNeverReportedAsMissing()
    {
        Assert.False(PathProbe.IsMissing("not a real path |<>"));
        Assert.False(PathProbe.IsMissing(""));
    }

    [Fact]
    public void IsUnderMatchesWholeFolderNamesOnly()
    {
        Assert.True(PathProbe.IsUnder(@"C:\Program Files\App\a.exe", @"C:\Program Files\App"));
        Assert.True(PathProbe.IsUnder(@"c:\program files\app\a.exe", @"C:\Program Files\App\"));
        Assert.False(PathProbe.IsUnder(@"C:\Program Files\App2\a.exe", @"C:\Program Files\App"));
        Assert.False(PathProbe.IsUnder(@"C:\Program Files\App", @"C:\Program Files\App"));
    }
}

/// <summary>Leftover detection: conservative, so "cannot tell" and anything owned are never reported.</summary>
public class LeftoverScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-left-{Guid.NewGuid():N}");

    public LeftoverScannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        Directory.Delete(_dir, recursive: true);
    }

    private string Folder(string name, params string[] files)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        foreach (var f in files) File.WriteAllText(Path.Combine(folder, f), "x");
        return folder;
    }

    private static IReadOnlyList<LeftoverFinding> Orphans(string root, string[]? claimedPaths = null, string[]? claimedNames = null) =>
        LeftoverScanner.FindOrphanFolders(new[] { root }, claimedPaths ?? Array.Empty<string>(), claimedNames ?? Array.Empty<string>(),
            CancellationToken.None).ToList();

    [Fact]
    public void FoldersAreClassifiedByWhatIsLeftInThem()
    {
        Folder("EmptyOne");
        Folder("DataOnly", "settings.ini", "log.txt");
        Folder("HasProgram", "tool.exe", "readme.txt");

        var byName = Orphans(_dir).ToDictionary(f => f.Name);

        Assert.Equal(LeftoverKind.EmptyFolder, byName["EmptyOne"].Kind);
        Assert.Equal(LeftoverKind.DataOnlyFolder, byName["DataOnly"].Kind);
        Assert.Equal(LeftoverKind.UnregisteredFolder, byName["HasProgram"].Kind);
        Assert.True(byName["DataOnly"].Bytes > 0);
    }

    [Fact]
    public void AFolderAnInstalledAppLivesInIsNotALeftover()
    {
        var owned = Folder("OwnedByPath", "app.exe");

        Assert.Empty(Orphans(_dir, claimedPaths: new[] { Path.Combine(owned, "app.exe") }));
    }

    [Fact]
    public void AFolderNamedAfterAnInstalledAppOrPublisherIsNotALeftover()
    {
        Folder("Docker", "x.exe");
        Folder("Acme Tools", "y.exe");

        Assert.Empty(Orphans(_dir, claimedNames: new[] { "Docker Desktop", "Acme Tools Inc." }));
    }

    [Fact]
    public void HiddenAndSystemFoldersAreNeverReported()
    {
        var hidden = Folder("HiddenOne", "a.exe");
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);

        Assert.Empty(Orphans(_dir));
    }

    [Theory]
    [InlineData("Common Files")]
    [InlineData("Windows Defender")]
    [InlineData("Microsoft Office")]
    [InlineData("dotnet")]
    [InlineData("WindowsApps")]
    public void WindowsOwnFoldersAreNeverReported(string name) =>
        Assert.True(LeftoverScanner.IsSystemFolder(name, FileAttributes.Directory));

    [Fact]
    public void VeryShortFolderNamesAreNotGuessedAt()
    {
        Assert.True(LeftoverScanner.IsClaimed(@"C:\Program Files\X1", Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void AnInstallFolderThatIsGoneIsReportedButOneThatExistsIsNot()
    {
        var present = Folder("StillThere", "a.exe");
        var gone = Path.Combine(_dir, "Removed");
        var installed = new[]
        {
            new InstalledProgram("Present App", "Pub", present, null, false),
            new InstalledProgram("Removed App", "Acme", gone, null, false),
        };

        var finding = Assert.Single(LeftoverScanner.FindBrokenRegistrations(installed));

        Assert.Equal(LeftoverKind.BrokenRegistration, finding.Kind);
        Assert.Equal("Removed App", finding.Name);
        Assert.Equal(gone, finding.Path);
    }

    [Fact]
    public void HiddenSystemEntriesAndWindowsOwnToolsAreNotJudged()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var installed = new[]
        {
            new InstalledProgram("Hidden component", "", Path.Combine(_dir, "Nope1"), null, IsSystemComponent: true),
            new InstalledProgram("Uses msiexec", "", null, Path.Combine(windows, "System32", "no-such-tool.exe"), false),
            new InstalledProgram("No paths at all", "", null, null, false),
        };

        Assert.Empty(LeftoverScanner.FindBrokenRegistrations(installed));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe")]
    [InlineData("C:\\Program Files\\App\\unins000.exe /SILENT", "C:\\Program Files\\App\\unins000.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe,0", "C:\\Program Files\\App\\app.exe")]
    [InlineData("\"C:\\x\\icon.ico\",-12", "C:\\x\\icon.ico")]
    [InlineData("MsiExec.exe /X{ABC}", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FilePathIsPulledOutOfUninstallStrings(string? command, string? expected) =>
        Assert.Equal(expected, LeftoverScanner.ExtractFilePath(command));
}

/// <summary>Driver-store parsing and duplicate grouping, from INF text and folder names (no display-language dependence).</summary>
public class DriverStoreScannerTests
{
    private static DriverPackage Package(string inf, string folder, DateTime? date, string? version, string? oem, long bytes = 1000, string arch = "amd64") =>
        new(folder, inf, arch, oem, date, version == null ? null : Version.Parse(version), "Acme", bytes);

    [Theory]
    [InlineData("netwtw10.inf_amd64_0123abcd", "netwtw10.inf", "amd64")]
    [InlineData("oem.with.dots.inf_arm64_FF00", "oem.with.dots.inf", "arm64")]
    public void PackageFolderNamesAreSplit(string folder, string inf, string arch)
    {
        var parsed = DriverStoreScanner.ParseFolderName(folder);
        Assert.Equal((inf, arch), parsed);
    }

    [Theory]
    [InlineData("not-a-package")]
    [InlineData("readme.txt")]
    public void NonPackageFoldersAreIgnored(string folder) => Assert.Null(DriverStoreScanner.ParseFolderName(folder));

    [Fact]
    public void DriverVerGivesDateAndVersionRegardlessOfWindowsLanguage()
    {
        var (date, version) = DriverStoreScanner.ParseDriverVer("[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nDriverVer = 09/28/2026,32.0.32015.2008\r\n");

        Assert.Equal(new DateTime(2026, 9, 28), date);
        Assert.Equal(new Version(32, 0, 32015, 2008), version);
    }

    [Fact]
    public void AnInfWithoutDriverVerOrWithANonsenseDateDoesNotThrow()
    {
        Assert.Equal((null, null), DriverStoreScanner.ParseDriverVer("[Version]\r\nClass=Net"));
        var (date, _) = DriverStoreScanner.ParseDriverVer("DriverVer=13/45/2026,1.0.0.0");
        Assert.Null(date);
    }

    [Fact]
    public void ProviderIsReadLiterallyOrResolvedFromTheStringsSection()
    {
        Assert.Equal("Acme Corp", DriverStoreScanner.ParseProvider("[Version]\nProvider = Acme Corp ; comment\n"));
        Assert.Equal("Realtek Semiconductor", DriverStoreScanner.ParseProvider(
            "[Version]\nProvider=%RTK%\n[Strings]\nRTK = \"Realtek Semiconductor\"\n"));
        Assert.Equal("", DriverStoreScanner.ParseProvider("[Version]\nClass=Net\n"));
    }

    [Fact]
    public void OnlyDriversStoredMoreThanOnceAreListed()
    {
        var packages = new[]
        {
            Package("single.inf", "single.inf_amd64_1", new DateTime(2026, 1, 1), "1.0.0.0", "oem1.inf"),
            Package("twice.inf", "twice.inf_amd64_1", new DateTime(2025, 1, 1), "1.0.0.0", "oem2.inf"),
            Package("twice.inf", "twice.inf_amd64_2", new DateTime(2026, 1, 1), "2.0.0.0", "oem3.inf"),
        };

        var copies = DriverStoreScanner.FindDuplicates(packages, new HashSet<string>());

        Assert.Equal(2, copies.Count);
        Assert.All(copies, c => Assert.Equal("twice.inf", c.Package.InfName));
    }

    [Fact]
    public void NewestIsFirstAndStatusSaysWhetherADeviceUsesIt()
    {
        var packages = new[]
        {
            Package("gpu.inf", "gpu.inf_amd64_old", new DateTime(2025, 5, 1), "31.0.0.1", "oem10.inf"),
            Package("gpu.inf", "gpu.inf_amd64_mid", new DateTime(2026, 1, 1), "31.0.0.9", "oem11.inf"),
            Package("gpu.inf", "gpu.inf_amd64_new", new DateTime(2026, 9, 28), "32.0.0.1", "oem12.inf"),
        };

        var copies = DriverStoreScanner.FindDuplicates(packages, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oem12.inf", "oem10.inf" });

        Assert.Equal(new[] { "oem12.inf", "oem11.inf", "oem10.inf" }, copies.Select(c => c.Package.PublishedName));
        Assert.Equal(DriverCopyStatus.NewestInUse, copies[0].Status);
        Assert.Equal(DriverCopyStatus.OlderUnused, copies[1].Status);
        Assert.Equal(DriverCopyStatus.OlderInUse, copies[2].Status);
    }

    [Fact]
    public void SameDateIsBrokenByVersionAndNewestUnusedIsFlagged()
    {
        var day = new DateTime(2026, 2, 2);
        var packages = new[]
        {
            Package("x.inf", "x.inf_amd64_a", day, "1.2.0.0", "oem1.inf"),
            Package("x.inf", "x.inf_amd64_b", day, "1.10.0.0", "oem2.inf"),
        };

        var copies = DriverStoreScanner.FindDuplicates(packages, new HashSet<string>());

        Assert.Equal("oem2.inf", copies[0].Package.PublishedName); // 1.10 is newer than 1.2
        Assert.Equal(DriverCopyStatus.NewestUnused, copies[0].Status);
        Assert.Equal(DriverCopyStatus.OlderUnused, copies[1].Status);
    }

    [Fact]
    public void DifferentArchitecturesAreNotCopiesOfEachOther()
    {
        var packages = new[]
        {
            Package("y.inf", "y.inf_amd64_1", new DateTime(2026, 1, 1), "1.0.0.0", null, arch: "amd64"),
            Package("y.inf", "y.inf_arm64_1", new DateTime(2026, 1, 1), "1.0.0.0", null, arch: "arm64"),
        };

        Assert.Empty(DriverStoreScanner.FindDuplicates(packages, new HashSet<string>()));
    }

    [Fact]
    public void InboxDriversWithoutAnOemNameAreMatchedByTheirInfName()
    {
        var packages = new[]
        {
            Package("usb.inf", "usb.inf_amd64_1", new DateTime(2025, 1, 1), "10.0.1.0", null),
            Package("usb.inf", "usb.inf_amd64_2", new DateTime(2026, 1, 1), "10.0.2.0", null),
        };

        var copies = DriverStoreScanner.FindDuplicates(packages, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "usb.inf" });

        Assert.All(copies, c => Assert.True(c.Status is DriverCopyStatus.NewestInUse or DriverCopyStatus.OlderInUse));
    }

    [Fact]
    public void ScanningTheRealDriverStoreOnlyReadsAndReturnsConsistentRows()
    {
        // Runs against this machine's driver store when present; must never throw, whatever it holds.
        var copies = DriverStoreScanner.Scan(CancellationToken.None);

        foreach (var group in copies.GroupBy(c => c.Package.InfName.ToLowerInvariant()))
            Assert.True(group.Count() > 1, "a driver stored once must not be listed");
    }
}

/// <summary>Every new string exists in every language, with the same placeholders as English.</summary>
[Collection("ViewOnly")] // one test here switches the app-wide language
public class ChecksLocalizationTests
{
    private static Dictionary<string, Dictionary<string, string>> Table() =>
        (Dictionary<string, Dictionary<string, string>>)typeof(Loc)
            .GetField("Strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static bool IsChecksKey(string key) => key.StartsWith("Ck_", StringComparison.Ordinal) || key == "Tab_Checks";

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v).ToArray();

    [Fact]
    public void EveryLanguageHasEveryChecksString()
    {
        var table = Table();
        var englishKeys = table["en"].Keys.Where(IsChecksKey).ToList();
        Assert.NotEmpty(englishKeys);

        foreach (var lang in Loc.Languages)
            foreach (var key in englishKeys)
                Assert.True(table[lang.Code].ContainsKey(key), $"{lang.Code} is missing {key}");
    }

    [Fact]
    public void TranslationsKeepTheEnglishPlaceholders()
    {
        var table = Table();
        foreach (var key in table["en"].Keys.Where(IsChecksKey))
            foreach (var lang in Loc.Languages)
                Assert.True(Placeholders(table["en"][key]).SequenceEqual(Placeholders(table[lang.Code][key])),
                    $"{lang.Code}/{key}: placeholders differ from English");
    }

    [Fact]
    public void NoLanguageLeavesAChecksStringInEnglish()
    {
        // Words that are legitimately identical to English in some language.
        var mayMatch = new HashSet<string> { "Ck_Col_Version", "Ck_Col_Date", "Ck_Col_Driver", "Ck_Col_Note", "Ck_Col_Size", "Ck_Col_Folder" /* Polish: "Folder" */ };
        var table = Table();
        foreach (var key in table["en"].Keys.Where(k => IsChecksKey(k) && !mayMatch.Contains(k)))
            foreach (var lang in Loc.Languages.Where(l => l.Code != "en"))
                Assert.NotEqual(table["en"][key], table[lang.Code][key]);
    }

    [Fact]
    public void EveryKindAndStatusShownInAGridHasATranslation()
    {
        var table = Table();
        foreach (var kind in Enum.GetValues<LeftoverKind>())
            foreach (var lang in Loc.Languages)
                Assert.True(table[lang.Code].ContainsKey($"Ck_Kind_{kind}"), $"{lang.Code} lacks Ck_Kind_{kind}");
        foreach (var status in Enum.GetValues<DriverCopyStatus>())
            foreach (var lang in Loc.Languages)
                Assert.True(table[lang.Code].ContainsKey($"Ck_Drv_{status}"), $"{lang.Code} lacks Ck_Drv_{status}");
    }

    [Fact]
    public void RowsAreBuiltInTheSelectedLanguage()
    {
        try
        {
            var findings = new[] { new LeftoverFinding(LeftoverKind.EmptyFolder, "Old", @"C:\Program Files\Old", 0, "0") };
            Loc.Instance.Language = "en";
            var en = CheckRows.ForLeftovers(findings).Single().Kind;
            Loc.Instance.Language = "uk";
            var uk = CheckRows.ForLeftovers(findings).Single().Kind;

            Assert.NotEqual(en, uk);
        }
        finally { Loc.Instance.Language = "en"; }
    }

    [Fact]
    public void SummariesSayNothingFoundWhenThereIsNothing()
    {
        Loc.Instance.Language = "en";
        Assert.Equal("Nothing found.", CheckRows.DuplicateSummary(Array.Empty<DuplicateGroup>()));
        Assert.Equal("Nothing found.", CheckRows.DriverSummary(Array.Empty<DriverCopy>()));
    }
}
