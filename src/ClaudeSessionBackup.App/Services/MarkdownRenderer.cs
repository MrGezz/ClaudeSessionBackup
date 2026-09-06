using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// Disambiguate Block between System.Windows.Documents and Markdig.Syntax.
using WpfBlock = System.Windows.Documents.Block;
using MdBlock = Markdig.Syntax.Block;

// UseWindowsForms (for NotifyIcon) brings System.Drawing into implicit usings.
// System.Drawing.FontFamily then collides with System.Windows.Media.FontFamily.
using FontFamily = System.Windows.Media.FontFamily;

namespace ClaudeSessionBackup.App.Services;

/// <summary>
/// Converts a Markdown string (via Markdig AST) to a WPF <see cref="FlowDocument"/>.
/// Native rendering - no WebView2, no HTML. Every brush goes through
/// <see cref="Application.Current"/> resources so the document follows theme switches.
/// </summary>
/// <remarks>
/// Pipeline: UseAdvancedExtensions minus anything that needs raw HTML. The AST
/// is walked recursively; unknown node types are rendered as their literal text.
/// Called once per VISIBLE block by <see cref="Views.MarkdownHost"/>, never
/// cached: a FlowDocument has one owning viewer, so a cached instance breaks the
/// recycling turn list. Live memory is therefore bounded by what is on screen
/// rather than by everything the user has scrolled past.
/// </remarks>
public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Parse Markdown text and render it into a <see cref="FlowDocument"/>.
    /// Must be called on the dispatcher thread (FlowDocument is not freezable).
    /// </summary>
    public FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI, sans-serif"),
            FontSize = 13,
        };

        // Bind the document foreground to the theme
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");

        var ast = Markdown.Parse(markdown, Pipeline);
        foreach (var block in ast)
        {
            var rendered = RenderBlock(block);
            if (rendered is not null)
                doc.Blocks.Add(rendered);
        }

        return doc;
    }

    private WpfBlock? RenderBlock(MarkdownObject node)
    {
        return node switch
        {
            HeadingBlock heading => RenderHeading(heading),
            ParagraphBlock para => RenderParagraph(para),
            FencedCodeBlock fenced => RenderCodeBlock(fenced.Lines.ToString(), fenced.Info),
            CodeBlock code => RenderCodeBlock(code.Lines.ToString(), null),
            ListBlock list => RenderList(list),
            QuoteBlock quote => RenderQuote(quote),
            ThematicBreakBlock => RenderThematicBreak(),
            Markdig.Extensions.Tables.Table table => RenderTable(table),
            _ => RenderFallbackBlock(node),
        };
    }

    private Paragraph RenderHeading(HeadingBlock heading)
    {
        var para = new Paragraph
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
            FontSize = heading.Level switch
            {
                1 => 20,
                2 => 17,
                3 => 15,
                _ => 14,
            },
        };
        para.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        AddInlines(para.Inlines, heading.Inline);
        return para;
    }

    private Paragraph RenderParagraph(ParagraphBlock para)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        p.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        if (para.Inline is not null)
            AddInlines(p.Inlines, para.Inline);
        return p;
    }

    private WpfBlock RenderCodeBlock(string code, string? language)
    {
        var trimmed = code.TrimEnd('\r', '\n');

        var para = new Paragraph(new Run(trimmed))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(10, 8, 10, 8),
            TextAlignment = TextAlignment.Left,
        };
        para.SetResourceReference(TextElement.ForegroundProperty, "LogForegroundBrush");
        para.SetResourceReference(Paragraph.BackgroundProperty, "LogBackgroundBrush");

        // Wrap in a Section so we can set FlowDirection and prevent wrapping
        var section = new Section(para);
        return section;
    }

    private List RenderList(ListBlock listBlock)
    {
        var list = new List
        {
            MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(16, 0, 0, 6),
            Padding = new Thickness(0),
        };

        foreach (var item in listBlock)
        {
            if (item is ListItemBlock listItem)
            {
                var li = new ListItem();
                foreach (var child in listItem)
                {
                    var rendered = RenderBlock(child);
                    if (rendered is not null)
                        li.Blocks.Add(rendered);
                }
                list.ListItems.Add(li);
            }
        }

        return list;
    }

    private Section RenderQuote(QuoteBlock quote)
    {
        var section = new Section
        {
            // BorderBrush set below via resource reference
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 4),
        };
        section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        section.SetResourceReference(Section.BorderBrushProperty, "PanelLineBrush");

        foreach (var child in quote)
        {
            var rendered = RenderBlock(child);
            if (rendered is not null)
                section.Blocks.Add(rendered);
        }

        return section;
    }

    private Paragraph RenderThematicBreak()
    {
        var border = new System.Windows.Controls.Border
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0),
        };
        border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelLineBrush");

        var para = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 8),
        };
        para.Inlines.Add(new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Center });
        return para;
    }

    private Table RenderTable(Markdig.Extensions.Tables.Table mdTable)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 8),
            FontSize = 12,
        };
        table.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");

        // Columns
        foreach (var col in mdTable.ColumnDefinitions)
        {
            table.Columns.Add(new TableColumn());
        }
        // Fallback: if no column defs, scan rows for max cells
        if (table.Columns.Count == 0)
        {
            int maxCols = 0;
            foreach (var row in mdTable.OfType<Markdig.Extensions.Tables.TableRow>())
                maxCols = Math.Max(maxCols, row.Count);
            for (int i = 0; i < maxCols; i++)
                table.Columns.Add(new TableColumn());
        }

        var rowGroup = new TableRowGroup();
        table.RowGroups.Add(rowGroup);

        foreach (var row in mdTable.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var tr = new TableRow();
            if (row.IsHeader)
            {
                tr.FontWeight = FontWeights.SemiBold;
            }
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var tc = new TableCell
                {
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(6, 2, 6, 2),
                };
                tc.SetResourceReference(TableCell.BorderBrushProperty, "PanelLineBrush");
                foreach (var child in cell)
                {
                    var rendered = RenderBlock(child);
                    if (rendered is not null)
                        tc.Blocks.Add(rendered);
                }
                tr.Cells.Add(tc);
            }
            rowGroup.Rows.Add(tr);
        }

        return table;
    }

    private WpfBlock? RenderFallbackBlock(MarkdownObject node)
    {
        // HtmlBlock: Inline is null, content lives in Lines. Render as monospace
        // so the raw HTML source is visible rather than silently discarded.
        if (node is Markdig.Syntax.HtmlBlock htmlBlock)
        {
            var text = htmlBlock.Lines.ToString().TrimEnd('\r', '\n');
            if (!string.IsNullOrEmpty(text))
                return RenderCodeBlock(text, null);
            return null;
        }

        // For other unknown block types, render their literal text if any
        if (node is LeafBlock leaf && leaf.Inline is not null)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
            p.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            AddInlines(p.Inlines, leaf.Inline);
            return p;
        }
        return null;
    }

    // ----------------------------------------------------------------- inlines

    private void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            AddInline(target, inline);
        }
    }

    private void AddInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
            {
                var span = new Span();
                if (emphasis.DelimiterCount >= 2)
                    span.FontWeight = FontWeights.Bold;
                else
                    span.FontStyle = FontStyles.Italic;
                AddInlines(span.Inlines, emphasis);
                target.Add(span);
                break;
            }

            case CodeInline code:
            {
                var run = new Run(code.Content)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontSize = 12,
                };
                var span = new Span(run);
                span.SetResourceReference(TextElement.BackgroundProperty, "LogBackgroundBrush");
                span.SetResourceReference(TextElement.ForegroundProperty, "LogForegroundBrush");
                target.Add(span);
                break;
            }

            case LinkInline link:
            {
                if (link.IsImage)
                {
                    // Images in markdown are ignored per spec
                    target.Add(new Run($"(image: {link.Url ?? ""})"));
                }
                else
                {
                    var hyperlink = new Hyperlink();
                    hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AppAccentBrush");
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
                        && uri.Scheme is "http" or "https")
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += (_, e) =>
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = e.Uri.AbsoluteUri,
                                    UseShellExecute = true,
                                });
                            }
                            catch (System.ComponentModel.Win32Exception) { /* unregistered scheme */ }
                            e.Handled = true;
                        };
                    }
                    else
                    {
                        // Non-http(s) URIs: show as text with a tooltip, not navigable.
                        hyperlink.ToolTip = link.Url;
                    }
                    AddInlines(hyperlink.Inlines, link);
                    target.Add(hyperlink);
                }
                break;
            }

            case LineBreakInline lineBreak:
                target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                break;

            case HtmlInline html:
                // Raw HTML shown as text per spec
                target.Add(new Run(html.Tag));
                break;

            case ContainerInline container:
                AddInlines(target, container);
                break;

            default:
                // Unknown inline: try to get its text
                target.Add(new Run(inline.ToString() ?? ""));
                break;
        }
    }
}

