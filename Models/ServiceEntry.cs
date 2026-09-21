namespace BgOptimizer.Models;

public class ServiceEntry
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "";
    public string StartType { get; set; } = "";
    public bool IsProtected { get; set; }
    public ItemCategory Category { get; set; }
    public string CategoryDisplay => Category.ToDisplay();
}
