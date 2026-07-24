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
using System.Text.Json;
using System.Text.RegularExpressions;
using MyHarnessWin.Plugins;
using MyHarnessWin.Tracing;

namespace MyHarnessWin;

/// <summary>
/// Builds and owns the ReAct harness agent (Ollama Cloud backend) together with all of its
/// disposable infrastructure: the Hyperlight Python sandbox, the local shell executor
/// (child PowerShell processes rooted at the user-selected working folder), the HTTP client
/// and the OpenTelemetry tracer. The WinForms UI talks only to <see cref="Agent"/>.
/// Mirrors the agent setup of Test04-MyHarness; only the console UI was replaced.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    /// <summary>Fallback context window for models that do not report their context length.</summary>
    public const int DefaultContextWindowTokens = 131_072;

    /// <summary>Upper bound for the output-token budget; shrinks for small-context models.</summary>
    public const int MaxOutputTokens = 16_384;

    private const string TracingSourceName = "Test05.Win";

    private readonly TracerProvider? tracerProvider;
    private readonly HyperlightCodeActProvider codeAct;
    private readonly LocalShellExecutor shellExecutor;
    private readonly HttpClient http;
    private readonly OllamaApiClient ollama;
    private readonly string instructions;
    private readonly AIFunction searchFilesTool;
    private readonly PluginManager pluginManager;

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
        TracerProvider? tracerProvider,
        HyperlightCodeActProvider codeAct,
        LocalShellExecutor shellExecutor,
        HttpClient http,
        OllamaApiClient ollama)
    {
        this.WorkingDirectory = workingDirectory;
        this.ModelName = modelName;
        this.ContextWindowTokens = contextWindowTokens;
        this.instructions = instructions;
        this.searchFilesTool = searchFilesTool;
        this.pluginManager = pluginManager;
        this.tracerProvider = tracerProvider;
        this.codeAct = codeAct;
        this.shellExecutor = shellExecutor;
        this.http = http;
        this.ollama = ollama;
        this.Agent = this.BuildAgent();
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
    /// </summary>
    public static async Task<AgentHost> CreateAsync(string workingDir)
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

        var tracerProvider = HarnessTracing.CreateFileTracerProvider(TracingSourceName);

        // Plugins live next to the exe (like skills/ and agents/); resident plugins are
        // compiled and started here so their tools can be handed to the agent below.
        var pluginManager = new PluginManager(Path.Combine(baseDir, "plugins"));
        pluginManager.LoadResidentPlugins();

        var codeAct = new HyperlightCodeActProvider(
            HyperlightCodeActProviderOptions.CreateForWasm(PythonGuestModule.GetModulePath()));

        var shellExecutor = new LocalShellExecutor(new LocalShellExecutorOptions
        {
            WorkingDirectory = workingDir,
            ConfineWorkingDirectory = false,
            Policy = new ShellPolicy(denyList:
            [
                @"\brm\s+-rf\b",
                @"\bsudo\b",
                @":\(\)\s*\{",          // fork-bomb shape
                @"\bmkfs\b",
                @">\s*/dev/sd",
                @"\bFormat-Volume\b",
            ]),
            Timeout = TimeSpan.FromSeconds(30),
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

    ### Стиль работы

    - Для любой нетривиальной задачи сначала составьте список дел (todo) и пройдите по нему.
    - Сначала читайте, потом пишите: используйте `file_access`/`search_files`, чтобы изучить существующие файлы.
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
      1. Одноразовый (`IOneShotPlugin`) — метод `RunAsync` выполняется по требованию через `plugin_run` и завершается.
      2. Резидентный (`IResidentPlugin`) — загружается вместе с приложением: `StartAsync` работает всё время
         (например, backend Telegram-бота), `GetTools()` отдаёт инструменты плагина агенту.
    - Инструменты: `plugin_create(name, sourceCode)` — создать/обновить и скомпилировать плагин;
      `plugin_run(name)` — выполнить одноразовый плагин; `plugin_list()` — список плагинов.
    - Контракт (namespace `MyHarnessWin.Plugins`):
      `IHarnessPlugin` — свойства `string Name`, `string Description`;
      `IOneShotPlugin : IHarnessPlugin` — `Task<string> RunAsync(IPluginContext, CancellationToken)`;
      `IResidentPlugin : IHarnessPlugin` — `IReadOnlyList<AIFunction> GetTools()` и `Task StartAsync(IPluginContext, CancellationToken)`;
      `IPluginContext` — `string PluginDirectory`, `void Log(string)`.
    - Стандартные using (System, System.IO, System.Linq, System.Net.Http, System.Threading.Tasks и т.п.)
      подключены неявно; остальные (например, Microsoft.Extensions.AI, System.Text.Json) указывайте явно.
      Доступны все сборки приложения; NuGet-пакеты добавить нельзя.
    - Новые резидентные плагины подхватываются при следующем запуске приложения; одноразовые — сразу.
    - Примеры уже в папке plugins: `hello-once` (одноразовый), `telegram-bot` (резидентный backend Telegram-бота).

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
            tracerProvider,
            codeAct,
            shellExecutor,
            http,
            ollama);

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

        return chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            MaxContextWindowTokens = this.ContextWindowTokens,
            MaxOutputTokens = this.OutputTokens,
            Name = "ReActAgent",
            Description = "ReAct-агент с доступом к файлам, локальным поиском, оболочкой, выполнением кода, навыками и OpenTelemetry.",
            OpenTelemetrySourceName = TracingSourceName,

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
                Tools = [this.searchFilesTool, this.shellExecutor.AsAIFunction(requireApproval: true), .. this.pluginManager.GetAgentTools()],
                Reasoning = new() { Effort = ReasoningEffort.Medium },
            },
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.pluginManager.DisposeAsync().ConfigureAwait(false);
        this.codeAct.Dispose();
        await this.shellExecutor.DisposeAsync().ConfigureAwait(false);
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
        const int MaxMatches = 50;

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (results.Count >= MaxMatches)
            {
                results.Add("... (обрезано, более 50 совпадений)");
                break;
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
