using System.Text;
using System.Text.Json;
using ClaudeSessionBackup.Core.Catalog;
using ClaudeSessionBackup.Core.Config;
using ClaudeSessionBackup.Core.Engine;
using ClaudeSessionBackup.Core.Model;
using ClaudeSessionBackup.Core.Rebuild;
using ClaudeSessionBackup.Core.Scheduling;
using ClaudeSessionBackup.Core.Transcripts;

namespace ClaudeSessionBackup.Cli;

// ---------------------------------------------------------------------------
// Typed command objects  (returned by Parse; used by RunAsync and by tests)
// ---------------------------------------------------------------------------

internal abstract record CliCommand;

internal sealed record BackupCommand(
    string? Destination,
    bool IncludeSubagents,
    bool NoSnapshot,
    bool NoCatalog,
    int? KeepSnapshots,
    bool Quiet,
    bool Verify) : CliCommand;

internal sealed record CatalogCommand(
    string? Destination,
    bool IncludeSubagents,
    bool NoCache,
    bool Report,
    bool Quiet) : CliCommand;

internal sealed record RebuildCommand(
    string CatalogFile,
    bool Commit,
    string? OutDir,
    string? IndexRoot,
    string? IndexDir,
    string? Donor,
    string? BackupIndex,
    bool IncludeEmpty,
    bool IncludeSubagents,
    bool IncludeCli,
    bool IncludeDeleted,
    bool Force,
    bool Quiet) : CliCommand;

internal sealed record InstallTaskCommand(
    string? Time,
    bool NoLogon,
    string? Destination,
    bool IncludeSubagents) : CliCommand;

internal sealed record UninstallTaskCommand : CliCommand;

internal sealed record TaskStatusCommand : CliCommand;

internal sealed record HelpCommand : CliCommand;

internal sealed record ExportCommand(
    string? Session,
    string? FilePath,
    /// <summary>"backup" (default) or "live".</summary>
    string Source,
    string? Destination,
    /// <summary>"md" (default) or "html".</summary>
    string Format,
    string? Out,
    bool NoThinking,
    bool NoTools,
    bool NoResults,
    bool NoSystem,
    bool Attachments,
    bool NoImages,
    int MaxResultChars,
    bool Quiet) : CliCommand;

// Represents a parse error that should exit 3
internal sealed record UsageErrorCommand(string Message) : CliCommand;

