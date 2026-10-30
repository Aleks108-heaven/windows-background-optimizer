using System.Globalization;
using System.Text.RegularExpressions;

namespace BgOptimizer.Services;

internal enum PowerValueKind
{
    /// <summary>A fixed list of meanings (Disabled/Enabled, Sleep/Hibernate, ...).</summary>
    Choice,
    /// <summary>A time in seconds; 0 means "never".</summary>
    Seconds,
    /// <summary>A whole percentage, 0 to 100.</summary>
    Percent,
}

/// <param name="Id">Names the setting everywhere: its title is Pw_{Id} and its hint Pw_{Id}_d.</param>
/// <param name="Group">Pw_Group_{Group}: the heading the row sits under.</param>
/// <param name="Subgroup">The power setting subgroup, as a GUID (never an alias: only constants reach powercfg's command line).</param>
/// <param name="Guid">The power setting itself.</param>
/// <param name="Choices">For <see cref="PowerValueKind.Choice"/>: each allowed value with the translation key of its label.</param>
/// <param name="Presets">For seconds and percentages: the values offered. The value already in the plan is always shown too, so nothing is hidden.</param>
/// <param name="Advanced">Tucked behind "Show advanced settings": rarely needed, or specific to some hardware.</param>
internal sealed record PowerSetting(
    string Id, string Group, string Subgroup, string Guid, PowerValueKind Kind,
    IReadOnlyList<(int Value, string LabelKey)>? Choices = null,
    IReadOnlyList<int>? Presets = null,
    bool Advanced = false);

/// <summary>
/// The power-plan settings this app offers, a curated subset of what powercfg has (see WinHance's Power page for the
/// long list). They are the ones that decide heat, fan noise, battery life and how often a laptop sleeps and wakes.
/// Each is changed for the active plan only, plugged in and on battery separately, read back after writing, and
/// reversible with Undo. Nothing here is a fan controller: fan speed belongs to the laptop's firmware.
/// </summary>
internal static class PowerCatalog
{
    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string SubButtons = "4f971e89-eebd-4455-a8de-9e59040e7347";
    private const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SubWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";

    /// <summary>The CPU speed limit has its own control (see <see cref="CoolingManager"/>); the table only reads it, to keep the minimum below it.</summary>
    internal static readonly PowerSetting CpuMax = new("CpuMax", "Cpu", SubProcessor, "bc5038f7-23e0-4960-96da-33abaf5935ec", PowerValueKind.Percent);

    private static readonly int[] Timeouts = { 0, 60, 120, 180, 300, 600, 900, 1200, 1800, 3600 };

    private static readonly (int, string)[] ButtonActions =
    {
        (0, "Pw_Act_Nothing"), (1, "Pw_Act_Sleep"), (2, "Pw_Act_Hibernate"), (3, "Pw_Act_Shutdown"),
    };

