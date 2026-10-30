using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using BgOptimizer.Localization;
using Xunit;
using Application = System.Windows.Application;

namespace BgOptimizer.Tests;

/// <summary>
/// Parses the app's real windows and dialogs with the real theme. Nothing else in the suite loads them, and a resource
/// key that does not exist, a property that does not exist or a broken binding only fails when the (elevated) app is
/// started, which is the worst place to find out. The code-behind is not part of this, so the class name and the event
/// handlers are removed from the text before it is parsed; everything else is the file as shipped.
/// </summary>
public class XamlIntegrityTests
{
    private static readonly string[] Files = { "MainWindow.xaml", "ConfirmDialog.xaml", "CodeDialog.xaml", "ProgramPickerDialog.xaml" };

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "xaml", name));

    /// <summary>Loose XAML cannot name a class or hook handlers, and "icon.ico" resolves against the exe, so those are cut.</summary>
    private static string Loosen(string xaml)
    {
        xaml = Regex.Replace(xaml, @"\sx:Class=""[^""]*""", "");
        xaml = Regex.Replace(xaml, @"\sIcon=""[^""]*""", "");
        xaml = Regex.Replace(xaml,
            @"\s(Click|SelectionChanged|TextChanged|PasswordChanged|Checked|Unchecked|Loaded|SizeChanged|Closing|Closed|StateChanged|ValueChanged|Sorting|MouseDoubleClick)=""[^""]*""", "");
        // Outside the app the clr-namespaces need the assembly spelled out.
        return xaml.Replace("clr-namespace:BgOptimizer.Localization\"", "clr-namespace:BgOptimizer.Localization;assembly=BgOptimizer\"");
    }

    // One application per process and its objects belong to the thread that made them, so everything runs on one STA thread.
    // Lazy, not a static initializer: that thread calls this class's own static methods, which would wait for the
    // initializer that is waiting for the thread.
    private static readonly Lazy<Dictionary<string, string>> LazyFailures = new(ParseEverything);
    private static Dictionary<string, string> Failures => LazyFailures.Value;

    private static Dictionary<string, string> ParseEverything()
    {
        var failures = new Dictionary<string, string>();
        var thread = new Thread(() =>
        {
            try
            {
                var themeXaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme.xaml"))
                    .Replace("clr-namespace:BgOptimizer.Converters\"", "clr-namespace:BgOptimizer.Converters;assembly=BgOptimizer\"");
                var app = Application.Current ?? new Application();
                app.Resources = (ResourceDictionary)XamlReader.Parse(themeXaml);

                foreach (var file in Files)
                {
                    try
                    {
                        var root = XamlReader.Parse(Loosen(Read(file)));
                        if (root is not Window) failures[file] = "the root is not a Window";
                    }
                    catch (Exception ex) { failures[file] = ex.GetBaseException().Message; }
                }
            }
            catch (Exception ex) { failures["(setup)"] = ex.ToString(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failures;
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("ConfirmDialog.xaml")]
    [InlineData("CodeDialog.xaml")]
    [InlineData("ProgramPickerDialog.xaml")]
    public void TheFileParsesWithTheRealThemeAndEveryResourceItNamesExists(string file)
    {
        Assert.False(Failures.ContainsKey("(setup)"), Failures.GetValueOrDefault("(setup)"));
        Assert.False(Failures.ContainsKey(file), $"{file}: {Failures.GetValueOrDefault(file)}");
    }

    private static Dictionary<string, Dictionary<string, string>> Table() =>
        (Dictionary<string, Dictionary<string, string>>)typeof(Loc)
            .GetField("Strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void EveryTranslationKeyTheWindowsBindToExistsInEveryLanguage()
    {
        var table = Table();
        foreach (var file in Files)
            foreach (Match m in Regex.Matches(Read(file), @"Path=\[([A-Za-z0-9_]+)\]"))
                foreach (var lang in Loc.Languages)
                    Assert.True(table[lang.Code].ContainsKey(m.Groups[1].Value), $"{file}: {lang.Code} lacks {m.Groups[1].Value}");
    }

    [Fact]
    public void EveryNamedControlTheCodeBehindUsesIsInTheXaml()
    {
        // The compiler already guarantees this for the app; this guards the test's own premise (the files are the shipped ones).
        var xaml = Read("MainWindow.xaml");
        foreach (var name in new[] { "PowerTab", "PowerRows", "PowerAdvancedRows", "PowerAdvancedCheck", "PowerAdvicePanel", "PowerPlanText",
                     "CoolTempText", "CpuLimitCombo", "KeepInTrayCheck", "BuiltInListPanel", "BuiltInListCheck", "NeverTouchBox", "PickProgramsButton" })
            Assert.Contains($"x:Name=\"{name}\"", xaml);
    }
}
