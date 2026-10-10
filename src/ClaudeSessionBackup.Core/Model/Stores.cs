namespace ClaudeSessionBackup.Core.Model;

/// <summary>How a store is copied.</summary>
public enum StoreMode
{
    /// <summary>The whole source tree, recursively, minus <see cref="StoreDefinition.ExcludeDirs"/>.</summary>
    Tree,

    /// <summary>
    /// Only the named top-level <see cref="StoreDefinition.Files"/> plus the named
    /// <see cref="StoreDefinition.Dirs"/> (each recursively). Used for the two config roots,
    /// which also hold caches, binaries and secrets that must never be copied.
    /// </summary>
    Whitelist,
}

/// <summary>
/// One session store: where it lives, how it is copied, and which safety rules apply.
/// Every property is present on every definition - consumers never probe for optional keys.
/// </summary>
public sealed partial record StoreDefinition(
    string Name,
    StoreMode Mode,
    string Source,
    IReadOnlyList<string> ExcludeDirs,
    IReadOnlyList<string> ExcludeFiles,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Dirs,
    bool ShrinkGuard,
    bool Snapshot,
    string Description)
{
    /// <summary>
    /// The source may legitimately be absent (a profile root the app creates and removes, such as the MSIX
    /// container's Roaming\Claude). An absent optional store is INFO, not a warning; when it exists it is
    /// copied like any other. Discovery treats it as covered either way, so its appearance is never "UNCOVERED".
    /// </summary>
    public bool Optional { get; init; }
}

/// <summary>Where Claude Desktop (Cowork), Claude Code and ZCode keep things on this machine.</summary>
public static class ClaudePaths
{
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>%USERPROFILE%\.claude</summary>
    public static string ClaudeHome => Path.Combine(UserProfile, ".claude");

    /// <summary>%USERPROFILE%\.claude\projects - transcripts (&lt;cwd-slug&gt;\&lt;sessionId&gt;.jsonl) and auto-memory.</summary>
    public static string Projects => Path.Combine(ClaudeHome, "projects");

    /// <summary>%APPDATA%\Claude</summary>
    public static string AppDataClaude => Path.Combine(AppData, "Claude");

    /// <summary>%APPDATA%\Claude\claude-code-sessions - Cowork sidebar records local_*.json + deleted_&lt;cliSessionId&gt; markers.</summary>
    public static string SidebarIndex => Path.Combine(AppDataClaude, "claude-code-sessions");

    /// <summary>%APPDATA%\Claude\local-agent-mode-sessions - VM / agent-mode sessions (rpm\ is a plugin cache).</summary>
    public static string AgentModeSessions => Path.Combine(AppDataClaude, "local-agent-mode-sessions");

    /// <summary>%APPDATA%\Claude\scratch-workspaces - working files of "No folder" sessions.</summary>
    public static string ScratchWorkspaces => Path.Combine(AppDataClaude, "scratch-workspaces");

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%LOCALAPPDATA%\Claude\Logs - the desktop app's LIVE logs (main.log, mcp-*.log, cowork_vm_node.log). %APPDATA%\Claude\logs stopped on 2026-08-22.</summary>
    public static string LocalLogs => Path.Combine(LocalAppData, "Claude", "Logs");

    /// <summary>%LOCALAPPDATA%\Claude-3p - a second Claude Desktop profile the app can create beside the main one. Its root holds host-creds-*.json: never copy the root.</summary>
    public static string Claude3p => Path.Combine(LocalAppData, "Claude-3p");
    public static string Claude3pSidebarIndex => Path.Combine(Claude3p, "claude-code-sessions");
    public static string Claude3pAgentModeSessions => Path.Combine(Claude3p, "local-agent-mode-sessions");

    /// <summary>%LOCALAPPDATA%\Packages - where Store (MSIX) apps get their per-package data.</summary>
    public static string Packages => Path.Combine(LocalAppData, "Packages");

    /// <summary>The Claude Desktop package family folder name as observed on this machine (publisher hash is stable across versions).</summary>
    public const string KnownMsixFamily = "Claude_pzs8sxrjxfjjc";

