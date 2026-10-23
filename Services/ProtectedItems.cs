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

    // Work agents that sit at ~0% CPU yet must stay reachable: the idle detector cannot tell "waiting for the next
    // message / packet / sync request" from "doing nothing", and freezing or slowing one means missed chat and mail
    // notifications, a dropped VPN tunnel, a dead remote-support session, a sync provider that hangs Explorer (a
    // frozen OneDrive stalls every open dialog on a cloud file), a password manager that stops filling, or a frozen
    // virtual machine. Messengers and mail, VPN and zero-trust clients, remote access, sync, password managers,
    // VM/container hosts. Treated like interactive apps by every guard that already checks IsInteractive; the
    // "never touch" list in Settings covers the corporate agents nobody can enumerate.
    public static readonly HashSet<string> AlwaysOnAgentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "slack", "Telegram", "WhatsApp", "Signal", "Viber", "Skype", "OUTLOOK", "olk", "HxOutlook", "thunderbird",
        "Webex", "webexmta", "CiscoCollabHost", "Mattermost", "Element",
        "openvpn", "openvpn-gui", "openvpnserv", "openvpnserv2", "WireGuard", "wireguard", "tailscale-ipn", "tailscaled",
        "NordVPN", "nordvpn-service", "ExpressVPN", "expressvpnd", "ProtonVPN", "ProtonVPNService", "vpnagent", "vpnui",
        "FortiClient", "FortiTray", "FortiSSLVPNdaemon", "PanGPA", "ZSATunnel", "ZSAService", "ZSATray", "cloudflared",
        "warp-svc", "Cloudflare WARP", "PulseSecureService", "pulsesecure", "NetExtender",
        "AnyDesk", "TeamViewer", "TeamViewer_Service", "rustdesk", "ScreenConnect.ClientService",
        "ScreenConnect.WindowsClient", "mstsc", "parsecd", "winvnc", "tvnserver", "LogMeIn", "SRService", "SRManager",
        "OneDrive", "Dropbox", "GoogleDriveFS", "googledrivesync", "syncthing", "Nextcloud", "MEGAsync", "BoxSync", "pCloud",
        "1Password", "Bitwarden", "KeePass", "KeePassXC", "Dashlane", "NordPass", "LastPass",
        "vmware-vmx", "vmware-view", "vmware", "VirtualBoxVM", "VBoxHeadless", "VBoxSVC", "qemu-system-x86_64",
        "Docker Desktop", "com.docker.backend", "com.docker.build", "dockerd", "wfica32", "SelfService", "CitrixWorkspace",
    };

    // Audio apps and always-on agents are included here so every existing guard (SetPriority, suspend, enforce
    // loop, Laptop Saver, WorkloadOptimizer) protects them without each needing its own check.
    public static bool IsInteractive(string processName)
    {
        var name = StripExe(processName);
        return InteractiveProcessNames.Contains(name) || AlwaysOnAgentNames.Contains(name) || IsAudioApp(processName);
    }

    private static string StripExe(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;

    // Windows and driver helpers that take part in power transitions, Wi-Fi, identity, display and thermal policy.
    // They are idle by CPU almost always (the idle detector would pick them), and a priority rule written to the
    // registry applies from the moment they start, including during sleep and wake. What prompted this was a run of
    // hard freezes right after resume from Modern Standby, with the Wi-Fi extension and a power-source change in the
    // log and permanent slow-down rules on several of these. It is not proven that the rules caused the freezes, but
    // slowing these helpers gains nothing and puts the power path at risk, so the app never slows, trims or freezes
    // them, by any route, with no override. Most are far below the optimizer's minimum size anyway; this is about
    // the permanent rules and the manual actions.
    public static readonly HashSet<string> PlatformHelperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wlanext", "dasHost", "WUDFCompanionHost", "NgcIso", "BioIso", "LsaIso", "SgrmBroker",
        "amdpmfservice", "amdpmfserviceuser", "atieclxx", "atiesrxx",
        "esif_uf", "igfxCUIService", "igfxEM", "igfxext", "igfxextN", "IntelCpHDCPSvc", "IntelCpHeciSvc", "jhi_service", "ibtsiva",
    };

    public static bool IsPlatformHelper(string processName) => PlatformHelperNames.Contains(StripExe(processName));

    // The user's own "never touch" list (Settings), for corporate agents and anything else only they know about.
    // Replaced as a whole, never edited in place, so a reader on another thread always sees a complete set.
    private static volatile HashSet<string> _userNames = new(StringComparer.OrdinalIgnoreCase);

    public const int MaxUserNames = 100;
    public const int MaxUserNameLength = 64;

    /// <summary>
    /// Splits the Settings text into program names: separated by commas, semicolons or line breaks (names may
    /// contain spaces, like "LM Studio"), ".exe" dropped, duplicates removed. Anything that cannot be a process
    /// name (a path, wildcard, control character, or one over 64 characters) is skipped, and only the first 100 count.
    /// </summary>
    internal static List<string> ParseUserNames(string? text)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return names;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = StripExe(part.Trim());
            if (name.Length is 0 or > MaxUserNameLength) continue;
            if (name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c))) continue;
            if (seen.Add(name)) names.Add(name);
            if (names.Count >= MaxUserNames) break;
        }
        return names;
    }

    public static void SetUserNames(string? text) =>
        _userNames = new HashSet<string>(ParseUserNames(text), StringComparer.OrdinalIgnoreCase);

    public static bool IsUserProtected(string processName) => _userNames.Contains(StripExe(processName));

    /// <summary>
    /// The message key explaining why the app will not slow down, trim or freeze this process, or null when it may.
    /// One answer for every route (manual, background, persistent rule), so no route can disagree with another.
    /// </summary>
    public static string? NeverTouchMessageKey(string processName) =>
        IsAiTool(processName) ? "Msg_ProcessAiTool"
        : IsPlatformHelper(processName) ? "Msg_ProcessPlatform"
        : IsUserProtected(processName) ? "Msg_ProcessUserProtected"
        : null;

    public static bool IsNeverTouch(string processName) => NeverTouchMessageKey(processName) is not null;

    // Local-AI stack: model hosts (LM Studio, Ollama, llama.cpp, ...) and the runtimes that agents are
    // written in. They look idle to the CPU-based detector while they wait on each other (an agent
    // waiting for a completion, a server waiting for the next request, or inference running on the GPU),
    // so trimming, throttling or freezing one stalls the whole pipeline, and a trimmed memory-mapped
    // model has to be paged back in from disk. Never touched, with no override.
    public static readonly HashSet<string> AiToolProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LM Studio", "lms", "lmlink-connector", "llama-server", "llama-cli", "llama-bench",
        "ollama", "ollama app", "ollama_llama_server", "llamafile", "koboldcpp", "GPT4All", "jan",
        "msty", "AnythingLLM", "LocalAI", "local-ai", "vllm",
        // Agent / pipeline runtimes and WSL, where models and agents are often hosted.
        "python", "pythonw", "node", "deno", "bun", "vmmem", "vmmemWSL", "wslhost", "wslservice",
    };

    private static readonly string[] AiNameFragments = { "llama", "ollama", "lmstudio", "lm studio", "gpt4all" };

    public static bool IsAiTool(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        if (AiToolProcessNames.Contains(name)) return true;
        foreach (var fragment in AiNameFragments)
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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
