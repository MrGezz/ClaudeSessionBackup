using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Rebuild;

/// <summary>
/// Regenerates missing Cowork sidebar records (local_*.json) from a session catalog.
/// </summary>
/// <remarks>
/// Behavioural contract (a port of rebuild_index_from_catalog.py, the reference implementation):
/// <list type="bullet">
/// <item>Index folder: <see cref="RebuildOptions.IndexDir"/> if given; else under IndexRoot the folder with the most local_*.json records (deleted_* and *_backup_* paths excluded); if none has records, the newest two-level &lt;uuid&gt;\&lt;uuid&gt; folder. Never the root itself (a record written there is invisible to the app) - refuse with a clear message.</item>
/// <item>Reference records = live index folder records, else the backup copy's. Per-key frequency over the reference set: keys present in fewer than 50% of records are "rare" (only computed when at least 5 reference records exist). Drop set = the static per-session list (bridgeSessionIds, completedTurns, scheduledTaskId, worktreePath, worktreeName, branch, sourceBranch, writtenBranches, spawnedFrom, cuAllowedApps, cuGrantFlags, cuLastScreenshotDims, cuSelectedDisplayId, pendingSystemReminder, resolvedBackgroundTaskSuggestions, scratchPromptRecents, chromeTabGroupId) union rare. Donor = the reference record with the most keys carrying no rare key (DonorPath overrides the choice, not the statistics). Output formatting (compact vs indented) follows the donor's bytes.</item>
/// <item>A record is synthesised by deep-copying the donor and overriding: sessionId = "local_" + new uuid4, cliSessionId, cwd and originCwd = catalog cwd, createdAt = first_ms, lastActivityAt = last_ms, lastFocusedAt = last_ms (if present), isArchived = false, title = catalog title, titleSource = "auto" (if present), lastSpawnRootDetected = false, remoteControlAutoEligible = false (must mirror the absence of bridgeSessionIds), alwaysAllowedReasons = [], sessionPermissionUpdates = [], spawnSeed = {} (each only if present); then the drop set is removed. Key ORDER of the donor is preserved. Written as UTF-8, no BOM, no trailing newline.</item>
/// <item>Skip (with reason): already live in the index (by cliSessionId, read from every live record), subagent transcript unless IncludeSubagents, entrypoint "cli" unless IncludeCli, deleted marker (live root or backup copy, or catalog flag) unless IncludeDeleted, transcript not on disk under ProjectsDir, 0-byte transcript unless IncludeEmpty. Planned in ascending last_ms order.</item>
/// <item>Apply: refuse when a claude.exe whose path is NOT under <see cref="ClaudePaths.CliBinaryRoot"/> is running (the desktop app) unless Force; when writing into the live index folder, copy it first to &lt;folder&gt;_backup_&lt;stamp&gt;; then write each planned file. Never touches existing records.</item>
/// </list>
/// </remarks>
public interface IIndexRebuilder
{
    RebuildPlan Plan(RebuildOptions options, IProgress<LogLine>? progress);
    RebuildResult Apply(RebuildPlan plan, RebuildOptions options, IProgress<LogLine>? progress);

    /// <summary>Full paths of running claude.exe processes that are NOT the Claude Code CLI (i.e. the desktop app).</summary>
    IReadOnlyList<string> DesktopAppProcesses();
}

public sealed partial class IndexRebuilder : IIndexRebuilder
{
    // ── Static per-session keys that are never cloned from the donor ─────────────
    // Measured on the live index 2026-09-05 (134 records): each appears on <= 44% of records.
    private static readonly HashSet<string> PerSessionKeys = new(StringComparer.Ordinal)
    {
        "bridgeSessionIds", "completedTurns", "scheduledTaskId",
        "worktreePath", "worktreeName", "branch", "sourceBranch", "writtenBranches", "spawnedFrom",
        "cuAllowedApps", "cuGrantFlags", "cuLastScreenshotDims", "cuSelectedDisplayId",
        "pendingSystemReminder", "resolvedBackgroundTaskSuggestions", "scratchPromptRecents", "chromeTabGroupId",
    };

