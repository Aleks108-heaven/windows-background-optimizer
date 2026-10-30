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

        // Privacy and security, from the same families of settings WinHance groups under Privacy & Security.
        new("Feedback", true,
            new RegValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, null)),
        new("ErrorReporting", true,
            new RegValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, null)),
        new("StartTracking", false,
            User($@"{CV}\Explorer\Advanced", "Start_TrackProgs", 0, 1)),
        new("SuggestedApps", false,
            User($@"{CV}\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "PreInstalledAppsEnabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "OemPreInstalledAppsEnabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "ContentDeliveryAllowed", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "FeatureManagementEnabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "SoftLandingEnabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "SubscribedContentEnabled", 0, 1)),
        new("SettingsNags", false,
            User($@"{CV}\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0, 1),
            User($@"{CV}\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0, 1),
            User($@"{CV}\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1)),
        new("SearchHistory", false,
            User($@"{CV}\SearchSettings", "IsDeviceSearchHistoryEnabled", 0, 1),
            User($@"{CV}\SearchSettings", "IsDynamicSearchBoxEnabled", 0, 1),
            User($@"{CV}\SearchSettings", "IsMSACloudSearchEnabled", 0, 1),
            User($@"{CV}\SearchSettings", "IsAADCloudSearchEnabled", 0, 1)),
        // Mode 0 is "HTTP only": updates still download from Microsoft, but this PC no longer uploads them to other PCs.
        new("DeliveryOpt", true,
            new RegValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null)),

        // Explorer.
        new("ShowExtensions", false,
            User($@"{CV}\Explorer\Advanced", "HideFileExt", 0, 1)),
        new("QuickAccess", false,
            User($@"{CV}\Explorer", "ShowRecent", 0, 1),
            User($@"{CV}\Explorer", "ShowFrequent", 0, 1),
            User($@"{CV}\Explorer", "ShowCloudFilesInQuickAccess", 0, 1),
            User($@"{CV}\Explorer", "ShowRecommendations", 0, 1)),
        new("SyncAds", false,
            User($@"{CV}\Explorer\Advanced", "ShowSyncProviderNotifications", 0, 1)),

        // Security: stops this PC accepting Windows Remote Assistance invitations (Quick Assist and other tools are separate).
        new("RemoteAssist", true,
            new RegValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0, 1)),
    };

    /// <summary>The headings the Tweaks tab shows (Tw_Group_{name}) and which tweak sits under which, in order. Every tweak is in exactly one.</summary>
    public static readonly IReadOnlyList<(string Group, string[] Ids)> Groups = new (string, string[])[]
    {
        ("Privacy", new[] { "Telemetry", "AdId", "Feedback", "ErrorReporting", "StartTracking" }),
        ("Suggestions", new[] { "Suggestions", "SuggestedApps", "SettingsNags", "WebSearch", "SearchHistory" }),
        ("Background", new[] { "BackgroundApps", "GameDvr", "StartupDelay", "DeliveryOpt" }),
        ("Explorer", new[] { "ShowExtensions", "QuickAccess", "SyncAds" }),
        ("Security", new[] { "RemoteAssist" }),
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
