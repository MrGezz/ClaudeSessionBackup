using System.Collections.ObjectModel;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.App.Services;

/// <summary>
/// Generates entirely fictional data so the app can run in demo mode without
/// touching real stores, settings, or the user's backup destination. Every
/// string is invented; no real username, machine name, path, session title, or
/// transcript content appears. Used by <c>--demo</c> and by
/// <c>Capture-Screenshots.ps1 -Demo</c> to produce screenshots safe for the
/// public repo.
/// </summary>
internal static class DemoDataSource
{
    // ----------------------------------------------------------------- stamp
    // A fixed stamp so the demo is deterministic across runs.
    private static readonly DateTime DemoStamp = new(2026, 9, 5, 21, 0, 14, DateTimeKind.Local);
    private static readonly string StampStr = DemoStamp.ToString("yyyyMMdd_HHmmss");

    // ----------------------------------------------------------- store rows

    /// <summary>
    /// Builds a <see cref="RunManifest"/> that populates the Dashboard table
    /// with 13 plausible rows, four of which are optional/absent MSIX stores.
    /// </summary>
    public static RunManifest BuildManifest()
    {
        var stores = new List<StoreResult>
        {
            S("code-transcripts", StoreStatus.Ok,     1203, 1_181_116_000L, 1203, 1_181_116_000L, 12, 1191, 0, 0),
            S("code-config",      StoreStatus.Ok,       47,     2_340_000L,   47,     2_340_000L,  3,   44, 0, 0),
            S("cowork-index",     StoreStatus.Ok,       38,     2_508_000L,   38,     2_508_000L,  1,   37, 0, 0),
            S("cowork-agent-mode",StoreStatus.Ok,       14,    18_900_000L,   14,    18_900_000L,  0,   14, 0, 0),
            S("cowork-scratch",   StoreStatus.Ok,        9,     4_200_000L,    9,     4_200_000L,  2,    7, 0, 0),
            S("cowork-config",    StoreStatus.Ok,       22,     1_450_000L,   22,     1_450_000L,  1,   21, 0, 0),
            S("cowork-logs",      StoreStatus.Ok,        5,    12_600_000L,    5,    12_600_000L,  1,    4, 0, 0),
            S("cowork3p-index",   StoreStatus.SourceMissing, 0, 0, 0, 0, 0, 0, 0, 0),
            S("cowork3p-agent-mode", StoreStatus.SourceMissing, 0, 0, 0, 0, 0, 0, 0, 0),
            S("msix-index",       StoreStatus.SourceMissing, 0, 0, 0, 0, 0, 0, 0, 0),
            S("msix-agent-mode",  StoreStatus.SourceMissing, 0, 0, 0, 0, 0, 0, 0, 0),
            S("msix-scratch",     StoreStatus.SourceMissing, 0, 0, 0, 0, 0, 0, 0, 0),
            S("msix-config",      StoreStatus.SourceMissing, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        return new RunManifest(
            StampStr, "backup", @"D:\Backups\Claude", IncludeSubagents: false,
            stores, @"D:\Backups\Claude\catalog\sessions_catalog.json",
            @"D:\Backups\Claude\snapshots\20260905_210014_claude-stores.zip",
            Warnings: Array.Empty<string>(), Seconds: 8.4,
            LogPath: @"D:\Backups\Claude\logs\20260905_210014_backup.log");

        static StoreResult S(string name, StoreStatus status,
            int lf, long lb, int bf, long bb, int copied, int unchanged, int failed, int held) =>
            new(name, status, lf, lb, bf, bb, copied, unchanged, failed, held, null);
    }

    // ------------------------------------------------------------ log lines

    public static IReadOnlyList<string> LogLines => new[]
    {
        $"{DemoStamp:yyyy-MM-dd HH:mm:ss} [INFO] === Backup-ClaudeSessions BACKUP {StampStr} ===",
        $"{DemoStamp:yyyy-MM-dd HH:mm:ss} [INFO] destination: D:\\Backups\\Claude",
        $"{DemoStamp.AddSeconds(2):yyyy-MM-dd HH:mm:ss} [INFO] code-transcripts     1,203 files seen,    12 copied,  1,191 unchanged",
        $"{DemoStamp.AddSeconds(5):yyyy-MM-dd HH:mm:ss} [INFO] cowork-index            38 files seen,     1 copied,    37 unchanged",
        $"{DemoStamp.AddSeconds(8):yyyy-MM-dd HH:mm:ss} [INFO] done: {DemoStamp.AddSeconds(8):yyyy-MM-dd HH:mm:ss} backup 13 stores, 0 warnings, 8s",
    };

    public static string LastRunSummary =>
        $"Last run: {StampStr} - backup - 20 copied, 0 failed, 0 warnings (8.4s)  on WORKSTATION  to D:\\Backups\\Claude";

    // ------------------------------------------------------------ catalog

    /// <summary>14 fictional sessions with invented titles, projects, and realistic sizes.</summary>
    public static List<SessionEntry> CatalogSessions()
    {
        var sessions = new List<SessionEntry>();
        int i = 0;

        void Add(string title, string project, int records, long size,
            int userP, int assistantM, string firstTs, string lastTs,
            bool isLost = false, bool isDangling = false,
            string? entrypoint = "cowork", string? gitBranch = null)
        {
            var id = Guid.NewGuid().ToString("N")[..24];
            var firstMs = DateTimeOffset.Parse(firstTs).ToUnixTimeMilliseconds();
            var lastMs = DateTimeOffset.Parse(lastTs).ToUnixTimeMilliseconds();

            sessions.Add(new SessionEntry
            {
                CliSessionId = id,
                Title = title,
                TitleSource = "ai-title",
                ProjectDir = project,
                TranscriptRel = isDangling ? null : $"{project}/{id}.jsonl",
                TranscriptLive = !isLost && !isDangling,
                TranscriptBackup = !isDangling,
                IsSubagent = false,
                Size = size,
                FirstTs = firstTs,
                LastTs = lastTs,
                FirstMs = firstMs,
                LastMs = lastMs,
                Records = records,
                UserPrompts = userP,
                AssistantMsgs = assistantM,
                Entrypoint = entrypoint,
                GitBranch = gitBranch,
                FirstPrompt = $"Session {i + 1} first prompt",
                IndexSessionId = isDangling ? null : Guid.NewGuid().ToString(),
                IndexFile = isDangling ? null : $"account/org/local_{id}.json",
            });
            i++;
        }

        Add("Refactor the invoice parser",        "acme-api",     312, 2_450_000,  28,  26, "2026-09-05T14:20:00Z", "2026-09-05T19:45:00Z", gitBranch: "feat/invoice-v2");
        Add("Plan the kitchen renovation",        "home-notes",    86,   340_000,  18,  16, "2026-09-04T08:00:00Z", "2026-09-04T10:30:00Z");
        Add("Debug the sprinkler timer",          "home-notes",    42,   180_000,  12,  10, "2026-09-03T17:00:00Z", "2026-09-03T18:15:00Z");
        Add("Add pagination to the REST endpoint","acme-api",     188, 1_100_000,  22,  20, "2026-09-02T09:00:00Z", "2026-09-02T16:00:00Z", gitBranch: "fix/pagination");
        Add("Draft the literature review",        "thesis",       240, 1_800_000,  32,  30, "2026-09-01T10:00:00Z", "2026-09-01T22:00:00Z");
        Add("Set up CI pipeline for the monorepo","acme-api",     104,   620_000,  14,  12, "2026-08-31T11:00:00Z", "2026-08-31T14:00:00Z", gitBranch: "ci/github-actions");
        Add("Migrate user table to Postgres 16",  "acme-api",     156,   900_000,  20,  18, "2026-08-30T08:00:00Z", "2026-08-30T17:00:00Z", gitBranch: "db/pg16-migration");
        Add("Summarise the quarterly report",     "work-docs",     64,   280_000,  10,   8, "2026-08-29T14:00:00Z", "2026-08-29T15:30:00Z");
        Add("Fix the leaking garden hose valve",  "home-notes",    28,   120_000,   8,   6, "2026-08-28T07:00:00Z", "2026-08-28T07:45:00Z");
        Add("Write unit tests for the auth module","acme-api",    220, 1_500_000,  26,  24, "2026-08-27T09:00:00Z", "2026-08-27T18:00:00Z", gitBranch: "test/auth-coverage");
        Add("Compare paint colours for the study", "home-notes",   36,   150_000,   6,   4, "2026-08-26T19:00:00Z", "2026-08-26T19:30:00Z");
        Add("Outline the methodology chapter",    "thesis",       180, 1_200_000,  24,  22, "2026-08-25T10:00:00Z", "2026-08-25T20:00:00Z");
        // LOST: in backup but not live
        Add("Prototype the notification service",  "acme-api",     92,   540_000,  16,  14, "2026-08-24T09:00:00Z", "2026-08-24T15:00:00Z", isLost: true, gitBranch: "feat/notifications");
        // DANGLING: no transcript at all, index-only
        Add("Quick chat about weekend plans",      "home-notes",    0,         0,   0,   0, "2026-08-23T20:00:00Z", "2026-08-23T20:10:00Z", isDangling: true);

        return sessions;
    }

    // --------------------------------------------------------- rebuild plan

    public static RebuildPlan BuildRebuildPlan()
    {
        var sessions = CatalogSessions();
        var toWrite = new List<PlannedRecord>();
        var skipped = new List<SkippedSession>();

        int written = 0;
        foreach (var s in sessions)
        {
            if (s.IsLost || s.IsDangling)
            {
                skipped.Add(new SkippedSession(s,
                    s.IsDangling ? "no transcript exists" : "transcript only in backup"));
                continue;
            }
            if (written < 3)
            {
                toWrite.Add(new PlannedRecord(s, $"account/org/local_{s.CliSessionId}.json", "{}"));
                written++;
            }
            else
            {
                skipped.Add(new SkippedSession(s, "record already exists"));
            }
        }

        return new RebuildPlan
        {
            CatalogPath = @"D:\Backups\Claude\catalog\sessions_catalog.json",
            CatalogGenerated = DemoStamp.ToString("yyyy-MM-dd HH:mm:ss"),
            CatalogSessions = sessions.Count,
            DonorPath = @"D:\Backups\Claude\live\cowork-index\account\org\local_donor.json",
            DonorKeys = 19,
            DonorCompact = true,
            ReferenceRecords = 38,
            DroppedKeys = new List<string> { "bridgeSessionIds", "completedTurns", "scheduledTaskId" },
            IndexDir = @"C:\Users\you\AppData\Roaming\Claude\claude-code-sessions\account\org",
            LiveRecords = 35,
            DeletedMarkers = 2,
            OutDir = @"C:\Users\you\AppData\Roaming\Claude\claude-code-sessions\account\org",
            ToWrite = toWrite,
            Skipped = skipped,
            KeysPerRecord = 16,
        };
    }

    // ----------------------------------------------------------- transcript

    /// <summary>
    /// Builds a demo transcript of ~24 turns titled "Refactor the invoice parser".
    /// Contains user prompts, assistant Markdown with a code block and a list,
    /// a thinking block, one redacted thinking, two tool calls with results,
    /// one image placeholder, a compact boundary, and a system note.
    /// </summary>
    public static Transcript BuildTranscript()
    {
        var turns = new List<TranscriptTurn>();
        int idx = 0;
        var baseTime = new DateTime(2026, 9, 5, 14, 20, 0, DateTimeKind.Utc);

        TranscriptTurn Turn(TranscriptRole role, int minuteOffset, params TranscriptBlock[] blocks)
        {
            var t = new TranscriptTurn
            {
                Index = idx++,
                Role = role,
                Timestamp = baseTime.AddMinutes(minuteOffset),
                Model = role == TranscriptRole.Assistant ? "claude-sonnet-4-6-20260905" : null,
            };
            t.Blocks.AddRange(blocks);
            return t;
        }

        // Turn 0: User prompt
        turns.Add(Turn(TranscriptRole.User, 0,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "I need to refactor the invoice parser in src/billing/parser.py. It currently handles only CSV but we need to support JSON and XML too. Can you look at the current code and propose a strategy?" }));

        // Turn 1: Assistant thinking + text
        turns.Add(Turn(TranscriptRole.Assistant, 1,
            new TranscriptBlock { Kind = TranscriptBlockKind.Thinking, Text = "The user wants to refactor a parser to support multiple formats. I should look at the current code first to understand the structure, then propose a strategy pattern or similar approach." },
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "I will start by reading the current parser to understand its structure, then propose a refactoring strategy.\n\nThe key goals are:\n\n1. **Support multiple formats** (CSV, JSON, XML)\n2. **Keep the existing CSV behaviour unchanged**\n3. **Make it easy to add new formats later**\n\nLet me look at the code first." }));

        // Turn 2: Assistant tool call - Read
        turns.Add(Turn(TranscriptRole.Assistant, 2,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolUse,
                ToolName = "Read",
                ToolUseId = "toolu_01ABC",
                InputJson = "{\n  \"file_path\": \"src/billing/parser.py\",\n  \"limit\": 200\n}"
            }));

