using System.Text;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace HarnessCli.UI;

/// <summary>
/// Runnable check for the transcript pipeline (Markdig → colored cells), reachable as
/// <c>HarnessCli --selfcheck</c>. It renders a sample transcript without starting the
/// Terminal.Gui main loop, asserts the parts that are easy to break silently (code-block
/// background, heading style, list markers, link color, indentation) and prints the result
/// as true-color ANSI so the palette can also be eyeballed in a plain console.
/// </summary>
internal static class RenderSelfCheck
{
    private const string Sample = """
        # Заголовок первого уровня

        Обычный текст со **жирным**, *курсивом* и `inline code`, а также
        [ссылкой](https://example.com).

        ## Список

        1. Первый пункт
        2. Второй пункт с продолжением,
           которое переносится
           - вложенный пункт

        > Цитата в две
        > строки.

        ```csharp
        var x = 42;
        Console.WriteLine(x);
        ```

        | Колонка A | Колонка B |
        |-----------|-----------|
        | значение  | ещё одно  |

        ---
        """;

    public static int Run()
    {
        var segments = new List<TranscriptView.Segment>
        {
            new("👤 Вы: покажи разметку\n", TranscriptView.SegmentKind.User, true),
            new("Думаю над ответом…\n", TranscriptView.SegmentKind.Reasoning, false),
            new("🔧 Вызов инструмента: search_files {\"pattern\":\"todo\"}…\n", TranscriptView.SegmentKind.Tool, false),
            // Streaming splits Markdown across chunks; the renderer must merge them first.
            new(Sample[..40], TranscriptView.SegmentKind.Markdown, false),
            new(Sample[40..], TranscriptView.SegmentKind.Markdown, false),
            new("❌ Ошибка: пример\n", TranscriptView.SegmentKind.Error, false),
        };

        var lines = BuildLines(segments);
        int failures = 0;

        failures += Check("транскрипт не пуст", lines.Count > 10);
        failures += Check(
            "текст пользователя выделен цветом ChatUser",
            AnyCell(lines, c => c.Attribute?.Foreground == Theme.ChatUser));
        failures += Check(
            "рассуждения выделены курсивом ChatReasoning",
            AnyCell(lines, c => c.Attribute?.Foreground == Theme.ChatReasoning
                && (c.Attribute?.Style & TextStyle.Italic) == TextStyle.Italic));
        failures += Check(
            "заголовок жирный с подчёркиванием",
            AnyCell(lines, c => (c.Attribute?.Style & TextStyle.Bold) == TextStyle.Bold
                && (c.Attribute?.Style & TextStyle.Underline) == TextStyle.Underline));
        failures += Check(
            "блок кода нарисован на фоне Surface",
            LineText(lines, "var x = 42;") is int codeLine
                && lines[codeLine].All(c => c.Attribute?.Background == Theme.Surface));
        failures += Check(
            "inline code тоже на фоне Surface",
            AnyCell(lines, c => c.Attribute?.Background == Theme.Surface && c.Grapheme == "l"));
        failures += Check(
            "ссылка окрашена акцентным цветом",
            AnyCell(lines, c => c.Attribute?.Foreground == Theme.Accent
                && (c.Attribute?.Style & TextStyle.Underline) == TextStyle.Underline));
        failures += Check(
            "маркеры нумерованного списка на месте",
            lines.Any(l => Text(l).StartsWith("1. ")) && lines.Any(l => Text(l).StartsWith("2. ")));
        failures += Check(
            "продолжение пункта остаётся в его логической строке",
            lines.Any(l => Text(l).StartsWith("2. ") && Text(l).Contains("переносится")));
        failures += Check(
            "вложенный пункт списка сдвинут глубже",
            lines.Any(l => Text(l).StartsWith("   • ")));
        failures += Check(
            "плотный список идёт без пустых строк между пунктами",
            LineText(lines, "1. Первый") is int firstBullet
                && Text(lines[firstBullet + 1]).StartsWith("2. "));
        failures += Check(
            "цитата помечена вертикальной чертой",
            lines.Any(l => Text(l).StartsWith("│ ") && Text(l).Contains("Цитата")));
        failures += Check(
            "строки таблицы разделены рамкой",
            lines.Any(l => Text(l).Contains("│ Колонка A │ Колонка B │")));
        failures += Check(
            "ошибка выделена цветом ChatError",
            AnyCell(lines, c => c.Attribute?.Foreground == Theme.ChatError));

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "Все проверки пройдены. Предпросмотр (24-битный ANSI):"
            : $"ПРОВАЛЕНО проверок: {failures}. Предпросмотр (24-битный ANSI):");
        Console.WriteLine();
        Console.Write(ToAnsi(lines));
        Console.WriteLine();

