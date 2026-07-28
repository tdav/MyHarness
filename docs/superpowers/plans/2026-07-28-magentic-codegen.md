# Magentic Codegen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Добавить в `Harness.Core` многоагентную оркестрацию Magentic для задач кодогенерации и зарегистрировать её как инструмент `magentic_codegen` главного агента.

**Architecture:** Класс `Harness.Core.Workflows.Magentic` строит шесть агентов на том же `IChatClient`, что и главный агент. Architect и Planner выполняются последовательно до цикла и готовят разбор кодовой базы и план; менеджер Magentic ведёт цикл «Task Ledger → Progress Ledger → выбор speaker'а → replan при застое» с участниками Coder, Reviewer, Tester. Весь ход запуска пишется в Serilog и в Markdown-отчёт в рабочей папке, инструмент возвращает финальный ответ и путь к отчёту.

**Tech Stack:** .NET 10, Microsoft Agent Framework (`Microsoft.Agents.AI` 1.15.0, `Microsoft.Agents.AI.Harness` 1.15.0, `Microsoft.Agents.AI.Workflows` 1.15.0), `Microsoft.Extensions.AI` 10.8.1, OllamaSharp, Serilog, OpenTelemetry.

**Спека:** [docs/superpowers/specs/2026-07-28-magentic-codegen-design.md](../specs/2026-07-28-magentic-codegen-design.md)

## Global Constraints

- Тесты в этом плане **не пишутся**: тестового проекта в решении нет, пользователь подтвердил отказ от тестов при утверждении спеки. Проверка каждой задачи — успешная сборка, финальная задача дополнительно проверяется ручным прогоном. Не создавать тестовые проекты и файлы без отдельного запроса.
- Приватные поля класса — **без префикса `_`**, обращение через `this.`.
- Комментарии в коде, имена типов и членов, сообщения коммитов — на английском. Тексты инструкций агентов и описания инструментов, видимые пользователю и модели, — на русском (как в существующем `AgentHost`).
- Версии пакетов Agent Framework — ровно `1.15.0`, как у уже подключённых `Microsoft.Agents.AI` / `Microsoft.Agents.AI.Abstractions` / `Microsoft.Agents.AI.Harness`.
- Целевая ветка — `feature/magentic-codegen` (создана от `master`); коммиты делаются только шагами этого плана, ветку переключать нельзя.
- Команда сборки везде одна: `dotnet build MyHarness.slnx` из корня репозитория.
- Файлы фичи живут только в `Harness.Core/Workflows/`; вне её меняется единственный файл — `Harness.Core/AgentHost.cs`.

---

## Файловая структура

| Файл | Ответственность |
|---|---|
| `Harness.Core/Harness.Core.csproj` | Ссылка на пакет `Microsoft.Agents.AI.Workflows` и подавление `MAAIW001` |
| `Harness.Core/Workflows/MagenticRunReport.cs` | Накопление и запись Markdown-отчёта одного запуска |
| `Harness.Core/Workflows/MagenticAgents.cs` | Построение шести агентов: роли, инструкции, наборы инструментов, права |
| `Harness.Core/Workflows/Magentic.cs` | Публичный API фичи: пре-фаза, сборка и прогон workflow, разбор событий, `AsAIFunction()` |
| `Harness.Core/AgentHost.cs` | Регистрация инструмента и абзац в системных инструкциях |

Существующий заглушечный `Harness.Core/Workflows/Magentic.cs` (класс без членов, старый стиль `namespace { }`) полностью перезаписывается в задаче 3.

---

### Task 1: Пакет Workflows и отчёт о запуске

**Files:**
- Modify: `Harness.Core/Harness.Core.csproj` (`PropertyGroup` с `NoWarn`, строка 11; `ItemGroup` с `PackageReference`, строки 14–28)
- Create: `Harness.Core/Workflows/MagenticRunReport.cs`

**Interfaces:**
- Consumes: ничего (первая задача).
- Produces: `internal sealed class MagenticRunReport` в `Harness.Core.Workflows` со следующими членами:
  - `MagenticRunReport(string workingDirectory, string task)`
  - `void AddSection(string title, string content)`
  - `void AddRound(int round, MagenticProgressLedger ledger)`
  - `void AddSpeakerDelta(string speaker, string text)`
  - `string Save()` — возвращает путь к файлу либо текст-объяснение, если запись не удалась.

- [ ] **Step 1: Добавить пакет и подавление предупреждения в csproj**

