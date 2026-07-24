// Example one-shot plugin (type 1): compiled and executed on demand via the
// plugin_run tool, then unloaded. Explicit usings are required (no implicit usings).
using MyHarnessWin.Plugins;

public sealed class HelloOncePlugin : IOneShotPlugin
{
    public string Name => "hello-once";

    public string Description => "Пример одноразового плагина: возвращает сведения о системе.";

    public Task<string> RunAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Log("hello-once выполнен.");
        return Task.FromResult(
            $"Привет из плагина! ОС: {Environment.OSVersion}, .NET: {Environment.Version}, время: {DateTime.Now:HH:mm:ss}");
    }
}
