using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Counts files and bytes under a folder tree, honouring excluded directory names
/// at any depth. Used to compute live and backup stats for the log lines and manifest,
/// and by <see cref="StoreCopier"/> to find what to copy.
/// </summary>
/// <remarks>
/// Two rules, learnt on 2026-09-06 when a backup died with "The path cannot be traversed
/// because it contains an untrusted mount point" on a junction the Claude harness had made
/// inside a session's <c>subagents\workflows</c> folder:
/// <list type="bullet">
/// <item>Directory reparse points (junctions, symlinks, mount points) are NEVER followed.
/// A target inside a store is backed up under its real path anyway; following the link
/// duplicates data, can loop forever (a junction to an ancestor), and is where Windows may
/// refuse traversal. Each one is reported once through the <see cref="SkipCallback"/> -
/// INFO when the target lies inside the store root, WARN when it points elsewhere.</item>
/// <item>A directory that cannot be read - access denied, vanished mid-scan, or any other
/// I/O error - is skipped and reported as WARN. It never aborts the store or the run.</item>
/// </list>
/// </remarks>
internal static class TreeScanner
{
    /// <summary>Called once per skipped directory: the path, why, and how loud to be.</summary>
    public delegate void SkipCallback(string path, string reason, LogLevel level);

    /// <summary>
    /// Options for the framework's own recursive enumerators (snapshot, catalog): do not
    /// descend into reparse points, and do not throw on folders that cannot be opened.
    /// A new instance each time because <see cref="EnumerationOptions"/> is mutable.
    /// </summary>
    internal static EnumerationOptions SafeRecursive => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// Scan a tree-mode store: every file recursively, skipping directories whose
    /// name (case-insensitive) appears in <paramref name="excludeDirs"/>.
    /// </summary>
    public static TreeStats ScanTree(string path, IReadOnlyList<string> excludeDirs, SkipCallback? onSkipped = null)
    {
        if (!Directory.Exists(path))
            return new TreeStats(0, 0);

        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        int count = 0;
        long bytes = 0;

        foreach (var fi in EnumerateFilesExcluding(root, excludeDirs, onSkipped))
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
        IReadOnlyList<string> excludeDirs,
        SkipCallback? onSkipped = null)
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
                var stats = ScanTree(p, excludeDirs, onSkipped);
                count += stats.Files;
                bytes += stats.Bytes;
            }
        }

        return new TreeStats(count, bytes);
    }

    /// <summary>
    /// Enumerate files under <paramref name="root"/>, skipping any directory whose name
    /// (case-insensitive) is in <paramref name="excludeDirs"/>, never following reparse
    /// points, and never throwing for a directory that cannot be read.
    /// </summary>
    internal static IEnumerable<FileInfo> EnumerateFilesExcluding(
        string root, IReadOnlyList<string> excludeDirs, SkipCallback? onSkipped = null)
    {
        var excludeSet = new HashSet<string>(excludeDirs, StringComparer.OrdinalIgnoreCase);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        // A stack-based walk so excluded and linked directories can be pruned at the
        // directory level, which Directory.EnumerateFiles does not allow.
        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // Files in this directory
            string[] fileEntries;
            try
            {
                fileEntries = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onSkipped?.Invoke(dir, "cannot list its files: " + Trim(ex.Message), LogLevel.Warn);
                continue;
            }

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

            // Sub-directories: excluded names pruned, reparse points never entered
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onSkipped?.Invoke(dir, "cannot list its folders: " + Trim(ex.Message), LogLevel.Warn);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var name = Path.GetFileName(subDir);
                if (excludeSet.Contains(name))
                    continue;

                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(subDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    onSkipped?.Invoke(subDir, "cannot read its attributes: " + Trim(ex.Message), LogLevel.Warn);
                    continue;
                }

                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    var reason = DescribeReparsePoint(subDir, rootFull, out var level);
                    onSkipped?.Invoke(subDir, reason, level);
                    continue;
                }

                stack.Push(subDir);
            }
        }
    }

    /// <summary>
    /// Text for a skipped junction or symlink. INFO when its target lies inside the store
    /// root (the data is backed up under its real path), WARN when it points elsewhere.
    /// </summary>
    private static string DescribeReparsePoint(string path, string root, out LogLevel level)
    {
        string? target = null;
        try
        {
            target = new DirectoryInfo(path).LinkTarget;
            if (target is not null && !Path.IsPathRooted(target))
                target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? path, target));
        }
        catch { target = null; }

        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var inside = target is not null &&
                     (target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                         .StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);

        level = inside ? LogLevel.Info : LogLevel.Warn;
        if (target is null)
            return "junction or symlink - not followed";
        return inside
            ? $"junction to {target} - not followed (that folder is backed up under its real path)"
            : $"junction to {target} - not followed (outside this store; add a store if that data matters)";
    }

    private static string Trim(string message) => message.Trim().TrimEnd('.');
}