        return failures == 0 ? 0 : 1;
    }

    // The transcript view owns the segment→cells conversion; rendering it here keeps the
    // check honest (same grouping of streamed chunks, same Markdown pipeline).
    private static List<List<Cell>> BuildLines(List<TranscriptView.Segment> segments)
    {
        var writer = new CellWriter();
        var group = new StringBuilder();

        for (int i = 0; i < segments.Count; i++)
        {
            group.Append(segments[i].Text);
            bool last = i == segments.Count - 1;
            if (last || segments[i + 1].Kind != segments[i].Kind || segments[i + 1].Bold != segments[i].Bold)
            {
                var kind = segments[i].Kind;
                var text = group.ToString();
                group.Clear();

                if (kind == TranscriptView.SegmentKind.Markdown)
                {
                    writer.EnsureBlankLine();
                    MarkdownRenderer.Render(text, writer);
                    continue;
                }

                var attribute = kind switch
                {
                    TranscriptView.SegmentKind.User => Theme.User,
                    TranscriptView.SegmentKind.Reasoning => Theme.Reasoning,
                    TranscriptView.SegmentKind.Tool => Theme.Tool,
                    TranscriptView.SegmentKind.Error => Theme.Error,
                    _ => Theme.Info,
                };

                writer.EnsureBlankLine();
                writer.WriteMultiline(text.TrimEnd('\n'), attribute);
                writer.NewLine();
            }
        }

        return writer.ToLines();
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "OK  " : "ПРОВАЛ")}] {what}");
        return ok ? 0 : 1;
    }

    private static bool AnyCell(List<List<Cell>> lines, Func<Cell, bool> predicate) =>
        lines.Any(line => line.Any(predicate));

    private static string Text(List<Cell> line) =>
        string.Concat(line.Select(c => c.Grapheme));

    private static int? LineText(List<List<Cell>> lines, string contains)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (Text(lines[i]).Contains(contains, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    private static string ToAnsi(List<List<Cell>> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            Attribute? previous = null;
            foreach (var cell in line)
            {
                if (cell.Attribute is Attribute attribute && !attribute.Equals(previous))
                {
                    sb.Append(AnsiFor(attribute));
                    previous = attribute;
                }

                sb.Append(cell.Grapheme);
            }

            sb.Append("[0m").Append('\n');
        }

        return sb.ToString();
    }

    private static string AnsiFor(Attribute attribute)
    {
        var fg = attribute.Foreground;
        var bg = attribute.Background;
        var sb = new StringBuilder("[0m");
        sb.Append($"[38;2;{fg.R};{fg.G};{fg.B}m");
        sb.Append($"[48;2;{bg.R};{bg.G};{bg.B}m");

        if (attribute.Style.HasFlag(TextStyle.Bold)) { sb.Append("[1m"); }
        if (attribute.Style.HasFlag(TextStyle.Faint)) { sb.Append("[2m"); }
        if (attribute.Style.HasFlag(TextStyle.Italic)) { sb.Append("[3m"); }
        if (attribute.Style.HasFlag(TextStyle.Underline)) { sb.Append("[4m"); }
        if (attribute.Style.HasFlag(TextStyle.Strikethrough)) { sb.Append("[9m"); }

        return sb.ToString();
    }
}
