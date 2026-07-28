using Microsoft.Extensions.AI;

namespace HarnessCli.Plugins;

/// <summary>
/// Runtime services available to a plugin: its own folder (for config/state files),
/// a log sink (plugin.log + the chat output in the main window) and a channel to the
/// harness agent for forwarding user requests and returning its answers.
/// </summary>
public interface IPluginContext
{
    /// <summary>Gets the plugin's own folder (plugins\&lt;name&gt; next to the exe).</summary>
    string PluginDirectory { get; }

    /// <summary>
    /// Gets the session's working folder. Plugins that read or send files out of the process
    /// (e.g. the Telegram bot) must confine themselves to this folder.
    /// </summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// Appends a timestamped line to plugin.log in the plugin folder and shows it in the
    /// chat output of the main window.
    /// </summary>
    void Log(string message);

    /// <summary>
    /// Sends a user request to the harness agent and returns its final text answer.
    /// Each plugin keeps its own persistent conversation session. Tool-approval requests
    /// raised during the run are auto-approved (there is no interactive user to ask).
    /// </summary>
    Task<string> AskAgentAsync(string message, CancellationToken cancellationToken);
}

/// <summary>Common identity of every plugin.</summary>
public interface IHarnessPlugin
{
    /// <summary>Gets the plugin name; for one-shot plugins it becomes the agent tool name.</summary>
    string Name { get; }

    /// <summary>Gets a short description; for one-shot plugins it becomes the tool description.</summary>
    string Description { get; }
}

/// <summary>
/// Type 1: a plugin exposed to the agent as an <see cref="AIFunction"/> tool. The delegate
/// returned by <see cref="CreateHandler"/> runs once per tool call and finishes; its
/// parameters (annotated with [Description] attributes) become the tool's parameter schema.
/// </summary>
public interface IOneShotPlugin : IHarnessPlugin
{
    /// <summary>
    /// Creates the handler delegate that is wrapped into an <see cref="AIFunction"/> via
    /// AIFunctionFactory. Annotate the parameters with [Description] so the model knows
    /// how to fill them.
    /// </summary>
    Delegate CreateHandler(IPluginContext context);
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
