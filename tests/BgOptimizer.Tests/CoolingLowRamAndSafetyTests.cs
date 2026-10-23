using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Microsoft.Win32;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The PDH wrapper that replaced the WMI load queries: real counters on this machine, plus the failure paths.</summary>
public class PdhQueryTests
{
    [Fact]
    public void AMultiInstanceCounterListsEveryInstanceWithUsableValues()
    {
        using var query = PdhQuery.TryOpen();
        Assert.NotNull(query);
        var counter = query!.AddCounter(@"\Processor Information(*)\% Processor Time");
        Assert.NotNull(counter);

        query.Collect();
        Thread.Sleep(300); // a rate needs two samples
        Assert.True(query.Collect());

        var values = counter!.Values();
        Assert.True(values.Count >= 2, "expected at least one core plus the total");
        Assert.Contains(values, v => v.Instance.EndsWith("_Total", StringComparison.OrdinalIgnoreCase));
        Assert.All(values, v =>
        {
            Assert.False(string.IsNullOrEmpty(v.Instance));
            Assert.True(v.Value >= 0);
        });
    }

    [Fact]
    public void ASingleInstanceCounterGivesAValue()
    {
        using var query = PdhQuery.TryOpen();
        var counter = query!.AddCounter(@"\Processor Information(_Total)\% Processor Time");
        Assert.NotNull(counter);
        Assert.Null(counter!.Value()); // nothing collected yet

        query.Collect();
        Thread.Sleep(300);
        query.Collect();

        Assert.InRange(counter.Value()!.Value, 0, 100.5);
    }

    [Fact]
    public void ACounterThatDoesNotExistIsRefusedNotThrown()
    {
        using var query = PdhQuery.TryOpen();
        Assert.Null(query!.AddCounter(@"\No Such Object(*)\No Such Counter"));
        Assert.Null(query.AddCounter("not a counter path at all"));
        Assert.Null(query.AddCounter(""));
    }

    [Fact]
    public void ADisposedQueryIsInertAndDisposingTwiceIsFine()
    {
        var query = PdhQuery.TryOpen()!;
        var counter = query.AddCounter(@"\Processor Information(*)\% Processor Time")!;
        query.Collect();

        query.Dispose();
        query.Dispose();

        Assert.False(query.Collect());
        Assert.Null(counter.Value());
        Assert.Empty(counter.Values());
        Assert.Null(query.AddCounter(@"\Processor Information(_Total)\% Processor Time"));
    }

    [Fact]
    public void ASampleCostsMillisecondsNotTheHalfSecondTheWmiQueriesTook()
    {
        // The WMI "formatted data" GPU query measured about 500 ms wall per call on a fast CPU. The limit here is
        // generous (loaded CI machines), but a regression to that approach would be far over it.
        using var sampler = new LoadSampler();
        Thread.Sleep(200);
        sampler.Sample(); // warm-up
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) { Thread.Sleep(50); sampler.Sample(); }
        var averageMs = (watch.Elapsed.TotalMilliseconds - 5 * 50) / 5;

        Assert.True(averageMs < 150, $"a load sample took {averageMs:N0} ms on average");
    }
}

public class LoadSamplerPdhTests
{
    [Fact]
    public void SamplesStayInRangeAfterTheBaselineHasAged()
    {
        using var sampler = new LoadSampler();
        Thread.Sleep(300);

        var load = sampler.Sample();

        if (load.DiskPercent is { } disk) Assert.InRange(disk, 0, 100);
        if (load.GpuPercent is { } gpu) Assert.InRange(gpu, 0, 100);
    }

    [Fact]
    public void ADisposedSamplerReportsNothingInsteadOfThrowing()
    {
        var sampler = new LoadSampler();
        sampler.Dispose();
        sampler.Dispose();

        Assert.Equal(default, sampler.Sample());
    }
}

public class ThermalSamplerTests
{
    private static (string, double)[] Zones(params double[] values) =>
        values.Select((v, i) => ($"\\_TZ.ZONE{i}", v)).ToArray();

    private static readonly (string, double)[] None = Array.Empty<(string, double)>();

    [Fact]
    public void TheHottestPlausibleZoneWinsFromTheHighPrecisionCounter()
    {
        // 3182 and 3000 tenths of a kelvin: 45.05 and 26.85 degrees.
        var r = ThermalSampler.Combine(Zones(3182, 3000), None, None, null, null);

        Assert.Equal(45.05, r.TemperatureC!.Value, precision: 2);
    }

