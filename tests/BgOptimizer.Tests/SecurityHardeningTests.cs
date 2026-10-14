using System.Diagnostics;
using System.IO;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>
/// Regression tests for the elevated app's two attack surfaces found in the security review: the user-writable
/// data folder (a junction there must never be written through) and untrusted paths from the registry,
/// shortcuts and scheduled tasks (a network path must never be contacted).
/// </summary>
public class DataFolderJunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bgopt-junction-{Guid.NewGuid():N}");
    private readonly string _target;   // stands in for a folder only an administrator can write to
    private readonly string _link;     // what an ordinary program could plant in the user's data folder

    public DataFolderJunctionTests()
    {
        _target = Path.Combine(_root, "protected");
        _link = Path.Combine(_root, "BgOptimizer");
        Directory.CreateDirectory(_target);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        // One argument string (not ArgumentList): cmd.exe has its own quoting rules. A junction needs no administrator rights.
        psi.Arguments = $"/c mklink /J \"{_link}\" \"{_target}\"";
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(Directory.Exists(_link), "test setup: junction was not created");
    }

    public void Dispose()
    {
        try { Directory.Delete(_link); } catch { }        // removes the junction itself, never what it points to
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void AJunctionedFolderIsNotSafeToWriteButAPlainOneIs()
    {
        Assert.False(SafeAppData.IsSafeToWrite(Path.Combine(_link, "actions.log")));
        Assert.True(SafeAppData.IsSafeToWrite(Path.Combine(_target, "actions.log")));
        Assert.True(SafeAppData.IsSafeToWrite(Path.Combine(_root, "not-yet-created", "settings.json")));
    }

    [Fact]
    public void TheActionLogDoesNotWriteThroughAJunction()
    {
        ActionLog.Write("must not land in the protected folder", Path.Combine(_link, "actions.log"));

        Assert.Empty(Directory.GetFileSystemEntries(_target));
    }

    [Fact]
    public void SettingsAreNotSavedThroughAJunction()
    {
        new AppSettings { ViewOnly = true }.Save(Path.Combine(_link, "settings.json"));

        Assert.Empty(Directory.GetFileSystemEntries(_target));
    }

    [Fact]
    public void TheFrozenListIsNeitherWrittenNorDeletedThroughAJunction()
    {
        var existing = Path.Combine(_target, "suspended.json");
        File.WriteAllText(existing, "[]");

        SuspendedStore.Save(new[] { (Environment.ProcessId, "testhost") }, Path.Combine(_link, "suspended.json"));
        SuspendedStore.Save(Array.Empty<(int, string)>(), Path.Combine(_link, "suspended.json")); // an empty list normally deletes the file
        SuspendedStore.ResumeLeftovers(Path.Combine(_link, "suspended.json"));                     // and so does resuming

        Assert.True(File.Exists(existing));
        Assert.Equal("[]", File.ReadAllText(existing));
    }

    [Fact]
    public void OrdinaryFoldersStillWork()
    {
        var path = Path.Combine(_target, "actions.log");
        ActionLog.Write("fine", path);
        Assert.Contains("fine", File.ReadAllText(path));
    }
}

public class UntrustedPathTests
{
    private const string BlackholeShare = @"\\192.0.2.1\share\evil.exe";   // TEST-NET-1: never answers, so a probe would hang

    [Theory]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData(@"\\192.0.2.1\share\evil.exe")]
    [InlineData(@"\\?\C:\Windows\notepad.exe")]
    [InlineData(@"relative\path.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyFullyQualifiedLocalDriveLetterPathsAreEverProbed(string? path) =>
        Assert.False(PathProbe.IsOnLocalFixedDrive(path));

    [Fact]
    public void AFileOnTheSystemDriveIsLocal()
    {
        Assert.True(PathProbe.IsOnLocalFixedDrive(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.True(PathProbe.DriveIsPresent(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
    }

    [Fact]
    public void ANetworkPathIsNeverContactedWhenReadingPublisherInfo()
    {
        var sw = Stopwatch.StartNew();

        Assert.Null(PublisherInfo.Get(BlackholeShare));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}: the network path was probed");
    }

    [Fact]
    public void ANetworkInstallPathInInstalledAppsIsNotProbedOrReportedAsMissing()
    {
        var sw = Stopwatch.StartNew();
        var installed = new[] { new InstalledProgram("Planted entry", "x", @"\\192.0.2.1\share\app", BlackholeShare, false) };

        Assert.Empty(LeftoverScanner.FindBrokenRegistrations(installed).ToList());

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}: the network path was probed");
    }

    [Fact]
    public void AStartupEntryPointingAtANetworkShareIsListedButNeverContacted()
    {
        var entry = new StartupEntry { Name = "planted", Command = $"\"{BlackholeShare}\" -run", Source = StartupSource.RegistryRunHkcu, Enabled = true };
        var sw = Stopwatch.StartNew();

        StartupManager.Enrich(entry, new Dictionary<string, string?>());

        Assert.Equal(BlackholeShare, entry.TargetPath);   // still shown, so the user can see it
        Assert.False(entry.TargetMissing);                // "cannot check" is not "missing"
        Assert.Null(entry.Publisher);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}: the network path was probed");
    }
}
