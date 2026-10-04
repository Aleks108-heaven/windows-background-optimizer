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
        "Idle", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "fontdrvhost", "sihost", "explorer",
        "taskhostw", "ctfmon", "conhost", "dllhost", "spoolsv",
        "audiodg", "WUDFHost", "RuntimeBroker", "WmiPrvSE",
        "MsMpEng", "NisSrv", "SecurityHealthService", "MpDefenderCoreService",
    };

    // Foreground/UI processes: lowering their priority (esp. persistently, which hits every
    // instance of the exe) makes browsers, Start, and shell surfaces stutter or freeze.
    public static readonly HashSet<string> InteractiveProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "msedgewebview2",
        "ApplicationFrameHost", "SearchHost", "ShellExperienceHost",
        "StartMenuExperienceHost", "ShellHost", "LockApp", "Taskmgr",
        "BgOptimizer", "AsusOSD", "AsusHotkey", "Code", "devenv",
    };

    // Audio/media apps do their real work in audiodg and the driver, so the app process itself sits
    // near 0% CPU while sound is playing. The idle detector therefore reads them as "doing nothing";
    // trimming, throttling or freezing one cuts the sound (what broke AudioTune Pro). Matched by exact
    // name plus the "audio"/"sound" fragments that vendor tools and drivers use.
    public static readonly HashSet<string> AudioProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AudioTunePro", "RtkAudUService64", "RtkNGUI64", "Nahimic3", "NahimicService", "DolbyDAX2API",
        "EqualizerAPO", "Voicemeeter", "voicemeeter8x64", "VoicemeeterPro", "FxSound", "Spotify",
        "vlc", "foobar2000", "AIMP", "wmplayer", "Music.UI", "Discord", "Teams", "ms-teams", "Zoom",
    };

    private static readonly string[] AudioNameFragments = { "audio", "sound", "voicemeeter" };

    public static bool IsAudioApp(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        if (AudioProcessNames.Contains(name)) return true;
        foreach (var fragment in AudioNameFragments)
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Audio apps are included here so every existing guard (SetPriority, suspend, enforce loop,
    // Laptop Saver, WorkloadOptimizer) protects them without each needing its own check.
    public static bool IsInteractive(string processName) =>
        InteractiveProcessNames.Contains(processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4] : processName)
        || IsAudioApp(processName);

    // Third-party AV/EDR/backup/VPN agents: not covered by ProtectedProcessNames (which only
    // lists Microsoft's own critical processes + Defender). These commonly run as low-CPU,
    // idle, non-interactive background services, so without this list they'd be
    // indistinguishable from ordinary throttle/suspend candidates to WorkloadOptimizer.
    // Suspending one freezes real-time protection; unlike priority changes this is never
    // allowed for these names, with no override.
    public static readonly HashSet<string> SecurityToolProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // CrowdStrike, SentinelOne, Sophos, ESET, Malwarebytes, Kaspersky, Symantec/Norton,
        // McAfee, Trend Micro, Carbon Black, Cortex XDR, Cylance, Webroot, Bitdefender,
        // Avast/AVG, F-Secure, Palo Alto, Huntress, Cisco, Qualys, Rapid7, Tanium
        "CSFalconService", "CSFalconContainer", "SentinelAgent", "SentinelServiceHost",
        "SophosHealth", "SophosFileScanner", "SophosSafestore", "ekrn", "ermsg",
        "mbam", "MBAMService", "avp", "avpui", "ccSvcHst", "NortonSecurity", "N360",
        "mcshield", "Mcafee", "mfemms", "tmlisten", "PccNTMon", "cb", "RepMgr",
        "cyserver", "CylanceSvc", "WRSA", "bdagent", "bdredline", "vsserv",
        "AvastSvc", "AVGSvc", "fsav", "fsorsp", "PanGPS", "CyveraService",
        "traps", "HuntressAgent", "ciscoamp", "sfc", "qualys-cloud-agent",
        "ir_agent", "TaniumClient",
        // Backup / endpoint-management agents commonly mistaken for idle background junk
        "VeeamAgent", "AcronisAgent", "MsMpEngCP",
    };

    public static bool IsSecurityTool(string processName) =>
        SecurityToolProcessNames.Contains(processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4] : processName);

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
        "Sense", "WdNisSvc", "wscsvc",
        // Owned by TrustedInstaller: WMI's ChangeStartMode returns access-denied (code 2)
        // for this even when running as admin, so it's blocked before we bother trying.
        "BrokerInfrastructure",
    };

    public static bool IsProcessProtected(string processName) => ProtectedProcessNames.Contains(processName);

    public static bool IsServiceProtected(string serviceName) => ProtectedServiceNames.Contains(serviceName);
}
