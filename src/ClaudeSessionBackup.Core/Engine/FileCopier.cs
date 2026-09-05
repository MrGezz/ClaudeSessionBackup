namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// Copies a single file using temp-name + move so a crash never leaves a half-written
/// file that looks valid. One retry after 1 s on IOException (a transcript being appended).
/// Preserves LastWriteTime.
/// </summary>
internal static class FileCopier
{
    /// <summary>
    /// Copy <paramref name="sourceFile"/> to <paramref name="destFile"/>. The file is first
    /// written beside the target under a ".partial" name, then moved into place atomically
    /// (on NTFS). If an IOException occurs, waits 1 s and retries once.
    /// </summary>
    /// <returns>True if the copy succeeded; false if both attempts failed.</returns>
    public static bool CopyWithTempAndMove(string sourceFile, string destFile, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destFile)!;
        Directory.CreateDirectory(dir);

        var tempFile = destFile + ".partial";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Copy(sourceFile, tempFile, overwrite: true);
                // Preserve the source's last-write time on the temp file before moving
                var srcInfo = new FileInfo(sourceFile);
                File.SetLastWriteTimeUtc(tempFile, srcInfo.LastWriteTimeUtc);
                File.Move(tempFile, destFile, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt == 0)
            {
                // One retry after 1 s - the transcript may be in use
                CleanPartial(tempFile);
                Thread.Sleep(1000);
            }
            catch (Exception)
            {
                CleanPartial(tempFile);
                if (attempt == 0)
                {
                    Thread.Sleep(1000);
                    continue;
                }
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Remove a leftover .partial file. Best-effort; we never want to leave partial files.
    /// </summary>
    internal static void CleanPartial(string tempFile)
    {
        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best effort */ }
    }

    /// <summary>
    /// Clean up any .partial files in <paramref name="directory"/> recursively.
    /// Called on cancellation to honour the contract: a cancelled run must leave
    /// no *.partial temp files behind.
    /// </summary>
    public static void CleanAllPartials(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        try
        {
            foreach (var partial in Directory.EnumerateFiles(directory, "*.partial", SearchOption.AllDirectories))
            {
                try { File.Delete(partial); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }
}
