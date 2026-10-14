using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>Loads the real Theme.xaml and checks WCAG contrast for the colour pairs the UI actually uses.</summary>
public class ThemeContrastTests
{
    private record Swatch(byte R, byte G, byte B, double Opacity);

    private sealed record ThemeData(Dictionary<string, Swatch> Brushes, HashSet<string> Keys);

    // WPF objects belong to the thread that created them, so everything is copied into plain values there.
    private static ThemeData LoadTheme()
    {
        ThemeData? data = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                // Outside the app, the converters' clr-namespace needs the assembly spelled out.
                var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme.xaml"))
                    .Replace("clr-namespace:BgOptimizer.Converters\"", "clr-namespace:BgOptimizer.Converters;assembly=BgOptimizer\"");
                var dict = (ResourceDictionary)XamlReader.Parse(xaml);
                var brushes = new Dictionary<string, Swatch>();
                var keys = new HashSet<string>();
                foreach (var k in dict.Keys)
                {
                    keys.Add(k.ToString()!);
                    if (dict[k] is SolidColorBrush b)
                        brushes[k.ToString()!] = new Swatch(b.Color.R, b.Color.G, b.Color.B, b.Opacity);
                }
                data = new ThemeData(brushes, keys);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error != null) throw error;
        return data!;
    }

    private static readonly ThemeData Theme = LoadTheme();

    private static Swatch C(string key) => Theme.Brushes[key];

    /// <summary>Colour as seen over a base: brush Opacity is composited onto the base.</summary>
    private static Swatch Over(string key, string baseKey)
    {
        var b = Theme.Brushes[key];
        var bg = Theme.Brushes[baseKey];
        double a = b.Opacity;
        return new Swatch((byte)(b.R * a + bg.R * (1 - a)), (byte)(b.G * a + bg.G * (1 - a)), (byte)(b.B * a + bg.B * (1 - a)), 1);
    }
    private static double Lum(Swatch c)
    {
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Ratio(Swatch a, Swatch b)
    {
        var x = Lum(a); var y = Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Fact]
    public void ThemeDefinesTheTokensTheUiRelatesOn()
    {
        foreach (var k in new[] { "BgBrush", "PanelBrush", "Panel2Brush", "TextBrush", "TextDimBrush", "TextFaintBrush",
                     "AccentBrush", "WarnBrush", "DangerBrush", "DangerTextBrush", "ControlBorderBrush", "FocusBrush" })
            Assert.Contains(k, Theme.Keys);
        Assert.Contains("FocusRing", Theme.Keys);
    }

    [Theory]
    [InlineData("TextBrush", "PanelBrush")]
    [InlineData("TextDimBrush", "PanelBrush")]
    [InlineData("TextDimBrush", "Panel2Brush")]
    [InlineData("TextFaintBrush", "BgBrush")]
    [InlineData("TextFaintBrush", "PanelBrush")]
    [InlineData("TextFaintBrush", "Panel2Brush")]
    [InlineData("TextFaintBrush", "BorderSoftBrush")]      // hovered row
    [InlineData("WarnBrush", "PanelBrush")]
    public void BodyAndHintTextMeetAA(string fg, string bg) =>
        Assert.True(Ratio(C(fg), C(bg)) >= 4.5, $"{fg} on {bg} = {Ratio(C(fg), C(bg)):F2}");

    [Fact]
    public void FaintTextOnSelectedRowMeetsAA()
    {
        var selected = Over("AccentBgBrush", "PanelBrush");
        Assert.True(Ratio(C("TextFaintBrush"), selected) >= 4.5, $"{Ratio(C("TextFaintBrush"), selected):F2}");
    }

    [Theory]
    [InlineData("PanelBrush")]
    [InlineData("Panel2Brush")]
    [InlineData("BgBrush")]
    public void DangerButtonTextMeetsAA(string surface)
    {
        var bg = Over("DangerBgBrush", surface);
        Assert.True(Ratio(C("DangerTextBrush"), bg) >= 4.5, $"{Ratio(C("DangerTextBrush"), bg):F2}");
    }

    [Fact]
    public void PrimaryButtonTextMeetsAA() =>
        Assert.True(Ratio(new Swatch(255, 255, 255, 1), C("AccentSolidBrush")) >= 4.5);

    [Fact]
    public void ThemeDefinesTheCheckBoxSwitchAndScrollBarStyles()
    {
        foreach (var k in new[] { "ToggleSwitch", "ScrollThumb", "ScrollPage", "ReadOnlyCheckCell", "LoadBar", "SliderThumb", "SliderFill", "SliderRest" })
            Assert.Contains(k, Theme.Keys);
    }

    // The load meter: its fill (normal and over-the-limit) must stand out from the track, and the limit marker must too.
    [Theory]
    [InlineData("AccentBrush")]
    [InlineData("DangerBrush")]
    [InlineData("TextBrush")]
    public void LoadBarFillsAndLimitMarkerStandOutFromTheTrack(string fill) =>
        Assert.True(Ratio(C(fill), C("Panel2Brush")) >= 3.0, $"{fill} on the bar track = {Ratio(C(fill), C("Panel2Brush")):F2}");

    [Fact]
    public void SliderTrackAndFilledPartAreVisibleOnThePanel()
    {
        Assert.True(Ratio(C("ControlBorderBrush"), C("BgBrush")) >= 3.0);
        Assert.True(Ratio(C("AccentSolidBrush"), C("BgBrush")) >= 3.0);
    }

    // Checkbox box / switch track: the filled (on) state must stand out from every surface, and the tick or thumb on it must be readable.
    [Theory]
    [InlineData("PanelBrush")]
    [InlineData("Panel2Brush")]
    [InlineData("BgBrush")]
    public void CheckedBoxAndOnSwitchStandOutFromTheSurface(string surface) =>
        Assert.True(Ratio(C("AccentSolidBrush"), C(surface)) >= 3.0, $"on-state fill on {surface} = {Ratio(C("AccentSolidBrush"), C(surface)):F2}");

    [Fact]
    public void TickAndOnThumbAreReadableOnTheOnFill() =>
        Assert.True(Ratio(new Swatch(255, 255, 255, 1), C("AccentSolidBrush")) >= 3.0);

    [Fact]
    public void OffSwitchThumbIsVisibleOnItsTrack() =>
        Assert.True(Ratio(C("TextDimBrush"), C("Panel2Brush")) >= 3.0, $"{Ratio(C("TextDimBrush"), C("Panel2Brush")):F2}");

    [Theory]
    [InlineData("PanelBrush")]
    [InlineData("BgBrush")]
    public void ScrollBarThumbMeets3To1(string surface) =>
        Assert.True(Ratio(C("ControlBorderBrush"), C(surface)) >= 3.0, $"scroll thumb on {surface}");

    [Theory]
    [InlineData("PanelBrush")]
    [InlineData("Panel2Brush")]
    [InlineData("BgBrush")]
    public void ControlBoundariesAndFocusRingMeet3To1(string surface)
    {
        Assert.True(Ratio(C("ControlBorderBrush"), C(surface)) >= 3.0, $"border on {surface}");
        Assert.True(Ratio(C("FocusBrush"), C(surface)) >= 3.0, $"focus on {surface}");
    }
}




