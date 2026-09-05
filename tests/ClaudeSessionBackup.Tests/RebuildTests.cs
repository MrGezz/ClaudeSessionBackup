using System.Text.Json;
using ClaudeSessionBackup.Core.Model;
using ClaudeSessionBackup.Core.Rebuild;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// Contract tests for IndexRebuilder. Assertions are derived from the contract in IndexRebuilder.cs
/// and the reference implementation rebuild_index_from_catalog.py. They will throw
/// NotImplementedException against the skeleton stub; the integrator runs them against the real
/// implementation to decide whether a failing test or the implementation is wrong.
/// </summary>
public class RebuildTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _indexRoot;     // substitute for %APPDATA%\Claude\claude-code-sessions
    private readonly string _indexDir;      // <indexRoot>\<account>\<org>
    private readonly string _projectsDir;   // substitute for ~\.claude\projects
    private readonly string _catalogDir;

    private static readonly string AccountUuid = "aaaaaaaa-1111-2222-3333-000000000001";
    private static readonly string OrgUuid = "bbbbbbbb-1111-2222-3333-000000000002";
    private static readonly string Session1 = "cccccccc-1111-2222-3333-000000000003";
    private static readonly string Session2 = "dddddddd-1111-2222-3333-000000000004";
    private static readonly string Session3 = "eeeeeeee-1111-2222-3333-000000000005";

    public RebuildTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "csb_reb_" + Guid.NewGuid().ToString("N"));
        _indexRoot = Path.Combine(_tmp, "index");
        _indexDir = Path.Combine(_indexRoot, AccountUuid, OrgUuid);
        _projectsDir = Path.Combine(_tmp, "projects");
        _catalogDir = Path.Combine(_tmp, "catalog");
        Directory.CreateDirectory(_indexDir);
        Directory.CreateDirectory(_projectsDir);
        Directory.CreateDirectory(_catalogDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch { /* best-effort */ }
    }

    // --- helpers ---

    private static JsonDocument ParseUtf8(byte[] bytes) =>
        JsonDocument.Parse(bytes);

    /// <summary>Write a local_*.json index record to the live index folder.</summary>
    private void WriteIndexRecord(string fileName, Dictionary<string, object?> fields)
    {
        var path = Path.Combine(_indexDir, fileName);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(fields, opts));
    }

    /// <summary>Write a compact local_*.json index record.</summary>
    private void WriteCompactIndexRecord(string fileName, Dictionary<string, object?> fields)
    {
        var path = Path.Combine(_indexDir, fileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(fields));
    }

    /// <summary>Make a minimal donor record with all the common keys plus some rare ones.</summary>
    private static Dictionary<string, object?> DonorRecord(string sessionId, string cliSessionId, bool includeRareKeys = false)
    {
        var d = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["cliSessionId"] = cliSessionId,
            ["cwd"] = "C:\\some\\project",
            ["originCwd"] = "C:\\some\\project",
            ["title"] = "Test Session",
            ["titleSource"] = "auto",
            ["createdAt"] = 1700000000000L,
            ["lastActivityAt"] = 1700000001000L,
            ["lastFocusedAt"] = 1700000001000L,
            ["isArchived"] = false,
            ["lastSpawnRootDetected"] = false,
            ["remoteControlAutoEligible"] = false,
            ["alwaysAllowedReasons"] = new string[] { },
            ["sessionPermissionUpdates"] = new string[] { },
            ["spawnSeed"] = new Dictionary<string, object?>(),
            ["enabledMcpTools"] = new string[] { },
        };
        if (includeRareKeys)
        {
            // These are in PER_SESSION_KEYS (static drop list) and must always be dropped.
            d["bridgeSessionIds"] = new string[] { };
            d["scheduledTaskId"] = "task123";
            d["worktreePath"] = "C:\\worktree";
        }
        return d;
    }

    /// <summary>Create a minimal transcript file for a session so the rebuild won't skip it.</summary>
    private string CreateTranscript(string slug, string sessionId, bool empty = false)
    {
        var dir = Path.Combine(_projectsDir, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        if (empty)
            File.WriteAllBytes(path, Array.Empty<byte>());
        else
            File.WriteAllText(path, "{\"type\":\"system\",\"cwd\":\"C:\\\\" + slug + "\"}\n");
        return path;
    }

    /// <summary>
    /// Write a sessions_catalog.json with the given sessions list.
    /// Returns the path to the catalog file.
    /// </summary>
    private string WriteCatalog(IEnumerable<SessionEntry> sessions)
    {
        var catalog = new SessionCatalog
        {
            Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Sessions = new List<SessionEntry>(sessions),
        };
        var path = Path.Combine(_catalogDir, "sessions_catalog.json");
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(catalog, opts));
        return path;
    }

    private RebuildOptions MakeOptions(string catalogPath, bool commit = false, string? outDir = null,
        bool includeDeleted = false, bool includeCli = false,
        bool includeSubagents = false, bool includeEmpty = false) =>
        new()
        {
            CatalogPath = catalogPath,
            IndexRoot = _indexRoot,
            IndexDir = _indexDir,
            ProjectsDir = _projectsDir,
            Commit = commit,
            OutDir = outDir,
            IncludeDeleted = includeDeleted,
            IncludeCli = includeCli,
            IncludeSubagents = includeSubagents,
            IncludeEmpty = includeEmpty,
            Force = true, // Tests must not be blocked by a running Claude Desktop
        };

    // -------------------------------------------------------------------------
    // Index folder resolution
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_IndexDir_NeverResolvesToRoot()
    {
        // The rebuild must refuse if the only folder is the root itself.
        // Contract: "Never the root itself - refuse with a clear message."
        var emptyRoot = Path.Combine(_tmp, "empty_root");
        Directory.CreateDirectory(emptyRoot);

        var catalogPath = WriteCatalog(Array.Empty<SessionEntry>());
        var opts = new RebuildOptions
        {
            CatalogPath = catalogPath,
            IndexRoot = emptyRoot,
            IndexDir = null,  // auto-detect
            ProjectsDir = _projectsDir,
        };

        // Plan should either throw a clear exception or return a plan with 0 items
        // and a clear refusal reason.
        var rebuilder = new IndexRebuilder();
        // We accept either: an exception with a clear message, or a plan with no ToWrite.
        try
        {
            var plan = rebuilder.Plan(opts, null);
            // If it didn't throw, the plan should have an empty IndexDir or no records to write.
            // A plan that would write to the root is the error case.
            if (plan.IndexDir.Length > 0)
            {
                // IndexDir must not resolve to the index root itself; records written there
                // are invisible to the app (they need to live in <root>\<account>\<org>).
                Assert.NotEqual(
                    Path.GetFullPath(emptyRoot),
                    Path.GetFullPath(plan.IndexDir));
            }
        }
        catch (Exception ex) when (ex.Message.Contains("root") || ex.Message.Contains("invisible") ||
                                   ex.Message.Contains("index") || ex.Message.Contains("FATAL"))
        {
            // A clear exception message is also an acceptable refusal.
        }
    }

    [Fact]
    public void Rebuild_IndexDir_PrefersHighestRecordCount()
    {
        // The folder with the most local_*.json records is chosen as IndexDir.
        // Create two sub-folders; the one with more records should win.
        var root = Path.Combine(_tmp, "multidir");
        var dir1 = Path.Combine(root, AccountUuid, OrgUuid);
        var dir2 = Path.Combine(root, "account2", "org2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        // dir1 has 3 records (written directly to dir1, not via WriteIndexRecord which targets _indexDir).
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllBytes(Path.Combine(dir1, "local_a.json"),
            JsonSerializer.SerializeToUtf8Bytes(DonorRecord("local_a", Session1), jsonOpts));
        File.WriteAllBytes(Path.Combine(dir1, "local_b.json"),
            JsonSerializer.SerializeToUtf8Bytes(DonorRecord("local_b", Session2), jsonOpts));
        File.WriteAllBytes(Path.Combine(dir1, "local_c.json"),
            JsonSerializer.SerializeToUtf8Bytes(DonorRecord("local_c", Session3), jsonOpts));
        // dir2 has 1 record
        File.WriteAllText(
            Path.Combine(dir2, "local_d.json"),
            JsonSerializer.Serialize(DonorRecord("local_d", "11111111-2222-3333-4444-555555555555")));

        var catalogPath = WriteCatalog(Array.Empty<SessionEntry>());
        var opts = new RebuildOptions
        {
            CatalogPath = catalogPath,
            IndexRoot = root,
            IndexDir = null,
            ProjectsDir = _projectsDir,
        };

        var plan = new IndexRebuilder().Plan(opts, null);

        Assert.Equal(Path.GetFullPath(dir1), Path.GetFullPath(plan.IndexDir));
    }

    // -------------------------------------------------------------------------
    // Key statistics: rare key drop
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Plan_DropsRareKeys()
    {
        // Keys present in fewer than 50% of reference records (with >= 5 records) are dropped.
        // Contract: "keys present in fewer than 50% of records are 'rare'"

        // Create 6 reference records; "rareKey" only appears in 2 of them (< 50%).
        for (int i = 0; i < 4; i++)
        {
            WriteIndexRecord($"local_{i:D3}.json",
                DonorRecord("local_" + Guid.NewGuid().ToString("N")[..16], Session1));
        }
        // Two records with the rare key.
        for (int i = 4; i < 6; i++)
        {
            var rec = DonorRecord("local_" + Guid.NewGuid().ToString("N")[..16], Session2);
            rec["rareKey"] = "someValue";
            WriteIndexRecord($"local_{i:D3}.json", rec);
        }

        // DroppedKeys = drop_set ∩ donor_keys. Force a donor that has "rareKey" so it appears
        // in DroppedKeys. Without DonorPath the auto-selected donor is the clean record (most keys
        // match the common schema), and "rareKey" is not in its key set.
        var catalogPath = WriteCatalog(Array.Empty<SessionEntry>());
        var donorPath = Path.Combine(_indexDir, "local_004.json");
        var opts = MakeOptions(catalogPath);
        opts.DonorPath = donorPath;
        var plan = new IndexRebuilder().Plan(opts, null);

        Assert.Contains("rareKey", plan.DroppedKeys);
    }

    [Fact]
    public void Rebuild_Plan_DropsStaticPerSessionKeys()
    {
        // Static per-session keys are ALWAYS dropped, regardless of frequency.
        // Contract: "Drop set = the static per-session list ... union rare."
        var donorFields = DonorRecord("local_donor", Session1, includeRareKeys: true);
        WriteIndexRecord("local_donor.json", donorFields);

        // Add 5+ more records so the rare-key heuristic activates.
        for (int i = 0; i < 5; i++)
        {
            var rec = DonorRecord("local_" + i, Session2);
            // bridgeSessionIds appears in 1 of 6 records = rare AND static.
            if (i == 0) rec["bridgeSessionIds"] = new string[] { };
            WriteIndexRecord($"local_r{i}.json", rec);
        }

        // DroppedKeys = drop_set ∩ donor_keys. Force local_donor.json (which carries
        // bridgeSessionIds, scheduledTaskId, worktreePath) as the donor so those keys
        // appear in DroppedKeys. Without DonorPath the auto-selected donor is one of the
        // clean records, which lack the static per-session keys.
        var catalogPath = WriteCatalog(Array.Empty<SessionEntry>());
        var donorPath = Path.Combine(_indexDir, "local_donor.json");
        var opts = MakeOptions(catalogPath);
        opts.DonorPath = donorPath;
        var plan = new IndexRebuilder().Plan(opts, null);

        // These must always appear in DroppedKeys.
        Assert.Contains("bridgeSessionIds", plan.DroppedKeys);
        Assert.Contains("scheduledTaskId", plan.DroppedKeys);
        Assert.Contains("worktreePath", plan.DroppedKeys);
    }

    [Fact]
    public void Rebuild_Plan_DoesNotDropRareKeysWithFewerThanFiveRecords()
    {
        // When there are fewer than 5 reference records, the rare-key heuristic is skipped.
        // Contract: "only computed when at least 5 reference records exist"

        // Only 2 records; "customProp" appears in 1 of 2 = 50% (normally rare).
        var rec1 = DonorRecord("local_1", Session1);
        rec1["customProp"] = "value";
        var rec2 = DonorRecord("local_2", Session2);
        WriteIndexRecord("local_1.json", rec1);
        WriteIndexRecord("local_2.json", rec2);

        var catalogPath = WriteCatalog(Array.Empty<SessionEntry>());
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath), null);

        // With only 2 reference records, the rare-key heuristic does not run.
        // customProp is NOT in the static list, so it should NOT be dropped.
        Assert.DoesNotContain("customProp", plan.DroppedKeys);
    }

    // -------------------------------------------------------------------------
    // Key order preservation
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Apply_KeyOrderPreservedFromDonor()
    {
        // The key order of the donor must be preserved in synthesised records.
        // Contract: "Key ORDER of the donor is preserved."
        var donorFields = new Dictionary<string, object?>
        {
            ["sessionId"] = "local_donor",
            ["cliSessionId"] = Session1,
            ["title"] = "Donor",
            ["cwd"] = "C:\\proj",
            ["originCwd"] = "C:\\proj",
            ["createdAt"] = 1700000000000L,
            ["lastActivityAt"] = 1700000001000L,
            ["isArchived"] = false,
        };
        WriteIndexRecord("local_donor.json", donorFields);
        CreateTranscript("project", Session2);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session2,
            Title = "New Session",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session2 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000002000L,
            LastMs = 1700000003000L,
        };

        var outDir = Path.Combine(_tmp, "out");
        Directory.CreateDirectory(outDir);
        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var opts = MakeOptions(catalogPath, commit: true, outDir: outDir);
        var rebuilder = new IndexRebuilder();
        var plan = rebuilder.Plan(opts, null);
        var result = rebuilder.Apply(plan, opts, null);

        Assert.False(result.Refused, "Apply must not be refused (Force=true).");
        Assert.NotEmpty(result.WrittenFiles);

        var writtenPath = result.WrittenFiles[0];
        var rawBytes = File.ReadAllBytes(writtenPath);
        using var doc = JsonDocument.Parse(rawBytes);

        // The first key in the JSON must be "sessionId".
        var firstProperty = doc.RootElement.EnumerateObject().First();
        Assert.Equal("sessionId", firstProperty.Name);
    }

    // -------------------------------------------------------------------------
    // Compact vs indented output
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Apply_CompactOutputFollowsDonor()
    {
        // If the donor is compact (no leading "{\n"), synthesised records must be compact too.
        // Contract: "Output formatting (compact vs indented) follows the donor's bytes."
        WriteCompactIndexRecord("local_compact_donor.json", DonorRecord("local_compact", Session1));
        CreateTranscript("project", Session2);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session2,
            Title = "New Session",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session2 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000002000L,
            LastMs = 1700000003000L,
        };

        var outDir = Path.Combine(_tmp, "out_compact");
        Directory.CreateDirectory(outDir);
        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var opts = MakeOptions(catalogPath, commit: true, outDir: outDir);
        var rebuilder = new IndexRebuilder();
        var plan = rebuilder.Plan(opts, null);

        // plan.DonorCompact should be true.
        Assert.True(plan.DonorCompact);

        var result = rebuilder.Apply(plan, opts, null);
        Assert.False(result.Refused, "Apply must not be refused (Force=true).");
        Assert.NotEmpty(result.WrittenFiles);

        var written = File.ReadAllBytes(result.WrittenFiles[0]);
        // Compact format does not start with "{\n".
        Assert.False(written.Length > 2 && written[0] == (byte)'{' && written[1] == (byte)'\n',
            "Compact donor must produce compact output (no leading newline after opening brace).");
    }

    [Fact]
    public void Rebuild_Apply_IndentedOutputFollowsDonor()
    {
        // If the donor is indented, synthesised records must also be indented.
        WriteIndexRecord("local_indented_donor.json", DonorRecord("local_indented", Session1));
        CreateTranscript("project", Session2);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session2,
            Title = "New Session",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session2 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000002000L,
            LastMs = 1700000003000L,
        };

        var outDir = Path.Combine(_tmp, "out_indented");
        Directory.CreateDirectory(outDir);
        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var opts = MakeOptions(catalogPath, commit: true, outDir: outDir);
        var rebuilder = new IndexRebuilder();
        var plan = rebuilder.Plan(opts, null);

        Assert.False(plan.DonorCompact, "WriteIndented=true donor should report DonorCompact=false.");

        var result = rebuilder.Apply(plan, opts, null);
        Assert.False(result.Refused, "Apply must not be refused (Force=true).");
        Assert.NotEmpty(result.WrittenFiles);

        var text = File.ReadAllText(result.WrittenFiles[0]);
        // Indented format has newlines inside the object.
        Assert.Contains("\n", text);
    }

    // -------------------------------------------------------------------------
    // Apply output: no BOM, no trailing newline
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Apply_NoBomInOutputFiles()
    {
        // Written files must be UTF-8 WITHOUT BOM.
        // Contract: "Written as UTF-8, no BOM, no trailing newline."
        WriteCompactIndexRecord("local_donor.json", DonorRecord("local_donor", Session1));
        CreateTranscript("project", Session2);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session2,
            Title = "Test",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session2 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var outDir = Path.Combine(_tmp, "out_bom");
        Directory.CreateDirectory(outDir);
        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var opts = MakeOptions(catalogPath, commit: true, outDir: outDir);
        var rebuilder = new IndexRebuilder();
        var plan = rebuilder.Plan(opts, null);
        var result = rebuilder.Apply(plan, opts, null);
        Assert.False(result.Refused, "Apply must not be refused (Force=true).");
        Assert.NotEmpty(result.WrittenFiles);

        var bytes = File.ReadAllBytes(result.WrittenFiles[0]);
        Assert.True(bytes.Length >= 3, "Written file must not be empty.");
        // UTF-8 BOM is EF BB BF.
        bool hasBom = bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(hasBom, "Written records must not have a UTF-8 BOM.");
    }

    [Fact]
    public void Rebuild_Apply_NoTrailingNewline()
    {
        // Written files must NOT end with a newline character.
        // Contract: "no trailing newline"
        WriteCompactIndexRecord("local_donor.json", DonorRecord("local_donor", Session1));
        CreateTranscript("project", Session2);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session2,
            Title = "Test",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session2 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var outDir = Path.Combine(_tmp, "out_newline");
        Directory.CreateDirectory(outDir);
        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var opts = MakeOptions(catalogPath, commit: true, outDir: outDir);
        var rebuilder = new IndexRebuilder();
        var plan = rebuilder.Plan(opts, null);
        var result = rebuilder.Apply(plan, opts, null);
        Assert.False(result.Refused, "Apply must not be refused (Force=true).");
        Assert.NotEmpty(result.WrittenFiles);

        var bytes = File.ReadAllBytes(result.WrittenFiles[0]);
        var last = bytes[^1];
        Assert.False(last == (byte)'\n' || last == (byte)'\r',
            "Written records must not end with a trailing newline.");
    }

    // -------------------------------------------------------------------------
    // Skip reasons
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Plan_SkipsAlreadyLiveSessions()
    {
        // Sessions already in the live index must be skipped.
        // Contract: "already live in the index (by cliSessionId)"
        WriteIndexRecord("local_existing.json", DonorRecord("local_existing", Session1));
        CreateTranscript("project", Session1);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session1,  // same as an existing record
            Title = "Already Live",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session1 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath), null);

        Assert.Empty(plan.ToWrite);
        Assert.True(plan.Skipped.Any(s => s.Session.CliSessionId == Session1),
            "Already-live sessions must appear in Skipped.");
        Assert.Contains(plan.Skipped, s =>
            s.Session.CliSessionId == Session1 &&
            s.Reason.Contains("already", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rebuild_Plan_SkipsSubagentsByDefault()
    {
        // Subagent sessions must be skipped unless IncludeSubagents is true.
        // Contract: "subagent transcript unless IncludeSubagents"
        WriteIndexRecord("local_donor.json", DonorRecord("local_donor", Session2));
        CreateTranscript("project", Session1);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session1,
            Title = "Subagent",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/subagents/" + Session1 + ".jsonl",
            IsSubagent = true,
            TranscriptLive = true,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath, includeSubagents: false), null);

        Assert.Empty(plan.ToWrite);
        Assert.Contains(plan.Skipped, s => s.Session.CliSessionId == Session1);
    }

    [Fact]
    public void Rebuild_Plan_SkipsCliSessionsByDefault()
    {
        // CLI sessions (entrypoint = "cli") must be skipped unless IncludeCli is true.
        // Contract: "entrypoint 'cli' unless IncludeCli"
        WriteIndexRecord("local_donor.json", DonorRecord("local_donor", Session2));
        CreateTranscript("project", Session1);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session1,
            Title = "CLI Session",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session1 + ".jsonl",
            Entrypoint = "cli",
            TranscriptLive = true,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath, includeCli: false), null);

        Assert.Empty(plan.ToWrite);
        Assert.Contains(plan.Skipped, s => s.Session.CliSessionId == Session1);
    }

    [Fact]
    public void Rebuild_Plan_SkipsDeletedSessionsByDefault()
    {
        // Sessions with a deleted marker must be skipped unless IncludeDeleted.
        // Contract: "deleted marker (live root or backup copy, or catalog flag) unless IncludeDeleted"
        WriteIndexRecord("local_donor.json", DonorRecord("local_donor", Session2));
        CreateTranscript("project", Session1);

        // Create a deleted marker file for Session1.
        File.WriteAllText(Path.Combine(_indexDir, "deleted_" + Session1), "");

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session1,
            Title = "Deleted Session",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session1 + ".jsonl",
            TranscriptLive = true,
            IndexDeletedMarker = true,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath, includeDeleted: false), null);

        Assert.Empty(plan.ToWrite);
        Assert.Contains(plan.Skipped, s => s.Session.CliSessionId == Session1);
    }

    [Fact]
    public void Rebuild_Plan_SkipsZeroByteTranscriptsByDefault()
    {
        // Zero-byte transcripts are skipped unless IncludeEmpty.
        // Contract: "0-byte transcript unless IncludeEmpty"
        WriteIndexRecord("local_donor.json", DonorRecord("local_donor", Session2));
        CreateTranscript("project", Session1, empty: true);  // 0-byte

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session1,
            Title = "Empty Session",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session1 + ".jsonl",
            TranscriptLive = true,
            Size = 0,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath, includeEmpty: false), null);

        Assert.Empty(plan.ToWrite);
        Assert.Contains(plan.Skipped, s => s.Session.CliSessionId == Session1);
    }

    [Fact]
    public void Rebuild_Plan_SkipsWhenTranscriptNotOnDisk()
    {
        // Sessions whose transcript is not present on disk are skipped.
        // Contract: "transcript not on disk under ProjectsDir"
        WriteIndexRecord("local_donor.json", DonorRecord("local_donor", Session2));
        // Do NOT create the transcript file on disk.

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session1,
            Title = "Missing Transcript",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session1 + ".jsonl",
            TranscriptLive = true,  // catalog says live but file doesn't exist
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath), null);

        Assert.Empty(plan.ToWrite);
        Assert.Contains(plan.Skipped, s => s.Session.CliSessionId == Session1);
    }

    // -------------------------------------------------------------------------
    // Plan ordering
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Plan_OrderedByAscendingLastMs()
    {
        // Sessions in the plan must be in ascending last_ms order.
        // Contract: "Planned in ascending last_ms order."
        WriteIndexRecord("local_donor.json", DonorRecord("local_donor", Session3));
        CreateTranscript("proj", Session1);
        CreateTranscript("proj", Session2);

        var sessions = new[]
        {
            new SessionEntry
            {
                CliSessionId = Session1,
                Title = "Later Session",
                Cwd = "C:\\proj",
                ProjectDir = "proj",
                TranscriptRel = "proj/" + Session1 + ".jsonl",
                TranscriptLive = true,
                LastMs = 1700000002000L,  // later
                FirstMs = 1700000001000L,
            },
            new SessionEntry
            {
                CliSessionId = Session2,
                Title = "Earlier Session",
                Cwd = "C:\\proj",
                ProjectDir = "proj",
                TranscriptRel = "proj/" + Session2 + ".jsonl",
                TranscriptLive = true,
                LastMs = 1700000001000L,  // earlier
                FirstMs = 1700000000000L,
            },
        };

        var catalogPath = WriteCatalog(sessions);
        var plan = new IndexRebuilder().Plan(MakeOptions(catalogPath), null);

        if (plan.ToWrite.Count >= 2)
        {
            var first = plan.ToWrite[0].Session.LastMs;
            var second = plan.ToWrite[1].Session.LastMs;
            Assert.True(first <= second,
                "Plan entries must be in ascending last_ms order.");
        }
    }

    // -------------------------------------------------------------------------
    // Dry run (Commit = false)
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Plan_DryRunWritesNoFiles()
    {
        // Plan() alone must not write any files.
        WriteIndexRecord("local_donor.json", DonorRecord("local_donor", Session1));
        CreateTranscript("project", Session2);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session2,
            Title = "New Session",
            Cwd = "C:\\proj",
            ProjectDir = "project",
            TranscriptRel = "project/" + Session2 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000000000L,
            LastMs = 1700000001000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });
        // Commit = false (default)
        new IndexRebuilder().Plan(MakeOptions(catalogPath, commit: false), null);

        // No new json files should appear (only the donor is there).
        var allJson = Directory.GetFiles(_indexDir, "local_*.json");
        Assert.Single(allJson);  // only the donor, dry-run must add none
    }

    // -------------------------------------------------------------------------
    // DesktopAppProcesses
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_DesktopAppProcesses_ReturnsList()
    {
        // DesktopAppProcesses must return a list (may be empty if no desktop app is running).
        // It must not throw. The result is used to block --commit when the app is running.
        var rebuilder = new IndexRebuilder();
        var processes = rebuilder.DesktopAppProcesses();
        Assert.NotNull(processes);
        // The result may be empty (no desktop app running during tests).
        // Critically: every returned path must NOT be under ClaudePaths.CliBinaryRoot.
        foreach (var path in processes)
        {
            Assert.False(path.StartsWith(ClaudePaths.CliBinaryRoot, StringComparison.OrdinalIgnoreCase),
                $"DesktopAppProcesses must not include CLI processes. Found: {path}");
        }
    }

    // -------------------------------------------------------------------------
    // Contract gap 9: Apply with OutDir == index folder backs up before writing
    // -------------------------------------------------------------------------

    [Fact]
    public void Rebuild_Apply_OutDirIsIndexDir_BacksUpFolderBeforeWriting()
    {
        // Contract: "when writing into the live index folder, copy it first to
        // <folder>_backup_<stamp>; then write each planned file."
        // Stage an index root with one donor record.  Apply with OutDir == IndexDir
        // (the default when OutDir is null) and Force=true.
        // Assert: (1) a <indexDir>_backup_<stamp> folder exists containing the original
        //         donor, and (2) the new synthesised record is written to the index folder.

        // Build a donor record in the index folder.
        WriteIndexRecord("local_donor_gap9.json", DonorRecord("local_donor_gap9", Session1));
        CreateTranscript("project-gap9", Session2);

        var sessionEntry = new SessionEntry
        {
            CliSessionId = Session2,
            Title = "Gap-9 Session",
            Cwd = "C:\\proj-gap9",
            ProjectDir = "project-gap9",
            TranscriptRel = "project-gap9/" + Session2 + ".jsonl",
            TranscriptLive = true,
            FirstMs = 1700000010000L,
            LastMs = 1700000011000L,
        };

        var catalogPath = WriteCatalog(new[] { sessionEntry });

        // OutDir = null → outDir resolves to plan.IndexDir → backup fires.
        var opts = new RebuildOptions
        {
            CatalogPath = catalogPath,
            IndexRoot = _indexRoot,
            IndexDir = _indexDir,
            ProjectsDir = _projectsDir,
            Commit = true,
            OutDir = null,  // write to the live index folder → triggers backup
            Force = true,   // skip desktop-app check (WPF app may be running)
        };

        var rebuilder = new IndexRebuilder();
        var plan = rebuilder.Plan(opts, null);
        var result = rebuilder.Apply(plan, opts, null);

        Assert.False(result.Refused, "Apply must not be refused (Force=true).");

        // (1) A backup folder <indexDir>_backup_<stamp> must exist.
        Assert.NotNull(result.IndexBackupDir);
        Assert.True(Directory.Exists(result.IndexBackupDir),
            "Apply must create a <indexDir>_backup_<stamp> folder before writing.");

        // (2) The backup folder must contain the original donor record.
        var backupFiles = Directory.GetFiles(result.IndexBackupDir!, "local_donor_gap9.json");
        Assert.True(backupFiles.Length > 0,
            "The backup folder must contain the original donor record.");

        // (3) The new record must be written to the index folder (not only to the backup).
        Assert.NotEmpty(result.WrittenFiles);
        Assert.True(result.WrittenFiles.All(f => f.StartsWith(_indexDir, StringComparison.OrdinalIgnoreCase)),
            "New records must be written to the live index folder, not just the backup.");
    }
}