    /// <summary>
    /// The packaged app's virtualised %APPDATA%\Claude: &lt;Packages&gt;\Claude_*\LocalCache\Roaming\Claude.
    /// Resolved from the first Claude_* package folder present, else the known family name. The folder itself
    /// comes and goes (seen 2026-09-05 evening with a local-agent-mode-sessions inside, gone by 2026-09-06 01:00),
    /// which is why the stores under it are <see cref="StoreDefinition.Optional"/>.
    /// </summary>
    public static string MsixRoamingClaude
    {
        get
        {
            var family = KnownMsixFamily;
            try
            {
                if (Directory.Exists(Packages))
                {
                    var hit = Directory.EnumerateDirectories(Packages, "Claude_*").Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (!string.IsNullOrEmpty(hit)) family = hit!;
                }
            }
            catch
            {
                // a Packages folder we cannot list is not a reason to fail; the known name is the fallback
            }
            return Path.Combine(Packages, family, "LocalCache", "Roaming", "Claude");
        }
    }

    /// <summary>%USERPROFILE%\Claude\_claude_sessions_backup - the default destination, shared with the PowerShell tool.</summary>
    public static string DefaultDestination => Path.Combine(UserProfile, "Claude", "_claude_sessions_backup");

    /// <summary>
    /// The Claude Code CLI binary lives under %APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe.
    /// Any OTHER running claude.exe is the desktop app, which must be closed before the sidebar index is written.
    /// </summary>
    public static string CliBinaryRoot => Path.Combine(AppDataClaude, "claude-code");

    // ------------------------------------------------------------------ ZCode
    // ZCode (the agent this tool is being developed with) keeps its per-user data under
    // %USERPROFILE%\.zcode the way Claude Code uses ~\.claude. Measured 2026-10-01/02:
    //   cli\rollout   model-io-sess_<uuid>.jsonl - one append-only dump per session holding
    //                 every request and response (the raw conversation; large: ~0.5 GB after a day)
    //   cli\db        db.sqlite + -wal - the session index (conversations, tasks, rollups)
    //   cli\artifacts sess_<uuid>\call_*-tool-result-* - tool results and media the rollout logs reference
    //   cli\memories  memories\projects\<project>\memory - the persistent per-project memory
    //   cli\config.json - registered MCP servers, hooks, plugins (the claude_desktop_config.json analogue)
    // NOT stores, deliberately: cli\exec (ephemeral shell snapshots, regenerated per session),
    // cli\log (app telemetry), cli\plugins (a cache, like rpm/node_modules), and the WHOLE ~\.zcode\v2
    // root - it holds credentials.json (sign-in secrets; the Claude-3p rule).

    /// <summary>%USERPROFILE%\.zcode - the ZCode agent's home, the ~\.claude analogue.</summary>
    public static string ZcodeHome => Path.Combine(UserProfile, ".zcode");

    /// <summary>%USERPROFILE%\.zcode\cli - ZCode's per-user data root.</summary>
    public static string ZcodeCli => Path.Combine(ZcodeHome, "cli");

    /// <summary>%USERPROFILE%\.zcode\cli\rollout - model-io-sess_&lt;uuid&gt;.jsonl, the raw ZCode conversations.</summary>
    public static string ZcodeRollout => Path.Combine(ZcodeCli, "rollout");

    /// <summary>%USERPROFILE%\.zcode\cli\db - db.sqlite + -wal, the ZCode session index.</summary>
    public static string ZcodeDb => Path.Combine(ZcodeCli, "db");

    /// <summary>%USERPROFILE%\.zcode\cli\artifacts - per-session tool results and media.</summary>
    public static string ZcodeArtifacts => Path.Combine(ZcodeCli, "artifacts");

    /// <summary>%USERPROFILE%\.zcode\cli\memories - the persistent per-project memory.</summary>
    public static string ZcodeMemories => Path.Combine(ZcodeCli, "memories");
}

