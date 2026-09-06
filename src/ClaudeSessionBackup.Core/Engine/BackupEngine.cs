using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Engine;

/// <summary>
/// The backup maker. One run = every store in <see cref="KnownStores.Default"/>, then the
/// catalog, then the snapshot, then the manifest and ledger line.
/// </summary>
/// <remarks>
/// Behavioural contract (a port of Backup-ClaudeSessions.ps1, which is the reference implementation):
/// <list type="bullet">
/// <item>NEVER deletes or truncates anything at the destination. A file that vanished from the source stays in the backup.</item>
/// <item>Copy rule: copy when the backup copy is missing, differs in size, or the source LastWriteTimeUtc differs by more than 2 s. Copy to a temporary name beside the target and move into place, so a crash never leaves a truncated file that a later run would mistake for a good copy. Preserve LastWriteTime. One retry after 1 s on IOException (a transcript being appended), then count the file as Failed.</item>
/// <item>Shrink guard (stores with <see cref="StoreDefinition.ShrinkGuard"/>, *.jsonl only): a source file SMALLER than the backup copy is not copied; the source copy goes to &lt;quarantine&gt;\&lt;stamp&gt;\&lt;rel&gt; and a WARN is logged. Counted as HeldBack. In Verify mode: WARN only, no quarantine.</item>
/// <item>Empty source: a Tree store with 0 live files and a backup that holds files logs WARN "looks WIPED" (status <see cref="StoreStatus.SourceEmptyBackupHasData"/>) and copies nothing; empty on both sides is <see cref="StoreStatus.SourceEmpty"/>, INFO.</item>
/// <item>Whitelist stores copy only the named files (top level) and the named dirs (recursive, honouring ExcludeDirs); ExcludeFiles are never copied even if named.</item>
/// <item>Snapshot: after the copy, zip &lt;live&gt;\&lt;store&gt; for every store with Snapshot=true plus the catalog folder into &lt;snapshots&gt;\&lt;stamp&gt;_claude-stores.zip (entry names rooted at the store name, e.g. "cowork-index/..."). Then retention: if KeepSnapshots &gt; 0 and more zips exist, MOVE the oldest (by name) into _to_delete - never delete.</item>
/// <item>Lock: &lt;Destination&gt;\.lock holds the PID; if it exists and is younger than 3 h, the run exits with a WARN and no work. Always removed in a finally.</item>
/// <item>Logging: every line goes to &lt;logs&gt;\&lt;stamp&gt;_backup.log or _verify.log AND to the progress callback. One ledger line is appended to backup.log; last_run.json holds the manifest.</item>
/// <item>Catalog: unless NoCatalog, build via <see cref="ICatalogBuilder"/> from the LIVE stores with BackupProjectsDir=&lt;live&gt;\code-transcripts and BackupIndexDir=&lt;live&gt;\cowork-index into &lt;Destination&gt;\catalog. A catalog failure is a WARN, not a run failure.</item>
/// <item>Verify: stats + shrink scan + catalog only; no copy, no snapshot, no quarantine.</item>
/// <item>Stamp format "yyyyMMdd_HHmmss" local time. Secrets (<see cref="KnownStores.SecretFiles"/>) are never copied by any path.</item>
/// </list>
/// </remarks>
public interface IBackupEngine
{
    Task<RunManifest> RunAsync(BackupOptions options, IProgress<LogLine>? progress, CancellationToken cancellationToken);
}

/// <summary>Stats of one folder subtree, honouring excluded directory names at any depth.</summary>
public sealed partial record TreeStats(int Files, long Bytes);

public sealed partial class BackupEngine : IBackupEngine
{
    private readonly Catalog.ICatalogBuilder _catalog;
    private readonly Func<BackupOptions, IReadOnlyList<StoreDefinition>> _stores;
    private readonly Func<IReadOnlyList<StoreDefinition>, IReadOnlyList<DiscoveredRoot>> _discovery;

    /// <param name="catalog">Catalog builder; default <see cref="Catalog.CatalogBuilder"/>.</param>
    /// <param name="stores">Store factory; default <see cref="KnownStores.Default"/>. Tests inject stores rooted in temp folders.</param>
    /// <param name="discovery">
    /// Store discovery (the sweep of THIS machine for Claude data outside the covered stores). Defaults to
    /// <see cref="StoreDiscovery.Discover(IReadOnlyList{StoreDefinition})"/> when the default stores are used,
    /// and to a no-op when a store factory is injected: a machine sweep compared against temp-tree stores
    /// would only produce nonsense warnings, and would make every engine test walk the real %LOCALAPPDATA%.
    /// </param>
    public BackupEngine(
        Catalog.ICatalogBuilder? catalog = null,
        Func<BackupOptions, IReadOnlyList<StoreDefinition>>? stores = null,
        Func<IReadOnlyList<StoreDefinition>, IReadOnlyList<DiscoveredRoot>>? discovery = null)
    {
        _catalog = catalog ?? new Catalog.CatalogBuilder();
        _stores = stores ?? KnownStores.Default;
        _discovery = discovery ?? (stores is null
            ? StoreDiscovery.Discover
            : static _ => Array.Empty<DiscoveredRoot>());
    }