    private const double RareKeyRatio = 0.5;
    private const int MinReferenceForRatio = 5;

    // Regex for two-level <uuid>\<uuid> folder structure (account\org)
    private static readonly System.Text.RegularExpressions.Regex UuidDirRe =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // ──────────────────────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────────────────────

    public RebuildPlan Plan(RebuildOptions options, IProgress<LogLine>? progress)
    {
        var plan = new RebuildPlan
        {
            CatalogPath = options.CatalogPath,
        };

        // ── Load catalog ──────────────────────────────────────────────────────────
        progress?.Report(Info($"Loading catalog: {options.CatalogPath}"));
        SessionCatalog catalog;
        try
        {
            var raw = File.ReadAllBytes(options.CatalogPath);
            catalog = JsonSerializer.Deserialize<SessionCatalog>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Catalog deserialized to null.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read catalog {options.CatalogPath}: {ex.Message}", ex);
        }
        plan.CatalogGenerated = catalog.Generated;
        plan.CatalogSessions = catalog.Sessions.Count;

        // ── Resolve index directory ───────────────────────────────────────────────
        string indexDir = options.IndexDir ?? FindIndexDir(options.IndexRoot)
            ?? throw new InvalidOperationException(
                $"No <account>\\<org> index folder found under {options.IndexRoot}. " +
                "Launch Claude Desktop once (it recreates the folder), restore it per RESTORE.md section 2A, " +
                "or pass --index-dir.");

        // Guard: never resolve to the root itself - records written there are invisible to the app
        if (string.Equals(Path.GetFullPath(indexDir), Path.GetFullPath(options.IndexRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The index folder resolved to the root {options.IndexRoot}; records written there are invisible to the app. Pass --index-dir.");

        plan.IndexDir = indexDir;

        // ── Resolve backup index directory ────────────────────────────────────────
        string backupIndexDir = options.BackupIndexDir
            ?? Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(options.CatalogPath))!)!,
                "live", "cowork-index");

        // ── Load reference records (live, else backup fallback) ───────────────────
        var liveRecords = LoadRecords(indexDir);
        List<(string Path, JsonObject Obj, bool Compact)> refRecords;
        List<string> fallbackDirs = new();
        if (Directory.Exists(backupIndexDir))
        {
            // Collect all subdirs in backup that have local_*.json (excluding _backup_ paths)
            foreach (var d in Directory.EnumerateDirectories(backupIndexDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(backupIndexDir, d);
                if (rel.Contains("_backup_")) continue;
                if (Directory.EnumerateFiles(d, "local_*.json").Any())
                    fallbackDirs.Add(d);
            }
        }

        if (liveRecords.Count > 0)
        {
            refRecords = liveRecords;
        }
        else
        {
            refRecords = new();
            foreach (var fd in fallbackDirs)
                refRecords.AddRange(LoadRecords(fd));
        }

        // ── Compute key frequency / rare set ─────────────────────────────────────
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, obj, _) in refRecords)
            foreach (var kv in obj)
                freq[kv.Key] = freq.GetValueOrDefault(kv.Key) + 1;

        var rareKeys = new HashSet<string>(StringComparer.Ordinal);
        if (refRecords.Count >= MinReferenceForRatio)
        {
            foreach (var (k, cnt) in freq)
                if ((double)cnt < RareKeyRatio * refRecords.Count)
                    rareKeys.Add(k);
        }

        var dropSet = new HashSet<string>(PerSessionKeys, StringComparer.Ordinal);
        dropSet.UnionWith(rareKeys);

        plan.ReferenceRecords = refRecords.Count;

