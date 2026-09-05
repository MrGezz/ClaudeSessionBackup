using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Catalog;

/// <summary>
/// Builds the session catalog: every transcript (live and backup) joined with every sidebar record.
/// </summary>
/// <remarks>
/// Behavioural contract (a port of build_catalog.py, the reference implementation):
/// <list type="bullet">
/// <item>Transcripts: &lt;projects&gt;\&lt;slug&gt;\&lt;uuid&gt;.jsonl (uuid = 8-4-4-4-12 hex). With IncludeSubagents also &lt;projects&gt;\&lt;slug&gt;\**\subagents\*.jsonl. Key = "slug/uuid.jsonl" (forward slashes). Same key in live and backup = same session; LOST = backup only, NOT-IN-BACKUP = live only.</item>
/// <item>Scan = parse every line that starts with "{" as JSON (System.Text.Json, JsonDocument). Record type from the top-level "type". Timestamps from top-level "timestamp" (first/last seen). cwd, entrypoint, version, gitBranch from the first record carrying them. "user" records: a human prompt if isMeta is not true and message.content is a non-empty string or a list containing a text block with non-empty text; tool_result-only records are not prompts. The first prompt's text (whitespace-collapsed, max 160 chars) is the first_prompt; a prompt that starts with &lt;command-name&gt; becomes "&lt;name&gt; &lt;command-args&gt;"; prompts starting with &lt;local-command or &lt;system-reminder are ignored. "assistant" records count one message when apiBlockIndex is 0 or absent. custom-title/aiTitle/summary records: last one wins. Unparseable lines are counted, never fatal. A 0-byte transcript yields an entry with zeros.</item>
/// <item>Cache: catalog_cache.json in OutDir, {"_schema": N, "entries": {key: {size, mtime_ms, entry}}}; an entry is reused only when size AND mtime match AND it carries every current field; any other cache (old schema, unreadable) is discarded. Keys are prefixed "live:" or "backup:".</item>
/// <item>Sidebar records: every local_*.json under IndexDir recursively; those under a *_backup_* or deleted_* path are Flagged and lose to an unflagged record with the same sessionId. Deleted markers = basenames "deleted_&lt;cliSessionId&gt;" (files or folders) anywhere under IndexDir, unioned with those under BackupIndexDir.</item>
/// <item>Join on cliSessionId; when several records point at one transcript prefer unflagged, then newest lastActivityAt. Title precedence: custom-title, ai-title, sidebar title, summary, first prompt, "(untitled)". first_ms/last_ms: transcript timestamps, else sidebar createdAt/lastActivityAt, else file mtime. Sidebar records whose transcript exists nowhere become DANGLING rows (transcript_rel null, project_dir = slug of cwd where slug = every non-alphanumeric char replaced by '-').</item>
/// <item>Outputs in OutDir: sessions_catalog.json (indent 1, snake_case per <see cref="SessionEntry"/>), sessions_catalog.md (table newest first: #, Last activity "yyyy-MM-dd HH:mm" local, Project = last path segment of cwd, Title + " **LOST**" / " **DANGLING**" / " (deleted in app)", Prompts/Replies, Size human, Live, Backup, Sidebar id[:14], cliSessionId), sessions_catalog.previous.json = the prior json if one existed, catalog_cache.json.</item>
/// <item>Agent-mode listing: every entry under &lt;AgentModeDir&gt;\&lt;account&gt;\&lt;org&gt;\ except "rpm", with recursive size.</item>
/// <item>Counts: see <see cref="CatalogCounts"/>; unindexed_live excludes subagents and app-deleted sessions.</item>
/// </list>
/// </remarks>
public interface ICatalogBuilder
{
    Task<SessionCatalog> BuildAsync(CatalogOptions options, IProgress<LogLine>? progress, CancellationToken cancellationToken);

    /// <summary>Reads an existing sessions_catalog.json.</summary>
    Task<SessionCatalog> LoadAsync(string catalogPath, CancellationToken cancellationToken);
}

public sealed partial class CatalogBuilder : ICatalogBuilder
{
    // Schema version: bump when the cache entry fields change.
    // Matches CACHE_SCHEMA = 3 in build_catalog.py.
    private const int CacheSchema = 3;

