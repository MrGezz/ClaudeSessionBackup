using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Shrink guard: a source *.jsonl file that is SMALLER than the backup copy is held back.
/// In backup mode, the source copy is quarantined under &lt;quarantine&gt;\&lt;stamp&gt;\&lt;rel&gt;.
/// In verify mode, a WARN is logged but nothing is quarantined.
/// Returns the set of source full paths that should be excluded from the copy.
/// </summary>
internal static class ShrinkGuard
{
    /// <summary>
    /// Scan for shrunken transcripts in a tree-mode store.
    /// </summary>
    /// <param name="sourceRoot">The live source root.</param>
    /// <param name="backupRoot">The backup destination root for this store.</param>
    /// <param name="excludeDirs">Directories to skip (e.g. "subagents").</param>
    /// <param name="quarantineDir">Timestamped quarantine folder; null in verify mode.</param>
    /// <param name="verifyOnly">True = WARN only, no quarantine.</param>
    /// <param name="progress">Progress callback for log lines.</param>
    /// <returns>Full paths of source files that were held back.</returns>
    public static List<string> Scan(
        string sourceRoot,
        string backupRoot,
        IReadOnlyList<string> excludeDirs,
        string? quarantineDir,
        bool verifyOnly,
        IProgress<LogLine>? progress)
    {
        var heldBack = new List<string>();

        if (!Directory.Exists(backupRoot))
            return heldBack;

        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var excludeSet = new HashSet<string>(excludeDirs, StringComparer.OrdinalIgnoreCase);

        foreach (var fi in EnumerateJsonlFiles(root, excludeSet))
        {
            var rel = fi.FullName.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
            var backupFile = Path.Combine(backupRoot, rel);

            if (!File.Exists(backupFile))
                continue;

            var backupLength = new FileInfo(backupFile).Length;
            if (fi.Length < backupLength)
            {
                heldBack.Add(fi.FullName);

                if (verifyOnly)
                {
                    progress?.Report(new LogLine(DateTime.Now, LogLevel.Warn,
                        $"SHRUNK: {rel} is {fi.Length} B live but {backupLength} B in backup"));
                }
                else
                {
                    // Quarantine the source copy
                    if (quarantineDir != null)
                    {
                        var qPath = Path.Combine(quarantineDir, rel);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(qPath)!);
                            File.Copy(fi.FullName, qPath, overwrite: true);
                        }
                        catch
                        {
                            // Best effort - quarantine is a diagnostic aid, not critical
                        }
                    }

                    progress?.Report(new LogLine(DateTime.Now, LogLevel.Warn,
                        $"SHRINK GUARD: {rel} is {fi.Length} B at source but {backupLength} B in backup - backup kept, source copy quarantined"));
                }
            }
        }

        return heldBack;
    }

    private static IEnumerable<FileInfo> EnumerateJsonlFiles(string root, HashSet<string> excludeSet)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir, "*.jsonl"); }
            catch { continue; }

            foreach (var f in files)
            {
                FileInfo fi;
                try { fi = new FileInfo(f); }
                catch { continue; }
                yield return fi;
            }

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sd in subDirs)
            {
                if (excludeSet.Contains(Path.GetFileName(sd)))
                    continue;
                stack.Push(sd);
            }
        }
    }
}