        // Turn 3: Tool result
        turns.Add(Turn(TranscriptRole.User, 2,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolResult,
                ToolUseId = "toolu_01ABC",
                Text = "1\timport csv\n2\tfrom dataclasses import dataclass\n3\t\n4\t@dataclass\n5\tclass Invoice:\n6\t    number: str\n7\t    amount: float\n8\t    currency: str\n9\t    vendor: str\n10\t\n11\tdef parse_invoices(path: str) -> list[Invoice]:\n12\t    results = []\n13\t    with open(path) as f:\n14\t        reader = csv.DictReader(f)\n15\t        for row in reader:\n16\t            results.append(Invoice(\n17\t                number=row['invoice_number'],\n18\t                amount=float(row['amount']),\n19\t                currency=row.get('currency', 'USD'),\n20\t                vendor=row['vendor_name'],\n21\t            ))\n22\t    return results"
            }));

        // Turn 4: Assistant analysis with code block and list
        turns.Add(Turn(TranscriptRole.Assistant, 3,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "The current parser is straightforward but tightly coupled to CSV. Here is my proposed strategy:\n\n## Strategy: Abstract Parser with Format Adapters\n\nWe introduce a base class and one adapter per format:\n\n```python\nfrom abc import ABC, abstractmethod\n\nclass InvoiceParser(ABC):\n    @abstractmethod\n    def parse(self, path: str) -> list[Invoice]:\n        ...\n\n    @classmethod\n    def for_format(cls, fmt: str) -> 'InvoiceParser':\n        registry = {\n            'csv': CsvInvoiceParser,\n            'json': JsonInvoiceParser,\n            'xml': XmlInvoiceParser,\n        }\n        parser_cls = registry.get(fmt)\n        if not parser_cls:\n            raise ValueError(f'Unsupported format: {fmt}')\n        return parser_cls()\n```\n\nBenefits:\n\n- **Open/closed principle** -- new formats need only a new class, no changes to existing code\n- **Testable** -- each adapter can be unit-tested independently\n- **Discoverable** -- the registry makes it clear which formats are supported\n\nShall I implement it?" }));

        // Turn 5: User agreement
        turns.Add(Turn(TranscriptRole.User, 5,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Yes, go ahead. Start with the base class and the CSV adapter, then add JSON." }));

        // Turn 6: Assistant thinking (redacted) + tool call - Write
        turns.Add(Turn(TranscriptRole.Assistant, 6,
            new TranscriptBlock { Kind = TranscriptBlockKind.Thinking, ThinkingRedacted = true },
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolUse,
                ToolName = "Write",
                ToolUseId = "toolu_02DEF",
                InputJson = "{\n  \"file_path\": \"src/billing/parser.py\",\n  \"content\": \"import csv\\nimport json\\nfrom abc import ABC, abstractmethod\\nfrom dataclasses import dataclass\\n\\n@dataclass\\nclass Invoice:\\n    number: str\\n    amount: float\\n    currency: str\\n    vendor: str\\n\\nclass InvoiceParser(ABC):\\n    @abstractmethod\\n    def parse(self, path: str) -> list[Invoice]:\\n        ...\\n\\n    @classmethod\\n    def for_format(cls, fmt: str) -> 'InvoiceParser':\\n        registry = {\\n            'csv': CsvInvoiceParser,\\n            'json': JsonInvoiceParser,\\n        }\\n        parser_cls = registry.get(fmt)\\n        if not parser_cls:\\n            raise ValueError(f'Unsupported: {fmt}')\\n        return parser_cls()\\n\\nclass CsvInvoiceParser(InvoiceParser):\\n    def parse(self, path):\\n        with open(path) as f:\\n            return [Invoice(\\n                number=r['invoice_number'],\\n                amount=float(r['amount']),\\n                currency=r.get('currency','USD'),\\n                vendor=r['vendor_name'])\\n             for r in csv.DictReader(f)]\\n\\nclass JsonInvoiceParser(InvoiceParser):\\n    def parse(self, path):\\n        with open(path) as f:\\n            data = json.load(f)\\n        return [Invoice(**item) for item in data]\"\n}"
            }));

        // Turn 7: Tool result for Write
        turns.Add(Turn(TranscriptRole.User, 6,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolResult,
                ToolUseId = "toolu_02DEF",
                Text = "File written successfully."
            }));

        // Turn 8: Assistant confirms
        turns.Add(Turn(TranscriptRole.Assistant, 7,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Done. The refactored parser now has:\n\n- `InvoiceParser` abstract base class with `for_format()` factory\n- `CsvInvoiceParser` preserving the original CSV behaviour\n- `JsonInvoiceParser` for JSON arrays of invoice objects\n\nLet me write the tests next." }));

        // Turn 9: User
        turns.Add(Turn(TranscriptRole.User, 8,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Good. Write tests for both formats." }));

        // Turn 10: Assistant thinking + text
        turns.Add(Turn(TranscriptRole.Assistant, 9,
            new TranscriptBlock { Kind = TranscriptBlockKind.Thinking, Text = "I need to write pytest tests. I should create test fixtures for both CSV and JSON formats, then test the parser factory and each adapter." },
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "I will create a test file with fixtures for both formats and test the full round-trip." }));

        // Turn 11: Assistant tool call - Write tests
        turns.Add(Turn(TranscriptRole.Assistant, 10,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolUse,
                ToolName = "Write",
                ToolUseId = "toolu_03GHI",
                InputJson = "{\n  \"file_path\": \"tests/test_parser.py\",\n  \"content\": \"import pytest, tempfile, json, os\\nfrom src.billing.parser import InvoiceParser, Invoice\\n\\nCSV_DATA = 'invoice_number,amount,currency,vendor_name\\\\nINV-001,150.00,USD,Acme Corp\\\\nINV-002,89.50,EUR,Widgets Ltd'\\nJSON_DATA = [{'number':'INV-001','amount':150,'currency':'USD','vendor':'Acme Corp'}]\\n\\n@pytest.fixture\\ndef csv_file(tmp_path):\\n    p = tmp_path / 'invoices.csv'\\n    p.write_text(CSV_DATA)\\n    return str(p)\\n\\n@pytest.fixture\\ndef json_file(tmp_path):\\n    p = tmp_path / 'invoices.json'\\n    p.write_text(json.dumps(JSON_DATA))\\n    return str(p)\\n\\ndef test_csv_parser(csv_file):\\n    results = InvoiceParser.for_format('csv').parse(csv_file)\\n    assert len(results) == 2\\n    assert results[0].number == 'INV-001'\\n\\ndef test_json_parser(json_file):\\n    results = InvoiceParser.for_format('json').parse(json_file)\\n    assert len(results) == 1\\n    assert results[0].vendor == 'Acme Corp'\"\n}"
            }));

        // Turn 12: Tool result
        turns.Add(Turn(TranscriptRole.User, 10,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolResult,
                ToolUseId = "toolu_03GHI",
                Text = "File written successfully."
            }));

        // Turn 13: Compact boundary
        turns.Add(Turn(TranscriptRole.System, 15,
            new TranscriptBlock { Kind = TranscriptBlockKind.CompactBoundary, Subtype = "compact_boundary" }));

        // Turn 14: User asks about tests
        turns.Add(Turn(TranscriptRole.User, 20,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Run the tests to make sure everything passes." }));

        // Turn 15: Assistant tool call - Bash
        turns.Add(Turn(TranscriptRole.Assistant, 21,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolUse,
                ToolName = "Bash",
                ToolUseId = "toolu_04JKL",
                InputJson = "{\n  \"command\": \"cd C:\\\\Users\\\\you\\\\projects\\\\acme-api && python -m pytest tests/test_parser.py -v\"\n}"
            }));

        // Turn 16: Tool result - test output
        turns.Add(Turn(TranscriptRole.User, 21,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.ToolResult,
                ToolUseId = "toolu_04JKL",
                Text = "============================= test session starts ==============================\ncollected 2 items\n\ntests/test_parser.py::test_csv_parser PASSED                              [ 50%]\ntests/test_parser.py::test_json_parser PASSED                             [100%]\n\n============================== 2 passed in 0.12s ==============================="
            }));

        // Turn 17: Assistant confirms
        turns.Add(Turn(TranscriptRole.Assistant, 22,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "All tests pass. Both the CSV and JSON parsers work correctly." }));

        // Turn 18: System note
        turns.Add(Turn(TranscriptRole.System, 25,
            new TranscriptBlock { Kind = TranscriptBlockKind.SystemNote, Subtype = "stop_hook_summary", Text = "Hook completed: lint check passed (0 issues)" }));

        // Turn 19: User asks about XML
        turns.Add(Turn(TranscriptRole.User, 30,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Now add the XML parser. Our invoices use this schema:\n\n```xml\n<invoices>\n  <invoice number=\"INV-001\" amount=\"150.00\" currency=\"USD\" vendor=\"Acme Corp\" />\n</invoices>\n```" }));

        // Turn 20: Assistant response
        turns.Add(Turn(TranscriptRole.Assistant, 31,
            new TranscriptBlock { Kind = TranscriptBlockKind.Thinking, Text = "The XML format uses attributes on <invoice> elements inside an <invoices> root. I will use xml.etree.ElementTree which is in the standard library." },
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "I will add an `XmlInvoiceParser` using the standard library `xml.etree.ElementTree`. Each `<invoice>` element's attributes map directly to the `Invoice` fields." }));

        // Turn 21: Image placeholder (screenshot of a diagram)
        turns.Add(Turn(TranscriptRole.User, 35,
            new TranscriptBlock
            {
                Kind = TranscriptBlockKind.Image,
                ImageMediaType = "image/png",
                ImageByteLength = 48_000,
                // No ImageBytes -- rendered as a placeholder
            }));

        // Turn 22: Assistant acknowledges the image
        turns.Add(Turn(TranscriptRole.Assistant, 36,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Thanks for the diagram. The class hierarchy matches what I have implemented. The `XmlInvoiceParser` fits neatly into the existing pattern.\n\nHere is the summary of changes:\n\n| Format | Class | Status |\n|--------|-------|--------|\n| CSV | `CsvInvoiceParser` | Done |\n| JSON | `JsonInvoiceParser` | Done |\n| XML | `XmlInvoiceParser` | In progress |" }));

        // Turn 23: User wraps up
        turns.Add(Turn(TranscriptRole.User, 40,
            new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Looks good. Finish the XML parser and add its test, then we are done for today." }));

        var transcript = new Transcript
        {
            SessionId = "demo-session-001",
            Path = @"C:\Users\you\.claude\projects\acme-api\demo-session-001.jsonl",
            Title = "Refactor the invoice parser",
            Cwd = @"C:\Users\you\projects\acme-api",
            Version = "1.0.33",
            GitBranch = "feat/invoice-v2",
            Entrypoint = "cowork",
            FirstTimestamp = baseTime,
            LastTimestamp = baseTime.AddMinutes(40),
            Turns = turns,
            Records = 312,
            ParseErrors = 0,
            ThinkingBlocks = 4,
            ThinkingRedacted = 1,
            ToolCalls = 4,
            Images = 1,
            Bytes = 2_450_000,
        };

        return transcript;
    }

    /// <summary>
    /// The <see cref="SessionEntry"/> that corresponds to the demo transcript,
    /// for the catalog "Open" path.
    /// </summary>
    public static SessionEntry TranscriptSessionEntry() => new()
    {
        CliSessionId = "demo-session-001",
        Title = "Refactor the invoice parser",
        TitleSource = "ai-title",
        ProjectDir = "acme-api",
        TranscriptRel = "acme-api/demo-session-001.jsonl",
        TranscriptLive = true,
        TranscriptBackup = true,
        Size = 2_450_000,
        FirstTs = "2026-09-05T14:20:00Z",
        LastTs = "2026-09-05T15:00:00Z",
        FirstMs = DateTimeOffset.Parse("2026-09-05T14:20:00Z").ToUnixTimeMilliseconds(),
        LastMs = DateTimeOffset.Parse("2026-09-05T15:00:00Z").ToUnixTimeMilliseconds(),
        Records = 312,
        UserPrompts = 28,
        AssistantMsgs = 26,
        Entrypoint = "cowork",
        GitBranch = "feat/invoice-v2",
    };
}
