using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

// View-only mode is process-wide state. Tests that switch it on must not run alongside tests that expect
// changes to work, so this collection runs by itself.
[CollectionDefinition("ViewOnly", DisableParallelization = true)]
public class ViewOnlyCollection { }

[Collection("ViewOnly")]
public class ViewOnlyModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-viewonly-{Guid.NewGuid():N}");

    public ViewOnlyModeTests() => ViewOnlyMode.Enabled = false;

    public void Dispose()
    {
        ViewOnlyMode.Enabled = false;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Process SpawnPing() =>
        Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

    private static void Cleanup(Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
        p.Dispose();
    }

    private static void AssertBlocked(ActionResult r)
    {
        Assert.False(r.Ok);
        Assert.Equal("Msg_ViewOnlyBlocked", r.MessageKey);
    }

    [Fact]
    public void ProcessesAreNeverSlowedFrozenTrimmedOrClosed()
    {
        var p = SpawnPing();
        try
        {
            p.Refresh();
            var before = p.PriorityClass;
            ViewOnlyMode.Enabled = true;

            AssertBlocked(ProcessMonitor.SetPriority(p.Id, p.ProcessName, ProcessPriorityClass.BelowNormal));
            AssertBlocked(ProcessMonitor.TrimMemory(p.Id, p.ProcessName));
            AssertBlocked(ProcessMonitor.SuspendProcess(p.Id, p.ProcessName));
            AssertBlocked(ProcessMonitor.Kill(p.Id, p.ProcessName));

            p.Refresh();
            Assert.False(p.HasExited);
            Assert.Equal(before, p.PriorityClass);
        }
        finally { ViewOnlyMode.Enabled = false; Cleanup(p); }
    }

    [Fact]
    public void ReleasingWhatWasChangedStaysPossible()
    {
        var p = SpawnPing();
        try
        {
            Assert.True(ProcessMonitor.SetPriority(p.Id, p.ProcessName, ProcessPriorityClass.BelowNormal).Ok);
            Assert.True(ProcessMonitor.SuspendProcess(p.Id, p.ProcessName).Ok);

            ViewOnlyMode.Enabled = true;

            // Turning view-only on must never strand a frozen or slowed process.
            Assert.True(ProcessMonitor.ResumeProcess(p.Id, p.ProcessName).Ok);
            Assert.True(ProcessMonitor.SetPriority(p.Id, p.ProcessName, ProcessPriorityClass.Normal, undo: true).Ok);
            p.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, p.PriorityClass);
        }
        finally { ViewOnlyMode.Enabled = false; Cleanup(p); }
    }

    [Fact]
    public void ChangesWorkAgainWhenViewOnlyIsOff()
    {
        var p = SpawnPing();
        try
        {
            ViewOnlyMode.Enabled = true;
            AssertBlocked(ProcessMonitor.SetPriority(p.Id, p.ProcessName, ProcessPriorityClass.BelowNormal));
            ViewOnlyMode.Enabled = false;
            Assert.True(ProcessMonitor.SetPriority(p.Id, p.ProcessName, ProcessPriorityClass.BelowNormal).Ok);
        }
        finally { ViewOnlyMode.Enabled = false; Cleanup(p); }
    }

    [Fact]
    public void EveryOtherKindOfChangeRefusesBeforeTouchingAnything()
    {
        ViewOnlyMode.Enabled = true;

        AssertBlocked(new StartupManager().SetEnabled(new StartupEntry { Name = "x", Source = StartupSource.RegistryRunHkcu }, false));
        var service = new ServiceEntry { Name = "NoSuchService", DisplayName = "No such service" };
        var services = new SvcManager();
        AssertBlocked(services.SetStartMode(service, "Disabled"));
        AssertBlocked(services.Stop(service));
        AssertBlocked(services.Start(service));

        var security = new SecurityManager();
        AssertBlocked(security.SetWinRm(false));
        AssertBlocked(security.SetSmbBlocked(true));
        AssertBlocked(security.SetAppBlocked(@"C:\nowhere\app.exe", true));
        AssertBlocked(new BatteryManager().SetHibernateOnLidClose(true));
        AssertBlocked(SystemRestoreService.CreateRestorePoint("test"));
        AssertBlocked(PriorityPersistence.SetPersistent("notepad", ProcessPriorityClass.Normal));
        AssertBlocked(PriorityPersistence.ClearPersistent("notepad"));

        var tweaks = new TweakManager();
        var (result, snapshot) = tweaks.Set(TweakManager.Tweaks[0], true);
        AssertBlocked(result);
        Assert.Empty(snapshot);
        AssertBlocked(tweaks.Restore(new List<(TweakManager.RegValue V, int? Old)>()));
    }

    [Fact]
    public void CleanupDeletesNothing()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "old.tmp");
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-90));
        var target = new CleanupTarget("T", () => new[] { _dir }, new[] { "*" }, 0, false);

        ViewOnlyMode.Enabled = true;
        var run = CleanupService.Clean(target);

        Assert.Equal(new CleanupRun(0, 0, 0), run);
        Assert.True(File.Exists(file));

        ViewOnlyMode.Enabled = false;
        Assert.True(CleanupService.Clean(target).Deleted >= 1); // and it is only the mode that held it back
    }
}

