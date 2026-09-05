using System.Text.Json;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Transcripts;

/// <summary>
/// Reads a Claude Code transcript (&lt;sessionId&gt;.jsonl) into turns and blocks for display and export.
/// </summary>
/// <remarks>
/// Behavioural contract (measured on the 145 transcripts on this machine, 2026-09-05):
/// <list type="bullet">
/// <item>Read-only, streaming (line by line, never the whole file in memory), opened with FileShare.ReadWrite because Claude Code may be appending. Each line starting with "{" is parsed as JSON; a line that fails to parse counts in ParseErrors and is skipped. Non-object lines are ignored. Never throws for content; throws only for a missing/unreadable file.</item>
/// <item>Record types and what they become: "user" -> a User turn: message.content string -> one Text block; list -> text -> Text, image -> Image (source.data base64 decoded when DecodeImages and the encoded length is within MaxImageBytes; otherwise ImageBytes null with ImageByteLength set; ImageMediaType from source.media_type), tool_result -> ToolResult (ToolUseId = tool_use_id, IsError = is_error, Text = content when a string, else the concatenated text parts; image parts become Image blocks placed right after the ToolResult block). A user record with isMeta:true becomes a System turn with one SystemNote block (Subtype "meta").</item>
/// <item>"assistant" -> consecutive records sharing message.id (fallback: requestId) merge into ONE Assistant turn, blocks appended in record order: thinking -> Thinking (ThinkingRedacted when the thinking text is empty or whitespace; counted in ThinkingBlocks and ThinkingRedacted), text -> Text (Markdown), tool_use -> ToolUse (ToolName = name, ToolUseId = id, InputJson = input pretty-printed with indentation; counted in ToolCalls), anything else (e.g. "fallback") -> SystemNote with Subtype = the block type. Model from message.model; RequestId / MessageId / Uuid / Timestamp from the first record of the group.</item>
/// <item>"system" -> when IncludeSystem: subtype compact_boundary -> a System turn with one CompactBoundary block; other subtypes -> a System turn with one SystemNote block (Subtype = subtype, Text = content). "attachment" -> when IncludeAttachments: a System turn with one Attachment block (Subtype = attachment.type, Text = a short JSON of the attachment without its own nested blobs, max 2,000 chars).</item>
/// <item>"custom-title" (customTitle) and "ai-title" (aiTitle): the LAST custom-title wins over the last ai-title for <see cref="Transcript.Title"/>. Cwd, Version, GitBranch, Entrypoint from the first record carrying them. First/LastTimestamp from record "timestamp" values (ISO 8601 with Z, parsed as UTC).</item>
/// <item>Records with isSidechain:true are skipped unless IncludeSidechain (then their turns carry IsSidechain = true). Record types last-prompt, bridge-session, atis-latch, queue-operation, mode, frame-link, agent-name, permission-mode, file-history-*, artifact-* are ignored and not counted as errors.</item>
/// <item>Turn.Index is sequential over emitted turns starting at 0. MaxTurns &gt; 0 stops after that many turns and sets Truncated. Records counts every parsed object; Bytes is the file length.</item>
/// <item>Performance: the largest transcript here is 87 MB / 18,430 records with 300+ images; a full read with images decoded must stay under ~10 s and not hold more than the decoded images in memory. Report progress as records parsed.</item>
/// </list>
/// </remarks>
public interface ITranscriptReader
{
    Task<Transcript> ReadAsync(string path, TranscriptReadOptions options, IProgress<int>? recordsProgress, CancellationToken cancellationToken);
}

