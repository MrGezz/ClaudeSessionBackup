using System.Text.Json.Serialization;

namespace ClaudeSessionBackup.Core.Model;

/// <summary>
/// One row of the session catalog: a transcript, a sidebar record, or both, joined on
/// cliSessionId == transcript sessionId. JSON property names are snake_case and identical
/// to the PowerShell/Python tool's <c>sessions_catalog.json</c> so the two remain interchangeable.
/// </summary>
public sealed partial class SessionEntry
{
    [JsonPropertyName("cli_session_id")] public string CliSessionId { get; set; } = "";

    /// <summary>Resolved title: custom-title &gt; ai-title &gt; sidebar title &gt; summary &gt; first prompt &gt; "(untitled)".</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "(untitled)";

    /// <summary>"custom-title" | "ai-title" | "sidebar" | "summary" | "first-prompt" | "none"</summary>
    [JsonPropertyName("title_source")] public string TitleSource { get; set; } = "none";

    [JsonPropertyName("custom_title")] public string? CustomTitle { get; set; }
    [JsonPropertyName("ai_title")] public string? AiTitle { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }

    /// <summary>First path segment of <see cref="TranscriptRel"/> (the cwd slug), or the slug computed from cwd for sidebar-only rows.</summary>
    [JsonPropertyName("project_dir")] public string ProjectDir { get; set; } = "";

    /// <summary>"&lt;slug&gt;/&lt;sessionId&gt;.jsonl" with forward slashes, relative to the projects root; null for sidebar-only rows.</summary>
    [JsonPropertyName("transcript_rel")] public string? TranscriptRel { get; set; }

    [JsonPropertyName("transcript_live")] public bool TranscriptLive { get; set; }
    [JsonPropertyName("transcript_backup")] public bool TranscriptBackup { get; set; }
    [JsonPropertyName("is_subagent")] public bool IsSubagent { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("first_ts")] public string? FirstTs { get; set; }
    [JsonPropertyName("last_ts")] public string? LastTs { get; set; }

    /// <summary>Epoch milliseconds; falls back to the sidebar record, then the file mtime.</summary>
    [JsonPropertyName("first_ms")] public long? FirstMs { get; set; }
    [JsonPropertyName("last_ms")] public long? LastMs { get; set; }

    [JsonPropertyName("entrypoint")] public string? Entrypoint { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("git_branch")] public string? GitBranch { get; set; }

    /// <summary>Human prompts (user records with text content; tool_result-only records are not prompts).</summary>
    [JsonPropertyName("user_prompts")] public int UserPrompts { get; set; }

    /// <summary>Assistant messages (assistant records whose apiBlockIndex is 0 or absent).</summary>
    [JsonPropertyName("assistant_msgs")] public int AssistantMsgs { get; set; }

    [JsonPropertyName("records")] public int Records { get; set; }
    [JsonPropertyName("first_prompt")] public string? FirstPrompt { get; set; }

    [JsonPropertyName("index_session_id")] public string? IndexSessionId { get; set; }
    [JsonPropertyName("index_file")] public string? IndexFile { get; set; }
    [JsonPropertyName("index_flagged")] public bool? IndexFlagged { get; set; }
    [JsonPropertyName("index_archived")] public bool? IndexArchived { get; set; }

    /// <summary>The app marked this session deleted (deleted_&lt;cliSessionId&gt; marker in the live OR backup index).</summary>
    [JsonPropertyName("index_deleted_marker")] public bool IndexDeletedMarker { get; set; }

    [JsonIgnore] public bool IsLost => TranscriptBackup && !TranscriptLive;
    [JsonIgnore] public bool IsDangling => !TranscriptLive && !TranscriptBackup;
}

/// <summary>A Cowork sidebar record as read from local_*.json (only the fields the catalog needs).</summary>
public sealed partial class IndexRecord
{
    public string IndexSessionId { get; set; } = "";
    public string? CliSessionId { get; set; }
    public string? Cwd { get; set; }
    public string? Title { get; set; }
    public long? CreatedAt { get; set; }
    public long? LastActivityAt { get; set; }
    public bool IsArchived { get; set; }
    public string? Model { get; set; }
    public string? ScheduledTaskId { get; set; }

