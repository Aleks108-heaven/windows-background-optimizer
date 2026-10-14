using System.IO;

namespace BgOptimizer.Services;

internal static class PathProbe
{
    /// <summary>
    /// True only when Windows says the file or folder is not there. "Access denied", a path that is too
    /// long or an unreadable drive are all "can't tell", which must never be reported as deleted
    /// (File.Exists would return false for every one of them).
    /// Callers must only ask about paths that pass <see cref="IsOnLocalFixedDrive"/>: probing is a real
    /// file-system call and, for a network path, a connection to whatever server the path names.
    /// </summary>
    public static bool IsMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    /// <summary>
    /// True when the path is on one of this PC's own fixed drives. The paths this app looks at come from
    /// the registry, shortcuts and scheduled tasks, which other programs can write; a UNC path there
    /// (\\server\share\x.exe) would make the elevated app open a network connection to that server just
    /// by checking whether the file exists (leaking the user's Windows credentials hash) and a path on a
    /// removable, optical or mapped drive would spin it up or hang. Those are never probed or read.
    /// </summary>
    public static bool IsOnLocalFixedDrive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    /// <summary>True when the path's drive is a present local fixed drive, so a missing folder on it really is gone (not just an unplugged or remote disk).</summary>
    public static bool DriveIsPresent(string path)
    {
        try { return IsOnLocalFixedDrive(path) && Directory.Exists(Path.GetPathRoot(path)); }
        catch { return false; }
    }

    public static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        var f = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
