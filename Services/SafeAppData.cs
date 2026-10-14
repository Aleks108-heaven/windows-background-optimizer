using System.IO;

namespace BgOptimizer.Services;

/// <summary>
/// The app runs as administrator, but its data folder (%LocalAppData%\BgOptimizer) belongs to the user, so any
/// program running as that user can change it, including replacing the folder or a file with a junction or
/// symbolic link that points somewhere only an administrator can write. Writing or deleting through such a link
/// from the elevated app would act on the link's target. Before the app writes, it therefore refuses a folder
/// or file that is a reparse point. This narrows the window rather than closing it (a swap between the check
/// and the write is still possible); the fixed file names and fixed contents keep what is left small.
/// </summary>
internal static class SafeAppData
{
    public static bool IsSafeToWrite(string filePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folder)) return false;

            var dir = new DirectoryInfo(folder);
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;

            var file = new FileInfo(filePath);
            return !file.Exists || (file.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }
}
