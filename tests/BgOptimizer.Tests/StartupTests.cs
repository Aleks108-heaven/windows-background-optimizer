using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Microsoft.Win32;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The StartupApproved flag: decoded the way Windows reads it, encoded the way Windows writes it.</summary>
public class StartupApprovedValueTests
{
    // Byte patterns copied from a real Windows 11 machine (Task Manager's own records).
    private static readonly byte[] DisabledByTaskManager = { 0x03, 0, 0, 0, 0xAB, 0x56, 0xD9, 0xCB, 0x62, 0xE8, 0xDC, 0x01 };
    private static readonly byte[] EnabledPlain = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] FolderItemSeenAs04 = { 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]   // another "enabled" form Windows writes
    [InlineData(0x04, true)]   // seen on a real machine for a Startup-folder item; even = not disabled
    [InlineData(0x03, false)]
    [InlineData(0x07, false)]
    public void TheLowBitOfTheFirstByteIsTheOffSwitch(byte first, bool enabled) =>
        Assert.Equal(enabled, StartupApprovedValue.IsEnabled(new byte[] { first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));

    [Fact]
    public void RealMachineValuesAreReadCorrectly()
    {
        Assert.False(StartupApprovedValue.IsEnabled(DisabledByTaskManager));
        Assert.True(StartupApprovedValue.IsEnabled(EnabledPlain));
        Assert.True(StartupApprovedValue.IsEnabled(FolderItemSeenAs04));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a string where bytes should be")]
    [InlineData(7)]
    public void NoRecordOrACorruptOneMeansEnabledNotACrash(object? raw) =>
        Assert.True(StartupApprovedValue.IsEnabled(raw));

    [Fact]
    public void AnEmptyOrTruncatedValueIsStillJudgedByItsFirstByte()
    {
        Assert.True(StartupApprovedValue.IsEnabled(Array.Empty<byte>()));
        Assert.False(StartupApprovedValue.IsEnabled(new byte[] { 0x03 }));
        Assert.True(StartupApprovedValue.IsEnabled(new byte[] { 0x02, 0x00 }));
    }

    [Fact]
    public void EncodedValuesAreTwelveBytesAndRoundTrip()
    {
        var at = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        var off = StartupApprovedValue.Encode(false, at);
        var on = StartupApprovedValue.Encode(true, at);

        Assert.Equal(12, off.Length);
        Assert.Equal(12, on.Length);
        Assert.False(StartupApprovedValue.IsEnabled(off));
        Assert.True(StartupApprovedValue.IsEnabled(on));
        Assert.Equal(0x03, off[0]);
        Assert.Equal(0x02, on[0]);
        Assert.Equal(at.ToFileTimeUtc(), BitConverter.ToInt64(off, 4));   // Windows stores when it was switched off
        Assert.All(on.Skip(1), b => Assert.Equal(0, b));                   // an "on" value carries no time
    }
}

/// <summary>Pulling the program out of a startup command line, whatever the quoting, length or alphabet.</summary>
public class CommandPathTests
{
    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Vendor App\\app.exe\" -background", "C:\\Program Files (x86)\\Vendor App\\app.exe")]
    [InlineData("C:\\Program Files (x86)\\Vendor App\\app.exe -background", "C:\\Program Files (x86)\\Vendor App\\app.exe")]
    [InlineData("C:\\Tools\\run.vbs //B", "C:\\Tools\\run.vbs")]
    [InlineData("\"C:\\Users\\Олена\\AppData\\Local\\Програма\\старт.exe\"", "C:\\Users\\Олена\\AppData\\Local\\Програма\\старт.exe")]
    [InlineData("C:\\Users\\Олена\\Програма\\старт.exe /s", "C:\\Users\\Олена\\Програма\\старт.exe")]
    [InlineData("\\\\server\\share\\tool.exe /quiet", "\\\\server\\share\\tool.exe")]
    [InlineData("C:\\Windows\\System32\\rundll32.exe C:\\x\\y.dll,Entry", "C:\\Windows\\System32\\rundll32.exe")]
    public void TheProgramIsFoundInQuotedUnquotedAndNonLatinCommands(string command, string expected) =>
        Assert.Equal(expected, CommandPath.ExtractFilePath(command));

    [Theory]
    [InlineData("cmd /c start something")]      // not a drive path: nothing to probe
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("\\\\?\\C:\\device\\path.exe")]  // device-namespace path
    public void CommandsWithoutADrivePathGiveNull(string? command) =>
        Assert.Null(CommandPath.ExtractFilePath(command));

    [Fact]
    public void EnvironmentVariablesAreExpanded()
    {
        var windows = Environment.GetEnvironmentVariable("SystemRoot")!;
        Assert.Equal(Path.Combine(windows, "System32", "x.exe"), CommandPath.ExtractFilePath("%SystemRoot%\\System32\\x.exe /a"));
    }

    [Fact]
    public void AVeryLongPathIsHandledWithoutThrowing()
    {
        var path = "C:\\" + string.Join("\\", Enumerable.Repeat("a-rather-long-folder-name", 15)) + "\\tool.exe";
        Assert.True(path.Length > 260);

        Assert.Equal(path, CommandPath.ExtractFilePath($"\"{path}\" /x"));
        Assert.True(PathProbe.IsMissing(path)); // probing a nonexistent over-long path must answer, not throw
    }
}

