using System.Text.RegularExpressions;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The power settings catalog: every entry must be well formed, because its GUIDs and values are what reach powercfg.</summary>
public class PowerCatalogTests
{
    private static readonly Regex GuidShape = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled);

    [Fact]
    public void IdsAreUniqueAndEveryGuidIsWellFormedLowercase()
    {
        Assert.Equal(PowerCatalog.Settings.Count, PowerCatalog.Settings.Select(s => s.Id).Distinct().Count());
        foreach (var s in PowerCatalog.Settings.Append(PowerCatalog.CpuMax))
        {
            Assert.Matches(GuidShape, s.Subgroup);
            Assert.Matches(GuidShape, s.Guid);
        }
    }

    [Fact]
    public void EachKindHasWhatItNeedsAndEveryOfferedValueIsValid()
    {
        foreach (var s in PowerCatalog.Settings)
        {
            Assert.Contains(s.Group, PowerCatalog.Groups);
            if (s.Kind == PowerValueKind.Choice)
            {
                Assert.NotEmpty(s.Choices!);
                Assert.Equal(s.Choices!.Count, s.Choices.Select(c => c.Value).Distinct().Count());
                Assert.All(s.Choices, c => Assert.True(PowerCatalog.IsValid(s, c.Value), $"{s.Id}: {c.Value}"));
            }
            else
            {
                Assert.NotEmpty(s.Presets!);
                Assert.All(s.Presets!, v => Assert.True(PowerCatalog.IsValid(s, v), $"{s.Id}: {v}"));
            }
        }
    }

    [Fact]
    public void TheCpuSpeedLimitIsReadButHasNoRowOfItsOwn()
    {
        Assert.DoesNotContain(PowerCatalog.Settings, s => s.Id == "CpuMax"); // CoolingManager owns it, with its floor
        Assert.Contains(PowerCatalog.CpuMax.Guid, PowerCatalog.ReadGuids);
    }

    [Theory]
    [InlineData("LidClose", 3, true)]
    [InlineData("LidClose", 4, false)]   // not one of the four actions
    [InlineData("LidClose", -1, false)]
    [InlineData("CpuMin", 100, true)]
    [InlineData("CpuMin", 101, false)]
    [InlineData("Sleep", 86400, true)]
    [InlineData("Sleep", 86401, false)]
    [InlineData("WakeTimers", 2, true)]
    [InlineData("WakeTimers", 3, false)]
    public void OnlySensibleValuesAreAccepted(string id, int value, bool expected) =>
        Assert.Equal(expected, PowerCatalog.IsValid(PowerCatalog.Get(id), value));

    [Fact]
    public void PuttingBackAnEarlierValueMayBeOneTheAppWouldNotOffer()
    {
        var sleep = PowerCatalog.Get("Sleep");
        Assert.True(PowerCatalog.IsValid(sleep, 7 * 3600, restoring: true));
        Assert.False(PowerCatalog.IsValid(sleep, -1, restoring: true)); // never negative, never anything but a plain number
        Assert.True(PowerCatalog.IsValid(PowerCatalog.Get("LidClose"), 9, restoring: true));
    }

    [Fact]
    public void SecondsAreWrittenAsNeverMinutesAndHours()
    {
        string F(int s) => PowerCatalog.FormatSeconds(s, "Never", "{0} min", "{0} h");
        Assert.Equal("Never", F(0));
        Assert.Equal("3 min", F(180));
        Assert.Equal("1 h", F(3600));
        Assert.Equal("90 min", F(5400));
        Assert.Matches(@"^1[.,]5 min$", F(90)); // not a whole minute: shown, never rounded away
    }
}

/// <summary>Reading powercfg's text: found by GUID and by 0x values, so the Windows display language does not matter.</summary>
public class PowerParsingTests
{
    // Labels in English, names in Ukrainian: exactly what this app sees on a Ukrainian Windows.
    private const string Dump = """
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Збалансований)
          GUID Alias: SCHEME_BALANCED
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Керування живленням процесора)
            GUID Alias: SUB_PROCESSOR
            Power Setting GUID: 893dee8e-2bef-41e0-89c6-b55d0929964c  (Мінімальна швидкодія процесора)
              GUID Alias: PROCTHROTTLEMIN
              Minimum Possible Setting: 0x00000000
              Maximum Possible Setting: 0x00000064
              Possible Settings increment: 0x00000001
              Possible Settings units: %
            Current AC Power Setting Index: 0x00000064
            Current DC Power Setting Index: 0x00000005

            Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Режим підвищеної продуктивності процесора)
              GUID Alias: PERFBOOSTMODE
              Possible Setting Index: 000
              Possible Setting Friendly Name: Вимкнуто
              Possible Setting Index: 001
              Possible Setting Friendly Name: Увімкнуто
              Possible Setting Index: 002
              Possible Setting Friendly Name: Агресивний
            Current AC Power Setting Index: 0x00000002
            Current DC Power Setting Index: 0x00000001

            Power Setting GUID: 00000000-1111-2222-3333-444444444444  (A setting nobody asked for)
            Current AC Power Setting Index: 0x00000009
            Current DC Power Setting Index: 0x00000009
          Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Sleep)
            Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Sleep after)
              Minimum Possible Setting: 0x00000000
              Maximum Possible Setting: 0xffffffff
              Possible Settings increment: 0x00000001
            Current AC Power Setting Index: 0x000000b4
            Current DC Power Setting Index: 0x00000384
        """;

