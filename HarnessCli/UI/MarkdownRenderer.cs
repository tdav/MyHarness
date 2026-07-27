using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace HarnessCli.UI;

/// <summary>
/// Accumulates styled terminal text as Terminal.Gui <see cref="Cell"/> lines — the console
/// counterpart of appending HTML to the WebView2 transcript. Every line starts with the
/// current indent prefix (quote bars, list indentation), so nested content stays aligned
/// even after the <see cref="TranscriptView"/> word-wraps it.
/// </summary>
internal sealed class CellWriter
{
    private readonly List<List<Cell>> lines = [];
    private readonly List<(string Continuation, Attribute Attribute)> indents = [];

    private List<Cell>? current;

    // First line after PushIndent uses the marker (list bullet) instead of the continuation.
    private string? pendingMarker;
    private Attribute pendingMarkerAttribute;

    /// <summary>Gets the finished lines (the open line is closed first).</summary>
    public List<List<Cell>> ToLines()
    {
        this.CloseLine();
        return this.lines.Count > 0 ? this.lines : [[]];
    }

    /// <summary>
    /// Indents everything written until the matching <see cref="PopIndent"/>.
    /// <paramref name="marker"/> is drawn on the first line only (list bullets, quote bars
    /// repeat via <paramref name="continuation"/>).
    /// </summary>
    public void PushIndent(string marker, string continuation, Attribute attribute)
    {
        this.CloseLine();
        this.indents.Add((continuation, attribute));
        this.pendingMarker = marker;
        this.pendingMarkerAttribute = attribute;
    }

    public void PopIndent()
    {
        this.CloseLine();
        if (this.indents.Count > 0)
        {
            this.indents.RemoveAt(this.indents.Count - 1);
        }

        this.pendingMarker = null;
    }

    /// <summary>Appends a styled run to the current line (never contains newlines).</summary>
    public void Write(string text, Attribute attribute)
    {
        if (text.Length == 0)
        {
            return;
        }

        this.EnsureLine().AddRange(Cell.ToCellList(text, attribute));
    }

    /// <summary>Appends text that may contain newlines, keeping the indent on every line.</summary>
    public void WriteMultiline(string text, Attribute attribute)
    {
        var parts = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                this.NewLine();
            }

            this.Write(parts[i], attribute);
        }
    }

    /// <summary>Ends the current line; the next write starts a fresh, indented one.</summary>
    public void NewLine()
    {
        this.EnsureLine();
        this.CloseLine();
    }

    /// <summary>Emits one empty separator line unless the output already ends with one.</summary>
    public void EnsureBlankLine()
    {
        this.CloseLine();
        if (this.lines.Count == 0 || this.lines[^1].Count == 0)
        {
            return;
        }

        this.lines.Add([]);
    }

    /// <summary>Gets a value indicating whether nothing has been written yet.</summary>
    public bool IsEmpty => this.lines.Count == 0 && this.current is null;

    private List<Cell> EnsureLine()
    {
        if (this.current is not null)
        {
            return this.current;
        }

        this.current = [];

        // Indent prefix: all enclosing levels as continuation, except the innermost one
        // right after PushIndent, which shows the marker.
        for (int i = 0; i < this.indents.Count; i++)
        {
            bool innermost = i == this.indents.Count - 1;
            var (continuation, attribute) = this.indents[i];
            string prefix = innermost && this.pendingMarker is not null ? this.pendingMarker : continuation;
            var prefixAttribute = innermost && this.pendingMarker is not null ? this.pendingMarkerAttribute : attribute;
            this.current.AddRange(Cell.ToCellList(prefix, prefixAttribute));
        }

        this.pendingMarker = null;
        return this.current;
    }

    private void CloseLine()
    {
        if (this.current is null)
        {
            return;
        }

        this.lines.Add(this.current);
        this.current = null;
    }
}

