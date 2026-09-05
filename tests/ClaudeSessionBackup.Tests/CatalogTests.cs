using System.Text.Json;
using ClaudeSessionBackup.Core.Catalog;
using ClaudeSessionBackup.Core.Model;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// Contract tests for CatalogBuilder. All assertions are derived from the <remarks> contract in
/// CatalogBuilder.cs and the reference implementation build_catalog.py.
/// They will throw NotImplementedException against the skeleton stub; the integrator runs them
/// against the real implementation.
/// </summary>
public class CatalogTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _projects;   // live ~\.claude\projects substitute
    private readonly string _index;      // live sidebar index substitute
    private readonly string _outDir;     // catalog output folder

    public CatalogTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "csb_cat_" + Guid.NewGuid().ToString("N"));
        _projects = Path.Combine(_tmp, "projects");
        _index = Path.Combine(_tmp, "index");
        _outDir = Path.Combine(_tmp, "catalog");
        Directory.CreateDirectory(_projects);
        Directory.CreateDirectory(_index);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch { /* best-effort */ }
    }

    // --- helpers ---

    private static readonly string SessionUuid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string ProjectSlug = "my-project";

    private string CreateTranscriptFile(string slug, string sessionId, params string[] jsonLines)
    {
        var dir = Path.Combine(_projects, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, jsonLines);
        return path;
    }

    private static string UserRecord(string text) =>
        "{\"type\":\"user\",\"timestamp\":\"2026-01-01T10:00:00.000Z\"," +
        $"\"message\":{{\"content\":\"{EscapeJson(text)}\"}}}}";

    private static string ToolResultRecord() =>
        "{\"type\":\"user\",\"timestamp\":\"2026-01-01T10:00:05.000Z\"," +
        "\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"x\",\"content\":[]}]}}";

    /// <summary>
    /// Builds an assistant JSONL record. When <paramref name="blockIndex"/> is null the
    /// <c>apiBlockIndex</c> field is omitted entirely, which is how real Claude transcripts
    /// mark the start of a new assistant message (absent == new message; present &gt;0 == continuation).
    /// </summary>
    private static string AssistantRecord(int? blockIndex = null) =>
        $"{{\"type\":\"assistant\",\"timestamp\":\"2026-01-01T10:01:00.000Z\"," +
        (blockIndex.HasValue ? $"\"apiBlockIndex\":{blockIndex.Value}," : "") +
        "\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}";  // 2 closes: message + outer

    private static string CustomTitleRecord(string title) =>
        $"{{\"type\":\"custom-title\",\"timestamp\":\"2026-01-01T10:02:00.000Z\"," +
        $"\"customTitle\":\"{EscapeJson(title)}\"}}";

    private static string AiTitleRecord(string title) =>
        $"{{\"type\":\"ai-title\",\"timestamp\":\"2026-01-01T10:02:00.000Z\"," +
        $"\"aiTitle\":\"{EscapeJson(title)}\"}}";

    private static string SummaryRecord(string summary) =>
        $"{{\"type\":\"summary\",\"timestamp\":\"2026-01-01T10:02:00.000Z\"," +
        $"\"summary\":\"{EscapeJson(summary)}\"}}";

    private static string SystemRecord(string cwd) =>
        $"{{\"type\":\"system\",\"timestamp\":\"2026-01-01T09:59:00.000Z\"," +
        $"\"cwd\":\"{EscapeJson(cwd)}\",\"entrypoint\":\"claude-desktop\"}}";

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private CatalogOptions MakeOptions(string? backupProjects = null, string? backupIndex = null) =>
        new()
        {
            ProjectsDir = _projects,
            IndexDir = _index,
            AgentModeDir = null,
            BackupProjectsDir = backupProjects,
            BackupIndexDir = backupIndex,
            OutDir = _outDir,
            IncludeSubagents = false,
            NoCache = true,   // always fresh scan in tests
        };

    // -------------------------------------------------------------------------
    // Output files
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_BuildAsync_WritesSessionsCatalogJson()
    {
        // BuildAsync must write sessions_catalog.json to OutDir.
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("hello"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_outDir, "sessions_catalog.json")),
            "sessions_catalog.json must be written to OutDir.");
        _ = cat; // returned value also holds the catalog
    }

    [Fact]
    public async Task Catalog_BuildAsync_WritesMarkdownTable()
    {
        // BuildAsync must write sessions_catalog.md.
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("hello"));

        await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var md = Path.Combine(_outDir, "sessions_catalog.md");
        Assert.True(File.Exists(md), "sessions_catalog.md must be written.");
        var content = File.ReadAllText(md);
        // Must contain a markdown table header.
        Assert.Contains("| #", content);
        Assert.Contains("Last activity", content);
        Assert.Contains("Title", content);
    }

    [Fact]
    public async Task Catalog_BuildAsync_WritesPreviousJsonWhenRebuilt()
    {
        // On the second build, the previous catalog is kept as sessions_catalog.previous.json.
        // Contract: "sessions_catalog.previous.json = the prior json if one existed"
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("hello"));

        var opts = MakeOptions();
        var builder = new CatalogBuilder();
        await builder.BuildAsync(opts, null, CancellationToken.None);
        await builder.BuildAsync(opts, null, CancellationToken.None);  // second run

        Assert.True(File.Exists(Path.Combine(_outDir, "sessions_catalog.previous.json")),
            "sessions_catalog.previous.json must be written on the second build.");
    }

    // -------------------------------------------------------------------------
    // Transcript scanning: title precedence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_TitlePrecedence_CustomTitleWins()
    {
        // custom-title beats everything else.
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            AiTitleRecord("AI Title"),
            CustomTitleRecord("My Custom Title"),
            SummaryRecord("Summary text"),
            UserRecord("First prompt"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal("My Custom Title", entry.Title);
        Assert.Equal("custom-title", entry.TitleSource);
    }

    [Fact]
    public async Task Catalog_TitlePrecedence_AiTitleBeforesSummary()
    {
        // ai-title beats summary and first-prompt when no custom-title exists.
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            AiTitleRecord("AI Title"),
            SummaryRecord("Summary text"),
            UserRecord("First prompt"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal("AI Title", entry.Title);
        Assert.Equal("ai-title", entry.TitleSource);
    }

    [Fact]
    public async Task Catalog_TitlePrecedence_SummaryBeforeFirstPrompt()
    {
        // summary beats first-prompt when no custom-title or ai-title exists.
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            SummaryRecord("The Summary"),
            UserRecord("First prompt text"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal("The Summary", entry.Title);
        Assert.Equal("summary", entry.TitleSource);
    }

    [Fact]
    public async Task Catalog_TitlePrecedence_FirstPromptWhenNothingElse()
    {
        // first-prompt is used when no named title exists.
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            UserRecord("First prompt text goes here"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal("first-prompt", entry.TitleSource);
        Assert.Contains("First prompt text", entry.Title);
    }

    [Fact]
    public async Task Catalog_TitlePrecedence_LastCustomTitleWins()
    {
        // When multiple custom-title records exist, the LAST one wins.
        // Contract: "custom-title/aiTitle/summary records: last one wins."
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            CustomTitleRecord("First Title"),
            CustomTitleRecord("Second Title"));  // last wins

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal("Second Title", entry.Title);
    }

    [Fact]
    public async Task Catalog_TitlePrecedence_UntitledWhenNoTitle()
    {
        // A zero-byte transcript or one with no title hints produces "(untitled)".
        var dir = Path.Combine(_projects, ProjectSlug);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, SessionUuid + ".jsonl"), Array.Empty<byte>());

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal("(untitled)", entry.Title);
        Assert.Equal("none", entry.TitleSource);
    }

    // -------------------------------------------------------------------------
    // Prompt counting
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_PromptCount_UserTextIsPrompt()
    {
        // A user record with text content counts as a prompt.
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            UserRecord("Prompt one"),
            UserRecord("Prompt two"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal(2, entry.UserPrompts);
    }

    [Fact]
    public async Task Catalog_PromptCount_ToolResultNotCountedAsPrompt()
    {
        // A user record that carries only tool_result content is NOT a prompt.
        // Contract: "tool_result-only records are not prompts."
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            UserRecord("Real prompt"),
            ToolResultRecord());   // tool_result only - not a prompt

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal(1, entry.UserPrompts);
    }

    [Fact]
    public async Task Catalog_AssistantCount_OnlyApiBlockIndexZeroOrAbsent()
    {
        // Assistant messages are counted only when apiBlockIndex is 0 or absent.
        // Contract: "apiBlockIndex 0 (or absent) marks a new assistant message"
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            UserRecord("prompt"),
            AssistantRecord(),                // absent apiBlockIndex = new message, counts
            AssistantRecord(blockIndex: 1),   // continuation - does NOT count
            AssistantRecord(blockIndex: 2));  // continuation - does NOT count

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.Equal(1, entry.AssistantMsgs);
    }

    // -------------------------------------------------------------------------
    // first_prompt: command-name tag extraction
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_FirstPrompt_CommandNameTagExtracted()
    {
        // A prompt starting with <command-name> is converted to "<name> <args>".
        // Contract: "a prompt that starts with <command-name> becomes '<name> <command-args>'"
        var cmdLine = "<command-name>run-tests</command-name><command-args>--all --verbose</command-args>";
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            UserRecord(cmdLine));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.NotNull(entry.FirstPrompt);
        Assert.Equal("run-tests --all --verbose", entry.FirstPrompt!.Trim());
    }

    [Fact]
    public async Task Catalog_FirstPrompt_SystemReminderIgnored()
    {
        // Prompts starting with <system-reminder are skipped.
        // Contract: "prompts starting with <local-command or <system-reminder are ignored."
        var systemLine = "<system-reminder>Some system reminder text</system-reminder>";
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            UserRecord(systemLine),
            UserRecord("Real first prompt"));  // second record is the actual first_prompt

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        // The system-reminder must not be the first_prompt.
        Assert.NotNull(entry.FirstPrompt);
        Assert.DoesNotContain("system-reminder", entry.FirstPrompt!);
        Assert.Contains("Real first prompt", entry.FirstPrompt!);
    }

    // -------------------------------------------------------------------------
    // LOST, DANGLING, and deleted markers
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_Lost_TranscriptInBackupNotLive()
    {
        // A transcript that exists in the backup but NOT in live is flagged LOST.
        // Contract: "LOST = backup only"
        var backupProjects = Path.Combine(_tmp, "backup_projects");
        var dir = Path.Combine(backupProjects, ProjectSlug);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, SessionUuid + ".jsonl"),
            new[] { SystemRecord("C:\\proj"), UserRecord("hello from backup") });

        // Do NOT create the file in _projects (it is lost from live).
        var cat = await new CatalogBuilder().BuildAsync(
            MakeOptions(backupProjects: backupProjects), null, CancellationToken.None);

        var entry = cat.Sessions.SingleOrDefault(s => s.CliSessionId == SessionUuid);
        Assert.NotNull(entry);
        Assert.True(entry!.IsLost,
            "A transcript present only in the backup must be marked LOST (IsLost = true).");
    }

    [Fact]
    public async Task Catalog_Dangling_SidebarRecordWithoutTranscript()
    {
        // A sidebar record whose cliSessionId appears in no transcript is DANGLING.
        // Contract: "Sidebar records whose transcript exists nowhere become DANGLING rows."
        var indexDir = Path.Combine(_index, "account", "org");
        Directory.CreateDirectory(indexDir);
        var danglingSessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var record = new
        {
            sessionId = "local_" + Guid.NewGuid().ToString("N"),
            cliSessionId = danglingSessionId,
            title = "Dangling Record",
            cwd = "C:\\proj",
            createdAt = 1700000000000L,
            lastActivityAt = 1700000001000L,
        };
        File.WriteAllText(
            Path.Combine(indexDir, "local_dangling.json"),
            JsonSerializer.Serialize(record));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.SingleOrDefault(s => s.CliSessionId == danglingSessionId);
        Assert.NotNull(entry);
        Assert.True(entry!.IsDangling,
            "A sidebar record with no matching transcript must be marked DANGLING.");
        Assert.Equal("Dangling Record", entry.Title);
    }

    [Fact]
    public async Task Catalog_DeletedMarker_FileRecognised()
    {
        // A file named deleted_<cliSessionId> is a deleted marker.
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("x"));

        // Create a deleted_* marker file in the index.
        var indexDir = Path.Combine(_index, "account", "org");
        Directory.CreateDirectory(indexDir);
        File.WriteAllText(Path.Combine(indexDir, "deleted_" + SessionUuid), "");  // file marker

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.SingleOrDefault(s => s.CliSessionId == SessionUuid);
        Assert.NotNull(entry);
        Assert.True(entry!.IndexDeletedMarker,
            "A file named deleted_<cliSessionId> must set IndexDeletedMarker.");
        Assert.Contains(SessionUuid, cat.DeletedMarkers);
    }

    [Fact]
    public async Task Catalog_DeletedMarker_FolderRecognised()
    {
        // A FOLDER named deleted_<cliSessionId> is also a deleted marker.
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("x"));

        var indexDir = Path.Combine(_index, "account", "org");
        Directory.CreateDirectory(indexDir);
        Directory.CreateDirectory(Path.Combine(indexDir, "deleted_" + SessionUuid));  // folder marker

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.SingleOrDefault(s => s.CliSessionId == SessionUuid);
        Assert.NotNull(entry);
        Assert.True(entry!.IndexDeletedMarker,
            "A folder named deleted_<cliSessionId> must also set IndexDeletedMarker.");
    }

    [Fact]
    public async Task Catalog_DeletedMarkers_UnionedWithBackupIndex()
    {
        // Deleted markers from the backup index are merged with live markers.
        // Contract: "unioned with those under BackupIndexDir"
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("x"));

        // Create deleted marker only in the backup index (not in the live index).
        var backupIndex = Path.Combine(_tmp, "backup_index");
        var backupIndexDir = Path.Combine(backupIndex, "account", "org");
        Directory.CreateDirectory(backupIndexDir);
        File.WriteAllText(Path.Combine(backupIndexDir, "deleted_" + SessionUuid), "");

        var cat = await new CatalogBuilder().BuildAsync(
            MakeOptions(backupIndex: backupIndex), null, CancellationToken.None);

        var entry = cat.Sessions.SingleOrDefault(s => s.CliSessionId == SessionUuid);
        Assert.NotNull(entry);
        Assert.True(entry!.IndexDeletedMarker,
            "Deleted markers from the backup index must be merged into the catalog.");
    }

    [Fact]
    public async Task Catalog_FlaggedRecord_LosesToUnflaggedRecord()
    {
        // A sidebar record inside a *_backup_* folder is flagged and loses to an unflagged
        // record for the same sessionId.
        // Contract: "those under a *_backup_* or deleted_* path are Flagged and lose to an unflagged record"
        var liveIndexDir = Path.Combine(_index, "account", "org");
        var backupIndexDir = Path.Combine(_index, "account_backup_2026", "org");
        Directory.CreateDirectory(liveIndexDir);
        Directory.CreateDirectory(backupIndexDir);

        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("x"));
        var sessionLocalId = "local_" + Guid.NewGuid().ToString("N");

        // Unflagged (live) record with the real title.
        var liveRecord = new
        {
            sessionId = sessionLocalId,
            cliSessionId = SessionUuid,
            title = "Live Title",
            cwd = "C:\\proj",
            createdAt = 1700000000000L,
            lastActivityAt = 1700000002000L,
        };
        File.WriteAllText(Path.Combine(liveIndexDir, "local_live.json"),
            JsonSerializer.Serialize(liveRecord));

        // Flagged (backup) record with a different title.
        var backupRecord = new
        {
            sessionId = sessionLocalId,
            cliSessionId = SessionUuid,
            title = "Backup Title",
            cwd = "C:\\proj",
            createdAt = 1700000000000L,
            lastActivityAt = 1700000001000L,
        };
        File.WriteAllText(Path.Combine(backupIndexDir, "local_backup.json"),
            JsonSerializer.Serialize(backupRecord));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.SingleOrDefault(s => s.CliSessionId == SessionUuid);
        Assert.NotNull(entry);
        // The flagged record must NOT override the live record for the sidebar title.
        Assert.NotEqual("Backup Title", entry!.IndexFile);
        // The live record should win (IndexFlagged should be false for the winning record).
        Assert.Equal(false, entry.IndexFlagged);
    }

    // -------------------------------------------------------------------------
    // Cache
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_Cache_ReusedWhenSizeAndMtimeMatch()
    {
        // When size and mtime match the cache, the transcript is not rescanned.
        // Contract: "an entry is reused only when size AND mtime match AND it carries every current field"
        var path = CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"), UserRecord("hello"));

        var optsWithCache = new CatalogOptions
        {
            ProjectsDir = _projects,
            IndexDir = _index,
            AgentModeDir = null,
            OutDir = _outDir,
            NoCache = false,  // allow cache
        };

        var builder = new CatalogBuilder();
        await builder.BuildAsync(optsWithCache, null, CancellationToken.None);

        // Verify cache file was written.
        Assert.True(File.Exists(Path.Combine(_outDir, "catalog_cache.json")));

        // Second build with same options: rescanned count should be 0.
        var cat2 = await builder.BuildAsync(optsWithCache, null, CancellationToken.None);
        Assert.Equal(0, cat2.Counts.Rescanned);
    }

    [Fact]
    public async Task Catalog_Cache_InvalidatedOnSizeChange()
    {
        // When the file size changes, the cache entry is invalidated.
        var path = CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"), UserRecord("hello"));

        var optsWithCache = new CatalogOptions
        {
            ProjectsDir = _projects,
            IndexDir = _index,
            AgentModeDir = null,
            OutDir = _outDir,
            NoCache = false,
        };

        var builder = new CatalogBuilder();
        await builder.BuildAsync(optsWithCache, null, CancellationToken.None);

        // Modify the file (changes size AND mtime).
        File.AppendAllText(path, "\n" + UserRecord("new line"));

        var cat2 = await builder.BuildAsync(optsWithCache, null, CancellationToken.None);
        Assert.True(cat2.Counts.Rescanned > 0,
            "A size-changed transcript must be rescanned, not served from cache.");
    }

    // -------------------------------------------------------------------------
    // Counts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_Counts_SessionsEqualsDistinctCliSessionIds()
    {
        // Count.sessions must equal the number of distinct cliSessionIds.
        var sid2 = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("x"));
        CreateTranscriptFile(ProjectSlug, sid2, SystemRecord("C:\\proj"), UserRecord("y"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        Assert.Equal(2, cat.Counts.Sessions);
        Assert.Equal(2, cat.Sessions.Count);
    }

    [Fact]
    public async Task Catalog_Counts_LiveTranscriptsCountedCorrectly()
    {
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("x"));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        Assert.Equal(1, cat.Counts.TranscriptsLive);
    }

    // -------------------------------------------------------------------------
    // LoadAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_LoadAsync_ReadsExistingJson()
    {
        // LoadAsync must deserialise a sessions_catalog.json written by BuildAsync.
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("hello"));

        var builder = new CatalogBuilder();
        await builder.BuildAsync(MakeOptions(), null, CancellationToken.None);

        var loaded = await builder.LoadAsync(Path.Combine(_outDir, "sessions_catalog.json"), CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.True(loaded.Sessions.Count > 0);
        Assert.Equal(SessionUuid, loaded.Sessions.First().CliSessionId);
    }

    // -------------------------------------------------------------------------
    // Subagent exclusion
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_SubagentTranscript_ExcludedByDefault()
    {
        // Subagent transcripts are excluded when IncludeSubagents = false.
        // Contract: "With IncludeSubagents also <projects>\<slug>\**\subagents\*.jsonl"
        var slug = ProjectSlug;
        var subagentDir = Path.Combine(_projects, slug, "subagents");
        Directory.CreateDirectory(subagentDir);
        var subSid = "cccccccc-dddd-eeee-ffff-000000000001";
        File.WriteAllLines(Path.Combine(subagentDir, subSid + ".jsonl"),
            new[] { SystemRecord("C:\\proj"), UserRecord("subagent work") });

        // Also create the parent transcript so _slug_ has files.
        CreateTranscriptFile(slug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("main"));

        var cat = await new CatalogBuilder().BuildAsync(
            new CatalogOptions
            {
                ProjectsDir = _projects,
                IndexDir = _index,
                AgentModeDir = null,
                OutDir = _outDir,
                NoCache = true,
                IncludeSubagents = false,  // explicit default
            },
            null, CancellationToken.None);

        // Subagent session must not appear.
        var subEntry = cat.Sessions.FirstOrDefault(s => s.CliSessionId == subSid);
        Assert.Null(subEntry);
    }

    [Fact]
    public async Task Catalog_SubagentTranscript_IncludedWhenFlagSet()
    {
        // Subagent transcripts appear when IncludeSubagents = true.
        var slug = ProjectSlug;
        var subagentDir = Path.Combine(_projects, slug, "subagents");
        Directory.CreateDirectory(subagentDir);
        var subSid = "cccccccc-dddd-eeee-ffff-000000000002";
        File.WriteAllLines(Path.Combine(subagentDir, subSid + ".jsonl"),
            new[] { SystemRecord("C:\\proj"), UserRecord("subagent work") });

        CreateTranscriptFile(slug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("main"));

        var cat = await new CatalogBuilder().BuildAsync(
            new CatalogOptions
            {
                ProjectsDir = _projects,
                IndexDir = _index,
                AgentModeDir = null,
                OutDir = _outDir,
                NoCache = true,
                IncludeSubagents = true,
            },
            null, CancellationToken.None);

        var subEntry = cat.Sessions.FirstOrDefault(s => s.CliSessionId == subSid);
        Assert.NotNull(subEntry);
        Assert.True(subEntry!.IsSubagent);
    }

    // -------------------------------------------------------------------------
    // Contract gap 4: TranscriptBackup / LiveNotInBackup
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_TranscriptBackup_FalseAndLiveNotInBackupCounted()
    {
        // Contract: "a transcript present live but absent from BackupProjectsDir yields
        // transcript_backup=false and counts.live_not_in_backup=1"
        var backupProjects = Path.Combine(_tmp, "backup_projects");
        Directory.CreateDirectory(backupProjects);

        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("hello"));
        // Do NOT place the transcript in backupProjects.

        var cat = await new CatalogBuilder().BuildAsync(
            MakeOptions(backupProjects: backupProjects), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.True(entry.TranscriptLive);
        Assert.False(entry.TranscriptBackup,
            "A transcript only in live must have transcript_backup=false.");
        Assert.True(cat.Counts.LiveNotInBackup == 1,
            "counts.live_not_in_backup must be 1 when a live transcript has no backup copy.");
    }

    [Fact]
    public async Task Catalog_TranscriptBackup_TrueWhenPresentInBoth()
    {
        // Contract: "one present in both yields true"
        var backupProjects = Path.Combine(_tmp, "backup_projects");
        Directory.CreateDirectory(backupProjects);

        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("hello"));

        // Mirror the transcript into the backup projects directory.
        var backupSlugDir = Path.Combine(backupProjects, ProjectSlug);
        Directory.CreateDirectory(backupSlugDir);
        File.Copy(
            Path.Combine(_projects, ProjectSlug, SessionUuid + ".jsonl"),
            Path.Combine(backupSlugDir, SessionUuid + ".jsonl"));

        var cat = await new CatalogBuilder().BuildAsync(
            MakeOptions(backupProjects: backupProjects), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.True(entry.TranscriptLive);
        Assert.True(entry.TranscriptBackup,
            "A transcript in both live and backup must have transcript_backup=true.");
        Assert.True(cat.Counts.LiveNotInBackup == 0,
            "counts.live_not_in_backup must be 0 when the transcript has a backup copy.");
    }

    // -------------------------------------------------------------------------
    // Contract gap 5: sidebar-title step of title precedence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_TitlePrecedence_SidebarTitleWhenNoCustomOrAiTitle()
    {
        // Contract: "Title precedence: custom-title, ai-title, sidebar title, summary, ..."
        // A transcript with no custom-title and no ai-title, joined with a sidebar record
        // that carries a title, must yield title_source "sidebar" and that title.
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            UserRecord("the first prompt"));

        // Write a sidebar record with a title for this session.
        var sidebarTitle = "Sidebar Session Title";
        var rec = new
        {
            sessionId = "local_sidebartest_0000000000000",
            cliSessionId = SessionUuid,
            cwd = "C:\\proj",
            title = sidebarTitle,
            createdAt = 1700000000000L,
            lastActivityAt = 1700000001000L,
            isArchived = false,
        };
        File.WriteAllText(
            Path.Combine(_index, "local_sidebartest.json"),
            System.Text.Json.JsonSerializer.Serialize(rec));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.True(entry.TitleSource == "sidebar",
            "title_source must be 'sidebar' when no custom-title or ai-title but sidebar has a title.");
        Assert.Equal(sidebarTitle, entry.Title);
    }

    [Fact]
    public async Task Catalog_TitlePrecedence_SummaryWhenNoSidebarTitle()
    {
        // A transcript with a summary record but no custom-title, ai-title, or sidebar title
        // must yield title_source "summary" and the summary text.
        var summaryText = "Session summary text";
        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            SummaryRecord(summaryText));
        // No sidebar record → no sidebar title.

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.True(entry.TitleSource == "summary",
            "title_source must be 'summary' when no custom/ai/sidebar title but a summary record exists.");
        Assert.Equal(summaryText, entry.Title);
    }

    // -------------------------------------------------------------------------
    // Contract gap 6: AgentModeDir scanning
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_AgentModeDir_ListsEntriesExceptRpm()
    {
        // Contract: "every entry under <AgentModeDir>\<account>\<org>\ except 'rpm',
        // with recursive sizes and counts.agent_mode_entries."
        var agentModeDir = Path.Combine(_tmp, "agent_mode");
        var acct = "account-uuid-abcd";
        var org  = "org-uuid-1234";
        var orgDir = Path.Combine(agentModeDir, acct, org);
        Directory.CreateDirectory(orgDir);

        // Two entries that must appear.
        File.WriteAllText(Path.Combine(orgDir, "agent-alpha"), "data");
        var betaDir = Path.Combine(orgDir, "agent-beta");
        Directory.CreateDirectory(betaDir);
        File.WriteAllText(Path.Combine(betaDir, "inner.txt"), "inner content");

        // "rpm" must be excluded.
        Directory.CreateDirectory(Path.Combine(orgDir, "rpm"));

        // Need at least one transcript for BuildAsync to succeed.
        CreateTranscriptFile(ProjectSlug, SessionUuid, SystemRecord("C:\\proj"), UserRecord("hi"));

        var opts = new CatalogOptions
        {
            ProjectsDir = _projects,
            IndexDir = _index,
            AgentModeDir = agentModeDir,
            OutDir = _outDir,
            NoCache = true,
        };
        var cat = await new CatalogBuilder().BuildAsync(opts, null, CancellationToken.None);

        Assert.DoesNotContain(cat.AgentMode, e => e.Name == "rpm");
        Assert.True(!cat.AgentMode.Any(e => e.Name == "rpm"),
            "'rpm' must be excluded from the agent-mode listing.");
        Assert.Contains(cat.AgentMode, e => e.Name == "agent-alpha" && !e.IsDir);
        Assert.Contains(cat.AgentMode, e => e.Name == "agent-beta" && e.IsDir);

        // counts.agent_mode_entries must reflect the listed count.
        Assert.Equal(cat.AgentMode.Count, cat.Counts.AgentModeEntries);
        Assert.True(cat.Counts.AgentModeEntries == 2,
            "counts.agent_mode_entries must equal the number of non-rpm entries.");
    }

    // -------------------------------------------------------------------------
    // Contract gap 7: isMeta:true user records are not prompts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Catalog_IsMetaRecord_NotCountedAsUserPromptOrFirstPrompt()
    {
        // Contract: "'user' records: a human prompt if isMeta is not true"
        // An isMeta:true user record must be excluded from user_prompts and skipped
        // as a candidate for first_prompt.
        var metaRecord =
            "{\"type\":\"user\",\"timestamp\":\"2026-01-01T10:00:00.000Z\"," +
            "\"isMeta\":true," +
            "\"message\":{\"content\":\"meta content that must not appear as first_prompt\"}}";
        var realPromptText = "the real first prompt";

        CreateTranscriptFile(ProjectSlug, SessionUuid,
            SystemRecord("C:\\proj"),
            metaRecord,
            UserRecord(realPromptText));

        var cat = await new CatalogBuilder().BuildAsync(MakeOptions(), null, CancellationToken.None);

        var entry = cat.Sessions.Single(s => s.CliSessionId == SessionUuid);
        Assert.True(entry.UserPrompts == 1,
            "isMeta:true user records must not be counted in user_prompts.");
        Assert.NotNull(entry.FirstPrompt);
        Assert.True(entry.FirstPrompt!.Contains(realPromptText),
            "first_prompt must skip isMeta:true records and use the first real prompt.");
    }
}
