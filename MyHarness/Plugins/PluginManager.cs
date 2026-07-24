using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.AI;
using Serilog;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

namespace MyHarnessWin.Plugins;

/// <summary>
/// Owns the plugins folder (plugins\ next to the exe): compiles plugin sources with Roslyn
/// in-process (no csproj needed), runs one-shot plugins on demand in a collectible
/// <see cref="AssemblyLoadContext"/>, keeps resident plugins running for the application
/// lifetime, and exposes plugin management as agent tools (plugin_create / plugin_run /
/// plugin_list) so the agent itself can author, compile and execute plugins.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    private static readonly Regex PluginNamePattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(BuildReferences);

    private readonly CancellationTokenSource shutdownCts = new();
    private readonly List<(IResidentPlugin Plugin, Task RunTask)> residentPlugins = [];

    /// <summary>Gets the root folder that holds one subfolder per plugin.</summary>
    public string PluginsDirectory { get; }

    public PluginManager(string pluginsDirectory)
    {
        this.PluginsDirectory = pluginsDirectory;
        Directory.CreateDirectory(pluginsDirectory);
    }

    /// <summary>Gets the resident plugins that were successfully loaded and started.</summary>
    public IReadOnlyList<IResidentPlugin> ResidentPlugins => this.residentPlugins.Select(p => p.Plugin).ToList();

    /// <summary>
    /// Compiles every plugin folder and starts all <see cref="IResidentPlugin"/> implementations.
    /// Folders that fail to compile or contain only one-shot plugins are skipped (errors are
    /// written to the plugin's plugin.log). Called once at application startup.
    /// </summary>
    public void LoadResidentPlugins()
    {
        foreach (var dir in Directory.EnumerateDirectories(this.PluginsDirectory))
        {
            try
            {
                var (assembly, alc, errors) = Compile(dir);
                if (assembly is null)
                {
                    WriteLog(dir, $"Ошибка компиляции при загрузке: {errors}");
                    continue;
                }

                var residentTypes = GetPluginTypes<IResidentPlugin>(assembly);
                if (residentTypes.Count == 0)
                {
                    alc!.Unload(); // One-shot-only plugin: nothing to keep resident.
                    continue;
                }

                foreach (var type in residentTypes)
                {
                    var plugin = (IResidentPlugin)Activator.CreateInstance(type)!;
                    var context = new PluginContext(dir);
                    var runTask = Task.Run(() => plugin.StartAsync(context, this.shutdownCts.Token));
                    this.residentPlugins.Add((plugin, runTask));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load resident plugin from {Dir}", dir);
                WriteLog(dir, $"Ошибка загрузки плагина: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets the plugin-management tools plus every tool contributed by loaded resident plugins.
    /// </summary>
    public IReadOnlyList<AITool> GetAgentTools()
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(
                ([Description("Имя плагина: латиница, цифры, '-' и '_'.")] string name,
                 [Description("Полный исходный код плагина (один .cs-файл, using-директивы указывать явно).")] string sourceCode) =>
                    this.CreatePlugin(name, sourceCode),
                name: "plugin_create",
                description: "Создать или обновить плагин: сохраняет исходник в plugins\\<имя>\\plugin.cs и компилирует его. " +
                             "Возвращает результат компиляции и обнаруженный тип плагина (одноразовый / резидентный)."),
            AIFunctionFactory.Create(
                ([Description("Имя плагина (папка внутри plugins).")] string name, CancellationToken cancellationToken) =>
                    this.RunOneShotAsync(name, cancellationToken),
                name: "plugin_run",
                description: "Скомпилировать и выполнить одноразовый плагин (IOneShotPlugin). Возвращает результат RunAsync."),
            AIFunctionFactory.Create(
                () => this.ListPlugins(),
                name: "plugin_list",
                description: "Список плагинов: папки в plugins\\ и загруженные резидентные плагины с их инструментами."),
        ];

        foreach (var (plugin, _) in this.residentPlugins)
        {
            tools.AddRange(plugin.GetTools());
        }

        return tools;
    }

    /// <summary>Saves the source into plugins\name\plugin.cs and compiles it as a validity check.</summary>
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

        var (assembly, alc, errors) = Compile(dir);
        if (assembly is null)
        {
            return $"Исходник сохранён, но компиляция не удалась:{Environment.NewLine}{errors}";
        }

        var oneShot = GetPluginTypes<IOneShotPlugin>(assembly).Count;
        var resident = GetPluginTypes<IResidentPlugin>(assembly).Count;
        alc!.Unload();

        if (oneShot == 0 && resident == 0)
        {
            return "Скомпилировано, но не найден класс, реализующий IOneShotPlugin или IResidentPlugin.";
        }

        var kind = resident > 0
            ? "резидентный (IResidentPlugin) — будет загружен при следующем запуске приложения"
            : "одноразовый (IOneShotPlugin) — выполняется через plugin_run";
        return $"Плагин '{name}' скомпилирован успешно. Тип: {kind}.";
    }

    /// <summary>Compiles and runs a one-shot plugin, then unloads its assembly.</summary>
    public async Task<string> RunOneShotAsync(string name, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(this.PluginsDirectory, name);
        if (!Directory.Exists(dir))
        {
            return $"Ошибка: плагин '{name}' не найден в {this.PluginsDirectory}.";
        }

        var (assembly, alc, errors) = Compile(dir);
        if (assembly is null)
        {
            return $"Ошибка компиляции:{Environment.NewLine}{errors}";
        }

        try
        {
            var type = GetPluginTypes<IOneShotPlugin>(assembly).FirstOrDefault();
            if (type is null)
            {
                return GetPluginTypes<IResidentPlugin>(assembly).Count > 0
                    ? $"'{name}' — резидентный плагин; он загружается при старте приложения, а не через plugin_run."
                    : $"Ошибка: в '{name}' нет класса, реализующего IOneShotPlugin.";
            }

            var plugin = (IOneShotPlugin)Activator.CreateInstance(type)!;
            WriteLog(dir, $"Запуск одноразового плагина '{plugin.Name}'.");
            return await plugin.RunAsync(new PluginContext(dir), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "One-shot plugin {Plugin} failed", name);
            return $"Ошибка выполнения плагина '{name}': {ex.Message}";
        }
        finally
        {
            alc!.Unload();
        }
    }

    /// <summary>Lists plugin folders and the currently loaded resident plugins with their tools.</summary>
    public string ListPlugins()
    {
        var sb = new StringBuilder();
        var dirs = Directory.EnumerateDirectories(this.PluginsDirectory).ToList();

        sb.AppendLine($"Папка плагинов: {this.PluginsDirectory}");
        sb.AppendLine(dirs.Count == 0 ? "Плагинов нет." : $"Папки плагинов: {string.Join(", ", dirs.Select(Path.GetFileName))}");

        if (this.residentPlugins.Count > 0)
        {
            sb.AppendLine("Загруженные резидентные плагины:");
            foreach (var (plugin, runTask) in this.residentPlugins)
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
        try
        {
            await Task.WhenAll(this.residentPlugins.Select(p => p.RunTask))
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

    // Compiles every .cs file in the plugin folder into an in-memory assembly loaded in a
    // collectible AssemblyLoadContext. Returns (null, null, errors) on failure.
    private static (Assembly? Assembly, AssemblyLoadContext? Alc, string Errors) Compile(string pluginDir)
    {
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
    private sealed class PluginContext(string pluginDirectory) : IPluginContext
    {
        public string PluginDirectory { get; } = pluginDirectory;

        public void Log(string message) => WriteLog(this.PluginDirectory, message);
    }
}
