using System.IO.Compression;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Creates a snapshot zip of the small stores and the catalog folder, then handles
/// retention by moving (never deleting) old snapshots to _to_delete.
/// </summary>
internal static class SnapshotBuilder
{
    /// <summary>
    /// Build a snapshot zip containing entries rooted at each store name.
    /// E.g. "cowork-index/local_abc.json", "catalog/sessions_catalog.json".
    /// </summary>
    /// <returns>The path of the zip, or null if nothing was zipped.</returns>
    public static string? CreateSnapshot(
        BackupOptions options,
        IReadOnlyList<StoreDefinition> stores,
        string stamp,
        IProgress<LogLine>? progress)
    {
        Directory.CreateDirectory(options.SnapshotsDir);
        var zipPath = Path.Combine(options.SnapshotsDir, $"{stamp}_claude-stores.zip");

        // Collect store folders that have Snapshot=true
        var storeNames = stores.Where(s => s.Snapshot).Select(s => s.Name).ToList();
        var foldersToZip = new List<(string fullPath, string entryRoot)>();

        foreach (var name in storeNames)
        {
            var p = Path.Combine(options.LiveRoot, name);
            if (Directory.Exists(p))
                foldersToZip.Add((p, name));
        }

        // Also include the catalog folder
        if (Directory.Exists(options.CatalogDir))
            foldersToZip.Add((options.CatalogDir, "catalog"));

        if (foldersToZip.Count == 0)
        {
            progress?.Report(new LogLine(DateTime.Now, LogLevel.Warn, "snapshot: nothing to zip"));
            return null;
        }

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var (folderPath, entryRoot) in foldersToZip)
                {
                    AddFolderToArchive(archive, folderPath, entryRoot);
                }
            }

            var zipSize = new FileInfo(zipPath).Length;
            progress?.Report(new LogLine(DateTime.Now, LogLevel.Info,
                $"snapshot: {zipPath} ({FormatBytes(zipSize)})"));
        }
        catch (Exception ex)
        {
            progress?.Report(new LogLine(DateTime.Now, LogLevel.Error,
                $"snapshot FAILED: {ex.Message}"));
            // Remove partial zip so retention does not miscount it as valid
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* best effort */ }
            return null;
        }

        return zipPath;
    }

    /// <summary>
    /// Move snapshots beyond the retention count to _to_delete. Never deletes.
    /// </summary>
    public static void ApplyRetention(BackupOptions options, IProgress<LogLine>? progress)
    {
        if (options.KeepSnapshots <= 0 || !Directory.Exists(options.SnapshotsDir))
            return;

        var zips = Directory.GetFiles(options.SnapshotsDir, "*_claude-stores.zip")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

        if (zips.Count <= options.KeepSnapshots)
            return;

        Directory.CreateDirectory(options.ToDeleteDir);
        var toMove = zips.Take(zips.Count - options.KeepSnapshots).ToList();

        foreach (var old in toMove)
        {
            var dest = Path.Combine(options.ToDeleteDir, Path.GetFileName(old));
            try
            {
                File.Move(old, dest, overwrite: true);
                progress?.Report(new LogLine(DateTime.Now, LogLevel.Info,
                    $"snapshot retention: moved {Path.GetFileName(old)} to _to_delete (you empty it)"));
            }
            catch (Exception ex)
            {
                progress?.Report(new LogLine(DateTime.Now, LogLevel.Warn,
                    $"snapshot retention: failed to move {Path.GetFileName(old)}: {ex.Message}"));
            }
        }
    }

    private static void AddFolderToArchive(ZipArchive archive, string folderPath, string entryRoot)
    {
        var root = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
            // Entry names use forward slashes, rooted at the store name
            var entryName = entryRoot + "/" + rel.Replace(Path.DirectorySeparatorChar, '/');
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
    }

    private static string FormatBytes(long n) => Formatting.FormatBytes(n);
}
