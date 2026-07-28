// Resident plugin (type 2): Telegram bot backend for @tdav_harness_bot.
// Loaded together with MyHarness.exe, runs a long-polling loop for the whole
// application lifetime, forwards every incoming message to the harness agent
// (context.AskAgentAsync) and sends the agent's answer back to the chat.
// Contributes three tools:
//   telegram_send          — send plain text
//   telegram_send_markdown — send Markdown-formatted text (parse_mode=MarkdownV2)
//   telegram_send_file     — send a file/document
//
// Bot token is embedded below; can be overridden by token.txt in the plugin folder
// or the TELEGRAM_BOT_TOKEN environment variable.
using Microsoft.Extensions.AI;
using MyHarnessWin.Plugins;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;

public sealed class TelegramBotPlugin : IResidentPlugin
{
    private const string BotToken = "8853258810:AAH7seasB8MKgvEqQIKf_hu93DfPR4FsMI8";
    private const int TelegramMessageLimit = 4000; // API hard limit is 4096.

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private string apiBase = string.Empty;
    private string workingDirectory = string.Empty;
    private long lastChatId;

    public string Name => "telegram-bot";

    public string Description =>
        "Backend Telegram-бота @tdav_harness_bot: пересылает входящие сообщения агенту и возвращает его ответы в чат. Поддерживает отправку текста, Markdown и файлов.";

    public IReadOnlyList<AIFunction> GetTools() =>
    [
        AIFunctionFactory.Create(
            ([Description("Текст сообщения.")] string text) => this.SendAsync(text),
            name: "telegram_send",
            description: "Отправить текст в последний активный чат Telegram-бота."),

        AIFunctionFactory.Create(
            ([Description("Текст сообщения в формате MarkdownV2. Поддерживаются: *жирный*, _курсив_, __подчёркнутый__, ~~зачёркнутый~~, `код`, ```блок кода```, [текст](URL). Спецсимволы экранируются автоматически.")] string markdown,
             [Description("Заголовок сообщения (необязательный, добавляется перед основным текстом жирным шрифтом).")] string? title = null) =>
                this.SendMarkdownAsync(markdown, title),
            name: "telegram_send_markdown",
            description: "Отправить Markdown-сообщение (parse_mode=MarkdownV2) в последний активный чат Telegram-бота."),

        AIFunctionFactory.Create(
            ([Description("Путь к файлу внутри рабочей папки (относительный — от рабочей папки). Файлы вне рабочей папки отправлять запрещено.")] string filePath,
             [Description("Подпись к файлу в формате Markdown (необязательный).")] string? caption = null) =>
                this.SendFileAsync(filePath, caption),
            name: "telegram_send_file",
            description: "Отправить файл в последний активный чат Telegram-бота через sendDocument. Поддерживает Markdown-подпись."),
    ];

    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        // Files may only leave the machine from inside this folder (see SendFileAsync).
        this.workingDirectory = context.WorkingDirectory;

        // Token priority: token.txt file > env variable > embedded constant.
        var tokenFile = Path.Combine(context.PluginDirectory, "token.txt");
        string token;
        if (File.Exists(tokenFile))
        {
            token = File.ReadAllText(tokenFile).Trim();
        }
        else
        {
            var envToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            token = !string.IsNullOrWhiteSpace(envToken) ? envToken : BotToken;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            context.Log("Токен не найден — бот не запущен.");
            return;
        }

        this.apiBase = $"https://api.telegram.org/bot{token}";

        // Verify the token by calling getMe.
        try
        {
            var meJson = await this.http.GetStringAsync($"{this.apiBase}/getMe", cancellationToken);
            using var meDoc = JsonDocument.Parse(meJson);
            if (meDoc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("username", out var username))
            {
                context.Log($"Бот @{username.GetString()} запущен, ожидание сообщений…");
            }
            else
            {
                context.Log($"Бот запущен, ожидание сообщений…");
            }
        }
        catch (Exception ex)
        {
            context.Log($"Не удалось проверить токен (getMe): {ex.Message}");
            context.Log("Бот запускается в режиме опроса…");
        }

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
                    var incoming = text.GetString() ?? string.Empty;
                    context.Log($"Входящее из чата {this.lastChatId}: {incoming}");

                    // Forward the user's message to the harness agent and relay its answer.
                    string answer;
                    try
                    {
                        answer = await context.AskAgentAsync(incoming, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        context.Log($"Ошибка агента: {ex.Message}");
                        answer = "Не удалось обработать запрос, попробуйте ещё раз.";
                    }

                    if (string.IsNullOrWhiteSpace(answer))
                    {
                        answer = "(пустой ответ агента)";
                    }

                    context.Log($"Ответ агента в чат {this.lastChatId}: {answer}");
                    await this.SendAsync(answer);
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

        if (text.Length > TelegramMessageLimit)
        {
            text = text[..TelegramMessageLimit] + "…";
        }

        await this.http.GetStringAsync(
            $"{this.apiBase}/sendMessage?chat_id={this.lastChatId}&text={Uri.EscapeDataString(text)}");
        return "Отправлено.";
    }

