using BgOptimizer.Localization;
using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// A small, curated set of reversible registry tweaks (privacy / background-load). Every value
/// records its Windows default (null = value absent), so switching a tweak off restores the
/// default and Undo restores the exact previous state, including "value did not exist".
/// Deliberately excludes Defender, SmartScreen, UAC, Windows Update and the firewall.
/// </summary>
public class TweakManager
{
    public record RegValue(RegistryHive Hive, string Path, string Name, int Target, int? Default);

    public record Tweak(string Id, bool NeedsAdmin, params RegValue[] Values);

    private const string CV = @"Software\Microsoft\Windows\CurrentVersion";

    private static RegValue User(string path, string name, int target, int? def = null) =>
        new(RegistryHive.CurrentUser, path, name, target, def);

    public static readonly IReadOnlyList<Tweak> Tweaks = new Tweak[]
    {
        new("Telemetry", true,
            new RegValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null)),
        new("AdId", false,
            User($@"{CV}\AdvertisingInfo", "Enabled", 0, 1),
            User($@"{CV}\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)),
        new("Suggestions", false,
            User($@"{CV}\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "SubscribedContent-353694Enabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "SubscribedContent-353696Enabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", 0, 1)),
        new("BackgroundApps", false,
            User($@"{CV}\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0)),
        new("WebSearch", false,
            User(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)),
        new("GameDvr", false,
            User(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
            User($@"{CV}\GameDVR", "AppCaptureEnabled", 0, 1)),
        new("StartupDelay", false,
            User($@"{CV}\Explorer\Serialize", "StartupDelayInMSec", 0, null)),
    };

    public static Tweak Get(string id) => Tweaks.First(t => t.Id == id);

    public bool IsApplied(Tweak t) => t.Values.All(v => Read(v) == v.Target);

    /// <summary>Applies or reverts a tweak; returns the exact prior values so Undo can restore them.</summary>
    public (ActionResult Result, List<(RegValue V, int? Old)> Snapshot) Set(Tweak t, bool enabled)
    {
        var snapshot = new List<(RegValue, int?)>();
        if (ViewOnlyMode.Block(t.Id) is { } viewOnly) return (viewOnly, snapshot);
        try
        {
            foreach (var v in t.Values)
            {
                snapshot.Add((v, Read(v)));
                Write(v, enabled ? v.Target : v.Default);
            }
            return (new ActionResult(true, enabled ? "Msg_TweakOn" : "Msg_TweakOff", t.Id), snapshot);
        }
        catch (Exception ex)
        {
            Restore(snapshot); // don't leave a half-applied tweak
            return (new ActionResult(false, "Msg_SecurityFailed", t.Id, ex.Message), snapshot);
        }
    }

    public ActionResult Restore(List<(RegValue V, int? Old)> snapshot)
    {
        if (ViewOnlyMode.Block("tweaks") is { } viewOnly) return viewOnly;
        try
        {
            foreach (var (v, old) in snapshot) Write(v, old);
            return new ActionResult(true, "Msg_TweakOff", "restore");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_SecurityFailed", "restore", ex.Message);
        }
    }

    private static RegistryKey Base(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    private static int? Read(RegValue v)
    {
        try
        {
            using var key = Base(v.Hive).OpenSubKey(v.Path);
            return key?.GetValue(v.Name) is int i ? i : null;
        }
        catch { return null; }
    }

    private static void Write(RegValue v, int? value)
    {
        using var root = Base(v.Hive);
        if (value is int i)
        {
            using var key = root.CreateSubKey(v.Path, writable: true);
            key.SetValue(v.Name, i, RegistryValueKind.DWord);
        }
        else
        {
            using var key = root.OpenSubKey(v.Path, writable: true);
            key?.DeleteValue(v.Name, throwOnMissingValue: false);
        }
    }
}
