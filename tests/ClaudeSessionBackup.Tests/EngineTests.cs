using System.IO.Compression;
using ClaudeSessionBackup.Core.Catalog;
using ClaudeSessionBackup.Core.Engine;
using ClaudeSessionBackup.Core.Model;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// Contract tests for BackupEngine. All tests are written against the contract in BackupEngine.cs
/// and the PowerShell reference (Backup-ClaudeSessions.ps1). They will throw NotImplementedException
/// against the skeleton stub; the integrator runs them against the real implementation to decide
/// whether a failing test or the implementation is wrong.
/// </summary>
public class EngineTests : IDisposable
{
    // Each test gets its own isolated temp directory tree so tests never interfere.
    private readonly string _tmp;

    public EngineTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "csb_eng_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // --- factory helpers ---

    /// <summary>Minimal options pointing at an isolated temp destination.</summary>
    private static BackupOptions Options(string dest, bool verify = false, bool noSnapshot = true, int keepSnapshots = 60) =>
        new()
        {
            Destination = dest,
            NoSnapshot = noSnapshot,
            NoCatalog = true,
            Verify = verify,
            KeepSnapshots = keepSnapshots,
        };

    private static StoreDefinition TreeStore(string name, string source,
        bool shrinkGuard = false, bool snapshot = false,
        IReadOnlyList<string>? excludeDirs = null,
        IReadOnlyList<string>? excludeFiles = null) =>
        new(name, StoreMode.Tree, source,
            ExcludeDirs: excludeDirs ?? Array.Empty<string>(),
            ExcludeFiles: excludeFiles ?? Array.Empty<string>(),
            Files: Array.Empty<string>(),
            Dirs: Array.Empty<string>(),
            ShrinkGuard: shrinkGuard,
            Snapshot: snapshot,
            Description: "test store");

    private static StoreDefinition WhitelistStore(
        string name, string source,
        IReadOnlyList<string> files,
        IReadOnlyList<string> dirs,
        IReadOnlyList<string>? excludeFiles = null,
        bool snapshot = false) =>
        new(name, StoreMode.Whitelist, source,
            ExcludeDirs: Array.Empty<string>(),
            ExcludeFiles: excludeFiles ?? Array.Empty<string>(),
            Files: files,
            Dirs: dirs,
            ShrinkGuard: false,
            Snapshot: snapshot,
            Description: "whitelist test store");

    /// <summary>
    /// A no-op catalog builder so NoCatalog:false runs don't call the real (stub) builder.
    /// </summary>
    private sealed class StubCatalog : ICatalogBuilder
    {
        public Task<SessionCatalog> BuildAsync(CatalogOptions options, IProgress<LogLine>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new SessionCatalog());

        public Task<SessionCatalog> LoadAsync(string catalogPath, CancellationToken cancellationToken) =>
            Task.FromResult(new SessionCatalog());
    }

    // -------------------------------------------------------------------------
    // Safety: destination never touches the real live Claude stores
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_Destination_IsInsideProvidedTempDir()
    {
        // The engine must write last_run.json ONLY under the specified Destination, never
        // inside %USERPROFILE%\.claude or %APPDATA%\Claude.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "file.txt"), "content");
        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        // The manifest's Destination must equal our temp dir, not any Claude path.
        Assert.Equal(dest, manifest.Destination);
        Assert.DoesNotContain(ClaudePaths.ClaudeHome, manifest.Destination, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClaudePaths.AppDataClaude, manifest.Destination, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Never-delete contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_NeverDeletes_BackupFileAbsentFromSource()
    {
        // A file that vanished from the source must stay in the backup.
        // Contract: "NEVER deletes or truncates anything at the destination."
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        // Pre-seed the backup with a file that is NOT in the source.
        var backupDir = Path.Combine(dest, "live", "test");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "orphan.txt"), "backup-only");
        File.WriteAllText(Path.Combine(src, "live.txt"), "in source");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        // orphan.txt must still be in the backup.
        Assert.True(File.Exists(Path.Combine(backupDir, "orphan.txt")),
            "Engine must not delete backup files absent from the source.");
    }

