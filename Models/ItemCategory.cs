namespace BgOptimizer.Models;

public enum ItemCategory
{
    Unknown,
    Core,
    Telemetry,
    DevTool,
    VendorDiag,
}

// Category text isn't localized, same as ServiceEntry.Status/StartType (which
// are raw OS-reported strings regardless of UI language) - it's a plain hint
// label, not user-facing prose.
public static class ItemCategoryExtensions
{
    public static string ToDisplay(this ItemCategory category) => category switch
    {
        ItemCategory.Core => "Core / Keep Enabled",
        ItemCategory.Telemetry => "Telemetry",
        ItemCategory.DevTool => "Developer Tool",
        ItemCategory.VendorDiag => "Vendor / Diagnostics",
        _ => "Uncategorized",
    };
}
