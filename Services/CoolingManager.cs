using System.Globalization;
using System.Text.RegularExpressions;
using BgOptimizer.Localization;

namespace BgOptimizer.Services;

/// <summary>
/// The one cooling lever that is safe on any laptop: the current power plan's "Maximum processor state", which
/// caps how fast the CPU may run when plugged in and on battery. A lower cap means less heat and a quieter fan.
/// Deliberately not a fan controller: fan speed belongs to the laptop's firmware and embedded controller, Windows
/// has no supported interface for it, and reaching it from user mode needs a kernel driver (such drivers have a
/// record of being abused to take over the machine), which an app that runs as administrator should not ship.
/// Like the hibernate toggle, a change is read back afterwards and reported as failed if it did not stick.
/// </summary>
internal sealed class CoolingManager
{
    /// <summary>Below half speed a laptop feels broken, and nobody setting this wants that.</summary>
    public const int MinimumLimit = 50;
    public const int NoLimit = 100;

    /// <summary>The choices offered in the window; 100 means "no limit".</summary>
    public static readonly int[] Presets = { 100, 90, 80, 70, 60 };

    private const string Setting = "SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX";

    /// <returns>The plugged-in and on-battery caps of the current plan, or null if this plan does not expose the setting.</returns>
    public (int Ac, int Dc)? ReadLimit()
    {
        var (code, output) = BatteryManager.RunPowercfg("/q " + Setting);
        return code != 0 ? null : ParseLimit(output);
    }

    /// <summary>
    /// powercfg's labels are localized but its values are 0x-prefixed hex, listed as minimum, maximum, increment,
    /// then the AC index and the DC index. So the last two hex values are read, the same way the lid-action
    /// parser does it, and anything that is not a percentage is refused.
    /// </summary>
    internal static (int Ac, int Dc)? ParseLimit(string powercfgOutput)
    {
        var hex = Regex.Matches(powercfgOutput, @"0x([0-9a-fA-F]{8})\b");
        if (hex.Count < 5) return null; // min, max, increment, AC, DC
        var ac = Convert.ToUInt32(hex[^2].Groups[1].Value, 16);
        var dc = Convert.ToUInt32(hex[^1].Groups[1].Value, 16);
        return ac <= 100 && dc <= 100 ? ((int)ac, (int)dc) : null;
    }

    public ActionResult SetLimit(int percent) => SetLimit(percent, percent);

    /// <param name="restoring">Putting back values that were already there (Undo): those may be below the usual floor, e.g. a
    /// power-saver plan, and must be accepted as they were. Still blocked in view-only mode, like any change to the plan.</param>
    public ActionResult SetLimit(int ac, int dc, bool restoring = false)
    {
        if (ViewOnlyMode.Block("cooling") is { } viewOnly) return viewOnly;
        var name = Loc.Instance["Set_CoolLimit"].TrimEnd(':', ' ');
        var floor = restoring ? 0 : MinimumLimit;
        if (ac < floor || ac > NoLimit || dc < floor || dc > NoLimit)
            return new ActionResult(false, "Msg_SecurityFailed", name, "out of range");

        var before = ReadLimit();
        Write(ac, dc);
        var after = ReadLimit();
        if (after == (ac, dc))
            return new ActionResult(true, "Msg_CoolSet", ac == dc ? ac.ToString(CultureInfo.CurrentCulture) : $"{ac} / {dc}");

        // Did not stick (a plan locked by policy, or a setting the hardware ignores): put back what was there.
        if (before is { } previous && after != previous) Write(previous.Ac, previous.Dc);
        return new ActionResult(false, "Msg_SecurityFailed", name, "Windows did not accept the new value");
    }

    private static void Write(int ac, int dc)
    {
        // Integers only: nothing the user typed ever reaches the command line.
        BatteryManager.RunPowercfg($"/setacvalueindex {Setting} {ac.ToString(CultureInfo.InvariantCulture)}");
        BatteryManager.RunPowercfg($"/setdcvalueindex {Setting} {dc.ToString(CultureInfo.InvariantCulture)}");
        BatteryManager.RunPowercfg("/setactive SCHEME_CURRENT"); // a changed index only applies once the plan is re-activated
    }
}