public class StartupSafetyTests
{
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Theory]
    [InlineData("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe")]
    [InlineData("egui", @"C:\Program Files\ESET\ESET Security\egui.exe")]
    [InlineData("Windows Defender", null)]
    [InlineData("whatever", @"C:\Program Files\Sophos\SophosHealth.exe")]
    public void SecuritySoftwareCannotBeDisabled(string name, string? target) =>
        Assert.Equal(StartupLock.SecurityTool, StartupSafety.Evaluate(name, target));

    [Fact]
    public void ComponentsInsideWindowsAreLockedButVendorHelpersInTheDriverStoreAreNot()
    {
        Assert.Equal(StartupLock.WindowsComponent,
            StartupSafety.Evaluate("Some Windows thing", Path.Combine(Windows, "System32", "thing.exe")));
        Assert.Equal(StartupLock.None,
            StartupSafety.Evaluate("RtkAudUService",
                Path.Combine(Windows, "System32", "DriverStore", "FileRepository", "realtekservice.inf_amd64_x", "RtkAudUService64.exe")));
    }

    [Fact]
    public void ThisAppIsLocked()
    {
        Assert.Equal(StartupLock.ThisApp, StartupSafety.Evaluate("Background Optimizer", @"C:\Program Files\Background Optimizer\BgOptimizer.exe"));
        Assert.Equal(StartupLock.ThisApp, StartupSafety.Evaluate("BgOptimizer", null));
    }

    [Theory]
    [InlineData("OneDrive", @"C:\Program Files\Microsoft OneDrive\OneDrive.exe")]
    [InlineData("Docker Desktop", @"C:\Program Files\Docker\Docker\Docker Desktop.exe")]
    [InlineData("Some updater", null)]
    public void OrdinaryStartupItemsAreNotLocked(string name, string? target) =>
        Assert.Equal(StartupLock.None, StartupSafety.Evaluate(name, target));
}

