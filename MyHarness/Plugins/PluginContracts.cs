using Microsoft.Extensions.AI;

namespace MyHarnessWin.Plugins;

/// <summary>
/// Runtime services available to a plugin: its own folder (for config/state files)
/// and a log sink (written to plugin.log inside that folder).
/// </summary>
public interface IPluginContext
{
    /// <summary>Gets the plugin's own folder (plugins\&lt;name&gt; next to the exe).</summary>
    string PluginDirectory { get; }

    /// <summary>Appends a timestamped line to plugin.log in the plugin folder.</summary>
    void Log(string message);
}

/// <summary>Common identity of every plugin.</summary>
public interface IHarnessPlugin
{
    /// <summary>Gets the display name of the plugin.</summary>
    string Name { get; }

    /// <summary>Gets a short description of what the plugin is for.</summary>
    string Description { get; }
}

/// <summary>
/// Type 1: a plugin that runs once on demand (via the plugin_run tool) and finishes.
/// The assembly is unloaded after the run.
/// </summary>
public interface IOneShotPlugin : IHarnessPlugin
{
    /// <summary>Executes the plugin once and returns a human-readable result.</summary>
    Task<string> RunAsync(IPluginContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Type 2: a plugin loaded together with the application. <see cref="StartAsync"/> may run
/// for the whole application lifetime (e.g. a Telegram bot polling loop) and is cancelled
/// on shutdown. <see cref="GetTools"/> exposes the plugin's tools to the harness agent.
/// </summary>
public interface IResidentPlugin : IHarnessPlugin
{
    /// <summary>Gets the tools this plugin contributes to the agent.</summary>
    IReadOnlyList<AIFunction> GetTools();

    /// <summary>Starts the plugin; the token is cancelled when the application shuts down.</summary>
    Task StartAsync(IPluginContext context, CancellationToken cancellationToken);
}
