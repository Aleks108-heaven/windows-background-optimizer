using System.Globalization;
using System.Windows.Data;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace BgOptimizer.Converters;

/// <summary>
/// Fixed colors matching Theme.xaml's palette, shared by every severity
/// converter so "warn" and "danger" always mean the same shade everywhere.
/// </summary>
internal static class SeverityColors
{
    public static readonly SolidColorBrush Ok = Freeze(0x9a, 0xa2, 0xb1);
    public static readonly SolidColorBrush Warn = Freeze(0xf0, 0xa9, 0x3e);
    public static readonly SolidColorBrush Danger = Freeze(0xef, 0x5b, 0x5b);
    public static readonly SolidColorBrush Faint = Freeze(0x5e, 0x64, 0x72);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>CPU% &gt;= 10 is danger, &gt;= 5 is warn, otherwise the neutral tone.</summary>
public class CpuSeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0;
        if (percent >= 10) return SeverityColors.Danger;
        if (percent >= 5) return SeverityColors.Warn;
        return SeverityColors.Ok;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Working-set &gt;= 400 MB is danger, &gt;= 200 MB is warn, otherwise the neutral tone.</summary>
public class MemorySeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var megabytes = value is long bytes ? bytes / 1024.0 / 1024.0 : 0;
        if (megabytes >= 400) return SeverityColors.Danger;
        if (megabytes >= 200) return SeverityColors.Warn;
        return SeverityColors.Ok;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class PrioritySeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "High" or "AboveNormal" or "RealTime" => SeverityColors.Warn,
            "Idle" => SeverityColors.Faint,
            _ => SeverityColors.Ok,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Renders the "Protected" column as a Segoe MDL2 Assets lock glyph instead of a
/// checkbox, so it reads as a status indicator rather than a settable option.
/// </summary>
public class ProtectedToLockGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
