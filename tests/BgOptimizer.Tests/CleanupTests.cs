using System.IO;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>Cleanup deletes files, so these pin down what it must leave alone as much as what it removes.</summary>
public class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bgopt-clean-{Guid.NewGuid():N}", "target");
    private readonly DateTime _now = DateTime.UtcNow;

    public CleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { } }

    private CleanupTarget Target(int minAgeDays, params string[] patterns) =>
        new("Test", () => new[] { _root }, patterns.Length == 0 ? new[] { "*" } : patterns, minAgeDays, false);

    private string Make(string relative, int ageDays, int bytes = 100)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, _now.AddDays(-ageDays));
        return path;
    }

    [Fact]
    public void OnlyFilesOlderThanTheTargetAgeAreDeleted()
    {
        var old = Make("old.tmp", ageDays: 10);
        var recent = Make("recent.tmp", ageDays: 2);

        var run = CleanupService.Clean(Target(minAgeDays: 7), _now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Equal(1, run.Deleted);
        Assert.Equal(100, run.BytesFreed);
    }

    [Fact]
    public void ScanReportsWhatCleanWouldDeleteWithoutDeletingIt()
    {
        var old = Make("a/old.log", ageDays: 40, bytes: 300);
        Make("a/new.log", ageDays: 1, bytes: 999);

        var scan = CleanupService.Scan(Target(minAgeDays: 30), _now);

        Assert.Equal(new CleanupScan(300, 1), scan);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void PatternsLimitWhichFilesAreTouched()
    {
        var log = Make("x.log", ageDays: 90);
        var other = Make("x.dat", ageDays: 90);

        CleanupService.Clean(Target(minAgeDays: 30, "*.log"), _now);

        Assert.False(File.Exists(log));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void ZeroAgeTargetClearsEverythingAndPrunesEmptyFoldersButKeepsTheRoot()
    {
        Make("sub/deeper/cache.bin", ageDays: 0);
        Make("top.bin", ageDays: 0);

        CleanupService.Clean(Target(minAgeDays: 0), _now.AddSeconds(5));

        Assert.True(Directory.Exists(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void FolderWithARecentFileIsNotPruned()
    {
        Make("keep/new.txt", ageDays: 1);
        Make("keep/old.txt", ageDays: 20);

        CleanupService.Clean(Target(minAgeDays: 7), _now);

        Assert.True(File.Exists(Path.Combine(_root, "keep", "new.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "keep", "old.txt")));
    }

    [Fact]
    public void FileInUseIsSkippedNotFatal()
    {
        var path = Make("locked.tmp", ageDays: 30);
        Make("free.tmp", ageDays: 30);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var run = CleanupService.Clean(Target(minAgeDays: 7), _now);
            Assert.Equal(1, run.Deleted);
            Assert.Equal(1, run.Skipped);
        }
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ReadOnlyFilesAreStillCleaned()
    {
        var path = Make("ro.tmp", ageDays: 30);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Equal(1, CleanupService.Clean(Target(minAgeDays: 7), _now).Deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void JunctionsAreNeverFollowed()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.doc");
        File.WriteAllText(precious, "keep me");
        File.SetLastWriteTimeUtc(precious, _now.AddDays(-400));

        var link = Path.Combine(_root, "link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch { return; } // symlink creation needs privileges the test host may lack; nothing to verify then

        CleanupService.Clean(Target(minAgeDays: 0), _now.AddSeconds(5));

        Assert.True(File.Exists(precious));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\DoesNotExist\Nowhere")]
    public void BroadOrMissingRootsAreRefused(string? root) =>
        Assert.False(CleanupService.IsSafeRoot(root));

    [Fact]
    public void UserProfileAndRealRootsAreHandledCorrectly()
    {
        Assert.False(CleanupService.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(CleanupService.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.True(CleanupService.IsSafeRoot(_root));
    }

    [Fact]
    public void BuiltInTargetsNeverIncludeDumpsRecycleBinOrTheLiveActionLog()
    {
        foreach (var target in CleanupService.Targets())
            foreach (var root in target.Roots())
            {
                Assert.DoesNotContain("Minidump", root, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("LiveKernelReports", root, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Recycle", root, StringComparison.OrdinalIgnoreCase);
                Assert.False(root.EndsWith("BgOptimizer", StringComparison.OrdinalIgnoreCase), "actions.log lives there");
            }
    }

    [Fact]
    public void RiskierCleanupGroupsStartUnticked()
    {
        var byId = CleanupService.Targets().ToDictionary(t => t.Id);
        Assert.True(byId["UserTemp"].SelectedByDefault);
        Assert.False(byId["ShaderCache"].SelectedByDefault);
        Assert.False(byId["BrowserCache"].SelectedByDefault);
        Assert.False(byId["DevCache"].SelectedByDefault);
    }

    [Fact]
    public void EveryCleanupGroupHasATranslatedNameAndDescription()
    {
        foreach (var target in CleanupService.Targets())
            foreach (var lang in BgOptimizer.Localization.Loc.Languages)
            {
                BgOptimizer.Localization.Loc.Instance.Language = lang.Code;
                Assert.NotEqual($"Clean_{target.Id}", BgOptimizer.Localization.Loc.Instance[$"Clean_{target.Id}"]);
                Assert.NotEqual($"Clean_{target.Id}_D", BgOptimizer.Localization.Loc.Instance[$"Clean_{target.Id}_D"]);
            }
        BgOptimizer.Localization.Loc.Instance.Language = "en";
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void BytesAreFormattedReadably(long bytes, string expected) =>
        Assert.Equal(expected, CleanupService.FormatBytes(bytes));
}

public class RefreshPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]   // window open: live data
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]   // hidden but Auto-Optimize is a standing job
    [InlineData(false, false, false)] // hidden, nothing to supervise: idle
    public void RefreshRunsOnlyWhenSomeoneIsLookingOrAutoOptimizeIsOn(bool visible, bool auto, bool expected) =>
        Assert.Equal(expected, RefreshPolicy.ShouldRun(visible, auto));
}
