using System.Globalization;
using System.IO;
using System.Text.Json;
using BgOptimizer.Localization;

namespace BgOptimizer.Services;

/// <summary>
/// The user's checkbox/threshold/language choices, kept in %LocalAppData%\BgOptimizer\settings.json so they
/// survive a restart. Deliberately absent: "Allow throttling interactive apps" (a caution override that
/// should start off every launch) and the priority dropdown. Loading never fails: a missing, unreadable or
/// hand-edited file falls back to defaults field by field, and saving never throws.
/// </summary>
internal sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public bool AutoOptimize { get; set; }
    public string FreeRamThresholdGb { get; set; } = "2";
    public bool AllowSuspend { get; set; }
    public bool AutoRestorePoint { get; set; } = true;
    public bool PersistPriority { get; set; }
    /// <summary>The app only watches and reports; see <see cref="ViewOnlyMode"/>.</summary>
    public bool ViewOnly { get; set; }
    /// <summary>Hash of the code that must be entered to turn view-only mode off; null means no code is set. See <see cref="ViewOnlyLock"/>.</summary>
    public string? ViewOnlyCodeHash { get; set; }
    /// <summary>Notify (log, and a tray balloon while hidden) when a load stays above <see cref="LoadLimitPercent"/>.</summary>
    public bool LoadAlert { get; set; }
    public int LoadLimitPercent { get; set; } = DefaultLoadLimit;

    public const int MinLoadLimit = 50;
    public const int MaxLoadLimit = 100;
    public const int DefaultLoadLimit = 90;

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgOptimizer", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new AppSettings();
            return Sanitize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings());
        }
        catch { return new AppSettings(); }
    }

    /// <summary>Written to a temp file first, so a crash mid-write can't leave a half-written settings file.</summary>
    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!SafeAppData.IsSafeToWrite(path) || !SafeAppData.IsSafeToWrite(path + ".tmp")) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch { /* losing a preference must never break the app */ }
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        if (!Loc.Languages.Any(l => l.Code == s.Language)) s.Language = "en";
        // Same invariant parsing as the Auto-Optimize tick (accepts "2,5" too): anything unusable means the default.
        var text = (s.FreeRamThresholdGb ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || gb <= 0 || gb > 1024)
            s.FreeRamThresholdGb = "2";
        // A value this app can't check would lock view-only mode forever, so it counts as "no code".
        if (!ViewOnlyLock.IsValid(s.ViewOnlyCodeHash)) s.ViewOnlyCodeHash = null;
        // A hand-edited limit outside the slider's range would put the limit marker off the bar.
        s.LoadLimitPercent = Math.Clamp(s.LoadLimitPercent, MinLoadLimit, MaxLoadLimit);
        return s;
    }
}
