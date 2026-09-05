using System.Text;
using System.Web;
using ClaudeSessionBackup.Core.Model;

namespace ClaudeSessionBackup.Core.Transcripts;

/// <summary>
/// Writes a parsed transcript as a readable Markdown or standalone HTML document.
/// </summary>
/// <remarks>
/// Contract:
/// <list type="bullet">
/// <item>Markdown: a header (title or "(untitled)", session id, cwd, entrypoint, model(s), first/last timestamp in local time, counts: turns, tool calls, images, thinking blocks incl. redacted), then one section per turn in order: "## N · User · HH:mm:ss" / "## N · Assistant · model · HH:mm:ss" / "## N · System · subtype". User text verbatim (fenced when it contains Markdown that would otherwise render); assistant Text verbatim (it is Markdown); Thinking as a "### Thinking" block quote (or "_thinking not stored (signature only)_" when redacted) when IncludeThinking; ToolUse as "**Tool: name**" + a fenced json block of InputJson when IncludeToolCalls; ToolResult as a fenced block (language "text"), cut at MaxToolResultChars with "(truncated N chars)" appended, IsError marked, when IncludeToolResults; Image as a data-URI image when EmbedImages and bytes are present, else "(image, N KB)"; CompactBoundary as a horizontal rule with "context compacted"; SystemNote / Attachment as a block quote with the subtype, when IncludeSystem / IncludeAttachments. Written as UTF-8 without BOM, LF.</item>
/// <item>HTML: one self-contained file, no external resources, CM2 dark styling (teal charcoal surfaces, #00BCD4 accent, muted #5F7885), monospace for tool input/results, thinking and tool blocks inside &lt;details&gt; (thinking open by default, tools closed), images inline as data URIs, a sticky header with the same facts as the Markdown header, and a small script for "expand all / collapse all". HTML-escape every text. Same option semantics as Markdown.</item>
/// <item><see cref="ToMarkdown"/> returns the Markdown as a string (used by the app's copy action); the Async methods write to <c>outPath</c> via a temp file + move.</item>
/// </list>
/// </remarks>
public interface ITranscriptExporter
{
    string ToMarkdown(Transcript transcript, ExportOptions options);
    Task ExportMarkdownAsync(Transcript transcript, string outPath, ExportOptions options, CancellationToken cancellationToken);
    Task ExportHtmlAsync(Transcript transcript, string outPath, ExportOptions options, CancellationToken cancellationToken);
}

public sealed partial class TranscriptExporter : ITranscriptExporter
{
    public string ToMarkdown(Transcript transcript, ExportOptions options)
    {
        var sb = new StringBuilder();
        WriteMarkdownHeader(sb, transcript);
        sb.Append('\n');

        foreach (var turn in transcript.Turns)
        {
            WriteTurnMarkdown(sb, turn, options);
        }

        return sb.ToString();
    }

