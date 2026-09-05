using System.Text;
using System.Text.Json;
using ClaudeSessionBackup.Core.Model;
using ClaudeSessionBackup.Core.Transcripts;
using Xunit;

namespace ClaudeSessionBackup.Tests;

/// <summary>
/// Tests for TranscriptReader and TranscriptExporter, using synthetic JSONL
/// written to temp files. No live-store access.
/// </summary>
public class TranscriptTests : IDisposable
{
    private readonly string _tempDir;

    public TranscriptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csb_test_transcript_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteJsonl(string name, params string[] lines)
    {
        var path = Path.Combine(_tempDir, name + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static TranscriptReader Reader() => new();
    private static TranscriptExporter Exporter() => new();
    private static TranscriptReadOptions DefaultOpts() => new();

    // ──── Multi-record assistant merge ────

    [Fact]
    public async Task MultiRecordAssistantMerge_IntoOneTurnInBlockOrder()
    {
        // Two assistant records sharing message.id merge into one turn.
        var path = WriteJsonl("merge",
            Record("assistant", msg: AssistantMsg("msg_1", "model-x",
                new[] { ThinkingBlock("deep thought") })),
            Record("assistant", msg: AssistantMsg("msg_1", "model-x",
                new[] { TextBlock("Hello world") }))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);

        Assert.Single(t.Turns);
        var turn = t.Turns[0];
        Assert.Equal(TranscriptRole.Assistant, turn.Role);
        Assert.Equal(2, turn.Blocks.Count);
        Assert.Equal(TranscriptBlockKind.Thinking, turn.Blocks[0].Kind);
        Assert.Equal("deep thought", turn.Blocks[0].Text);
        Assert.Equal(TranscriptBlockKind.Text, turn.Blocks[1].Kind);
        Assert.Equal("Hello world", turn.Blocks[1].Text);
        Assert.Equal("model-x", turn.Model);
        Assert.Equal("msg_1", turn.MessageId);
    }

    // ──── Empty thinking -> ThinkingRedacted ────

    [Fact]
    public async Task EmptyThinking_MarkedAsRedacted_CountedInBothCounters()
    {
        var path = WriteJsonl("redacted",
            Record("assistant", msg: AssistantMsg("msg_2", "model-y",
                new[] { ThinkingBlock(""), TextBlock("reply") }))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);

        Assert.Equal(1, t.ThinkingBlocks);
        Assert.Equal(1, t.ThinkingRedacted);
        var thinkBlock = t.Turns[0].Blocks[0];
        Assert.True(thinkBlock.ThinkingRedacted);
        Assert.Null(thinkBlock.Text);
    }

    [Fact]
    public async Task WhitespaceThinking_AlsoRedacted()
    {
        var path = WriteJsonl("ws_redacted",
            Record("assistant", msg: AssistantMsg("msg_3", "m",
                new[] { ThinkingBlock("   \n  ") }))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.True(t.Turns[0].Blocks[0].ThinkingRedacted);
        Assert.Equal(1, t.ThinkingRedacted);
    }

    // ──── Tool use + ToolCalls count ────

    [Fact]
    public async Task ToolUse_PrettyPrintedJson_CountedInToolCalls()
    {
        var input = "{\"command\":\"ls\",\"timeout\":5}";
        var path = WriteJsonl("tool_use",
            Record("assistant", msg: AssistantMsg("msg_tu", "m",
                new[] { ToolUseBlock("toolu_1", "Bash", input) }))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);

        Assert.Equal(1, t.ToolCalls);
        var block = t.Turns[0].Blocks[0];
        Assert.Equal(TranscriptBlockKind.ToolUse, block.Kind);
        Assert.Equal("Bash", block.ToolName);
        Assert.Equal("toolu_1", block.ToolUseId);
        // Pretty-printed: contains newlines and indentation.
        Assert.NotNull(block.InputJson);
        Assert.Contains("\n", block.InputJson);
        Assert.Contains("\"command\"", block.InputJson);
    }

    // ──── Tool result: string form ────

    [Fact]
    public async Task ToolResult_StringContent()
    {
        var path = WriteJsonl("tr_string",
            Record("user", content: ToolResultContent("toolu_1", false, "\"output text\""))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        var block = t.Turns[0].Blocks[0];
        Assert.Equal(TranscriptBlockKind.ToolResult, block.Kind);
        Assert.Equal("toolu_1", block.ToolUseId);
        Assert.False(block.IsError);
        Assert.Equal("output text", block.Text);
    }

    // ──── Tool result: list form with text parts ────

    [Fact]
    public async Task ToolResult_ListContent_TextConcatenated()
    {
        var path = WriteJsonl("tr_list",
            Record("user", content: ToolResultListContent("toolu_2", false,
                new[] { TextPart("line 1"), TextPart("line 2") }))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        var block = t.Turns[0].Blocks[0];
        Assert.Equal(TranscriptBlockKind.ToolResult, block.Kind);
        Assert.Equal("line 1\nline 2", block.Text);
    }

    // ──── Tool result: is_error ────

    [Fact]
    public async Task ToolResult_IsError_Propagated()
    {
        var path = WriteJsonl("tr_err",
            Record("user", content: ToolResultContent("toolu_3", true, "\"error msg\""))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.True(t.Turns[0].Blocks[0].IsError);
    }

    // ──── Tool result: image parts -> Image blocks after the result ────

    [Fact]
    public async Task ToolResult_WithImages_ImageBlocksAfterResult()
    {
        var smallBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // tiny PNG header
        var path = WriteJsonl("tr_img",
            Record("user", content: ToolResultListContent("toolu_4", false,
                new[] { TextPart("result text"), ImagePart("image/png", smallBase64) }))
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Equal(2, t.Turns[0].Blocks.Count);
        Assert.Equal(TranscriptBlockKind.ToolResult, t.Turns[0].Blocks[0].Kind);
        Assert.Equal(TranscriptBlockKind.Image, t.Turns[0].Blocks[1].Kind);
        Assert.Equal("image/png", t.Turns[0].Blocks[1].ImageMediaType);
        Assert.Equal(1, t.Images);
    }

    // ──── User image: decoded under cap, placeholder over cap ────

    [Fact]
    public async Task UserImage_DecodedUnderCap()
    {
        var data = new byte[100];
        var b64 = Convert.ToBase64String(data);
        var path = WriteJsonl("img_small",
            Record("user", content: ImageContent("image/jpeg", b64))
        );

        var opts = new TranscriptReadOptions { DecodeImages = true, MaxImageBytes = 1000 };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        var block = t.Turns[0].Blocks[0];
        Assert.Equal(TranscriptBlockKind.Image, block.Kind);
        Assert.NotNull(block.ImageBytes);
        Assert.Equal(100, block.ImageByteLength);
        Assert.Equal("image/jpeg", block.ImageMediaType);
    }

    [Fact]
    public async Task UserImage_PlaceholderOverCap()
    {
        var data = new byte[500];
        var b64 = Convert.ToBase64String(data);
        var path = WriteJsonl("img_large",
            Record("user", content: ImageContent("image/png", b64))
        );

        // Set cap smaller than the encoded length.
        var opts = new TranscriptReadOptions { DecodeImages = true, MaxImageBytes = 100 };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        var block = t.Turns[0].Blocks[0];
        Assert.Null(block.ImageBytes);
        Assert.True(block.ImageByteLength > 0);
        Assert.Equal(1, t.Images);
    }

    // ──── isMeta -> System turn ────

    [Fact]
    public async Task IsMeta_BecomesSystemTurn()
    {
        var path = WriteJsonl("meta",
            $"{{\"type\":\"user\",\"isMeta\":true,\"message\":{{\"role\":\"user\",\"content\":\"Continue.\"}},\"uuid\":\"u1\",\"timestamp\":\"2026-01-01T00:00:00Z\"}}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Single(t.Turns);
        Assert.Equal(TranscriptRole.System, t.Turns[0].Role);
        Assert.Equal("meta", t.Turns[0].Blocks[0].Subtype);
        Assert.Equal("Continue.", t.Turns[0].Blocks[0].Text);
    }

    // ──── compact_boundary -> CompactBoundary ────

    [Fact]
    public async Task CompactBoundary_CreatesCompactBoundaryBlock()
    {
        var path = WriteJsonl("compact",
            "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"content\":\"Conversation compacted\",\"timestamp\":\"2026-01-01T12:00:00Z\"}"
        );

        var t = await Reader().ReadAsync(path, new TranscriptReadOptions { IncludeSystem = true }, null, CancellationToken.None);
        Assert.Single(t.Turns);
        Assert.Equal(TranscriptBlockKind.CompactBoundary, t.Turns[0].Blocks[0].Kind);
    }

    // ──── Other system subtypes -> SystemNote ────

    [Fact]
    public async Task SystemSubtype_OtherThanCompact_BecomesSystemNote()
    {
        var path = WriteJsonl("sysnote",
            "{\"type\":\"system\",\"subtype\":\"api_error\",\"content\":\"rate limited\",\"timestamp\":\"2026-01-01T12:00:00Z\"}"
        );

        var t = await Reader().ReadAsync(path, new TranscriptReadOptions { IncludeSystem = true }, null, CancellationToken.None);
        Assert.Equal(TranscriptBlockKind.SystemNote, t.Turns[0].Blocks[0].Kind);
        Assert.Equal("api_error", t.Turns[0].Blocks[0].Subtype);
        Assert.Equal("rate limited", t.Turns[0].Blocks[0].Text);
    }

    // ──── Attachments: only when IncludeAttachments ────

    [Fact]
    public async Task Attachments_SkippedByDefault()
    {
        var path = WriteJsonl("attach_off",
            "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_success\",\"content\":\"stuff\"},\"timestamp\":\"2026-01-01T00:00:00Z\"}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Empty(t.Turns);
    }

    [Fact]
    public async Task Attachments_IncludedWhenEnabled()
    {
        var path = WriteJsonl("attach_on",
            "{\"type\":\"attachment\",\"attachment\":{\"type\":\"hook_success\",\"content\":\"stuff\"},\"timestamp\":\"2026-01-01T00:00:00Z\"}"
        );

        var opts = new TranscriptReadOptions { IncludeAttachments = true };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        Assert.Single(t.Turns);
        Assert.Equal(TranscriptBlockKind.Attachment, t.Turns[0].Blocks[0].Kind);
        Assert.Equal("hook_success", t.Turns[0].Blocks[0].Subtype);
    }

    // ──── Sidechain: skipped unless included ────

    [Fact]
    public async Task Sidechain_SkippedByDefault()
    {
        var path = WriteJsonl("sc_off",
            Record("user", content: "\"hello\"", isSidechain: true)
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Empty(t.Turns);
    }

    [Fact]
    public async Task Sidechain_IncludedWhenEnabled_MarkedOnTurn()
    {
        var path = WriteJsonl("sc_on",
            Record("user", content: "\"hello\"", isSidechain: true)
        );

        var opts = new TranscriptReadOptions { IncludeSidechain = true };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        Assert.Single(t.Turns);
        Assert.True(t.Turns[0].IsSidechain);
    }

    // ──── Ignored record types not counted as errors ────

    [Fact]
    public async Task IgnoredTypes_NotCountedAsErrors()
    {
        var path = WriteJsonl("ignored",
            "{\"type\":\"mode\",\"mode\":\"normal\"}",
            "{\"type\":\"permission-mode\",\"permissionMode\":\"default\"}",
            "{\"type\":\"last-prompt\",\"leafUuid\":\"x\"}",
            "{\"type\":\"bridge-session\",\"sessionId\":\"x\"}",
            "{\"type\":\"atis-latch\",\"value\":true}",
            "{\"type\":\"queue-operation\",\"op\":\"x\"}",
            "{\"type\":\"frame-link\",\"link\":\"x\"}",
            "{\"type\":\"agent-name\",\"name\":\"x\"}",
            "{\"type\":\"file-history-snapshot\",\"snapshot\":{}}",
            "{\"type\":\"artifact-create\",\"data\":{}}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Equal(0, t.ParseErrors);
        Assert.Empty(t.Turns);
        Assert.Equal(10, t.Records);
    }

    // ──── Malformed line counted in ParseErrors ────

    [Fact]
    public async Task MalformedLine_CountedInParseErrors()
    {
        var path = WriteJsonl("malformed",
            "{this is not valid json}",
            Record("user", content: "\"good\"")
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Equal(1, t.ParseErrors);
        Assert.Single(t.Turns);
    }

    // ──── Last custom-title beats ai-title ────

    [Fact]
    public async Task Title_LastCustomTitleBeatsAiTitle()
    {
        var path = WriteJsonl("titles",
            "{\"type\":\"ai-title\",\"aiTitle\":\"AI says\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"User says 1\"}",
            "{\"type\":\"ai-title\",\"aiTitle\":\"AI says again\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"User says 2\"}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Equal("User says 2", t.Title);
    }

    [Fact]
    public async Task Title_AiTitleUsedWhenNoCustom()
    {
        var path = WriteJsonl("aititle",
            "{\"type\":\"ai-title\",\"aiTitle\":\"AI title\"}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Equal("AI title", t.Title);
    }

    // ──── Timestamps parsed as UTC ────

    [Fact]
    public async Task Timestamps_ParsedAsUtc()
    {
        var path = WriteJsonl("ts",
            $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"hi\"}},\"timestamp\":\"2026-03-15T14:30:00Z\",\"uuid\":\"u1\"}}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.NotNull(t.FirstTimestamp);
        Assert.Equal(DateTimeKind.Utc, t.FirstTimestamp!.Value.Kind);
        Assert.Equal(14, t.FirstTimestamp!.Value.Hour);
        Assert.Equal(30, t.FirstTimestamp!.Value.Minute);
        Assert.Equal(DateTimeKind.Utc, t.Turns[0].Timestamp!.Value.Kind);
    }

    // ──── MaxTurns sets Truncated ────

    [Fact]
    public async Task MaxTurns_StopsEarly_SetsTruncated()
    {
        var path = WriteJsonl("maxturns",
            Record("user", content: "\"one\""),
            Record("user", content: "\"two\""),
            Record("user", content: "\"three\"")
        );

        var opts = new TranscriptReadOptions { MaxTurns = 2 };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        Assert.Equal(2, t.Turns.Count);
        Assert.True(t.Truncated);
    }

    // ──── MaxTurns with assistant flush: no overshoot ────

    [Fact]
    public async Task MaxTurns_UserAssistantUser_StopsAtExactLimit()
    {
        // user -> assistant(msg1) -> user: with MaxTurns=2, the flush of the
        // assistant group after the second user record must not emit the user
        // turn that would push the count to 3.
        var path = WriteJsonl("maxturns_flush",
            Record("user", content: "\"one\""),
            Record("assistant", msg: AssistantMsg("msg_mt1", "m", new[] { TextBlock("reply") })),
            Record("user", content: "\"two\"")
        );

        var opts = new TranscriptReadOptions { MaxTurns = 2 };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        Assert.Equal(2, t.Turns.Count);
        Assert.True(t.Truncated);
    }

    [Fact]
    public async Task MaxTurns_AssistantGroupSwitch_NoExtraTurn()
    {
        // user -> assistant(msg1) -> assistant(msg2, different id): with
        // MaxTurns=2, flushing msg1 when msg2 arrives should not let the
        // done-label flush emit msg2 as a third turn.
        var path = WriteJsonl("maxturns_agroup",
            Record("user", content: "\"one\""),
            Record("assistant", msg: AssistantMsg("msg_a1", "m", new[] { TextBlock("r1") })),
            Record("assistant", msg: AssistantMsg("msg_a2", "m", new[] { TextBlock("r2") }))
        );

        var opts = new TranscriptReadOptions { MaxTurns = 2 };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        Assert.Equal(2, t.Turns.Count);
        Assert.True(t.Truncated);
    }

    // ──── FileShare.ReadWrite: can read while another handle writes ────

    [Fact]
    public async Task FileShare_ReadWhileAnotherHandleHoldsOpenForAppend()
    {
        var path = Path.Combine(_tempDir, "shared.jsonl");
        // Open the file for append, holding the handle.
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var line = Encoding.UTF8.GetBytes(Record("user", content: "\"concurrent\"") + "\n");
        writer.Write(line, 0, line.Length);
        writer.Flush();

        // Now read it while the writer handle is still open.
        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Single(t.Turns);
        Assert.Equal("concurrent", t.Turns[0].Blocks[0].Text);
    }

    // ──── Bytes and Records ────

    [Fact]
    public async Task BytesAndRecords_SetCorrectly()
    {
        var path = WriteJsonl("counts",
            Record("user", content: "\"a\""),
            Record("user", content: "\"b\"")
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Equal(2, t.Records);
        Assert.True(t.Bytes > 0);
        Assert.Equal(new FileInfo(path).Length, t.Bytes);
    }

    // ──── Turn.Index sequential ────

    [Fact]
    public async Task TurnIndex_Sequential()
    {
        var path = WriteJsonl("index",
            Record("user", content: "\"one\""),
            Record("assistant", msg: AssistantMsg("m1", "mdl", new[] { TextBlock("r1") })),
            Record("user", content: "\"two\"")
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Equal(3, t.Turns.Count);
        Assert.Equal(0, t.Turns[0].Index);
        Assert.Equal(1, t.Turns[1].Index);
        Assert.Equal(2, t.Turns[2].Index);
    }

    // ──── Metadata from first record ────

    [Fact]
    public async Task Metadata_FromFirstRecordCarryingThem()
    {
        var path = WriteJsonl("meta_fields",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"},\"uuid\":\"u1\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"cwd\":\"C:/work\",\"entrypoint\":\"claude-code\",\"version\":\"1.2.3\",\"gitBranch\":\"main\"}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi2\"},\"uuid\":\"u2\",\"timestamp\":\"2026-01-02T00:00:00Z\",\"cwd\":\"C:/other\",\"entrypoint\":\"desktop\"}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        // First record's metadata wins.
        Assert.Equal("C:/work", t.Cwd);
        Assert.Equal("claude-code", t.Entrypoint);
        Assert.Equal("1.2.3", t.Version);
        Assert.Equal("main", t.GitBranch);
    }

    // ──── Assistant grouping by requestId fallback ────

    [Fact]
    public async Task AssistantGrouping_FallbackToRequestId()
    {
        // Two records with no message.id but same requestId -> merge.
        var path = WriteJsonl("reqid_group",
            "{\"type\":\"assistant\",\"requestId\":\"req_1\",\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"part1\"}]},\"timestamp\":\"2026-01-01T00:00:00Z\",\"uuid\":\"u1\"}",
            "{\"type\":\"assistant\",\"requestId\":\"req_1\",\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"part2\"}]},\"timestamp\":\"2026-01-01T00:01:00Z\",\"uuid\":\"u2\"}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Single(t.Turns);
        Assert.Equal(2, t.Turns[0].Blocks.Count);
        Assert.Equal("part1", t.Turns[0].Blocks[0].Text);
        Assert.Equal("part2", t.Turns[0].Blocks[1].Text);
    }

    // ──── Unknown assistant block type -> SystemNote ────

    [Fact]
    public async Task UnknownAssistantBlockType_BecomesSystemNote()
    {
        var path = WriteJsonl("fallback_block",
            "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_fb\",\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"fallback\",\"text\":\"fallback content\"}]},\"timestamp\":\"2026-01-01T00:00:00Z\"}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        var block = t.Turns[0].Blocks[0];
        Assert.Equal(TranscriptBlockKind.SystemNote, block.Kind);
        Assert.Equal("fallback", block.Subtype);
        Assert.Equal("fallback content", block.Text);
    }

    // ──── User with string content (not array) ────

    [Fact]
    public async Task UserStringContent_SingleTextBlock()
    {
        var path = WriteJsonl("user_str",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"plain text\"},\"uuid\":\"u1\",\"timestamp\":\"2026-01-01T00:00:00Z\"}"
        );

        var t = await Reader().ReadAsync(path, DefaultOpts(), null, CancellationToken.None);
        Assert.Single(t.Turns[0].Blocks);
        Assert.Equal("plain text", t.Turns[0].Blocks[0].Text);
    }

    // ──── System records skipped when IncludeSystem=false ────

    [Fact]
    public async Task SystemRecords_SkippedWhenDisabled()
    {
        var path = WriteJsonl("sys_off",
            "{\"type\":\"system\",\"subtype\":\"api_error\",\"content\":\"err\",\"timestamp\":\"2026-01-01T00:00:00Z\"}"
        );

        var opts = new TranscriptReadOptions { IncludeSystem = false };
        var t = await Reader().ReadAsync(path, opts, null, CancellationToken.None);
        Assert.Empty(t.Turns);
    }

    // ══════════════════════════════════════════════════════════
    // EXPORTER TESTS
    // ══════════════════════════════════════════════════════════

    private static Transcript MakeSampleTranscript()
    {
        var t = new Transcript
        {
            SessionId = "test-session-1",
            Title = "Test Transcript",
            Cwd = "C:/project",
            Entrypoint = "claude-code",
            FirstTimestamp = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc),
            LastTimestamp = new DateTime(2026, 3, 15, 11, 0, 0, DateTimeKind.Utc),
            ToolCalls = 1,
            Images = 1,
            ThinkingBlocks = 2,
            ThinkingRedacted = 1,
            Records = 10,
            Bytes = 5000
        };

        // User turn.
        t.Turns.Add(new TranscriptTurn
        {
            Index = 0,
            Role = TranscriptRole.User,
            Timestamp = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc),
            Blocks = { new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "Hello" } }
        });

        // Assistant turn with thinking (redacted), text, tool_use.
        t.Turns.Add(new TranscriptTurn
        {
            Index = 1,
            Role = TranscriptRole.Assistant,
            Model = "claude-opus-5",
            Timestamp = new DateTime(2026, 3, 15, 10, 0, 5, DateTimeKind.Utc),
            Blocks =
            {
                new TranscriptBlock { Kind = TranscriptBlockKind.Thinking, ThinkingRedacted = true },
                new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "I will help." },
                new TranscriptBlock
                {
                    Kind = TranscriptBlockKind.ToolUse,
                    ToolName = "Bash",
                    ToolUseId = "toolu_1",
                    InputJson = "{\n  \"command\": \"ls\"\n}"
                }
            }
        });

        // User turn with tool result (long, for truncation test) + image.
        var imgBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        t.Turns.Add(new TranscriptTurn
        {
            Index = 2,
            Role = TranscriptRole.User,
            Timestamp = new DateTime(2026, 3, 15, 10, 0, 10, DateTimeKind.Utc),
            Blocks =
            {
                new TranscriptBlock
                {
                    Kind = TranscriptBlockKind.ToolResult,
                    ToolUseId = "toolu_1",
                    Text = new string('x', 5000) // Long tool result.
                },
                new TranscriptBlock
                {
                    Kind = TranscriptBlockKind.Image,
                    ImageBytes = imgBytes,
                    ImageMediaType = "image/png",
                    ImageByteLength = imgBytes.Length
                }
            }
        });

        // System: compact boundary.
        t.Turns.Add(new TranscriptTurn
        {
            Index = 3,
            Role = TranscriptRole.System,
            Timestamp = new DateTime(2026, 3, 15, 10, 5, 0, DateTimeKind.Utc),
            Blocks = { new TranscriptBlock { Kind = TranscriptBlockKind.CompactBoundary, Subtype = "compact_boundary" } }
        });

        return t;
    }

    [Fact]
    public void Markdown_ContainsHeaderFacts()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions());

        Assert.Contains("# Test Transcript", md);
        Assert.Contains("test-session-1", md);
        Assert.Contains("C:/project", md);
        Assert.Contains("claude-code", md);
        Assert.Contains("claude-opus-5", md);
        Assert.Contains("Turns:", md);
        Assert.Contains("Tool calls:", md);
        Assert.Contains("Images:", md);
        Assert.Contains("Thinking blocks:", md);
        Assert.Contains("1 redacted", md);
    }

    [Fact]
    public void Markdown_PerTurnSections()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions());

        Assert.Contains("## 0 · User", md);
        Assert.Contains("## 1 · Assistant · claude-opus-5", md);
        Assert.Contains("## 3 · System · compact_boundary", md);
    }

    [Fact]
    public void Markdown_ThinkingRedacted_ShowsMessage()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions { IncludeThinking = true });
        Assert.Contains("_thinking not stored (signature only)_", md);
    }

    [Fact]
    public void Markdown_HonoursIncludeThinking_False()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions { IncludeThinking = false });
        // "### Thinking" section heading should be absent; the "Thinking blocks:" header stat is fine.
        Assert.DoesNotContain("### Thinking", md);
        Assert.DoesNotContain("signature only", md);
    }

    [Fact]
    public void Markdown_HonoursIncludeToolCalls_False()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions { IncludeToolCalls = false });
        Assert.DoesNotContain("**Tool: Bash**", md);
    }

    [Fact]
    public void Markdown_HonoursIncludeToolResults_False()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions { IncludeToolResults = false });
        Assert.DoesNotContain("```text", md);
    }

    [Fact]
    public void Markdown_TruncatesToolResult_WithNote()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions { MaxToolResultChars = 100 });
        Assert.Contains("(truncated", md);
        Assert.Contains("chars)", md);
    }

    [Fact]
    public void Markdown_EmbedImage_DataUri()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions { EmbedImages = true });
        Assert.Contains("data:image/png;base64,", md);
    }

