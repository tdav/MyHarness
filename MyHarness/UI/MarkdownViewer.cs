using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MyHarnessWin.UI;

/// <summary>
/// Chat transcript viewer that renders assistant answers as Markdown (Markdig) inside
/// a dark-themed WebView2. Non-markdown entries — user lines, reasoning, tool calls,
/// errors, info — are shown as colored blocks. Replaces the RichTextBox log; content
/// is appended in batches and the page repaints once per batch, so nothing flickers.
/// Links open in the default browser; the transcript can be snapshotted and restored
/// per chat session.
/// </summary>
public sealed class MarkdownViewer : UserControl
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

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly WebView2 web;
    private readonly List<Segment> segments = [];
    private bool ready;
    private bool renderQueued;

    public MarkdownViewer()
    {
        this.web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Theme.Background,
        };
        this.BackColor = Theme.Background;
        this.Controls.Add(this.web);
    }

    /// <inheritdoc/>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!this.DesignMode)
        {
            _ = this.InitializeAsync();
        }
    }

    /// <summary>Appends a batch of chunks and re-renders the page once.</summary>
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

    private async Task InitializeAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Test05-Win",
                "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await this.web.EnsureCoreWebView2Async(environment);

            var core = this.web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Markdown links must not navigate the transcript away — open them externally.
            core.NavigationStarting += (_, args) =>
            {
                if (args.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    try
                    {
                        Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
                    }
                    catch
                    {
                        // No default browser — ignore.
                    }
                }
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;

            core.NavigationCompleted += (_, _) =>
            {
                this.ready = true;
                if (this.renderQueued)
                {
                    this.renderQueued = false;
                    this.Render();
                }
            };

            this.web.NavigateToString(HtmlTemplate);
        }
        catch (Exception ex)
        {
            // Most likely the WebView2 runtime is missing — show why instead of a blank box.
            this.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.ChatError,
                BackColor = Theme.Background,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = $"Не удалось инициализировать WebView2: {ex.Message}",
            });
        }
    }

    /// <summary>Rebuilds the page content and scrolls to the bottom (one repaint).</summary>
    private void Render()
    {
        if (!this.ready)
        {
            this.renderQueued = true;
            return;
        }

        string html = BuildHtml(this.segments);
        string script =
            $"document.getElementById('c').innerHTML = {JsonSerializer.Serialize(html)};" +
            "window.scrollTo(0, document.body.scrollHeight);";
        _ = this.web.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>
    /// Merges consecutive chunks of the same kind (streaming splits words across chunks)
    /// and renders each group: Markdown through Markdig, the rest as escaped colored blocks.
    /// </summary>
    private static string BuildHtml(List<Segment> segments)
    {
        var sb = new StringBuilder();
        var group = new StringBuilder();

        for (int i = 0; i < segments.Count; i++)
        {
            group.Append(segments[i].Text);

            bool last = i == segments.Count - 1;
            if (last || segments[i + 1].Kind != segments[i].Kind || segments[i + 1].Bold != segments[i].Bold)
            {
                FlushGroup(sb, group.ToString(), segments[i].Kind, segments[i].Bold);
                group.Clear();
            }
        }

        return sb.ToString();
    }

    private static void FlushGroup(StringBuilder sb, string text, SegmentKind kind, bool bold)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (kind == SegmentKind.Markdown)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append("<div class=\"md\">").Append(Markdown.ToHtml(text, Pipeline)).Append("</div>");
            }

            return;
        }

        string cls = kind switch
        {
            SegmentKind.User => "user",
            SegmentKind.Reasoning => "reason",
            SegmentKind.Tool => "tool",
            SegmentKind.Error => "error",
            _ => "info",
        };

        sb.Append("<div class=\"blk ").Append(cls);
        if (bold)
        {
            sb.Append(" b");
        }

        sb.Append("\">").Append(WebUtility.HtmlEncode(text.TrimEnd('\n'))).Append("</div>");
    }

    // Colors mirror the Theme palette (dark navy + teal accent).
    private const string HtmlTemplate = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <style>
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body {
            background: #1a212b; color: #e8edf2; margin: 0; padding: 10px 14px;
            font: 14px "Segoe UI", sans-serif; overflow-wrap: break-word;
        }
        .blk { white-space: pre-wrap; margin: 3px 0; }
        .b { font-weight: 600; }
        .user { color: #6fb7e8; margin-top: 10px; }
        .reason { color: #b18cd9; font-style: italic; }
        .tool { color: #e0b36a; }
        .error { color: #f76d6d; }
        .info { color: #8593a5; }
        .md p { margin: 6px 0; }
        .md h1, .md h2, .md h3, .md h4 { margin: 12px 0 6px; }
        .md pre {
            background: #232d3a; border: 1px solid #344050; border-radius: 6px;
            padding: 10px; overflow-x: auto;
        }
        .md code {
            background: #232d3a; padding: 1px 5px; border-radius: 4px;
            font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px;
        }
        .md pre code { background: transparent; padding: 0; }
        .md table { border-collapse: collapse; margin: 6px 0; }
        .md th, .md td { border: 1px solid #344050; padding: 4px 10px; }
        .md th { background: #232d3a; }
        .md a { color: #3fc1b0; }
        .md blockquote {
            border-left: 3px solid #344050; margin: 6px 0; padding-left: 10px; color: #8593a5;
        }
        .md ul, .md ol { margin: 6px 0; padding-left: 24px; }
        .md hr { border: none; border-top: 1px solid #344050; }
        </style>
        </head>
        <body><div id="c"></div></body>
        </html>
        """;
}