    private static readonly Regex UuidRegex =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CommandNameRegex =
        new(@"<command-name>(.*?)</command-name>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CommandArgsRegex =
        new(@"<command-args>(.*?)</command-args>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex WhitespaceRegex =
        new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex SlugRegex =
        new(@"[^A-Za-z0-9]", RegexOptions.Compiled);

    // JSON options for reading catalog files (property-name case-insensitive for robustness).
    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // JSON options for writing the catalog (indent = 1, no Unicode escaping).
    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        Indented = true,
        // The standard writer with indent=1 uses 2 spaces; we want 1 space to match Python's json.dump(indent=1).
        // JsonWriterOptions.IndentSize is available in .NET 9+; for .NET 8 we write manually.
    };

    public async Task<SessionCatalog> BuildAsync(
        CatalogOptions options,
        IProgress<LogLine>? progress,
        CancellationToken cancellationToken)
    {
        void Log(LogLevel level, string msg) =>
            progress?.Report(new LogLine(DateTime.Now, level, msg));

        Directory.CreateDirectory(options.OutDir);

        // ── Cache ──────────────────────────────────────────────────────────────
        // cache["live:slug/uuid.jsonl"] = {size, mtime_ms, entry:{…}}
        var cache = LoadCache(options.OutDir);

        // ── Transcript discovery ───────────────────────────────────────────────
        Log(LogLevel.Info, $"Scanning live transcripts from: {options.ProjectsDir}");
        var live = ListTranscripts(options.ProjectsDir, options.IncludeSubagents);

        var backup = options.BackupProjectsDir is { } bpd
            ? ListTranscripts(bpd, options.IncludeSubagents)
            : new Dictionary<string, string>();

        Log(LogLevel.Info, $"Found {live.Count} live transcripts, {backup.Count} backup transcripts.");

        // ── Sidebar index ──────────────────────────────────────────────────────
        Log(LogLevel.Info, $"Loading sidebar index from: {options.IndexDir}");
        var (indexRecords, deletedMarkers) = LoadIndex(options.IndexDir);

        if (options.BackupIndexDir is { } bid)
        {
            var (_, backupDeleted) = LoadIndex(bid);
            deletedMarkers = deletedMarkers.Union(backupDeleted).ToHashSet();
        }

        Log(LogLevel.Info, $"Found {indexRecords.Count} sidebar records, {deletedMarkers.Count} deleted markers.");

        // Map cliSessionId -> list of sidebar records
        var byCliSessionId = new Dictionary<string, List<IndexRecord>>();
        foreach (var rec in indexRecords.Values)
        {
            if (rec.CliSessionId is { } cliId)
            {
                if (!byCliSessionId.TryGetValue(cliId, out var list))
                    byCliSessionId[cliId] = list = new List<IndexRecord>();
                list.Add(rec);
            }
        }

        // ── Scan transcripts ───────────────────────────────────────────────────
        int rescanned = 0;
        var sessions = new Dictionary<string, SessionEntry>();
        var allRels = live.Keys.Union(backup.Keys).OrderBy(k => k).ToList();

        foreach (var rel in allRels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool inLive = live.ContainsKey(rel);
            bool inBackup = backup.ContainsKey(rel);
            string path = inLive ? live[rel] : backup[rel];

            // Key is prefixed so live and backup entries never collide even for same file
            string cacheKey = (inLive ? "live:" : "backup:") + rel;

            var (entry, fresh) = ScanCached(cacheKey, path, cache, options.NoCache);
            if (fresh) rescanned++;

            // Pick the best sidebar record: prefer unflagged, then newest lastActivityAt
            List<IndexRecord>? recs = byCliSessionId.GetValueOrDefault(entry.SessionId);
            IndexRecord? rec = recs is { Count: > 0 }
                ? recs.OrderBy(r => r.Flagged ? 1 : 0)
                       .ThenByDescending(r => r.LastActivityAt ?? 0)
                       .First()
                : null;

            // Title precedence: custom-title > ai-title > sidebar title > summary > first prompt > "(untitled)"
            string title = "(untitled)";
            string titleSource = "none";
            foreach (var (cand, src) in new[]
            {
                (entry.CustomTitle, "custom-title"),
                (entry.AiTitle, "ai-title"),
                (rec?.Title, "sidebar"),
                (entry.Summary, "summary"),
                (entry.FirstPrompt, "first-prompt"),
            })
            {
                if (!string.IsNullOrEmpty(cand))
                {
                    title = cand!;
                    titleSource = src;
                    break;
                }
            }

            long? firstMs = IsoToMs(entry.FirstTs)
                            ?? (rec?.CreatedAt > 0 ? rec?.CreatedAt : null)
                            ?? (long?)entry.MtimeMs;
            long? lastMs = IsoToMs(entry.LastTs)
                           ?? (rec?.LastActivityAt > 0 ? rec?.LastActivityAt : null)
                           ?? (long?)entry.MtimeMs;

            var sessionEntry = new SessionEntry
            {
                CliSessionId = entry.SessionId,
                Title = title,
                TitleSource = titleSource,
                CustomTitle = entry.CustomTitle,
                AiTitle = entry.AiTitle,
                Cwd = entry.Cwd ?? rec?.Cwd,
                ProjectDir = rel.Split('/')[0],
                TranscriptRel = rel,
                TranscriptLive = inLive,
                TranscriptBackup = inBackup,
                IsSubagent = rel.Contains("/subagents/"),
                Size = entry.Size,
                FirstTs = entry.FirstTs,
                LastTs = entry.LastTs,
                FirstMs = firstMs,
                LastMs = lastMs,
                Entrypoint = entry.Entrypoint,
                Version = entry.Version,
                GitBranch = entry.GitBranch,
                UserPrompts = entry.UserPrompts,
                AssistantMsgs = entry.AssistantMsgs,
                Records = entry.Records,
                FirstPrompt = entry.FirstPrompt,
                IndexSessionId = rec?.IndexSessionId,
                IndexFile = rec?.IndexFile,
                IndexFlagged = rec?.Flagged,
                IndexArchived = rec?.IsArchived,
                IndexDeletedMarker = deletedMarkers.Contains(entry.SessionId),
            };

            sessions[entry.SessionId] = sessionEntry;
        }

        // ── Dangling sidebar records (transcript exists nowhere) ───────────────
        var danglingList = new List<IndexRecord>();
        foreach (var rec in indexRecords.Values)
        {
            if (rec.CliSessionId is not { } cliId) continue;
            if (sessions.ContainsKey(cliId)) continue;
            if (rec.Flagged) continue;

            danglingList.Add(rec);
            sessions[cliId] = new SessionEntry
            {
                CliSessionId = cliId,
                Title = rec.Title ?? "(untitled)",
                TitleSource = "sidebar",
                CustomTitle = null,
                AiTitle = null,
                Cwd = rec.Cwd,
                ProjectDir = SlugRegex.Replace(rec.Cwd ?? "", "-"),
                TranscriptRel = null,
                TranscriptLive = false,
                TranscriptBackup = false,
                IsSubagent = false,
                Size = 0,
                FirstTs = null,
                LastTs = null,
                FirstMs = rec.CreatedAt,
                LastMs = rec.LastActivityAt,
                Entrypoint = null,
                Version = null,
                GitBranch = null,
                UserPrompts = 0,
                AssistantMsgs = 0,
                Records = 0,
                FirstPrompt = null,
                IndexSessionId = rec.IndexSessionId,
                IndexFile = rec.IndexFile,
                IndexFlagged = false,
                IndexArchived = rec.IsArchived,
                IndexDeletedMarker = deletedMarkers.Contains(cliId),
            };
        }

        // ── Sort: newest first by LastMs ───────────────────────────────────────
        var rows = sessions.Values
            .OrderByDescending(s => s.LastMs ?? 0)
            .ToList();

        // ── Agent-mode listing ─────────────────────────────────────────────────
        var agentMode = options.AgentModeDir is { } amd ? ListAgentMode(amd) : new List<AgentModeEntry>();

        // ── Counts ────────────────────────────────────────────────────────────
        var counts = new CatalogCounts
        {
            Sessions = rows.Count,
            TranscriptsLive = rows.Count(s => s.TranscriptLive),
            TranscriptsBackup = rows.Count(s => s.TranscriptBackup),
            IndexRecords = indexRecords.Values.Count(r => !r.Flagged),
            IndexRecordsFlagged = indexRecords.Values.Count(r => r.Flagged),
            LostFromLive = rows.Count(s => s.TranscriptBackup && !s.TranscriptLive),
            DanglingIndex = danglingList.Count,
            // unindexed_live excludes subagents and app-deleted sessions
            UnindexedLive = rows.Count(s => s.TranscriptLive && s.IndexSessionId is null && !s.IsSubagent && !s.IndexDeletedMarker),
            DeletedInApp = rows.Count(s => s.IndexDeletedMarker),
            LiveNotInBackup = options.BackupProjectsDir is not null
                ? rows.Count(s => s.TranscriptLive && !s.TranscriptBackup)
                : 0,
            Rescanned = rescanned,
            AgentModeEntries = agentMode.Count,
        };

        var sources = new CatalogSources
        {
            Projects = options.ProjectsDir,
            Index = options.IndexDir,
            AgentMode = options.AgentModeDir,
            BackupProjects = options.BackupProjectsDir,
            BackupIndex = options.BackupIndexDir,
            IncludeSubagents = options.IncludeSubagents,
        };

        string generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var catalog = new SessionCatalog
        {
            Generated = generated,
            Sources = sources,
            Counts = counts,
            DeletedMarkers = deletedMarkers.OrderBy(m => m).ToList(),
            Sessions = rows,
            AgentMode = agentMode,
        };

        // ── Save cache ─────────────────────────────────────────────────────────
        SaveCache(options.OutDir, cache);

        // ── Write sessions_catalog.previous.json ───────────────────────────────
        string outJson = Path.Combine(options.OutDir, "sessions_catalog.json");
        if (File.Exists(outJson))
            File.Copy(outJson, Path.Combine(options.OutDir, "sessions_catalog.previous.json"), overwrite: true);

        // ── Write sessions_catalog.json ────────────────────────────────────────
        await WriteJsonAsync(outJson, catalog, cancellationToken);

        // ── Write sessions_catalog.md ─────────────────────────────────────────
        string outMd = Path.Combine(options.OutDir, "sessions_catalog.md");
        WriteMarkdown(outMd, catalog, options.BackupProjectsDir is not null);

        Log(LogLevel.Info,
            $"catalog: {counts.Sessions} sessions ({counts.TranscriptsLive} live, {counts.TranscriptsBackup} backup), " +
            $"{counts.IndexRecords} sidebar records, rescanned {rescanned} -> {outJson}");

        return catalog;
    }

    public async Task<SessionCatalog> LoadAsync(string catalogPath, CancellationToken cancellationToken)
    {
        // Read the JSON using the standard deserialiser; the [JsonPropertyName] attributes on
        // the Model types mean the snake_case property names round-trip without any extra options.
        string json = await File.ReadAllTextAsync(catalogPath, cancellationToken);
        var catalog = JsonSerializer.Deserialize<SessionCatalog>(json, JsonRead);
        return catalog ?? new SessionCatalog();
    }

    // ── Transcript discovery ─────────────────────────────────────────────────

    /// <summary>
    /// Returns "slug/uuid.jsonl" -> absolute-path for every transcript under root.
    /// With includeSubagents also matches slug/**\/subagents/*.jsonl.
    /// </summary>
    private static Dictionary<string, string> ListTranscripts(string? root, bool includeSubagents)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return result;

        foreach (string slugDir in Directory.EnumerateDirectories(root))
        {
            string slugName = Path.GetFileName(slugDir);

            // Top-level *.jsonl files in the slug directory
            foreach (string file in Directory.EnumerateFiles(slugDir, "*.jsonl"))
            {
                string fileName = Path.GetFileName(file);
                string sessionId = Path.GetFileNameWithoutExtension(fileName);
                if (!UuidRegex.IsMatch(sessionId)) continue;
                result[slugName + "/" + fileName] = file;
            }

            // With subagents: slug/**\/subagents/*.jsonl
            if (includeSubagents)
            {
                foreach (string subFile in Directory.EnumerateFiles(slugDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(root, subFile).Replace('\\', '/');
                    if (!relPath.Contains("/subagents/")) continue;
                    if (result.ContainsKey(relPath)) continue; // already added at top level
                    string sessionId = Path.GetFileNameWithoutExtension(subFile);
                    if (!UuidRegex.IsMatch(sessionId)) continue;
                    result[relPath] = subFile;
                }
            }
        }

        return result;
    }

    // ── Sidebar index ─────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all local_*.json under indexRoot and collects deleted_* markers.
    /// Records in *_backup_* or deleted_* paths are flagged and lose to an unflagged record with the same sessionId.
    /// </summary>
    private static (Dictionary<string, IndexRecord> Records, HashSet<string> DeletedMarkers) LoadIndex(string? indexRoot)
    {
        var records = new Dictionary<string, IndexRecord>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(indexRoot) || !Directory.Exists(indexRoot))
            return (records, deleted);

        // Collect deleted_<cliSessionId> markers (files or folders)
        foreach (string entry in Directory.EnumerateFileSystemEntries(indexRoot, "deleted_*", SearchOption.AllDirectories))
        {
            string baseName = Path.GetFileName(entry);
            // baseName is "deleted_<cliSessionId>"
            string cliId = baseName["deleted_".Length..];
            deleted.Add(cliId);
        }

        // Load local_*.json records
        foreach (string file in Directory.EnumerateFiles(indexRoot, "local_*.json", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(indexRoot, file).Replace('\\', '/');
            bool flagged = rel.Contains("_backup_") || ("/" + rel).Contains("/deleted_");

            JsonDocument? doc;
            try
            {
                using var stream = File.OpenRead(file);
                doc = JsonDocument.Parse(stream);
            }
            catch
            {
                continue; // unreadable; skip
            }

            using (doc)
            {
                var root = doc.RootElement;
                string sessionId = GetString(root, "sessionId")
                    ?? Path.GetFileNameWithoutExtension(file);

                var rec = new IndexRecord
                {
                    IndexSessionId = sessionId,
                    CliSessionId = GetString(root, "cliSessionId"),
                    Cwd = GetString(root, "cwd"),
                    Title = GetString(root, "title"),
                    CreatedAt = GetLong(root, "createdAt"),
                    LastActivityAt = GetLong(root, "lastActivityAt"),
                    IsArchived = GetBool(root, "isArchived"),
                    Model = GetString(root, "model"),
                    ScheduledTaskId = GetString(root, "scheduledTaskId"),
                    IndexFile = rel,
                    Flagged = flagged,
                };

                // Prefer unflagged over flagged
                if (!records.TryGetValue(sessionId, out var prev) ||
                    (prev.Flagged && !flagged))
                {
                    records[sessionId] = rec;
                }
            }
        }

        return (records, deleted);
    }

    // ── Agent-mode listing ────────────────────────────────────────────────────

    private static List<AgentModeEntry> ListAgentMode(string root)
    {
        var result = new List<AgentModeEntry>();
        if (!Directory.Exists(root)) return result;

        foreach (string acctDir in Directory.EnumerateDirectories(root))
        {
            string acct = Path.GetFileName(acctDir);
            foreach (string orgDir in Directory.EnumerateDirectories(acctDir))
            {
                string org = Path.GetFileName(orgDir);
                // Each entry inside org (files or dirs), except "rpm"
                foreach (string entry in Directory.EnumerateFileSystemEntries(orgDir))
                {
                    string name = Path.GetFileName(entry);
                    if (name == "rpm") continue;

                    bool isDir = Directory.Exists(entry);
                    long size = isDir ? DirectorySize(entry) : new FileInfo(entry).Length;
                    long mtimeMs = (long)(File.GetLastWriteTimeUtc(entry)
                        .Subtract(DateTimeOffset.UnixEpoch.UtcDateTime)
                        .TotalMilliseconds);

                    result.Add(new AgentModeEntry
                    {
                        Account = acct,
                        Org = org,
                        Name = name,
                        IsDir = isDir,
                        Size = size,
                        MtimeMs = mtimeMs,
                    });
                }
            }
        }

        return result;
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { /* skip inaccessible */ }
        }
        return total;
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    /// <summary>Cache entry matching Python's scan_transcript output.</summary>
    private sealed class CacheEntry
    {
        public long Size { get; set; }
        public long MtimeMs { get; set; }
        public TranscriptScanResult? Entry { get; set; }
    }

    private sealed class TranscriptScanResult
    {
        public string SessionId { get; set; } = "";
        public long Size { get; set; }
        public long MtimeMs { get; set; }
        public string? Cwd { get; set; }
        public string? Entrypoint { get; set; }
        public string? Version { get; set; }
        public string? GitBranch { get; set; }
        public string? FirstTs { get; set; }
        public string? LastTs { get; set; }
        public string? FirstPrompt { get; set; }
        public string? CustomTitle { get; set; }
        public string? AiTitle { get; set; }
        public string? Summary { get; set; }
        public int UserPrompts { get; set; }
        public int AssistantMsgs { get; set; }
        public int Records { get; set; }
        public int ParseErrors { get; set; }
    }

    // All required keys in a TranscriptScanResult - mirrors ENTRY_KEYS in build_catalog.py.
    private static readonly IReadOnlySet<string> EntryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "session_id", "size", "mtime_ms", "cwd", "entrypoint", "version", "git_branch",
        "first_ts", "last_ts", "first_prompt", "custom_title", "ai_title", "summary",
        "user_prompts", "assistant_msgs", "records", "parse_errors",
    };