public class LoadWatchTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, double?> Loads(double? cpu = 10, double? mem = 10) =>
        new() { ["cpu"] = cpu, ["mem"] = mem };

    [Fact]
    public void ASpikeIsNotAnAlertButASustainedOverloadIs()
    {
        var watch = new LoadWatch();
        Assert.Empty(watch.Evaluate(T0, Loads(cpu: 95), 90));
        Assert.Empty(watch.Evaluate(T0.AddSeconds(5), Loads(cpu: 95), 90));
        Assert.Equal(new[] { "cpu" }, watch.Evaluate(T0.AddSeconds(10), Loads(cpu: 95), 90));
    }

    [Fact]
    public void AnInterruptedStreakStartsOver()
    {
        var watch = new LoadWatch();
        watch.Evaluate(T0, Loads(cpu: 95), 90);
        watch.Evaluate(T0.AddSeconds(5), Loads(cpu: 95), 90);
        watch.Evaluate(T0.AddSeconds(10), Loads(cpu: 50), 90);          // dips below: streak over
        Assert.Empty(watch.Evaluate(T0.AddSeconds(15), Loads(cpu: 95), 90));
        Assert.Empty(watch.Evaluate(T0.AddSeconds(20), Loads(cpu: 95), 90));
    }

    [Fact]
    public void OneAlertPerEpisodeAndNotAgainDuringTheCooldown()
    {
        var watch = new LoadWatch();
        var t = T0;
        for (var i = 0; i < LoadWatch.SustainedSamples - 1; i++, t = t.AddSeconds(5)) watch.Evaluate(t, Loads(cpu: 95), 90);
        Assert.Single(watch.Evaluate(t, Loads(cpu: 95), 90));

        // Still overloaded: no repeat. Dips and rises again within the cooldown: still quiet.
        t = t.AddSeconds(5);
        Assert.Empty(watch.Evaluate(t, Loads(cpu: 95), 90));
        t = t.AddSeconds(5); watch.Evaluate(t, Loads(cpu: 10), 90);
        for (var i = 0; i < LoadWatch.SustainedSamples; i++, t = t.AddSeconds(5))
            Assert.Empty(watch.Evaluate(t, Loads(cpu: 95), 90));
    }

    [Fact]
    public void ItMayAlertAgainOnceTheCooldownHasPassed()
    {
        var watch = new LoadWatch();
        var t = T0;
        for (var i = 0; i < LoadWatch.SustainedSamples; i++, t = t.AddSeconds(5)) watch.Evaluate(t, Loads(cpu: 95), 90);
        t = t.AddSeconds(5); watch.Evaluate(t, Loads(cpu: 10), 90);

        t = t.Add(LoadWatch.Cooldown);
        IReadOnlyList<string> due = Array.Empty<string>();
        for (var i = 0; i < LoadWatch.SustainedSamples; i++, t = t.AddSeconds(5)) due = watch.Evaluate(t, Loads(cpu: 95), 90);
        Assert.Equal(new[] { "cpu" }, due);
    }

    [Fact]
    public void LoadsAreJudgedIndependentlyAndAMissingReadingChangesNothing()
    {
        var watch = new LoadWatch();
        var t = T0;
        IReadOnlyList<string> due = Array.Empty<string>();
        for (var i = 0; i < LoadWatch.SustainedSamples; i++, t = t.AddSeconds(5))
            due = watch.Evaluate(t, new Dictionary<string, double?> { ["cpu"] = 10, ["mem"] = 97, ["gpu"] = null }, 90);

        Assert.Equal(new[] { "mem" }, due);
    }

    [Fact]
    public void ExactlyAtTheLimitIsNotOverIt()
    {
        var watch = new LoadWatch();
        for (var i = 0; i < 10; i++)
            Assert.Empty(watch.Evaluate(T0.AddSeconds(5 * i), Loads(cpu: 90), 90));
    }

    [Fact]
    public void ResetForgetsTheCount()
    {
        var watch = new LoadWatch();
        watch.Evaluate(T0, Loads(cpu: 95), 90);
        watch.Evaluate(T0.AddSeconds(5), Loads(cpu: 95), 90);
        watch.Reset();
        Assert.Empty(watch.Evaluate(T0.AddSeconds(10), Loads(cpu: 95), 90));
    }
}

