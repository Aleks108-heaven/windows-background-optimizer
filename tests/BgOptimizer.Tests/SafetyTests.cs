using System.Diagnostics;
using System.IO;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The deny-lists and identity checks that stop the app acting on the wrong process.</summary>
public class SafetyTests
{
    [Theory]
    [InlineData("svchost")]
    [InlineData("SVCHOST")]
    [InlineData("lsass")]
    [InlineData("MsMpEng")]
    public void CoreWindowsProcessesAreProtected(string name) =>
        Assert.True(ProtectedItems.IsProcessProtected(name));

    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("msedge")]
    [InlineData("Taskmgr")]
    public void BrowsersAndShellAreInteractive(string name) =>
        Assert.True(ProtectedItems.IsInteractive(name));

    // An audio app idles near 0% CPU while playing, so it must never reach the idle-throttle/suspend path.
    [Theory]
    [InlineData("AudioTunePro")]
    [InlineData("AudioTunePro.exe")]
    [InlineData("SomeVendorSoundBooster")]
    [InlineData("Spotify")]
    public void AudioAppsAreProtectedFromThrottlingAndSuspend(string name)
    {
        Assert.True(ProtectedItems.IsInteractive(name));
        Assert.False(ProcessMonitor.SuspendProcess(Environment.ProcessId, name).Ok);
    }

    // WMI reports StartMode "Auto" but ChangeStartMode rejects it (code 21); only "Automatic" is accepted.
    [Theory]
    [InlineData("Auto", "Automatic")]
    [InlineData("auto", "Automatic")]
    [InlineData("Automatic", "Automatic")]
    [InlineData("Manual", "Manual")]
    [InlineData("Disabled", "Disabled")]
    public void ServiceStartModeIsMappedToWhatWmiAccepts(string input, string expected) =>
        Assert.Equal(expected, SvcManager.ToWmiStartMode(input));

    [Fact]
    public void AudioItemsAreClassifiedCoreNotVendorBloat()
    {
        Assert.Equal(ItemCategory.Core, ItemClassifier.ClassifyService("RtkAudioUniversalService", "Realtek Audio Universal Service"));
        Assert.Equal(ItemCategory.Core, ItemClassifier.ClassifyStartup("AudioTunePro", @"C:\Program Files\AudioTunePro\AudioTunePro.exe"));
        Assert.NotEqual(ItemCategory.Core, ItemClassifier.ClassifyService("AsusSurveyService", "ASUS Survey Service"));
    }

    [Fact]
    public void ShorterRefreshIntervalKeepsIdleThresholdsInTime()
    {
        var wo = new WorkloadOptimizer(tickSeconds: 5);
        var e = new ProcessEntry { Pid = 1, Name = "idleapp", MemoryBytes = 100L * 1024 * 1024, CpuKnown = true, CpuPercent = 0 };
        var list = new List<ProcessEntry> { e };
        var none = new HashSet<int>();
        wo.UpdateStreaks(list, none); wo.UpdateStreaks(list, none);
        Assert.Empty(wo.GetIdleCandidates(list, none)); // two quiet samples are never enough
        wo.UpdateStreaks(list, none);
        Assert.Single(wo.GetIdleCandidates(list, none));
    }

    [Theory]
    [InlineData("CSFalconService")]
    [InlineData("SentinelAgent.exe")]
    public void SecurityToolsAreRecognised(string name) =>
        Assert.True(ProtectedItems.IsSecurityTool(name));

    [Fact]
    public void OrdinaryAppIsNotProtectedInteractiveOrSecurityTool()
    {
        Assert.False(ProtectedItems.IsProcessProtected("notepad"));
        Assert.False(ProtectedItems.IsInteractive("notepad"));
        Assert.False(ProtectedItems.IsSecurityTool("notepad"));
    }

    [Fact]
    public void CriticalServicesAreProtected()
    {
        Assert.True(ProtectedItems.IsServiceProtected("WinDefend"));
        Assert.True(ProtectedItems.IsServiceProtected("rpcss"));
        Assert.False(ProtectedItems.IsServiceProtected("Fax"));
    }

    // ---- actions against a real child process of the test run (never anything else) ----

    private static Process SpawnPing() =>
        Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

    private static void Cleanup(Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
        p.Dispose();
    }

    [Fact]
    public void KillRefusesWhenNameDoesNotMatchLiveProcess()
    {
        var p = SpawnPing();
        try
        {
            var r = ProcessMonitor.Kill(p.Id, "notepad");
            Assert.False(r.Ok);
            Assert.Equal("Msg_ProcessChanged", r.MessageKey);
            Assert.False(p.HasExited);
        }
        finally { Cleanup(p); }
    }

    [Fact]
    public void KillRefusesProtectedName()
    {
        var p = SpawnPing();
        try
        {
            var r = ProcessMonitor.Kill(p.Id, "svchost");
            Assert.False(r.Ok);
            Assert.False(p.HasExited);
        }
        finally { Cleanup(p); }
    }

    [Fact]
    public void KillWithCorrectNameTerminatesProcess()
    {
        var p = SpawnPing();
        try
        {
            Assert.True(ProcessMonitor.Kill(p.Id, "ping").Ok);
            Assert.True(p.WaitForExit(5000));
        }
        finally { Cleanup(p); }
    }

    [Fact]
    public void KillOfExitedPidFailsWithoutThrowing()
    {
        var p = SpawnPing();
        var pid = p.Id;
        p.Kill(); p.WaitForExit(); p.Dispose();
        Assert.False(ProcessMonitor.Kill(pid, "ping").Ok);
    }

    [Fact]
    public void LoweringPriorityOfInteractiveAppNeedsOverride()
    {
        var blocked = ProcessMonitor.SetPriority(Environment.ProcessId, "chrome", ProcessPriorityClass.Idle);
        Assert.False(blocked.Ok);
        Assert.Equal("Msg_ProcessInteractive", blocked.MessageKey);
    }

    [Fact]
    public void PriorityChangeAppliesAndNameMatchIsCaseInsensitive()
    {
        var p = SpawnPing();
        try
        {
            Assert.True(ProcessMonitor.SetPriority(p.Id, "PING", ProcessPriorityClass.BelowNormal).Ok);
            p.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, p.PriorityClass);
        }
        finally { Cleanup(p); }
    }

    [Fact]
    public void SuspendRefusesSecurityToolAndProtectedNames()
    {
        Assert.False(ProcessMonitor.SuspendProcess(Environment.ProcessId, "CSFalconService").Ok);
        Assert.False(ProcessMonitor.SuspendProcess(Environment.ProcessId, "lsass").Ok);
    }

    [Fact]
    public void FrozenProcessLeftByACrashedRunIsResumedOnNextStart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bgopt-{Guid.NewGuid():N}.json");
        var p = SpawnPing();
        try
        {
            Assert.True(ProcessMonitor.SuspendProcess(p.Id, "ping").Ok);
            SuspendedStore.Save(new[] { (p.Id, "ping") }, path);
            Assert.True(File.Exists(path));

            // "Next start": nothing in memory, only the file.
            Assert.Equal(1, SuspendedStore.ResumeLeftovers(path));
            Assert.False(File.Exists(path));
        }
        finally { Cleanup(p); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LeftoverWithMismatchedIdentityIsNotResumed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bgopt-{Guid.NewGuid():N}.json");
        var p = SpawnPing();
        try
        {
            // Same PID but a different start time = a recycled PID, i.e. someone else's process.
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                new[] { new SuspendedStore.Item(p.Id, "ping", DateTime.UtcNow.AddDays(-30).Ticks) }));
            Assert.Equal(0, SuspendedStore.ResumeLeftovers(path));

            // ...and the same start time under a different name is rejected too.
            SuspendedStore.Save(new[] { (p.Id, "ping") }, path);
            var wrongName = File.ReadAllText(path).Replace("\"ping\"", "\"notping\"");
            File.WriteAllText(path, wrongName);
            Assert.Equal(0, SuspendedStore.ResumeLeftovers(path));
        }
        finally { Cleanup(p); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void EmptyFrozenListRemovesTheStoreFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bgopt-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "[]");
        SuspendedStore.Save(Array.Empty<(int, string)>(), path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SuspendThenResumeChildProcess()
    {
        var p = SpawnPing();
        try
        {
            Assert.True(ProcessMonitor.SuspendProcess(p.Id, "ping").Ok);
            Assert.True(ProcessMonitor.ResumeProcess(p.Id, "ping").Ok);
        }
        finally { Cleanup(p); }
    }

    [Fact]
    public void FirewallBlockRefusesProtectedAndMissingPrograms()
    {
        var sec = new SecurityManager();
        Assert.False(sec.SetAppBlocked(@"C:\Windows\System32\svchost.exe", true).Ok);
        Assert.False(sec.SetAppBlocked(@"C:\definitely\missing.exe", true).Ok);
        Assert.False(sec.SetAppBlocked(@"C:\Windows\System32\notepad.txt", true).Ok);
    }
}