    private static Dictionary<string, CacheEntry> LoadCache(string outDir)
    {
        string cachePath = Path.Combine(outDir, "catalog_cache.json");
        if (!File.Exists(cachePath))
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        try
        {
            using var stream = File.OpenRead(cachePath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("_schema", out var schemaEl) ||
                schemaEl.GetInt32() != CacheSchema)
                return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

            if (!root.TryGetProperty("entries", out var entriesEl))
                return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

            var result = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            foreach (var kv in entriesEl.EnumerateObject())
            {
                // Verify the cached entry has all required fields
                if (!kv.Value.TryGetProperty("entry", out var entryEl))
                    continue;

                // Check all required keys are present
                bool hasAllKeys = EntryKeys.All(k =>
                    entryEl.TryGetProperty(k, out _) ||
                    entryEl.TryGetProperty(ToCamelCase(k), out _));

                if (!hasAllKeys) continue;

                long size = kv.Value.TryGetProperty("size", out var sEl) ? sEl.GetInt64() : 0;
                long mtimeMs = kv.Value.TryGetProperty("mtime_ms", out var mEl) ? mEl.GetInt64() : 0;

                var entry = DeserializeTranscriptScanResult(entryEl);
                if (entry is null) continue;

                result[kv.Name] = new CacheEntry { Size = size, MtimeMs = mtimeMs, Entry = entry };
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
    }

    private static string ToCamelCase(string snake)
    {
        // "git_branch" -> "gitBranch"  (only used for key checking)
        var parts = snake.Split('_');
        if (parts.Length == 1) return snake;
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static TranscriptScanResult? DeserializeTranscriptScanResult(JsonElement el)
    {
        try
        {
            return new TranscriptScanResult
            {
                // Python keys are snake_case; try both styles for forward compat
                SessionId = GetStringEl(el, "session_id") ?? "",
                Size = GetLongEl(el, "size") ?? 0,
                MtimeMs = GetLongEl(el, "mtime_ms") ?? 0,
                Cwd = GetStringEl(el, "cwd"),
                Entrypoint = GetStringEl(el, "entrypoint"),
                Version = GetStringEl(el, "version"),
                GitBranch = GetStringEl(el, "git_branch"),
                FirstTs = GetStringEl(el, "first_ts"),
                LastTs = GetStringEl(el, "last_ts"),
                FirstPrompt = GetStringEl(el, "first_prompt"),
                CustomTitle = GetStringEl(el, "custom_title"),
                AiTitle = GetStringEl(el, "ai_title"),
                Summary = GetStringEl(el, "summary"),
                UserPrompts = (int)(GetLongEl(el, "user_prompts") ?? 0),
                AssistantMsgs = (int)(GetLongEl(el, "assistant_msgs") ?? 0),
                Records = (int)(GetLongEl(el, "records") ?? 0),
                ParseErrors = (int)(GetLongEl(el, "parse_errors") ?? 0),
            };
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string outDir, Dictionary<string, CacheEntry> cache)
    {
        // Write the cache as JSON. We need a specific format matching what build_catalog.py reads.
        // The Python code writes: {"_schema": 3, "entries": {key: {"size": N, "mtime_ms": N, "entry": {...}}}}
        string cachePath = Path.Combine(outDir, "catalog_cache.json");
        string tmpPath = cachePath + ".tmp";

        // Use a nested scope so the writer is fully flushed and the stream is closed before the Move.
        {
            using var stream = File.Open(tmpPath, FileMode.Create, FileAccess.Write);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();
            writer.WriteNumber("_schema", CacheSchema);
            writer.WriteStartObject("entries");
            foreach (var (key, ce) in cache)
            {
                if (ce.Entry is null) continue;
                writer.WriteStartObject(key);
                writer.WriteNumber("size", ce.Size);
                writer.WriteNumber("mtime_ms", ce.MtimeMs);
                writer.WriteStartObject("entry");
                // Write entry fields in the same order as Python's new_entry()
                writer.WriteString("session_id", ce.Entry.SessionId);
                writer.WriteNumber("size", ce.Entry.Size);
                writer.WriteNumber("mtime_ms", ce.Entry.MtimeMs);
                WriteNullableString(writer, "cwd", ce.Entry.Cwd);
                WriteNullableString(writer, "entrypoint", ce.Entry.Entrypoint);
                WriteNullableString(writer, "version", ce.Entry.Version);
                WriteNullableString(writer, "git_branch", ce.Entry.GitBranch);
                WriteNullableString(writer, "first_ts", ce.Entry.FirstTs);
                WriteNullableString(writer, "last_ts", ce.Entry.LastTs);
                WriteNullableString(writer, "first_prompt", ce.Entry.FirstPrompt);
                WriteNullableString(writer, "custom_title", ce.Entry.CustomTitle);
                WriteNullableString(writer, "ai_title", ce.Entry.AiTitle);
                WriteNullableString(writer, "summary", ce.Entry.Summary);
                writer.WriteNumber("user_prompts", ce.Entry.UserPrompts);
                writer.WriteNumber("assistant_msgs", ce.Entry.AssistantMsgs);
                writer.WriteNumber("records", ce.Entry.Records);
                writer.WriteNumber("parse_errors", ce.Entry.ParseErrors);
                writer.WriteEndObject(); // entry
                writer.WriteEndObject(); // key
            }
            writer.WriteEndObject(); // entries
            writer.WriteEndObject(); // root
            // writer and stream both disposed here by using statements
        }

        File.Move(tmpPath, cachePath, overwrite: true);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    // ── Transcript scanning ───────────────────────────────────────────────────

    private static (TranscriptScanResult Entry, bool Fresh) ScanCached(
        string cacheKey,
        string path,
        Dictionary<string, CacheEntry> cache,
        bool noCache)
    {
        var fi = new FileInfo(path);
        long size = fi.Exists ? fi.Length : 0;
        long mtimeMs = fi.Exists
            ? (long)(fi.LastWriteTimeUtc.Subtract(DateTimeOffset.UnixEpoch.UtcDateTime).TotalMilliseconds)
            : 0;

        if (!noCache && cache.TryGetValue(cacheKey, out var cached) &&
            cached.Size == size && cached.MtimeMs == mtimeMs && cached.Entry is not null)
        {
            return (cached.Entry, Fresh: false);
        }

        var entry = ScanTranscript(path);
        cache[cacheKey] = new CacheEntry { Size = size, MtimeMs = mtimeMs, Entry = entry };
        return (entry, Fresh: true);
    }

    private static TranscriptScanResult ScanTranscript(string path)
    {
        var fi = new FileInfo(path);
        string sessionId = Path.GetFileNameWithoutExtension(path);
        long size = fi.Exists ? fi.Length : 0;
        long mtimeMs = fi.Exists
            ? (long)(fi.LastWriteTimeUtc.Subtract(DateTimeOffset.UnixEpoch.UtcDateTime).TotalMilliseconds)
            : 0;

        var entry = new TranscriptScanResult
        {
            SessionId = sessionId,
            Size = size,
            MtimeMs = mtimeMs,
        };

        if (size == 0 || !fi.Exists) return entry;

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith('{')) continue;

            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch
            {
                entry.ParseErrors++;
                continue;
            }

            using (doc)
            {
                entry.Records++;
                var obj = doc.RootElement;

                // Timestamp (first/last seen)
                string? ts = GetStringEl(obj, "timestamp");
                if (ts is { Length: >= 2 } && ts[..2] == "20")
                {
                    entry.FirstTs ??= ts;
                    entry.LastTs = ts;
                }

                // Context fields from the first record that has them
                if (entry.Cwd is null && GetStringEl(obj, "cwd") is { } cwd && cwd.Length > 0)
                    entry.Cwd = cwd;
                if (entry.Entrypoint is null && GetStringEl(obj, "entrypoint") is { } ep && ep.Length > 0)
                    entry.Entrypoint = ep;
                if (entry.Version is null && GetStringEl(obj, "version") is { } ver && ver.Length > 0)
                    entry.Version = ver;
                if (entry.GitBranch is null && GetStringEl(obj, "gitBranch") is { } gb && gb.Length > 0)
                    entry.GitBranch = gb;

                string? type = GetStringEl(obj, "type");
                switch (type)
                {
                    case "user":
                        if (IsHumanPrompt(obj))
                        {
                            entry.UserPrompts++;
                            if (entry.FirstPrompt is null)
                                entry.FirstPrompt = FirstPromptText(obj);
                        }
                        break;

                    case "assistant":
                        // apiBlockIndex 0 (or absent) marks a new assistant message
                        // In Python: `not o.get("apiBlockIndex")` is True for 0, None, and missing
                        if (!obj.TryGetProperty("apiBlockIndex", out var blockIdx) ||
                            blockIdx.ValueKind == JsonValueKind.Null ||
                            (blockIdx.ValueKind == JsonValueKind.Number && blockIdx.GetInt32() == 0))
                        {
                            entry.AssistantMsgs++;
                        }
                        break;

                    case "custom-title":
                        if (GetStringEl(obj, "customTitle") is { Length: > 0 } ct)
                            entry.CustomTitle = ct;
                        break;

                    case "ai-title":
                        if (GetStringEl(obj, "aiTitle") is { Length: > 0 } at)
                            entry.AiTitle = at;
                        break;

                    case "summary":
                        if (GetStringEl(obj, "summary") is { Length: > 0 } sm)
                            entry.Summary = sm;
                        break;
                }
            }
        }

        return entry;
    }

    /// <summary>
    /// A 'user' record is a human turn unless it only carries tool results or is a meta record.
    /// Matches is_human_prompt() in build_catalog.py.
    /// </summary>
    private static bool IsHumanPrompt(JsonElement obj)
    {
        if (GetBoolEl(obj, "isMeta")) return false;

        if (!obj.TryGetProperty("message", out var msg)) return false;
        if (!msg.TryGetProperty("content", out var content)) return false;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() is { } s && s.Trim().Length > 0;

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (GetStringEl(item, "type") != "text") continue;
                if (GetStringEl(item, "text") is { } t && t.Trim().Length > 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the human text of a user record, or null if it is a tool_result / meta record.
    /// Matches first_prompt_text() in build_catalog.py.
    /// </summary>
    private static string? FirstPromptText(JsonElement obj)
    {
        if (GetBoolEl(obj, "isMeta")) return null;

        if (!obj.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;

        string? text = null;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (GetStringEl(item, "type") != "text") continue;
                string? t = GetStringEl(item, "text");
                if (t is not null)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(t);
                }
            }
            text = sb.ToString();
        }

        if (string.IsNullOrEmpty(text)) return null;
        text = text.Trim();

        if (text.StartsWith("<command-name>"))
        {
            var nameMatch = CommandNameRegex.Match(text);
            var argsMatch = CommandArgsRegex.Match(text);
            text = ((nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "") + " " +
                    (argsMatch.Success ? argsMatch.Groups[1].Value.Trim() : "")).Trim();
        }

        if (text.StartsWith("<local-command") || text.StartsWith("<system-reminder") || text.Length == 0)
            return null;

        // Collapse whitespace and truncate to 160 chars
        text = WhitespaceRegex.Replace(text, " ");
        return text.Length > 160 ? text[..160] : text;
    }

    // ── Output writers ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes sessions_catalog.json with indent=1 (one space) to match Python's json.dump(indent=1).
    /// The standard System.Text.Json Indented writer uses 2 spaces; we write the JSON manually.
    /// </summary>
    private static async Task WriteJsonAsync(string path, SessionCatalog catalog, CancellationToken ct)
    {
        // Serialize with the default indented writer then re-format to 1-space indentation.
        // Using a MemoryStream is fast enough for a catalog of hundreds of sessions.
        using var ms = new MemoryStream();
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        await JsonSerializer.SerializeAsync(ms, catalog, opts, ct);
        ms.Position = 0;
        using var reader = new StreamReader(ms, Encoding.UTF8);
        string twoSpace = await reader.ReadToEndAsync(ct);

        // Convert 2-space indent to 1-space indent
        string oneSpace = ConvertIndent(twoSpace, fromSpaces: 2, toSpaces: 1);

        string tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, oneSpace, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>Converts JSON indentation from <paramref name="fromSpaces"/> to <paramref name="toSpaces"/>.</summary>
    private static string ConvertIndent(string json, int fromSpaces, int toSpaces)
    {
        string from = new string(' ', fromSpaces);
        string to = new string(' ', toSpaces);
        var sb = new StringBuilder(json.Length);
        foreach (var rawLine in json.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            // Count leading "from" sequences and replace with "to" sequences
            int i = 0;
            int depth = 0;
            while (i + fromSpaces <= line.Length && line[i..(i + fromSpaces)] == from)
            {
                depth++;
                i += fromSpaces;
            }
            sb.Append(string.Concat(Enumerable.Repeat(to, depth)));
            sb.Append(line[i..]);
            sb.Append('\n');
        }
        // Remove trailing extra newline if any
        if (sb.Length > 0 && sb[^1] == '\n')
            sb.Length--;
        return sb.ToString();
    }

    private static void WriteMarkdown(string path, SessionCatalog catalog, bool hasBackup)
    {
        var sb = new StringBuilder();
        var counts = catalog.Counts;
        string generated = catalog.Generated;
        var rows = catalog.Sessions;
        var agentMode = catalog.AgentMode;

        sb.AppendLine("# Claude session catalog");
        sb.AppendLine();
        sb.Append($"Generated {generated}. {counts.Sessions} sessions: " +
                  $"{counts.TranscriptsLive} transcripts live, {counts.TranscriptsBackup} in backup, " +
                  $"{counts.IndexRecords} sidebar records, " +
                  $"{counts.LostFromLive} LOST from live (still in backup), " +
                  $"{counts.DanglingIndex} dangling sidebar records, " +
                  $"{counts.UnindexedLive} live transcripts without a sidebar record, " +
                  $"{counts.DeletedInApp} deleted in the app (transcript kept), " +
                  $"{counts.LiveNotInBackup} live transcripts not yet in backup.");
        sb.AppendLine();

        if (agentMode.Count > 0)
        {
            var names = agentMode.Select(a => a.Name).Distinct().OrderBy(n => n).Take(12);
            sb.AppendLine($"Agent-mode store: {agentMode.Count} entries ({string.Join(", ", names)}).");
        }

        sb.AppendLine("| # | Last activity | Project | Title | Prompts/Replies | Size | Live | Backup | Sidebar | cliSessionId |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        int i = 1;
        foreach (var s in rows)
        {
            string flag = "";
            if (s.TranscriptBackup && !s.TranscriptLive)
                flag = " **LOST**";
            else if (!s.TranscriptLive && !s.TranscriptBackup)
                flag = " **DANGLING**";
            if (s.IndexDeletedMarker)
                flag += " (deleted in app)";

            string lastActivity = FmtMs(s.LastMs);
            string project = ShortProject(s.Cwd).Replace("|", "\\|");
            string titleCol = (s.Title ?? "").Replace("|", "\\|");
            if (titleCol.Length > 90) titleCol = titleCol[..90];
            string sizeCol = HumanSize(s.Size);
            string liveCol = s.TranscriptLive ? "yes" : "no";
            string backupCol = s.TranscriptBackup ? "yes" : (!hasBackup ? "-" : "no");
            string sidebarCol = (s.IndexSessionId ?? "-")[..Math.Min(14, (s.IndexSessionId ?? "-").Length)];

            sb.AppendLine($"| {i} | {lastActivity} | {project} | {titleCol}{flag} | {s.UserPrompts}/{s.AssistantMsgs} | {sizeCol} | {liveCol} | {backupCol} | {sidebarCol} | `{s.CliSessionId}` |");
            i++;
        }

        string tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmpPath, path, overwrite: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long? IsoToMs(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try
        {
            string s2 = s.Replace("Z", "+00:00");
            var dto = DateTimeOffset.Parse(s2, System.Globalization.CultureInfo.InvariantCulture);
            return (long)(dto.ToUnixTimeMilliseconds());
        }
        catch
        {
            return null;
        }
    }

    private static string FmtMs(long? ms)
    {
        if (ms is null or <= 0) return "";
        return DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static string ShortProject(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        var parts = cwd.TrimEnd('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : cwd;
    }

    private static string HumanSize(long n)
    {
        if (n < 1024) return $"{n} B";
        double d = n;
        foreach (string unit in new[] { "KB", "MB", "GB" })
        {
            d /= 1024.0;
            if (d < 1024 || unit == "GB")
                return $"{d:F1} {unit}";
        }
        return $"{n} B"; // unreachable
    }

    // JsonElement helpers
    private static string? GetStringEl(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString();
        return null;
    }

    private static long? GetLongEl(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number) return p.GetInt64();
        }
        return null;
    }

    private static bool GetBoolEl(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.True) return true;
        }
        return false;
    }

    // JsonDocument root helpers
    private static string? GetString(JsonElement root, string name) => GetStringEl(root, name);

    private static long? GetLong(JsonElement root, string name) => GetLongEl(root, name);

    private static bool GetBool(JsonElement root, string name) => GetBoolEl(root, name);
}