/// <summary>
/// Where the OTHER coding agents keep session data. Roots catalogued from clean-my-agent's
/// scanner providers (electron/lib/scanner-providers, main d714cdb) and checked on this machine
/// 2026-10-10: only %USERPROFILE%\.codex existed, holding config.toml alone - so every agent
/// store below is <see cref="StoreDefinition.Optional"/>: quiet while absent, copied the moment
/// the agent is installed and used.
///
/// Deliberately NOT stores, and why:
///  - Cursor's globalStorage: its state.vscdb carries the sign-in refresh token INSIDE the
///    database, where a file-name exclusion cannot reach (the Claude-3p rule: a root that mixes
///    secrets into undecodable state is never a store). Chats live per workspace in
///    workspaceStorage, which IS a store.
///  - Gemini (~\.gemini / ~\.config\gemini): the catalogue declares no Windows-specific root,
///    nothing exists here to measure, and the root is where oauth_creds.json lives. Add it from
///    a measured layout, not a guess; oauth_creds.json is already in SecretFiles.
///  - OpenCode's desktop-shell variants (%APPDATA%\ai.opencode.desktop[.beta]\opencode): add
///    when one is seen on disk; the three plain roots below are the catalogue's primary ones.
/// </summary>
public static class AgentPaths
{
    /// <summary>%USERPROFILE%\.codex - the Codex CLI home (config.toml beside auth.json, which SecretFiles blocks).</summary>
    public static string CodexHome => Path.Combine(ClaudePaths.UserProfile, ".codex");

    /// <summary>%USERPROFILE%\.codex\sessions - rollout-*.jsonl transcripts in dated subfolders.</summary>
    public static string CodexSessions => Path.Combine(CodexHome, "sessions");

    /// <summary>%USERPROFILE%\.codex\archived_sessions - the same shape, archived by the CLI.</summary>
    public static string CodexArchive => Path.Combine(CodexHome, "archived_sessions");

    /// <summary>%USERPROFILE%\.codex\tasks - task state beside the sessions.</summary>
    public static string CodexTasks => Path.Combine(CodexHome, "tasks");

    /// <summary>%APPDATA%\Cursor\User\workspaceStorage - one folder per workspace; chats live in each state.vscdb.</summary>
    public static string CursorWorkspaceStorage =>
        Path.Combine(ClaudePaths.AppData, "Cursor", "User", "workspaceStorage");

    /// <summary>%LOCALAPPDATA%\opencode</summary>
    public static string OpencodeLocal => Path.Combine(ClaudePaths.LocalAppData, "opencode");

    /// <summary>%APPDATA%\opencode</summary>
    public static string OpencodeRoaming => Path.Combine(ClaudePaths.AppData, "opencode");

    /// <summary>%USERPROFILE%\.local\share\opencode - opencode's cross-platform data root (auth.json beside it is blocked by SecretFiles).</summary>
    public static string OpencodeShare =>
        Path.Combine(ClaudePaths.UserProfile, ".local", "share", "opencode");

    /// <summary>%USERPROFILE%\.pi\agent\sessions - Pi agent JSONL transcripts.</summary>
    public static string PiSessions => Path.Combine(ClaudePaths.UserProfile, ".pi", "agent", "sessions");
}

/// <summary>Options for one backup or verify run. Mirrors the PowerShell tool's switches one to one.</summary>
public sealed partial class BackupOptions
{
    public string Destination { get; set; } = ClaudePaths.DefaultDestination;

    /// <summary>Also copy ~\.claude\projects\**\subagents (workflow scratch, ~1.2 GB). Off by default.</summary>
    public bool IncludeSubagents { get; set; }

    public bool NoSnapshot { get; set; }
    public bool NoCatalog { get; set; }

    /// <summary>Snapshot zips beyond this count are MOVED to &lt;Destination&gt;\_to_delete. 0 = keep all.</summary>
    public int KeepSnapshots { get; set; } = 60;

    /// <summary>Report only: compare live and backup, run the shrink scan without copying, build the catalog. Copies nothing.</summary>
    public bool Verify { get; set; }

    public string LiveRoot => Path.Combine(Destination, "live");
    public string SnapshotsDir => Path.Combine(Destination, "snapshots");
    public string CatalogDir => Path.Combine(Destination, "catalog");
    public string LogsDir => Path.Combine(Destination, "logs");
    public string QuarantineDir => Path.Combine(Destination, "quarantine");
    public string ToDeleteDir => Path.Combine(Destination, "_to_delete");
    public string LockFile => Path.Combine(Destination, ".lock");
    public string LedgerFile => Path.Combine(Destination, "backup.log");
    public string ManifestFile => Path.Combine(Destination, "last_run.json");
}