/// <summary>
/// The headless entry point - what Task Scheduler runs.
/// </summary>
/// <remarks>
/// Command surface (the contract the app, the README and the scheduled task rely on):
/// <code>
/// ClaudeSessionBackup.Cli backup        [--destination D] [--include-subagents] [--no-snapshot] [--no-catalog] [--keep-snapshots N] [--quiet]
/// ClaudeSessionBackup.Cli verify        [--destination D] [--include-subagents] [--quiet]
/// ClaudeSessionBackup.Cli catalog       [--destination D] [--include-subagents] [--no-cache] [--report]
/// ClaudeSessionBackup.Cli rebuild       --catalog F [--commit] [--out-dir D] [--index-root R] [--index-dir D] [--donor F] [--backup-index D]
///                                       [--include-empty] [--include-subagents] [--include-cli] [--include-deleted] [--force]
/// ClaudeSessionBackup.Cli install-task  [--time HH:mm] [--no-logon] [--destination D] [--include-subagents]
/// ClaudeSessionBackup.Cli uninstall-task
/// ClaudeSessionBackup.Cli task-status
/// ClaudeSessionBackup.Cli --help
/// </code>
/// Options default to %APPDATA%\ClaudeSessionBackup\settings.json (SettingsStore) when not given on the command line.
/// Exit codes: 0 ok, 1 ok with warnings, 2 failures, 3 usage / config error, 4 refused (another run holds the lock,
/// or the desktop app is running for a rebuild --commit).
/// </remarks>
internal static class Program
{
    internal const int ExitOk = 0;
    internal const int ExitWarnings = 1;
    internal const int ExitFailed = 2;
    internal const int ExitUsage = 3;
    internal const int ExitRefused = 4;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return ExitFailed;
        }
    }

    // Visible to tests without running real commands.
    internal static CliCommand Parse(string[] args)
    {
        if (args.Length == 0)
            return new UsageErrorCommand("No command given.");

        // --help at any position
        if (args.Any(a => a is "--help" or "-h" or "-?"))
            return new HelpCommand();

        var verb = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return verb switch
        {
            "backup"          => ParseBackup(rest, verify: false),
            "verify"          => ParseBackup(rest, verify: true),
            "catalog"         => ParseCatalog(rest),
            "rebuild"         => ParseRebuild(rest),
            "export"          => ParseExport(rest),
            "install-task"    => ParseInstallTask(rest),
            "uninstall-task"  => ParseUninstallTask(rest),
            "task-status"     => ParseTaskStatus(rest),
            _                 => new UsageErrorCommand($"Unknown command: '{args[0]}'")
        };
    }

    // ---------------------------------------------------------------------------
    // Parse helpers
    // ---------------------------------------------------------------------------

    private static CliCommand ParseBackup(string[] args, bool verify)
    {
        string? destination = null;
        bool includeSubagents = false, noSnapshot = false, noCatalog = false, quiet = false;
        int? keepSnapshots = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--destination":
                    if (!TryNext(args, ref i, out var dest))
                        return new UsageErrorCommand("--destination requires a value.");
                    destination = dest;
                    break;
                case "--include-subagents": includeSubagents = true; break;
                case "--no-snapshot":       noSnapshot = true; break;
                case "--no-catalog":        noCatalog = true; break;
                case "--quiet":             quiet = true; break;
                case "--keep-snapshots":
                    if (!TryNext(args, ref i, out var ks) || !int.TryParse(ks, out var n))
                        return new UsageErrorCommand("--keep-snapshots requires an integer.");
                    keepSnapshots = n;
                    break;
                default:
                    return new UsageErrorCommand($"Unknown option: '{args[i]}'");
            }
        }

        return new BackupCommand(destination, includeSubagents, noSnapshot, noCatalog, keepSnapshots, quiet, verify);
    }

    private static CliCommand ParseCatalog(string[] args)
    {
        string? destination = null;
        bool includeSubagents = false, noCache = false, report = false, quiet = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--destination":
                    if (!TryNext(args, ref i, out var dest))
                        return new UsageErrorCommand("--destination requires a value.");
                    destination = dest;
                    break;
                case "--include-subagents": includeSubagents = true; break;
                case "--no-cache":          noCache = true; break;
                case "--report":            report = true; break;
                case "--quiet":             quiet = true; break;
                default:
                    return new UsageErrorCommand($"Unknown option: '{args[i]}'");
            }
        }

        return new CatalogCommand(destination, includeSubagents, noCache, report, quiet);
    }

    private static CliCommand ParseRebuild(string[] args)
    {
        string? catalogFile = null, outDir = null, indexRoot = null, indexDir = null;
        string? donor = null, backupIndex = null;
        bool commit = false, includeEmpty = false, includeSubagents = false;
        bool includeCli = false, includeDeleted = false, force = false, quiet = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--catalog":
                    if (!TryNext(args, ref i, out var c))
                        return new UsageErrorCommand("--catalog requires a value.");
                    catalogFile = c;
                    break;
                case "--out-dir":
                    if (!TryNext(args, ref i, out var od))
                        return new UsageErrorCommand("--out-dir requires a value.");
                    outDir = od;
                    break;
                case "--index-root":
                    if (!TryNext(args, ref i, out var ir))
                        return new UsageErrorCommand("--index-root requires a value.");
                    indexRoot = ir;
                    break;
                case "--index-dir":
                    if (!TryNext(args, ref i, out var id))
                        return new UsageErrorCommand("--index-dir requires a value.");
                    indexDir = id;
                    break;
                case "--donor":
                    if (!TryNext(args, ref i, out var d))
                        return new UsageErrorCommand("--donor requires a value.");
                    donor = d;
                    break;
                case "--backup-index":
                    if (!TryNext(args, ref i, out var bi))
                        return new UsageErrorCommand("--backup-index requires a value.");
                    backupIndex = bi;
                    break;
                case "--commit":            commit = true; break;
                case "--include-empty":     includeEmpty = true; break;
                case "--include-subagents": includeSubagents = true; break;
                case "--include-cli":       includeCli = true; break;
                case "--include-deleted":   includeDeleted = true; break;
                case "--force":             force = true; break;
                case "--quiet":             quiet = true; break;
                default:
                    return new UsageErrorCommand($"Unknown option: '{args[i]}'");
            }
        }

        if (catalogFile is null)
            return new UsageErrorCommand("rebuild requires --catalog <file>.");

        return new RebuildCommand(catalogFile, commit, outDir, indexRoot, indexDir,
            donor, backupIndex, includeEmpty, includeSubagents, includeCli, includeDeleted, force, quiet);
    }

    private static CliCommand ParseInstallTask(string[] args)
    {
        string? time = null, destination = null;
        bool noLogon = false, includeSubagents = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--time":
                    if (!TryNext(args, ref i, out var t))
                        return new UsageErrorCommand("--time requires a value.");
                    time = t;
                    break;
                case "--destination":
                    if (!TryNext(args, ref i, out var dest))
                        return new UsageErrorCommand("--destination requires a value.");
                    destination = dest;
                    break;
                case "--no-logon":          noLogon = true; break;
                case "--include-subagents": includeSubagents = true; break;
                default:
                    return new UsageErrorCommand($"Unknown option: '{args[i]}'");
            }
        }

        return new InstallTaskCommand(time, noLogon, destination, includeSubagents);
    }

    private static CliCommand ParseUninstallTask(string[] args)
    {
        if (args.Length > 0)
            return new UsageErrorCommand($"uninstall-task takes no options (got: '{args[0]}').");
        return new UninstallTaskCommand();
    }

    private static CliCommand ParseTaskStatus(string[] args)
    {
        if (args.Length > 0)
            return new UsageErrorCommand($"task-status takes no options (got: '{args[0]}').");
        return new TaskStatusCommand();
    }

    private static CliCommand ParseExport(string[] args)
    {
        string? session = null, filePath = null, destination = null, outPath = null;
        string source = "backup", format = "md";
        bool noThinking = false, noTools = false, noResults = false, noSystem = false;
        bool attachments = false, noImages = false, quiet = false;
        int maxResultChars = 4000;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session":
                    if (!TryNext(args, ref i, out var sess))
                        return new UsageErrorCommand("--session requires a value.");
                    session = sess;
                    break;
                case "--file":
                    if (!TryNext(args, ref i, out var fp))
                        return new UsageErrorCommand("--file requires a value.");
                    filePath = fp;
                    break;
                case "--source":
                    if (!TryNext(args, ref i, out var src))
                        return new UsageErrorCommand("--source requires a value.");
                    if (src is not ("backup" or "live"))
                        return new UsageErrorCommand($"--source must be 'backup' or 'live', got '{src}'.");
                    source = src;
                    break;
                case "--destination":
                    if (!TryNext(args, ref i, out var dest))
                        return new UsageErrorCommand("--destination requires a value.");
                    destination = dest;
                    break;
                case "--format":
                    if (!TryNext(args, ref i, out var fmt))
                        return new UsageErrorCommand("--format requires a value.");
                    if (fmt is not ("md" or "html"))
                        return new UsageErrorCommand($"--format must be 'md' or 'html', got '{fmt}'.");
                    format = fmt;
                    break;
                case "--out":
                    if (!TryNext(args, ref i, out var op))
                        return new UsageErrorCommand("--out requires a value.");
                    outPath = op;
                    break;
                case "--no-thinking":     noThinking = true; break;
                case "--no-tools":        noTools = true; break;
                case "--no-results":      noResults = true; break;
                case "--no-system":       noSystem = true; break;
                case "--attachments":     attachments = true; break;
                case "--no-images":       noImages = true; break;
                case "--quiet":           quiet = true; break;
                case "--max-result-chars":
                    if (!TryNext(args, ref i, out var mrc) || !int.TryParse(mrc, out var n))
                        return new UsageErrorCommand("--max-result-chars requires an integer.");
                    maxResultChars = n;
                    break;
                default:
                    return new UsageErrorCommand($"Unknown option: '{args[i]}'");
            }
        }

        if (session is null && filePath is null)
            return new UsageErrorCommand("export requires --session <id> or --file <path>.");
        if (session is not null && filePath is not null)
            return new UsageErrorCommand("export: --session and --file are mutually exclusive.");

        return new ExportCommand(session, filePath, source, destination, format, outPath,
            noThinking, noTools, noResults, noSystem, attachments, noImages, maxResultChars, quiet);
    }

    private static bool TryNext(string[] args, ref int i, out string value)
    {
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
        {
            value = args[++i];
            return true;
        }
        value = "";
        return false;
    }

    // ---------------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------------

    private static async Task<int> RunAsync(string[] args)
    {
        var cmd = Parse(args);

        switch (cmd)
        {
            case HelpCommand:
                PrintUsage();
                return ExitOk;

            case UsageErrorCommand err:
                Console.Error.WriteLine($"ERROR: {err.Message}");
                Console.Error.WriteLine("Run with --help for usage.");
                return ExitUsage;

            case BackupCommand bc:
                return await RunBackupAsync(bc).ConfigureAwait(false);

            case CatalogCommand cc:
                return await RunCatalogAsync(cc).ConfigureAwait(false);

            case ExportCommand ec:
                return await RunExportAsync(ec).ConfigureAwait(false);

            case RebuildCommand rc:
                return RunRebuild(rc);

            case InstallTaskCommand it:
                return RunInstallTask(it);

            case UninstallTaskCommand:
                return RunUninstallTask();

            case TaskStatusCommand:
                return RunTaskStatus();

            default:
                // should never happen - every CliCommand subtype is handled above
                Console.Error.WriteLine($"INTERNAL: unhandled command type {cmd.GetType().Name}");
                return ExitFailed;
        }
    }

    // ---------------------------------------------------------------------------
    // IProgress<LogLine> -> console
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns a progress handler that prints to the console, respecting the quiet flag.
    /// INFO lines go to stdout; WARN goes to stderr in yellow; ERROR goes to stderr in red.
    /// </summary>
    private static IProgress<LogLine> MakeProgress(bool quiet) =>
        new Progress<LogLine>(line =>
        {
            switch (line.Level)
            {
                case LogLevel.Info:
                    if (!quiet)
                        Console.WriteLine(line.ToString());
                    break;
                case LogLevel.Warn:
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine(line.ToString());
                    Console.ForegroundColor = prev;
                    break;
                case LogLevel.Error:
                    var prevE = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(line.ToString());
                    Console.ForegroundColor = prevE;
                    break;
            }
        });

    // ---------------------------------------------------------------------------
    // backup / verify
    // ---------------------------------------------------------------------------

    private static async Task<int> RunBackupAsync(BackupCommand cmd)
    {
        var settings = SettingsStore.Load();

        // CLI overrides win over stored settings
        var options = settings.ToBackupOptions(verify: cmd.Verify);
        if (cmd.Destination is not null) options.Destination = cmd.Destination;
        if (cmd.IncludeSubagents) options.IncludeSubagents = true;
        if (cmd.NoSnapshot) options.NoSnapshot = true;
        if (cmd.NoCatalog) options.NoCatalog = true;
        if (cmd.KeepSnapshots.HasValue) options.KeepSnapshots = cmd.KeepSnapshots.Value;

        var progress = MakeProgress(cmd.Quiet);
        var engine = new BackupEngine();

        try
        {
            var manifest = await engine.RunAsync(options, progress, CancellationToken.None)
                .ConfigureAwait(false);

            if (manifest.IsRefused) return ExitRefused;
            if (manifest.HasFailures) return ExitFailed;
            if (manifest.HasWarnings) return ExitWarnings;
            return ExitOk;
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitFailed;
        }
    }

    // ---------------------------------------------------------------------------
    // catalog
    // ---------------------------------------------------------------------------

    private static async Task<int> RunCatalogAsync(CatalogCommand cmd)
    {
        var settings = SettingsStore.Load();
        var destination = cmd.Destination ?? settings.Destination;
        var includeSubagents = cmd.IncludeSubagents || settings.IncludeSubagents;

        var catalogDir = Path.Combine(destination, "catalog");
        var liveRoot = Path.Combine(destination, "live");

        var options = new CatalogOptions
        {
            ProjectsDir = ClaudePaths.Projects,
            IndexDir = ClaudePaths.SidebarIndex,
            AgentModeDir = ClaudePaths.AgentModeSessions,
            BackupProjectsDir = Path.Combine(liveRoot, KnownStores.CodeTranscripts),
            BackupIndexDir = Path.Combine(liveRoot, KnownStores.CoworkIndex),
            OutDir = catalogDir,
            IncludeSubagents = includeSubagents,
            NoCache = cmd.NoCache,
        };

        var builder = new CatalogBuilder();
        var progress = MakeProgress(cmd.Quiet);

        try
        {
            var catalog = await builder.BuildAsync(options, progress, CancellationToken.None)
                .ConfigureAwait(false);

            if (cmd.Report)
                PrintCatalogReport(catalog);

            // Exit 1 if there are notable issues, 0 otherwise
            if (catalog.Counts.LostFromLive > 0 || catalog.Counts.DanglingIndex > 0)
                return ExitWarnings;
            return ExitOk;
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
    }

    // Port of the Python --report output format
    private static void PrintCatalogReport(SessionCatalog catalog)
    {
        // Classify entries into the five lists (matching the Python tool's labels)
        var lost = catalog.Sessions.Where(s => s.IsLost).ToList();
        var dangling = catalog.Sessions.Where(s => s.IsDangling).ToList();
        var unindexed = catalog.Sessions
            .Where(s => s.TranscriptLive && s.IndexSessionId is null && !s.IsSubagent && !s.IndexDeletedMarker)
            .ToList();
        var deletedInApp = catalog.Sessions.Where(s => s.IndexDeletedMarker).ToList();
        var notInBackup = catalog.Sessions.Where(s => s.TranscriptLive && !s.TranscriptBackup).ToList();

        foreach (var (label, list) in new[] {
            ("LOST",          lost),
            ("DANGLING",      dangling),
            ("UNINDEXED",     unindexed),
            ("DELETED-IN-APP",deletedInApp),
            ("NOT-IN-BACKUP", notInBackup),
        })
        {
            foreach (var s in list)
            {
                var lastDate = s.LastMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(s.LastMs.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                    : "                ";
                var project = (s.Cwd is not null ? Path.GetFileName(s.Cwd.TrimEnd('/', '\\')) : s.ProjectDir);
                project = (project ?? "").Length > 26 ? project![..26] : (project ?? "").PadRight(26);
                var title = (s.Title ?? "").Length > 70 ? s.Title![..70] : s.Title ?? "";
                Console.WriteLine($"  {label,-13} {lastDate}  {project}  {title}");
            }
        }
    }

    // ---------------------------------------------------------------------------
    // rebuild
    // ---------------------------------------------------------------------------

    private static int RunRebuild(RebuildCommand cmd)
    {
        // Build RebuildOptions from the CLI command
        var options = new RebuildOptions
        {
            CatalogPath    = cmd.CatalogFile,
            IndexRoot      = cmd.IndexRoot ?? ClaudePaths.SidebarIndex,
            IndexDir       = cmd.IndexDir,
            ProjectsDir    = ClaudePaths.Projects,
            DonorPath      = cmd.Donor,
            BackupIndexDir = cmd.BackupIndex,
            Commit         = cmd.Commit,
            OutDir         = cmd.OutDir,
            IncludeEmpty        = cmd.IncludeEmpty,
            IncludeSubagents    = cmd.IncludeSubagents,
            IncludeCli          = cmd.IncludeCli,
            IncludeDeleted      = cmd.IncludeDeleted,
            Force               = cmd.Force,
        };

        var rebuilder = new IndexRebuilder();
        var progress = MakeProgress(cmd.Quiet);

        RebuildPlan plan;
        try
        {
            plan = rebuilder.Plan(options, progress);
        }
        catch (InvalidOperationException ioe)
        {
            Console.Error.WriteLine($"ERROR: {ioe.Message}");
            return ExitUsage;
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }

        // Print the plan (port of the Python dry-run output)
        PrintRebuildPlan(plan, options);

        if (!cmd.Commit)
        {
            if (plan.ToWrite.Count > 0)
                Console.WriteLine("re-run with --commit to write (close Claude Desktop first, or --out-dir <staging> to inspect).");
            return ExitOk;
        }

        // --commit: check for desktop app first
        try
        {
            var desktopProcs = rebuilder.DesktopAppProcesses();
            if (desktopProcs.Count > 0 && !cmd.Force)
            {
                var paths = string.Join("; ", desktopProcs);
                Console.WriteLine($"REFUSING: Claude Desktop is running ({paths}). Quit it fully (window + tray icon) or pass --force.");
                return ExitRefused;
            }
            if (desktopProcs.Count > 0)
                Console.WriteLine("WARNING (overridden): Claude Desktop is running");

            var result = rebuilder.Apply(plan, options, progress);
            if (result.Refused)
            {
                Console.Error.WriteLine($"REFUSED: {result.RefusalReason}");
                return ExitRefused;
            }

            // Print written files (already done in plan, but confirm with "WROTE")
            foreach (var r in plan.ToWrite)
                Console.WriteLine($"  {"WROTE",-11} {r.FileName}  {r.Session.Title?[..Math.Min(r.Session.Title.Length, 70)] ?? ""}");

            return ExitOk;
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
    }

    // Port of the Python plan print (dry-run header + rows + summary)
    private static void PrintRebuildPlan(RebuildPlan plan, RebuildOptions options)
    {
        // Load the catalog to get the generated/count for the header line
        Console.WriteLine($"catalog      : {plan.CatalogPath} ({plan.CatalogSessions} sessions, generated {plan.CatalogGenerated})");

        var compact = plan.DonorCompact ? "compact" : "indented";
        Console.WriteLine($"donor        : {plan.DonorPath} ({plan.DonorKeys} keys, {compact})");

        var tooFew = plan.ReferenceRecords < 5 ? " (too few for the rare-key heuristic - static list only)" : "";
        var dropping = plan.DroppedKeys.Count > 0
            ? string.Join(", ", plan.DroppedKeys.OrderBy(k => k))
            : "nothing";
        Console.WriteLine($"key stats    : {plan.ReferenceRecords} reference records{tooFew}; dropping {dropping}");
        Console.WriteLine($"index dir    : {plan.IndexDir} ({plan.LiveRecords} live records)");
        Console.WriteLine($"deleted      : {plan.DeletedMarkers} markers (live + backup {options.BackupIndexDir ?? "(no backup copy)"})");
        Console.WriteLine($"output dir   : {plan.OutDir}");
        Console.WriteLine($"mode         : {(options.Commit ? "COMMIT (writing files)" : "DRY RUN (no files written)")}");
        Console.WriteLine();

        // "would write" rows (written in Apply; here we print dry-run preview)
        if (!options.Commit)
        {
            foreach (var r in plan.ToWrite)
            {
                var title = r.Session.Title is not null && r.Session.Title.Length > 70
                    ? r.Session.Title[..70]
                    : r.Session.Title ?? "";
                Console.WriteLine($"  {"would write",-11} {r.FileName}  {title}");
            }
        }

        Console.WriteLine();

        // Skipped summary grouped by reason (same as Python's SKIP x<N> output)
        var grouped = plan.Skipped
            .GroupBy(s => s.Reason.Split(':')[0])
            .OrderByDescending(g => g.Count());
        foreach (var g in grouped)
            Console.WriteLine($"  SKIP x{g.Count(),-4} {g.Key}");

        Console.WriteLine();
        Console.WriteLine($"summary: {plan.CatalogSessions} catalogued | {plan.ToWrite.Count} {(options.Commit ? "written" : "would be written")} | {plan.Skipped.Count} skipped | keys per record: {plan.KeysPerRecord}");
    }

    // ---------------------------------------------------------------------------
    // task commands
    // ---------------------------------------------------------------------------

    private static int RunInstallTask(InstallTaskCommand cmd)
    {
        var settings = SettingsStore.Load();
        var destination = cmd.Destination ?? settings.Destination;
        var includeSubagents = cmd.IncludeSubagents || settings.IncludeSubagents;

        // Validate time format before calling the service (must match Register's HH:mm)
        var taskTime = cmd.Time ?? settings.TaskTime;
        if (!TimeOnly.TryParseExact(taskTime, "HH:mm", out _))
        {
            Console.Error.WriteLine($"ERROR: --time '{taskTime}' is not valid HH:mm (use two-digit hour, e.g. 07:05).");
            return ExitUsage;
        }

        var schedOptions = new ScheduleOptions
        {
            StartAt          = taskTime,
            AtLogon          = !cmd.NoLogon,
            Destination      = destination,
            IncludeSubagents = includeSubagents,
        };

        var svc = new TaskSchedulerService();
        var cliPath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (cliPath is null)
        {
            Console.Error.WriteLine("ERROR: Cannot determine CLI executable path.");
            return ExitUsage;
        }

        try
        {
            svc.Register(schedOptions, cliPath);
            Console.WriteLine($"Task '{schedOptions.TaskName}' installed (daily {taskTime}{(schedOptions.AtLogon ? " + at logon" : "")}).");
            return ExitOk;
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
        catch (ArgumentException aex)
        {
            Console.Error.WriteLine($"ERROR: {aex.Message}");
            return ExitUsage;
        }
    }

    private static int RunUninstallTask()
    {
        var svc = new TaskSchedulerService();
        try
        {
            svc.Unregister(new ScheduleOptions().TaskName);
            Console.WriteLine("Task uninstalled (or was not present).");
            return ExitOk;
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
    }

    private static int RunTaskStatus()
    {
        var svc = new TaskSchedulerService();
        try
        {
            var info = svc.Query(new ScheduleOptions().TaskName);
            if (!info.Exists)
            {
                Console.WriteLine("Task is not installed.");
                return ExitOk;
            }
            Console.WriteLine($"State:    {info.State ?? "unknown"}");
            Console.WriteLine($"Next run: {info.NextRun?.ToString("yyyy-MM-dd HH:mm") ?? "-"}");
            Console.WriteLine($"Last run: {info.LastRun?.ToString("yyyy-MM-dd HH:mm") ?? "-"}  ({info.LastResult ?? "-"})");
            Console.WriteLine($"Action:   {info.Action ?? "-"}");
            return ExitOk;
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
    }

    // ---------------------------------------------------------------------------
    // export
    // ---------------------------------------------------------------------------

    // Case-insensitive to match both Python (snake_case) and any future re-casing of the catalog.
    private static readonly JsonSerializerOptions CatalogJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static async Task<int> RunExportAsync(ExportCommand cmd)
    {
        var settings = SettingsStore.Load();
        var destination = cmd.Destination ?? settings.Destination;

        // ---------- resolve transcript path ----------
        string transcriptPath;
        string sessionId;

        if (cmd.FilePath is not null)
        {
            // --file: direct path; session id is the file stem
            transcriptPath = cmd.FilePath;
            sessionId = Path.GetFileNameWithoutExtension(transcriptPath);
        }
        else
        {
            // --session: look up in <destination>\catalog\sessions_catalog.json
            var catalogPath = Path.Combine(destination, "catalog", "sessions_catalog.json");
            if (!File.Exists(catalogPath))
            {
                Console.Error.WriteLine(
                    $"ERROR: catalog not found at '{catalogPath}'." +
                    $" Run 'backup' or 'catalog' first to build it.");
                return ExitUsage;
            }

            SessionCatalog catalog;
            try
            {
                var json = await File.ReadAllTextAsync(catalogPath).ConfigureAwait(false);
                catalog = JsonSerializer.Deserialize<SessionCatalog>(json, CatalogJson)
                    ?? new SessionCatalog();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: failed to read catalog: {ex.Message}");
                return ExitUsage;
            }

            // Match by exact id first, then by prefix (case-insensitive)
            var prefix = cmd.Session!;
            var matches = catalog.Sessions
                .Where(s => s.TranscriptRel is not null &&
                            s.CliSessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                Console.Error.WriteLine(
                    $"ERROR: no session with id or prefix '{prefix}' found in the catalog.");
                return ExitUsage;
            }

            if (matches.Count > 1)
            {
                // Prefer an exact match if one exists; otherwise report ambiguity
                var exact = matches.Where(
                    s => s.CliSessionId.Equals(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 1)
                {
                    matches = exact;
                }
                else
                {
                    Console.Error.WriteLine(
                        $"ERROR: ambiguous session prefix '{prefix}' matches {matches.Count} sessions:");
                    foreach (var m in matches.Take(10))
                        Console.Error.WriteLine($"  {m.CliSessionId}  {m.Title}");
                    if (matches.Count > 10)
                        Console.Error.WriteLine($"  ... and {matches.Count - 10} more");
                    return ExitUsage;
                }
            }

            var entry = matches[0];
            sessionId = entry.CliSessionId;
            // transcript_rel uses forward slashes; convert to OS separator
            var rel = entry.TranscriptRel!.Replace('/', Path.DirectorySeparatorChar);

            var backupPath = Path.Combine(destination, "live", KnownStores.CodeTranscripts, rel);
            var livePath   = Path.Combine(ClaudePaths.Projects, rel);

            if (cmd.Source == "backup")
            {
                // Prefer backup; fall back to live when backup copy is absent
                transcriptPath = File.Exists(backupPath) ? backupPath : livePath;
                if (!File.Exists(transcriptPath))
                {
                    Console.Error.WriteLine(
                        $"ERROR: transcript not found." +
                        $"\n  backup: {backupPath}" +
                        $"\n  live:   {livePath}");
                    return ExitUsage;
                }
            }
            else
            {
                // --source live: prefer live; fall back to backup
                transcriptPath = File.Exists(livePath) ? livePath : backupPath;
                if (!File.Exists(transcriptPath))
                {
                    Console.Error.WriteLine(
                        $"ERROR: transcript not found." +
                        $"\n  live:   {livePath}" +
                        $"\n  backup: {backupPath}");
                    return ExitUsage;
                }
            }
        }

        // ---------- output path ----------
        var outPath = cmd.Out;
        try
        {
            if (outPath is null)
            {
                var exportsDir = Path.Combine(destination, "exports");
                Directory.CreateDirectory(exportsDir);
                outPath = Path.Combine(exportsDir, $"{sessionId}.{cmd.Format}");
            }
            else
            {
                var outDir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ERROR: cannot create output directory: {ex.Message}");
            return ExitFailed;
        }

        // ---------- read transcript ----------
        var readOptions = new TranscriptReadOptions
        {
            IncludeSystem      = !cmd.NoSystem,
            IncludeAttachments = cmd.Attachments,
            DecodeImages       = !cmd.NoImages,
        };

        var exportOptions = new ExportOptions
        {
            IncludeThinking    = !cmd.NoThinking,
            IncludeToolCalls   = !cmd.NoTools,
            IncludeToolResults = !cmd.NoResults,
            IncludeSystem      = !cmd.NoSystem,
            IncludeAttachments = cmd.Attachments,
            EmbedImages        = !cmd.NoImages,
            MaxToolResultChars = cmd.MaxResultChars,
        };

        if (!cmd.Quiet)
            Console.WriteLine($"Reading {transcriptPath} ...");

        var reader   = new TranscriptReader();
        var exporter = new TranscriptExporter();

        Transcript transcript;
        try
        {
            transcript = await reader.ReadAsync(
                transcriptPath, readOptions, recordsProgress: null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"ERROR reading transcript: {ex.Message}");
            return ExitFailed;
        }

        // ---------- export ----------
        try
        {
            if (cmd.Format == "html")
                await exporter.ExportHtmlAsync(transcript, outPath, exportOptions, CancellationToken.None)
                    .ConfigureAwait(false);
            else
                await exporter.ExportMarkdownAsync(transcript, outPath, exportOptions, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (NotImplementedException nie)
        {
            Console.Error.WriteLine($"NOT IMPLEMENTED: {nie.Message}");
            return ExitFailed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"ERROR writing export: {ex.Message}");
            return ExitFailed;
        }

        if (!cmd.Quiet)
            Console.WriteLine(
                $"Exported to {outPath}  ({transcript.Turns.Count} turns, {transcript.Records} records)");

        return ExitOk;
    }

    // ---------------------------------------------------------------------------
    // Help text
    // ---------------------------------------------------------------------------

    internal static void PrintUsage()
    {
        Console.WriteLine("""
            ClaudeSessionBackup.Cli <command> [options]

            COMMANDS

              backup        Incremental backup of all Claude session stores.
                --destination D        Backup root (default: from settings.json)
                --include-subagents    Also copy subagent transcripts (~1.2 GB)
                --no-snapshot          Skip the dated zip snapshot
                --no-catalog           Skip catalog rebuild after backup
                --keep-snapshots N     Move old zips to _to_delete after N (default 60)
                --quiet                Suppress INFO lines; WARN/ERROR still go to stderr

              export        Export one transcript as Markdown or HTML.
                --session <id>         Session id or unique prefix (looks up in catalog)
                --file <path.jsonl>    Read a transcript file directly (bypasses catalog)
                --source backup|live   Which copy to read (default: backup, fallback to live)
                --destination D        Backup root for catalog lookup and default output
                --format md|html       Output format (default: md)
                --out F                Output file (default: <destination>\exports\<id>.<fmt>)
                --no-thinking          Omit thinking blocks
                --no-tools             Omit tool-call blocks
                --no-results           Omit tool-result blocks
                --no-system            Omit system / compact-boundary blocks
                --attachments          Include attachment records (off by default)
                --no-images            Omit images (placeholder text instead)
                --max-result-chars N   Truncate tool results at N chars (default 4000; 0=never)
                --quiet                Suppress progress lines

              verify        Compare live stores with backup; report shrink / lost / dangling.
                --destination D
                --include-subagents
                --quiet

              catalog       Build the session catalog (transcript <-> sidebar join).
                --destination D        Locates <destination>\live and \catalog
                --include-subagents
                --no-cache             Re-scan every transcript; ignore catalog_cache.json
                --report               Print LOST / DANGLING / UNINDEXED / DELETED-IN-APP / NOT-IN-BACKUP lists
                --quiet                Suppress INFO lines; WARN/ERROR still go to stderr

              rebuild       Regenerate missing Cowork sidebar records from a session catalog.
                --catalog F            (REQUIRED) Path to sessions_catalog.json
                --commit               Write files (default: dry run)
                --out-dir D            Write to a staging folder instead of the live index
                --index-root R         Live claude-code-sessions root (default: %APPDATA%\Claude\claude-code-sessions)
                --index-dir D          Explicit <root>\<account>\<org> folder; skips auto-detection
                --donor F              Explicit donor local_*.json
                --backup-index D       Backup copy of claude-code-sessions
                --include-empty        Also emit records for 0-byte transcripts
                --include-subagents    Also emit records for subagent transcripts
                --include-cli          Also emit records for terminal CLI sessions
                --include-deleted      Resurrect sessions the app marked deleted
                --force                Write even if Claude Desktop is running (dangerous)
                --quiet                Suppress INFO lines; WARN/ERROR still go to stderr

              install-task  Register a Windows scheduled task (daily + at logon).
                --time HH:mm           Daily trigger time (default: 21:00)
                --no-logon             Skip the at-logon trigger
                --destination D
                --include-subagents

              uninstall-task   Remove the scheduled task.

              task-status      Show the scheduled task's state, next/last run, and action.

            EXIT CODES
              0  success
              1  success with warnings
              2  failure (copy errors, catalog errors)
              3  usage / config error
              4  refused (lock held, or desktop app running for rebuild --commit)
            """);
    }
}
