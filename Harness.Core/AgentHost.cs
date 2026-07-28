using HyperlightSandbox.Guest.Python;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using Serilog;
using OpenTelemetry.Trace;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Plugins;
using Harness.Core.Tracing;
using Harness.Core.Workflows;

namespace Harness.Core;

/// <summary>
/// Builds and owns the ReAct harness agent (Ollama Cloud backend) together with all of its
/// disposable infrastructure: the Hyperlight Python sandbox, the local shell executor
/// (child PowerShell processes rooted at the user-selected working folder), the HTTP client
/// and the OpenTelemetry tracer. A host UI (WinForms, console, …) talks only to
/// <see cref="Agent"/>; everything below this type is UI-agnostic.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    /// <summary>Fallback context window for models that do not report their context length.</summary>
    public const int DefaultContextWindowTokens = 131_072;

    /// <summary>Upper bound for the output-token budget; shrinks for small-context models.</summary>
    public const int MaxOutputTokens = 16_384;

    /// <summary>Tracing source name used when the host app does not supply one.</summary>
    public const string DefaultTracingSourceName = "Harness";

    // Shared between the main agent's shell executor and the orchestration's own one below,
    // so the two deny-lists cannot drift apart.
    private static readonly string[] ShellDenyListPatterns =
    [
        @"\brm\s+-rf\b",
        @"\bsudo\b",
        @":\(\)\s*\{",          // fork-bomb shape
        @"\bmkfs\b",
        @">\s*/dev/sd",
        @"\bFormat-Volume\b",
    ];

    private readonly string tracingSourceName;
    private readonly TracerProvider? tracerProvider;
    private readonly HyperlightCodeActProvider codeAct;
    private readonly LocalShellExecutor shellExecutor;
    private readonly LocalShellExecutor orchestrationShellExecutor;
    private readonly HttpClient http;
    private readonly OllamaApiClient ollama;
    private readonly string instructions;
    private readonly AIFunction searchFilesTool;
    private readonly PluginManager pluginManager;
    private volatile bool pluginToolsDirty;

    // Per-plugin persistent agent sessions for the AskAgentAsync channel; the semaphore
    // serializes plugin-originated turns (they arrive from background threads).
    private readonly Dictionary<string, (AIAgent Agent, AgentSession Session)> pluginSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim pluginSessionsLock = new(1, 1);

    /// <summary>
    /// Gets the configured harness agent. The instance is replaced when a model switch
    /// changes the context window (see <see cref="SetModelAsync"/>) — callers holding
    /// sessions must re-attach them via serialize/deserialize after a rebuild.
    /// </summary>
    public AIAgent Agent { get; private set; }

    /// <summary>Gets the user-selected working folder that all file tools are rooted at.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets the model name in use (for display in the UI title bar).</summary>
    public string ModelName { get; private set; }

    /// <summary>
    /// Gets the context window of the current model, reported by the Ollama endpoint
    /// (/api/show, model_info "*.context_length"); <see cref="DefaultContextWindowTokens"/>
    /// when the model does not report one.
    /// </summary>
    public int ContextWindowTokens { get; private set; }

    /// <summary>Gets the output-token budget derived from the current context window.</summary>
    public int OutputTokens => Math.Min(MaxOutputTokens, this.ContextWindowTokens / 4);

    private AgentHost(
        string workingDirectory,
        string modelName,
        int contextWindowTokens,
        string instructions,
        AIFunction searchFilesTool,
        PluginManager pluginManager,
        string tracingSourceName,
        TracerProvider? tracerProvider,
        HyperlightCodeActProvider codeAct,
        LocalShellExecutor shellExecutor,
        LocalShellExecutor orchestrationShellExecutor,
        HttpClient http,
        OllamaApiClient ollama)
    {
        this.WorkingDirectory = workingDirectory;
        this.tracingSourceName = tracingSourceName;
        this.ModelName = modelName;
        this.ContextWindowTokens = contextWindowTokens;
        this.instructions = instructions;
        this.searchFilesTool = searchFilesTool;
        this.pluginManager = pluginManager;
        this.tracerProvider = tracerProvider;
        this.codeAct = codeAct;
        this.shellExecutor = shellExecutor;
        this.orchestrationShellExecutor = orchestrationShellExecutor;
        this.http = http;
        this.ollama = ollama;
        this.pluginManager.PluginsChanged += () => this.pluginToolsDirty = true;
        this.Agent = this.BuildAgent();
    }

    /// <summary>Gets the plugin manager (the UI subscribes to its PluginLog event).</summary>
    public PluginManager Plugins => this.pluginManager;

    /// <summary>
    /// Gets a value indicating whether a plugin was hot-loaded and the agent must be
    /// rebuilt to expose its tools (see <see cref="RefreshPluginToolsIfNeeded"/>).
    /// </summary>
    public bool HasPendingPluginTools => this.pluginToolsDirty;

    /// <summary>
    /// Rebuilds the agent when a plugin was hot-loaded since the last build, making the
    /// plugin's tools available. Returns <see langword="true"/> when the agent was
    /// replaced — the caller must then migrate its sessions (serialize with the old agent,
    /// deserialize with the new one), same as after <see cref="SetModelAsync"/>.
    /// </summary>
    public bool RefreshPluginToolsIfNeeded()
    {
        if (!this.pluginToolsDirty)
        {
            return false;
        }

        this.pluginToolsDirty = false;
        this.Agent = this.BuildAgent();
        return true;
    }

    /// <summary>
    /// Runs a plugin-originated user request through the harness agent and returns its final
    /// text answer (the IPluginContext.AskAgentAsync channel). Each plugin keeps its own
    /// persistent session, migrated automatically when the agent instance is rebuilt.
    /// Tool-approval requests are auto-approved: the request comes from an external channel
    /// (e.g. a Telegram chat) where the host UI's approval dialog cannot be shown. The one
    /// exception is <see cref="Magentic.ToolName"/>: that orchestration is auto-denied here,
    /// since it needs the user's own confirmation, not a blanket channel approval.
    /// </summary>
    public async Task<string> RunPluginRequestAsync(string pluginName, string message, CancellationToken cancellationToken)
    {
        await this.pluginSessionsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var agent = this.Agent;
            AgentSession session;
            if (this.pluginSessions.TryGetValue(pluginName, out var entry))
            {
                session = entry.Session;
                if (!ReferenceEquals(entry.Agent, agent))
                {
                    // The agent was rebuilt (model switch / plugin load) — re-attach the session.
                    var snapshot = await entry.Agent.SerializeSessionAsync(session).ConfigureAwait(false);
                    session = await agent.DeserializeSessionAsync(snapshot).ConfigureAwait(false);
                }
            }
            else
            {
                session = await agent.CreateSessionAsync().ConfigureAwait(false);
            }

            var answer = new StringBuilder();
            IList<ChatMessage>? next = [new ChatMessage(ChatRole.User, message)];
            while (next is not null)
            {
                var approvals = new List<ToolApprovalRequestContent>();
                answer.Clear(); // Only the text produced after the last approval round is the answer.
                await foreach (var update in agent.RunStreamingAsync(next, session, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case ToolApprovalRequestContent approval:
                                approvals.Add(approval);
                                break;

                            case TextContent text when !string.IsNullOrEmpty(text.Text):
                                answer.Append(text.Text);
                                break;
                        }
                    }
                }

                next = approvals.Count > 0
                    ? approvals
                        .Select(a => new ChatMessage(
                            ChatRole.User,
                            [a.ToolCall is FunctionCallContent { Name: Magentic.ToolName }
                                ? a.CreateResponse(
                                    approved: false,
                                    reason: "Оркестрация magentic_codegen доступна только из интерфейса приложения — во внешнем канале её нельзя подтвердить.")
                                : a.CreateResponse(approved: true, reason: "Plugin channel: auto-approved")]))
                        .ToList()
                    : null;
            }

            this.pluginSessions[pluginName] = (agent, session);
            return answer.ToString();
        }
        finally
        {
            this.pluginSessionsLock.Release();
        }
    }

    /// <summary>
    /// Switches the backend model and adopts its context window (queried from the endpoint).
    /// When the context window changes, the harness agent is rebuilt with the new token
    /// budgets and this method returns <see langword="true"/> — the caller must then migrate
    /// existing sessions to the new <see cref="Agent"/> (serialize with the old agent,
    /// deserialize with the new one). Otherwise only the target model changes and the
    /// current agent/sessions stay intact.
    /// </summary>
    public async Task<bool> SetModelAsync(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Имя модели не может быть пустым.", nameof(modelName));
        }

        modelName = modelName.Trim();
        var contextWindow = await this.GetModelContextLengthAsync(modelName).ConfigureAwait(false)
            ?? DefaultContextWindowTokens;

        this.ollama.SelectedModel = modelName;
        this.ModelName = modelName;

        if (contextWindow == this.ContextWindowTokens)
        {
            return false;
        }

        this.ContextWindowTokens = contextWindow;
        this.Agent = this.BuildAgent();
        return true;
    }

    /// <summary>
    /// Queries the model's context window via /api/show. Returns null when the endpoint,
    /// the model, or its metadata does not expose a "*.context_length" entry.
    /// </summary>
    public async Task<int?> GetModelContextLengthAsync(string modelName)
    {
        try
        {
            var response = await this.ollama
                .ShowModelAsync(new ShowModelRequest { Model = modelName })
                .ConfigureAwait(false);

            var extra = response?.Info?.ExtraInfo;
            if (extra is null)
            {
                return null;
            }

            foreach (var pair in extra)
            {
                if (!pair.Key.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var length = pair.Value switch
                {
                    JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt64(out var l) => l,
                    long l => l,
                    int i => i,
                    _ => 0L,
                };

                if (length > 0)
                {
                    return (int)Math.Min(length, int.MaxValue);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            // Unknown model / offline endpoint — the caller falls back to the default.
            Log.Warning(ex, "Failed to query context length for model {Model}", modelName);
            return null;
        }
    }

    /// <summary>
    /// Fetches the model names available on the configured Ollama endpoint (/api/tags).
    /// Returns an empty list when the endpoint does not support listing or the call fails —
    /// the UI then falls back to manual model-name entry.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync()
    {
        try
        {
            var models = await this.ollama.ListLocalModelsAsync().ConfigureAwait(false);
            return models
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to list models from the Ollama endpoint");
            return [];
        }
    }

    /// <summary>
    /// Creates the agent rooted at <paramref name="workingDir"/>.
    /// Backend config comes from secret.json (gitignored); env vars override secret.json if set.
    /// The context window is queried from the endpoint for the configured model.
    /// <paramref name="tracingSourceName"/> names the OpenTelemetry activity source so traces
    /// of the different host apps stay distinguishable.
    /// </summary>
    public static async Task<AgentHost> CreateAsync(
        string workingDir,
        string tracingSourceName = DefaultTracingSourceName)
    {
        var secret = LoadOllamaSecret(Path.Combine(AppContext.BaseDirectory, "secret.json"));
        var endpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? secret.Endpoint;
        var modelName = Environment.GetEnvironmentVariable("OLLAMA_MODEL_NAME") ?? secret.Model;
        var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY") ??  secret.ApiKey;
        endpoint = string.IsNullOrWhiteSpace(endpoint) ? "https://ollama.com" : endpoint;
        modelName = string.IsNullOrWhiteSpace(modelName) ? "glm-5.2:cloud" : modelName;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Ollama API key is missing. Set Ollama.ApiKey in secret.json or the OLLAMA_API_KEY env var.");
        }

        var baseDir = AppContext.BaseDirectory;

        // dotnet scripts live in a dedicated scripts/ folder inside the working folder;
        // bundled examples are seeded there on first run (existing files are never overwritten).
        var scriptsDir = Path.Combine(workingDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        SeedExampleScripts(Path.Combine(baseDir, "dotnet-scripts"), scriptsDir);

        var tracerProvider = HarnessTracing.CreateFileTracerProvider(tracingSourceName);

        // Plugins live next to the exe (like skills/ and agents/); they are loaded after the
        // host exists so the AskAgentAsync channel works from the very first plugin start.
        var pluginManager = new PluginManager(Path.Combine(baseDir, "plugins"), workingDir);

        var codeAct = new HyperlightCodeActProvider(
            HyperlightCodeActProviderOptions.CreateForWasm(PythonGuestModule.GetModulePath()));

        var shellExecutor = new LocalShellExecutor(new LocalShellExecutorOptions
        {
            WorkingDirectory = workingDir,
            ConfineWorkingDirectory = false,
            Policy = new ShellPolicy(denyList: ShellDenyListPatterns),
            Timeout = TimeSpan.FromSeconds(30),
        });

        // The Magentic orchestration gets its own executor: same deny-list and working folder,
        // but a 5-minute timeout — a cold `dotnet build` routinely exceeds the main agent's
        // 30-second budget, which would make Tester report a false failure and burn rounds on
        // replans. The main agent keeps its own 30-second executor untouched.
        var orchestrationShellExecutor = new LocalShellExecutor(new LocalShellExecutorOptions
        {
            WorkingDirectory = workingDir,
            ConfineWorkingDirectory = false,
            Policy = new ShellPolicy(denyList: ShellDenyListPatterns),
            Timeout = TimeSpan.FromMinutes(5),
        });

        // search_files: local recursive grep over the working folder (replaces hosted web search).
        AIFunction searchFilesTool = AIFunctionFactory.Create(
            ([Description("Подстрока или regex-шаблон для поиска.")] string pattern,
             [Description("Необязательная вложенная папка внутри рабочей папки для ограничения области поиска (пусто = вся папка).")] string path = "") =>
                SearchFiles(workingDir, path, pattern),
            name: "search_files",
            description: "Поиск по содержимому файлов в рабочей папке (рекурсивно). " +
                        "Возвращает пути подходящих файлов и совпадающие строки. " +
                        "Используйте это, чтобы найти код, данные или заметки по содержимому, а не угадывать.");

        var instructions =
            $"""
    ## Инструкции ReAct-ассистента

    Вы — автономный ассистент для работы с кодом и данными, действующий по циклу ReAct:
    **размышляйте** над запросом, выберите **инструмент**, выполните **действие**, затем изучите
    **наблюдение**, прежде чем решать следующий шаг. Повторяйте, пока запрос не будет выполнен,
    затем ответьте пользователю.

    Рабочая папка выбрана пользователем при запуске: {workingDir}

    ### Доступные инструменты

    - `file_access` — чтение, запись, просмотр списка и редактирование файлов в рабочей папке.
    - `search_files` — поиск по содержимому файлов рабочей папки по шаблону (локальный поиск).
    - `run_shell` — выполнение команд PowerShell в дочернем процессе (рабочий каталог — рабочая
      папка). Пользователь подтверждает каждый запуск в диалоговом окне. Сначала изучите
      состояние, прежде чем что-либо менять; заранее объясните свой план. Этим же способом можно
      читать документы-персоны (например, `Get-Content "{Path.Combine(baseDir, "agents", "researcher", "AGENTS.md")}"`).
    - `execute_code` — пишите и запускайте Python в песочнице, чтобы вычислить или проверить результат.
      Предпочитайте запуск кода рассуждениям о том, что произошло бы.
    - `magentic_codegen` — многоагентная оркестрация для крупных задач кодогенерации: архитектор
      изучает кодовую базу, планировщик строит пошаговый план, затем цикл «кодер → ревьюер →
      тестировщик» выполняет его под управлением менеджера. Запускайте её для задач в несколько
      файлов или требующих проверки сборкой и тестами; мелкие правки делайте сами через
      `file_access` и `run_shell`. Внутри оркестрации подтверждения не запрашиваются —
      пользователь подтверждает только сам вызов. Отчёт о запуске сохраняется в
      `{Path.Combine(workingDir, "magentic")}`.

    ### Стиль работы

    - Для любой нетривиальной задачи сначала составьте список дел (todo) и пройдите по нему.
    - Сначала читайте, потом пишите: используйте `file_access`/`search_files`, чтобы изучить существующие файлы.
    - Внешние факты (версии, даты, цены, стандарты, новости, состояние рынка) берите только из
      `web_search` + `web_fetch` и указывайте URL источника рядом с фактом. Ваши собственные знания
      устарели — не отвечайте на такие вопросы по памяти. Если веб-инструменты недоступны, прямо
      скажите об этом и о том, на какой момент актуальны данные, вместо того чтобы называть цифры.
    - Если задача соответствует навыку (SKILL, обнаруженному в skills/), загрузите его и следуйте его сценарию.
    - Показывайте свою работу: включайте выполненные команды/код и ключевые наблюдения.
    - Сохраняйте долговременные выводы в файловую память для следующих сессий.

    ### Скрипты dotnet (C#)

    - Скрипты C# — это file-based программы .NET 10: один `.cs`-файл с top-level statements, без csproj.
    - Все скрипты сохраняйте ТОЛЬКО в отдельной папке `scripts/` внутри рабочей папки: {scriptsDir}
    - Запуск через `run_shell` (в дочернем процессе): `dotnet run scripts\имя.cs -- <аргументы>`.
    - NuGet-пакеты подключайте директивой `#:package Имя@Версия` в начале скрипта.
    - Готовые примеры уже лежат в scripts/: `hello.cs` (аргументы и вывод), `sysinfo.cs` (система и диски),
      `todo-report.cs` (обход файлов и Markdown-отчёт).
    - Специализированные роли для скриптов: `dotnet-scripter`, `dotnet-analyst`, `dotnet-automator`
      (см. папку agents ниже); подробный сценарий — в навыке `dotnet-script`.

    ### Плагины

    - Плагины — это C#-код, который приложение само компилирует и выполняет (Roslyn, без csproj).
      Каждый плагин живёт в своей папке: {Path.Combine(baseDir, "plugins")}\<имя>\plugin.cs
    - Два типа плагинов:
      1. Одноразовый (`IOneShotPlugin`) — становится инструментом агента (AIFunction): метод
         `Delegate CreateHandler(IPluginContext)` возвращает делегат, чьи параметры (с атрибутами
         `[Description]`) образуют схему параметров инструмента; имя инструмента = `Name` плагина.
      2. Резидентный (`IResidentPlugin`) — загружается вместе с приложением: `StartAsync` работает всё время
         (например, backend Telegram-бота), `GetTools()` отдаёт инструменты плагина агенту.
    - Инструменты: `plugin_create(name, sourceCode)` — создать/обновить, скомпилировать и сразу загрузить
      плагин; `plugin_load(name)` — загрузить плагин из существующей папки без перезапуска;
      `plugin_list()` — список плагинов.
    - Контракт (namespace `Harness.Core.Plugins`):
      `IHarnessPlugin` — свойства `string Name`, `string Description`;
      `IOneShotPlugin : IHarnessPlugin` — `Delegate CreateHandler(IPluginContext)`;
      `IResidentPlugin : IHarnessPlugin` — `IReadOnlyList<AIFunction> GetTools()` и `Task StartAsync(IPluginContext, CancellationToken)`;
      `IPluginContext` — `string PluginDirectory`, `string WorkingDirectory` (рабочая папка сессии;
      плагин, отдающий файлы наружу, обязан ограничиваться ею), `void Log(string)` (пишет в plugin.log и в чат),
      `Task<string> AskAgentAsync(string, CancellationToken)` — передать запрос пользователя агенту
      и получить его ответ (так, например, Telegram-бот пересылает входящие сообщения агенту).
    - Стандартные using (System, System.IO, System.Linq, System.Net.Http, System.Threading.Tasks и т.п.)
      подключены неявно; остальные (например, Microsoft.Extensions.AI, System.ComponentModel, System.Text.Json)
      указывайте явно. Доступны все сборки приложения; NuGet-пакеты добавить нельзя.
    - Плагин, созданный через `plugin_create`, загружается сразу (горячая загрузка); его инструменты
      становятся доступны со следующего сообщения. Обновление кода уже загруженного плагина вступает
      в силу только после перезапуска приложения.
    - Примеры уже в папке plugins: `hello-once` (одноразовый инструмент с параметром),
      `telegram-bot` (резидентный backend Telegram-бота, пересылающий сообщения агенту),
      `web-search` (резидентный, даёт инструменты `web_search` и `web_fetch` — живой доступ в интернет).

    ### Папки agents и skills

    - `{Path.Combine(baseDir, "skills")}\<skill>\SKILL.md` — обнаруживаемые навыки (загружаются по мере необходимости провайдером навыков).
    - `{Path.Combine(baseDir, "agents")}\<name>\AGENTS.md` — документы-персоны/роли. Прочитайте нужный через `run_shell`, когда хотите принять роль.
    """;

        // Ollama Cloud: Bearer-token auth via HttpClient (API key from secret.json).
        var http = new HttpClient { BaseAddress = new Uri(endpoint) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var ollama = new OllamaApiClient(http, modelName);

        var host = new AgentHost(
            workingDir,
            modelName,
            DefaultContextWindowTokens,
            instructions,
            searchFilesTool,
            pluginManager,
            tracingSourceName,
            tracerProvider,
            codeAct,
            shellExecutor,
            orchestrationShellExecutor,
            http,
            ollama);

        // Wire the plugin → agent channel first, then load plugins: their tools are folded
        // into the agent by the rebuild below.
        pluginManager.AgentInvoker = host.RunPluginRequestAsync;
        pluginManager.LoadPlugins();
        host.RefreshPluginToolsIfNeeded();

        // Adopt the configured model's real context window (rebuilds the agent only
        // when the reported value differs from the default).
        var contextWindow = await host.GetModelContextLengthAsync(modelName).ConfigureAwait(false);
        if (contextWindow is int reported && reported != host.ContextWindowTokens)
        {
            host.ContextWindowTokens = reported;
            host.Agent = host.BuildAgent();
        }

        return host;
    }

    /// <summary>
    /// Builds the harness agent from the host's current state — called at construction and
    /// again whenever a model switch changes <see cref="ContextWindowTokens"/>.
    /// </summary>
    private AIAgent BuildAgent()
    {
        var baseDir = AppContext.BaseDirectory;
        IChatClient chatClient = this.ollama;

        // Recreated on every rebuild so the orchestration always sees the current token
        // budgets (model switch) and the current plugin tools (hot load).
        var magentic = new Magentic(
            chatClient,
            this.WorkingDirectory,
            Path.Combine(baseDir, "agent-files"),
            this.codeAct,
            this.orchestrationShellExecutor,
            this.searchFilesTool,
            () => this.pluginManager.GetAgentTools(),
            this.ContextWindowTokens,
            this.OutputTokens,
            this.tracingSourceName);

        return chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            MaxContextWindowTokens = this.ContextWindowTokens,
            MaxOutputTokens = this.OutputTokens,
            Name = "ReActAgent",
            Description = "ReAct-агент с доступом к файлам, локальным поиском, оболочкой, выполнением кода, навыками и OpenTelemetry.",
            OpenTelemetrySourceName = this.tracingSourceName,

            // FileMemory: persistent notes across sessions.
            FileMemoryStore = new FileSystemAgentFileStore(Path.Combine(baseDir, "agent-files")),
            // FileAccess (opt-in): root the read/write/list/edit tools at the chosen working folder.
            FileAccessStore = new FileSystemAgentFileStore(this.WorkingDirectory),

            // WebSearch is off for Ollama (no hosted web-search tool). Local search_files replaces it.
            DisableWebSearch = true,

            // Approvals: read-only file_access is auto-approved; writes, shell, and code still prompt.
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [FileAccessProvider.ReadOnlyToolsAutoApprovalRule],
            },

            // Start in "execute" mode for quick actions; the UI has a mode selector for "plan".
            AgentModeProviderOptions = new AgentModeProviderOptions { DefaultMode = "execute" },

            // Context providers: CodeAct + shell environment info injected into the system prompt.
            // (TodoProvider, AgentModeProvider, FileMemory, ToolApproval, AgentSkillsProvider are on by default.)
            AIContextProviders = [this.codeAct, new ShellEnvironmentProvider(this.shellExecutor)],

            ChatOptions = new ChatOptions
            {
                Instructions = this.instructions,
                MaxOutputTokens = this.OutputTokens,
                Tools =
                [
                    this.searchFilesTool,
                    this.shellExecutor.AsAIFunction(requireApproval: true),
                    magentic.AsAIFunction(),
                    .. this.pluginManager.GetAgentTools(),
                ],
                Reasoning = new() { Effort = ReasoningEffort.Medium },

                // Research/analysis harness: sampling is pinned to greedy decoding. Without these
                // the endpoint applies the model's own defaults (Ollama: temperature 0.8 / top_p 0.9),
                // which make the model invent plausible-but-false facts instead of saying "unknown".
                Temperature = 0f,
                TopP = 1f,
                Seed = 0,
            },
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.pluginManager.DisposeAsync().ConfigureAwait(false);
        this.codeAct.Dispose();
        await this.shellExecutor.DisposeAsync().ConfigureAwait(false);
        await this.orchestrationShellExecutor.DisposeAsync().ConfigureAwait(false);
        this.http.Dispose();
        this.tracerProvider?.Dispose();
    }

    // Copies the bundled example scripts (dotnet-scripts/ next to the exe) into
    // <workingDir>\scripts on first run. Existing files are never overwritten.
    private static void SeedExampleScripts(string examplesDir, string scriptsDir)
    {
        try
        {
            if (!Directory.Exists(examplesDir))
            {
                return;
            }

            foreach (var source in Directory.EnumerateFiles(examplesDir, "*.cs"))
            {
                var target = Path.Combine(scriptsDir, Path.GetFileName(source));
                if (!File.Exists(target))
                {
                    File.Copy(source, target);
                }
            }
        }
        catch (IOException ex) { Log.Warning(ex, "Example script seeding failed"); }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Example script seeding failed"); }
    }

    // Noise filter for search_files: repository/build/dependency folders and binary files.
    // Without it a single .git or bin\ tree exhausts the match budget with garbage lines
    // (ReadAllLines over a binary file yields mojibake), so the agent never sees the real
    // sources and fills the gaps from its own priors.
    private static readonly string[] SkipDirectories =
        [".git", ".vs", ".idea", "bin", "obj", "node_modules", ".venv", "packages", "TestResults"];

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".dat", ".cache", ".nupkg",
        ".zip", ".7z", ".gz", ".tar", ".rar",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svgz", ".pdf",
        ".mp3", ".mp4", ".avi", ".mkv", ".woff", ".woff2", ".ttf", ".otf", ".eot",
    };

    private static bool ShouldSkipFile(string root, string file)
    {
        if (SkipExtensions.Contains(Path.GetExtension(file)))
        {
            return true;
        }

        var relativeDir = Path.GetDirectoryName(Path.GetRelativePath(root, file));
        return !string.IsNullOrEmpty(relativeDir)
            && relativeDir
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => SkipDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    // Recursive content search under the working folder (the search_files tool body).
    private static string SearchFiles(string workingDir, string? relativePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Ошибка: параметр 'pattern' обязателен.";
        }

        var root = string.IsNullOrWhiteSpace(relativePath)
            ? workingDir
            : Path.GetFullPath(Path.Combine(workingDir, relativePath));

        if (!root.StartsWith(workingDir, StringComparison.OrdinalIgnoreCase))
        {
            return "Ошибка: путь должен оставаться в пределах рабочей папки.";
        }

        if (!Directory.Exists(root))
        {
            return $"Ошибка: папка не найдена: {root}";
        }

        var regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        var results = new List<string>();
        const int MaxMatches = 200;

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (results.Count >= MaxMatches)
            {
                results.Add($"... (обрезано, более {MaxMatches} совпадений)");
                break;
            }

            if (ShouldSkipFile(root, file))
            {
                continue;
            }

            try
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (results.Count >= MaxMatches)
                    {
                        break;
                    }

                    if (regex.IsMatch(lines[i]))
                    {
                        var rel = Path.GetRelativePath(workingDir, file);
                        var snippet = lines[i].Length > 200 ? lines[i][..200] + "…" : lines[i];
                        results.Add($"{rel}:{i + 1}: {snippet}");
                    }
                }
            }
            catch (IOException ex) { Log.Debug(ex, "search_files: skipping unreadable file {File}", file); }
            catch (UnauthorizedAccessException ex) { Log.Debug(ex, "search_files: skipping no-access file {File}", file); }
        }

        return results.Count == 0
            ? $"Совпадений для '{pattern}' в {Path.GetRelativePath(workingDir, root)} не найдено."
            : string.Join(Environment.NewLine, results);
    }

    // Ollama Cloud credentials loaded from secret.json.
    private sealed record OllamaSecret(string Endpoint = "", string Model = "", string ApiKey = "");
    private sealed record SecretConfig(OllamaSecret? Ollama = null);

    private static OllamaSecret LoadOllamaSecret(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"secret.json not found: {path}{Environment.NewLine}" +
                "Create it next to the csproj: " +
                "{ \"Ollama\": { \"Endpoint\": \"https://ollama.com\", \"Model\": \"glm-5.2:cloud\", \"ApiKey\": \"<key>\" } }");
        }

        var cfg = JsonSerializer.Deserialize<SecretConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("secret.json is empty or contains invalid JSON.");

        return cfg.Ollama ?? new OllamaSecret();
    }
}
