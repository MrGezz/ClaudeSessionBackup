namespace ClaudeSessionBackup.Core.Model;

public enum TranscriptRole { User, Assistant, System }

public enum TranscriptBlockKind
{
    /// <summary>Human prompt text (user turn) or assistant prose in Markdown (assistant turn).</summary>
    Text,
    /// <summary>Assistant thinking. <see cref="TranscriptBlock.ThinkingRedacted"/> when the file holds only a signature.</summary>
    Thinking,
    /// <summary>A tool call: <see cref="TranscriptBlock.ToolName"/>, <see cref="TranscriptBlock.ToolUseId"/>, <see cref="TranscriptBlock.InputJson"/>.</summary>
    ToolUse,
    /// <summary>The result of a tool call, in a user turn: text and/or images, <see cref="TranscriptBlock.IsError"/>.</summary>
    ToolResult,
    /// <summary>An embedded image (base64 in the transcript).</summary>
    Image,
    /// <summary>A system record (hook summaries, API errors, local command output, meta prompts). <see cref="TranscriptBlock.Subtype"/>.</summary>
    SystemNote,
    /// <summary>Claude Code compacted the context here.</summary>
    CompactBoundary,
    /// <summary>An attachment record (reminders, diagnostics, edited-file notices). Off by default.</summary>
    Attachment,
}

/// <summary>One content block inside a turn.</summary>
public sealed partial class TranscriptBlock
{
    public TranscriptBlockKind Kind { get; set; }

    /// <summary>Text / Thinking / SystemNote / Attachment text; tool-result text.</summary>
    public string? Text { get; set; }

    /// <summary>Thinking block whose text was empty at the source (signature only). Nothing to recover.</summary>
    public bool ThinkingRedacted { get; set; }

    public string? ToolName { get; set; }
    public string? ToolUseId { get; set; }

    /// <summary>Tool input, pretty-printed JSON.</summary>
    public string? InputJson { get; set; }

    public bool IsError { get; set; }

    /// <summary>Decoded image bytes, or null when not decoded (options) or larger than the cap.</summary>
    public byte[]? ImageBytes { get; set; }
    public string? ImageMediaType { get; set; }

    /// <summary>Encoded size even when the image was not decoded.</summary>
    public int ImageByteLength { get; set; }

    /// <summary>System subtype (compact_boundary, api_error, local_command, stop_hook_summary, meta, ...) or attachment type.</summary>
    public string? Subtype { get; set; }
}

/// <summary>One turn of the conversation: a user prompt, one assistant message (all its blocks), a tool-result batch, or a system note.</summary>
public sealed partial class TranscriptTurn
{
    public int Index { get; set; }
    public TranscriptRole Role { get; set; }

    /// <summary>UTC.</summary>
    public DateTime? Timestamp { get; set; }
    public string? Uuid { get; set; }
    public string? RequestId { get; set; }
    public string? MessageId { get; set; }
    public string? Model { get; set; }
    public bool IsSidechain { get; set; }
    public List<TranscriptBlock> Blocks { get; set; } = new();

    /// <summary>A user turn that only carries tool results (no human text).</summary>
    public bool IsToolResultOnly => Role == TranscriptRole.User && Blocks.Count > 0 && Blocks.All(b => b.Kind == TranscriptBlockKind.ToolResult);
}

/// <summary>A parsed transcript (one &lt;sessionId&gt;.jsonl).</summary>
public sealed partial class Transcript
{
    public string SessionId { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>custom-title, else ai-title, else null (last record wins).</summary>
    public string? Title { get; set; }
    public string? Cwd { get; set; }
    public string? Version { get; set; }
    public string? GitBranch { get; set; }
    public string? Entrypoint { get; set; }
    public DateTime? FirstTimestamp { get; set; }
    public DateTime? LastTimestamp { get; set; }
    public List<TranscriptTurn> Turns { get; set; } = new();
    public int Records { get; set; }
    public int ParseErrors { get; set; }
    public int ThinkingBlocks { get; set; }
    public int ThinkingRedacted { get; set; }
    public int ToolCalls { get; set; }
    public int Images { get; set; }
    public long Bytes { get; set; }

    /// <summary>True when <see cref="TranscriptReadOptions.MaxTurns"/> stopped the read early.</summary>
    public bool Truncated { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed partial class TranscriptReadOptions
{
    public bool IncludeAttachments { get; set; }
    public bool IncludeSystem { get; set; } = true;
    public bool IncludeSidechain { get; set; }
    public bool DecodeImages { get; set; } = true;

    /// <summary>Images whose encoded size exceeds this are kept as placeholders (ImageBytes null, ImageByteLength set).</summary>
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>0 = all turns.</summary>
    public int MaxTurns { get; set; }
}

public sealed partial class ExportOptions
{
    public bool IncludeThinking { get; set; } = true;
    public bool IncludeToolCalls { get; set; } = true;
    public bool IncludeToolResults { get; set; } = true;
    public bool IncludeSystem { get; set; } = true;
    public bool IncludeAttachments { get; set; }

    /// <summary>Embed images as data URIs (Markdown / HTML); otherwise a "(image, N KB)" placeholder.</summary>
    public bool EmbedImages { get; set; } = true;

    /// <summary>Tool results longer than this are cut with a "(truncated N chars)" note. 0 = never cut.</summary>
    public int MaxToolResultChars { get; set; } = 4000;
}