    [Fact]
    public void AZoneThatIsNotReallyThereIsIgnoredAndThePlainCounterIsTheFallback()
    {
        // 0 K is a sensor that reports nothing; the plain (kelvin) counter takes over.
        var r = ThermalSampler.Combine(Zones(0), Zones(318), None, null, null);

        Assert.Equal(44.85, r.TemperatureC!.Value, precision: 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99999)]   // a garbage value, not 8000 degrees
    [InlineData(-5)]
    public void ImplausibleTemperaturesGiveNoTemperature(double raw) =>
        Assert.Null(ThermalSampler.Combine(Zones(raw), Zones(raw), None, null, null).TemperatureC);

    [Fact]
    public void TheStrictestPassiveLimitCountsAndZeroOrOutOfRangeIsIgnored()
    {
        var r = ThermalSampler.Combine(None, None, Zones(100, 70, 0, 150), null, null);

        Assert.Equal(70, r.PassiveLimitPercent);
        Assert.True(r.Throttled);
        Assert.Null(ThermalSampler.Combine(None, None, Zones(0, 150), null, null).PassiveLimitPercent);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(99, true)]
    [InlineData(40, true)]
    public void ThrottledMeansWindowsIsHoldingTheCpuBack(int limit, bool expected) =>
        Assert.Equal(expected, new ThermalReading(null, limit, null, null).Throttled);

    [Fact]
    public void NotReportedIsNeverThrottled()
    {
        Assert.False(default(ThermalReading).Throttled);
        Assert.False(default(ThermalReading).HasAnything);
    }

    [Fact]
    public void SpeedAndFrequencyAreRoundedAndNonsenseIsDropped()
    {
        var r = ThermalSampler.Combine(None, None, None, 85.4, 1999.6);
        Assert.Equal(85.0, r.CpuSpeedPercent);
        Assert.Equal(2000.0, r.CpuMhz);
        Assert.True(r.HasAnything);

        var bad = ThermalSampler.Combine(None, None, None, -1, 0);
        Assert.Null(bad.CpuSpeedPercent);
        Assert.Null(bad.CpuMhz);
    }

    [Fact]
    public void SamplingTheRealMachineNeverThrowsAndStaysPlausible()
    {
        using var sampler = new ThermalSampler();
        sampler.Sample();
        Thread.Sleep(250);

        var r = sampler.Sample();

        if (r.TemperatureC is { } t) Assert.InRange(t, -20, 150);
        if (r.PassiveLimitPercent is { } l) Assert.InRange(l, 1, 100);
    }
}

[Collection("ViewOnly")]
public class CoolingManagerTests : IDisposable
{
    public CoolingManagerTests() => ViewOnlyMode.Enabled = false;
    public void Dispose() => ViewOnlyMode.Enabled = false;

    // The real layout of "powercfg /q SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX" on a Ukrainian Windows 11.
    private const string RealListing = """
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Збалансований)
          GUID Alias: SCHEME_BALANCED
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Керування живленням процесора)
            GUID Alias: SUB_PROCESSOR
            Power Setting GUID: bc5038f7-23e0-4960-96da-33abaf5935ec  (Максимальна швидкодія процесора)
              GUID Alias: PROCTHROTTLEMAX
              Minimum Possible Setting: 0x00000000
              Maximum Possible Setting: 0x00000064
              Possible Settings increment: 0x00000001
              Possible Settings units: %
            Current AC Power Setting Index: 0x00000064
            Current DC Power Setting Index: 0x00000064
        """;

    [Fact]
    public void TheLastTwoValuesAreTheAcAndDcLimitsWhateverTheLanguage() =>
        Assert.Equal((100, 100), CoolingManager.ParseLimit(RealListing));