public class StartupManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-startup-{Guid.NewGuid():N}");

    public StartupManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static readonly RegistryKey Hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    [Fact]
    public void ListingTheRealMachineNeverThrowsAndEveryRowIsUsable()
    {
        var items = new StartupManager().GetAll();

        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Name));
            Assert.NotEqual("Unknown", i.SourceDisplay);
            if (i.TargetMissing) Assert.NotNull(i.TargetPath);
        });
    }

    [Fact]
    public void ReadOnlyKindsAreRefusedWithoutTouchingAnything()
    {
        foreach (var source in new[] { StartupSource.RunOnceHkcu, StartupSource.RunOnceHklm, StartupSource.PolicyHkcu, StartupSource.PolicyHklm, StartupSource.StoreApp })
        {
            var entry = new StartupEntry { Name = "x", Source = source, Enabled = true };
            Assert.True(entry.IsReadOnly);
            var r = StartupManager.Toggle(entry, false);
            Assert.False(r.Ok);
            Assert.Equal("Msg_StartupReadOnly", r.MessageKey);
            Assert.True(entry.Enabled);
        }
    }

    [Fact]
    public void LockedItemsCannotBeDisabledHere()
    {
        var entry = new StartupEntry { Name = "SecurityHealth", Source = StartupSource.RegistryRunHklm, Enabled = true, Lock = StartupLock.SecurityTool };

        var r = StartupManager.Toggle(entry, false);

        Assert.False(r.Ok);
        Assert.Equal("Msg_StartupLocked", r.MessageKey);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void FolderItemsAreReadWithoutAnyRename_IncludingSpacesParenthesesAndCyrillic()
    {
        foreach (var name in new[] { "plain.lnk", "My App (x86).lnk", "Привіт програма.lnk", "run it.cmd" })
            File.WriteAllText(Path.Combine(_dir, name), "x");
        var hidden = Path.Combine(_dir, "desktop.ini");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var items = StartupManager.ReadFolder(_dir, StartupSource.StartupFolderUser, Hkcu);

        Assert.Equal(new[] { "My App (x86)", "plain", "run it", "Привіт програма" }, items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(items, i => Assert.True(i.Enabled));     // no approval record = enabled
        Assert.True(File.Exists(Path.Combine(_dir, "plain.lnk")));
    }

    [Fact]
    public void ACopyRenamedByAnOlderVersionIsListedAsOffAndStaysTheSameFile()
    {
        var legacy = Path.Combine(_dir, "old.lnk.disabled");
        File.WriteAllText(legacy, "x");

        var item = Assert.Single(StartupManager.ReadFolder(_dir, StartupSource.StartupFolderUser, Hkcu));

        Assert.Equal("old", item.Name);
        Assert.False(item.Enabled);
        Assert.Equal(legacy, item.Command);

        // Switching an already-off legacy item off is a no-op: nothing is renamed or written.
        var r = StartupManager.SetFolderItemEnabled(Hkcu, item, false);
        Assert.True(r.Ok);
        Assert.Equal("Msg_StartupNoChange", r.MessageKey);
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void EmptyAndLargeFoldersAreFine()
    {
        Assert.Empty(StartupManager.ReadFolder(_dir, StartupSource.StartupFolderUser, Hkcu));
        Assert.Empty(StartupManager.ReadFolder(Path.Combine(_dir, "missing"), StartupSource.StartupFolderUser, Hkcu));

        for (var i = 0; i < 600; i++) File.WriteAllText(Path.Combine(_dir, $"item{i:D3}.lnk"), "x");
        Assert.Equal(600, StartupManager.ReadFolder(_dir, StartupSource.StartupFolderUser, Hkcu).Count);
    }
}

public class StartupDetailsTests
{
    private static StartupEntry Entry(Action<StartupEntry>? tweak = null)
    {
        var e = new StartupEntry
        {
            Name = "Docker Desktop", Command = "\"C:\\Program Files\\Docker\\Docker Desktop.exe\" -background",
            Source = StartupSource.RegistryRunHkcu, Enabled = true, TargetPath = "C:\\Program Files\\Docker\\Docker Desktop.exe",
            Publisher = "Docker Inc",
        };
        tweak?.Invoke(e);
        return e;
    }

    public StartupDetailsTests() => Loc.Instance.Language = "en";

    [Fact]
    public void NothingSelectedExplainsWhatToDo() =>
        Assert.Equal("Select an item to see what it is and what it runs.", StartupDetails.Describe(null));

    [Fact]
    public void ARegistryEntryShowsItsFullCommandAndPublisher()
    {
        var text = StartupDetails.Describe(Entry());

        Assert.Contains("Docker Desktop.exe\" -background", text);   // arguments are shown, they matter
        Assert.Contains("Publisher: Docker Inc", text);
        Assert.DoesNotContain("no longer exists", text);
    }

    [Fact]
    public void AMissingTargetIsExplainedAndNoPublisherIsClaimed()
    {
        var text = StartupDetails.Describe(Entry(e => { e.TargetMissing = true; e.Publisher = null; }));

        Assert.Contains("no longer exists", text);
        Assert.DoesNotContain("Publisher", text);
    }

    [Fact]
    public void LockedAndReadOnlyItemsSayWhy()
    {
        Assert.Contains("Disabling is blocked", StartupDetails.Describe(Entry(e => e.Lock = StartupLock.SecurityTool)));
        Assert.Contains("cannot be switched here", StartupDetails.Describe(Entry(e => e.Source = StartupSource.StoreApp)));
        Assert.Contains("unknown or not signed", StartupDetails.Describe(Entry(e => e.Publisher = null)));
    }

    [Fact]
    public void RowNotesCombineEverythingThatApplies()
    {
        var e = Entry(x => { x.Lock = StartupLock.WindowsComponent; x.TargetMissing = true; });
        Assert.Equal("Locked: Windows component · Target file missing", e.NoteDisplay);
        Assert.Equal("", Entry().NoteDisplay);
    }
}

/// <summary>The new Startup-tab strings exist in every language, with the same placeholders as English.</summary>
public class StartupLocalizationTests
{
    private static Dictionary<string, Dictionary<string, string>> Table() =>
        (Dictionary<string, Dictionary<string, string>>)typeof(Loc)
            .GetField("Strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static readonly string[] OtherKeys =
    {
        "Src_RegistryHklm32", "Src_RunOnceHkcu", "Src_RunOnceHklm", "Src_PolicyHkcu", "Src_PolicyHklm", "Src_StoreApp",
        "Msg_StartupReadOnly", "Msg_StartupLocked", "Msg_StartupReenabled",
    };

    private static bool IsStartupKey(string k) => k.StartsWith("St_", StringComparison.Ordinal) || OtherKeys.Contains(k);

    private static string[] Placeholders(string s) => Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v).ToArray();

    [Fact]
    public void EveryLanguageHasEveryStringWithMatchingPlaceholders()
    {
        var table = Table();
        var keys = table["en"].Keys.Where(IsStartupKey).ToList();
        Assert.True(keys.Count >= 23);
        foreach (var lang in Loc.Languages)
            foreach (var key in keys)
            {
                Assert.True(table[lang.Code].ContainsKey(key), $"{lang.Code} is missing {key}");
                Assert.True(Placeholders(table["en"][key]).SequenceEqual(Placeholders(table[lang.Code][key])), $"{lang.Code}/{key} placeholders");
                if (lang.Code != "en") Assert.NotEqual(table["en"][key], table[lang.Code][key]);
            }
    }

    [Fact]
    public void EveryStartupSourceHasALabelInEveryLanguage()
    {
        try
        {
            foreach (var lang in Loc.Languages)
            {
                Loc.Instance.Language = lang.Code;
                foreach (var source in Enum.GetValues<StartupSource>())
                {
                    var label = new StartupEntry { Source = source }.SourceDisplay;
                    Assert.NotEqual(Loc.Instance["Src_Unknown"], label);
                    Assert.False(label.StartsWith("Src_", StringComparison.Ordinal), $"{lang.Code}/{source} has no translation");
                }
            }
        }
        finally { Loc.Instance.Language = "en"; }
    }
}
