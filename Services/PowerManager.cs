using System.Collections.Concurrent;
using System.Globalization;
using BgOptimizer.Localization;

namespace BgOptimizer.Services;

/// <summary>
/// Reads and changes the active power plan's settings listed in <see cref="PowerCatalog"/>. Like the CPU speed limit
/// and the hibernate toggle, a change is read back and reported as failed (and put back) if Windows did not take it,
/// because some plans are locked by policy and some hardware ignores a setting. Everything that reaches powercfg is a
/// GUID from the catalog or an integer this class formatted itself; nothing the user types goes near a command line.
/// </summary>
internal sealed class PowerManager
{
    /// <summary>The values of every catalog setting the active plan has (keyed by setting id, plus "CpuMax"), and the plan's name.</summary>
    internal sealed record PowerReading(string? PlanName, IReadOnlyDictionary<string, (int Ac, int Dc)> Values);

    public PowerReading Read()
    {
        // One small query per setting, four at a time: about 40 ms each, a quarter of a second in all. A single dump of
        // the whole plan (/qh SCHEME_CURRENT) is 80 KB and takes about 1.8 s, because the processor group alone has
        // dozens of settings. Measured on the ASUS Vivobook S 15 this was written for; slower CPUs gain more.
        var found = new ConcurrentDictionary<string, (int Ac, int Dc)>();
        Parallel.ForEach(PowerCatalog.Settings.Append(PowerCatalog.CpuMax), new ParallelOptions { MaxDegreeOfParallelism = 4 }, setting =>
        {
            if (ReadOne(setting) is { } pair) found[setting.Id] = pair;
        });

        var (_, active) = BatteryManager.RunPowercfg("/getactivescheme");
        return new PowerReading(PowerCatalog.ParsePlanName(active), new Dictionary<string, (int Ac, int Dc)>(found));
    }

    /// <summary>A value as the window shows it: "Never", "15 min", "Hibernate", "50%". A value that is not one of the choices is shown as its number.</summary>
    public static string FormatValue(PowerSetting setting, int value) => setting.Kind switch
    {
        PowerValueKind.Seconds => PowerCatalog.FormatSeconds(value, Loc.Instance["Pw_Never"], Loc.Instance["Pw_Min"], Loc.Instance["Pw_Hour"]),
        PowerValueKind.Percent => $"{value}%",
        _ => setting.Choices!.FirstOrDefault(c => c.Value == value).LabelKey is { } key
            ? Loc.Instance[key]
            : value.ToString(CultureInfo.CurrentCulture),
    };

    // /qh, not /q: it also lists the settings Windows hides (boost mode, energy preference, cooling policy).
    private static (int Ac, int Dc)? ReadOne(PowerSetting setting) =>
        PowerCatalog.ParseIndexes(BatteryManager.RunPowercfg("/qh SCHEME_CURRENT " + setting.Subgroup + " " + setting.Guid).Output,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { setting.Guid })
            is { Count: > 0 } found && found.TryGetValue(setting.Guid, out var pair) ? pair : null;

    /// <param name="restoring">Putting back values that were already there (Undo): those may be ones this app would not offer.</param>
    public ActionResult Set(PowerSetting setting, int ac, int dc, bool restoring = false)
    {
        var name = Loc.Instance["Pw_" + setting.Id];
        if (ViewOnlyMode.Block(name) is { } viewOnly) return viewOnly;
        if (!PowerCatalog.IsValid(setting, ac, restoring) || !PowerCatalog.IsValid(setting, dc, restoring))
            return new ActionResult(false, "Msg_SecurityFailed", name, "out of range");

        // The minimum speed cannot be above the maximum: Windows would then hold the CPU at the maximum even when idle.
        if (setting.Id == "CpuMin" && !restoring && ReadOne(PowerCatalog.CpuMax) is { } max && (ac > max.Ac || dc > max.Dc))
            return new ActionResult(false, "Msg_PowerMinAboveMax", max.Ac, max.Dc);

        var before = ReadOne(setting);
        Write(setting, ac, dc);
        var after = ReadOne(setting);
        if (after == (ac, dc))
        {
            ActionLog.Write($"POWER_SET {setting.Id} ac={ac} dc={dc} (was {(before is { } b ? $"{b.Ac}/{b.Dc}" : "unknown")})");
            return new ActionResult(true, "Msg_PowerSet", name, FormatValue(setting, ac), FormatValue(setting, dc));
        }

        // Did not stick (a plan locked by policy, or a setting the hardware ignores): put back what was there.
        if (before is { } previous && after != previous) Write(setting, previous.Ac, previous.Dc);
        return new ActionResult(false, "Msg_SecurityFailed", name, "Windows did not accept the new value");
    }

    private static void Write(PowerSetting setting, int ac, int dc)
    {
        var target = $"SCHEME_CURRENT {setting.Subgroup} {setting.Guid}"; // constants from the catalog
        BatteryManager.RunPowercfg($"/setacvalueindex {target} {ac.ToString(CultureInfo.InvariantCulture)}");
        BatteryManager.RunPowercfg($"/setdcvalueindex {target} {dc.ToString(CultureInfo.InvariantCulture)}");
        BatteryManager.RunPowercfg("/setactive SCHEME_CURRENT"); // a changed index only applies once the plan is re-activated
    }
}