    [Fact]
    public void DifferentPluggedInAndBatteryLimitsAreReadApart()
    {
        var listing = RealListing
            .Replace("Current AC Power Setting Index: 0x00000064", "Current AC Power Setting Index: 0x00000050")
            .Replace("Current DC Power Setting Index: 0x00000064", "Current DC Power Setting Index: 0x0000004B");

        Assert.Equal((80, 75), CoolingManager.ParseLimit(listing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("The power scheme, subgroup or setting specified does not exist.")]
    [InlineData("Current AC Power Setting Index: 0x00000050\nCurrent DC Power Setting Index: 0x0000004B")] // no min/max/increment: not this kind of setting
    [InlineData("0x00000000 0x00000064 0x00000001 0x00000096 0x00000064")]                              // 150 % is not a percentage
    public void ListingsThatAreNotAPercentageSettingAreRefused(string output) =>
        Assert.Null(CoolingManager.ParseLimit(output));

    [Theory]
    [InlineData(49)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-10)]
    public void ALimitOutsideTheSaneRangeIsRefusedBeforeAnythingIsChanged(int percent)
    {
        var result = new CoolingManager().SetLimit(percent);

        Assert.False(result.Ok);
        Assert.Equal("Msg_SecurityFailed", result.MessageKey);
    }

    [Fact]
    public void ViewOnlyModeStopsTheChange()
    {
        ViewOnlyMode.Enabled = true;

        var result = new CoolingManager().SetLimit(80);

        Assert.False(result.Ok);
        Assert.Equal("Msg_ViewOnlyBlocked", result.MessageKey);
    }

    [Fact]
    public void ThePresetsStartAtNoLimitAndNeverGoBelowTheFloor()
    {
        Assert.Equal(CoolingManager.NoLimit, CoolingManager.Presets[0]);
        Assert.Equal(CoolingManager.Presets.OrderByDescending(p => p), CoolingManager.Presets);
        Assert.All(CoolingManager.Presets, p => Assert.InRange(p, CoolingManager.MinimumLimit, CoolingManager.NoLimit));
    }

    [Fact]
    public void ReadingTheRealPlanNeverThrows()
    {
        var limit = new CoolingManager().ReadLimit();
        if (limit is { } l) { Assert.InRange(l.Ac, 0, 100); Assert.InRange(l.Dc, 0, 100); }
    }
}

public class RamPolicyTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(2.0, 4.0, 4.0 / 3)]    // half of a 4 GB laptop is capped to a third
    [InlineData(2.0, 8.0, 2.0)]        // a quarter of 8 GB is fine as typed
    [InlineData(2.0, 16.0, 2.0)]
    [InlineData(8.0, 16.0, 16.0 / 3)]  // an explicit but excessive value is capped too
    public void TheThresholdNeverExceedsAThirdOfInstalledRam(double wantedGb, double installedGb, double expectedGb)
    {
        var effective = RamPolicy.EffectiveThresholdBytes((long)(wantedGb * Gb), (long)(installedGb * Gb));

        Assert.Equal(expectedGb, (double)effective / Gb, precision: 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnUnknownTotalMeansTheUsersValueStands(long installed) =>
        Assert.Equal(2 * Gb, RamPolicy.EffectiveThresholdBytes(2 * Gb, installed));
}

/// <summary>The trim pause that stops trim, "recovered", release, re-fault, trim again.</summary>
public class TrimCooldownTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AProgramIsTrimmedOncePerCooldownNotOnEveryEpisode()
    {
        var wo = new WorkloadOptimizer();

        Assert.True(wo.ClaimTrim(10, T0));
        Assert.False(wo.ClaimTrim(10, T0.AddMinutes(2)));
        Assert.False(wo.ClaimTrim(10, T0.AddMinutes(9).AddSeconds(59)));
        Assert.True(wo.ClaimTrim(10, T0.AddMinutes(10)));
    }

    [Fact]
    public void EachProgramHasItsOwnCooldown()
    {
        var wo = new WorkloadOptimizer();

        Assert.True(wo.ClaimTrim(1, T0));
        Assert.True(wo.ClaimTrim(2, T0));
        Assert.False(wo.ClaimTrim(1, T0.AddSeconds(30)));
    }

    [Fact]
    public void AnExitedProgramIsForgottenSoARecycledPidStartsFresh()
    {
        var wo = new WorkloadOptimizer();
        var none = new HashSet<int>();
        Assert.True(wo.ClaimTrim(7, T0));

        wo.UpdateStreaks(Array.Empty<ProcessEntry>(), none); // pid 7 is gone

        Assert.True(wo.ClaimTrim(7, T0.AddSeconds(5)));
    }
}

