using System.Text.RegularExpressions;

namespace BgOptimizer.Services;

/// <summary>
/// Pulls the program file out of a command line as stored in the registry, a shortcut or a task. Every path
/// handled here is untrusted text: it is parsed and checked for existence, never executed or opened.
/// </summary>
internal static class CommandPath
{
    private static readonly Regex IconIndex = new(@",\s*-?\d+$", RegexOptions.Compiled);

    private static readonly Regex FileExtension = new(
        @"\.(exe|ico|dll|bat|cmd|vbs|js|ps1|jar|lnk)(?=$|[\s""',])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The file named at the start of a command ("C:\x\a.exe" /S, C:\x\a.exe -background, C:\x\a.exe,0), with
    /// %ENV% variables expanded, or null when it does not start with a drive or network path.
    /// </summary>
    public static string? ExtractFilePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string candidate;
        try { candidate = Environment.ExpandEnvironmentVariables(command.Trim()); }
        catch { return null; }

        if (candidate.StartsWith('"'))
        {
            var end = candidate.IndexOf('"', 1);
            candidate = end > 1 ? candidate[1..end] : candidate[1..];
        }
        else
        {
            var match = FileExtension.Match(candidate);
            candidate = match.Success ? candidate[..(match.Index + match.Length)] : IconIndex.Replace(candidate, "");
        }
        candidate = IconIndex.Replace(candidate.Trim(), "");
        return IsDrivePath(candidate) ? candidate : null;
    }

    /// <summary>Device paths (\\?\, \Device\...) and anything without a drive letter or share cannot be probed meaningfully.</summary>
    public static bool IsDrivePath(string path) =>
        path.Length > 2 && ((char.IsLetter(path[0]) && path[1] == ':') || path.StartsWith(@"\\", StringComparison.Ordinal))
        && !path.StartsWith(@"\\?\", StringComparison.Ordinal);

    /// <summary>The file a .lnk shortcut points to, read through the shell's scripting object (no process is started); null if unreadable.</summary>
    public static string? ShortcutTarget(string lnkPath)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            dynamic link = ((dynamic)shell!).CreateShortcut(lnkPath);
            string target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
        }
        catch { return null; }
        finally
        {
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}
