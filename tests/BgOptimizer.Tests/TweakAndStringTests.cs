using System.Reflection;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;
using BgOptimizer.Services;
using Microsoft.Win32;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The curated registry tweaks: every one must be reversible to the Windows default and be shown under exactly one heading.</summary>
public class TweakCatalogTests
{
    [Fact]
    public void IdsAreUniqueAndEveryTweakSitsUnderExactlyOneHeading()
    {
        var ids = TweakManager.Tweaks.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var grouped = TweakManager.Groups.SelectMany(g => g.Ids).ToList();
        Assert.Equal(grouped.Count, grouped.Distinct().Count());                 // none listed twice
        Assert.Equal(ids.OrderBy(i => i), grouped.OrderBy(i => i));               // none missing, none invented
        Assert.Equal(TweakManager.Groups.Count, TweakManager.Groups.Select(g => g.Group).Distinct().Count());
    }

    [Fact]
    public void EveryValueChangesSomethingAndHasARealPath()
    {
        foreach (var tweak in TweakManager.Tweaks)
        {
            Assert.NotEmpty(tweak.Values);
            foreach (var v in tweak.Values)
            {
                Assert.NotEqual(v.Default, v.Target);                             // turning it on must change the value
                Assert.False(string.IsNullOrWhiteSpace(v.Path));
                Assert.False(string.IsNullOrWhiteSpace(v.Name));
            }
        }
    }

    [Fact]
    public void ATweakThatWritesMachineWideSaysItNeedsAdministratorRights()
    {
        foreach (var tweak in TweakManager.Tweaks)
            if (tweak.Values.Any(v => v.Hive == RegistryHive.LocalMachine))
                Assert.True(tweak.NeedsAdmin, $"{tweak.Id} writes to HKLM");
            else
                Assert.False(tweak.NeedsAdmin, $"{tweak.Id} only writes to the current user");
    }

    [Fact]
    public void NothingTouchesDefenderUacTheFirewallOrWindowsUpdateItself()
    {
        // The app's promise (README, Tweaks tab): none of these areas is changed.
        var forbidden = new[] { "Windows Defender", "System\\ConsentPromptBehaviorAdmin", "EnableLUA", "SharedAccess", "WindowsUpdate\\AU", "SmartScreen" };
        foreach (var v in TweakManager.Tweaks.SelectMany(t => t.Values))
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, v.Path + "\\" + v.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadingEveryTweaksCurrentStateOnlyReadsAndNeverThrows()
    {
        var manager = new TweakManager();
        foreach (var tweak in TweakManager.Tweaks) _ = manager.IsApplied(tweak); // against this machine's real registry; read only
    }

}

/// <summary>The refusal sits inside TweakManager.Set, so every tweak, new ones included, is held back by view-only mode before a value is read or written.</summary>
[Collection("ViewOnly")]
public class TweakViewOnlyTests : IDisposable
{
    public TweakViewOnlyTests() => ViewOnlyMode.Enabled = false;
    public void Dispose() => ViewOnlyMode.Enabled = false;

