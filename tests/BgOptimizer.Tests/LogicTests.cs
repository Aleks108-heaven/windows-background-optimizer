using System.Reflection;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

public class ClassifierTests
{
    [Theory]
    [InlineData("Adobe Digital Editions")]
    [InlineData("Legitimate Updater")]
    public void GitInsideAnotherWordIsNotADevTool(string name) =>
        Assert.NotEqual(ItemCategory.DevTool, ItemClassifier.ClassifyStartup(name, name));

    [Theory]
    [InlineData("GitHubDesktop")]
    [InlineData("Docker Desktop")]
    [InlineData("Keep-WSLService-Enabled")]
    public void RealDevToolsAreStillRecognised(string name)
    {
        var c = ItemClassifier.ClassifyStartup(name, name);
        Assert.True(c is ItemCategory.DevTool or ItemCategory.Core, c.ToString());
    }

    [Fact]
    public void TelemetryAndVendorPatternsStillMatch()
    {
        Assert.Equal(ItemCategory.Telemetry, ItemClassifier.ClassifyStartup("CompatTelRunner", "CompatTelRunner"));
        Assert.Equal(ItemCategory.VendorDiag, ItemClassifier.ClassifyStartup("ASUS Survey", "ASUS Survey"));
    }

    [Fact]
    public void ProtectedNamesAreCore() =>
        Assert.Equal(ItemCategory.Core, ItemClassifier.ClassifyStartup("svchost", "svchost"));
}

/// <summary>Parsers that read output of Windows tools; they must not depend on the Windows display language.</summary>
public class ToolOutputParsingTests
{
    private const string LidTemplate = @"
Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
  GUID Alias: SCHEME_BALANCED
  Subgroup GUID: 4f971e89-eebd-4455-a8de-9e59040e7347  ({0})
    GUID Alias: SUB_BUTTONS
    Power Setting GUID: 5ca83367-6e45-459f-a27b-476b1d01c936  ({1})
      GUID Alias: LIDACTION
      Possible Setting Index: 000
      Possible Setting Friendly Name: x
      Possible Setting Index: 002
      Possible Setting Friendly Name: y
    {2}: 0x00000001
    {3}: 0x{4}
";

    private static string Lid(string ac, string dc, string acLabel, string dcLabel) =>
        string.Format(LidTemplate, "Power buttons and lid", "Lid close action", acLabel, dcLabel, dc);

