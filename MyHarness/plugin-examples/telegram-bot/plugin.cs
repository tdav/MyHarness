// Example resident plugin (type 2): a Telegram bot backend. Loaded together with
// MyHarness.exe, runs a long-polling loop for the whole application lifetime and
// contributes the telegram_send tool to the agent.
//
// Bot token: file "token.txt" inside this plugin folder, or the TELEGRAM_BOT_TOKEN
// environment variable. Without a token the plugin logs a note and stays idle.
using Microsoft.Extensions.AI;
using MyHarnessWin.Plugins;
using System.ComponentModel;
using System.Text.Json;

public sealed class TelegramBotPlugin : IResidentPlugin
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private string apiBase = string.Empty;
    private long lastChatId;

    public string Name => "telegram-bot";

    public string Description =>
        "Backend Telegram-бота: принимает входящие сообщения (long polling) и отправляет ответы в последний активный чат.";

    public IReadOnlyList<AIFunction> GetTools() =>
    [
        AIFunctionFactory.Create(
            ([Description("Текст сообщения.")] string text) => this.SendAsync(text),
            name: "telegram_send",
            description: "Отправить текст в последний активный чат Telegram-бота."),
    ];

    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var tokenFile = Path.Combine(context.PluginDirectory, "token.txt");
        var token = File.Exists(tokenFile)
            ? File.ReadAllText(tokenFile).Trim()
            : Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            context.Log("Токен не найден (token.txt рядом с плагином или TELEGRAM_BOT_TOKEN) — бот не запущен.");
            return;
        }

        this.apiBase = $"https://api.telegram.org/bot{token}";
        context.Log("Бот запущен, ожидание сообщений…");

        long offset = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var json = await this.http.GetStringAsync(
                    $"{this.apiBase}/getUpdates?timeout=60&offset={offset}", cancellationToken);

                using var doc = JsonDocument.Parse(json);
                foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
                {
                    offset = update.GetProperty("update_id").GetInt64() + 1;
                    if (!update.TryGetProperty("message", out var message) ||
                        !message.TryGetProperty("text", out var text))
                    {
                        continue;
                    }

                    this.lastChatId = message.GetProperty("chat").GetProperty("id").GetInt64();
                    context.Log($"Входящее из чата {this.lastChatId}: {text.GetString()}");
                    await this.SendAsync($"Принято: {text.GetString()}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                context.Log($"Ошибка опроса: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        context.Log("Бот остановлен.");
    }

    private async Task<string> SendAsync(string text)
    {
        if (this.lastChatId == 0)
        {
            return "Нет активного чата: боту ещё никто не писал.";
        }

        await this.http.GetStringAsync(
            $"{this.apiBase}/sendMessage?chat_id={this.lastChatId}&text={Uri.EscapeDataString(text)}");
        return "Отправлено.";
    }
}
