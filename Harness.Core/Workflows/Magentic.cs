using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Serilog;
using MagenticEvents = Microsoft.Agents.AI.Workflows.Specialized.Magentic;

namespace Harness.Core.Workflows;

/// <summary>
/// Magentic orchestration for code-generation tasks. Architect and Planner run first and
/// produce a codebase analysis and a numbered plan; the resulting brief is handed to the
/// Magentic manager, which drives the inner loop over Coder, Reviewer and Tester — task
/// ledger, progress ledger, stall detection and replanning are implemented by the framework.
/// Everything that happens is logged and written to a Markdown report in the working folder.
/// </summary>
public sealed class Magentic
{
    /// <summary>
    /// Tool name for AIFunctionFactory.Create below and for gating approval of this specific
    /// tool in AgentHost.RunPluginRequestAsync — kept as one constant so the two cannot drift apart.
    /// </summary>
    public const string ToolName = "magentic_codegen";

    /// <summary>Coordination rounds before the manager has to answer with what it has.</summary>
    private const int MaxRounds = 10;

    /// <summary>Consecutive stalled rounds that trigger a replan.</summary>
    private const int MaxStalls = 3;

    /// <summary>Plan resets allowed for one run.</summary>
    private const int MaxResets = 2;

    /// <summary>Cap on the answer handed back to the calling agent; the report keeps it all.</summary>
    private const int MaxAnswerChars = 8_000;

    private readonly MagenticAgents agents;
    private readonly string workingDirectory;

    public Magentic(
        IChatClient chatClient,
        string workingDirectory,
        string fileMemoryDirectory,
        HyperlightCodeActProvider codeAct,
        LocalShellExecutor shellExecutor,
        AIFunction searchFilesTool,
        Func<IReadOnlyList<AITool>> pluginTools,
        int maxContextWindowTokens,
        int maxOutputTokens,
        string tracingSourceName)
    {
        this.workingDirectory = workingDirectory;
        this.agents = new MagenticAgents(
            chatClient,
            workingDirectory,
            fileMemoryDirectory,
            codeAct,
            shellExecutor,
            searchFilesTool,
            pluginTools,
            maxContextWindowTokens,
            maxOutputTokens,
            tracingSourceName);
    }

    /// <summary>
    /// Exposes the orchestration as the magentic_codegen tool. Approval is required: one
    /// dialog authorizes the whole run, and every tool call inside it is auto-approved.
    /// </summary>
    public AIFunction AsAIFunction()
    {
        AIFunction run = AIFunctionFactory.Create(
            ([Description("Задача кодогенерации целиком: что нужно получить, в каких файлах или проекте, чем проверяется результат.")] string task,
             CancellationToken cancellationToken)
                => this.RunAsync(task, cancellationToken),
            name: ToolName,
            description: "Многоагентная оркестрация Magentic для крупных задач кодогенерации. " +
                         "Архитектор изучает кодовую базу, планировщик строит пошаговый план, " +
                         "затем цикл «кодер → ревьюер → тестировщик» под управлением менеджера " +
                         "выполняет план и проверяет результат сборкой и тестами. " +
                         "Возвращает итог работы и путь к подробному отчёту о запуске. " +
                         "Используйте для задач в несколько файлов; мелкие правки делайте сами.");

        return new ApprovalRequiredAIFunction(run);
    }

    /// <summary>
    /// Runs the full orchestration. Never throws: cancellation and failures are recorded in
    /// the report and returned as text, because the caller is an agent turn, not a user.
    /// </summary>
    public async Task<string> RunAsync(string task, CancellationToken cancellationToken)
    {
        var report = new MagenticRunReport(this.workingDirectory, task);
        string outcome;

        try
        {
            var analysis = await RunOnceAsync(
                this.agents.CreateArchitect(),
                $"""
                Задача:
                {task}

                Изучите кодовую базу и опишите целевое решение по вашему регламенту.
                """,
                cancellationToken).ConfigureAwait(false);
            report.AddSection("Architecture analysis", analysis);

            var plan = await RunOnceAsync(
                this.agents.CreatePlanner(),
                $"""
                Задача:
                {task}

                Разбор архитектора:
                {analysis}

                Составьте пошаговый план работ по вашему регламенту.
                """,
                cancellationToken).ConfigureAwait(false);
            report.AddSection("Plan", plan);

            var brief =
                $"""
                Задача:
                {task}

                Разбор архитектора:
                {analysis}

                План работ:
                {plan}

                Выполните план силами команды. Рабочая папка: {this.workingDirectory}
                Задача считается выполненной, когда изменения внесены, приняты ревьюером
                и подтверждены запуском сборки или тестов.
                """;

            outcome = await this.RunWorkflowAsync(brief, report, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Magentic run cancelled");
            report.AddSection("Cancelled", "Запуск отменён до получения результата.");
            outcome = "Запуск Magentic отменён.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Magentic run failed");
            report.AddSection("Error", ex.ToString());
            outcome = $"Запуск Magentic завершился ошибкой: {ex.Message}";
        }

        var reportPath = report.Save();
        return $"{Truncate(outcome)}{Environment.NewLine}{Environment.NewLine}Отчёт о запуске: {reportPath}";
    }

