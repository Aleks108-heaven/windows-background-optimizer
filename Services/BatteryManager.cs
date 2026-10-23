using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;

namespace BgOptimizer.Services;

/// <summary>
/// Read-only battery wear info (design vs full-charge capacity, live drain) plus an optional
/// "hibernate instead of sleep on lid close while on battery" toggle. Hibernate/S3 support
/// varies a lot on Modern-Standby-only laptops, so the toggle re-reads the setting after
/// writing it and reports failure instead of claiming success when the hardware ignores it.
/// </summary>
public class BatteryManager
{
    public record BatteryHealth(int? DesignMWh, int? FullChargeMWh, int? Percent, bool? Charging, int? RateMw);

    // Design and full-charge capacity change over months, not seconds, and each WMI read wakes the battery's
    // embedded controller; re-reading them on every refresh was pure cost. The live status is still read each time.
    private static readonly TimeSpan CapacityCacheFor = TimeSpan.FromMinutes(10);
    private readonly object _capacityGate = new();
    private (int? Design, int? Full, DateTime ReadAt)? _capacity;

    private (int? Design, int? Full) Capacity()
    {
        lock (_capacityGate)
        {
            if (_capacity is { } cached && DateTime.UtcNow - cached.ReadAt < CapacityCacheFor)
                return (cached.Design, cached.Full);

            var design = ReadWmiUInt(@"root\wmi", "BatteryStaticData", "DesignedCapacity");
            var full = ReadWmiUInt(@"root\wmi", "BatteryFullChargedCapacity", "FullChargedCapacity");
            _capacity = (design, full, DateTime.UtcNow);
            return (design, full);
        }
    }

    public BatteryHealth GetHealth()
    {
        var (design, full) = Capacity();

        bool? charging = null;
        int? rate = null;
        int? remaining = null;
        try
        {
            // Every WMI object is disposed: they wrap native COM objects that otherwise wait for the garbage collector.
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT Charging, ChargeRate, DischargeRate, RemainingCapacity FROM BatteryStatus");
            using var results = searcher.Get();
            foreach (ManagementBaseObject o in results)
            {
                using (o)
                {
                    charging = o["Charging"] as bool?;
                    rate = charging == true ? o["ChargeRate"] as uint? is uint c ? (int)c : null
                                             : o["DischargeRate"] as uint? is uint d ? (int)d : null;
                    remaining = o["RemainingCapacity"] as uint? is uint r ? (int)r : null;
                }
                break;
            }
        }
        catch { }

        // RemainingCapacity/FullChargedCapacity matches what Windows itself shows in the
        // taskbar; Win32_Battery.EstimatedChargeRemaining is a separate, coarser value that
        // can lag noticeably behind, so it's only a fallback if the precise reading fails.
        int? percent = remaining is int rem && full is int f && f > 0
            ? (int)Math.Round(rem * 100.0 / f, MidpointRounding.AwayFromZero)
            : ReadWmiUInt("root\\cimv2", "Win32_Battery", "EstimatedChargeRemaining");

        return new BatteryHealth(design, full, percent, charging, rate);
    }

    private static int? ReadWmiUInt(string ns, string wmiClass, string field)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(ns, $"SELECT {field} FROM {wmiClass}");
            using var results = searcher.Get();
            foreach (ManagementBaseObject o in results)
                using (o)
                    return o[field] switch { uint u => (int)u, ushort s => (int)s, _ => null };
            return null;
        }
        catch { return null; }
    }

    /// <returns>true/false = current DC lid-close action, null = setting not exposed on this hardware.</returns>
    public bool? IsHibernateOnLidClose()
    {
        var (code, output) = RunPowercfg("/q SCHEME_CURRENT SUB_BUTTONS LIDACTION");
        return code != 0 ? null : ParseDcLidAction(output);
    }

    /// <summary>
    /// powercfg's labels are localized, but its values are always 0x-prefixed hex and the DC
    /// ("on battery") index is printed last, after min/max/increment and the AC index. So the
    /// last hex value is read instead of matching the English "Current DC Power Setting Index".
    /// </summary>
    internal static bool? ParseDcLidAction(string powercfgOutput)
    {
        var hex = Regex.Matches(powercfgOutput, @"0x([0-9a-fA-F]{8})\b");
        // Needs at least the AC and DC indexes (plus min/max/increment) to be a real /q listing.
        if (hex.Count < 2) return null;
        return Convert.ToUInt32(hex[^1].Groups[1].Value, 16) == 2;
    }

    // On Modern Standby laptops, screen-off and idle both enter standby (light stays on) and the lid
    // action never applies to them; only "hibernate after" moves standby on to a real hibernate.
    // Left at "never" Windows eventually hibernates on its own battery-budget rule, hours later.
    internal const int HibernateAfterSeconds = 30 * 60;
    private uint? _previousHibernateAfter;

    /// <returns>The DC ("on battery") value of a powercfg setting, or null if it can't be read.</returns>
    private static uint? ReadDcIndex(string subgroup, string setting)
    {
        var (code, output) = RunPowercfg($"/q SCHEME_CURRENT {subgroup} {setting}");
        if (code != 0) return null;
        var hex = Regex.Matches(output, @"0x([0-9a-fA-F]{8})\b");
        return hex.Count < 2 ? null : Convert.ToUInt32(hex[^1].Groups[1].Value, 16);
    }

    public ActionResult SetHibernateOnLidClose(bool enabled)
    {
        if (ViewOnlyMode.Block("hibernate") is { } viewOnly) return viewOnly;
        RunPowercfg("-attributes SUB_BUTTONS LIDACTION -ATTRIB_HIDE"); // unhide; some OEM images hide it by default
        RunPowercfg("-attributes SUB_SLEEP HIBERNATEIDLE -ATTRIB_HIDE");

        if (enabled)
        {
            var (hibCode, hibOut) = RunPowercfg("/hibernate on");
            if (hibCode != 0)
                return new ActionResult(false, "Msg_SecurityFailed", "Hibernate", hibOut.Trim());

            RunPowercfg("/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 2");
            _previousHibernateAfter ??= ReadDcIndex("SUB_SLEEP", "HIBERNATEIDLE");
            RunPowercfg($"/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE {HibernateAfterSeconds}");
            RunPowercfg("/setactive SCHEME_CURRENT");

            return IsHibernateOnLidClose() == true
                ? new ActionResult(true, "Msg_HibernateOn")
                : new ActionResult(false, "Msg_HibernateUnsupported");
        }
        else
        {
            var (code, output) = RunPowercfg("/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1");
            RunPowercfg($"/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE {_previousHibernateAfter ?? 0}");
            _previousHibernateAfter = null;
            RunPowercfg("/setactive SCHEME_CURRENT");
            return code == 0
                ? new ActionResult(true, "Msg_HibernateOff")
                : new ActionResult(false, "Msg_SecurityFailed", "Hibernate", output.Trim());
        }
    }

    internal static (int Code, string Output) RunPowercfg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(SystemTools.Path("powercfg.exe"), args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