В `Harness.Core/Harness.Core.csproj` заменить строку с `NoWarn`:

```xml
    <NoWarn>$(NoWarn);OPENAI001;MAAI001</NoWarn>
```

на:

```xml
    <NoWarn>$(NoWarn);OPENAI001;MAAI001;MAAIW001</NoWarn>
```

и добавить в `ItemGroup` с пакетами строку сразу после `Microsoft.Agents.AI.Tools.Shell`:

```xml
    <PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.15.0" />
```

- [ ] **Step 2: Проверить восстановление пакета**

```bash
dotnet build MyHarness.slnx
```

Ожидается: `Build succeeded`, пакет `Microsoft.Agents.AI.Workflows 1.15.0` восстановлен без ошибок NU1101/NU1102.

- [ ] **Step 3: Создать `Harness.Core/Workflows/MagenticRunReport.cs`**

```csharp
using System.Text;
using Microsoft.Agents.AI.Workflows;
using Serilog;

namespace Harness.Core.Workflows;

/// <summary>
/// Accumulates the Markdown transcript of a single Magentic run and writes it to
/// &lt;workingDir&gt;\magentic\run-yyyyMMdd-HHmmss.md. The file is written once, at the end
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
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}.md");

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

    /// <summary>
    /// Writes the report to disk. Never throws: a failed write is logged and reported back
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Magentic: failed to write the run report to {Path}", this.filePath);
            return $"(отчёт не удалось записать: {ex.Message})";
        }
    }
}
```

- [ ] **Step 4: Собрать**

```bash
dotnet build MyHarness.slnx
```

Ожидается: `Build succeeded`. Если компилятор выдаёт CS0246 на `MagenticProgressLedger` — тип лежит в `Microsoft.Agents.AI.Workflows` (не в `...Specialized.Magentic`), проверить, что using именно такой.

- [ ] **Step 5: Коммит**

```bash
git add Harness.Core/Harness.Core.csproj Harness.Core/Workflows/MagenticRunReport.cs && git commit -m "feat(magentic): add Workflows package and run report writer"
```

---

### Task 2: Шесть агентов оркестрации

**Files:**
- Create: `Harness.Core/Workflows/MagenticAgents.cs`

**Interfaces:**
- Consumes: ничего из задачи 1 (файлы независимы).
- Produces: `internal sealed class MagenticAgents` в `Harness.Core.Workflows`:
  - ctor `MagenticAgents(IChatClient chatClient, string workingDirectory, string fileMemoryDirectory, HyperlightCodeActProvider codeAct, LocalShellExecutor shellExecutor, AIFunction searchFilesTool, Func<IReadOnlyList<AITool>> pluginTools, int maxContextWindowTokens, int maxOutputTokens, string tracingSourceName)`
  - `AIAgent CreateManager()`, `AIAgent CreateArchitect()`, `AIAgent CreatePlanner()`, `AIAgent CreateCoder()`, `AIAgent CreateReviewer()`, `AIAgent CreateTester()`

- [ ] **Step 1: Создать `Harness.Core/Workflows/MagenticAgents.cs`**

```csharp
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
```

- [ ] **Step 2: Собрать**

```bash
dotnet build MyHarness.slnx
```

Ожидается: `Build succeeded`.

Возможные ошибки и что делать:
- CS0246 на `HyperlightSandbox.Guest.Python` — этот using в файле не нужен, удалить его.
- CS1061 на `AllToolsAutoApprovalRule` — правило лежит в `ToolApprovalAgent` (не в `ToolApprovalAgentOptions`), проверить написание.
- CS1503 на `AIContextProviders` или `Tools` — параметры объявлены как `IList<...>`, вызовы передают коллекционные выражения `[...]`; при несовпадении типа сменить тип параметра на тот, который требует `HarnessAgentOptions`.

- [ ] **Step 3: Коммит**

```bash
git add Harness.Core/Workflows/MagenticAgents.cs && git commit -m "feat(magentic): add the six orchestration agents"
```

---

### Task 3: Оркестрация и инструмент

**Files:**
- Modify (перезапись целиком): `Harness.Core/Workflows/Magentic.cs`

**Interfaces:**
- Consumes:
  - `MagenticRunReport(string, string)`, `AddSection`, `AddRound`, `AddSpeakerDelta`, `Save`, `FilePath` — задача 1;
  - `MagenticAgents(...)`, `CreateManager/Architect/Planner/Coder/Reviewer/Tester` — задача 2.