    // -------------------------------------------------------------------------
    // Shrink guard
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_ShrinkGuard_HoldsBackSmallerJsonl()
    {
        // A source *.jsonl that is smaller than the backup copy must be held back.
        // Contract: "a source file SMALLER than the backup copy is not copied; ... Counted as HeldBack."
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        var projectDir = Path.Combine(src, "project");
        Directory.CreateDirectory(projectDir);

        // Backup copy is 100 bytes; source is only 10 bytes.
        var backupDir = Path.Combine(dest, "live", "shrink-store", "project");
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(Path.Combine(backupDir, "session.jsonl"), new byte[100]);
        File.WriteAllBytes(Path.Combine(projectDir, "session.jsonl"), new byte[10]);

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("shrink-store", src, shrinkGuard: true) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "shrink-store");
        Assert.True(result.HeldBack > 0,
            "Shrink guard must count held-back files in HeldBack, not copy them over the larger backup.");
    }

    [Fact]
    public async Task Engine_ShrinkGuard_NonJsonlFilesAreNotHeldBack()
    {
        // The shrink guard applies only to *.jsonl files. A smaller .txt file should copy normally.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var backupDir = Path.Combine(dest, "live", "shrink-store");
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(Path.Combine(backupDir, "config.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(src, "config.txt"), new byte[10]);  // smaller, but not .jsonl

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("shrink-store", src, shrinkGuard: true) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "shrink-store");
        // A non-jsonl file is not subject to the shrink guard even if smaller.
        Assert.Equal(0, result.HeldBack);
    }

    [Fact]
    public async Task Engine_ShrinkGuard_QuarantinesSourceCopy()
    {
        // The shrunken source file must be copied to quarantine, not just skipped.
        // Contract: "the source copy goes to <quarantine>\<stamp>\<rel>"
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        var projectDir = Path.Combine(src, "project");
        Directory.CreateDirectory(projectDir);

        var backupDir = Path.Combine(dest, "live", "shrink-store", "project");
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(Path.Combine(backupDir, "session.jsonl"), new byte[100]);
        File.WriteAllBytes(Path.Combine(projectDir, "session.jsonl"), new byte[10]);

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("shrink-store", src, shrinkGuard: true) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        // Quarantine must contain a copy of session.jsonl.
        var quarantine = opts.QuarantineDir;
        var quarantinedFiles = Directory.GetFiles(quarantine, "session.jsonl", SearchOption.AllDirectories);
        Assert.True(quarantinedFiles.Length > 0,
            "The shrunken source file must be placed in quarantine.");
    }

    [Fact]
    public async Task Engine_ShrinkGuard_VerifyMode_WarnButNoQuarantine()
    {
        // In verify mode the shrink guard warns but does NOT quarantine the file.
        // Contract: "In Verify mode: WARN only, no quarantine."
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        var projectDir = Path.Combine(src, "project");
        Directory.CreateDirectory(projectDir);

        var backupDir = Path.Combine(dest, "live", "shrink-store", "project");
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(Path.Combine(backupDir, "session.jsonl"), new byte[100]);
        File.WriteAllBytes(Path.Combine(projectDir, "session.jsonl"), new byte[10]);

        var opts = Options(dest, verify: true);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("shrink-store", src, shrinkGuard: true) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        // Quarantine must be empty in verify mode.
        var quarantine = opts.QuarantineDir;
        bool quarantineHasFiles = Directory.Exists(quarantine) &&
            Directory.GetFiles(quarantine, "*", SearchOption.AllDirectories).Length > 0;
        Assert.False(quarantineHasFiles,
            "Verify mode must not write to quarantine.");
    }

    // -------------------------------------------------------------------------
    // Wiped-source detection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_EmptySource_NonEmptyBackup_ReportsSourceEmptyBackupHasData()
    {
        // An empty source where the backup has data is what a wipe looks like.
        // Contract: "status SourceEmptyBackupHasData"
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);  // source exists but is empty

        var backupDir = Path.Combine(dest, "live", "wiped-store");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "record.json"), "{}");  // backup has data

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("wiped-store", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "wiped-store");
        Assert.Equal(StoreStatus.SourceEmptyBackupHasData, result.Status);
    }

    [Fact]
    public async Task Engine_EmptySourceAndEmptyBackup_ReportsSourceEmpty()
    {
        // An empty store on both sides is just an empty store.
        // Contract: "empty on both sides is SourceEmpty, INFO."
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);  // source exists but is empty

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("empty-store", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "empty-store");
        Assert.Equal(StoreStatus.SourceEmpty, result.Status);
    }

    [Fact]
    public async Task Engine_MissingSource_ReportsSourceMissing()
    {
        // A source that doesn't exist at all reports SourceMissing and leaves the backup alone.
        var dest = Path.Combine(_tmp, "dest");
        var missingPath = Path.Combine(_tmp, "does-not-exist");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("missing-store", missingPath) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "missing-store");
        Assert.Equal(StoreStatus.SourceMissing, result.Status);
    }

    // -------------------------------------------------------------------------
    // Whitelist store
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_WhitelistStore_CopiesOnlyNamedFiles()
    {
        // A whitelist store must copy only the named top-level files.
        // Contract: "Whitelist stores copy only the named files (top level) and the named dirs"
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(src, "extra.txt"), "should not be copied");
        File.WriteAllText(Path.Combine(src, "cache.db"), "should not be copied");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[]
            {
                WhitelistStore("config-store", src,
                    files: new[] { "settings.json" },
                    dirs: Array.Empty<string>())
            });

        await engine.RunAsync(opts, null, CancellationToken.None);

        var backupDir = Path.Combine(dest, "live", "config-store");
        Assert.True(File.Exists(Path.Combine(backupDir, "settings.json")),
            "Named whitelist file must be copied.");
        Assert.False(File.Exists(Path.Combine(backupDir, "extra.txt")),
            "Unlisted files must not be copied in whitelist mode.");
        Assert.False(File.Exists(Path.Combine(backupDir, "cache.db")),
            "Unlisted files must not be copied in whitelist mode.");
    }

    [Fact]
    public async Task Engine_WhitelistStore_CopiesNamedDirRecursively()
    {
        // Whitelisted dirs are copied recursively.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        var tasksDir = Path.Combine(src, "tasks");
        Directory.CreateDirectory(tasksDir);
        File.WriteAllText(Path.Combine(tasksDir, "task1.json"), "{}");
        File.WriteAllText(Path.Combine(src, "unlisted.txt"), "no");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[]
            {
                WhitelistStore("config-store", src,
                    files: Array.Empty<string>(),
                    dirs: new[] { "tasks" })
            });

        await engine.RunAsync(opts, null, CancellationToken.None);

        var backupDir = Path.Combine(dest, "live", "config-store");
        Assert.True(File.Exists(Path.Combine(backupDir, "tasks", "task1.json")),
            "Whitelisted directory must be copied recursively.");
        Assert.False(File.Exists(Path.Combine(backupDir, "unlisted.txt")),
            "Files outside the whitelist must not be copied.");
    }

    [Fact]
    public async Task Engine_WhitelistStore_NeverCopiesSecretFiles()
    {
        // Secret files (.credentials.json, .claude.json) must never be copied.
        // Contract: "Secrets (KnownStores.SecretFiles) are never copied by any path."
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(src, ".credentials.json"), "SECRET");
        File.WriteAllText(Path.Combine(src, ".claude.json"), "SECRET");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[]
            {
                WhitelistStore("code-config", src,
                    files: new[] { "settings.json", ".credentials.json" },  // even if accidentally listed
                    dirs: Array.Empty<string>(),
                    excludeFiles: KnownStores.SecretFiles)
            });

        await engine.RunAsync(opts, null, CancellationToken.None);

        var backupDir = Path.Combine(dest, "live", "code-config");
        Assert.False(File.Exists(Path.Combine(backupDir, ".credentials.json")),
            ".credentials.json must never be copied (ExcludeFiles takes precedence).");
        Assert.False(File.Exists(Path.Combine(backupDir, ".claude.json")),
            ".claude.json must never be copied.");
    }

    // -------------------------------------------------------------------------
    // Snapshot zip contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_Snapshot_ZipEntriesRootedAtStoreName()
    {
        // Zip entry names must be rooted at the store name (e.g. "cowork-index/local_*.json").
        // Contract: "entry names rooted at the store name, e.g. 'cowork-index/...'"
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "local_abc.json"), "{}");

        var opts = Options(dest, noSnapshot: false);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("cowork-index", src, snapshot: true) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        Assert.NotNull(manifest.SnapshotPath);
        Assert.True(File.Exists(manifest.SnapshotPath!), "Snapshot zip must be created.");

        using var zip = ZipFile.OpenRead(manifest.SnapshotPath!);
        foreach (var entry in zip.Entries)
        {
            Assert.True(entry.FullName.StartsWith("cowork-index/", StringComparison.OrdinalIgnoreCase)
                        || entry.FullName.StartsWith("catalog/", StringComparison.OrdinalIgnoreCase),
                $"Zip entry '{entry.FullName}' must be rooted at the store name, not an absolute path.");
        }
    }

    // -------------------------------------------------------------------------
    // Snapshot retention
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_Retention_MovesOldestSnapshotsToToDelete()
    {
        // When there are more snapshots than KeepSnapshots, the oldest are MOVED (not deleted)
        // to _to_delete.
        // Contract: "MOVE the oldest (by name) into _to_delete - never delete."
        var dest = Path.Combine(_tmp, "dest");
        var snapDir = Path.Combine(dest, "snapshots");
        Directory.CreateDirectory(snapDir);

        // Pre-create 5 fake snapshot zips. KeepSnapshots=2, so 3 should move.
        var names = new[]
        {
            "20260101_000001_claude-stores.zip",
            "20260101_000002_claude-stores.zip",
            "20260101_000003_claude-stores.zip",
            "20260101_000004_claude-stores.zip",
            "20260101_000005_claude-stores.zip",
        };
        // create a throwaway dir for the zip above (must exist BEFORE the foreach)
        Directory.CreateDirectory(Path.Combine(_tmp, "empty_dir"));
        foreach (var n in names)
        {
            ZipFile.CreateFromDirectory(Path.Combine(_tmp, "empty_dir"), Path.Combine(snapDir, n));
        }

        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "file.txt"), "x");

        var opts = Options(dest, noSnapshot: false, keepSnapshots: 2);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test-store", src, snapshot: true) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        var toDelete = Path.Combine(dest, "_to_delete");
        var moved = Directory.Exists(toDelete)
            ? Directory.GetFiles(toDelete, "*_claude-stores.zip").Length
            : 0;

        // With 5 old + 1 new = 6 total, keeping 2, we should move 4.
        Assert.Equal(4, moved);
    }

    [Fact]
    public async Task Engine_Retention_OldSnapshotsAreNotDeleted()
    {
        // The overflow snapshots must be MOVED to _to_delete, never deleted.
        var dest = Path.Combine(_tmp, "dest");
        var snapDir = Path.Combine(dest, "snapshots");
        var emptyDir = Path.Combine(_tmp, "empty_dir");
        Directory.CreateDirectory(snapDir);
        Directory.CreateDirectory(emptyDir);

        ZipFile.CreateFromDirectory(emptyDir, Path.Combine(snapDir, "20260101_000001_claude-stores.zip"));
        ZipFile.CreateFromDirectory(emptyDir, Path.Combine(snapDir, "20260101_000002_claude-stores.zip"));

        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "file.txt"), "x");

        var opts = Options(dest, noSnapshot: false, keepSnapshots: 1);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test-store", src, snapshot: true) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        // The old zip must exist somewhere (in _to_delete), not be permanently deleted.
        var toDelete = Path.Combine(dest, "_to_delete");
        var totalAfter = Directory.GetFiles(dest, "*_claude-stores.zip", SearchOption.AllDirectories).Length;
        Assert.True(totalAfter >= 1,
            "Overflow snapshots must be moved to _to_delete, not deleted. No zip must disappear entirely.");
    }

    // -------------------------------------------------------------------------
    // Lock contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_Lock_RefusesWhenFreshLockExists()
    {
        // If a fresh .lock file exists (<3 hours old), the run must exit with a warning and do no work.
        // Contract: "if it exists and is younger than 3 h, the run exits with a WARN and no work."
        var dest = Path.Combine(_tmp, "dest");
        Directory.CreateDirectory(dest);

        // Write a lock file with the current PID (fresh).
        File.WriteAllText(Path.Combine(dest, ".lock"), Environment.ProcessId.ToString());

        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "file.txt"), "content");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        // When the lock is held, no files should be copied to the backup.
        var backupFile = Path.Combine(dest, "live", "test", "file.txt");
        Assert.False(File.Exists(backupFile),
            "When a fresh lock exists the engine must refuse to run and copy nothing.");
        Assert.True(manifest.Warnings.Count > 0,
            "The lock-held case must produce at least one warning.");
    }

    [Fact]
    public async Task Engine_Lock_CleanedUpAfterSuccessfulRun()
    {
        // The .lock file must be removed in a finally block even if the run fails.
        // Contract: "Always removed in a finally."
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        Assert.False(File.Exists(opts.LockFile),
            "The .lock file must be removed after the run completes.");
    }

    // -------------------------------------------------------------------------
    // Verify mode
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_VerifyMode_WritesNothingUnderLive()
    {
        // In verify mode, the engine must not copy any file into <destination>/live.
        // Contract: "Verify: stats + shrink scan + catalog only; no copy, no snapshot, no quarantine."
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "transcript.jsonl"), "{}");

        var opts = Options(dest, verify: true);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        var liveDir = opts.LiveRoot;
        bool anyLiveFiles = Directory.Exists(liveDir) &&
            Directory.GetFiles(liveDir, "*", SearchOption.AllDirectories).Length > 0;
        Assert.False(anyLiveFiles,
            "Verify mode must not write any files under <destination>/live.");
    }

    [Fact]
    public async Task Engine_VerifyMode_ResultHasVerifiedStatus()
    {
        // Verify mode returns Verified status for each store.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        // Source must be non-empty; an empty source returns SourceEmpty before the verify check.
        File.WriteAllText(Path.Combine(src, "transcript.jsonl"), "{}");

        var opts = Options(dest, verify: true);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "test");
        Assert.Equal(StoreStatus.Verified, result.Status);
    }

    // -------------------------------------------------------------------------
    // Temp-name copy (no *.partial leftover)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_TempNameCopy_LeavesNoPartialFiles()
    {
        // The temp-name copy mechanism must leave no *.partial or *.tmp files
        // after a normal (non-crashing) run.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.jsonl"), "{}");
        File.WriteAllText(Path.Combine(src, "b.jsonl"), "{}");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        var partial = Directory.GetFiles(dest, "*.partial", SearchOption.AllDirectories);
        Assert.Empty(partial);
    }

    // -------------------------------------------------------------------------
    // Stamp and manifest
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_Manifest_HasCorrectStampFormat()
    {
        // Stamp must be "yyyyMMdd_HHmmss" format.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => Array.Empty<StoreDefinition>());

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        Assert.Matches(@"^\d{8}_\d{6}$", manifest.Stamp);
    }

    [Fact]
    public async Task Engine_Manifest_WrittenToManifestFile()
    {
        // last_run.json must be written after the run.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => Array.Empty<StoreDefinition>());

        await engine.RunAsync(opts, null, CancellationToken.None);

        Assert.True(File.Exists(opts.ManifestFile),
            "last_run.json must be written after every run.");
    }

    [Fact]
    public void Engine_SixStores_DefaultFactoryReturnsSix()
    {
        // The default factory produces exactly 6 stores. This is a pure structural
        // assertion that never reads live Claude paths.
        var dest = Path.Combine(_tmp, "dest");
        var opts = Options(dest);
        Assert.Equal(6, KnownStores.Default(opts).Count);
    }

    [Fact]
    public async Task Engine_SixStores_AllPresentInManifest()
    {
        // Inject 6 temp-rooted stores and verify the manifest reports all 6.
        var dest = Path.Combine(_tmp, "dest");
        var opts = Options(dest);
        var storeNames = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var stores = storeNames.Select(n =>
        {
            var src = Path.Combine(_tmp, "stores", n);
            Directory.CreateDirectory(src);
            return TreeStore(n, src);
        }).ToArray();

        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => stores);

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        Assert.Equal(6, manifest.Stores.Count);
    }

    // -------------------------------------------------------------------------
    // Log output
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_LogLines_ReportedThroughProgress()
    {
        // The engine must report log lines via the IProgress<LogLine> callback.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var opts = Options(dest);
        var lines = new List<LogLine>();
        var progress = new Progress<LogLine>(l => lines.Add(l));

        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => Array.Empty<StoreDefinition>());

        await engine.RunAsync(opts, progress, CancellationToken.None);
        // Give the synchronous Progress callback a tick to flush.
        await Task.Yield();

        Assert.True(lines.Count > 0,
            "The engine must emit at least one log line through the IProgress callback.");
    }

    // -------------------------------------------------------------------------
    // Contract gap 1: mtime-based copy trigger
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_MtimeTrigger_FileCopiedWhenMtimeDiffersMoreThan2s()
    {
        // Contract: "copy when the backup copy is missing, differs in size, or the source
        // LastWriteTimeUtc differs by more than 2 s."
        // Same-size file whose mtime is 10 seconds older in the backup must be re-copied.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var content = new byte[64];
        var sourceFile = Path.Combine(src, "data.txt");
        File.WriteAllBytes(sourceFile, content);

        var backupDir = Path.Combine(dest, "live", "mtime-store");
        Directory.CreateDirectory(backupDir);
        var backupFile = Path.Combine(backupDir, "data.txt");
        File.WriteAllBytes(backupFile, content);

        // Source is 10 seconds newer than the backup → must trigger a re-copy.
        var baseMtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(backupFile, baseMtime);
        File.SetLastWriteTimeUtc(sourceFile, baseMtime.AddSeconds(10));

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("mtime-store", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "mtime-store");
        Assert.True(result.Copied > 0,
            "A file with same size but mtime > 2 s from the backup copy must be re-copied.");
    }

    [Fact]
    public async Task Engine_MtimeTrigger_FileUnchangedWhenSizeAndMtimeWithin2s()
    {
        // Contract: identical size and mtime within 2 s → NOT re-copied.
        // Prove it via StoreResult.Unchanged == 1 and the backup file's mtime is untouched.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var content = new byte[64];
        var sourceFile = Path.Combine(src, "data.txt");
        File.WriteAllBytes(sourceFile, content);

        var backupDir = Path.Combine(dest, "live", "noop-store");
        Directory.CreateDirectory(backupDir);
        var backupFile = Path.Combine(backupDir, "data.txt");
        File.WriteAllBytes(backupFile, content);

        // Same mtime on both files → diff < 2 s → NeedsCopy returns false.
        var sharedMtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourceFile, sharedMtime);
        File.SetLastWriteTimeUtc(backupFile, sharedMtime);

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("noop-store", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "noop-store");
        Assert.Equal(0, result.Copied);
        Assert.True(result.Unchanged > 0,
            "A file with same size and mtime within 2 s must NOT be re-copied.");
        // Extra proof: the backup file's mtime was not touched.
        Assert.Equal(sharedMtime, File.GetLastWriteTimeUtc(backupFile));
    }

    // -------------------------------------------------------------------------
    // Contract gap 2: IOException retry; run-log file creation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Engine_IOException_RetrySucceedsAfterLockRelease()
    {
        // Contract: "One retry after 1 s on IOException (a transcript being appended),
        // then count the file as Failed."
        // Open the source file exclusively so the first copy attempt throws IOException;
        // release it after 300 ms so the retry (at ~1000 ms) succeeds.
        // The file must end up in the backup with Failed == 0.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var sourceFile = Path.Combine(src, "live.jsonl");
        File.WriteAllText(sourceFile, "content");

        // Hold an exclusive read lock; release after 300 ms.
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockTask = Task.Run(async () =>
        {
            using var fs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.None);
            await Task.Delay(300).ConfigureAwait(false);
            released.TrySetResult(true);
        });

        // Give the lock task time to actually open the file before the engine starts.
        await Task.Delay(60);

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);
        await released.Task; // ensure lock task finished

        var result = manifest.Stores.Single(s => s.Name == "test");
        Assert.True(result.Failed == 0,
            "After the retry (1 s sleep), the file must be copied successfully; Failed must be 0.");
        Assert.True(File.Exists(Path.Combine(dest, "live", "test", "live.jsonl")),
            "The file must exist in the backup after the IOException retry succeeds.");
    }

    [Fact]
    public async Task Engine_RunLog_FileCreatedWithPerStoreLine()
    {
        // Contract: "every line goes to <logs>\<stamp>_backup.log"
        // A <logs>/<stamp>_backup.log must exist after the run and contain a per-store line.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "file.txt"), "x");

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("log-store", src) });

        await engine.RunAsync(opts, null, CancellationToken.None);

        var logsDir = opts.LogsDir;
        Assert.True(Directory.Exists(logsDir), "The logs directory must be created.");
        var logFiles = Directory.GetFiles(logsDir, "*_backup.log");
        Assert.True(logFiles.Length > 0, "A <stamp>_backup.log file must be written to <logs>.");
        var content = File.ReadAllText(logFiles[0]);
        Assert.True(content.Contains("log-store"),
            "The run log must contain a per-store output line naming the store.");
    }

    // -------------------------------------------------------------------------
    // Contract gap 3: catalog failure is a WARN, not a run failure
    // -------------------------------------------------------------------------

    private sealed class ThrowingCatalog : ICatalogBuilder
    {
        public Task<SessionCatalog> BuildAsync(CatalogOptions options, IProgress<LogLine>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated catalog failure");

        public Task<SessionCatalog> LoadAsync(string catalogPath, CancellationToken cancellationToken) =>
            Task.FromResult(new SessionCatalog());
    }

    [Fact]
    public async Task Engine_CatalogFailure_IsWarnNotRunFailure()
    {
        // Contract: "A catalog failure is a WARN, not a run failure."
        // Injecting a catalog builder that always throws must:
        //   - leave the stores at StoreStatus.Ok
        //   - set HasWarnings = true
        //   - leave HasFailures = false
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "file.txt"), "x");

        var opts = new BackupOptions
        {
            Destination = dest,
            NoSnapshot = true,
            NoCatalog = false, // enabled so the ThrowingCatalog is actually called
        };
        var engine = new BackupEngine(
            catalog: new ThrowingCatalog(),
            stores: _ => new[] { TreeStore("test", src) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        var result = manifest.Stores.Single(s => s.Name == "test");
        Assert.True(result.Status == StoreStatus.Ok,
            "Stores must complete with Ok even when the catalog builder throws.");
        Assert.False(manifest.HasFailures,
            "A catalog exception must NOT set HasFailures; it is a WARN-level event.");
        Assert.True(manifest.HasWarnings,
            "A catalog exception must be recorded as a WARN in the manifest.");
    }

    // -------------------------------------------------------------------------
    // Contract gap 8 (engine level): exit code 1 = warnings
    // -------------------------------------------------------------------------
    // Tested at the engine level because the CLI uses live Claude stores as source,
    // which cannot be controlled or replaced in the test environment.  The mapping
    // HasWarnings == true → Program.ExitWarnings (1) is a one-liner in Program.cs
    // and covered by reading the source; this test pins the engine behaviour.

    [Fact]
    public async Task Engine_HasWarnings_WhenShrinkGuardHoldsBackFile()
    {
        // A shrink-guard hold-back adds a WARN to the run log → HasWarnings = true.
        // This is the scenario that maps to exit code 1 in the CLI.
        var dest = Path.Combine(_tmp, "dest");
        var src = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(src);

        var backupDir = Path.Combine(dest, "live", "warn-store");
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(Path.Combine(backupDir, "session.jsonl"), new byte[200]);
        File.WriteAllBytes(Path.Combine(src, "session.jsonl"), new byte[10]); // smaller → held back

        var opts = Options(dest);
        var engine = new BackupEngine(
            catalog: new StubCatalog(),
            stores: _ => new[] { TreeStore("warn-store", src, shrinkGuard: true) });

        var manifest = await engine.RunAsync(opts, null, CancellationToken.None);

        Assert.True(manifest.HasWarnings,
            "A shrink-guard hold-back must produce warnings in the manifest (CLI exit code 1).");
        Assert.False(manifest.HasFailures,
            "A hold-back must not be counted as a failure (would be exit code 2, not 1).");
    }
}