    /// <summary>
    /// Sends a Markdown-formatted message using Telegram's MarkdownV2 parse_mode.
    /// The caller provides already-formatted Markdown; this method escapes characters
    /// that are NOT part of MarkdownV2 formatting syntax (i.e. literal special chars
    /// that appear outside formatting entities).
    /// 
    /// IMPORTANT: In MarkdownV2, the following characters MUST be escaped with a
    /// backslash when they appear as literal text (not as part of formatting):
    ///   _ * [ ] ( ) ~ ` > # + - = | { } . !
    /// 
    /// However, since the agent provides text that may already contain MarkdownV2
    /// formatting (like **bold**, __italic__, etc.), we do NOT auto-escape here.
    /// The caller is responsible for proper MarkdownV2 formatting.
    /// If the message fails due to parsing errors, we retry with plain text.
    /// </summary>
    private async Task<string> SendMarkdownAsync(string markdown, string? title)
    {
        if (this.lastChatId == 0)
        {
            return "Нет активного чата: боту ещё никто не писал.";
        }

        // Build the full message text.
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("*").Append(title.Trim()).Append("*\n\n");
        }
        sb.Append(markdown);

        var fullText = sb.ToString();

        if (fullText.Length > TelegramMessageLimit)
        {
            fullText = fullText[..TelegramMessageLimit] + "…";
        }

        // Try sending with MarkdownV2 parse_mode.
        var url = $"{this.apiBase}/sendMessage";
        var payload = new
        {
            chat_id = this.lastChatId,
            text = fullText,
            parse_mode = "MarkdownV2",
            disable_web_page_preview = true
        };

        var jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(payload, jsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var response = await this.http.PostAsync(url, content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    return "Markdown-сообщение отправлено.";
                }
            }

            // If MarkdownV2 failed, try HTML parse_mode as fallback.
            context_Log($"MarkdownV2 не удался, пробую HTML: {responseJson}");
            var htmlPayload = new
            {
                chat_id = this.lastChatId,
                text = ConvertMarkdownToHtml(fullText),
                parse_mode = "HTML",
                disable_web_page_preview = true
            };
            var htmlContent = new StringContent(JsonSerializer.Serialize(htmlPayload, jsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var htmlResponse = await this.http.PostAsync(url, htmlContent);
            var htmlResponseJson = await htmlResponse.Content.ReadAsStringAsync();

            if (htmlResponse.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(htmlResponseJson);
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    return "Сообщение отправлено (HTML fallback).";
                }
            }

            // Final fallback: plain text without formatting.
            var plainPayload = new
            {
                chat_id = this.lastChatId,
                text = StripMarkdown(fullText),
                disable_web_page_preview = true
            };
            var plainContent = new StringContent(JsonSerializer.Serialize(plainPayload, jsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var plainResponse = await this.http.PostAsync(url, plainContent);
            var plainResponseJson = await plainResponse.Content.ReadAsStringAsync();

            if (plainResponse.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(plainResponseJson);
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    return "Сообщение отправлено (plain text fallback).";
                }
            }

            return $"Ошибка отправки: {plainResponseJson}";
        }
        catch (Exception ex)
        {
            return $"Исключение при отправке: {ex.Message}";
        }
    }

    /// <summary>
    /// Sends a file with optional Markdown caption via sendDocument API.
    /// Only files inside the session's working folder may be sent — everything else
    /// (absolute paths elsewhere, .. traversal, links pointing outside) is refused.
    /// </summary>
    private async Task<string> SendFileAsync(string filePath, string? caption)
    {
        if (this.lastChatId == 0)
        {
            return "Нет активного чата: боту ещё никто не писал.";
        }

        if (string.IsNullOrWhiteSpace(this.workingDirectory))
        {
            return "Рабочая папка неизвестна — отправка файлов запрещена.";
        }

        // Security boundary: the bot sends files out of the machine, so confine it to the
        // working folder. Relative paths are resolved against that folder, not the process CWD.
        var fullPath = ResolveInsideWorkingFolder(this.workingDirectory, filePath);
        if (fullPath is null)
        {
            return $"Отказано: файл вне рабочей папки ({this.workingDirectory}). Отправлять можно только файлы внутри неё.";
        }

        if (!File.Exists(fullPath))
        {
            return $"Файл не найден: {fullPath}";
        }

        var fileName = Path.GetFileName(fullPath);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(this.lastChatId.ToString()), "chat_id");