- Produces: `public sealed class Magentic` в `Harness.Core.Workflows`:
  - ctor `Magentic(IChatClient chatClient, string workingDirectory, string fileMemoryDirectory, HyperlightCodeActProvider codeAct, LocalShellExecutor shellExecutor, AIFunction searchFilesTool, Func<IReadOnlyList<AITool>> pluginTools, int maxContextWindowTokens, int maxOutputTokens, string tracingSourceName)`
  - `AIFunction AsAIFunction()` — инструмент `magentic_codegen`, требующий подтверждения
  - `Task<string> RunAsync(string task, CancellationToken cancellationToken)`

- [ ] **Step 1: Перезаписать `Harness.Core/Workflows/Magentic.cs` целиком**

```csharp
using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Microsoft.Extensions.AI;
using Serilog;

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
            name: "magentic_codegen",
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

        await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);

        var round = 0;
        WorkflowOutputEvent? finalOutput = null;

        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (workflowEvent)
            {
                case AgentResponseUpdateEvent update:
                    report.AddSpeakerDelta(update.ExecutorId, update.Update.Text);
                    break;

                case MagenticPlanCreatedEvent planCreated:
                    Log.Information("Magentic: task ledger created");
                    report.AddSection("Task ledger", planCreated.FullTaskLedger.Text);
                    break;

                case MagenticReplannedEvent replanned:
                    Log.Warning("Magentic: replanned after a stall");
                    report.AddSection("Replanned", replanned.FullTaskLedger.Text);
                    break;

                case MagenticProgressLedgerUpdatedEvent progressUpdated:
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

                case WorkflowErrorEvent error:
                    Log.Error(error.Exception, "Magentic workflow error");
                    report.AddSection("Workflow error", error.Exception?.ToString() ?? "Unknown workflow error.");
                    break;

                case ExecutorFailedEvent failed:
                    Log.Error("Magentic executor failed: {Failure}", failed.ToString());
                    report.AddSection("Executor failed", failed.ToString() ?? "Unknown executor failure.");
                    break;
            }
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
```

- [ ] **Step 2: Собрать**

```bash
dotnet build MyHarness.slnx
```

Ожидается: `Build succeeded`.

Возможные ошибки и что делать:
- CS0118 «`Magentic` — это тип, но используется как пространство имён»: конфликт имени класса и хвоста `...Specialized.Magentic`. Заменить директиву на алиас `using MagenticEvents = Microsoft.Agents.AI.Workflows.Specialized.Magentic;` и обращаться к событиям как `MagenticEvents.MagenticPlanCreatedEvent` и т. д.
- CS1503 на `RunStreamingAsync` — перегрузка принимает `(Workflow, TInput, string?, CancellationToken)`; аргумент уже передаётся именованно, при несовпадении добавить `runId: null`.
- CS1061 на `WithMaxRounds`/`WithMaxStalls`/`WithMaxResets` — сверить имена с XML-документацией пакета в `~/.nuget/packages/microsoft.agents.ai.workflows/1.15.0/lib/net10.0/`.

- [ ] **Step 3: Коммит**

```bash
git add Harness.Core/Workflows/Magentic.cs && git commit -m "feat(magentic): orchestrate architect, planner and the coder loop"
```

---

### Task 4: Регистрация в AgentHost и ручной прогон

**Files:**
- Modify: `Harness.Core/AgentHost.cs` (using-директивы, строки 1–16; блок инструкций в `CreateAsync`, около строки 386; `BuildAgent()`, строки 498–547)

**Interfaces:**
- Consumes: `Magentic` ctor и `AsAIFunction()` — задача 3.
- Produces: инструмент `magentic_codegen` в наборе инструментов главного агента.

Экземпляр `Magentic` создаётся внутри `BuildAgent()`, а не хранится в поле: `BuildAgent()` вызывается при смене модели и при горячей загрузке плагинов, поэтому оркестрация всегда получает актуальные бюджеты токенов (`ContextWindowTokens` / `OutputTokens`). Инструменты плагинов передаются делегатом и потому тоже всегда актуальны.

- [ ] **Step 1: Добавить using**

В `Harness.Core/AgentHost.cs` после строки `using Harness.Core.Tracing;` добавить:

```csharp
using Harness.Core.Workflows;
```

- [ ] **Step 2: Зарегистрировать инструмент в `BuildAgent()`**

В методе `BuildAgent()` после строки `IChatClient chatClient = this.ollama;` добавить:

```csharp
        // Recreated on every rebuild so the orchestration always sees the current token
        // budgets (model switch) and the current plugin tools (hot load).
        var magentic = new Magentic(
            this.ollama,
            this.WorkingDirectory,
            Path.Combine(baseDir, "agent-files"),
            this.codeAct,
            this.shellExecutor,
            this.searchFilesTool,
            () => this.pluginManager.GetAgentTools(),
            this.ContextWindowTokens,
            this.OutputTokens,
            this.tracingSourceName);
```

и в `ChatOptions` заменить строку со списком инструментов:

```csharp
                Tools = [this.searchFilesTool, this.shellExecutor.AsAIFunction(requireApproval: true), .. this.pluginManager.GetAgentTools()],
```

на:

```csharp
                Tools =
                [
                    this.searchFilesTool,
                    this.shellExecutor.AsAIFunction(requireApproval: true),
                    magentic.AsAIFunction(),
                    .. this.pluginManager.GetAgentTools(),
                ],
```

- [ ] **Step 3: Описать инструмент в системных инструкциях**

В `CreateAsync`, в блоке `instructions`, в разделе «### Доступные инструменты», после пункта про `execute_code` добавить:

```
    - `magentic_codegen` — многоагентная оркестрация для крупных задач кодогенерации: архитектор
      изучает кодовую базу, планировщик строит пошаговый план, затем цикл «кодер → ревьюер →
      тестировщик» выполняет его под управлением менеджера. Запускайте её для задач в несколько
      файлов или требующих проверки сборкой и тестами; мелкие правки делайте сами через
      `file_access` и `run_shell`. Внутри оркестрации подтверждения не запрашиваются —
      пользователь подтверждает только сам вызов. Отчёт о запуске сохраняется в
      `{Path.Combine(workingDir, "magentic")}`.
```

- [ ] **Step 4: Собрать**

```bash
dotnet build MyHarness.slnx
```

Ожидается: `Build succeeded`.

- [ ] **Step 5: Подготовить сценарий ручного прогона**

Ручной прогон выполняет пользователь: нужен живой ключ Ollama и интерактивный ввод,
исполнителю задачи запускать приложение не нужно. Задача исполнителя — убедиться, что
проект собирается (шаг 4), и передать сценарий ниже без изменений.

```bash
dotnet run --project HarnessCli
```

Сценарий проверки:
1. Выбрать рабочей папкой пустую или тестовую директорию.
2. Отправить запрос вида: «Через magentic_codegen: создай в scripts/ file-based .NET-скрипт `wordcount.cs`, который считает строки, слова и символы в переданном файле, и проверь его запуском на README.md».
3. Подтвердить единственный диалог вызова `magentic_codegen`.
4. Дождаться ответа и проверить:
   - в ответе есть итог работы и путь к отчёту;
   - файл `<рабочая папка>\magentic\run-*.md` создан и содержит разделы `Architecture analysis`, `Plan`, `Task ledger`, хотя бы один `Round N` и `Final answer`;
   - изменения в рабочей папке действительно сделаны (`scripts\wordcount.cs` существует);
   - внутри оркестрации диалоги подтверждения не появлялись.

Если прогон падает на первом же раунде с ошибкой разбора ответа менеджера — уменьшить нагрузку на модель: снизить `MaxRounds` до 5 в `Magentic.cs` и повторить; в отчёте будет видно, на каком шаге сорвался разбор.

- [ ] **Step 6: Коммит**

```bash
git add Harness.Core/AgentHost.cs && git commit -m "feat(magentic): register magentic_codegen with the harness agent"
```

---

## Проверка результата

Критерии приёмки из спеки и то, чем они закрываются:

| Критерий | Где проверяется |
|---|---|
| `Harness.Core` собирается, `MAAIW001` подавлен | Задача 1, шаг 2 |
| `magentic_codegen` виден агенту и требует подтверждения | Задача 4, шаг 5, пункт 3 |
| Запуск доходит до финального ответа, отчёт создан | Задача 4, шаг 5, пункт 4 |
| Смена модели и горячая загрузка плагина не ломают инструмент | Задача 4, шаг 2: инструмент пересоздаётся в `BuildAgent()`, инструменты плагинов передаются делегатом |
| Отмена оставляет корректный отчёт | Задача 3, шаг 1: `catch (OperationCanceledException)` с записью раздела `Cancelled` и последующим `Save()` |
