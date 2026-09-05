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

/// <summary>Where Claude Desktop (Cowork) and Claude Code keep things on this machine.</summary>
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

/// <summary>The nine stores. The single source of truth for what "a session store" means.</summary>
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

    /// <summary>The config-file whitelist shared by every Claude Desktop profile root.</summary>
    public static readonly IReadOnlyList<string> ProfileConfigFiles = new[]
    {
        "claude_desktop_config.json", "config.json", "git-worktrees.json", "mcp-user-tool-toggles.json",
        "cowork-enabled-cli-ops.json", "developer_settings.json", "extensions-installations.json", "ant-device-registry.json",
    };

    /// <summary>Files under ~\.claude that hold secrets. Never copied, in any mode.</summary>
    public static readonly IReadOnlyList<string> SecretFiles = new[] { ".credentials.json", ".claude.json" };

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

        new StoreDefinition(
            Cowork3pIndex, StoreMode.Tree, ClaudePaths.Claude3pSidebarIndex,
            ExcludeDirs: Array.Empty<string>(), ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "sidebar records of the second Claude Desktop profile (Claude-3p)"),

        new StoreDefinition(
            Cowork3pAgentMode, StoreMode.Tree, ClaudePaths.Claude3pAgentModeSessions,
            ExcludeDirs: new[] { "rpm" }, ExcludeFiles: Array.Empty<string>(), Files: Array.Empty<string>(), Dirs: Array.Empty<string>(),
            ShrinkGuard: false, Snapshot: true,
            Description: "VM / agent-mode sessions of the Claude-3p profile (rpm plugin cache excluded)"),

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
    };
}