/// <summary>The twenty-seven stores (seven required, twenty Optional). The single source of truth for what "a session store" means.</summary>
public static class KnownStores
{
    public const string CodeTranscripts = "code-transcripts";
    public const string CodeConfig = "code-config";
    public const string CoworkIndex = "cowork-index";
    public const string CoworkAgentMode = "cowork-agent-mode";
    public const string CoworkScratch = "cowork-scratch";
    public const string CoworkConfig = "cowork-config";
    public const string CoworkLogs = "cowork-logs";
    public const string Cowork3pIndex = "cowork3p-index";
    public const string Cowork3pAgentMode = "cowork3p-agent-mode";
    public const string MsixIndex = "msix-index";
    public const string MsixAgentMode = "msix-agent-mode";
    public const string MsixScratch = "msix-scratch";
    public const string MsixConfig = "msix-config";
    public const string ZcodeTranscripts = "zcode-transcripts";
    public const string ZcodeDb = "zcode-db";
    public const string ZcodeArtifacts = "zcode-artifacts";
    public const string ZcodeMemories = "zcode-memories";
    public const string ZcodeConfig = "zcode-config";
    public const string CodexSessions = "codex-sessions";
    public const string CodexArchive = "codex-archive";
    public const string CodexTasks = "codex-tasks";
    public const string CodexConfig = "codex-config";
    public const string CursorWorkspaces = "cursor-workspaces";
    public const string OpencodeLocal = "opencode-local";
    public const string OpencodeRoaming = "opencode-roaming";
    public const string OpencodeShare = "opencode-share";
    public const string PiSessions = "pi-sessions";

    /// <summary>The config-file whitelist shared by every Claude Desktop profile root.</summary>
    public static readonly IReadOnlyList<string> ProfileConfigFiles = new[]
    {
        "claude_desktop_config.json", "config.json", "git-worktrees.json", "mcp-user-tool-toggles.json",
        "cowork-enabled-cli-ops.json", "developer_settings.json", "extensions-installations.json", "ant-device-registry.json",
    };

    /// <summary>Files that hold sign-in secrets. Never copied, in any mode, by any store. ".credentials.json" / ".claude.json" are Claude's (~\.claude); "credentials.json" is ZCode's (~\.zcode\v2\credentials.json) - no store roots at v2, and the name is skipped everywhere as defence in depth. "auth.json" is Codex's (~\.codex) and opencode's (~\.local\share\opencode); "oauth_creds.json" is Gemini's (~\.gemini) - the codex-* stores root below or beside it and gemini has no store at all, but the names are blocked everywhere for the same reason.</summary>
    public static readonly IReadOnlyList<string> SecretFiles = new[] { ".credentials.json", ".claude.json", "credentials.json", "auth.json", "oauth_creds.json" };

