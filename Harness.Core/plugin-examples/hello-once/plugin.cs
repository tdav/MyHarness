// Example one-shot plugin (type 1): becomes an agent tool (AIFunction). The delegate
// returned by CreateHandler runs once per tool call; its [Description]-annotated
// parameters form the tool's parameter schema.
using Harness.Core.Plugins;
using System.ComponentModel;

public sealed class HelloOncePlugin : IOneShotPlugin
{
    public string Name => "hello_once";

    public string Description => "Пример одноразового плагина-инструмента: приветствие и сведения о системе.";

    public Delegate CreateHandler(IPluginContext context) =>
        ([Description("Имя, с которым нужно поздороваться.")] string userName) =>
        {
            context.Log($"hello_once вызван для '{userName}'.");
            return $"Привет, {userName}! ОС: {Environment.OSVersion}, .NET: {Environment.Version}, время: {DateTime.Now:HH:mm:ss}";
        };
}