        // ── Select donor ──────────────────────────────────────────────────────────
        (string DonorPath, JsonObject DonorObj, bool DonorCompact) donor;
        if (options.DonorPath is not null)
        {
            // Explicit donor: load from its directory but still use the computed drop set
            var donorDir = Path.GetDirectoryName(options.DonorPath)!;
            var donorRecs = LoadRecords(donorDir);
            var donorAbs = Path.GetFullPath(options.DonorPath);
            var match = donorRecs.FirstOrDefault(r => string.Equals(Path.GetFullPath(r.Path), donorAbs, StringComparison.OrdinalIgnoreCase));
            if (match.Path is null)
                throw new InvalidOperationException($"FATAL: --donor {options.DonorPath} is not a readable local_*.json");
            donor = match;
        }
        else
        {
            // Auto-select: no rare keys, most keys, then path as tiebreaker
            var clean = refRecords.Where(r => !r.Obj.Any(kv => rareKeys.Contains(kv.Key))).ToList();
            var pool = clean.Count > 0 ? clean : refRecords;
            if (pool.Count == 0)
                throw new InvalidOperationException(
                    "FATAL: no donor local_*.json found; pass --donor <file> (any live or backed-up sidebar record)");
            pool.Sort((a, b) =>
            {
                int cmp = b.Obj.Count.CompareTo(a.Obj.Count);
                return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.Ordinal);
            });
            donor = pool[0];
        }

        plan.DonorPath = donor.DonorPath;
        plan.DonorKeys = donor.DonorObj.Count;
        plan.DonorCompact = donor.DonorCompact;
        // Dropped keys = drop set members that actually appear in the donor
        plan.DroppedKeys = dropSet.Where(k => donor.DonorObj.ContainsKey(k)).OrderBy(k => k).ToList();
        plan.KeysPerRecord = donor.DonorObj.Count - plan.DroppedKeys.Count;

        // ── Collect deleted markers: live root (whole tree) + backup copy ─────────
        var deletedMarkers = new HashSet<string>(StringComparer.Ordinal);
        CollectDeletedMarkers(options.IndexRoot, deletedMarkers);
        CollectDeletedMarkers(backupIndexDir, deletedMarkers);
        if (options.IndexDir is not null)
            CollectDeletedMarkers(options.IndexDir, deletedMarkers);
        plan.DeletedMarkers = deletedMarkers.Count;

        // ── Collect existing cliSessionIds from live records ──────────────────────
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, obj, _) in liveRecords)
        {
            if (obj["cliSessionId"] is JsonValue v && v.TryGetValue<string>(out var cli) && cli is not null)
                existing.Add(cli);
        }
        plan.LiveRecords = existing.Count;
        plan.OutDir = options.OutDir ?? indexDir;

        // ── Plan records in ascending last_ms order ───────────────────────────────
        progress?.Report(Info($"Catalog: {plan.CatalogSessions} sessions, donor: {plan.DonorPath} ({plan.DonorKeys} keys, {(plan.DonorCompact ? "compact" : "indented")})"));
        progress?.Report(Info($"Reference records: {plan.ReferenceRecords}; dropping: {(plan.DroppedKeys.Count > 0 ? string.Join(", ", plan.DroppedKeys) : "nothing")}"));
        progress?.Report(Info($"Index dir: {plan.IndexDir} ({plan.LiveRecords} live records)"));
        progress?.Report(Info($"Deleted markers: {plan.DeletedMarkers}"));

        var sessions = catalog.Sessions
            .OrderBy(s => s.LastMs ?? 0)
            .ToList();

        foreach (var s in sessions)
        {
            var cli = s.CliSessionId;

            if (existing.Contains(cli))
            {
                plan.Skipped.Add(new SkippedSession(s, "already live in index"));
                continue;
            }
            if (s.IsSubagent && !options.IncludeSubagents)
            {
                plan.Skipped.Add(new SkippedSession(s, "subagent transcript"));
                continue;
            }
            if (string.Equals(s.Entrypoint, "cli", StringComparison.OrdinalIgnoreCase) && !options.IncludeCli)
            {
                plan.Skipped.Add(new SkippedSession(s, "terminal CLI session (pass --include-cli to add it to the sidebar)"));
                continue;
            }
            if ((deletedMarkers.Contains(cli) || s.IndexDeletedMarker) && !options.IncludeDeleted)
            {
                plan.Skipped.Add(new SkippedSession(s, "deleted in the app (pass --include-deleted to resurrect)"));
                continue;
            }
            // Transcript must exist on disk
            string? transcriptPath = null;
            if (s.TranscriptRel is not null)
            {
                // TranscriptRel uses forward slashes; split and combine with OS separator
                var parts = s.TranscriptRel.Split('/');
                transcriptPath = Path.Combine(new[] { options.ProjectsDir }.Concat(parts).ToArray());
            }
            if (transcriptPath is null || !File.Exists(transcriptPath))
            {
                plan.Skipped.Add(new SkippedSession(s, $"transcript not on disk: {transcriptPath ?? "(none)"}"));
                continue;
            }
            if (new FileInfo(transcriptPath).Length == 0 && !options.IncludeEmpty)
            {
                plan.Skipped.Add(new SkippedSession(s, "0-byte transcript, would open empty"));
                continue;
            }

            // ── Build the synthesised record ──────────────────────────────────────
            var record = BuildRecord(s, donor.DonorObj, dropSet);
            string fileName = record["sessionId"]!.GetValue<string>() + ".json";
            string json = plan.DonorCompact
                ? record.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                : record.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            plan.ToWrite.Add(new PlannedRecord(s, fileName, json));
            // Mark as "will be written" so duplicate catalogue entries don't collide
            existing.Add(cli);
        }

        progress?.Report(Info($"Plan: {plan.ToWrite.Count} to write, {plan.Skipped.Count} skipped"));
        return plan;
    }

    public RebuildResult Apply(RebuildPlan plan, RebuildOptions options, IProgress<LogLine>? progress)
    {
        var result = new RebuildResult();

        // ── Desktop app check ─────────────────────────────────────────────────────
        // Force bypasses the check; the app writes the index from memory and would overwrite us.
        if (!options.Force)
        {
            var desktopPaths = DesktopAppProcesses();
            if (desktopPaths.Count > 0)
            {
                result.Refused = true;
                result.RefusalReason =
                    $"REFUSING: Claude Desktop is running ({string.Join("; ", desktopPaths)}). " +
                    "Quit it fully (window + tray icon) or pass --force.";
                progress?.Report(Warn(result.RefusalReason));
                return result;
            }
        }
        else
        {
            // Even with Force, still report if running so the caller can warn
            var desktopPaths = DesktopAppProcesses();
            if (desktopPaths.Count > 0)
                progress?.Report(Warn($"WARNING (overridden): Claude Desktop is running ({string.Join("; ", desktopPaths)})"));
        }

        // ── Backup the live index before writing (only when writing to the live folder) ──
        string outDir = options.OutDir ?? plan.IndexDir;
        if (string.Equals(Path.GetFullPath(outDir), Path.GetFullPath(plan.IndexDir), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(plan.IndexDir))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var bakDir = plan.IndexDir + "_backup_" + stamp;
            progress?.Report(Info($"Backing up index to {bakDir}"));
            CopyDirectory(plan.IndexDir, bakDir);
            result.IndexBackupDir = bakDir;
        }

        Directory.CreateDirectory(outDir);

        // ── Write each planned file ───────────────────────────────────────────────
        foreach (var pr in plan.ToWrite)
        {
            var dest = Path.Combine(outDir, pr.FileName);
            // Write UTF-8, no BOM, no trailing newline - exactly as the Python reference does
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(pr.Json);
            File.WriteAllBytes(dest, bytes);
            result.WrittenFiles.Add(dest);
            progress?.Report(Info($"WROTE {pr.FileName}  {pr.Session.Title?.Substring(0, Math.Min(70, pr.Session.Title.Length))}"));
        }

        progress?.Report(Info($"Done: {result.WrittenFiles.Count} written."));
        return result;
    }

    /// <summary>
    /// Returns full paths of running claude.exe processes that are NOT the Claude Code CLI.
    /// Win32Exception / access-denied on any individual process: treat path as unknown and
    /// include a placeholder so the check errs on refusing (i.e., safer to assume desktop).
    /// </summary>
    public IReadOnlyList<string> DesktopAppProcesses()
    {
        var result = new List<string>();
        var cliRoot = Path.GetFullPath(ClaudePaths.CliBinaryRoot);

        foreach (var proc in Process.GetProcessesByName("claude"))
        {
            try
            {
                string? path = null;
                try
                {
                    path = proc.MainModule?.FileName;
                }
                catch (Win32Exception)
                {
                    // Access denied reading MainModule - treat as unknown, include placeholder
                    result.Add("claude.exe (path unavailable)");
                    continue;
                }

                if (path is null)
                {
                    result.Add("claude.exe (path unavailable)");
                    continue;
                }

                // If the path is NOT under the CLI binary root, it's the desktop app
                if (!path.StartsWith(cliRoot, StringComparison.OrdinalIgnoreCase))
                    result.Add(path);
            }
            catch (Exception)
            {
                // Process may have exited; skip it
            }
            finally
            {
                proc.Dispose();
            }
        }

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Internals
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Find the &lt;account&gt;\&lt;org&gt; directory within the index root that holds local_*.json records.
    /// Prefers the folder with the most records. On a wiped index (no records anywhere), falls
    /// back to the newest two-level &lt;uuid&gt;\&lt;uuid&gt; folder. Never returns the root itself.
    /// </summary>
    private static string? FindIndexDir(string root)
    {
        if (!Directory.Exists(root)) return null;

        string? best = null;
        int bestN = 0;
        var uuidDirs = new List<(DateTime Mtime, string Path)>();

        // Walk every subdirectory under root
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, d);

            // Skip deleted_* and *_backup_* paths
            var baseName = Path.GetFileName(d);
            if (baseName.StartsWith("deleted_", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("_backup_")) continue;

            int n = Directory.EnumerateFiles(d, "local_*.json").Count();
            if (n > bestN)
            {
                best = d;
                bestN = n;
            }

            // Track two-level <uuid>\<uuid> folders as fallback
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Length == 2 && parts.All(p => UuidDirRe.IsMatch(p)))
            {
                var mtime = Directory.GetLastWriteTime(d);
                uuidDirs.Add((mtime, d));
            }
        }

        if (bestN > 0) return best;

        // Wiped index: return the newest two-level uuid\uuid folder
        if (uuidDirs.Count > 0)
        {
            uuidDirs.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));
            return uuidDirs[0].Path;
        }

        return null;
    }

    /// <summary>
    /// Collect all deleted_&lt;cliSessionId&gt; marker names (files or directories) from the given root.
    /// </summary>
    private static void CollectDeletedMarkers(string root, HashSet<string> into)
    {
        if (!Directory.Exists(root)) return;
        // Enumerate both files and directories named deleted_*
        foreach (var p in Directory.EnumerateFileSystemEntries(root, "deleted_*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(p);
            if (name.StartsWith("deleted_", StringComparison.OrdinalIgnoreCase))
                into.Add(name.Substring("deleted_".Length));
        }
    }

    /// <summary>
    /// Load all local_*.json records from a directory as JsonObjects (preserving key order).
    /// Records that fail to parse are silently skipped (matching the Python reference).
    /// Returns compact=true when the raw file is not indented (i.e., does not start with "{\n").
    /// </summary>
    private static List<(string Path, JsonObject Obj, bool Compact)> LoadRecords(string dir)
    {
        var result = new List<(string, JsonObject, bool)>();
        if (!Directory.Exists(dir)) return result;

        foreach (var f in Directory.EnumerateFiles(dir, "local_*.json").OrderBy(x => x))
        {
            try
            {
                var raw = File.ReadAllBytes(f);
                // Strip UTF-8 BOM if present (utf-8-sig in Python)
                var text = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
                    ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
                    : Encoding.UTF8.GetString(raw);

                var node = JsonNode.Parse(text, nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = false });
                if (node is not JsonObject obj) continue;

                // compact = the raw bytes don't start with "{\n" or "{\r\n"
                bool compact = !(text.TrimStart().StartsWith("{\n") || text.TrimStart().StartsWith("{\r\n"));
                result.Add((f, obj, compact));
            }
            catch
            {
                // Silently skip unreadable records, matching Python behaviour
            }
        }
        return result;
    }

    /// <summary>
    /// Synthesise a sidebar record by deep-copying the donor JsonObject and overriding per-session fields.
    /// Key ORDER of the donor is preserved. Drop set members are removed.
    /// </summary>
    private static JsonObject BuildRecord(SessionEntry sess, JsonObject donor, HashSet<string> dropSet)
    {
        // Build a new JsonObject in donor key order, applying overrides
        var newId = "local_" + Guid.NewGuid().ToString("D");
        var cwd = sess.Cwd ?? "";
        var createdAt = (sess.FirstMs > 0 ? sess.FirstMs : null) ?? (sess.LastMs > 0 ? sess.LastMs : null) ?? 0L;
        var lastActivity = (sess.LastMs > 0 ? sess.LastMs : null) ?? (sess.FirstMs > 0 ? sess.FirstMs : null) ?? 0L;
        var title = !string.IsNullOrEmpty(sess.Title) ? sess.Title : "(untitled)";

        var result = new JsonObject();

        foreach (var kv in donor)
        {
            // Skip keys in the drop set
            if (dropSet.Contains(kv.Key)) continue;

            // Override per-session fields
            switch (kv.Key)
            {
                case "sessionId":
                    result.Add(kv.Key, JsonValue.Create(newId));
                    break;
                case "cliSessionId":
                    result.Add(kv.Key, JsonValue.Create(sess.CliSessionId));
                    break;
                case "cwd":
                    result.Add(kv.Key, JsonValue.Create(cwd));
                    break;
                case "originCwd":
                    result.Add(kv.Key, JsonValue.Create(cwd));
                    break;
                case "createdAt":
                    result.Add(kv.Key, JsonValue.Create(createdAt));
                    break;
                case "lastActivityAt":
                    result.Add(kv.Key, JsonValue.Create(lastActivity));
                    break;
                case "lastFocusedAt":
                    result.Add(kv.Key, JsonValue.Create(lastActivity));
                    break;
                case "isArchived":
                    result.Add(kv.Key, JsonValue.Create(false));
                    break;
                case "title":
                    result.Add(kv.Key, JsonValue.Create(title));
                    break;
                case "titleSource":
                    result.Add(kv.Key, JsonValue.Create("auto"));
                    break;
                case "lastSpawnRootDetected":
                    result.Add(kv.Key, JsonValue.Create(false));
                    break;
                case "remoteControlAutoEligible":
                    // Must mirror the absence of bridgeSessionIds (which is in the drop set)
                    result.Add(kv.Key, JsonValue.Create(false));
                    break;
                case "alwaysAllowedReasons":
                    result.Add(kv.Key, new JsonArray());
                    break;
                case "sessionPermissionUpdates":
                    result.Add(kv.Key, new JsonArray());
                    break;
                case "spawnSeed":
                    result.Add(kv.Key, new JsonObject());
                    break;
                default:
                    // Deep-copy the donor value
                    result.Add(kv.Key, DeepCopy(kv.Value));
                    break;
            }
        }

        return result;
    }

    /// <summary>Deep-copies a JsonNode (handles null, primitives, arrays, objects).</summary>
    private static JsonNode? DeepCopy(JsonNode? node)
    {
        if (node is null) return null;
        // JsonNode.ToJsonString() + JsonNode.Parse() is the safest way to deep-copy
        return JsonNode.Parse(node.ToJsonString());
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }

    private static LogLine Info(string msg) => new(DateTime.Now, LogLevel.Info, msg);
    private static LogLine Warn(string msg) => new(DateTime.Now, LogLevel.Warn, msg);
}