    // Builds the workflow and drains its event stream into the log and the report.
    private async Task<string> RunWorkflowAsync(
        string brief,
        MagenticRunReport report,
        CancellationToken cancellationToken)
    {
        Workflow workflow = new MagenticWorkflowBuilder(this.agents.CreateManager())
            .AddParticipants([this.agents.CreateCoder(), this.agents.CreateReviewer(), this.agents.CreateTester()])
            .RequirePlanSignoff(false)
            .WithMaxRounds(MaxRounds)
            .WithMaxStalls(MaxStalls)
            .WithMaxResets(MaxResets)
            .Build();

        await using StreamingRun run = await InProcessExecution
            .RunStreamingAsync(workflow, new List<ChatMessage> { new(ChatRole.User, brief) }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false))
        {
            Log.Error("Magentic: run did not start — TrySendMessageAsync returned false");
            report.AddSection("Start failed", "Прогон не принял стартовый сигнал (TrySendMessageAsync вернул false) — оркестрация не началась.");
            return "Оркестрация Magentic не запустилась.";
        }

        var round = 0;
        WorkflowOutputEvent? finalOutput = null;

        try
        {
            await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (workflowEvent)
                {
                    case AgentResponseUpdateEvent update:
                        report.AddSpeakerDelta(update.ExecutorId, update.Update.Text);
                        break;

                    case MagenticEvents.MagenticPlanCreatedEvent planCreated:
                        Log.Information("Magentic: task ledger created");
                        report.AddSection("Task ledger", planCreated.FullTaskLedger.Text);
                        break;

                    case MagenticEvents.MagenticReplannedEvent replanned:
                        Log.Warning("Magentic: replanned after a stall");
                        report.AddSection("Replanned", replanned.FullTaskLedger.Text);
                        break;

                    case MagenticEvents.MagenticProgressLedgerUpdatedEvent progressUpdated:
                        round++;
                        Log.Information(
                            "Magentic round {Round}: next speaker {Speaker}",
                            round,
                            progressUpdated.ProgressLedger.NextSpeaker);
                        report.AddRound(round, progressUpdated.ProgressLedger);
                        break;

                    case WorkflowOutputEvent output when output.Is<List<ChatMessage>>():
                        finalOutput = output;
                        break;

                    case WorkflowOutputEvent unexpected:
                        Log.Warning(
                            "Magentic: terminal output was {Type}, not List<ChatMessage> — the tool will report no final answer",
                            unexpected.Data?.GetType().FullName ?? "null");
                        break;

                    case WorkflowErrorEvent error:
                        Log.Error(error.Exception, "Magentic workflow error");
                        report.AddSection("Workflow error", error.Exception?.ToString() ?? "Unknown workflow error.");
                        break;

                    case ExecutorFailedEvent failed:
                        Log.Error(failed.Data, "Magentic executor {ExecutorId} failed", failed.ExecutorId);
                        report.AddSection(
                            "Executor failed",
                            $"Executor: {failed.ExecutorId}{Environment.NewLine}{Environment.NewLine}{failed.Data?.ToString() ?? "Unknown executor failure."}");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // WatchStreamAsync's cancellation only stops the event stream (SDK 1.15.0) — it does
            // not cancel workflow execution. Cancel the run explicitly while it is still in scope
            // so agents actually stop instead of continuing in the background with auto-approved
            // tools, then rethrow so RunAsync's outer catch still writes the Cancelled section.
            await run.CancelRunAsync().ConfigureAwait(false);
            throw;
        }

        var answer = FormatFinalAnswer(finalOutput);
        report.AddSection("Final answer", answer);
        return answer;
    }

    // Runs a pre-phase agent for a single turn and returns its text.
    private static async Task<string> RunOnceAsync(AIAgent agent, string prompt, CancellationToken cancellationToken)
    {
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Text;
    }

    // The terminal output carries the whole transcript; the manager's synthesized answer is
    // the last message that has text.
    private static string FormatFinalAnswer(WorkflowOutputEvent? finalOutput)
    {
        if (finalOutput?.As<List<ChatMessage>>() is not { Count: > 0 } transcript)
        {
            return "Оркестрация завершилась без финального ответа — подробности в отчёте.";
        }

        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(transcript[i].Text))
            {
                return transcript[i].Text;
            }
        }

        return "Оркестрация завершилась без финального ответа — подробности в отчёте.";
    }

    // Keeps a long transcript from eating the calling agent's context; the report has it all.
    private static string Truncate(string text) =>
        text.Length <= MaxAnswerChars
            ? text
            : text[..MaxAnswerChars] + $"{Environment.NewLine}… (ответ обрезан, полный текст в отчёте)";
}
