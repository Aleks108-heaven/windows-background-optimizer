using System.IO;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>What the close button does. Hiding must never get in the way of exiting, signing out or an installer's close request.</summary>
public class CloseBehaviorTests
{
    [Theory]
    [InlineData(true, false, false, true)]    // the normal case: the close button hides the window
    [InlineData(true, true, false, false)]    // tray menu Exit: really close
    [InlineData(true, false, true, false)]    // signing out, shutting down, an installer asking the app to close
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]  // the user turned it off: the close button quits
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    public void TheWindowIsOnlyHiddenWhenNothingElseNeedsItToClose(bool keep, bool exiting, bool sessionEnding, bool hides) =>
        Assert.Equal(hides, CloseBehavior.ShouldHide(keep, exiting, sessionEnding));
}

public class TraySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-tray-{Guid.NewGuid():N}");
    private string PathIn(string name) => Path.Combine(_dir, name);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void ClosingToTheTrayIsOnByDefaultAndTheHintIsNotYetShown()
    {
        var s = new AppSettings();
        Assert.True(s.KeepRunningInTray);
        Assert.False(s.TrayHintShown);
    }

    [Fact]
    public void AnOlderSettingsFileWithoutTheFieldsGetsTheDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn("settings.json"), "{\"Language\":\"uk\",\"AutoOptimize\":true}");

        var s = AppSettings.Load(PathIn("settings.json"));

        Assert.True(s.KeepRunningInTray);   // upgrading must not silently turn it off
        Assert.True(s.AutoOptimize);        // and the rest of the file is kept
    }

    [Fact]
    public void BothChoicesSurviveARestart()
    {
        new AppSettings { KeepRunningInTray = false, TrayHintShown = true }.Save(PathIn("settings.json"));

        var s = AppSettings.Load(PathIn("settings.json"));

        Assert.False(s.KeepRunningInTray);
        Assert.True(s.TrayHintShown);
    }
}

/// <summary>Restart Explorer: it may only ever touch Windows' own shell in this session, and only when Windows will bring it back.</summary>
[Collection("ViewOnly")]
public class ExplorerRestarterTests : IDisposable
{
    public ExplorerRestarterTests() => ViewOnlyMode.Enabled = false;
    public void Dispose() => ViewOnlyMode.Enabled = false;

    private const string Windows = @"C:\WINDOWS";

    [Theory]
    [InlineData(@"C:\WINDOWS\Explorer.EXE", 1, 1, true)]      // exactly how Windows spells it on this machine
    [InlineData(@"c:\windows\explorer.exe", 1, 1, true)]
    [InlineData(@"C:\WINDOWS\Explorer.EXE", 2, 1, false)]     // another user's shell
    [InlineData(@"C:\Users\x\Downloads\explorer.exe", 1, 1, false)]  // a look-alike
    [InlineData(@"C:\WINDOWS\System32\explorer.exe", 1, 1, false)]
    [InlineData(null, 1, 1, false)]                            // the path could not be read: not ours to kill
    public void OnlyWindowsOwnShellInThisSessionCounts(string? path, int session, int mine, bool expected) =>
        Assert.Equal(expected, ExplorerRestarter.IsShell(path, session, Windows, mine));

    [Theory]
    [InlineData(null, null, true)]                 // Windows' defaults (both values absent)
    [InlineData("explorer.exe", 1, true)]
    [InlineData("Explorer.EXE", null, true)]
    [InlineData("explorer.exe", 0, false)]         // restart switched off: killing it would leave no desktop
    [InlineData("cmd.exe", 1, false)]              // a custom shell
    [InlineData("explorer.exe", "1", false)]       // not a number: not trusted
    [InlineData(5, 1, false)]
    public void TheShellIsOnlyClosedWhenWindowsWillStartANewOne(object? shell, object? autoRestart, bool expected) =>
        Assert.Equal(expected, ExplorerRestarter.WindowsRestartsTheShell(shell, autoRestart));

    [Fact]
    public void ViewOnlyModeRefusesBeforeAnythingIsClosed()
    {
        ViewOnlyMode.Enabled = true;

        var result = ExplorerRestarter.Restart();

        Assert.False(result.Ok);
        Assert.Equal("Msg_ViewOnlyBlocked", result.MessageKey);
    }

    [Fact]
    public void ExplorerStaysOnTheProtectedListSoNothingElseCanKillIt()
    {
        Assert.True(ProtectedItems.IsProcessProtected("explorer"));
        // A PID that cannot exist, so even a regression in the guard could not end a real process; the refusal is by name.
        var result = ProcessMonitor.Kill(int.MaxValue, "explorer");
        Assert.False(result.Ok);
        Assert.Equal("Msg_ProcessProtectedKill", result.MessageKey);
    }
}

/// <summary>Windows' own older driver copies have no oemNN.inf name: they are labelled as built in and are never offered.</summary>
public class DriverBuiltInTests
{
    private static DriverPackage Package(string folder, string version, DateTime date, string? published) =>
        new(folder, "amdgpio2.inf", "amd64", published, date, Version.Parse(version), "AMD", 1000);

