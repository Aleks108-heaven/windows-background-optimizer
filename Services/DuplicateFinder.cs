using System.IO;
using System.Security.Cryptography;

namespace BgOptimizer.Services;

internal sealed record DuplicateFile(string Path, DateTime ModifiedUtc);

internal sealed record DuplicateGroup(long Size, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>What keeping a single copy would free.</summary>
    public long WastedBytes => Size * (Files.Count - 1);
}

/// <param name="Comparing">False while listing files, true once candidates with equal sizes are being hashed.</param>
/// <param name="Count">Files listed so far, or candidate files still to compare.</param>
internal readonly record struct DuplicateProgress(bool Comparing, int Count);

/// <summary>
/// Finds files with identical content, read-only. Cheapest test first: same size, then a hash of the first
/// 64 KB, then a SHA-256 of the whole file, so only files that agree on everything before are read in full.
/// Never follows junctions/symlinks, and skips cloud placeholders (OneDrive "online-only" files) because
/// reading one would download it.
/// </summary>
internal static class DuplicateFinder
{
    /// <summary>Smaller files are skipped: they are numerous, rarely worth the time, and mostly legitimate copies (icons, configs).</summary>
    public const long DefaultMinBytes = 1024 * 1024;

    private const int PartialBytes = 64 * 1024;
    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 | (FileAttributes)0x00040000; // RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "$RECYCLE.BIN", "System Volume Information",
    };

    public static IEnumerable<string> DefaultRoots()
    {
        var folders = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        return folders.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<DuplicateGroup> Find(
        IEnumerable<string> roots, long minBytes, CancellationToken ct, IProgress<DuplicateProgress>? progress = null)
    {
        var bySize = new Dictionary<long, List<FileInfo>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // overlapping roots must not count a file twice
        var listed = 0;

        foreach (var file in Walk(roots, ct))
        {
            if (!seen.Add(file.FullName)) continue;
            if (!TryGetCandidateLength(file, minBytes, out var length)) continue;
            if (!bySize.TryGetValue(length, out var list)) bySize[length] = list = new List<FileInfo>();
            list.Add(file);
            if (++listed % 500 == 0) progress?.Report(new DuplicateProgress(false, listed));
        }

        var sameSize = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var toCompare = sameSize.Sum(kv => kv.Value.Count);
        var groups = new List<DuplicateGroup>();

        foreach (var (size, files) in sameSize)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DuplicateProgress(true, toCompare));
            toCompare -= files.Count;

            foreach (var sameHead in GroupByHash(files, PartialBytes, ct))
            {
                var sameContent = size <= PartialBytes
                    ? new List<List<FileInfo>> { sameHead }  // the partial hash already covered the whole file
                    : GroupByHash(sameHead, null, ct);
                foreach (var set in sameContent)
                    if (set.Count > 1)
                        groups.Add(new DuplicateGroup(size, set.Select(f => new DuplicateFile(f.FullName, f.LastWriteTimeUtc)).ToList()));
            }
        }

        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static bool TryGetCandidateLength(FileInfo file, long minBytes, out long length)
    {
        length = 0;
        try
        {
            if ((file.Attributes & CloudOnly) != 0) return false;
            length = file.Length;
            return length >= minBytes && length > 0;
        }
        catch { return false; }
    }

    // Manual walk: a junction/symlink is not entered, and an unreadable folder skips just that folder.
    private static IEnumerable<FileInfo> Walk(IEnumerable<string> roots, CancellationToken ct)
    {
        var pending = new Stack<string>(roots.Where(Directory.Exists).Reverse());
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos().ToList(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo dir)
                    {
                        if (!SkippedFolders.Contains(dir.Name)) pending.Push(dir.FullName);
                        continue;
                    }
                }
                catch { continue; }
                if (entry is FileInfo file) yield return file;
            }
        }
    }

    /// <summary>Splits files into sets that share a hash of their first <paramref name="maxBytes"/> (or all of it). Unreadable files drop out.</summary>
    private static List<List<FileInfo>> GroupByHash(List<FileInfo> files, int? maxBytes, CancellationToken ct)
    {
        var byHash = new Dictionary<string, List<FileInfo>>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string hash;
            try { hash = Hash(file.FullName, maxBytes, ct); }
            catch (OperationCanceledException) { throw; }
            catch { continue; } // in use, access denied, or gone since it was listed
            if (!byHash.TryGetValue(hash, out var set)) byHash[hash] = set = new List<FileInfo>();
            set.Add(file);
        }
        return byHash.Values.Where(s => s.Count > 1).ToList();
    }

    private static string Hash(string path, int? maxBytes, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            81920, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buffer = new byte[81920];
        long remaining = maxBytes ?? long.MaxValue;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested(); // a multi-GB video must not make Stop wait for the whole file
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
