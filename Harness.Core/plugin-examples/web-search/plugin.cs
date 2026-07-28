// Example resident plugin (type 2): live web access for the research workflow.
// The Ollama backend has no hosted web-search tool (AgentHost sets DisableWebSearch),
// so without this plugin every "research X" answer comes from the model's training
// data — outdated versions, invented dates and numbers. This plugin gives the agent
// two tools so it can cite real sources instead:
//
//   web_search(query, maxResults) — result list (title / url / snippet)
//   web_fetch(url, maxChars)      — page text, so the agent reads the source itself
//
// Search backend: Tavily when a key is present (file "tavily.txt" in this plugin
// folder, or the TAVILY_API_KEY environment variable), otherwise the key-less
// DuckDuckGo Lite endpoint.
using Microsoft.Extensions.AI;
using Harness.Core.Plugins;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class WebSearchPlugin : IResidentPlugin
{
    private const string TavilyEndpoint = "https://api.tavily.com/search";
    private const string DuckDuckGoEndpoint = "https://lite.duckduckgo.com/lite/";
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    // All patterns carry a timeout: they run over untrusted remote HTML.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex ResultLinkPattern = new(
        """<a\b(?=[^>]*class=["']result-link["'])[^>]*href=["'](?<url>[^"']+)["'][^>]*>(?<title>.*?)</a>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex ResultSnippetPattern = new(
        """<td\b[^>]*class=["']result-snippet["'][^>]*>(?<text>.*?)</td>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex ScriptStylePattern = new(
        @"<(script|style|noscript|svg|head)\b[^>]*>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex BlockEndPattern = new(
        @"</(p|div|section|article|h[1-6]|li|tr|table|ul|ol|blockquote|pre)\s*>|<br\s*/?>",
        RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex TagPattern = new(
        "<[^>]+>", RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex HorizontalSpacePattern = new(
        @"[ \t\f\v]+", RegexOptions.None, RegexTimeout);

    private static readonly Regex BlankLinesPattern = new(
        @"(?:[ \t]*\n){3,}", RegexOptions.None, RegexTimeout);

    private readonly HttpClient http;
    private string tavilyKey = string.Empty;

    public WebSearchPlugin()
    {
        this.http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

        // Both endpoints reject requests without a browser-like agent string.
        this.http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
    }

    public string Name => "web-search";

    public string Description =>
        "Живой веб-поиск и загрузка страниц: даёт агенту актуальные внешние данные вместо памяти модели.";

    public IReadOnlyList<AIFunction> GetTools() =>
    [
        AIFunctionFactory.Create(
            ([Description("Поисковый запрос на естественном языке.")] string query,
             [Description("Сколько результатов вернуть (1–20, по умолчанию 5).")] int maxResults = 5) =>
                this.SearchAsync(query, maxResults),
            name: "web_search",
            description: "Поиск в интернете. Возвращает список результатов (заголовок, URL, краткая выдержка). " +
                         "Используйте для любых вопросов о внешнем мире — версиях, датах, ценах, стандартах, " +
                         "новостях — вместо того чтобы отвечать по памяти. Затем читайте источники через web_fetch."),
        AIFunctionFactory.Create(
            ([Description("Абсолютный http/https URL страницы.")] string url,
             [Description("Максимум символов текста (500–200000, по умолчанию 20000).")] int maxChars = 20_000) =>
                this.FetchAsync(url, maxChars),
            name: "web_fetch",
            description: "Скачать страницу по URL и вернуть её текст (HTML-разметка убрана). " +
                         "Используйте после web_search, чтобы прочитать источник и цитировать его, а не пересказывать выдержку."),
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var keyFile = Path.Combine(context.PluginDirectory, "tavily.txt");
        this.tavilyKey = (File.Exists(keyFile)
            ? File.ReadAllText(keyFile).Trim()
            : Environment.GetEnvironmentVariable("TAVILY_API_KEY")) ?? string.Empty;

        context.Log(this.tavilyKey.Length > 0
            ? "Веб-поиск активен (Tavily API)."
            : "Веб-поиск активен (DuckDuckGo, без ключа). Для стабильного API положите ключ в tavily.txt рядом с плагином.");

        // Nothing to run in the background: the plugin only contributes tools.
        return Task.CompletedTask;
    }

    private async Task<string> SearchAsync(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Ошибка: параметр 'query' обязателен.";
        }

        maxResults = Math.Clamp(maxResults, 1, 20);

        try
        {
            var hits = this.tavilyKey.Length > 0
                ? await this.SearchTavilyAsync(query, maxResults)
                : await this.SearchDuckDuckGoAsync(query, maxResults);

            if (hits.Count == 0)
            {
                return $"По запросу «{query}» ничего не найдено.";
            }

            var text = new StringBuilder();
            for (var i = 0; i < hits.Count; i++)
            {
                text.AppendLine($"{i + 1}. {hits[i].Title}");
                text.AppendLine($"   {hits[i].Url}");
                if (hits[i].Snippet.Length > 0)
                {
                    text.AppendLine($"   {hits[i].Snippet}");
                }

                text.AppendLine();
            }

            return text.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Ошибка поиска: {ex.Message}";
        }
    }

    private async Task<List<Hit>> SearchTavilyAsync(string query, int maxResults)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query,
            max_results = maxResults,
            search_depth = "advanced",
            include_answer = false,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, TavilyEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {this.tavilyKey}");

        using var response = await this.http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                hits.Add(new Hit(
                    Text(item, "title"),
                    Text(item, "url"),
                    Truncate(Text(item, "content"), 400)));
            }
        }

        return hits;

        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    // ponytail: HTML scraping of DuckDuckGo's lite endpoint — free and key-less, but it
    // breaks whenever they change the markup. Drop a Tavily key into tavily.txt for a
    // stable API; that path is preferred automatically when the key is present.
    private async Task<List<Hit>> SearchDuckDuckGoAsync(string query, int maxResults)
    {
        using var form = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)]);
        using var response = await this.http.PostAsync(DuckDuckGoEndpoint, form);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        var links = ResultLinkPattern.Matches(html);
        var snippets = ResultSnippetPattern.Matches(html);

        var hits = new List<Hit>();
        for (var i = 0; i < links.Count && hits.Count < maxResults; i++)
        {
            var url = UnwrapRedirect(WebUtility.HtmlDecode(links[i].Groups["url"].Value));
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hits.Add(new Hit(
                HtmlToText(links[i].Groups["title"].Value),
                url,
                i < snippets.Count ? Truncate(HtmlToText(snippets[i].Groups["text"].Value), 400) : string.Empty));
        }

        return hits;
    }

    private async Task<string> FetchAsync(string url, int maxChars)
    {
        // Trust boundary: only absolute http/https targets, never file:// or anything else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Ошибка: допустим только абсолютный http/https URL.";
        }

        maxChars = Math.Clamp(maxChars, 500, 200_000);

        try
        {
            using var response = await this.http.GetAsync(uri);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            var text = HtmlToText(body);

            return text.Length == 0
                ? $"Страница {uri} не содержит текста (возможно, требуется JavaScript)."
                : $"Источник: {uri}{Environment.NewLine}{Environment.NewLine}{Truncate(text, maxChars)}";
        }
        catch (Exception ex)
        {
            return $"Ошибка загрузки {uri}: {ex.Message}";
        }
    }

    // DuckDuckGo wraps some results in //duckduckgo.com/l/?uddg=<url-encoded target>.
    private static string UnwrapRedirect(string url)
    {
        var marker = url.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return url;
        }

        var value = url[(marker + 5)..];
        var end = value.IndexOf('&');
        return Uri.UnescapeDataString(end < 0 ? value : value[..end]);
    }

    private static string HtmlToText(string html)
    {
        html = ScriptStylePattern.Replace(html, " ");
        html = BlockEndPattern.Replace(html, "\n");
        html = TagPattern.Replace(html, " ");
        html = WebUtility.HtmlDecode(html);
        html = HorizontalSpacePattern.Replace(html, " ");
        return BlankLinesPattern.Replace(html, "\n\n").Trim();
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] + "… (обрезано)" : text;

    private sealed record Hit(string Title, string Url, string Snippet);
}
