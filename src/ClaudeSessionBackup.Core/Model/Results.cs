namespace ClaudeSessionBackup.Core.Model;

public enum LogLevel { Info, Warn, Error }

/// <summary>One line of run output. The engine reports through <c>IProgress&lt;LogLine&gt;</c>; the CLI prints it, the app appends it to the log pane, and the engine itself writes it to the run log file.</summary>
public sealed partial record LogLine(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level.ToString().ToUpperInvariant()}] {Message}";
}

public enum StoreStatus
{
    /// <summary>Copied (or nothing to copy) without failures.</summary>
    Ok,
    /// <summary>At least one file failed to copy after the retry.</summary>
    Failed,
    /// <summary>The source folder does not exist. Backup copy untouched.</summary>
    SourceMissing,
    /// <summary>Source has 0 files and the backup is empty too - an empty store, nothing to do.</summary>
    SourceEmpty,
    /// <summary>Source has 0 files but the backup holds files - what a wipe looks like. Nothing copied, backup untouched, WARN.</summary>
    SourceEmptyBackupHasData,
    /// <summary>Verify mode: compared only.</summary>
    Verified,
    /// <summary>The copy threw. Message in <see cref="StoreResult.Error"/>.</summary>
    Error,
}

/// <summary>Per-store outcome of one run.</summary>
public sealed partial record StoreResult(
    string Name,
    StoreStatus Status,
    int LiveFiles,
    long LiveBytes,
    int BackupFiles,
    long BackupBytes,
    int Copied,
    int Unchanged,
    int Failed,
    /// <summary>Files the shrink guard refused to copy over the backup copy (source smaller than backup).</summary>
    int HeldBack,
    string? Error);

/// <summary>Written to &lt;Destination&gt;\last_run.json after every run, and returned to the caller.</summary>
public sealed partial record RunManifest(
    string Stamp,
    string Mode,
    string Destination,
    bool IncludeSubagents,
    IReadOnlyList<StoreResult> Stores,
    string? CatalogPath,
    string? SnapshotPath,
    IReadOnlyList<string> Warnings,
    double Seconds,
    string LogPath)
{
    /// <summary>True when the run was refused because the lock was held.</summary>
    public bool IsRefused { get; init; }
    public bool HasFailures => Stores.Any(s => s.Status is StoreStatus.Failed or StoreStatus.Error);
    public bool HasWarnings => Warnings.Count > 0;
}