    public async Task<RunManifest> RunAsync(BackupOptions options, IProgress<LogLine>? progress, CancellationToken cancellationToken)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var started = Stopwatch.StartNew();
        var mode = options.Verify ? "verify" : "backup";
        var logSuffix = options.Verify ? "verify" : "backup";
        var logFile = Path.Combine(options.LogsDir, $"{stamp}_{logSuffix}.log");

        // Ensure the destination structure exists
        Directory.CreateDirectory(options.Destination);
        Directory.CreateDirectory(options.LiveRoot);
        Directory.CreateDirectory(options.LogsDir);

        var log = new RunLogWriter(logFile, progress);

        // Lock: refuse if another run is active
        using var lockMgr = new LockManager(options.LockFile);
        if (!lockMgr.TryAcquire(progress))
        {
            // Return a manifest that signals refusal (no stores processed)
            return new RunManifest(
                stamp, mode, options.Destination, options.IncludeSubagents,
                Array.Empty<StoreResult>(), null, null,
                new[] { $"another run appears active (lock {options.LockFile} is fresh) - exiting" },
                started.Elapsed.TotalSeconds, logFile)
            { IsRefused = true, Machine = Environment.MachineName };
        }

        log.Log($"=== Backup-ClaudeSessions {mode.ToUpperInvariant()} {stamp} ===");
        log.Log($"destination: {options.Destination}");
        if (!options.IncludeSubagents)
            log.Log("subagent transcripts excluded (pass --include-subagents to add ~1.2 GB of workflow scratch)");

        var stores = _stores(options);
        var results = new List<StoreResult>();
        var quarantine = Path.Combine(options.QuarantineDir, stamp);