    [Fact]
    public void NoTweakCanBeAppliedOrRevertedInViewOnlyMode()
    {
        ViewOnlyMode.Enabled = true;
        var manager = new TweakManager();

        foreach (var tweak in TweakManager.Tweaks)
            foreach (var on in new[] { true, false })
            {
                var (result, snapshot) = manager.Set(tweak, on);
                Assert.False(result.Ok, tweak.Id);
                Assert.Equal("Msg_ViewOnlyBlocked", result.MessageKey);
                Assert.Empty(snapshot); // nothing was even read
            }
    }
}

/// <summary>Every string this round added exists in all seven languages, with the same placeholders, and formats without an error.</summary>
public class NewStringsTests
{
    private static Dictionary<string, Dictionary<string, string>> Table() =>
        (Dictionary<string, Dictionary<string, string>>)typeof(Loc)
            .GetField("Strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v).ToArray();

    private static readonly string[] Fixed =
    {
        "Ck_Drv_OlderUnusedBuiltIn", "Ck_DrvBuiltInName", "Ck_DrvOnlyBuiltIn", "Ck_DrvBuiltInKept",
        "Set_WindowTitle", "Set_KeepTray", "Set_KeepTrayHint", "Msg_TrayStillRunning",
        "Btn_RestartExplorer", "Btn_RestartExplorerHint", "Dlg_ExplorerTitle", "Dlg_ExplorerMessage", "Msg_BlockedExplorer",
        "Msg_ExplorerRestarted", "Msg_ExplorerNotFound", "Msg_ExplorerNotBack", "Msg_ExplorerNoAutoRestart", "Msg_ExplorerFailed",
        "Set_NeverIntro", "Set_NeverBuiltIn", "Set_NeverShowList", "Set_NeverOwn", "Set_NeverPick", "Set_NeverPickTitle",
        "Set_NeverPickHint", "Set_NeverPickAdd", "Set_NeverPickNone", "Set_NeverPickAdded",
        "Tab_Power", "Pw_Title", "Pw_Hint", "Pw_ActivePlan", "Pw_AdviceTitle", "Pw_Reading", "Pw_AdviceNone", "Pw_AdviceMinCpu",
        "Pw_AdviceMinAboveMax", "Pw_AdvicePerf", "Pw_AdviceSleep", "Pw_AdviceWake", "Pw_ShowAdvanced", "Pw_PluggedIn", "Pw_OnBattery", "Pw_Apply",
        "Pw_NotAvailable", "Pw_PairBoth", "Pw_Never", "Pw_Min", "Pw_Hour",
        "Dlg_PowerChange", "Msg_PowerSet", "Msg_PowerUnchanged", "Msg_PowerMinAboveMax",
        "Msg_ServiceModeDenied",
    };

    /// <summary>The fixed list plus every key the catalogs name: titles, hints, headings and option labels.</summary>
    private static IReadOnlyList<string> Keys()
    {
        var keys = new List<string>(Fixed);
        foreach (var s in PowerCatalog.Settings)
        {
            keys.Add("Pw_" + s.Id);
            keys.Add("Pw_" + s.Id + "_d");
            keys.Add("Pw_Group_" + s.Group);
            if (s.Choices is not null) keys.AddRange(s.Choices.Select(c => c.LabelKey));
        }
        foreach (var (group, ids) in TweakManager.Groups)
        {
            keys.Add("Tw_Group_" + group);
            foreach (var id in ids) { keys.Add("Tw_" + id); keys.Add("Tw_" + id + "_d"); }
        }
        keys.AddRange(ProtectedItems.BuiltInGroups().Select(g => g.TitleKey));
        return keys.Distinct().ToList();
    }

    [Fact]
    public void EveryLanguageHasEveryNewString()
    {
        var table = Table();
        foreach (var lang in Loc.Languages)
            foreach (var key in Keys())
                Assert.True(table[lang.Code].ContainsKey(key), $"{lang.Code} is missing {key}");
    }

    [Fact]
    public void PlaceholdersMatchEnglishAndEveryStringFormats()
    {
        // Straight from the table, not through Loc.Instance: switching the app-wide language would race with other tests.
        var table = Table();
        foreach (var key in Keys())
        {
            var count = Placeholders(table["en"][key]).Length;
            foreach (var lang in Loc.Languages)
            {
                Assert.True(Placeholders(table["en"][key]).SequenceEqual(Placeholders(table[lang.Code][key])),
                    $"{lang.Code}/{key}: placeholders differ from English");
                var args = Enumerable.Range(0, count).Select(i => (object)$"v{i}").ToArray();
                var text = string.Format(table[lang.Code][key], args); // throws on a stray brace or a missing argument
                Assert.DoesNotContain("{", text);
            }
        }
    }

    [Fact]
    public void NoLanguageLeavesANewStringInEnglish()
    {
        // Words that are the same in some language: a unit written the same way, and "Privacy".
        var mayMatch = new HashSet<string> { "Pw_Min", "Pw_Hour", "Tw_Group_Privacy" };
        var table = Table();
        foreach (var key in Keys().Where(k => !mayMatch.Contains(k)))
            foreach (var lang in Loc.Languages.Where(l => l.Code != "en"))
                Assert.NotEqual(table["en"][key], table[lang.Code][key]);
    }

    [Fact]
    public void EveryDriverStatusStillHasATranslation()
    {
        var table = Table();
        foreach (var status in Enum.GetValues<DriverCopyStatus>())
            foreach (var lang in Loc.Languages)
                Assert.True(table[lang.Code].ContainsKey($"Ck_Drv_{status}"), $"{lang.Code} lacks Ck_Drv_{status}");
    }

    [Fact]
    public void NoNewStringHasAnEmptyTranslation()
    {
        var table = Table();
        foreach (var key in Keys())
            foreach (var lang in Loc.Languages)
                Assert.False(string.IsNullOrWhiteSpace(table[lang.Code][key]), $"{lang.Code}/{key} is empty");
    }
}
