using System.IO;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

public class ExplorerLauncherTests
{
    [Fact]
    public void ASelectedPathWithSpacesIsQuotedOnItsOwnNotTogetherWithTheSwitch()
    {
        // "/select,C:\Program Files\Asus" as one quoted argument is what made Explorer open Documents.
        Assert.Equal(@"/select,""C:\Program Files\Asus""", ExplorerLauncher.Arguments(@"C:\Program Files\Asus", select: true));
    }

    [Fact]
    public void AFolderToOpenIsJustQuoted() =>
        Assert.Equal(@"""C:\Windows\System32\DriverStore\FileRepository\x.inf_amd64_1""",
            ExplorerLauncher.Arguments(@"C:\Windows\System32\DriverStore\FileRepository\x.inf_amd64_1", select: false));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\a\"b")]            // a quote could end the argument early
    [InlineData("C:\\a\0b")]
    public void UnquotablePathsAreRefused(string path) =>
        Assert.Null(ExplorerLauncher.Arguments(path, select: true));
}

public class LoadPressureTests
{
    private static Dictionary<string, double?> Loads(double? cpu = 10, double? mem = 10, double? disk = null, double? gpu = null) =>
        new() { ["cpu"] = cpu, ["mem"] = mem, ["disk"] = disk, ["gpu"] = gpu };

    [Fact]
    public void AMomentarySpikeDoesNotCount()
    {
        var p = new LoadPressure();

        Assert.False(p.Update(Loads(cpu: 95), 90));
        Assert.False(p.Update(Loads(cpu: 95), 90));
        Assert.False(p.Update(Loads(cpu: 20), 90)); // the streak is broken
        Assert.False(p.Update(Loads(cpu: 95), 90));
        Assert.False(p.Update(Loads(cpu: 95), 90));
    }

    [Fact]
    public void ThreeSamplesInARowAboveTheLimitStartIt()
    {
        var p = new LoadPressure();

        p.Update(Loads(mem: 95), 90);
        p.Update(Loads(mem: 95), 90);
        Assert.True(p.Update(Loads(mem: 95), 90));
        Assert.Equal(new[] { "mem" }, p.Causes);
    }

    [Fact]
    public void ItKeepsGoingUntilEveryLoadIsClearlyUnderTheLimit()
    {
        var p = new LoadPressure();
        for (var i = 0; i < LoadPressure.SustainedSamples; i++) p.Update(Loads(cpu: 95), 90);
        Assert.True(p.Active);

        // 88 % is under the limit but not 5 points under it: still active, so it does not flip at the line.
        for (var i = 0; i < 6; i++) Assert.True(p.Update(Loads(cpu: 88), 90));

        // 5 points under the limit (85 %) for three samples in a row releases it.
        Assert.True(p.Update(Loads(cpu: 85), 90));
        Assert.True(p.Update(Loads(cpu: 85), 90));
        Assert.False(p.Update(Loads(cpu: 85), 90));
        Assert.Empty(p.Causes);
    }

    [Fact]
    public void ACalmStreakIsBrokenByAnotherBusySample()
    {
        var p = new LoadPressure();
        for (var i = 0; i < LoadPressure.SustainedSamples; i++) p.Update(Loads(cpu: 95), 90);

        p.Update(Loads(cpu: 50), 90);
        p.Update(Loads(cpu: 50), 90);
        p.Update(Loads(cpu: 89), 90); // back near the limit
        p.Update(Loads(cpu: 50), 90);
        p.Update(Loads(cpu: 50), 90);

        Assert.True(p.Active);
    }

    [Fact]
    public void AnUnavailableLoadIsNeitherPressureNorCalm()
    {
        var p = new LoadPressure();
        for (var i = 0; i < 10; i++) Assert.False(p.Update(Loads(cpu: 10, mem: 10, disk: null, gpu: null), 90));

        for (var i = 0; i < LoadPressure.SustainedSamples; i++) p.Update(Loads(cpu: 95, disk: null), 90);
        Assert.True(p.Active);
    }

    [Fact]
    public void AnyOneLoadIsEnoughAndResetForgetsEverything()
    {
        var p = new LoadPressure();
        for (var i = 0; i < LoadPressure.SustainedSamples; i++) p.Update(Loads(cpu: 10, mem: 10, disk: 99), 90);
        Assert.Equal(new[] { "disk" }, p.Causes);

        p.Reset();

        Assert.False(p.Active);
        Assert.False(p.Update(Loads(disk: 99), 90)); // the count starts again
    }

