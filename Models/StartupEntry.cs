namespace BgOptimizer.Models;

public enum StartupSource
{
    RegistryRunHkcu,
    RegistryRunHklm,
    StartupFolderUser,
    StartupFolderCommon,
}

public class StartupEntry
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public StartupSource Source { get; set; }
    public bool Enabled { get; set; }
    public string SourceDisplay => Source switch
    {
        StartupSource.RegistryRunHkcu => "Registry (current user)",
        StartupSource.RegistryRunHklm => "Registry (all users)",
        StartupSource.StartupFolderUser => "Startup folder (you)",
        StartupSource.StartupFolderCommon => "Startup folder (all users)",
        _ => "Unknown",
    };
}