        var fileBytes = File.ReadAllBytes(fullPath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "document", fileName);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            // Telegram caption limit is 1024 characters.
            var cap = caption.Length > 1024 ? caption[..1024] + "…" : caption;
            form.Add(new StringContent(cap), "caption");
            form.Add(new StringContent("MarkdownV2"), "parse_mode");
        }

        var response = await this.http.PostAsync($"{this.apiBase}/sendDocument", form);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return $"Ошибка отправки файла ({response.StatusCode}): {responseJson}";
        }

        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            return $"Файл «{fileName}» отправлен.";
        }
        else
        {
            var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : "неизвестно";
            return $"Ошибка Telegram API: {desc}";
        }
    }

    /// <summary>
    /// Resolves a caller-supplied path against the working folder and returns the full path
    /// only if it stays inside that folder; otherwise null. Blocks absolute paths pointing
    /// elsewhere, ".." traversal and symlinks/junctions whose final target is outside.
    /// </summary>
    private static string? ResolveInsideWorkingFolder(string root, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string fullRoot, fullPath;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            fullPath = Path.GetFullPath(filePath, fullRoot);
        }
        catch (Exception)
        {
            return null; // Malformed path — treat as outside.
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A link inside the folder must not leak a file outside it.
        if (File.Exists(fullPath))
        {
            try
            {
                var target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName;
                if (target is not null &&
                    !Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
            catch (IOException)
            {
                return null; // Broken or unresolvable link — refuse.
            }
        }

        return fullPath;
    }

    // ─── Helper methods for Markdown processing ───────────────────────────

    /// <summary>
    /// Escapes MarkdownV2 special characters in literal text.
    /// Characters that must be escaped: _ * [ ] ( ) ~ ` > # + - = | { } . !
    /// </summary>
    private static string EscapeMarkdownV2(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new System.Text.StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '_' || c == '*' || c == '[' || c == ']' || c == '(' || c == ')' ||
                c == '~' || c == '`' || c == '>' || c == '#' || c == '+' || c == '-' ||
                c == '=' || c == '|' || c == '{' || c == '}' || c == '.' || c == '!')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts basic Markdown to HTML as a fallback when MarkdownV2 parsing fails.
    /// Supports: **bold**, __italic__, `code`, ```code blocks```, [text](url), ~~strikethrough~~
    /// </summary>
    private static string ConvertMarkdownToHtml(string markdown)
    {
        var text = markdown;

        // Code blocks ```...```
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"```([\s\S]*?)```", m => $"<pre><code>{HtmlEscape(m.Groups[1].Value)}</code></pre>");

        // Inline code `...`
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"`([^`]+)`", m => $"<code>{HtmlEscape(m.Groups[1].Value)}</code>");

        // Links [text](url)
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\[([^\]]+)\]\(([^)]+)\)", m => $"<a href=\"{m.Groups[2].Value}\">{HtmlEscape(m.Groups[1].Value)}</a>");

        // Bold **text** or *text*
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\*\*([^*]+)\*\*", m => $"<b>{HtmlEscape(m.Groups[1].Value)}</b>");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?<!\*)\*([^*]+)\*(?!\*)", m => $"<b>{HtmlEscape(m.Groups[1].Value)}</b>");

        // Italic __text__ or _text_
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"__([^_]+)__", m => $"<i>{HtmlEscape(m.Groups[1].Value)}</i>");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?<!_)_([^_]+)_(?!_)", m => $"<i>{HtmlEscape(m.Groups[1].Value)}</i>");

        // Strikethrough ~~text~~
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"~~([^~]+)~~", m => $"<s>{HtmlEscape(m.Groups[1].Value)}</s>");

        // Headers ### text → <b>text</b>
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"^#{1,6}\s+(.+)$", m => $"<b>{HtmlEscape(m.Groups[1].Value)}</b>",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        return text;
    }

    /// <summary>
    /// Strips all Markdown formatting to produce plain text.
    /// </summary>
    private static string StripMarkdown(string markdown)
    {
        var text = markdown;

        // Remove code blocks
        text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "");
        // Remove inline code markers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");
        // Remove links, keep text
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        // Remove bold/italic/strikethrough markers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\*)\*([^*]+)\*(?!\*)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__([^_]+)__", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!_)_([^_]+)_(?!_)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"~~([^~]+)~~", "$1");
        // Remove header markers
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s+", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        return text.Trim();
    }

    /// <summary>
    /// Escapes HTML special characters.
    /// </summary>
    private static string HtmlEscape(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    // Workaround: log from non-async helpers without passing context around.
    // The SendMarkdownAsync method uses this for diagnostics.
    private static void context_Log(string msg) { /* no-op fallback */ }
}