public class LoadSamplerTests
{
    [Fact]
    public void GpuLoadIsTheBusiestEngineTypeSummedOverProcesses()
    {
        var engines = new (string, double)[]
        {
            ("pid_1_luid_0x0_phys_0_eng_0_engtype_3D", 30),
            ("pid_2_luid_0x0_phys_0_eng_0_engtype_3D", 25),
            ("pid_3_luid_0x0_phys_0_eng_3_engtype_VideoDecode", 40),
            ("pid_4_luid_0x0_phys_0_eng_1_engtype_Copy", 5),
        };

        Assert.Equal(55, LoadSampler.GpuFromEngines(engines));
    }

    [Fact]
    public void GpuLoadIsCappedAt100AndNullWithoutEngines()
    {
        Assert.Equal(100, LoadSampler.GpuFromEngines(new (string, double)[]
            { ("a_engtype_3D", 80), ("b_engtype_3D", 70) }));
        Assert.Null(LoadSampler.GpuFromEngines(Array.Empty<(string, double)>()));
        Assert.Null(LoadSampler.GpuFromEngines(new (string, double)[] { ("something without a type", 50) }));
    }

    [Fact]
    public void SamplingNeverThrowsAndStaysInRange()
    {
        var load = new LoadSampler().Sample();

        if (load.DiskPercent is { } d) Assert.InRange(d, 0, 100);
        if (load.GpuPercent is { } g) Assert.InRange(g, 0, 100);
    }
}

public class RefreshPolicyLoadAlertTests
{
    [Theory]
    [InlineData(false, false, true, true)]   // hidden, but the load alert is a standing job
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    public void TheLoadAlertKeepsTheRefreshAliveWhileHidden(bool visible, bool auto, bool alert, bool expected) =>
        Assert.Equal(expected, RefreshPolicy.ShouldRun(visible, auto, alert));
}

public class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-set2-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void ViewOnlyAndLoadChoicesSurviveARestart()
    {
        var path = Path.Combine(_dir, "settings.json");
        new AppSettings { ViewOnly = true, LoadAlert = true, LoadLimitPercent = 75 }.Save(path);

        var loaded = AppSettings.Load(path);

        Assert.True(loaded.ViewOnly);
        Assert.True(loaded.LoadAlert);
        Assert.Equal(75, loaded.LoadLimitPercent);
    }

    [Fact]
    public void DefaultsAreActiveModeNoAlertAndA90PercentLimit()
    {
        var s = AppSettings.Load(Path.Combine(_dir, "none.json"));

        Assert.False(s.ViewOnly);
        Assert.False(s.LoadAlert);
        Assert.Equal(90, s.LoadLimitPercent);
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(0, 50)]
    [InlineData(250, 100)]
    [InlineData(-5, 50)]
    [InlineData(80, 80)]
    public void AnOutOfRangeLimitIsPulledIntoTheSlidersRange(int stored, int expected)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "edited.json");
        File.WriteAllText(path, $"{{ \"LoadLimitPercent\": {stored} }}");

        Assert.Equal(expected, AppSettings.Load(path).LoadLimitPercent);
    }
}

/// <summary>The new Settings-tab strings exist in every language with the same placeholders as English.</summary>
public class SettingsLocalizationTests
{
    private static Dictionary<string, Dictionary<string, string>> Table() =>
        (Dictionary<string, Dictionary<string, string>>)typeof(Loc)
            .GetField("Strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static bool IsSettingsKey(string k) =>
        k.StartsWith("Set_", StringComparison.Ordinal) || k == "Tab_Settings"
        || k.StartsWith("Msg_ViewOnly", StringComparison.Ordinal) || k.StartsWith("Msg_LoadHigh", StringComparison.Ordinal);

    private static string[] Placeholders(string s) => Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v).ToArray();

    [Fact]
    public void EveryLanguageHasEveryString()
    {
        var table = Table();
        var keys = table["en"].Keys.Where(IsSettingsKey).ToList();
        Assert.True(keys.Count >= 23);
        foreach (var lang in Loc.Languages)
            foreach (var key in keys)
                Assert.True(table[lang.Code].ContainsKey(key), $"{lang.Code} is missing {key}");
    }

    [Fact]
    public void PlaceholdersMatchEnglishAndNothingStaysInEnglish()
    {
        var table = Table();
        var sameAsEnglishIsFine = new HashSet<string> { "Set_LoadLimitValue", "Set_LoadGpu" };
        foreach (var key in table["en"].Keys.Where(IsSettingsKey))
            foreach (var lang in Loc.Languages.Where(l => l.Code != "en"))
            {
                Assert.True(Placeholders(table["en"][key]).SequenceEqual(Placeholders(table[lang.Code][key])),
                    $"{lang.Code}/{key}: placeholders differ from English");
                if (!sameAsEnglishIsFine.Contains(key))
                    Assert.NotEqual(table["en"][key], table[lang.Code][key]);
            }
    }
}