/// <summary>The new "never touch" groups: work agents, Windows/driver helpers, and the user's own list.</summary>
[Collection("ViewOnly")] // these expect the guards' own refusals, so view-only mode must be off
public class NeverTouchTests : IDisposable
{
    public NeverTouchTests() { ViewOnlyMode.Enabled = false; ProtectedItems.SetUserNames(""); }
    public void Dispose() { ViewOnlyMode.Enabled = false; ProtectedItems.SetUserNames(""); }

    [Theory]
    [InlineData("AnyDesk")]
    [InlineData("OneDrive.exe")]
    [InlineData("slack")]
    [InlineData("tailscaled")]
    [InlineData("1Password")]
    [InlineData("vmware-vmx")]
    [InlineData("Docker Desktop")]
    [InlineData("OUTLOOK")]
    public void WorkAgentsThatIdleButMustStayReachableAreInteractive(string name)
    {
        Assert.True(ProtectedItems.IsInteractive(name));
        Assert.Equal("Msg_ProcessInteractive", ProcessMonitor.SetPriority(Environment.ProcessId, name, ProcessPriorityClass.BelowNormal).MessageKey);
        Assert.False(ProcessMonitor.SuspendProcess(Environment.ProcessId, name).Ok);
    }

    [Theory]
    [InlineData("wlanext")]
    [InlineData("dasHost.exe")]
    [InlineData("AMDPMFSERVICE")]
    [InlineData("NgcIso")]
    [InlineData("esif_uf")]
    public void WindowsAndDriverHelpersCannotBeSlowedTrimmedOrFrozenByAnyRoute(string name)
    {
        Assert.Equal("Msg_ProcessPlatform", ProtectedItems.NeverTouchMessageKey(name));
        Assert.Equal("Msg_ProcessPlatform", ProcessMonitor.TrimMemory(Environment.ProcessId, name).MessageKey);
        Assert.Equal("Msg_ProcessPlatform", ProcessMonitor.SuspendProcess(Environment.ProcessId, name).MessageKey);
        Assert.Equal("Msg_ProcessPlatform", ProcessMonitor.SetPriority(Environment.ProcessId, name, ProcessPriorityClass.BelowNormal, allowInteractiveThrottle: true).MessageKey);
        Assert.Equal("Msg_ProcessPlatform", PriorityPersistence.SetPersistent(name, ProcessPriorityClass.BelowNormal).MessageKey);
        Assert.Equal("Msg_ProcessPlatform", PriorityPersistence.SetPersistent(name, ProcessPriorityClass.Idle).MessageKey);
    }

    [Fact]
    public void TheLocalAiExplanationStillComesFirstAndOrdinaryAppsAreFree()
    {
        Assert.Equal("Msg_ProcessAiTool", ProtectedItems.NeverTouchMessageKey("ollama"));
        Assert.Null(ProtectedItems.NeverTouchMessageKey("notepad"));
        Assert.False(ProtectedItems.IsNeverTouch("AsusOptimization")); // vendor bloat is exactly what the app may slow
    }

    [Fact]
    public void RaisingAHelpersPriorityIsNotBlockedByTheSlowDownGuard()
    {
        // Only slowing is refused; this still reaches the process lookup and fails there (wrong name for this PID).
        var r = ProcessMonitor.SetPriority(Environment.ProcessId, "wlanext", ProcessPriorityClass.Normal);
        Assert.NotEqual("Msg_ProcessPlatform", r.MessageKey);
    }

    [Fact]
    public void ParsingTheUserListHandlesCommasSemicolonsLinesSpacesAndExe()
    {
        var names = ProtectedItems.ParseUserNames("AnyDesk, MyVpn.exe ;LM Studio\r\n  anydesk \n,, ;");

        Assert.Equal(new[] { "AnyDesk", "MyVpn", "LM Studio" }, names);
    }

    [Fact]
    public void ThingsThatCannotBeProcessNamesAreSkipped()
    {
        var tooLong = new string('a', ProtectedItems.MaxUserNameLength + 1);
        var justRight = new string('b', ProtectedItems.MaxUserNameLength);

        var names = ProtectedItems.ParseUserNames($@"C:\x\a.exe, bad*name, ok, a|b, x{'\u0001'}y, ..\evil, {tooLong}, {justRight}, what?, <tag>");

        Assert.Equal(new[] { "ok", justRight }, names);
    }