    public static IReadOnlyList<StoreDefinition> Default(BackupOptions options) => new[]
    {
        new StoreDefinition(
            CodeTranscripts, StoreMode.Tree, ClaudePaths.Projects,
            ExcludeDirs: options.IncludeSubagents ? Array.Empty<string>() : new[] { "subagents" },
            ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: true, Snapshot: false,
            Description: "Claude Code + Cowork-launched CLI transcripts (*.jsonl, append-only) and per-project auto-memory"),

        new StoreDefinition(
            CodeConfig, StoreMode.Whitelist, ClaudePaths.ClaudeHome,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: SecretFiles,
            Files: new[] { "settings.json", "settings.local.json", "CLAUDE.md", "history.jsonl", "statusline.js", "keybindings.json" },
            Dirs: new[] { "tasks", "scheduled-tasks", "skills", "commands", "plans", "todos", "agents", "hooks" },
            ShrinkGuard: false, Snapshot: true,
            Description: "Claude Code settings, CLAUDE.md, prompt history, task state, scheduled tasks, skills, commands"),

        new StoreDefinition(
            CoworkIndex, StoreMode.Tree, ClaudePaths.SidebarIndex,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "Cowork sidebar records (local_*.json, ~66 KB each) and deleted_<cliSessionId> markers"),

        new StoreDefinition(
            CoworkAgentMode, StoreMode.Tree, ClaudePaths.AgentModeSessions,
            ExcludeDirs: new[] { "rpm" }, ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "VM / agent-mode sessions - the only copy of those conversations (rpm plugin cache excluded)"),

        new StoreDefinition(
            CoworkScratch, StoreMode.Tree, ClaudePaths.ScratchWorkspaces,
            ExcludeDirs: new[] { "node_modules", ".git" }, ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "working files of \"No folder\" sessions (the app removes them when the session is gone)"),

        new StoreDefinition(
            CoworkConfig, StoreMode.Whitelist, ClaudePaths.AppDataClaude,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(),
            Files: ProfileConfigFiles,
            Dirs: new[] { "logs" },
            ShrinkGuard: false, Snapshot: true,
            Description: "Claude Desktop config, tool toggles, worktrees, and logs"),

        // Found by store discovery on 2026-09-05 (the user asked why the MSIX container was ignored):
        // the desktop app's live logs moved to %LOCALAPPDATA%, and a dormant second profile exists.
        new StoreDefinition(
            CoworkLogs, StoreMode.Tree, ClaudePaths.LocalLogs,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "Claude Desktop live logs (main.log recorded the 2026-08-22 wipe; mcp-*.log, cowork_vm_node.log)"),

        // Optional since 1.0.4 (2026-09-27). The profile is dormant (last activity 2026-08-14) and its
        // two session folders disappeared between 2026-09-15 and 2026-09-20 while the profile root
        // stayed; every run since had warned "source missing" twice for stores whose backup copy was
        // intact and whose profile may never be launched again. Optional keeps that copy, copies again
        // the moment the folders come back, and reports their absence at INFO with a muted pill.
        new StoreDefinition(
            Cowork3pIndex, StoreMode.Tree, ClaudePaths.Claude3pSidebarIndex,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "sidebar records of the second Claude Desktop profile (Claude-3p)") { Optional = true },

        new StoreDefinition(
            Cowork3pAgentMode, StoreMode.Tree, ClaudePaths.Claude3pAgentModeSessions,
            ExcludeDirs: new[] { "rpm" }, ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "VM / agent-mode sessions of the Claude-3p profile (rpm plugin cache excluded)") { Optional = true },

        // The packaged (Store) app's virtualised profile root. Seen populated on 2026-09-05 evening
        // (local-agent-mode-sessions inside) and gone again by 2026-09-06 01:00 - the same come-and-go
        // pattern as the 2026-08-22 wipe. Optional: quiet while absent, copied whenever present, and
        // never reported as UNCOVERED by store discovery.
        new StoreDefinition(
            MsixIndex, StoreMode.Tree, Path.Combine(ClaudePaths.MsixRoamingClaude, "claude-code-sessions"),
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "sidebar records written by the packaged (Store) Claude Desktop into its MSIX container") { Optional = true },

        new StoreDefinition(
            MsixAgentMode, StoreMode.Tree, Path.Combine(ClaudePaths.MsixRoamingClaude, "local-agent-mode-sessions"),
            ExcludeDirs: new[] { "rpm" }, ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "VM / agent-mode sessions written into the MSIX container (rpm plugin cache excluded)") { Optional = true },

        new StoreDefinition(
            MsixScratch, StoreMode.Tree, Path.Combine(ClaudePaths.MsixRoamingClaude, "scratch-workspaces"),
            ExcludeDirs: new[] { "node_modules", ".git" }, ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "scratch workspaces written into the MSIX container") { Optional = true },

        new StoreDefinition(
            MsixConfig, StoreMode.Whitelist, ClaudePaths.MsixRoamingClaude,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(),
            Files: ProfileConfigFiles,
            Dirs: new[] { "logs" },
            ShrinkGuard: false, Snapshot: true,
            Description: "config and logs of the packaged Claude Desktop's container profile") { Optional = true },

        // ZCode (added 2026-10-02). All five are Optional for the cowork3p reason: the data root
        // belongs to software installed on 2026-10-01 - if ZCode is ever uninstalled, or moves its
        // root the way the desktop app moved into the MSIX container, five absent stores must be
        // one INFO line each, not five nightly WARNs. Optional keeps the backup copy, copies again
        // the moment the folders return, and store discovery never reports them as UNCOVERED.
        new StoreDefinition(
            ZcodeTranscripts, StoreMode.Tree, ClaudePaths.ZcodeRollout,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: true, Snapshot: false,
            Description: "ZCode conversations - model-io-sess_<uuid>.jsonl, append-only request/response dumps (large; never snapshotted, same rule as code-transcripts)") { Optional = true },

        new StoreDefinition(
            ZcodeDb, StoreMode.Tree, ClaudePaths.ZcodeDb,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: new[] { "db.sqlite-shm" }, Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "ZCode session index (db.sqlite + write-ahead log; -shm excluded - shared-memory state, meaningless without the writing process)") { Optional = true },

        new StoreDefinition(
            ZcodeArtifacts, StoreMode.Tree, ClaudePaths.ZcodeArtifacts,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "ZCode per-session tool results and media (sess_<uuid>\\call_*-tool-result-*) - the images and big outputs the rollout logs reference") { Optional = true },

        new StoreDefinition(
            ZcodeMemories, StoreMode.Tree, ClaudePaths.ZcodeMemories,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "ZCode persistent memory (memories\\projects\\<project>\\memory)") { Optional = true },

        new StoreDefinition(
            ZcodeConfig, StoreMode.Whitelist, ClaudePaths.ZcodeCli,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(),
            Files: new[] { "config.json" },
            Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "ZCode config - registered MCP servers, hooks, plugins (the claude_desktop_config.json analogue)") { Optional = true },

        // The other coding agents (added 2026-10-10), roots per the AgentPaths catalogue. All
        // Optional for the zcode reason: on this machine only ~\.codex existed (config.toml
        // alone), and an agent never installed must cost one INFO line, not a nightly WARN.
        // Session-shaped stores (append-only JSONL) take the shrink guard and are never
        // snapshotted, the code-transcripts rule; small state snapshots.
        new StoreDefinition(
            CodexSessions, StoreMode.Tree, AgentPaths.CodexSessions,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: true, Snapshot: false,
            Description: "Codex CLI conversations - rollout-*.jsonl in dated subfolders, append-only") { Optional = true },

        new StoreDefinition(
            CodexArchive, StoreMode.Tree, AgentPaths.CodexArchive,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: true, Snapshot: false,
            Description: "Codex CLI archived conversations - the sessions tree's shape, moved here by the CLI") { Optional = true },

        new StoreDefinition(
            CodexTasks, StoreMode.Tree, AgentPaths.CodexTasks,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "Codex CLI task state (small, beside the sessions; state may legitimately shrink)") { Optional = true },

        new StoreDefinition(
            CodexConfig, StoreMode.Whitelist, AgentPaths.CodexHome,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(),
            Files: new[] { "config.toml" },
            Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "Codex CLI config (config.toml only - auth.json lives beside it and is never copied)") { Optional = true },

        new StoreDefinition(
            CursorWorkspaces, StoreMode.Tree, AgentPaths.CursorWorkspaceStorage,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: new[] { "state.vscdb-shm" }, Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "Cursor per-workspace state incl. its chats (state.vscdb + -wal; -shm excluded, the zcode-db rule; globalStorage is deliberately NOT a store - its database carries the sign-in token)") { Optional = true },

        new StoreDefinition(
            OpencodeLocal, StoreMode.Tree, AgentPaths.OpencodeLocal,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "OpenCode data under %LOCALAPPDATA% (one of its three catalogued roots)") { Optional = true },

        new StoreDefinition(
            OpencodeRoaming, StoreMode.Tree, AgentPaths.OpencodeRoaming,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "OpenCode data under %APPDATA% (the desktop-shell variants are added when seen on disk)") { Optional = true },

        new StoreDefinition(
            OpencodeShare, StoreMode.Tree, AgentPaths.OpencodeShare,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: false,
            Description: "OpenCode cross-platform data root (~\\.local\\share\\opencode; auth.json beside it is never copied)") { Optional = true },

        new StoreDefinition(
            PiSessions, StoreMode.Tree, AgentPaths.PiSessions,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: true, Snapshot: false,
            Description: "Pi agent conversations (~\\.pi\\agent\\sessions, JSONL, append-only)") { Optional = true },
    };
}