public sealed partial class TranscriptReader : ITranscriptReader
{
    /// <summary>Record types that are silently ignored (not counted as parse errors).</summary>
    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "last-prompt", "bridge-session", "atis-latch", "queue-operation",
        "mode", "frame-link", "agent-name", "permission-mode"
    };

    /// <summary>Mutable state carried across lines while grouping assistant records.</summary>
    private sealed class AssistantGroup
    {
        public string GroupKey = "";
        public TranscriptTurn Turn = null!;
    }

    public async Task<Transcript> ReadAsync(
        string path,
        TranscriptReadOptions options,
        IProgress<int>? recordsProgress,
        CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        var fileInfo = new FileInfo(path);

        var transcript = new Transcript
        {
            SessionId = sessionId,
            Path = path,
            Bytes = fileInfo.Length
        };

        string? lastCustomTitle = null;
        string? lastAiTitle = null;
        int turnIndex = 0;
        AssistantGroup? assistantGroup = null;

        // FileShare.ReadWrite: Claude Code may be appending while we read.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 65536);
        using var reader = new StreamReader(fs, encoding: System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);

        int recordCount = 0;
        const int progressInterval = 500;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Non-object lines are silently ignored (blank, comments, etc.).
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                continue;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                transcript.ParseErrors++;
                continue;
            }

            using (doc)
            {
                recordCount++;
                transcript.Records = recordCount;

                if (recordCount % progressInterval == 0)
                    recordsProgress?.Report(recordCount);

                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                    continue;

                var recordType = typeEl.GetString() ?? "";

                // Track timestamps from every record.
                TrackTimestamp(root, transcript);

                // Extract metadata (cwd, version, etc.) from the first record that carries them.
                ExtractMetadata(root, transcript);

                // Sidechain: skip unless IncludeSidechain, but still process titles.
                bool isSidechain = root.TryGetProperty("isSidechain", out var scEl) && scEl.ValueKind == JsonValueKind.True;
                if (isSidechain && !options.IncludeSidechain)
                {
                    ProcessTitles(root, recordType, ref lastCustomTitle, ref lastAiTitle);
                    continue;
                }

                // Titles are extracted regardless (they produce no turns).
                ProcessTitles(root, recordType, ref lastCustomTitle, ref lastAiTitle);

                switch (recordType)
                {
                    case "user":
                        FlushAssistantGroup(transcript, ref assistantGroup, ref turnIndex);
                        if (options.MaxTurns > 0 && turnIndex >= options.MaxTurns)
                        { transcript.Truncated = true; goto done; }
                        EmitUserTurn(root, options, transcript, isSidechain, ref turnIndex);
                        if (options.MaxTurns > 0 && turnIndex >= options.MaxTurns)
                        { transcript.Truncated = true; goto done; }
                        break;

                    case "assistant":
                        AppendAssistantRecord(root, options, transcript, isSidechain, ref assistantGroup, ref turnIndex);
                        if (options.MaxTurns > 0 && turnIndex >= options.MaxTurns)
                        { transcript.Truncated = true; goto done; }
                        break;

                    case "system":
                        FlushAssistantGroup(transcript, ref assistantGroup, ref turnIndex);
                        if (options.IncludeSystem)
                        {
                            EmitSystemTurn(root, transcript, isSidechain, ref turnIndex);
                            if (options.MaxTurns > 0 && turnIndex >= options.MaxTurns)
                            { transcript.Truncated = true; goto done; }
                        }
                        break;

                    case "attachment":
                        FlushAssistantGroup(transcript, ref assistantGroup, ref turnIndex);
                        if (options.IncludeAttachments)
                        {
                            EmitAttachmentTurn(root, transcript, isSidechain, ref turnIndex);
                            if (options.MaxTurns > 0 && turnIndex >= options.MaxTurns)
                            { transcript.Truncated = true; goto done; }
                        }
                        break;

                    case "custom-title":
                    case "ai-title":
                        break; // Already handled.

                    default:
                        // file-history-*, artifact-* are ignored by prefix.
                        // Other IgnoredTypes are also silently skipped.
                        // Unknown types not in either list are still not counted as errors.
                        break;
                }
            }
        }

    done:
        if (!transcript.Truncated)
            FlushAssistantGroup(transcript, ref assistantGroup, ref turnIndex);
        else
            assistantGroup = null;

        transcript.Title = lastCustomTitle ?? lastAiTitle;
        progressReport(recordsProgress, recordCount);

        return transcript;
    }

    private static void progressReport(IProgress<int>? progress, int count)
    {
        progress?.Report(count);
    }

    private static void TrackTimestamp(JsonElement root, Transcript transcript)
    {
        if (!root.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String)
            return;
        var tsStr = tsEl.GetString();
        if (tsStr == null) return;
        if (!DateTime.TryParse(tsStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
            return;
        ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        if (transcript.FirstTimestamp == null || ts < transcript.FirstTimestamp)
            transcript.FirstTimestamp = ts;
        if (transcript.LastTimestamp == null || ts > transcript.LastTimestamp)
            transcript.LastTimestamp = ts;
    }

    private static void ExtractMetadata(JsonElement root, Transcript transcript)
    {
        if (transcript.Cwd == null && root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
            transcript.Cwd = cwdEl.GetString();
        if (transcript.Version == null && root.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.String)
            transcript.Version = verEl.GetString();
        if (transcript.GitBranch == null && root.TryGetProperty("gitBranch", out var gbEl) && gbEl.ValueKind == JsonValueKind.String)
            transcript.GitBranch = gbEl.GetString();
        if (transcript.Entrypoint == null && root.TryGetProperty("entrypoint", out var epEl) && epEl.ValueKind == JsonValueKind.String)
            transcript.Entrypoint = epEl.GetString();
    }

    private static void ProcessTitles(JsonElement root, string recordType, ref string? lastCustomTitle, ref string? lastAiTitle)
    {
        if (recordType == "custom-title")
        {
            if (root.TryGetProperty("customTitle", out var ctEl) && ctEl.ValueKind == JsonValueKind.String)
                lastCustomTitle = ctEl.GetString();
        }
        else if (recordType == "ai-title")
        {
            if (root.TryGetProperty("aiTitle", out var atEl) && atEl.ValueKind == JsonValueKind.String)
                lastAiTitle = atEl.GetString();
        }
    }

    // ──── User turns ────

    private static void EmitUserTurn(JsonElement root, TranscriptReadOptions options,
        Transcript transcript, bool isSidechain, ref int turnIndex)
    {
        bool isMeta = root.TryGetProperty("isMeta", out var metaEl) && metaEl.ValueKind == JsonValueKind.True;

        var turn = new TranscriptTurn
        {
            Index = turnIndex,
            Role = isMeta ? TranscriptRole.System : TranscriptRole.User,
            IsSidechain = isSidechain
        };
        SetTurnTimestampAndUuid(root, turn);

        if (isMeta)
        {
            var text = ExtractContentAsPlainText(root);
            turn.Blocks.Add(new TranscriptBlock { Kind = TranscriptBlockKind.SystemNote, Subtype = "meta", Text = text });
            transcript.Turns.Add(turn);
            turnIndex++;
            return;
        }

        if (!root.TryGetProperty("message", out var msgEl) ||
            !msgEl.TryGetProperty("content", out var contentEl))
        {
            transcript.Turns.Add(turn);
            turnIndex++;
            return;
        }

        if (contentEl.ValueKind == JsonValueKind.String)
        {
            turn.Blocks.Add(new TranscriptBlock { Kind = TranscriptBlockKind.Text, Text = contentEl.GetString() ?? "" });
        }
        else if (contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentEl.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var btEl) || btEl.ValueKind != JsonValueKind.String)
                    continue;
                var blockType = btEl.GetString() ?? "";
                switch (blockType)
                {
                    case "text":
                        turn.Blocks.Add(new TranscriptBlock
                        {
                            Kind = TranscriptBlockKind.Text,
                            Text = GetStringProp(block, "text") ?? ""
                        });
                        break;
                    case "image":
                        AddImageBlock(block, options, turn, transcript);
                        break;
                    case "tool_result":
                        AddToolResultBlock(block, options, turn, transcript);
                        break;
                }
            }
        }

        transcript.Turns.Add(turn);
        turnIndex++;
    }

    // ──── Assistant turns (grouped by message.id) ────

    private static void AppendAssistantRecord(JsonElement root, TranscriptReadOptions options,
        Transcript transcript, bool isSidechain,
        ref AssistantGroup? group, ref int turnIndex)
    {
        if (!root.TryGetProperty("message", out var msgEl))
            return;

        string? messageId = GetStringProp(msgEl, "id");
        string? requestId = GetStringProp(root, "requestId");
        string groupKey = messageId ?? requestId ?? Guid.NewGuid().ToString();

        if (group == null || group.GroupKey != groupKey)
        {
            // New message: flush the previous group, start fresh.
            FlushAssistantGroup(transcript, ref group, ref turnIndex);

            var turn = new TranscriptTurn
            {
                Role = TranscriptRole.Assistant,
                IsSidechain = isSidechain,
                MessageId = messageId,
                RequestId = requestId,
                Model = GetStringProp(msgEl, "model")
            };
            SetTurnTimestampAndUuid(root, turn);

            group = new AssistantGroup { GroupKey = groupKey, Turn = turn };
        }

        // Append content blocks.
        if (!msgEl.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in contentEl.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var btEl) || btEl.ValueKind != JsonValueKind.String)
                continue;
            var blockType = btEl.GetString() ?? "";
            switch (blockType)
            {
                case "thinking":
                {
                    transcript.ThinkingBlocks++;
                    var thinkingText = GetStringProp(block, "thinking") ?? "";
                    bool redacted = string.IsNullOrWhiteSpace(thinkingText);
                    if (redacted) transcript.ThinkingRedacted++;
                    group.Turn.Blocks.Add(new TranscriptBlock
                    {
                        Kind = TranscriptBlockKind.Thinking,
                        Text = redacted ? null : thinkingText,
                        ThinkingRedacted = redacted
                    });
                    break;
                }
                case "text":
                    group.Turn.Blocks.Add(new TranscriptBlock
                    {
                        Kind = TranscriptBlockKind.Text,
                        Text = GetStringProp(block, "text") ?? ""
                    });
                    break;
                case "tool_use":
                {
                    transcript.ToolCalls++;
                    string? inputJson = null;
                    if (block.TryGetProperty("input", out var inputEl))
                        inputJson = JsonSerializer.Serialize(inputEl, s_indentedOptions).Replace("\r\n", "\n");
                    group.Turn.Blocks.Add(new TranscriptBlock
                    {
                        Kind = TranscriptBlockKind.ToolUse,
                        ToolName = GetStringProp(block, "name"),
                        ToolUseId = GetStringProp(block, "id"),
                        InputJson = inputJson
                    });
                    break;
                }
                default:
                    // Unknown assistant block types -> SystemNote with Subtype = the type string.
                    group.Turn.Blocks.Add(new TranscriptBlock
                    {
                        Kind = TranscriptBlockKind.SystemNote,
                        Subtype = blockType,
                        Text = GetStringProp(block, "text")
                    });
                    break;
            }
        }
    }

    private static void FlushAssistantGroup(Transcript transcript, ref AssistantGroup? group, ref int turnIndex)
    {
        if (group == null) return;
        group.Turn.Index = turnIndex;
        transcript.Turns.Add(group.Turn);
        turnIndex++;
        group = null;
    }

    // ──── System turns ────

    private static void EmitSystemTurn(JsonElement root, Transcript transcript,
        bool isSidechain, ref int turnIndex)
    {
        var subtype = GetStringProp(root, "subtype") ?? "";
        var content = GetStringProp(root, "content");

        var turn = new TranscriptTurn
        {
            Index = turnIndex,
            Role = TranscriptRole.System,
            IsSidechain = isSidechain
        };
        SetTurnTimestampAndUuid(root, turn);

        if (subtype == "compact_boundary")
        {
            turn.Blocks.Add(new TranscriptBlock { Kind = TranscriptBlockKind.CompactBoundary, Subtype = subtype });
        }
        else
        {
            turn.Blocks.Add(new TranscriptBlock { Kind = TranscriptBlockKind.SystemNote, Subtype = subtype, Text = content });
        }

        transcript.Turns.Add(turn);
        turnIndex++;
    }

    // ──── Attachment turns ────

    private static void EmitAttachmentTurn(JsonElement root, Transcript transcript,
        bool isSidechain, ref int turnIndex)
    {
        var turn = new TranscriptTurn
        {
            Index = turnIndex,
            Role = TranscriptRole.System,
            IsSidechain = isSidechain
        };
        SetTurnTimestampAndUuid(root, turn);

        string? attachType = null;
        string? attachText = null;
        if (root.TryGetProperty("attachment", out var attEl))
        {
            attachType = GetStringProp(attEl, "type");

            // A short JSON of the attachment without nested blobs, max 2,000 chars.
            // Clone to a dictionary, strip large nested objects.
            try
            {
                var summary = new Dictionary<string, object?>();
                foreach (var prop in attEl.EnumerateObject())
                {
                    // Skip properties that are large nested blobs.
                    if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        // Keep small ones, skip large nested blobs.
                        var serialized = prop.Value.GetRawText();
                        if (serialized.Length > 500) continue;
                    }
                    summary[prop.Name] = prop.Value.Clone();
                }
                attachText = JsonSerializer.Serialize(summary, s_indentedOptions).Replace("\r\n", "\n");
                if (attachText.Length > 2000)
                    attachText = attachText[..2000];
            }
            catch
            {
                attachText = attEl.GetRawText();
                if (attachText.Length > 2000)
                    attachText = attachText[..2000];
            }
        }

        turn.Blocks.Add(new TranscriptBlock
        {
            Kind = TranscriptBlockKind.Attachment,
            Subtype = attachType,
            Text = attachText
        });

        transcript.Turns.Add(turn);
        turnIndex++;
    }

    // ──── Image / tool-result helpers ────

    private static void AddImageBlock(JsonElement block, TranscriptReadOptions options,
        TranscriptTurn turn, Transcript transcript)
    {
        var imgBlock = new TranscriptBlock { Kind = TranscriptBlockKind.Image };

        if (block.TryGetProperty("source", out var srcEl))
        {
            imgBlock.ImageMediaType = GetStringProp(srcEl, "media_type");
            if (srcEl.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
            {
                var base64 = dataEl.GetString() ?? "";
                int encodedLen = base64.Length;
                // Approximate decoded size: 3 bytes per 4 base64 chars.
                int approxBytes = (encodedLen * 3) / 4;
                imgBlock.ImageByteLength = approxBytes;

                if (options.DecodeImages && encodedLen <= options.MaxImageBytes)
                {
                    try
                    {
                        imgBlock.ImageBytes = Convert.FromBase64String(base64);
                        imgBlock.ImageByteLength = imgBlock.ImageBytes.Length;
                    }
                    catch (FormatException)
                    {
                        // Bad base64: keep placeholder with approximate size.
                    }
                }
            }
        }

        transcript.Images++;
        turn.Blocks.Add(imgBlock);
    }

    private static void AddToolResultBlock(JsonElement block, TranscriptReadOptions options,
        TranscriptTurn turn, Transcript transcript)
    {
        var resultBlock = new TranscriptBlock
        {
            Kind = TranscriptBlockKind.ToolResult,
            ToolUseId = GetStringProp(block, "tool_use_id"),
            IsError = block.TryGetProperty("is_error", out var errEl) && errEl.ValueKind == JsonValueKind.True
        };

        if (!block.TryGetProperty("content", out var contentEl))
        {
            turn.Blocks.Add(resultBlock);
            return;
        }

        if (contentEl.ValueKind == JsonValueKind.String)
        {
            resultBlock.Text = contentEl.GetString() ?? "";
            turn.Blocks.Add(resultBlock);
            return;
        }

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            var textParts = new List<string>();
            var trailingImages = new List<TranscriptBlock>();

            foreach (var part in contentEl.EnumerateArray())
            {
                var partType = GetStringProp(part, "type") ?? "";
                if (partType == "text")
                {
                    var t = GetStringProp(part, "text") ?? "";
                    textParts.Add(t);
                }
                else if (partType == "image")
                {
                    var imgBlock = new TranscriptBlock { Kind = TranscriptBlockKind.Image };
                    if (part.TryGetProperty("source", out var srcEl))
                    {
                        imgBlock.ImageMediaType = GetStringProp(srcEl, "media_type");
                        if (srcEl.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
                        {
                            var b64 = dataEl.GetString() ?? "";
                            int encodedLen = b64.Length;
                            imgBlock.ImageByteLength = (encodedLen * 3) / 4;
                            if (options.DecodeImages && encodedLen <= options.MaxImageBytes)
                            {
                                try
                                {
                                    imgBlock.ImageBytes = Convert.FromBase64String(b64);
                                    imgBlock.ImageByteLength = imgBlock.ImageBytes.Length;
                                }
                                catch (FormatException) { }
                            }
                        }
                    }
                    transcript.Images++;
                    trailingImages.Add(imgBlock);
                }
            }

            resultBlock.Text = string.Join("\n", textParts);
            turn.Blocks.Add(resultBlock);
            // Image blocks placed right after the ToolResult block.
            foreach (var img in trailingImages)
                turn.Blocks.Add(img);
            return;
        }

        turn.Blocks.Add(resultBlock);
    }

    // ──── Utilities ────

    private static readonly JsonSerializerOptions s_indentedOptions = new() { WriteIndented = true };

    private static string? GetStringProp(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static void SetTurnTimestampAndUuid(JsonElement root, TranscriptTurn turn)
    {
        if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String)
        {
            var tsStr = tsEl.GetString();
            if (tsStr != null && DateTime.TryParse(tsStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
                turn.Timestamp = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        }
        turn.Uuid = GetStringProp(root, "uuid");
    }

    private static string ExtractContentAsPlainText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msgEl) ||
            !msgEl.TryGetProperty("content", out var contentEl))
            return "";

        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString() ?? "";

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in contentEl.EnumerateArray())
            {
                if (GetStringProp(block, "type") == "text")
                {
                    var t = GetStringProp(block, "text") ?? "";
                    parts.Add(t);
                }
            }
            return string.Join("\n", parts);
        }
        return "";
    }
}