    public static readonly IReadOnlyList<PowerSetting> Settings = new PowerSetting[]
    {
        new("Display", "Screen", SubVideo, "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", PowerValueKind.Seconds, Presets: Timeouts),
        new("Sleep", "Screen", SubSleep, "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", PowerValueKind.Seconds, Presets: Timeouts),
        new("HibernateAfter", "Screen", SubSleep, "9d7815a6-7ee4-497e-8888-515a05f02364", PowerValueKind.Seconds,
            Presets: new[] { 0, 900, 1800, 3600, 7200, 14400 }),
        new("WakeTimers", "Screen", SubSleep, "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d", PowerValueKind.Choice,
            Choices: new[] { (0, "Pw_Opt_Disable"), (1, "Pw_Opt_Enable"), (2, "Pw_Opt_ImportantOnly") }),

        new("LidClose", "Lid", SubButtons, "5ca83367-6e45-459f-a27b-476b1d01c936", PowerValueKind.Choice, Choices: ButtonActions),
        new("PowerButton", "Lid", SubButtons, "7648efa3-dd9c-4e3e-b566-50f929386280", PowerValueKind.Choice, Choices: ButtonActions),

        new("CpuMin", "Cpu", SubProcessor, "893dee8e-2bef-41e0-89c6-b55d0929964c", PowerValueKind.Percent,
            Presets: new[] { 0, 5, 10, 20, 30, 50, 70, 100 }),

        new("UsbSuspend", "Devices", SubUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", PowerValueKind.Choice,
            Choices: new[] { (0, "Pw_Opt_Disabled"), (1, "Pw_Opt_Enabled") }, Advanced: true),
        new("WifiPower", "Devices", SubWireless, "12bbebe6-58d6-4636-95bb-3217ef867c1a", PowerValueKind.Choice,
            Choices: new[] { (0, "Pw_Wifi_Max"), (1, "Pw_Wifi_Low"), (2, "Pw_Wifi_Medium"), (3, "Pw_Wifi_Saving") }, Advanced: true),
        new("Aspm", "Devices", SubPcie, "ee12f906-d277-404b-b6da-e5fa1a576df5", PowerValueKind.Choice,
            Choices: new[] { (0, "Pw_Opt_Off"), (1, "Pw_Aspm_Moderate"), (2, "Pw_Aspm_Maximum") }, Advanced: true),

        new("Boost", "Cpu", SubProcessor, "be337238-0d82-4146-a960-4f3749d470c7", PowerValueKind.Choice,
            Choices: new[] { (0, "Pw_Opt_Disabled"), (1, "Pw_Opt_Enabled"), (2, "Pw_Boost_Aggressive"), (3, "Pw_Boost_Efficient"), (4, "Pw_Boost_EfficientAggressive") },
            Advanced: true),
        new("Epp", "Cpu", SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", PowerValueKind.Percent,
            Presets: new[] { 0, 25, 50, 75, 100 }, Advanced: true),
        new("CoolPolicy", "Cpu", SubProcessor, "94d3a615-a899-4ac5-ae2b-e4d8f634367f", PowerValueKind.Choice,
            Choices: new[] { (0, "Pw_Cool_Passive"), (1, "Pw_Cool_Active") }, Advanced: true),
    };

    /// <summary>The order the groups appear in.</summary>
    public static readonly string[] Groups = { "Screen", "Lid", "Cpu", "Devices" };

    public static PowerSetting Get(string id) => Settings.First(s => s.Id == id);

    /// <summary>Every GUID the tab reads, including the CPU limit (needed to keep the minimum under it).</summary>
    public static IReadOnlySet<string> ReadGuids { get; } =
        Settings.Select(s => s.Guid).Append(CpuMax.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---------- Validation: only these ever reach powercfg ----------

    /// <summary>
    /// Whether a value may be written. Offered values only, except when putting back what was there before
    /// (<paramref name="restoring"/>), which may be something this app would not offer, e.g. 3 minutes. Always a plain int.
    /// </summary>
    public static bool IsValid(PowerSetting s, int value, bool restoring = false)
    {
        if (value < 0) return false;
        if (restoring) return true;
        return s.Kind switch
        {
            PowerValueKind.Choice => s.Choices!.Any(c => c.Value == value),
            PowerValueKind.Percent => value <= 100,
            PowerValueKind.Seconds => value <= 24 * 3600,
            _ => false,
        };
    }

    // ---------- Reading powercfg's output ----------

    private static readonly Regex GuidInLine = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);

    private static readonly Regex HexIndex = new(@"0x([0-9a-fA-F]{8})\b", RegexOptions.Compiled);