        foreach (var store in stores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoreResult result;
            try
            {
                result = ProcessStore(store, options, log, quarantine, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One store must never take the run down with it: the other stores still get
                // copied and last_run.json is still written. 2026-09-06: an I/O error raised
                // while counting code-transcripts (a junction Windows refused to traverse)
                // escaped ProcessStore and aborted the whole run before any manifest existed.
                log.Log($"{store.Name}: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                result = new StoreResult(store.Name, StoreStatus.Error, 0, 0, 0, 0, 0, 0, 0, 0, ex.Message);
            }
            results.Add(result);
        }

        // Store discovery: warn about Claude data roots that the backup does not cover.
        // Runs in both backup and verify modes, before the catalog, so the user learns
        // about uncovered data even if the catalog step is skipped.
        var discovered = _discovery(stores);
        foreach (var root in discovered)
        {
            var size = Formatting.FormatBytes(root.Bytes);
            log.Log($"UNCOVERED: {root.Path} - {root.Reason} ({root.Files} files, {size}) - " +
                    "not backed up; add a store or tell the author", LogLevel.Warn);
        }

        // Catalog: unless NoCatalog, build from the LIVE sources
        string? catalogPath = null;
        if (!options.NoCatalog)
        {
            catalogPath = await BuildCatalogSafe(options, stores, log, cancellationToken);
        }

        // Snapshot (backup mode only, unless --no-snapshot)
        string? snapshotPath = null;
        if (!options.Verify && !options.NoSnapshot)
        {
            snapshotPath = SnapshotBuilder.CreateSnapshot(options, stores, stamp, progress);
            if (snapshotPath != null)
                SnapshotBuilder.ApplyRetention(options, progress);
        }

        // Shared-destination detection: read the previous last_run.json (if any) and warn
        // when the previous run came from a different machine. Two machines writing the same
        // destination stomp each other's last_run.json, backup.log, and catalog.
        var thisMachine = Environment.MachineName;
        DetectSharedDestination(options, thisMachine, log);

        started.Stop();
        var seconds = Math.Round(started.Elapsed.TotalSeconds, 1);

        var manifest = new RunManifest(
            stamp, mode, options.Destination, options.IncludeSubagents,
            results, catalogPath, snapshotPath,
            log.Warnings.ToList(), seconds, logFile)
        { Discovered = discovered, Machine = thisMachine };

        // Write manifest (last_run.json) and ledger (backup.log)
        ManifestWriter.Write(options, manifest);

        var ledgerNote = snapshotPath != null
            ? $", snapshot {Path.GetFileName(snapshotPath)}"
            : "";
        log.Log($"done: {DateTime.Now:yyyy-MM-dd HH:mm:ss} {mode,-6} {results.Count} stores, " +
                $"{log.Warnings.Count} warnings, {seconds:N0}s{ledgerNote}");

        if (log.Warnings.Count > 0)
        {
            log.Log($"{log.Warnings.Count} warning(s):", LogLevel.Warn);
        }

        return manifest;
    }

    /// <summary>
    /// Process a single store: stats, shrink scan, copy (or verify), log line.
    /// </summary>
    private StoreResult ProcessStore(
        StoreDefinition store,
        BackupOptions options,
        RunLogWriter log,
        string quarantine,
        CancellationToken ct)
    {
        var dest = Path.Combine(options.LiveRoot, store.Name);

        // Source missing?
        if (!Directory.Exists(store.Source))
        {
            if (store.Optional)
            {
                // Optional stores whose source does not exist are expected (e.g. the MSIX
                // container's Roaming\Claude comes and goes). INFO, not a warning.
                log.Log($"{store.Name}: optional source absent ({store.Source}) - nothing to copy");
            }
            else
            {
                log.Log($"{store.Name}: source missing ({store.Source}) - skipped; backup copy untouched", LogLevel.Warn);
            }
            return new StoreResult(store.Name, StoreStatus.SourceMissing, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        // Directories the walk does not enter (junctions, unreadable folders) are logged
        // once per store, at the level the scanner chose: INFO for a junction whose target
        // is inside the store (its files are copied under the real path), WARN otherwise.
        var skippedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TreeScanner.SkipCallback onSkipped = (path, reason, level) =>
        {
            if (skippedDirs.Add(path))
                log.Log($"{store.Name}: skipped {path} - {reason}", level);
        };

        // Compute live stats
        var live = store.Mode == StoreMode.Tree
            ? TreeScanner.ScanTree(store.Source, store.ExcludeDirs, onSkipped)
            : TreeScanner.ScanWhitelist(store.Source, store.Files, store.Dirs, store.ExcludeDirs, onSkipped);

        // Empty-source detection (tree mode only)
        if (store.Mode == StoreMode.Tree && live.Files == 0)
        {
            var bakStats = TreeScanner.ScanTree(dest, store.ExcludeDirs);
            if (bakStats.Files > 0)
            {
                log.Log($"{store.Name}: source has 0 files but the backup holds {bakStats.Files} - " +
                        "looks WIPED; nothing copied, backup copy untouched. See RESTORE.md", LogLevel.Warn);
                if (store.Name == KnownStores.CoworkIndex)
                    log.Log("cowork-index wiped: deleted_<cliSessionId> markers and sidebar records now come from the backup copy; " +
                            "restore per RESTORE.md section 2A before any rebuild", LogLevel.Warn);

                return new StoreResult(store.Name, StoreStatus.SourceEmptyBackupHasData,
                    0, 0, bakStats.Files, bakStats.Bytes, 0, 0, 0, 0, null);
            }
            else
            {
                log.Log($"{store.Name}: source has 0 files and the backup is empty too - nothing to copy");
                return new StoreResult(store.Name, StoreStatus.SourceEmpty, 0, 0, 0, 0, 0, 0, 0, 0, null);
            }
        }

        // Shrink guard (stores with ShrinkGuard = true, *.jsonl only)
        var heldBackPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (store.ShrinkGuard)
        {
            var shrunk = ShrinkGuard.Scan(
                store.Source, dest, store.ExcludeDirs,
                options.Verify ? null : quarantine,
                options.Verify,
                new Progress<LogLine>(line => log.Log(line.Message, line.Level)));

            foreach (var p in shrunk)
                heldBackPaths.Add(p);
        }

        // Verify mode: stats only, no copy
        if (options.Verify)
        {
            var bak = TreeScanner.ScanTree(dest, store.ExcludeDirs);
            log.Log(string.Format("{0,-18} live {1,6} files {2,10}   backup {3,6} files {4,10}",
                store.Name, live.Files, FormatBytes(live.Bytes), bak.Files, FormatBytes(bak.Bytes)));

            return new StoreResult(store.Name, StoreStatus.Verified,
                live.Files, live.Bytes, bak.Files, bak.Bytes, 0, 0, 0, heldBackPaths.Count, null);
        }

        // Copy
        CopyResult agg;
        try
        {
            if (store.Mode == StoreMode.Tree)
            {
                agg = StoreCopier.CopyTree(
                    store.Source, dest,
                    store.ExcludeDirs, store.ExcludeFiles,
                    heldBackPaths, ct,
                    new Progress<LogLine>(line => log.Log(line.Message, line.Level)),
                    onSkipped);
            }
            else
            {
                agg = StoreCopier.CopyWhitelist(
                    store.Source, dest,
                    store.Files, store.Dirs,
                    store.ExcludeDirs, store.ExcludeFiles,
                    heldBackPaths, ct,
                    new Progress<LogLine>(line => log.Log(line.Message, line.Level)),
                    onSkipped);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Log($"{store.Name}: copy threw {ex.Message}", LogLevel.Error);
            return new StoreResult(store.Name, StoreStatus.Error,
                live.Files, live.Bytes, 0, 0, 0, 0, 0, heldBackPaths.Count, ex.Message);
        }

        var status = agg.Failed > 0 ? StoreStatus.Failed : StoreStatus.Ok;
        if (status == StoreStatus.Failed)
        {
            log.Log($"{store.Name}: copy reported failures (failed {agg.Failed}) - see {log.LogPath}", LogLevel.Error);
        }

        var heldBackNote = heldBackPaths.Count > 0
            ? $", {heldBackPaths.Count} held back by shrink guard"
            : "";
        log.Log(string.Format("{0,-18} {1,6} files seen, {2,5} copied, {3,5} unchanged{4}",
            store.Name, agg.Total, agg.Copied, agg.Unchanged, heldBackNote));

        return new StoreResult(store.Name, status,
            live.Files, live.Bytes, 0, 0,
            agg.Copied, agg.Unchanged, agg.Failed, heldBackPaths.Count, null);
    }

    /// <summary>
    /// Build the catalog, catching any failure as a WARN (not a run failure).
    /// The catalog is built from the LIVE stores, cross-checked against the backup copy.
    /// </summary>
    private async Task<string?> BuildCatalogSafe(
        BackupOptions options,
        IReadOnlyList<StoreDefinition> stores,
        RunLogWriter log,
        CancellationToken ct)
    {
        try
        {
            var transcriptStore = stores.FirstOrDefault(s => s.Name == KnownStores.CodeTranscripts);
            var indexStore = stores.FirstOrDefault(s => s.Name == KnownStores.CoworkIndex);

            var catalogOptions = new CatalogOptions
            {
                ProjectsDir = transcriptStore?.Source ?? ClaudePaths.Projects,
                IndexDir = indexStore?.Source ?? ClaudePaths.SidebarIndex,
                AgentModeDir = ClaudePaths.AgentModeSessions,
                BackupProjectsDir = Path.Combine(options.LiveRoot, KnownStores.CodeTranscripts),
                BackupIndexDir = Path.Combine(options.LiveRoot, KnownStores.CoworkIndex),
                OutDir = options.CatalogDir,
                IncludeSubagents = options.IncludeSubagents,
            };

            var catalog = await _catalog.BuildAsync(catalogOptions,
                new Progress<LogLine>(line => log.Log($"catalog: {line.Message}", line.Level)),
                ct);

            var catalogJson = Path.Combine(options.CatalogDir, "sessions_catalog.json");
            if (File.Exists(catalogJson))
                return catalogJson;
        }
        catch (NotImplementedException)
        {
            // The catalog builder is a stub in the skeleton - this is expected during
            // testing with the default CatalogBuilder.
            log.Log("catalog builder not yet implemented - catalog skipped", LogLevel.Warn);
        }
        catch (Exception ex)
        {
            log.Log($"catalog failed: {ex.Message}", LogLevel.Warn);
        }

        return null;
    }

    /// <summary>
    /// Read the previous last_run.json and warn if it was written by a different machine.
    /// Tolerates absence, corruption, and missing fields: a bad file must never prevent a run.
    /// </summary>
    private static void DetectSharedDestination(BackupOptions options, string thisMachine, RunLogWriter log)
    {
        try
        {
            if (!File.Exists(options.ManifestFile)) return;

            var json = File.ReadAllText(options.ManifestFile);
            var readOpts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            };
            var prev = JsonSerializer.Deserialize<RunManifest>(json, readOpts);
            if (prev?.Machine is null || string.IsNullOrEmpty(prev.Machine)) return;

            if (!string.Equals(prev.Machine, thisMachine, StringComparison.OrdinalIgnoreCase))
            {
                log.Log(
                    $"shared destination: the previous run here was from {prev.Machine} - " +
                    "last_run.json, backup.log and the catalog now describe THIS machine; " +
                    $"use one destination per machine (e.g. {options.Destination}\\{thisMachine}) to keep them apart",
                    LogLevel.Warn);
            }
        }
        catch
        {
            // A corrupt or unreadable last_run.json must never prevent the run.
        }
    }

    private static string FormatBytes(long n) => Formatting.FormatBytes(n);
}
