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
/// Fixed colors matching Theme.xaml's palette, shared by every severity
/// converter so "warn" and "danger" always mean the same shade everywhere.
/// </summary>
internal static class SeverityColors
{
    public static readonly SolidColorBrush Ok = Freeze(0x9a, 0xa2, 0xb1);
    public static readonly SolidColorBrush Warn = Freeze(0xf0, 0xa9, 0x3e);
    public static readonly SolidColorBrush Danger = Freeze(0xef, 0x5b, 0x5b);
    public static readonly SolidColorBrush Faint = Freeze(0x83, 0x8b, 0x9b);

    public static SolidColorBrush For(Severity s) => s switch
    {
        Severity.Danger => Danger,
        Severity.Warn => Warn,
        Severity.Faint => Faint,
        _ => Ok,
    };

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
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