    [Fact]
    public void ALimitOf100CanNeverBeExceeded()
    {
        var p = new LoadPressure();
        for (var i = 0; i < 10; i++) Assert.False(p.Update(Loads(cpu: 100, mem: 100), 100));
    }
}

public class AutoOptimizeOnLoadSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-loadopt-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void ItIsOffByDefaultAndSurvivesARestart()
    {
        var path = Path.Combine(_dir, "settings.json");
        Assert.False(AppSettings.Load(path).AutoOptimizeOnLoad);

        new AppSettings { AutoOptimizeOnLoad = true }.Save(path);

        Assert.True(AppSettings.Load(path).AutoOptimizeOnLoad);
    }

    [Theory]
    [InlineData(false, false, true, true)]    // hidden in the tray with only this switch on: it must keep running
    [InlineData(false, false, false, false)]
    public void ItKeepsTheRefreshAliveWhileHidden(bool visible, bool auto, bool loadJob, bool expected) =>
        Assert.Equal(expected, RefreshPolicy.ShouldRun(visible, auto, loadJob));
}

public class DriverStoreCleanerTests
{
    private static DriverCopy Copy(DriverCopyStatus status, string? published = "oem12.inf", DateTime? date = null, bool dated = true) =>
        new(new DriverPackage("x.inf_amd64_abc", "x.inf", "amd64", published,
            dated ? date ?? new DateTime(2024, 8, 20) : null, new Version(1, 2, 0, 3), "Acme", 1000), status);

    [Fact]
    public void OnlyAnOlderCopyNoDeviceUsesIsOffered()
    {
        Assert.True(DriverStoreCleaner.CanDelete(Copy(DriverCopyStatus.OlderUnused)));
        Assert.False(DriverStoreCleaner.CanDelete(Copy(DriverCopyStatus.NewestUnused)));
        Assert.False(DriverStoreCleaner.CanDelete(Copy(DriverCopyStatus.NewestInUse)));
        Assert.False(DriverStoreCleaner.CanDelete(Copy(DriverCopyStatus.OlderInUse)));
    }

    [Fact]
    public void InboxDriversAndCopiesWithoutADateAreNeverOffered()
    {
        Assert.False(DriverStoreCleaner.CanDelete(Copy(DriverCopyStatus.OlderUnused, published: null)));
        Assert.False(DriverStoreCleaner.CanDelete(Copy(DriverCopyStatus.OlderUnused, dated: false)));
    }

    [Theory]
    [InlineData("oem0.inf", true)]
    [InlineData("OEM123.INF", true)]
    [InlineData("oem.inf", false)]
    [InlineData("oem12.inf /force", false)]
    [InlineData("oem12.inf\n", false)]
    [InlineData("..\\oem12.inf", false)]
    [InlineData("netwtw10.inf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnOemNameReachesPnputil(string? name, bool expected) =>
        Assert.Equal(expected, DriverStoreCleaner.IsPublishedName(name));

    [Fact]
    public void PnputilIsNeverGivenForce() =>
        Assert.Equal(new[] { "/delete-driver", "oem12.inf" }, DriverStoreCleaner.Arguments("oem12.inf"));

    [Fact]
    public void AnUnsuitableCopyIsRefusedWithoutRunningAnything()
    {
        var result = DriverStoreCleaner.Delete(Copy(DriverCopyStatus.NewestInUse));

        Assert.False(result.Ok);
        Assert.Equal("Ck_DrvNotRemovable", result.MessageKey);
    }

    [Fact]
    public void RowsRememberTheirDriverStoreFolder()
    {
        var row = CheckRows.ForDrivers(new[] { Copy(DriverCopyStatus.OlderUnused) }).Single();

        Assert.Equal("x.inf_amd64_abc", row.FolderName);
    }
}

[Collection("ViewOnly")]
public class DriverStoreCleanerViewOnlyTests : IDisposable
{
    public DriverStoreCleanerViewOnlyTests() => ViewOnlyMode.Enabled = false;
    public void Dispose() => ViewOnlyMode.Enabled = false;

    [Fact]
    public void ViewOnlyModeBlocksRemovingADriver()
    {
        ViewOnlyMode.Enabled = true;
        var copy = new DriverCopy(new DriverPackage("x.inf_amd64_abc", "x.inf", "amd64", "oem12.inf",
            new DateTime(2024, 8, 20), new Version(1, 0), "Acme", 1000), DriverCopyStatus.OlderUnused);

        var result = DriverStoreCleaner.Delete(copy);

        Assert.False(result.Ok);
        Assert.Equal("Msg_ViewOnlyBlocked", result.MessageKey);
    }
}