    /// <summary>
    /// Reads the plugged-in and on-battery value of each wanted setting out of <c>powercfg /qh</c>. powercfg's labels
    /// are translated but GUIDs and its 0x-prefixed hex values are not, so a setting is found by its GUID and its two
    /// values are the last two hex numbers before the next GUID (the list of possible values uses plain digits).
    /// </summary>
    internal static Dictionary<string, (int Ac, int Dc)> ParseIndexes(string powercfgOutput, IReadOnlySet<string> wantedGuids)
    {
        var found = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var values = new List<uint>();

        void Flush()
        {
            if (current is not null && values.Count >= 2)
                found[current] = (ToInt(values[^2]), ToInt(values[^1]));
            current = null;
            values.Clear();
        }

        foreach (var line in powercfgOutput.Split('\n'))
        {
            var guid = GuidInLine.Match(line);
            if (guid.Success)
            {
                Flush();
                current = wantedGuids.Contains(guid.Value) ? guid.Value.ToLowerInvariant() : null;
                continue;
            }
            if (current is null) continue;
            foreach (Match hex in HexIndex.Matches(line))
                values.Add(uint.Parse(hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        Flush();
        return found;
    }

    private static int ToInt(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    /// <summary>The plan's name from <c>powercfg /getactivescheme</c>: the text in the last parentheses, or null.</summary>
    internal static string? ParsePlanName(string getActiveSchemeOutput)
    {
        var m = Regex.Match(getActiveSchemeOutput, @"\(([^()\r\n]+)\)\s*\*?\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ---------- Showing values ----------

    /// <summary>"3 min", "1 h" and "Never" for a time in seconds; the words come from translations passed in.</summary>
    internal static string FormatSeconds(int seconds, string never, string minutesFormat, string hoursFormat)
    {
        if (seconds <= 0) return never;
        if (seconds % 3600 == 0) return string.Format(CultureInfo.CurrentCulture, hoursFormat, seconds / 3600);
        if (seconds % 60 == 0) return string.Format(CultureInfo.CurrentCulture, minutesFormat, seconds / 60);
        return string.Format(CultureInfo.CurrentCulture, minutesFormat, Math.Round(seconds / 60.0, 1));
    }

    // ---------- Advice ----------

    /// <summary>One suggestion: a translation key and the numbers it quotes.</summary>
    internal sealed record PowerAdvice(string Key, params object[] Args);

    /// <summary>
    /// Observations about the current values, worded as suggestions. They only look at what is set; none is a
    /// diagnosis, and most laptops are fine with every one of these values. The minimum processor state is the
    /// one that matters most: it is the floor of the CPU's speed, so a high value keeps it fast, hot and loud while idle.
    /// </summary>
    internal static IReadOnlyList<PowerAdvice> Advise(IReadOnlyDictionary<string, (int Ac, int Dc)> values)
    {
        var advice = new List<PowerAdvice>();

        if (values.TryGetValue("CpuMin", out var min))
        {
            if (min.Ac >= 50 || min.Dc >= 50)
                advice.Add(new PowerAdvice("Pw_AdviceMinCpu", min.Ac, min.Dc));
            if (values.TryGetValue("CpuMax", out var max) && (min.Ac > max.Ac || min.Dc > max.Dc))
                advice.Add(new PowerAdvice("Pw_AdviceMinAboveMax", min.Ac, min.Dc, max.Ac, max.Dc));
        }

        // Plugged in, asking for maximum performance (preference 0) and aggressive boost (2) at once is the hottest and loudest pairing.
        if (values.TryGetValue("Epp", out var epp) && epp.Ac <= 10
            && values.TryGetValue("Boost", out var boost) && boost.Ac == 2)
            advice.Add(new PowerAdvice("Pw_AdvicePerf", epp.Ac));

        // Five minutes or less while plugged in: a sleep and a wake many times a day.
        if (values.TryGetValue("Sleep", out var sleep) && sleep.Ac is > 0 and <= 300)
            advice.Add(new PowerAdvice("Pw_AdviceSleep", sleep.Ac / 60));

        if (values.TryGetValue("WakeTimers", out var wake) && (wake.Ac == 1 || wake.Dc == 1))
            advice.Add(new PowerAdvice("Pw_AdviceWake"));

        return advice;
    }
}