    private static IReadOnlySet<string> Wanted(params string[] ids) =>
        ids.Select(id => PowerCatalog.Get(id).Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ValuesAreFoundByGuidAndAreTheLastTwoHexNumbersOfEachBlock()
    {
        var found = PowerCatalog.ParseIndexes(Dump, Wanted("CpuMin", "Boost", "Sleep"));

        Assert.Equal((100, 5), found[PowerCatalog.Get("CpuMin").Guid]);     // min/max/increment come first and are skipped
        Assert.Equal((2, 1), found[PowerCatalog.Get("Boost").Guid]);        // the list of possible values uses plain digits
        Assert.Equal((180, 900), found[PowerCatalog.Get("Sleep").Guid]);
        Assert.Equal(3, found.Count);                                       // the setting nobody asked for is not returned
    }

    [Fact]
    public void ALabelInAnotherLanguageMakesNoDifference()
    {
        const string german = """
            GUID des Energiesparplans: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)
              Untergruppen-GUID: 54533251-82be-4824-96c1-47b60b740d00  (Prozessorenergieverwaltung)
                GUID der Energieeinstellung: 893dee8e-2bef-41e0-89c6-b55d0929964c  (Minimaler Prozessorzustand)
                  Kleinster möglicher Wert: 0x00000000
                  Größter möglicher Wert: 0x00000064
                  Inkrement: 0x00000001
                Aktueller Wechselstromeinstellungsindex: 0x00000032
                Aktueller Gleichstromeinstellungsindex: 0x0000000a
            """;

        var found = PowerCatalog.ParseIndexes(german, Wanted("CpuMin"));

        Assert.Equal((50, 10), found[PowerCatalog.Get("CpuMin").Guid]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("The power setting does not exist.")]
    [InlineData("Power Setting GUID: 893dee8e-2bef-41e0-89c6-b55d0929964c  (x)\r\n  Current AC Power Setting Index: 0x00000001")] // one value is not a pair
    public void AnythingThatIsNotAFullListingGivesNothing(string text) =>
        Assert.Empty(PowerCatalog.ParseIndexes(text, Wanted("CpuMin")));

    [Fact]
    public void AValueTooBigForAnIntIsCappedNotWrapped()
    {
        const string big = "Power Setting GUID: 893dee8e-2bef-41e0-89c6-b55d0929964c  (x)\n 0xffffffff\n 0xffffffff";
        Assert.Equal((int.MaxValue, int.MaxValue), PowerCatalog.ParseIndexes(big, Wanted("CpuMin"))[PowerCatalog.Get("CpuMin").Guid]);
    }

    [Theory]
    [InlineData("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)", "Balanced")]
    [InlineData("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Збалансований) *", "Збалансований")]
    [InlineData("no plan here", null)]
    [InlineData("", null)]
    public void ThePlanNameIsTheTextInTheLastParentheses(string output, string? expected) =>
        Assert.Equal(expected, PowerCatalog.ParsePlanName(output));
}

/// <summary>The suggestions only describe what is set; each rule is checked on its own.</summary>
public class PowerAdviceTests
{
    private static Dictionary<string, (int, int)> Values(params (string Id, int Ac, int Dc)[] v) =>
        v.ToDictionary(x => x.Id, x => (x.Ac, x.Dc));

    [Fact]
    public void ACpuThatCannotIdleDownIsFlaggedWithBothValues()
    {
        var advice = PowerCatalog.Advise(Values(("CpuMin", 100, 5)));

        var single = Assert.Single(advice);
        Assert.Equal("Pw_AdviceMinCpu", single.Key);
        Assert.Equal(new object[] { 100, 5 }, single.Args);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(10, 20)]
    [InlineData(49, 49)]
    public void AnOrdinaryMinimumIsNotFlagged(int ac, int dc) =>
        Assert.Empty(PowerCatalog.Advise(Values(("CpuMin", ac, dc))));

    [Fact]
    public void AMinimumAboveTheSpeedLimitIsFlaggedSeparately()
    {
        var advice = PowerCatalog.Advise(Values(("CpuMin", 100, 5), ("CpuMax", 80, 100)));

        Assert.Contains(advice, a => a.Key == "Pw_AdviceMinAboveMax" && a.Args.SequenceEqual(new object[] { 100, 5, 80, 100 }));
    }

    [Theory]
    [InlineData(180, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(900, false)]
    [InlineData(0, false)]   // never
    public void ASleepTimeOfFiveMinutesOrLessWhilePluggedInIsFlagged(int seconds, bool flagged) =>
        Assert.Equal(flagged, PowerCatalog.Advise(Values(("Sleep", seconds, 900))).Any(a => a.Key == "Pw_AdviceSleep"));

    [Theory]
    [InlineData(0, 2, true)]     // maximum performance and aggressive boost, plugged in
    [InlineData(10, 2, true)]
    [InlineData(11, 2, false)]
    [InlineData(0, 1, false)]    // boost only "enabled"
    [InlineData(50, 4, false)]
    public void MaximumPerformanceWithAggressiveBoostWhilePluggedInIsFlagged(int epp, int boost, bool flagged)
    {
        var advice = PowerCatalog.Advise(Values(("Epp", epp, 50), ("Boost", boost, 1)));

        Assert.Equal(flagged, advice.Any(a => a.Key == "Pw_AdvicePerf" && a.Args.SequenceEqual(new object[] { epp })));
    }

    [Fact]
    public void OnlyThePluggedInSideOfTheBoostPairIsJudged() =>
        Assert.Empty(PowerCatalog.Advise(Values(("Epp", 50, 0), ("Boost", 1, 2))));

    [Fact]
    public void TheOnBatterySleepTimeIsNotJudged() =>
        Assert.Empty(PowerCatalog.Advise(Values(("Sleep", 900, 60))));

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)]   // important wake timers only
    public void WakeTimersFlagOnlyWhenFullyAllowed(int ac, int dc, bool flagged) =>
        Assert.Equal(flagged, PowerCatalog.Advise(Values(("WakeTimers", ac, dc))).Any(a => a.Key == "Pw_AdviceWake"));

    [Fact]
    public void NothingReadMeansNothingToSay() =>
        Assert.Empty(PowerCatalog.Advise(new Dictionary<string, (int, int)>()));
}

/// <summary>The part of PowerManager that decides before touching powercfg. Nothing here may change the real power plan.</summary>
[Collection("ViewOnly")]
public class PowerManagerTests : IDisposable
{
    public PowerManagerTests() => ViewOnlyMode.Enabled = false;
    public void Dispose() => ViewOnlyMode.Enabled = false;

