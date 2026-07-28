using SharpConsoleUI.Controls;
using SharpConsoleUI.Parsing;

namespace HarnessCli.UI;

/// <summary>Visual style of one transcript chunk.</summary>
internal enum TranscriptKind
{
    /// <summary>Assistant answer text — accumulated and rendered as Markdown.</summary>
    Markdown,
    User,
    Reasoning,
    Tool,
    Error,
    Info,
}

/// <summary>One transcript chunk (streaming chunks of the same kind merge into one message).</summary>
internal sealed record TranscriptChunk(string Text, TranscriptKind Kind);

/// <summary>
/// Drives a <see cref="ChatTranscriptControl"/> from the harness's chunk stream. Consecutive
/// chunks of the same kind become one chat message, so a streamed answer keeps growing in
/// place instead of producing a message per token. The chunks are also kept here, which is
/// what lets a session's transcript be stored and restored when sessions are switched.
/// Everything on this class must run on the UI thread — the control's mutators require it.
/// </summary>
internal sealed class TranscriptLog
{
    private readonly ChatTranscriptControl control;
    private readonly List<TranscriptChunk> chunks = [];

    private TranscriptKind? openKind;
    private ChatMessageId openId;

    public TranscriptLog(ChatTranscriptControl control)
    {
        this.control = control;

        // The library's per-role defaults are English and assume every body is Markdown.
        // Only the assistant answer is Markdown here; everything else is literal text the
        // agent produced (tool arguments, exception messages) and must not be re-parsed.
        control.SetRoleStyle(ChatRole.Assistant, new ChatRoleStyle
        {
            Header = static (_, author) => author ?? "Ассистент",
        });

        control.SetRoleStyle(ChatRole.User, new ChatRoleStyle
        {
            Markdown = false,
            ColorRole = SharpConsoleUI.Themes.ColorRole.Primary,
            HeaderStyle = CollapsibleHeaderStyle.Rounded,
            Header = static (_, author) => author ?? "Вы",
        });

        control.SetRoleStyle(ChatRole.Tool, new ChatRoleStyle
        {
            Markdown = false,
            Collapsible = true,
            StartCollapsed = false,
            Header = static (_, author) => author ?? "Инструмент",
        });

        control.SetRoleStyle(ChatRole.Error, new ChatRoleStyle
        {
            Markdown = false,
            Header = static (_, author) => author ?? "Ошибка",
        });

        // Info notes are one-liners ("model switched", plugin logs) — a header and a
        // collapse toggle would cost more rows than the note itself.
        control.SetRoleStyle(ChatRole.System, new ChatRoleStyle
        {
            Markdown = false,
            ShowHeader = false,
            Collapsible = false,
        });
    }

    /// <summary>Appends a batch of chunks, merging each run of same-kind chunks into one message.</summary>
    public void Append(IEnumerable<TranscriptChunk> batch)
    {
        foreach (var chunk in batch)
        {
            if (chunk.Text.Length == 0)
            {
                continue;
            }

            this.chunks.Add(chunk);

            if (this.openKind == chunk.Kind)
            {
                this.control.Append(this.openId, Body(chunk));
            }
            else
            {
                this.openKind = chunk.Kind;
                this.openId = this.control.AddMessage(RoleOf(chunk.Kind), Body(chunk), AuthorOf(chunk.Kind));
            }
        }
    }

    /// <summary>Returns a copy of the current transcript for storing per session.</summary>
    public List<TranscriptChunk> Snapshot() => [.. this.chunks];

    /// <summary>Replaces the whole transcript (used when switching chat sessions).</summary>
    public void Load(IReadOnlyList<TranscriptChunk> transcript)
    {
        this.Clear();
        this.Append(transcript);
    }

    public void Clear()
    {
        this.chunks.Clear();
        this.openKind = null;
        this.control.Clear();
    }

    // Only the assistant answer is Markdown; every other kind is literal text whose square
    // brackets (JSON tool arguments, above all) would otherwise be eaten as markup tags.
    private static string Body(TranscriptChunk chunk) =>
        chunk.Kind == TranscriptKind.Markdown ? chunk.Text : MarkupParser.Escape(chunk.Text);

    private static ChatRole RoleOf(TranscriptKind kind) => kind switch
    {
        TranscriptKind.Markdown => ChatRole.Assistant,
        TranscriptKind.User => ChatRole.User,
        TranscriptKind.Reasoning or TranscriptKind.Tool => ChatRole.Tool,
        TranscriptKind.Error => ChatRole.Error,
        _ => ChatRole.System,
    };

    // Reasoning and tool calls share the Tool role (both are collapsible side channels);
    // the header keeps them apart.
    private static string? AuthorOf(TranscriptKind kind) => kind switch
    {
        TranscriptKind.Reasoning => "🧠 Размышления",
        TranscriptKind.Tool => "🔧 Инструменты",
        _ => null,
    };
}
