using System.Diagnostics;
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