/// <summary>
/// Renders Markdown (parsed by Markdig) into colored terminal cells. This is the console
/// equivalent of the WebView2 stylesheet in the WinForms app: headings, code, quotes,
/// links, lists and tables each get the matching <see cref="Theme"/> attribute instead of
/// a CSS class.
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>Renders <paramref name="markdown"/> into <paramref name="writer"/>.</summary>
    public static void Render(string markdown, CellWriter writer)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        RenderBlocks(document, writer, first: writer.IsEmpty);
    }

    private static void RenderBlocks(ContainerBlock container, CellWriter writer, bool first = true)
    {
        foreach (var block in container)
        {
            if (!first)
            {
                writer.EnsureBlankLine();
            }

            first = false;
            RenderBlock(block, writer);
        }
    }

    private static void RenderBlock(Block block, CellWriter writer)
    {
        switch (block)
        {
            case HeadingBlock heading:
                // Level shows as a leading bar run; deeper headings get a shorter bar.
                writer.Write(new string('▌', Math.Max(1, 4 - Math.Min(heading.Level, 3))) + " ", Theme.ListMarker);
                RenderInlines(heading.Inline, writer, Theme.Heading);
                writer.NewLine();
                break;

            case ParagraphBlock paragraph:
                RenderInlines(paragraph.Inline, writer, Theme.Markdown);
                writer.NewLine();
                break;

            case ListBlock list:
                RenderList(list, writer);
                break;

            case QuoteBlock quote:
                writer.PushIndent("│ ", "│ ", Theme.Rule);
                RenderBlocks(quote, writer);
                writer.PopIndent();
                break;

            case CodeBlock code:
                RenderCode(code, writer);
                break;

            case ThematicBreakBlock:
                writer.Write(new string('─', 40), Theme.Rule);
                writer.NewLine();
                break;

            case Table table:
                RenderTable(table, writer);
                break;

            case HtmlBlock html:
                // Raw HTML has no terminal equivalent — show the source, dimmed.
                writer.WriteMultiline(LinesOf(html).TrimEnd(), Theme.Info);
                writer.NewLine();
                break;

            case ContainerBlock container:
                RenderBlocks(container, writer);
                break;

            case LeafBlock leaf:
                RenderInlines(leaf.Inline, writer, Theme.Markdown);
                writer.NewLine();
                break;
        }
    }

    private static void RenderList(ListBlock list, CellWriter writer)
    {
        int number = list.IsOrdered && int.TryParse(list.OrderedStart, out int start) ? start : 1;
        bool firstItem = true;

        foreach (var child in list)
        {
            // Tight lists stay compact; only a loose list (blank lines in the source)
            // gets breathing room between its items.
            if (!firstItem && list.IsLoose)
            {
                writer.EnsureBlankLine();
            }

            firstItem = false;

            string marker = list.IsOrdered ? $"{number++}. " : "• ";
            writer.PushIndent(marker, new string(' ', marker.Length), Theme.ListMarker);
            if (child is ContainerBlock item)
            {
                RenderBlocks(item, writer);
            }

            writer.PopIndent();
        }
    }

    private static void RenderCode(CodeBlock code, CellWriter writer)
    {
        var text = LinesOf(code).TrimEnd('\n', '\r');
        var rows = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Pad every row to the same width so the raised background forms a solid block,
        // the terminal equivalent of the .md pre { background } rule.
        int width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        string? language = (code as FencedCodeBlock)?.Info;

        if (!string.IsNullOrWhiteSpace(language))
        {
            writer.Write($"  {language}".PadRight(width + 4), Theme.Info);
            writer.NewLine();
        }

        foreach (var row in rows)
        {
            writer.Write("  " + row.PadRight(width + 2), Theme.Code);
            writer.NewLine();
        }
    }

    private static void RenderTable(Table table, CellWriter writer)
    {
        foreach (var rowBlock in table)
        {
            if (rowBlock is not TableRow row)
            {
                continue;
            }

            var attribute = row.IsHeader ? Theme.TableHeader : Theme.Markdown;
            bool firstCell = true;

            foreach (var cellBlock in row)
            {
                writer.Write(firstCell ? "│ " : " │ ", Theme.TableBorder);
                firstCell = false;

                if (cellBlock is TableCell cell)
                {
                    foreach (var content in cell)
                    {
                        if (content is LeafBlock leaf)
                        {
                            RenderInlines(leaf.Inline, writer, attribute);
                        }
                    }
                }
            }

            writer.Write(" │", Theme.TableBorder);
            writer.NewLine();

            if (row.IsHeader)
            {
                writer.Write(new string('─', 40), Theme.TableBorder);
                writer.NewLine();
            }
        }
    }

    private static void RenderInlines(ContainerInline? container, CellWriter writer, Attribute attribute)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            RenderInline(inline, writer, attribute);
        }
    }

    private static void RenderInline(Inline inline, CellWriter writer, Attribute attribute)
    {
        switch (inline)
        {
            case LiteralInline literal:
                writer.Write(literal.Content.ToString(), attribute);
                break;

            case EmphasisInline emphasis:
                RenderInlines(emphasis, writer, EmphasisAttribute(emphasis, attribute));
                break;

            case CodeInline code:
                writer.Write(code.Content, Theme.Code);
                break;

            case LinkInline link:
                RenderLink(link, writer, attribute);
                break;

            case AutolinkInline autolink:
                writer.Write(autolink.Url, Theme.Link);
                break;

            case LineBreakInline lineBreak:
                if (lineBreak.IsHard)
                {
                    writer.NewLine();
                }
                else
                {
                    writer.Write(" ", attribute);
                }

                break;

            case HtmlInline html:
                writer.Write(html.Tag, Theme.Info);
                break;

            case ContainerInline nested:
                RenderInlines(nested, writer, attribute);
                break;

            default:
                writer.Write(inline.ToString() ?? string.Empty, attribute);
                break;
        }
    }

    private static void RenderLink(LinkInline link, CellWriter writer, Attribute attribute)
    {
        if (link.IsImage)
        {
            writer.Write("🖼 ", Theme.Info);
        }

        bool hasLabel = link.FirstChild is not null;
        if (hasLabel)
        {
            RenderInlines(link, writer, Theme.Link);
        }

        // A terminal cannot hide the target behind the label — show it so the URL stays
        // usable (the WinForms viewer opened it in a browser instead).
        if (!string.IsNullOrEmpty(link.Url))
        {
            writer.Write(hasLabel ? $" ({link.Url})" : link.Url, hasLabel ? Theme.Info : Theme.Link);
        }
        else if (!hasLabel)
        {
            writer.Write(link.Label ?? string.Empty, attribute);
        }
    }

    // Bold/italic/strikethrough keep the surrounding color and only add the text style,
    // so emphasis inside a link stays link-colored.
    private static Attribute EmphasisAttribute(EmphasisInline emphasis, Attribute attribute)
    {
        var style = emphasis.DelimiterChar switch
        {
            '~' => TextStyle.Strikethrough,
            '=' => TextStyle.Reverse,
            _ => emphasis.DelimiterCount >= 2 ? TextStyle.Bold : TextStyle.Italic,
        };

        return new Attribute(attribute.Foreground, attribute.Background, attribute.Style | style);
    }

    private static string LinesOf(LeafBlock block)
    {
        var sb = new System.Text.StringBuilder();
        var lines = block.Lines.Lines;
        for (int i = 0; i < block.Lines.Count; i++)
        {
            sb.Append(lines[i].Slice.ToString()).Append('\n');
        }

        return sb.ToString();
    }
}