    [Fact]
    public void OnlyTheFirstHundredNamesCountAndNothingBlowsUpOnHugeInput()
    {
        var many = string.Join(",", Enumerable.Range(0, 500).Select(i => $"app{i}"));

        Assert.Equal(ProtectedItems.MaxUserNames, ProtectedItems.ParseUserNames(many).Count);
        Assert.Empty(ProtectedItems.ParseUserNames(null));
        Assert.Empty(ProtectedItems.ParseUserNames("   "));
        Assert.Equal(ProtectedItems.MaxUserNames, ProtectedItems.ParseUserNames(new string(',', 2_000_000) + many).Count);
    }

    [Fact]
    public void TheUsersListBlocksEveryRouteAndTakesEffectImmediatelyAndCanBeEmptied()
    {
        const string name = "BgOptTestCorpAgent";
        Assert.False(ProtectedItems.IsUserProtected(name));

        ProtectedItems.SetUserNames($"something, {name}.exe");

        Assert.True(ProtectedItems.IsUserProtected(name.ToUpperInvariant()));
        Assert.Equal("Msg_ProcessUserProtected", ProtectedItems.NeverTouchMessageKey(name));
        Assert.Equal("Msg_ProcessUserProtected", ProcessMonitor.TrimMemory(Environment.ProcessId, name).MessageKey);
        Assert.Equal("Msg_ProcessUserProtected", ProcessMonitor.SuspendProcess(Environment.ProcessId, name).MessageKey);
        Assert.Equal("Msg_ProcessUserProtected", ProcessMonitor.SetPriority(Environment.ProcessId, name, ProcessPriorityClass.Idle, allowInteractiveThrottle: true).MessageKey);
        Assert.Equal("Msg_ProcessUserProtected", PriorityPersistence.SetPersistent(name, ProcessPriorityClass.BelowNormal).MessageKey);

        ProtectedItems.SetUserNames("");
        Assert.False(ProtectedItems.IsUserProtected(name));
    }

    [Fact]
    public void NoneOfTheNewGroupsIsEverAnIdleCandidateHoweverLongItIdles()
    {
        ProtectedItems.SetUserNames("BgOptTestCorpAgent");
        var wo = new WorkloadOptimizer(tickSeconds: 5);
        ProcessEntry Idle(int pid, string name) => new() { Pid = pid, Name = name, MemoryBytes = 500L << 20, CpuKnown = true, CpuPercent = 0 };
        var list = new List<ProcessEntry> { Idle(1, "AnyDesk"), Idle(2, "wlanext"), Idle(3, "BgOptTestCorpAgent"), Idle(4, "idleapp") };
        var none = new HashSet<int>();

        for (var i = 0; i < 80; i++) wo.UpdateStreaks(list, none);

        Assert.Equal(new[] { 4 }, wo.GetIdleCandidates(list, none).Select(p => p.Pid));
        Assert.Equal(new[] { 4 }, wo.GetLongIdleCandidates(list, none).Select(p => p.Pid));
    }

