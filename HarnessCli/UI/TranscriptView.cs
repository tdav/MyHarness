using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace HarnessCli.UI;

// TextView is marked obsolete in favour of the separate Terminal.Gui.Editor package, but it
// is the only in-box view that renders a List<List<Cell>> with per-cell colors, scrolls and
// word-wraps. Switching to EditorView means a new dependency and a Terminal.Gui 2.5 bump —
// worth doing when the transcript needs syntax highlighting or find/replace, not before.
#pragma warning disable CS0618 // Type or member is obsolete

/// <summary>
/// Chat transcript for the console UI: a read-only, word-wrapping <see cref="TextView"/>
/// whose content is built from colored <see cref="Cell"/>s. Assistant answers go through
/// Markdig (<see cref="MarkdownRenderer"/>); user lines, reasoning, tool calls, errors and
/// info notes are plain colored blocks. This is the Terminal.Gui counterpart of the
/// WebView2-based MarkdownViewer — same segment model, same colors, same batching, so
/// nothing flickers while the agent streams.
/// </summary>
internal sealed class TranscriptView : TextView
{
    /// <summary>Visual style of one transcript chunk.</summary>
    public enum SegmentKind
    {
        /// <summary>Assistant answer text — accumulated and rendered as Markdown.</summary>
        Markdown,
        User,
        Reasoning,
        Tool,
        Error,
        Info,
    }

    /// <summary>One transcript chunk (streaming chunks of the same kind merge on render).</summary>
    public sealed record Segment(string Text, SegmentKind Kind, bool Bold);

    private readonly List<Segment> segments = [];

    public TranscriptView()
    {
        this.ReadOnly = true;
        this.Multiline = true;
        this.WordWrap = true;
        this.ScrollBars = true;
        this.SchemeName = Theme.SchemeTranscript;
    }

    /// <summary>Appends a batch of chunks and re-renders the transcript once.</summary>
    public void AppendSegments(IEnumerable<Segment> batch)
    {
        this.segments.AddRange(batch);
        this.Render();
    }

    /// <summary>Replaces the whole transcript (used when switching chat sessions).</summary>
    public void LoadSegments(IReadOnlyList<Segment> transcript)
    {
        this.segments.Clear();
        this.segments.AddRange(transcript);
        this.Render();
    }

    /// <summary>Returns a copy of the current transcript for storing per session.</summary>
    public List<Segment> Snapshot() => [.. this.segments];

    public void ClearTranscript()
    {
        this.segments.Clear();
        this.Render();
    }

    /// <summary>
    /// Rebuilds the cell content and scrolls to the bottom. Consecutive chunks of the same
    /// kind are merged first — streaming splits words (and Markdown syntax) across chunks,
    /// so only the merged text parses correctly.
    /// </summary>
    private void Render()
    {
        var writer = new CellWriter();
        var group = new System.Text.StringBuilder();

        for (int i = 0; i < this.segments.Count; i++)
        {
            group.Append(this.segments[i].Text);

            bool last = i == this.segments.Count - 1;
            if (last || this.segments[i + 1].Kind != this.segments[i].Kind ||
                this.segments[i + 1].Bold != this.segments[i].Bold)
            {
                FlushGroup(writer, group.ToString(), this.segments[i].Kind, this.segments[i].Bold);
                group.Clear();
            }
        }

        this.Load(writer.ToLines());
        this.MoveEnd();
    }

    private static void FlushGroup(CellWriter writer, string text, SegmentKind kind, bool bold)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (kind == SegmentKind.Markdown)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                writer.EnsureBlankLine();
                MarkdownRenderer.Render(text, writer);
            }

            return;
        }

        var attribute = kind switch
        {
            SegmentKind.User => Theme.User,
            SegmentKind.Reasoning => Theme.Reasoning,
            SegmentKind.Tool => Theme.Tool,
            SegmentKind.Error => Theme.Error,
            _ => Theme.Info,
        };

        if (bold)
        {
            attribute = new Attribute(attribute.Foreground, attribute.Background, attribute.Style | TextStyle.Bold);
        }

        writer.EnsureBlankLine();
        writer.WriteMultiline(text.TrimEnd('\n'), attribute);
        writer.NewLine();
    }
}