    public async Task ExportMarkdownAsync(Transcript transcript, string outPath,
        ExportOptions options, CancellationToken cancellationToken)
    {
        var md = await Task.Run(() => ToMarkdown(transcript, options), cancellationToken).ConfigureAwait(false);
        await WriteViaTempAsync(outPath, md, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportHtmlAsync(Transcript transcript, string outPath,
        ExportOptions options, CancellationToken cancellationToken)
    {
        var html = await Task.Run(() => BuildHtml(transcript, options), cancellationToken).ConfigureAwait(false);
        await WriteViaTempAsync(outPath, html, cancellationToken).ConfigureAwait(false);
    }

    // ──── Markdown ────

    private static void WriteMarkdownHeader(StringBuilder sb, Transcript transcript)
    {
        sb.Append("# ").Append(transcript.Title ?? "(untitled)").Append('\n');
        sb.Append('\n');
        sb.Append("- **Session:** ").Append(transcript.SessionId).Append('\n');
        if (transcript.Cwd != null)
            sb.Append("- **Cwd:** ").Append(transcript.Cwd).Append('\n');
        if (transcript.Entrypoint != null)
            sb.Append("- **Entrypoint:** ").Append(transcript.Entrypoint).Append('\n');

        var models = transcript.Turns
            .Where(t => t.Model != null)
            .Select(t => t.Model!)
            .Distinct()
            .ToList();
        if (models.Count > 0)
            sb.Append("- **Model(s):** ").AppendJoin(", ", models).Append('\n');

        if (transcript.FirstTimestamp != null)
            sb.Append("- **First:** ").Append(transcript.FirstTimestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
        if (transcript.LastTimestamp != null)
            sb.Append("- **Last:** ").Append(transcript.LastTimestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');

        sb.Append("- **Turns:** ").Append(transcript.Turns.Count).Append('\n');
        sb.Append("- **Tool calls:** ").Append(transcript.ToolCalls).Append('\n');
        sb.Append("- **Images:** ").Append(transcript.Images).Append('\n');
        sb.Append("- **Thinking blocks:** ").Append(transcript.ThinkingBlocks);
        if (transcript.ThinkingRedacted > 0)
            sb.Append(" (").Append(transcript.ThinkingRedacted).Append(" redacted)");
        sb.Append('\n');
    }

    private static void WriteTurnMarkdown(StringBuilder sb, TranscriptTurn turn, ExportOptions options)
    {
        var timeStr = turn.Timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "";
        switch (turn.Role)
        {
            case TranscriptRole.User:
                sb.Append("\n## ").Append(turn.Index).Append(" · User");
                if (timeStr.Length > 0) sb.Append(" · ").Append(timeStr);
                sb.Append('\n');
                break;
            case TranscriptRole.Assistant:
                sb.Append("\n## ").Append(turn.Index).Append(" · Assistant");
                if (turn.Model != null) sb.Append(" · ").Append(turn.Model);
                if (timeStr.Length > 0) sb.Append(" · ").Append(timeStr);
                sb.Append('\n');
                break;
            case TranscriptRole.System:
                var subtype = turn.Blocks.FirstOrDefault()?.Subtype ?? "system";
                sb.Append("\n## ").Append(turn.Index).Append(" · System · ").Append(subtype);
                if (timeStr.Length > 0) sb.Append(" · ").Append(timeStr);
                sb.Append('\n');
                break;
        }

        sb.Append('\n');

        foreach (var block in turn.Blocks)
        {
            switch (block.Kind)
            {
                case TranscriptBlockKind.Text:
                    if (turn.Role == TranscriptRole.User)
                    {
                        var text = block.Text ?? "";
                        if (LooksLikeMarkdown(text))
                        {
                            var fence = SafeFence(text);
                            sb.Append(fence).Append('\n').Append(text).Append('\n').Append(fence).Append("\n\n");
                        }
                        else
                            sb.Append(text).Append("\n\n");
                    }
                    else
                    {
                        sb.Append(block.Text ?? "").Append("\n\n");
                    }
                    break;

                case TranscriptBlockKind.Thinking:
                    if (!options.IncludeThinking) break;
                    sb.Append("### Thinking\n\n");
                    if (block.ThinkingRedacted)
                    {
                        sb.Append("_thinking not stored (signature only)_\n\n");
                    }
                    else
                    {
                        foreach (var ql in (block.Text ?? "").Split('\n'))
                            sb.Append("> ").Append(ql).Append('\n');
                        sb.Append('\n');
                    }
                    break;

                case TranscriptBlockKind.ToolUse:
                    if (!options.IncludeToolCalls) break;
                    sb.Append("**Tool: ").Append(block.ToolName ?? "unknown").Append("**\n\n");
                    sb.Append("```json\n").Append(block.InputJson ?? "{}").Append("\n```\n\n");
                    break;

                case TranscriptBlockKind.ToolResult:
                    if (!options.IncludeToolResults) break;
                    if (block.IsError)
                        sb.Append("**Error result:**\n\n");
                    var resultText = block.Text ?? "";
                    if (options.MaxToolResultChars > 0 && resultText.Length > options.MaxToolResultChars)
                    {
                        int truncated = resultText.Length - options.MaxToolResultChars;
                        resultText = resultText[..options.MaxToolResultChars] + $"\n(truncated {truncated} chars)";
                    }
                    sb.Append("```text\n").Append(resultText).Append("\n```\n\n");
                    break;

                case TranscriptBlockKind.Image:
                    WriteImageMarkdown(sb, block, options);
                    break;

                case TranscriptBlockKind.CompactBoundary:
                    if (!options.IncludeSystem) break;
                    sb.Append("---\n\n_context compacted_\n\n");
                    break;

                case TranscriptBlockKind.SystemNote:
                    if (!options.IncludeSystem) break;
                    sb.Append("> **").Append(block.Subtype ?? "system").Append("**\n");
                    if (block.Text != null)
                    {
                        foreach (var ql in block.Text.Split('\n'))
                            sb.Append("> ").Append(ql).Append('\n');
                    }
                    sb.Append('\n');
                    break;

                case TranscriptBlockKind.Attachment:
                    if (!options.IncludeAttachments) break;
                    sb.Append("> **attachment: ").Append(block.Subtype ?? "unknown").Append("**\n");
                    if (block.Text != null)
                    {
                        foreach (var ql in block.Text.Split('\n'))
                            sb.Append("> ").Append(ql).Append('\n');
                    }
                    sb.Append('\n');
                    break;
            }
        }
    }

    private static void WriteImageMarkdown(StringBuilder sb, TranscriptBlock block, ExportOptions options)
    {
        if (options.EmbedImages && block.ImageBytes != null && block.ImageMediaType != null)
        {
            var mimeType = SafeImageMimeTypes.Contains(block.ImageMediaType) ? block.ImageMediaType : "image/png";
            var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(block.ImageBytes)}";
            sb.Append("![image](").Append(dataUri).Append(")\n\n");
        }
        else
        {
            int kb = block.ImageByteLength / 1024;
            sb.Append("(image, ").Append(kb).Append(" KB)\n\n");
        }
    }

    private static bool LooksLikeMarkdown(string text)
    {
        return text.Contains('#') || text.Contains('*') || text.Contains('[') ||
               text.Contains('`') || text.Contains('>') || text.Contains("---");
    }

    /// <summary>
    /// Returns a backtick fence string whose length exceeds the longest
    /// consecutive backtick run in <paramref name="text"/>, so the outer
    /// fence cannot collide with embedded code fences.
    /// </summary>
    private static string SafeFence(string text)
    {
        int maxRun = 0, cur = 0;
        foreach (var ch in text)
        {
            if (ch == '`') { cur++; if (cur > maxRun) maxRun = cur; }
            else cur = 0;
        }
        int needed = Math.Max(3, maxRun + 1);
        return new string('`', needed);
    }

    // ──── HTML ────

    private static string BuildHtml(Transcript transcript, ExportOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(Esc(transcript.Title ?? "(untitled)")).Append("</title>\n");
        sb.Append("<style>\n");
        sb.Append(@":root {
  --bg: #0B0E14; --surface: #141820; --border: #1E2430;
  --text: #D0D8E0; --muted: #5F7885; --accent: #00BCD4;
  --error: #FF5252;
  --mono: 'Cascadia Mono','Fira Code','Consolas',monospace;
}
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--text);line-height:1.6}
.header{position:sticky;top:0;background:var(--surface);border-bottom:1px solid var(--border);padding:12px 20px;z-index:10}
.header h1{font-size:1.2em;color:var(--accent);margin-bottom:4px}
.header .meta{font-size:.82em;color:var(--muted)}
.header .meta span{margin-right:16px}
.controls{margin-top:6px}
.controls button{background:var(--border);color:var(--text);border:none;padding:4px 10px;border-radius:4px;cursor:pointer;font-size:.8em;margin-right:6px}
.controls button:hover{background:var(--accent);color:var(--bg)}
.turns{max-width:900px;margin:0 auto;padding:20px}
.turn{margin-bottom:24px;border-left:3px solid var(--border);padding-left:16px}
.turn.user{border-left-color:var(--accent)}
.turn.assistant{border-left-color:#4DB6AC}
.turn.system{border-left-color:var(--muted)}
.turn-header{font-size:.85em;color:var(--muted);margin-bottom:8px;font-weight:600}
.turn-header .role{color:var(--accent)}
.text-block{white-space:pre-wrap;word-break:break-word;margin-bottom:12px}
details{margin-bottom:12px}
details summary{cursor:pointer;color:var(--accent);font-size:.9em}
.tool-name{color:var(--accent);font-weight:600}
pre{background:var(--surface);border:1px solid var(--border);border-radius:6px;padding:12px;overflow-x:auto;font-family:var(--mono);font-size:.85em;margin:6px 0 12px 0;white-space:pre-wrap;word-break:break-word}
.error-marker{color:var(--error);font-weight:600}
.image-placeholder{color:var(--muted);font-style:italic}
img.embedded{max-width:100%;border-radius:6px;margin:8px 0}
.compact-boundary{border-top:1px dashed var(--muted);padding-top:8px;color:var(--muted);font-style:italic;margin:16px 0}
blockquote{border-left:2px solid var(--muted);padding-left:12px;color:var(--muted);margin:8px 0;white-space:pre-wrap;word-break:break-word}
.redacted{font-style:italic;color:var(--muted)}
");
        sb.Append("</style>\n</head>\n<body>\n");

        // Sticky header.
        sb.Append("<div class=\"header\">\n");
        sb.Append("  <h1>").Append(Esc(transcript.Title ?? "(untitled)")).Append("</h1>\n");
        sb.Append("  <div class=\"meta\">\n");
        sb.Append("    <span>Session: ").Append(Esc(transcript.SessionId)).Append("</span>\n");
        if (transcript.Cwd != null)
            sb.Append("    <span>Cwd: ").Append(Esc(transcript.Cwd)).Append("</span>\n");
        if (transcript.Entrypoint != null)
            sb.Append("    <span>Entrypoint: ").Append(Esc(transcript.Entrypoint)).Append("</span>\n");

        var models = transcript.Turns.Where(t => t.Model != null).Select(t => t.Model!).Distinct().ToList();
        if (models.Count > 0)
            sb.Append("    <span>Model(s): ").Append(Esc(string.Join(", ", models))).Append("</span>\n");

        if (transcript.FirstTimestamp != null)
            sb.Append("    <span>First: ").Append(transcript.FirstTimestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append("</span>\n");
        if (transcript.LastTimestamp != null)
            sb.Append("    <span>Last: ").Append(transcript.LastTimestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append("</span>\n");

        sb.Append("    <span>Turns: ").Append(transcript.Turns.Count).Append("</span>\n");
        sb.Append("    <span>Tool calls: ").Append(transcript.ToolCalls).Append("</span>\n");
        sb.Append("    <span>Images: ").Append(transcript.Images).Append("</span>\n");
        sb.Append("    <span>Thinking: ").Append(transcript.ThinkingBlocks);
        if (transcript.ThinkingRedacted > 0)
            sb.Append(" (").Append(transcript.ThinkingRedacted).Append(" redacted)");
        sb.Append("</span>\n");
        sb.Append("  </div>\n");
        sb.Append("  <div class=\"controls\">\n");
        sb.Append("    <button onclick=\"toggleAll(true)\">Expand all</button>\n");
        sb.Append("    <button onclick=\"toggleAll(false)\">Collapse all</button>\n");
        sb.Append("  </div>\n");
        sb.Append("</div>\n\n");

        sb.Append("<div class=\"turns\">\n");
        foreach (var turn in transcript.Turns)
            WriteTurnHtml(sb, turn, options);
        sb.Append("</div>\n\n");

        sb.Append("<script>\nfunction toggleAll(open){document.querySelectorAll('details').forEach(d=>d.open=open);}\n</script>\n");
        sb.Append("</body>\n</html>\n");

        return sb.ToString();
    }

    private static void WriteTurnHtml(StringBuilder sb, TranscriptTurn turn, ExportOptions options)
    {
        var roleClass = turn.Role.ToString().ToLowerInvariant();
        sb.Append("<div class=\"turn ").Append(roleClass).Append("\">\n");

        var timeStr = turn.Timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "";
        sb.Append("  <div class=\"turn-header\">");
        sb.Append("<span class=\"role\">").Append(turn.Index).Append(" · ").Append(turn.Role).Append("</span>");
        if (turn.Role == TranscriptRole.Assistant && turn.Model != null)
            sb.Append(" · ").Append(Esc(turn.Model));
        if (turn.Role == TranscriptRole.System)
        {
            var subtype = turn.Blocks.FirstOrDefault()?.Subtype ?? "system";
            sb.Append(" · ").Append(Esc(subtype));
        }
        if (timeStr.Length > 0) sb.Append(" · ").Append(timeStr);
        sb.Append("</div>\n");

        foreach (var block in turn.Blocks)
        {
            switch (block.Kind)
            {
                case TranscriptBlockKind.Text:
                    sb.Append("  <div class=\"text-block\">").Append(Esc(block.Text ?? "")).Append("</div>\n");
                    break;

                case TranscriptBlockKind.Thinking:
                    if (!options.IncludeThinking) break;
                    sb.Append("  <details open>\n    <summary>Thinking</summary>\n");
                    if (block.ThinkingRedacted)
                        sb.Append("    <p class=\"redacted\">thinking not stored (signature only)</p>\n");
                    else
                        sb.Append("    <blockquote>").Append(Esc(block.Text ?? "")).Append("</blockquote>\n");
                    sb.Append("  </details>\n");
                    break;

                case TranscriptBlockKind.ToolUse:
                    if (!options.IncludeToolCalls) break;
                    sb.Append("  <details>\n    <summary><span class=\"tool-name\">Tool: ")
                      .Append(Esc(block.ToolName ?? "unknown")).Append("</span></summary>\n");
                    sb.Append("    <pre>").Append(Esc(block.InputJson ?? "{}")).Append("</pre>\n");
                    sb.Append("  </details>\n");
                    break;

                case TranscriptBlockKind.ToolResult:
                    if (!options.IncludeToolResults) break;
                    if (block.IsError)
                        sb.Append("  <span class=\"error-marker\">Error result:</span>\n");
                    var rt = block.Text ?? "";
                    if (options.MaxToolResultChars > 0 && rt.Length > options.MaxToolResultChars)
                    {
                        int cut = rt.Length - options.MaxToolResultChars;
                        rt = rt[..options.MaxToolResultChars] + $"\n(truncated {cut} chars)";
                    }
                    sb.Append("  <pre>").Append(Esc(rt)).Append("</pre>\n");
                    break;

                case TranscriptBlockKind.Image:
                    WriteImageHtml(sb, block, options);
                    break;

                case TranscriptBlockKind.CompactBoundary:
                    if (!options.IncludeSystem) break;
                    sb.Append("  <div class=\"compact-boundary\">context compacted</div>\n");
                    break;

                case TranscriptBlockKind.SystemNote:
                    if (!options.IncludeSystem) break;
                    sb.Append("  <blockquote><strong>").Append(Esc(block.Subtype ?? "system"))
                      .Append("</strong><br>").Append(Esc(block.Text ?? "")).Append("</blockquote>\n");
                    break;

                case TranscriptBlockKind.Attachment:
                    if (!options.IncludeAttachments) break;
                    sb.Append("  <blockquote><strong>attachment: ").Append(Esc(block.Subtype ?? "unknown"))
                      .Append("</strong><br>").Append(Esc(block.Text ?? "")).Append("</blockquote>\n");
                    break;
            }
        }

        sb.Append("</div>\n");
    }

    /// <summary>Allowlist for image MIME types that may appear in a data URI.</summary>
    private static readonly HashSet<string> SafeImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml", "image/bmp"
    };

    private static void WriteImageHtml(StringBuilder sb, TranscriptBlock block, ExportOptions options)
    {
        if (options.EmbedImages && block.ImageBytes != null && block.ImageMediaType != null)
        {
            // Sanitize the MIME type to prevent attribute injection via crafted media_type.
            var mimeType = SafeImageMimeTypes.Contains(block.ImageMediaType) ? block.ImageMediaType : "image/png";
            var dataUri = $"data:{Esc(mimeType)};base64,{Convert.ToBase64String(block.ImageBytes)}";
            sb.Append("  <img class=\"embedded\" src=\"").Append(dataUri).Append("\" alt=\"image\">\n");
        }
        else
        {
            int kb = block.ImageByteLength / 1024;
            sb.Append("  <span class=\"image-placeholder\">(image, ").Append(kb).Append(" KB)</span>\n");
        }
    }

    // ──── Utilities ────

    private static string Esc(string text) => HttpUtility.HtmlEncode(text);

    /// <summary>
    /// Writes content to a temp file then moves into place (atomic on same volume).
    /// UTF-8 without BOM, LF line endings.
    /// </summary>
    private static async Task WriteViaTempAsync(string outPath, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var tmpPath = outPath + ".partial";
        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(tmpPath, content, utf8NoBom, cancellationToken).ConfigureAwait(false);
            File.Move(tmpPath, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* best effort cleanup */ }
            throw;
        }
    }
}
