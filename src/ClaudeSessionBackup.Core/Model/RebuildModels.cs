namespace ClaudeSessionBackup.Core.Model;

/// <summary>Options for regenerating missing Cowork sidebar records from a catalog.</summary>
public sealed partial class RebuildOptions
{
    /// <summary>sessions_catalog.json produced by the catalog builder.</summary>
    public string CatalogPath { get; set; } = "";

    /// <summary>Live claude-code-sessions root (the folder holding &lt;account&gt;\&lt;org&gt;).</summary>
    public string IndexRoot { get; set; } = ClaudePaths.SidebarIndex;

    /// <summary>Explicit &lt;root&gt;\&lt;account&gt;\&lt;org&gt; folder; skips auto-detection.</summary>
    public string? IndexDir { get; set; }

    /// <summary>Live ~\.claude\projects; a session is only rebuilt if its transcript exists here.</summary>
    public string ProjectsDir { get; set; } = ClaudePaths.Projects;

    /// <summary>Explicit donor local_*.json. Default: the reference record with the most keys and no rare key.</summary>
    public string? DonorPath { get; set; }

    /// <summary>Backup copy of claude-code-sessions. Default: &lt;catalog dir&gt;\..\live\cowork-index. Used for the donor when the live index is empty, and ALWAYS for deleted markers.</summary>
    public string? BackupIndexDir { get; set; }

    /// <summary>Write files. Default false = plan only.</summary>
    public bool Commit { get; set; }

    /// <summary>Write to a staging folder instead of the live index folder.</summary>
    public string? OutDir { get; set; }

    public bool IncludeEmpty { get; set; }
    public bool IncludeSubagents { get; set; }

    /// <summary>Also emit records for sessions started from a terminal (entrypoint "cli"). Default: only Cowork-launched sessions get sidebar records.</summary>
    public bool IncludeCli { get; set; }

    /// <summary>Also emit records for sessions the app marked deleted. Default: a session you deleted in the sidebar stays deleted.</summary>
    public bool IncludeDeleted { get; set; }

    /// <summary>Write even if the desktop app appears to be running. Dangerous: the app rewrites the folder from memory.</summary>
    public bool Force { get; set; }
}

/// <summary>One record the rebuild would write (or wrote).</summary>
public sealed partial record PlannedRecord(SessionEntry Session, string FileName, string Json);

public sealed partial record SkippedSession(SessionEntry Session, string Reason);

/// <summary>Everything decided before any file is written. The CLI prints it; the app shows it; <c>Apply</c> executes it.</summary>
public sealed partial class RebuildPlan
{
    public string CatalogPath { get; set; } = "";
    public string CatalogGenerated { get; set; } = "";
    public int CatalogSessions { get; set; }
    public string DonorPath { get; set; } = "";
    public int DonorKeys { get; set; }
    public bool DonorCompact { get; set; }

    /// <summary>Number of records the key statistics were computed from (live index, else backup copy).</summary>
    public int ReferenceRecords { get; set; }

    /// <summary>Donor keys that will be removed from every synthesised record.</summary>
    public List<string> DroppedKeys { get; set; } = new();
    public string IndexDir { get; set; } = "";
    public int LiveRecords { get; set; }
    public int DeletedMarkers { get; set; }
    public string OutDir { get; set; } = "";
    public List<PlannedRecord> ToWrite { get; set; } = new();
    public List<SkippedSession> Skipped { get; set; } = new();

    /// <summary>Keys per synthesised record (donor keys minus dropped).</summary>
    public int KeysPerRecord { get; set; }
}

public sealed partial class RebuildResult
{
    public bool Refused { get; set; }
    public string? RefusalReason { get; set; }
    public string? IndexBackupDir { get; set; }
    public List<string> WrittenFiles { get; set; } = new();
}

/// <summary>Scheduled-task settings. One task, root folder, interactive user, least privilege.</summary>
public sealed partial class ScheduleOptions
{
    public string TaskName { get; set; } = "ClaudeSessionBackup";

    /// <summary>"HH:mm" local time for the daily trigger.</summary>
    public string StartAt { get; set; } = "21:00";

    public bool AtLogon { get; set; } = true;
    public TimeSpan LogonDelay { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Passed through to the CLI action as --destination.</summary>
    public string Destination { get; set; } = ClaudePaths.DefaultDestination;

    public bool IncludeSubagents { get; set; }
}

public sealed partial record ScheduledTaskInfo(
    bool Exists,
    DateTime? NextRun,
    DateTime? LastRun,
    string? LastResult,
    string? State,
    string? Action);
