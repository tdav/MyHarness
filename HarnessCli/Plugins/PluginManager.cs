using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.AI;
using Serilog;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

namespace HarnessCli.Plugins;

/// <summary>
/// Owns the plugins folder (plugins\ next to the exe): compiles plugin sources with Roslyn
/// in-process (no csproj needed) and hot-loads them into the running application.
/// One-shot plugins become <see cref="AIFunction"/> tools of the agent (their delegate
/// parameters, annotated with [Description], form the tool schema); resident plugins run
/// for the application lifetime and contribute their own tools. Plugin management itself
/// is exposed as agent tools (plugin_create / plugin_load / plugin_list), so the agent can
/// author, compile and load plugins. Plugin logs go to plugin.log and to the chat output
/// via <see cref="PluginLog"/>; plugins reach the agent through <see cref="AgentInvoker"/>.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    /// <summary>
    /// Whether plugins can work at all in this build. A plugin is C# source compiled by
    /// Roslyn at runtime and loaded from memory — both need a JIT, which the NativeAOT
    /// build does not have. The rest of the app is unaffected, so instead of failing the
    /// startup the plugin surface simply disappears there.
    /// </summary>
    private static bool PluginsSupported => RuntimeFeature.IsDynamicCodeSupported;

    private const string UnsupportedMessage =
        "плагины недоступны в NativeAOT-сборке: компиляция и загрузка кода во время работы " +
        "требуют JIT. Запустите обычную (JIT) сборку, чтобы пользоваться плагинами.";

    private static readonly Regex PluginNamePattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(BuildReferences);

    private readonly CancellationTokenSource shutdownCts = new();
    private readonly object sync = new();
    private readonly List<(IResidentPlugin Plugin, Task RunTask)> residentPlugins = [];
    private readonly List<AIFunction> oneShotFunctions = [];
    private readonly HashSet<string> loadedDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when a plugin is hot-loaded at runtime — the agent must be rebuilt so the
    /// plugin's tools become available (see AgentHost.RefreshPluginToolsIfNeeded).
    /// </summary>
    public event Action? PluginsChanged;

    /// <summary>
    /// Raised for every <see cref="IPluginContext.Log"/> call: (plugin name, message).
    /// The main window subscribes and mirrors the messages into the chat output.
    /// </summary>
    public event Action<string, string>? PluginLog;

    /// <summary>
    /// The channel plugins use to talk to the harness agent:
    /// (plugin name, user message, token) → the agent's final text answer.
    /// Set by AgentHost right after construction, before plugins are loaded.
    /// </summary>
    public Func<string, string, CancellationToken, Task<string>>? AgentInvoker { get; set; }

    /// <summary>Gets the root folder that holds one subfolder per plugin.</summary>
    public string PluginsDirectory { get; }

    public PluginManager(string pluginsDirectory)
    {
        this.PluginsDirectory = pluginsDirectory;
        Directory.CreateDirectory(pluginsDirectory);
    }

    /// <summary>Gets the resident plugins that were successfully loaded and started.</summary>
    public IReadOnlyList<IResidentPlugin> ResidentPlugins
    {
        get
        {
            lock (this.sync)
            {
                return this.residentPlugins.Select(p => p.Plugin).ToList();
            }
        }
    }

    /// <summary>
    /// Compiles and loads every plugin folder (one-shot tools + resident plugins).
    /// Folders that fail to compile are skipped (errors go to the plugin's plugin.log).
    /// Called once at application startup.
    /// </summary>
    public void LoadPlugins()
    {
        if (!PluginsSupported)
        {
            Log.Information("Plugin loading skipped: the AOT build cannot compile or load assemblies at runtime");
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(this.PluginsDirectory))
        {
            try
            {
                this.TryLoadFrom(dir, out _);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load plugin from {Dir}", dir);
                WriteLog(dir, $"Ошибка загрузки плагина: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Hot-loads a plugin folder into the running application (no restart). One-shot tools
    /// and resident plugins start immediately; their tools reach the agent after the current
    /// turn, when the host rebuilds the agent in response to <see cref="PluginsChanged"/>.
    /// </summary>
    public string LoadPlugin(string name)
    {
        var dir = Path.Combine(this.PluginsDirectory, name);
        if (!Directory.Exists(dir))
        {
            return $"Ошибка: плагин '{name}' не найден в {this.PluginsDirectory}.";
        }

        try
        {
            return this.TryLoadFrom(dir, out var message)
                ? $"Плагин '{name}' загружен ({message}); инструменты станут доступны со следующего сообщения."
                : $"Плагин '{name}' не загружен: {message}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hot load of plugin {Plugin} failed", name);
            return $"Ошибка загрузки плагина '{name}': {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the plugin-management tools, every loaded one-shot plugin as an AIFunction,
    /// and every tool contributed by loaded resident plugins.
    /// </summary>
    public IReadOnlyList<AITool> GetAgentTools()
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(
                () => this.ListPlugins(),
                name: "plugin_list",
                description: "Список плагинов: папки в plugins\\, загруженные одноразовые инструменты и резидентные плагины."),
        ];

        // Authoring tools are offered only where they can actually run — in the AOT build
        // they would fail on every call, and an unusable tool in the schema is worse than
        // no tool at all.
        if (PluginsSupported)
        {
            tools.Add(AIFunctionFactory.Create(
                ([Description("Имя плагина: латиница, цифры, '-' и '_'.")] string name,
                 [Description("Полный исходный код плагина (один .cs-файл).")] string sourceCode) =>
                    this.CreatePlugin(name, sourceCode),
                name: "plugin_create",
                description: "Создать или обновить плагин: сохраняет исходник в plugins\\<имя>\\plugin.cs, компилирует " +
                             "и сразу загружает его (одноразовый плагин становится инструментом агента с параметрами, " +
                             "резидентный — запускается)."));
            tools.Add(AIFunctionFactory.Create(
                ([Description("Имя плагина (папка внутри plugins).")] string name) => this.LoadPlugin(name),
                name: "plugin_load",
                description: "Загрузить плагин в работающее приложение без перезапуска. Плагины, созданные через " +
                             "plugin_create, загружаются автоматически — этот инструмент нужен для папок, добавленных вручную."));
        }

        lock (this.sync)
        {
            tools.AddRange(this.oneShotFunctions);
            foreach (var (plugin, _) in this.residentPlugins)
            {
                tools.AddRange(plugin.GetTools());
            }
        }

        return tools;
    }

    /// <summary>Saves the source into plugins\name\plugin.cs, compiles and hot-loads it.</summary>
    public string CreatePlugin(string name, string sourceCode)
    {
        if (!PluginNamePattern.IsMatch(name))
        {
            return "Ошибка: имя плагина может содержать только латиницу, цифры, '-' и '_'.";
        }

        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return "Ошибка: исходный код пуст.";
        }

        var dir = Path.Combine(this.PluginsDirectory, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.cs"), sourceCode);

        bool alreadyLoaded;
        lock (this.sync)
        {
            alreadyLoaded = this.loadedDirs.Contains(dir);
        }

        if (alreadyLoaded)
        {
            // Verify the new source still compiles, but the running instance keeps the old code.
            var (assembly, alc, errors) = Compile(dir);
            if (assembly is null)
            {
                return $"Исходник сохранён, но компиляция не удалась:{Environment.NewLine}{errors}";
            }

            alc!.Unload();
            return $"Плагин '{name}' скомпилирован успешно. Плагин уже загружен — " +
                   "обновлённый код вступит в силу после перезапуска приложения.";
        }

        return this.TryLoadFrom(dir, out var message)
            ? $"Плагин '{name}' скомпилирован и загружен ({message}); инструменты станут доступны со следующего сообщения."
            : $"Исходник сохранён, но плагин не загружен: {message}";
    }

    /// <summary>Lists plugin folders, loaded one-shot tools and resident plugins.</summary>
    public string ListPlugins()
    {
        var sb = new StringBuilder();
        var dirs = Directory.EnumerateDirectories(this.PluginsDirectory).ToList();

        sb.AppendLine($"Папка плагинов: {this.PluginsDirectory}");
        sb.AppendLine(dirs.Count == 0 ? "Плагинов нет." : $"Папки плагинов: {string.Join(", ", dirs.Select(Path.GetFileName))}");

        if (!PluginsSupported)
        {
            sb.AppendLine($"Внимание: {UnsupportedMessage}");
            return sb.ToString();
        }

        List<AIFunction> oneShots;
        List<(IResidentPlugin Plugin, Task RunTask)> loaded;
        lock (this.sync)
        {
            oneShots = [.. this.oneShotFunctions];
            loaded = [.. this.residentPlugins];
        }

        if (oneShots.Count > 0)
        {
            sb.AppendLine($"Одноразовые плагины-инструменты: {string.Join(", ", oneShots.Select(f => f.Name))}");
        }

        if (loaded.Count > 0)
        {
            sb.AppendLine("Загруженные резидентные плагины:");
            foreach (var (plugin, runTask) in loaded)
            {
                var toolNames = plugin.GetTools().Select(t => t.Name);
                var state = runTask.IsFaulted ? "ошибка" : runTask.IsCompleted ? "завершён" : "работает";
                sb.AppendLine($"- {plugin.Name} [{state}]: {plugin.Description} Инструменты: {string.Join(", ", toolNames)}");
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        this.shutdownCts.Cancel();
        List<Task> runTasks;
        lock (this.sync)
        {
            runTasks = this.residentPlugins.Select(p => p.RunTask).ToList();
        }

        try
        {
            await Task.WhenAll(runTasks)
                .WaitAsync(TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort shutdown: plugins that ignore cancellation are abandoned.
            Log.Warning(ex, "Resident plugin shutdown did not complete cleanly");
        }

        this.shutdownCts.Dispose();
    }

    // Compiles the folder and loads its plugins (one-shot tools + resident). Returns false
    // with a reason when nothing was loaded; on success the message summarizes what loaded.
    private bool TryLoadFrom(string dir, out string message)
    {
        lock (this.sync)
        {
            if (this.loadedDirs.Contains(dir))
            {
                message = "уже загружен (обновление кода работающего плагина требует перезапуска приложения)";
                return false;
            }
        }

        var (assembly, alc, errors) = Compile(dir);
        if (assembly is null)
        {
            WriteLog(dir, $"Ошибка компиляции при загрузке: {errors}");
            message = $"ошибка компиляции:{Environment.NewLine}{errors}";
            return false;
        }

        var oneShotTypes = GetPluginTypes<IOneShotPlugin>(assembly);
        var residentTypes = GetPluginTypes<IResidentPlugin>(assembly);
        if (oneShotTypes.Count == 0 && residentTypes.Count == 0)
        {
            alc!.Unload();
            message = "нет класса, реализующего IOneShotPlugin или IResidentPlugin";
            return false;
        }

        var parts = new List<string>();
        var context = new PluginContext(this, dir);
        lock (this.sync)
        {
            foreach (var type in oneShotTypes)
            {
                var plugin = (IOneShotPlugin)Activator.CreateInstance(type)!;
                this.oneShotFunctions.Add(AIFunctionFactory.Create(
                    plugin.CreateHandler(context),
                    name: plugin.Name,
                    description: plugin.Description));
                parts.Add($"инструмент '{plugin.Name}'");
            }

            foreach (var type in residentTypes)
            {
                var plugin = (IResidentPlugin)Activator.CreateInstance(type)!;
                var runTask = Task.Run(() => plugin.StartAsync(context, this.shutdownCts.Token));
                this.residentPlugins.Add((plugin, runTask));
                parts.Add($"резидентный '{plugin.Name}'");
            }

            this.loadedDirs.Add(dir);
        }

        this.PluginsChanged?.Invoke();
        message = string.Join(", ", parts);
        return true;
    }

    // Compiles every .cs file in the plugin folder into an in-memory assembly loaded in a
    // collectible AssemblyLoadContext. Returns (null, null, errors) on failure.
    private static (Assembly? Assembly, AssemblyLoadContext? Alc, string Errors) Compile(string pluginDir)
    {
        if (!PluginsSupported)
        {
            return (null, null, UnsupportedMessage);
        }

        var sources = Directory.GetFiles(pluginDir, "*.cs");
        if (sources.Length == 0)
        {
            return (null, null, "В папке плагина нет .cs-файлов.");
        }

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = sources
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: f))
            .ToList();

        // Mirror the SDK's ImplicitUsings so plugin sources compile like regular app code.
        trees.Add(CSharpSyntaxTree.ParseText(
            """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """,
            parseOptions));

        var compilation = CSharpCompilation.Create(
            $"Plugin_{Path.GetFileName(pluginDir)}_{Guid.NewGuid():N}",
            trees,
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errors = string.Join(
                Environment.NewLine,
                emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(20)
                    .Select(d => d.ToString()));
            return (null, null, errors);
        }

        ms.Position = 0;
        var alc = new AssemblyLoadContext($"plugin:{Path.GetFileName(pluginDir)}", isCollectible: true);
        return (alc.LoadFromStream(ms), alc, string.Empty);
    }

    // Reference set for plugin compilation: the runtime's trusted platform assemblies already
    // include the framework, the host exe and all its NuGet dependencies.
    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                references[path] = MetadataReference.CreateFromFile(path);
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                references.TryAdd(assembly.Location, MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references.Values.ToList();
    }

    private static List<Type> GetPluginTypes<T>(Assembly assembly) =>
        assembly.GetTypes().Where(t => !t.IsAbstract && typeof(T).IsAssignableFrom(t)).ToList();

    private void RaisePluginLog(string pluginName, string message) =>
        this.PluginLog?.Invoke(pluginName, message);

    private static void WriteLog(string pluginDir, string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(pluginDir, "plugin.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (IOException ex) { Log.Warning(ex, "Failed to write plugin.log in {Dir}", pluginDir); }
    }

    // IPluginContext implementation handed to plugins.
    private sealed class PluginContext(PluginManager owner, string pluginDirectory) : IPluginContext
    {
        public string PluginDirectory { get; } = pluginDirectory;

        private string PluginName => Path.GetFileName(this.PluginDirectory);

        public void Log(string message)
        {
            WriteLog(this.PluginDirectory, message);
            owner.RaisePluginLog(this.PluginName, message);
        }

        public Task<string> AskAgentAsync(string message, CancellationToken cancellationToken)
        {
            var invoker = owner.AgentInvoker
                ?? throw new InvalidOperationException("Агент ещё не инициализирован.");
            return invoker(this.PluginName, message, cancellationToken);
        }
    }
}
