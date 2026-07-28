using HyperlightSandbox.Guest.Python;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace Harness.Core.Workflows;

/// <summary>
/// Builds the six agents of a Magentic run. Five of them are harness agents, so they keep
/// every capability the main agent has — file access rooted at the working folder, file
/// memory, skills, todo list, telemetry, greedy sampling — and differ only in role
/// instructions, tools and write permissions. The manager is a plain chat agent on purpose:
/// the Magentic orchestrator supplies its own ledger prompts and parses the replies as JSON,
/// so harness system-prompt scaffolding would only get in its way.
///
/// Every tool call inside the orchestration is auto-approved. The user already approved the
/// magentic_codegen call itself, and the loop has no UI to show per-call dialogs — the same
/// trade-off the plugin channel makes in AgentHost.RunPluginRequestAsync. The safety net
/// stays structural: the shell deny-list and timeout, file access confined to the working
/// folder, read-only roles with write tools disabled, and Python inside Hyperlight.
/// </summary>
internal sealed class MagenticAgents
{
    private readonly IChatClient chatClient;
    private readonly string workingDirectory;
    private readonly string fileMemoryDirectory;
    private readonly HyperlightCodeActProvider codeAct;
    private readonly LocalShellExecutor shellExecutor;
    private readonly AIFunction searchFilesTool;
    private readonly Func<IReadOnlyList<AITool>> pluginTools;
    private readonly int maxContextWindowTokens;
    private readonly int maxOutputTokens;
    private readonly string tracingSourceName;

    public MagenticAgents(
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
        this.chatClient = chatClient;
        this.workingDirectory = workingDirectory;
        this.fileMemoryDirectory = fileMemoryDirectory;
        this.codeAct = codeAct;
        this.shellExecutor = shellExecutor;
        this.searchFilesTool = searchFilesTool;
        this.pluginTools = pluginTools;
        this.maxContextWindowTokens = maxContextWindowTokens;
        this.maxOutputTokens = maxOutputTokens;
        this.tracingSourceName = tracingSourceName;
    }

    /// <summary>
    /// Creates the Magentic manager: no tools, no harness providers — the orchestrator owns
    /// the prompts for the task ledger, the progress ledger and the final synthesis.
    /// </summary>
    public AIAgent CreateManager() => new ChatClientAgent(
        this.chatClient,
        new ChatClientAgentOptions
        {
            Name = "Manager",
            Description = "Ведёт леджеры задачи и прогресса, выбирает следующего исполнителя.",
            ChatOptions = new ChatOptions
            {
                MaxOutputTokens = this.maxOutputTokens,
                Temperature = 0f,
                TopP = 1f,
                Seed = 0,
            },
        });

    /// <summary>Creates the read-only agent that maps the codebase before the loop starts.</summary>
    public AIAgent CreateArchitect() => this.CreateHarnessAgent(
        name: "Architect",
        description: "Изучает кодовую базу и предлагает целевое решение.",
        instructions:
            """
            Вы — архитектор. До начала работы команды вы изучаете кодовую базу и описываете
            целевое решение. Писать файлы и запускать команды вам нельзя — только читать.

            Порядок работы:
            1. Найдите относящиеся к задаче файлы через `search_files` и прочитайте их.
            2. Опишите текущее устройство затронутой части: файлы, типы, точки расширения,
               принятые в проекте соглашения (стиль, именование, обработка ошибок).
            3. Предложите целевое решение: какие файлы создать и какие изменить, какие
               публичные сигнатуры появятся, как решение встраивается в существующий код.
            4. Перечислите риски и то, что нужно проверить сборкой или запуском.

            Отвечайте одним сообщением, компактным Markdown, без вступлений и извинений.
            Опирайтесь только на то, что прочитали: если чего-то не нашли — так и напишите.
            """,
        tools: [this.searchFilesTool],
        contextProviders: [],
        readOnlyFiles: true);

    /// <summary>Creates the read-only agent that turns the analysis into a numbered plan.</summary>
    public AIAgent CreatePlanner() => this.CreateHarnessAgent(
        name: "Planner",
        description: "Превращает разбор архитектора в пошаговый план работ.",
        instructions:
            """
            Вы — планировщик. На основе задачи и разбора архитектора вы составляете план работ
            для команды из трёх исполнителей: Coder (пишет код), Reviewer (проверяет код),
            Tester (запускает сборку и тесты). Писать файлы и запускать команды вам нельзя.

            План — нумерованный список шагов. Для каждого шага укажите:
            - что именно делается и в каких файлах (точные пути);
            - кто исполнитель — Coder, Reviewer или Tester;
            - критерий приёмки: что должно быть верно, чтобы шаг считался выполненным.

            Шаги делайте мелкими и проверяемыми, порядок — от фундамента к надстройке.
            В конце добавьте раздел «Определение готовности»: чем проверяется задача целиком
            (какая команда сборки, какие тесты, какой ручной сценарий).

            Отвечайте одним сообщением, компактным Markdown, без вступлений.
            """,
        tools: [this.searchFilesTool],
        contextProviders: [],
        readOnlyFiles: true);

