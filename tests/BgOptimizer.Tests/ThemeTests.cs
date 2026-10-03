using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace BgOptimizer.Tests;

/// <summary>Loads the real Theme.xaml and checks WCAG contrast for the colour pairs the UI actually uses.</summary>
public class ThemeContrastTests
{
    private static ResourceDictionary LoadTheme()
    {
        ResourceDictionary? dict = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                // Outside the app, the converters' clr-namespace needs the assembly spelled out.
                var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme.xaml"))
                    .Replace("clr-namespace:BgOptimizer.Converters\"", "clr-namespace:BgOptimizer.Converters;assembly=BgOptimizer\"");
                dict = (ResourceDictionary)XamlReader.Parse(xaml);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error != null) throw error;
        return dict!;
    }

    private static readonly ResourceDictionary Theme = LoadTheme();

    private static Color C(string key) => ((SolidColorBrush)Theme[key]).Color;

    /// <summary>Colour as seen over a base: brush Opacity is composited onto the base.</summary>
    private static Color Over(string key, string baseKey)
    {
        var b = (SolidColorBrush)Theme[key];
        var bg = C(baseKey);
        double a = b.Opacity;
        return Color.FromRgb((byte)(b.Color.R * a + bg.R * (1 - a)), (byte)(b.Color.G * a + bg.G * (1 - a)), (byte)(b.Color.B * a + bg.B * (1 - a)));
    }

    private static double Lum(Color c)
    {
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Ratio(Color a, Color b)
    {
        var x = Lum(a); var y = Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Fact]
    public void ThemeDefinesTheTokensTheUiRelatesOn()
    {
        foreach (var k in new[] { "BgBrush", "PanelBrush", "Panel2Brush", "TextBrush", "TextDimBrush", "TextFaintBrush",
                     "AccentBrush", "WarnBrush", "DangerBrush", "DangerTextBrush", "ControlBorderBrush", "FocusBrush" })
            Assert.True(Theme.Contains(k), k);
        Assert.True(Theme.Contains("FocusRing"));
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
        Assert.True(Ratio(Colors.White, C("AccentSolidBrush")) >= 4.5);

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


