using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Copies one store from source to destination, honouring the copy rule:
///   - copy when the backup copy is missing, differs in size, or the source
///     LastWriteTimeUtc differs by more than 2 s
///   - copy to temp name + move via <see cref="FileCopier"/>
///   - files in parallel (max degree 8), counters thread-safe
///   - never deletes at the destination (no /MIR, no /PURGE)
///   - secrets in <see cref="KnownStores.SecretFiles"/> are never copied
///   - files in <paramref name="excludeFullPaths"/> (shrink guard) are skipped
/// </summary>
internal static class StoreCopier
{
    /// <summary>
    /// Copy a tree-mode store.
    /// </summary>
    public static CopyResult CopyTree(
        string source, string dest,
        IReadOnlyList<string> excludeDirs,
        IReadOnlyList<string> excludeFiles,
        HashSet<string> excludeFullPaths,
        CancellationToken ct,
        IProgress<LogLine>? progress)
    {
        Directory.CreateDirectory(dest);

        var root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        var excludeFileSet = new HashSet<string>(excludeFiles, StringComparer.OrdinalIgnoreCase);
        var secretSet = new HashSet<string>(KnownStores.SecretFiles, StringComparer.OrdinalIgnoreCase);

        // Gather all source files
        var filesToProcess = new List<(FileInfo info, string rel)>();
        foreach (var fi in TreeScanner.EnumerateFilesExcluding(root, excludeDirs))
        {
            var rel = fi.FullName.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
            var fileName = fi.Name;

            // Skip secrets
            if (secretSet.Contains(fileName))
                continue;

            // Skip excluded files (by name)
            if (excludeFileSet.Contains(fileName))
                continue;

            // Skip shrink-guarded files (by full path)
            if (excludeFullPaths.Contains(fi.FullName))
                continue;

            filesToProcess.Add((fi, rel));
        }

        return CopyFileList(filesToProcess, dest, ct);
    }

    /// <summary>
    /// Copy a whitelist-mode store: named top-level files + named dirs recursively.
    /// </summary>
    public static CopyResult CopyWhitelist(
        string source, string dest,
        IReadOnlyList<string> files,
        IReadOnlyList<string> dirs,
        IReadOnlyList<string> excludeDirs,
        IReadOnlyList<string> excludeFiles,
        HashSet<string> excludeFullPaths,
        CancellationToken ct,
        IProgress<LogLine>? progress)
    {
        Directory.CreateDirectory(dest);

        var excludeFileSet = new HashSet<string>(excludeFiles, StringComparer.OrdinalIgnoreCase);
        var secretSet = new HashSet<string>(KnownStores.SecretFiles, StringComparer.OrdinalIgnoreCase);
        var filesToProcess = new List<(FileInfo info, string rel)>();

        // Named top-level files
        foreach (var fileName in files)
        {
            if (secretSet.Contains(fileName) || excludeFileSet.Contains(fileName))
                continue;

            var p = Path.Combine(source, fileName);
            if (!File.Exists(p))
                continue;

            var fi = new FileInfo(p);
            if (excludeFullPaths.Contains(fi.FullName))
                continue;

            filesToProcess.Add((fi, fileName));
        }

        // Named subdirectories - walk each recursively
        foreach (var dirName in dirs)
        {
            var dirPath = Path.Combine(source, dirName);
            if (!Directory.Exists(dirPath))
                continue;

            var dirRoot = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var fi in TreeScanner.EnumerateFilesExcluding(dirRoot, excludeDirs))
            {
                if (secretSet.Contains(fi.Name) || excludeFileSet.Contains(fi.Name))
                    continue;
                if (excludeFullPaths.Contains(fi.FullName))
                    continue;

                var relInDir = fi.FullName.Substring(dirRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                var rel = Path.Combine(dirName, relInDir);
                filesToProcess.Add((fi, rel));
            }
        }

        return CopyFileList(filesToProcess, dest, ct);
    }

    /// <summary>
    /// Process the list of files in parallel (max degree 8), applying the copy rule.
    /// </summary>
    private static CopyResult CopyFileList(
        List<(FileInfo info, string rel)> filesToProcess,
        string dest,
        CancellationToken ct)
    {
        int total = filesToProcess.Count;
        int copied = 0;
        int unchanged = 0;
        int failed = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = ct,
        };

        try
        {
            Parallel.ForEach(filesToProcess, parallelOptions, item =>
            {
                ct.ThrowIfCancellationRequested();

                var (srcInfo, rel) = item;
                var destFile = Path.Combine(dest, rel);

                if (!NeedsCopy(srcInfo, destFile))
                {
                    Interlocked.Increment(ref unchanged);
                    return;
                }

                if (FileCopier.CopyWithTempAndMove(srcInfo.FullName, destFile, ct))
                {
                    Interlocked.Increment(ref copied);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Clean up partials on cancellation
            FileCopier.CleanAllPartials(dest);
            throw;
        }

        return new CopyResult(total, copied, unchanged, failed);
    }

    /// <summary>
    /// The copy rule: copy when the backup is missing, differs in size, or the
    /// source LastWriteTimeUtc differs by more than 2 seconds.
    /// </summary>
    private static bool NeedsCopy(FileInfo source, string destPath)
    {
        if (!File.Exists(destPath))
            return true;

        var destInfo = new FileInfo(destPath);

        if (source.Length != destInfo.Length)
            return true;

        var timeDiff = Math.Abs((source.LastWriteTimeUtc - destInfo.LastWriteTimeUtc).TotalSeconds);
        return timeDiff > 2;
    }
}

/// <summary>Aggregated counts from one store copy.</summary>
internal readonly record struct CopyResult(int Total, int Copied, int Unchanged, int Failed);
