using System.Text;
using Microsoft.Agents.AI.Workflows;
using Serilog;

namespace Harness.Core.Workflows;

/// <summary>
/// Accumulates the Markdown transcript of a single Magentic run and writes it to
/// &lt;workingDir&gt;\magentic\run-yyyyMMdd-HHmmss-fff.md. The file is written once, at the end
/// of the run — including cancelled and failed runs, so a broken run stays diagnosable.
/// </summary>
internal sealed class MagenticRunReport
{
    private readonly StringBuilder body = new();
    private readonly string filePath;
    private string? currentSpeaker;

    public MagenticRunReport(string workingDirectory, string task)
    {
        this.filePath = Path.Combine(
            workingDirectory,
            "magentic",
            $"run-{DateTime.Now:yyyyMMdd-HHmmss-fff}.md");

        this.body
            .AppendLine("# Magentic run")
            .AppendLine()
            .AppendLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine()
            .AppendLine("## Task")
            .AppendLine()
            .AppendLine(task)
            .AppendLine();
    }

    /// <summary>Appends a titled section (analysis, plan, replan, error, final answer).</summary>
    public void AddSection(string title, string content)
    {
        this.currentSpeaker = null;
        this.EnsureTrailingNewLine();
        this.body
            .AppendLine($"## {title}")
            .AppendLine()
            .AppendLine(content)
            .AppendLine();
    }

    /// <summary>Appends one progress-ledger round: the manager's verdict and the next speaker.</summary>
    public void AddRound(int round, MagenticProgressLedger ledger)
    {
        this.currentSpeaker = null;
        this.EnsureTrailingNewLine();
        this.body
            .AppendLine($"## Round {round}")
            .AppendLine()
            .AppendLine($"- request satisfied: {ledger.IsRequestSatisfied}")
            .AppendLine($"- in loop: {ledger.IsInLoop}")
            .AppendLine($"- progress being made: {ledger.IsProgressBeingMade}")
            .AppendLine($"- next speaker: {ledger.NextSpeaker}")
            .AppendLine($"- instruction: {ledger.InstructionOrQuestion}")
            .AppendLine();
    }

    /// <summary>
    /// Appends a streaming delta, opening a new block whenever the speaking agent changes.
    /// </summary>
    public void AddSpeakerDelta(string speaker, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!string.Equals(speaker, this.currentSpeaker, StringComparison.Ordinal))
        {
            this.currentSpeaker = speaker;
            this.body.AppendLine().AppendLine($"### {speaker}").AppendLine();
        }

        this.body.Append(text);
    }

    // AddSpeakerDelta appends raw streamed text with no trailing newline, so a heading written
    // right after a speaker block would glue onto its last line ("...ответа## Round 3") and
    // Markdown would not render it as a heading. Called from AddSection/AddRound to close the
    // previous block before opening a new one. Ensure blank line before heading for proper Markdown rendering.
    private void EnsureTrailingNewLine()
    {
        if (this.body.Length > 0 && this.body[^1] != '\n')
        {
            this.body.AppendLine();
        }

        if (this.body.Length > 1 && this.body[^1] == '\n' && this.body[^2] != '\n')
        {
            this.body.AppendLine();
        }
    }

    /// <summary>
    /// Writes the report to disk. Never throws: any write failure is logged and reported back
    /// as text, because losing the report must not lose the run's result.
    /// </summary>
    public string Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.filePath)!);
            File.WriteAllText(this.filePath, this.body.ToString());
            return this.filePath;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Magentic: failed to write the run report to {Path}", this.filePath);
            return $"(отчёт не удалось записать: {ex.Message})";
        }
    }
}