    [Fact]
    public void Markdown_NoEmbedImage_Placeholder()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions { EmbedImages = false });
        Assert.Contains("(image,", md);
        Assert.Contains("KB)", md);
    }

    [Fact]
    public void Markdown_CompactBoundary_HorizontalRule()
    {
        var t = MakeSampleTranscript();
        var md = Exporter().ToMarkdown(t, new ExportOptions());
        Assert.Contains("---", md);
        Assert.Contains("_context compacted_", md);
    }

    [Fact]
    public async Task ExportMarkdown_NoBom_WrittenViaTemp()
    {
        var t = MakeSampleTranscript();
        var outPath = Path.Combine(_tempDir, "out.md");
        await Exporter().ExportMarkdownAsync(t, outPath, new ExportOptions(), CancellationToken.None);

        Assert.True(File.Exists(outPath));
        var bytes = File.ReadAllBytes(outPath);
        // No BOM: first bytes are NOT 0xEF 0xBB 0xBF.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        // Contains valid UTF-8 content.
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Test Transcript", text);
    }

    // ──── HTML tests ────

    [Fact]
    public void Html_SelfContained_NoHttpSrcOrHref()
    {
        var t = MakeSampleTranscript();
        var html = BuildHtmlForTest(t, new ExportOptions());

        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Html_EscapesAngleBrackets()
    {
        var t = new Transcript
        {
            SessionId = "esc",
            Turns =
            {
                new TranscriptTurn
                {
                    Index = 0,
                    Role = TranscriptRole.User,
                    Blocks = { new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = "<script>alert('xss')</script> & \"quotes\"" } }
                }
            }
        };

        var html = BuildHtmlForTest(t, new ExportOptions());
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("<script>alert", html);
    }

    [Fact]
    public void Html_HasDetailsElements()
    {
        var t = MakeSampleTranscript();
        var html = BuildHtmlForTest(t, new ExportOptions());

        Assert.Contains("<details open>", html); // Thinking open by default.
        Assert.Contains("<details>", html);       // Tools closed by default.
        Assert.Contains("Expand all", html);
        Assert.Contains("Collapse all", html);
    }

    [Fact]
    public async Task ExportHtml_WritesFile()
    {
        var t = MakeSampleTranscript();
        var outPath = Path.Combine(_tempDir, "out.html");
        await Exporter().ExportHtmlAsync(t, outPath, new ExportOptions(), CancellationToken.None);

        Assert.True(File.Exists(outPath));
        var html = File.ReadAllText(outPath);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Test Transcript", html);
    }

    [Fact]
    public void Html_EmbedImage_DataUri()
    {
        var t = MakeSampleTranscript();
        var html = BuildHtmlForTest(t, new ExportOptions { EmbedImages = true });
        Assert.Contains("data:image/png;base64,", html);
    }

    // Helper to get HTML string directly.
    private static string BuildHtmlForTest(Transcript transcript, ExportOptions options)
    {
        // Use the exporter to get the Markdown, then use ExportHtmlAsync to get HTML.
        // Actually, we can access the BuildHtml method indirectly through ExportHtmlAsync,
        // but for unit testing let's just write to a temp file and read back.
        var tmpDir = Path.Combine(Path.GetTempPath(), "csb_html_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        var outPath = Path.Combine(tmpDir, "test.html");
        try
        {
            var exporter = new TranscriptExporter();
            exporter.ExportHtmlAsync(transcript, outPath, options, CancellationToken.None).GetAwaiter().GetResult();
            return File.ReadAllText(outPath);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════
    // JSONL record builders
    // ══════════════════════════════════════════════════════════

    private static string Record(string type, string? content = null, string? msg = null, bool isSidechain = false)
    {
        var sc = isSidechain ? "true" : "false";
        var uuid = Guid.NewGuid().ToString();
        if (msg != null)
            return $"{{\"type\":\"{type}\",\"isSidechain\":{sc},\"message\":{msg},\"uuid\":\"{uuid}\",\"timestamp\":\"2026-01-01T00:00:00Z\"}}";
        if (content != null)
            return $"{{\"type\":\"{type}\",\"isSidechain\":{sc},\"message\":{{\"role\":\"user\",\"content\":{content}}},\"uuid\":\"{uuid}\",\"timestamp\":\"2026-01-01T00:00:00Z\"}}";
        return $"{{\"type\":\"{type}\",\"isSidechain\":{sc},\"uuid\":\"{uuid}\",\"timestamp\":\"2026-01-01T00:00:00Z\"}}";
    }

    private static string AssistantMsg(string id, string model, string[] contentBlocks)
    {
        var content = string.Join(",", contentBlocks);
        return $"{{\"id\":\"{id}\",\"role\":\"assistant\",\"model\":\"{model}\",\"content\":[{content}]}}";
    }

    private static string ThinkingBlock(string text)
    {
        var escaped = JsonEncodedText.Encode(text).ToString();
        return $"{{\"type\":\"thinking\",\"thinking\":\"{escaped}\"}}";
    }

    private static string TextBlock(string text)
    {
        var escaped = JsonEncodedText.Encode(text).ToString();
        return $"{{\"type\":\"text\",\"text\":\"{escaped}\"}}";
    }

    private static string ToolUseBlock(string id, string name, string inputJson)
    {
        return $"{{\"type\":\"tool_use\",\"id\":\"{id}\",\"name\":\"{name}\",\"input\":{inputJson}}}";
    }

    private static string ToolResultContent(string toolUseId, bool isError, string content)
    {
        var errStr = isError ? "true" : "false";
        return $"[{{\"type\":\"tool_result\",\"tool_use_id\":\"{toolUseId}\",\"is_error\":{errStr},\"content\":{content}}}]";
    }

    private static string ToolResultListContent(string toolUseId, bool isError, string[] parts)
    {
        var errStr = isError ? "true" : "false";
        var contentArr = string.Join(",", parts);
        return $"[{{\"type\":\"tool_result\",\"tool_use_id\":\"{toolUseId}\",\"is_error\":{errStr},\"content\":[{contentArr}]}}]";
    }

    private static string TextPart(string text)
    {
        var escaped = JsonEncodedText.Encode(text).ToString();
        return $"{{\"type\":\"text\",\"text\":\"{escaped}\"}}";
    }

    private static string ImagePart(string mediaType, string base64)
    {
        return $"{{\"type\":\"image\",\"source\":{{\"type\":\"base64\",\"media_type\":\"{mediaType}\",\"data\":\"{base64}\"}}}}";
    }

    private static string ImageContent(string mediaType, string base64)
    {
        return $"[{{\"type\":\"image\",\"source\":{{\"type\":\"base64\",\"media_type\":\"{mediaType}\",\"data\":\"{base64}\"}}}}]";
    }
}
