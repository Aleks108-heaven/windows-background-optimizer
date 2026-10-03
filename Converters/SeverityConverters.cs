using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace BgOptimizer.Converters;

public enum Severity { Ok, Warn, Danger, Faint }

/// <summary>
/// Single source of truth for what counts as warn/danger, shared by the brush, glyph and
/// weight converters (and ProcessEntry.IsHog) so they can never disagree.
/// </summary>
public static class SeverityRules
{
    public const double CpuWarnPercent = 5;
    public const double CpuDangerPercent = 10;
    public const long MemoryWarnBytes = 200L * 1024 * 1024;
    public const long MemoryDangerBytes = 400L * 1024 * 1024;

    public static Severity Cpu(object? value)
    {
        var percent = value is double d ? d : 0;
        return percent >= CpuDangerPercent ? Severity.Danger : percent >= CpuWarnPercent ? Severity.Warn : Severity.Ok;
    }

    public static Severity Memory(object? value)
    {
        var bytes = value is long b ? b : 0;
        return bytes >= MemoryDangerBytes ? Severity.Danger : bytes >= MemoryWarnBytes ? Severity.Warn : Severity.Ok;
    }

    public static Severity Priority(object? value) =>
        (value as string) switch
        {
            "High" or "AboveNormal" or "RealTime" => Severity.Warn,
            "Idle" => Severity.Faint,
            _ => Severity.Ok,
        };
}

/// <summary>
/// Severity colours come from Theme.xaml (OkText = TextDim, Warn, Danger, Faint = TextFaint) so a
/// palette change there is the only edit needed. The literals are used only when no Application
/// (e.g. unit tests) is loaded; they must equal the theme values.
/// </summary>
internal static class SeverityColors
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    private static SolidColorBrush Pick(string themeKey, byte r, byte g, byte b)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(themeKey, out var cached)) return cached;
            if (System.Windows.Application.Current?.TryFindResource(themeKey) is SolidColorBrush fromTheme)
                return Cache[themeKey] = fromTheme;
            // No theme loaded (unit tests): fall back to the literal, but don't cache it.
            var fallback = new SolidColorBrush(Color.FromRgb(r, g, b));
            fallback.Freeze();
            return fallback;
        }
    }

    public static SolidColorBrush Ok => Pick("TextDimBrush", 0x9a, 0xa2, 0xb1);
    public static SolidColorBrush Warn => Pick("WarnBrush", 0xf2, 0xb8, 0x4b);
    public static SolidColorBrush Danger => Pick("DangerBrush", 0xef, 0x5b, 0x5b);
    public static SolidColorBrush Faint => Pick("TextFaintBrush", 0x90, 0x97, 0xa7);

    public static SolidColorBrush For(Severity s) => s switch
    {
        Severity.Danger => Danger,
        Severity.Warn => Warn,
        Severity.Faint => Faint,
        _ => Ok,
    };
}
/// <summary>
/// Base for the severity converters: subclasses only say how to rate a value; brush,
/// glyph and weight are derived from the target type. The glyph and weight exist so
/// severity is never conveyed by colour alone (WCAG 1.4.1): warn is a triangle,
/// danger a bold diamond, low-priority a down-triangle.
/// </summary>
public abstract class SeverityConverterBase : IValueConverter
{
    private static readonly FontWeight[] Weights = { FontWeights.Normal, FontWeights.SemiBold, FontWeights.Bold, FontWeights.Normal };

    protected abstract Severity Rate(object? value);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var severity = Rate(value);
        if (targetType == typeof(string)) return Glyph(severity);
        if (targetType == typeof(FontWeight)) return Weights[(int)severity];
        return SeverityColors.For(severity);
    }

    private static string Glyph(Severity s) => s switch
    {
        Severity.Danger => "◆",
        Severity.Warn => "▲",
        Severity.Faint => "▼",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>CPU% &gt;= 10 is danger, &gt;= 5 is warn, otherwise the neutral tone.</summary>
public class CpuSeverityBrushConverter : SeverityConverterBase
{
    protected override Severity Rate(object? value) => SeverityRules.Cpu(value);
}

/// <summary>Working-set &gt;= 400 MB is danger, &gt;= 200 MB is warn, otherwise the neutral tone.</summary>
public class MemorySeverityBrushConverter : SeverityConverterBase
{
    protected override Severity Rate(object? value) => SeverityRules.Memory(value);
}

public class PrioritySeverityBrushConverter : SeverityConverterBase
{
    protected override Severity Rate(object? value) => SeverityRules.Priority(value);
}

/// <summary>
/// Renders the "Protected" column as a Segoe MDL2 Assets lock glyph (U+E72E) instead of a
/// checkbox, so it reads as a status indicator rather than a settable option.
/// </summary>
public class ProtectedToLockGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
