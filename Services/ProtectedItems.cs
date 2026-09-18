namespace BgOptimizer.Services;

/// <summary>
/// Deny-lists that keep the app from letting a user disable or kill something
/// that would break login, networking, or the shell. Checked before every
/// destructive action; these names are never exposed as toggleable in the UI.
/// </summary>
internal static class ProtectedItems
{
    public static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Registry", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "fontdrvhost", "sihost", "explorer",
        "taskhostw", "ctfmon", "conhost", "dllhost", "spoolsv",
        "audiodg", "WUDFHost", "RuntimeBroker", "WmiPrvSE",
    };

    public static readonly HashSet<string> ProtectedServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "Winmgmt", "EventLog",
        "PlugPlay", "Power", "Schedule", "Dhcp", "Dnscache", "BFE", "MpsSvc",
        "WinDefend", "CryptSvc", "TrustedInstaller", "TokenBroker", "UserManager",
        "ProfSvc", "SamSs", "LanmanServer", "LanmanWorkstation", "Netman",
        "NlaSvc", "Nsi", "gpsvc", "Wcmsvc", "WdiServiceHost", "WdiSystemHost",
        "wuauserv", "AudioEndpointBuilder", "Audiosrv", "Themes", "WSearch",
        "DeviceInstall", "DcomLaunch", "SystemEventsBroker", "TimeBrokerSvc",
        "PcaSvc", "shpamsvc", "StateRepository", "InstallService",
    };

    public static bool IsProcessProtected(string processName) => ProtectedProcessNames.Contains(processName);

    public static bool IsServiceProtected(string serviceName) => ProtectedServiceNames.Contains(serviceName);
}