    [Fact]
    public void ViewOnlyModeRefusesBeforeAnythingIsRun()
    {
        ViewOnlyMode.Enabled = true;

        var result = new PowerManager().Set(PowerCatalog.Get("Sleep"), 900, 900);

        Assert.False(result.Ok);
        Assert.Equal("Msg_ViewOnlyBlocked", result.MessageKey);
    }

    [Theory]
    [InlineData("Sleep", -5, 0)]
    [InlineData("LidClose", 9, 1)]
    [InlineData("CpuMin", 101, 5)]
    [InlineData("WakeTimers", 0, 7)]
    public void AValueOutsideTheOfferedOnesIsRefusedBeforeAnythingIsRun(string id, int ac, int dc)
    {
        var result = new PowerManager().Set(PowerCatalog.Get(id), ac, dc);

        Assert.False(result.Ok);
        Assert.Equal("Msg_SecurityFailed", result.MessageKey);
    }

    [Fact]
    public void ValuesAreShownAsWordsAndNumbers()
    {
        Assert.Equal("Never", PowerManager.FormatValue(PowerCatalog.Get("Sleep"), 0));
        Assert.Equal("15 min", PowerManager.FormatValue(PowerCatalog.Get("Sleep"), 900));
        Assert.Equal("Hibernate", PowerManager.FormatValue(PowerCatalog.Get("LidClose"), 2));
        Assert.Equal("5%", PowerManager.FormatValue(PowerCatalog.Get("CpuMin"), 5));
        Assert.Equal("9", PowerManager.FormatValue(PowerCatalog.Get("LidClose"), 9)); // not a known choice: the number, never a blank
    }

    [Fact]
    public void ReadingTheRealPlanOnlyReadsAndNeverThrows()
    {
        // Runs against this machine's active plan; a PC without a setting just does not list it.
        var reading = new PowerManager().Read();

        var known = PowerCatalog.Settings.Select(s => s.Id).Append("CpuMax").ToHashSet();
        Assert.All(reading.Values.Keys, id => Assert.Contains(id, known));
        Assert.All(reading.Values.Values, v => Assert.True(v.Ac >= 0 && v.Dc >= 0));
    }
}