    /// <summary>Path relative to the index root, forward slashes.</summary>
    public string IndexFile { get; set; } = "";

    /// <summary>Lives inside a *_backup_* or deleted_* folder: kept but loses to a live record with the same sessionId.</summary>
    public bool Flagged { get; set; }
}

/// <summary>
/// One session file of a NON-Claude agent (ZCode, Codex, Pi), listed in the catalog from the
/// live root and the backup copy. File facts only - these formats are each agent's own, so the
/// catalog never parses them (the zcode rollouts alone were 516 MB when measured); name, size
/// and mtime answer "what sessions exist, and is each one backed up".
/// </summary>
public sealed partial class AgentSessionEntry
{
    /// <summary>"zcode" | "codex" | "codex-archive" | "pi" - the source list's agent label.</summary>
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";

    /// <summary>Path relative to the agent's live root, forward slashes (the live/backup join key).</summary>
    [JsonPropertyName("rel")] public string Rel { get; set; } = "";

    /// <summary>File name without extension (e.g. "model-io-sess_&lt;uuid&gt;").</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime_ms")] public long MtimeMs { get; set; }
    [JsonPropertyName("live")] public bool Live { get; set; }
    [JsonPropertyName("backup")] public bool Backup { get; set; }
}

/// <summary>One agent root the catalog lists: where its session files live, and where the backup copy is.</summary>
public sealed partial class AgentCatalogSource
{
    public AgentCatalogSource(string agent, string? liveDir, string? backupDir)
    {
        Agent = agent;
        LiveDir = liveDir;
        BackupDir = backupDir;
    }

    public string Agent { get; }
    public string? LiveDir { get; }
    public string? BackupDir { get; }

    /// <summary>Session-file pattern under the roots (recursive; Codex nests by date).</summary>
    public string Pattern { get; init; } = "*.jsonl";
}

/// <summary>The agent sources every catalog build lists - the one place the set is defined.</summary>
public static class AgentCatalog
{
    /// <summary>
    /// ZCode, Codex (live + archive) and Pi: the agents whose stores are file-per-session JSONL.
    /// Cursor and OpenCode are backed up by their stores but keep sessions inside databases and
    /// nested JSON state, so a per-file listing would mislead - they are deliberately absent here.
    /// <paramref name="backupLiveRoot"/> is &lt;destination&gt;\live, or null for a live-only listing.
    /// </summary>
    public static IReadOnlyList<AgentCatalogSource> DefaultSources(string? backupLiveRoot)
    {
        string? B(string store) => backupLiveRoot is null ? null : Path.Combine(backupLiveRoot, store);
        return new[]
        {
            new AgentCatalogSource("zcode", ClaudePaths.ZcodeRollout, B(KnownStores.ZcodeTranscripts)),
            new AgentCatalogSource("codex", AgentPaths.CodexSessions, B(KnownStores.CodexSessions)),
            new AgentCatalogSource("codex-archive", AgentPaths.CodexArchive, B(KnownStores.CodexArchive)),
            new AgentCatalogSource("pi", AgentPaths.PiSessions, B(KnownStores.PiSessions)),
        };
    }
}

public sealed partial class AgentModeEntry
{
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("org")] public string Org { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime_ms")] public long MtimeMs { get; set; }
}

