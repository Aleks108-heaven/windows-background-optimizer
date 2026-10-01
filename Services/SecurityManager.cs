using System.Diagnostics;
using System.Management;
using BgOptimizer.Localization;
using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// Narrow hardening controls: WinRM on/off, an inbound-SMB firewall block, and a
/// read-only view of the firewall profiles. Deliberately cannot turn the firewall
/// itself off or open arbitrary ports.
/// </summary>
public class SecurityManager
{
    private const string SmbRuleName = "BgOptimizer Block SMB Inbound";
    private const string ProfilesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    public record WinRmState(string Status, string StartMode);

    public WinRmState GetWinRm()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT State, StartMode FROM Win32_Service WHERE Name='WinRM'");
            foreach (ManagementObject o in searcher.Get())
                return new WinRmState(o["State"]?.ToString() ?? "Unknown", o["StartMode"]?.ToString() ?? "Unknown");
        }
        catch { }
        return new WinRmState("Unknown", "Unknown");
    }

    public ActionResult SetWinRm(bool enabled)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name='WinRM'");
            var svc = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (svc == null) return new ActionResult(false, "Msg_ServiceNotFound", "WinRM");

            if (enabled)
            {
                Invoke(svc, "ChangeStartMode", "Automatic");
                Invoke(svc, "StartService");
            }
            else
            {
                Invoke(svc, "StopService");
                Invoke(svc, "ChangeStartMode", "Disabled");
            }
            return new ActionResult(true, enabled ? "Msg_WinRmEnabled" : "Msg_WinRmDisabled");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_SecurityFailed", "WinRM", ex.Message);
        }
    }

    // WMI return codes: 0 = success, 5 = already stopped, 10 = already running.
    private static void Invoke(ManagementObject svc, string method, params object[] args)
    {
        var code = Convert.ToUInt32(svc.InvokeMethod(method, args));
        if (code != 0 && code != 5 && code != 10)
            throw new InvalidOperationException($"{method} returned code {code}");
    }

    // netsh exits with 1 when "show"/"delete" finds no rule of that name (it is not a failure),
    // and its message is localized, so the exit code of a "show" is the reliable existence test.
    private static bool RuleExists(string ruleName) =>
        RunNetsh("advfirewall", "firewall", "show", "rule", $"name={ruleName}") == 0;

    public bool IsSmbBlocked() => RuleExists(SmbRuleName);

    public ActionResult SetSmbBlocked(bool blocked)
    {
        // Unblocking when our rule is already gone (removed by the script, by hand, or never added)
        // is the desired end state, not an error.
        if (!blocked && !RuleExists(SmbRuleName))
            return new ActionResult(true, "Msg_SmbUnblocked");

        var code = blocked
            ? RunNetsh("advfirewall", "firewall", "add", "rule", $"name={SmbRuleName}", "dir=in", "action=block", "protocol=TCP", "localport=139,445", "profile=any")
            : RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={SmbRuleName}");
        return code == 0
            ? new ActionResult(true, blocked ? "Msg_SmbBlocked" : "Msg_SmbUnblocked")
            : new ActionResult(false, "Msg_SecurityFailed", "SMB", $"netsh exit code {code}");
    }

    private const string AppRulePrefix = "BgOptimizer Block Out: ";

    /// <summary>Program paths that have a rule created by this app.</summary>
    public List<string> GetBlockedApps()
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(SystemTools.Path("netsh.exe"))
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "advfirewall", "firewall", "show", "rule", "name=all", "dir=out" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                var idx = t.IndexOf(':');
                if (idx > 0 && t.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase)
                    && t[(idx + 1)..].Trim() is var n && n.StartsWith(AppRulePrefix))
                    result.Add(n[AppRulePrefix.Length..]);
            }
        }
        catch { }
        return result;
    }

    public ActionResult SetAppBlocked(string exePath, bool blocked)
    {
        var name = System.IO.Path.GetFileName(exePath);
        if (blocked)
        {
            if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(exePath) || exePath.Contains('"'))
                return new ActionResult(false, "Msg_SecurityFailed", name, "not a valid .exe path");
            // Blocking core Windows processes (svchost, lsass, ...) would cut off networking.
            if (ProtectedItems.IsProcessProtected(System.IO.Path.GetFileNameWithoutExtension(exePath)))
                return new ActionResult(false, "Msg_ProcessProtectedModify", name);
        }

        var rule = AppRulePrefix + exePath;
        if (!blocked && !RuleExists(rule))
            return new ActionResult(true, "Msg_AppUnblocked", name);

        var code = blocked
            ? RunNetsh("advfirewall", "firewall", "add", "rule", $"name={rule}", "dir=out", "action=block", $"program={exePath}", "enable=yes", "profile=any")
            : RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={rule}");
        return code == 0
            ? new ActionResult(true, blocked ? "Msg_AppBlocked" : "Msg_AppUnblocked", name)
            : new ActionResult(false, "Msg_SecurityFailed", name, $"netsh exit code {code}");
    }

    /// <returns>(Domain, Private, Public) firewall-enabled flags; null if unreadable.</returns>
    public (bool? Domain, bool? Private, bool? Public) GetFirewallProfiles() =>
        (ReadProfile("DomainProfile"), ReadProfile("StandardProfile"), ReadProfile("PublicProfile"));

    private static bool? ReadProfile(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ProfilesKey}\{name}");
            return key?.GetValue("EnableFirewall") is int v ? v != 0 : null;
        }
        catch { return null; }
    }

    // Arguments are passed as a list (never concatenated into one string), so a quote or space in a
    // rule name or program path can't be reinterpreted by netsh as extra parameters.
    private static int RunNetsh(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(SystemTools.Path("netsh.exe"))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