    private static IReadOnlyList<DriverCopy> Scan(string? olderPublished) =>
        DriverStoreScanner.FindDuplicates(
            new[]
            {
                Package("amdgpio2.inf_amd64_new", "2.2.0.137", new DateTime(2026, 3, 22), "oem112.inf"),
                Package("amdgpio2.inf_amd64_old", "2.2.0.71", new DateTime(2017, 5, 17), olderPublished),
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oem112.inf" });

    [Fact]
    public void AnOlderUnusedCopyWithoutAnOemNameIsBuiltIn()
    {
        var copies = Scan(olderPublished: null);

        Assert.Equal(DriverCopyStatus.NewestInUse, copies[0].Status);
        Assert.Equal(DriverCopyStatus.OlderUnusedBuiltIn, copies[1].Status);
        Assert.False(DriverStoreCleaner.CanDelete(copies[1]));
        Assert.Equal(1, DriverStoreCleaner.CountBuiltIn(copies));
    }

    [Fact]
    public void TheSameCopyWithAnOemNameIsStillAnOrdinaryRemovableOne()
    {
        var copies = Scan(olderPublished: "oem9.inf");

        Assert.Equal(DriverCopyStatus.OlderUnused, copies[1].Status);
        Assert.True(DriverStoreCleaner.CanDelete(copies[1]));
        Assert.Equal(0, DriverStoreCleaner.CountBuiltIn(copies));
    }

    [Fact]
    public void WhenOnlyBuiltInCopiesWerePickedTheMessageSaysWhyInsteadOfRepeatingTheRule()
    {
        Assert.Equal("Ck_DrvOnlyBuiltIn", DriverStoreCleaner.NothingToRemoveKey(Scan(olderPublished: null)));
        Assert.Equal("Ck_DrvNoneDeletable", DriverStoreCleaner.NothingToRemoveKey(Scan(olderPublished: "oem9.inf").Take(1)));
        Assert.Equal("Ck_DrvNoneDeletable", DriverStoreCleaner.NothingToRemoveKey(Array.Empty<DriverCopy>()));
    }

    [Fact]
    public void ABuiltInCopyIsRefusedWithoutRunningAnything()
    {
        var result = DriverStoreCleaner.Delete(Scan(olderPublished: null)[1]);

        Assert.False(result.Ok);
        Assert.Equal("Ck_DrvNotRemovable", result.MessageKey);
    }

    [Fact]
    public void ThePublishedColumnSaysBuiltInInsteadOfADash()
    {
        var row = BgOptimizer.Models.CheckRows.ForDrivers(Scan(olderPublished: null)).Last();

        Assert.NotEqual("—", row.PublishedName);
        Assert.NotEmpty(row.PublishedName);
    }
}

/// <summary>A service Windows refuses to change says so in words; the real log showed "WMI error code 2" twice for the WSL service.</summary>
public class ServiceFailureMessageTests
{
    [Fact]
    public void AccessDeniedHasItsOwnExplanationAndEveryOtherCodeKeepsTheGenericOne()
    {
        Assert.Equal("Msg_ServiceModeDenied", SvcManager.FailureKey(2));
        foreach (var code in new uint[] { 1, 3, 5, 14, 21, 255 })
            Assert.Equal("Msg_ServiceModeFailed", SvcManager.FailureKey(code));
    }
}

/// <summary>The "never touch" list shown in Settings, and the picker's candidates.</summary>
[Collection("ViewOnly")]
public class ProtectedListUiTests : IDisposable
{
    public ProtectedListUiTests() => ProtectedItems.SetUserNames("");
    public void Dispose() => ProtectedItems.SetUserNames("");

    [Fact]
    public void EveryAlwaysOnGroupIsListedWithItsNames()
    {
        var groups = ProtectedItems.BuiltInGroups();

        Assert.Equal(7, groups.Count);
        Assert.All(groups, g => Assert.NotEmpty(g.Names));
        Assert.Equal(groups.Count, groups.Select(g => g.TitleKey).Distinct().Count());
        Assert.All(groups.SelectMany(g => g.Names), n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Theory]
    [InlineData("Set_Built_Core", "explorer")]
    [InlineData("Set_Built_Agents", "AnyDesk")]
    [InlineData("Set_Built_Platform", "wlanext")]
    [InlineData("Set_Built_Security", "CSFalconService")]
    [InlineData("Set_Built_Audio", "*audio*")]      // a rule that matches part of a name is shown as such
    [InlineData("Set_Built_Ai", "ollama")]
    [InlineData("Set_Built_Shell", "msedge")]
    public void TheListShowsWhatIsReallyProtected(string group, string name) =>
        Assert.Contains(name, ProtectedItems.BuiltInGroups().Single(g => g.TitleKey == group).Names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ThePickerOffersOnlyProgramsThatAreNotAlreadyProtectedBiggestFirst()
    {
        var running = new (string Name, long Bytes)[]
        {
            ("chrome", 900), ("svchost", 800), ("AnyDesk", 700), ("CSFalconService", 600), ("ollama", 500),  // all already protected
            ("MyTool", 40), ("MyTool", 60), ("BigApp", 300), ("small", 10), ("Odd,Name", 500), ("BigApp.exe", 50),
        };

        var candidates = ProtectedItems.PickerCandidates(running);

        Assert.Equal(new[] { "BigApp", "MyTool", "small" }, candidates.Select(c => c.Name));   // "Odd,Name" would split in two
        Assert.Equal(350, candidates[0].Bytes);       // instances of one name are added together ("BigApp" and "BigApp.exe")
        Assert.Equal(2, candidates[1].Instances);
    }

    [Fact]
    public void AProgramAlreadyOnTheUsersOwnListIsNotOfferedAgain()
    {
        ProtectedItems.SetUserNames("MyTool");

        var candidates = ProtectedItems.PickerCandidates(new (string, long)[] { ("MyTool", 5), ("Other", 4) });

        Assert.Equal(new[] { "Other" }, candidates.Select(c => c.Name));
    }

    [Fact]
    public void ThePickerNeverOffersMoreThanTheCap()
    {
        var many = Enumerable.Range(0, 300).Select(i => ($"prog{i}", (long)i)).ToList();

        Assert.Equal(80, ProtectedItems.PickerCandidates(many).Count);
        Assert.Equal(5, ProtectedItems.PickerCandidates(many, max: 5).Count);
    }
}