public sealed partial class CatalogCounts
{
    [JsonPropertyName("sessions")] public int Sessions { get; set; }
    [JsonPropertyName("transcripts_live")] public int TranscriptsLive { get; set; }
    [JsonPropertyName("transcripts_backup")] public int TranscriptsBackup { get; set; }
    [JsonPropertyName("index_records")] public int IndexRecords { get; set; }
    [JsonPropertyName("index_records_flagged")] public int IndexRecordsFlagged { get; set; }
    [JsonPropertyName("lost_from_live")] public int LostFromLive { get; set; }
    [JsonPropertyName("dangling_index")] public int DanglingIndex { get; set; }
    [JsonPropertyName("unindexed_live")] public int UnindexedLive { get; set; }
    [JsonPropertyName("deleted_in_app")] public int DeletedInApp { get; set; }
    [JsonPropertyName("live_not_in_backup")] public int LiveNotInBackup { get; set; }
    [JsonPropertyName("rescanned")] public int Rescanned { get; set; }
    [JsonPropertyName("agent_mode_entries")] public int AgentModeEntries { get; set; }

    /// <summary>Non-Claude agent session files listed (zcode, codex, codex-archive, pi).</summary>
    [JsonPropertyName("agent_sessions")] public int AgentSessions { get; set; }

    /// <summary>Of those, live files whose backup copy is missing.</summary>
    [JsonPropertyName("agent_not_in_backup")] public int AgentNotInBackup { get; set; }
}

public sealed partial class CatalogSources
{
    [JsonPropertyName("projects")] public string? Projects { get; set; }
    [JsonPropertyName("index")] public string? Index { get; set; }
    [JsonPropertyName("agent_mode")] public string? AgentMode { get; set; }
    [JsonPropertyName("backup_projects")] public string? BackupProjects { get; set; }
    [JsonPropertyName("backup_index")] public string? BackupIndex { get; set; }
    [JsonPropertyName("include_subagents")] public bool IncludeSubagents { get; set; }
}

/// <summary>The whole catalog (sessions_catalog.json).</summary>
public sealed partial class SessionCatalog
{
    /// <summary>"yyyy-MM-dd HH:mm:ss" local time.</summary>
    [JsonPropertyName("generated")] public string Generated { get; set; } = "";
    [JsonPropertyName("sources")] public CatalogSources Sources { get; set; } = new();
    [JsonPropertyName("counts")] public CatalogCounts Counts { get; set; } = new();
    [JsonPropertyName("deleted_markers")] public List<string> DeletedMarkers { get; set; } = new();
    /// <summary>Newest first by <see cref="SessionEntry.LastMs"/>.</summary>
    [JsonPropertyName("sessions")] public List<SessionEntry> Sessions { get; set; } = new();
    [JsonPropertyName("agent_mode")] public List<AgentModeEntry> AgentMode { get; set; } = new();

    /// <summary>Non-Claude agent session files (zcode, codex, codex-archive, pi), newest first by mtime.</summary>
    [JsonPropertyName("agent_sessions")] public List<AgentSessionEntry> AgentSessions { get; set; } = new();
}

/// <summary>Inputs to the catalog builder. Paths null = that source is skipped.</summary>
public sealed partial class CatalogOptions
{
    public string ProjectsDir { get; set; } = ClaudePaths.Projects;
    public string IndexDir { get; set; } = ClaudePaths.SidebarIndex;
    public string? AgentModeDir { get; set; } = ClaudePaths.AgentModeSessions;

    /// <summary>The backup copy of the projects tree. Enables LOST detection (in backup, not live).</summary>
    public string? BackupProjectsDir { get; set; }

    /// <summary>The backup copy of the sidebar index. Its deleted_* markers are unioned with the live ones.</summary>
    public string? BackupIndexDir { get; set; }

    /// <summary>Where sessions_catalog.json, sessions_catalog.md, sessions_catalog.previous.json and catalog_cache.json go.</summary>
    public string OutDir { get; set; } = "";

    public bool IncludeSubagents { get; set; }

    /// <summary>Re-scan every transcript instead of trusting catalog_cache.json.</summary>
    public bool NoCache { get; set; }

    /// <summary>
    /// Non-Claude agent roots to list (file facts only, no parsing). Callers pass
    /// <see cref="AgentCatalog.DefaultSources"/> with their backup live root; empty = none listed.
    /// </summary>
    public IReadOnlyList<AgentCatalogSource> Agents { get; set; } = Array.Empty<AgentCatalogSource>();
}
