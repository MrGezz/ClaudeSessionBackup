using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Counts files and bytes under a folder tree, honouring excluded directory names
/// at any depth. Used to compute live and backup stats for the log lines and manifest.
/// </summary>
internal static class TreeScanner
{
    /// <summary>
    /// Scan a tree-mode store: every file recursively, skipping directories whose
    /// name (case-insensitive) appears in <paramref name="excludeDirs"/>.
    /// </summary>
    public static TreeStats ScanTree(string path, IReadOnlyList<string> excludeDirs)
    {
        if (!Directory.Exists(path))
            return new TreeStats(0, 0);

        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        int count = 0;
        long bytes = 0;

        foreach (var fi in EnumerateFilesExcluding(root, excludeDirs))
        {
            count++;
            bytes += fi.Length;
        }

        return new TreeStats(count, bytes);
    }

    /// <summary>
    /// Scan a whitelist-mode store: only the named top-level files plus the named
    /// sub-directories (each recursively, honouring excludeDirs).
    /// </summary>
    public static TreeStats ScanWhitelist(
        string source,
        IReadOnlyList<string> files,
        IReadOnlyList<string> dirs,
        IReadOnlyList<string> excludeDirs)
    {
        int count = 0;
        long bytes = 0;

        foreach (var fileName in files)
        {
            var p = Path.Combine(source, fileName);
            if (File.Exists(p))
            {
                var fi = new FileInfo(p);
                count++;
                bytes += fi.Length;
            }
        }

        foreach (var dirName in dirs)
        {
            var p = Path.Combine(source, dirName);
            if (Directory.Exists(p))
            {
                var stats = ScanTree(p, excludeDirs);
                count += stats.Files;
                bytes += stats.Bytes;
            }
        }

        return new TreeStats(count, bytes);
    }

    /// <summary>
    /// Enumerate files under <paramref name="root"/>, skipping any directory
    /// whose name (case-insensitive) is in <paramref name="excludeDirs"/>.
    /// </summary>
    internal static IEnumerable<FileInfo> EnumerateFilesExcluding(
        string root, IReadOnlyList<string> excludeDirs)
    {
        var excludeSet = new HashSet<string>(excludeDirs, StringComparer.OrdinalIgnoreCase);

        // Use a stack-based walk so we can skip excluded directories
        // without relying on Directory.EnumerateFiles which does not
        // let us prune at the directory level.
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // Yield files in this directory
            string[] fileEntries;
            try
            {
                fileEntries = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var filePath in fileEntries)
            {
                FileInfo fi;
                try
                {
                    fi = new FileInfo(filePath);
                }
                catch { continue; }
                yield return fi;
            }

            // Push subdirectories (excluding named ones)
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var subDir in subDirs)
            {
                var name = Path.GetFileName(subDir);
                if (excludeSet.Contains(name))
                    continue;
                stack.Push(subDir);
            }
        }
    }
}