    /// <summary>Creates the only agent allowed to modify files; also gets the Python sandbox.</summary>
    public AIAgent CreateCoder() => this.CreateHarnessAgent(
        name: "Coder",
        description: "Пишет и правит код в рабочей папке по инструкции менеджера.",
        instructions:
            """
            Вы — программист команды. Вы единственный, кто вносит изменения в файлы.
            Выполняйте ровно ту инструкцию, которую дал менеджер, — не больше и не меньше.

            Порядок работы:
            1. Прочитайте файлы, которые собираетесь менять, прежде чем их менять.
            2. Внесите минимальное изменение, решающее поставленный шаг, в стиле окружающего
               кода: те же соглашения об именовании, та же структура, тот же уровень
               комментариев.
            3. Для вычислений и проверок гипотез используйте `execute_code` (Python
               в песочнице), а не рассуждения о том, что получилось бы.
            4. В ответе перечислите изменённые файлы и коротко — что в них сделано.

            Не запускайте сборку и тесты — это работа Tester. Если инструкция противоречит
            прочитанному коду, не додумывайте: сообщите об этом в ответе и предложите вариант.
            """,
        tools: [this.searchFilesTool, .. this.pluginTools()],
        contextProviders: [this.codeAct],
        readOnlyFiles: false);

    /// <summary>Creates the read-only agent that reviews the coder's changes.</summary>
    public AIAgent CreateReviewer() => this.CreateHarnessAgent(
        name: "Reviewer",
        description: "Проверяет внесённые изменения и выдаёт вердикт менеджеру.",
        instructions:
            """
            Вы — ревьюер. Вы проверяете изменения, внесённые программистом, и не правите их сами:
            запись файлов и запуск команд вам недоступны.

            Порядок работы:
            1. Прочитайте изменённые файлы целиком, а не только процитированные фрагменты.
            2. Проверьте: решает ли изменение поставленный шаг; нет ли ошибок в логике,
               в обработке ошибок и в граничных случаях; соответствует ли код соглашениям
               проекта; не осталось ли мусора, дублирования и неиспользуемого кода.
            3. Дайте вердикт первой строкой ответа: `ВЕРДИКТ: принято` или
               `ВЕРДИКТ: доработать`.
            4. При доработке перечислите замечания списком: файл, строка, что не так,
               что сделать. Только то, что действительно нужно исправить.
            """,
        tools: [this.searchFilesTool],
        contextProviders: [],
        readOnlyFiles: true);

    /// <summary>Creates the read-only agent that runs the build and the tests via PowerShell.</summary>
    public AIAgent CreateTester() => this.CreateHarnessAgent(
        name: "Tester",
        description: "Запускает сборку, тесты и скрипты, докладывает фактический результат.",
        instructions:
            """
            Вы — тестировщик. Вы проверяете изменения запуском, а не рассуждением.
            Править файлы вам нельзя, только читать и выполнять команды через `run_shell`.

            Порядок работы:
            1. Определите, чем проверяется текущий шаг (сборка, тесты, запуск скрипта),
               и выполните это через `run_shell` в рабочей папке.
            2. Приведите в ответе выполненную команду и существенную часть вывода —
               ошибки и итоговые строки, а не весь лог.
            3. Первой строкой ответа дайте результат: `РЕЗУЛЬТАТ: успешно` или
               `РЕЗУЛЬТАТ: ошибка`, дальше — краткий разбор причины ошибки.

            Никогда не выдавайте предполагаемый результат за фактический: если команду
            выполнить не удалось, так и напишите.
            """,
        tools: [this.searchFilesTool, this.shellExecutor.AsAIFunction(requireApproval: false)],
        contextProviders: [new ShellEnvironmentProvider(this.shellExecutor)],
        readOnlyFiles: true);

    // Shared harness configuration: same context window, output budget, telemetry source,
    // file memory and greedy sampling as the main agent in AgentHost.BuildAgent.
    private AIAgent CreateHarnessAgent(
        string name,
        string description,
        string instructions,
        IList<AITool> tools,
        IList<AIContextProvider> contextProviders,
        bool readOnlyFiles) =>
        this.chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = name,
            Description = description,
            MaxContextWindowTokens = this.maxContextWindowTokens,
            MaxOutputTokens = this.maxOutputTokens,
            OpenTelemetrySourceName = this.tracingSourceName,

            FileMemoryStore = new FileSystemAgentFileStore(this.fileMemoryDirectory),
            FileAccessStore = new FileSystemAgentFileStore(this.workingDirectory),
            FileAccessProviderOptions = new FileAccessProviderOptions
            {
                // Read-only roles cannot write at all — enforced by the provider, not by prompt.
                DisableWriteTools = readOnlyFiles,
                DisableReadOnlyToolApproval = true,
                DisableWriteToolApproval = true,
            },

            // No hosted web search on Ollama; the local search_files tool replaces it.
            DisableWebSearch = true,

            // The manager decides what happens next — a per-agent plan/execute mode would
            // only add prompt noise inside the orchestration.
            DisableAgentModeProvider = true,

            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [ToolApprovalAgent.AllToolsAutoApprovalRule],
            },

            AIContextProviders = contextProviders,

            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                MaxOutputTokens = this.maxOutputTokens,
                Tools = tools,
                Reasoning = new() { Effort = ReasoningEffort.Medium },

                // Same greedy decoding as the main agent: invented facts are worse than "unknown".
                Temperature = 0f,
                TopP = 1f,
                Seed = 0,
            },
        });
}