    [Theory]
    [InlineData("Current AC Power Setting Index", "Current DC Power Setting Index")]
    [InlineData("Поточний індекс параметра живлення змінного струму", "Поточний індекс параметра живлення постійного струму")]
    [InlineData("Aktueller Wechselstromeinstellungsindex", "Aktueller Gleichstromeinstellungsindex")]
    public void HibernateIsDetectedRegardlessOfLanguage(string ac, string dc)
    {
        Assert.True(BatteryManager.ParseDcLidAction(Lid("1", "00000002", ac, dc)));
        Assert.False(BatteryManager.ParseDcLidAction(Lid("1", "00000001", ac, dc)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("The power scheme, subgroup or setting specified does not exist.")]
    public void UnparseableOutputIsUnknownNotFalse(string output) =>
        Assert.Null(BatteryManager.ParseDcLidAction(output));

    [Fact]
    public void FirewallRuleOwnedByThisAppIsParsed()
    {
        const string rule = @"v2.31|Action=Block|Active=TRUE|Dir=Out|App=C:\Tools\a.exe|Name=BgOptimizer Block Out: C:\Tools\a.exe|";
        Assert.Equal(@"C:\Tools\a.exe", SecurityManager.ParseBlockedAppPath(rule));
    }

    [Theory]
    [InlineData(@"v2.31|Action=Block|Active=TRUE|Dir=In|Name=BgOptimizer Block Out: C:\a.exe|")]
    [InlineData(@"v2.31|Action=Block|Active=TRUE|Dir=Out|Name=Some Other Rule|")]
    [InlineData("garbage")]
    public void OtherFirewallRulesAreIgnored(string rule) =>
        Assert.Null(SecurityManager.ParseBlockedAppPath(rule));

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", true)]
    [InlineData(@"\microsoft\Windows\x", true)]
    [InlineData(@"\ASUS\ASUS Survey", false)]
    [InlineData(@"\MicrosoftEdgeUpdateTaskMachineCore", false)]
    public void MicrosoftTaskFilter(string path, bool expected) =>
        Assert.Equal(expected, StartupManager.IsMicrosoftTaskPath(path));
}

public class WorkloadOptimizerTests
{
    private static ProcessEntry Entry(int pid, string name, bool cpuKnown, double cpu = 0, long mb = 100)
    {
        var e = new ProcessEntry { Pid = pid, Name = name, MemoryBytes = mb << 20, CpuPercent = cpu };
        e.CpuKnown = cpuKnown;
        return e;
    }

    private static readonly IReadOnlySet<int> NoForeground = new HashSet<int>();

    [Fact]
    public void CandidateOnlyAfterThreeIdleSamples()
    {
        var wo = new WorkloadOptimizer();
        var list = new List<ProcessEntry> { Entry(1, "idleapp", true) };
        wo.UpdateStreaks(list, NoForeground);
        wo.UpdateStreaks(list, NoForeground);
        Assert.Empty(wo.GetIdleCandidates(list, NoForeground));
        wo.UpdateStreaks(list, NoForeground);
        Assert.Single(wo.GetIdleCandidates(list, NoForeground));
    }

    [Fact]
    public void UnknownCpuBrowserBusyAndSmallProcessesAreNeverCandidates()
    {
        var wo = new WorkloadOptimizer();
        var list = new List<ProcessEntry>
        {
            Entry(1, "unknown", cpuKnown: false),
            Entry(2, "chrome", cpuKnown: true),
            Entry(3, "busy", cpuKnown: true, cpu: 40),
            Entry(4, "tiny", cpuKnown: true, mb: 5),
            Entry(5, "CSFalconService", cpuKnown: true),
        };
        for (var i = 0; i < 5; i++) wo.UpdateStreaks(list, NoForeground);
        Assert.Empty(wo.GetIdleCandidates(list, NoForeground));
    }

    [Fact]
    public void ForegroundProcessIsExempt()
    {
        var wo = new WorkloadOptimizer();
        var list = new List<ProcessEntry> { Entry(1, "idleapp", true) };
        var fg = new HashSet<int> { 1 };
        for (var i = 0; i < 5; i++) wo.UpdateStreaks(list, fg);
        Assert.Empty(wo.GetIdleCandidates(list, fg));
    }

    [Fact]
    public void ForegroundExpandsToSameNamedHelpers()
    {
        var list = new List<ProcessEntry> { Entry(1, "app", true), Entry(2, "app", true), Entry(3, "other", true) };
        var expanded = WorkloadOptimizer.ExpandForeground(list, new HashSet<int> { 1 });
        Assert.Contains(2, expanded);
        Assert.DoesNotContain(3, expanded);
    }

    [Fact]
    public void BusyBurstResetsTheStreak()
    {
        var wo = new WorkloadOptimizer();
        var e = Entry(1, "app", true);
        var list = new List<ProcessEntry> { e };
        wo.UpdateStreaks(list, NoForeground); wo.UpdateStreaks(list, NoForeground);
        e.CpuPercent = 50; wo.UpdateStreaks(list, NoForeground);
        e.CpuPercent = 0; wo.UpdateStreaks(list, NoForeground);
        Assert.Empty(wo.GetIdleCandidates(list, NoForeground));
    }
}

public class ProcessMonitorTests
{
    [Fact]
    public void RefreshReturnsUniqueSortedPidsWithSaneCpu()
    {
        var pm = new ProcessMonitor();
        pm.Refresh();
        Thread.Sleep(300);
        var list = pm.Refresh();
        Assert.NotEmpty(list);
        Assert.Equal(list.Count, list.Select(p => p.Pid).Distinct().Count());
        Assert.True(list.Zip(list.Skip(1), (a, b) => a.Pid < b.Pid).All(x => x));
        Assert.All(list, p => Assert.InRange(p.CpuPercent, 0, 100));
        Assert.Contains(list, p => p.Pid == Environment.ProcessId);
    }

    [Fact]
    public void ProcessEntrySeverityHogRulesMatch()
    {
        var hog = new ProcessEntry { Name = "x", MemoryBytes = 500L << 20 };
        var protectedHog = new ProcessEntry { Name = "svchost", IsProtected = true, MemoryBytes = 500L << 20 };
        Assert.True(hog.IsHog);
        Assert.False(protectedHog.IsHog);
    }
}