    [Fact]
    public void AHandEditedSettingsFileCanOnlyAddUsableNames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bgopt-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "NeverTouch": "C:\\evil\\x.exe, ok1,  ok2.exe, bad*name, ok1" }""");
            Assert.Equal("ok1, ok2", AppSettings.Load(path).NeverTouch);

            File.WriteAllText(path, """{ "NeverTouch": 12345 }""");
            Assert.Equal("", AppSettings.Load(path).NeverTouch); // wrong type: defaults, not a crash
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheListSurvivesSavingAndLoading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bgopt-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { NeverTouch = "AnyDesk, LM Studio" }.Save(path);
            Assert.Equal("AnyDesk, LM Studio", AppSettings.Load(path).NeverTouch);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}

/// <summary>Leftover permanent slow-down rules: only low ones on protected programs are removed, each is logged, nothing is claimed that did not happen.</summary>
[Collection("ViewOnly")]
public class UnsafeRuleCleanupTests : IDisposable
{
    public UnsafeRuleCleanupTests() => ViewOnlyMode.Enabled = false;
    public void Dispose() => ViewOnlyMode.Enabled = false;

    [Theory]
    [InlineData(1, true)]    // Idle
    [InlineData(5, true)]    // Below normal
    [InlineData(2, false)]   // Normal
    [InlineData(3, false)]   // High
    [InlineData(4, false)]   // Real-time
    [InlineData(6, false)]   // Above normal
    public void OnlyIdleAndBelowNormalAreSlowDownRules(int value, bool low) =>
        Assert.Equal(low, PriorityPersistence.IsLowRule(value));

    [Fact]
    public void TheListCoversBrowsersWorkAgentsAndHelpersButNotAudioOrAiToolsOrTheUsersOwn()
    {
        var names = PriorityPersistence.UnsafeRuleNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("msedge", names);
        Assert.Contains("AnyDesk", names);
        Assert.Contains("wlanext", names);
        Assert.Contains("amdpmfservice", names);
        Assert.DoesNotContain("python", names);   // somebody's deliberate rule, not one this app could have written
        Assert.DoesNotContain("Spotify", names);
        Assert.DoesNotContain("SearchIndexer", names); // slowing the indexer is the documented use of the feature
    }

    [Fact]
    public void LowRulesAreRemovedAndLoggedWithTheirValueWhileNormalAndHigherAreKept()
    {
        var store = new Dictionary<string, int> { ["a"] = 5, ["b"] = 1, ["c"] = 2, ["d"] = 3, ["e"] = 6 };
        var log = new List<string>();

        var cleared = PriorityPersistence.ClearUnsafeRules(store.Keys.ToList(),
            n => store.TryGetValue(n, out var v) ? v : null,
            n => { store.Remove(n); return new ActionResult(true, "Msg_PriorityPersistCleared", n); },
            log.Add);

        Assert.Equal(new[] { ("a", "BelowNormal"), ("b", "Idle") }, cleared);
        Assert.Equal(new[] { "c", "d", "e" }, store.Keys.OrderBy(k => k));
        Assert.Equal(2, log.Count);
        Assert.All(log, line => Assert.StartsWith("IFEO_CLEAR", line));
        Assert.Contains(log, l => l.Contains("name=\"a\"") && l.Contains("was=BelowNormal"));
    }

    [Fact]
    public void ARuleThatCouldNotBeRemovedIsNotClaimedAsRemoved()
    {
        var log = new List<string>();

        var cleared = PriorityPersistence.ClearUnsafeRules(new[] { "a" }, _ => 5,
            n => new ActionResult(false, "Msg_ViewOnlyBlocked", n), log.Add);

        Assert.Empty(cleared);
        Assert.Empty(log);
    }

    [Fact]
    public void NamesWithNoRuleAreSkippedAndTheRealRegistryIsReadWithoutThrowing()
    {
        Assert.Null(PriorityPersistence.ReadPersisted("bgopt-no-such-program-xyz"));
        Assert.False(PriorityPersistence.IsPersisted("bgopt-no-such-program-xyz"));
        Assert.Empty(PriorityPersistence.ClearUnsafeRules(new[] { "bgopt-no-such-program-xyz" },
            PriorityPersistence.ReadPersisted, PriorityPersistence.ClearPersistent, _ => { }));
    }

    [Fact]
    public void InViewOnlyModeTheRealClearRefusesSoNothingIsRemoved()
    {
        ViewOnlyMode.Enabled = true;

        Assert.False(PriorityPersistence.ClearPersistent("bgopt-no-such-program-xyz").Ok);
    }
}

/// <summary>The fix for the "Which app do you want to use to open this .disabled file?" prompt left by versions up to 1.2.x.</summary>
[Collection("ViewOnly")]
public class LegacyDisabledRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bgopt-legacy-{Guid.NewGuid():N}");
    private readonly string _startup;
    private readonly string _aside;
    private const string TestKeyBase = @"Software\BgOptimizerTests";
    private readonly string _approvedPath = $@"{TestKeyBase}\{Guid.NewGuid():N}";
    private static readonly RegistryKey Hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    public LegacyDisabledRepairTests()
    {
        ViewOnlyMode.Enabled = false;
        _startup = Path.Combine(_root, "Startup");
        _aside = Path.Combine(_root, "aside");
        Directory.CreateDirectory(_startup);
    }

    public void Dispose()
    {
        ViewOnlyMode.Enabled = false;
        try { Directory.Delete(_root, recursive: true); } catch { /* a junction test may leave nothing, or the folder is gone */ }
        Hkcu.DeleteSubKeyTree(_approvedPath, throwOnMissingSubKey: false);
        using var parent = Hkcu.OpenSubKey(TestKeyBase, writable: true);
        if (parent is { SubKeyCount: 0 }) { parent.Close(); Hkcu.DeleteSubKey(TestKeyBase, throwOnMissingSubKey: false); }
    }

    // Outcomes go to a list, never to the real action log, which is the user's record for diagnosing freezes.
    private readonly List<string> _log = new();

    private StartupManager.LegacyRepair Repair(string? folder = null, string? root = null) =>
        StartupManager.RepairLegacyFolder(folder ?? _startup, Hkcu, _approvedPath, root ?? _root, _aside, _log.Add);

    private string Write(string name, string content = "x", FileAttributes? attributes = null)
    {
        var path = Path.Combine(_startup, name);
        File.WriteAllText(path, content);
        if (attributes is { } a) File.SetAttributes(path, a);
        return path;
    }

    [Fact]
    public void ACopyRenamedByAnOlderVersionIsMovedBackAndStaysSwitchedOff()
    {
        Write("old.lnk.disabled");

        var result = Repair();

        Assert.Equal(new StartupManager.LegacyRepair(1, 0, 0), result);
        Assert.True(File.Exists(Path.Combine(_startup, "old.lnk")));
        Assert.False(File.Exists(Path.Combine(_startup, "old.lnk.disabled")));   // so Windows has nothing it cannot open
        using var key = Hkcu.OpenSubKey(_approvedPath)!;
        Assert.False(StartupApprovedValue.IsEnabled(key.GetValue("old.lnk")));    // and it is still off, as the user left it
        Assert.Equal("STARTUP_REPAIR_LEGACY file=\"old.lnk.disabled\" result=moved-back-and-kept-off", Assert.Single(_log));
    }

    [Fact]
    public void TheRepairedItemIsListedAsOffByTheNormalReader()
    {
        Write("shortcut.lnk.disabled");
        Repair();

        // Read back the way the Startup tab does, against the same approval key.
        var item = Assert.Single(StartupManager.ReadFolder(_startup, StartupSource.StartupFolderUser, Hkcu, _approvedPath));

        Assert.Equal("shortcut", item.Name);
        Assert.False(item.Enabled);
        Assert.EndsWith("shortcut.lnk", item.Command);
    }

    [Fact]
    public void ARealCopyThatAlreadyExistsIsNeverOverwrittenTheStaleOneIsSetAside()
    {
        Write("dup.lnk", "the real one");
        Write("dup.lnk.disabled", "the stale one");

        var result = Repair();

        Assert.Equal(new StartupManager.LegacyRepair(0, 1, 0), result);
        Assert.Equal("the real one", File.ReadAllText(Path.Combine(_startup, "dup.lnk")));
        Assert.False(File.Exists(Path.Combine(_startup, "dup.lnk.disabled")));
        var kept = Assert.Single(Directory.GetFiles(_aside));
        Assert.Equal("the stale one", File.ReadAllText(kept)); // moved, not deleted
    }

    [Theory]
    [InlineData("notes.txt.disabled")]
    [InlineData("plain.disabled")]
    [InlineData("library.dll.disabled")]
    [InlineData("data.json.disabled")]
    public void OnlyFilesThatRunByThemselvesFromStartupAreTouched(string name)
    {
        var path = Write(name);

        Assert.Equal(default, Repair());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void HiddenOrSystemFilesAreLeftAlone()
    {
        var hidden = Write("h.lnk.disabled", attributes: FileAttributes.Hidden);
        var system = Write("s.lnk.disabled", attributes: FileAttributes.System);

        Assert.Equal(default, Repair());
        Assert.True(File.Exists(hidden));
        Assert.True(File.Exists(system));
    }

    [Fact]
    public void ARepairedFolderIsQuietTheSecondTime()
    {
        Write("a.lnk.disabled");
        Write("b.cmd.disabled");

        Assert.Equal(2, Repair().Repaired);
        Assert.Equal(default, Repair());
    }

    [Fact]
    public void AFolderOutsideTheRequiredRootIsNotTouched()
    {
        var path = Write("old.lnk.disabled");
        var elsewhere = Path.Combine(_root, "somewhere-else");
        Directory.CreateDirectory(elsewhere);

        Assert.Equal(default, Repair(root: elsewhere));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AJunctionStandingInForTheStartupFolderIsNotFollowed()
    {
        var path = Write("old.lnk.disabled");
        var link = Path.Combine(_root, "link");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", link, _startup } }))
            mk!.WaitForExit(10_000);
        Assert.True(Directory.Exists(link), "the test could not create a junction");

        Assert.Equal(default, Repair(folder: link));
        Assert.True(File.Exists(path));
        Assert.False(StartupManager.IsPlainFolderUnder(link, _root));
        Directory.Delete(link); // removes the link only, never its target
    }

    [Fact]
    public void ASiblingFolderWithTheSamePrefixIsNotInsideTheRoot()
    {
        var root = Path.Combine(_root, "base");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "base-evil"));

        Assert.True(StartupManager.IsPlainFolderUnder(Path.Combine(root, "sub"), root));
        Assert.False(StartupManager.IsPlainFolderUnder(Path.Combine(_root, "base-evil"), root));
        Assert.False(StartupManager.IsPlainFolderUnder(Path.Combine(root, "missing"), root));
        Assert.False(StartupManager.IsPlainFolderUnder("", root));
    }

    [Fact]
    public void ViewOnlyModeRepairsNothingAtAll()
    {
        ViewOnlyMode.Enabled = true;

        Assert.Equal(default, StartupManager.RepairLegacyDisabled()); // returns before it looks at any folder
    }
}

/// <summary>"svchost" alone reads as one hog; the busiest-programs line says how many processes a name stands for.</summary>
public class BusiestProgramsLabelTests
{
    [Theory]
    [InlineData("svchost", 84, "svchost ×84")]
    [InlineData("explorer", 1, "explorer")]
    [InlineData("chrome", 2, "chrome ×2")]
    public void SeveralProcessesOfOneNameShowTheirCount(string name, int count, string expected) =>
        Assert.Equal(expected, MainWindow.Labelled(name, count));
}

/// <summary>Every string this change added exists in every language, with the same placeholders, and none is left in English.</summary>
public class CoolingAndSafetyLocalizationTests
{
    private static readonly string[] NewKeys =
    {
        "Msg_LegacyStartupFixed", "Msg_ThresholdCapped", "Msg_ProcessPlatform", "Msg_ProcessUserProtected", "Msg_RulesCleared",
        "Set_NeverTitle", "Set_NeverHint", "Set_NeverName", "Set_CoolTitle", "Set_CoolHint", "Set_CoolTemp", "Set_CoolTempNone",
        "Set_CoolSpeed", "Set_CoolOk", "Set_CoolThrottled", "Set_CoolLimit", "Set_CoolNone", "Set_CoolApply", "Set_CoolCurrent",
        "Set_CoolUnavailable", "Dlg_CoolLimit", "Dlg_CoolNone", "Msg_CoolSet",
    };

    private static Dictionary<string, Dictionary<string, string>> Table() =>
        (Dictionary<string, Dictionary<string, string>>)typeof(Loc)
            .GetField("Strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string[] Placeholders(string s) =>
        Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v).ToArray();

    [Fact]
    public void EveryLanguageHasEveryNewString()
    {
        var table = Table();
        foreach (var lang in Loc.Languages)
            foreach (var key in NewKeys)
                Assert.True(table[lang.Code].ContainsKey(key), $"{lang.Code} is missing {key}");
    }

    [Fact]
    public void PlaceholdersMatchEnglishAndNothingStaysInEnglish()
    {
        var table = Table();
        foreach (var key in NewKeys)
            foreach (var lang in Loc.Languages.Where(l => l.Code != "en"))
            {
                Assert.True(Placeholders(table["en"][key]).SequenceEqual(Placeholders(table[lang.Code][key])), $"{lang.Code}/{key}: placeholders differ from English");
                Assert.NotEqual(table["en"][key], table[lang.Code][key]);
            }
    }

    [Fact]
    public void EveryMessageFormatsInEveryLanguageWithItsOwnPlaceholders()
    {
        // Straight from the table, not through Loc.Instance: switching the app-wide language here would race with
        // other tests that expect English while they run in parallel.
        var table = Table();
        foreach (var lang in Loc.Languages)
            foreach (var key in NewKeys)
            {
                var args = Enumerable.Range(0, Placeholders(table["en"][key]).Length).Select(i => (object)$"v{i}").ToArray();
                var text = string.Format(table[lang.Code][key], args); // throws on a stray brace or a missing argument
                Assert.DoesNotContain("{", text);
            }
    }
}
