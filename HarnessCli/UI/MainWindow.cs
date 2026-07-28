using Harness.Core;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Serilog;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

// SharpConsoleUI's ChatRole (a transcript display role) collides with the chat-protocol role
// of Microsoft.Extensions.AI; in this file the protocol one wins. The display role is used
// only by TranscriptLog.
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace HarnessCli.UI;

/// <summary>
/// Console chat window for the harness agent — the SharpConsoleUI port of the WinForms
/// MainForm, with the same structure: a session sidebar on the left (working folders and
/// their sessions), a menu bar on top (session, model, mode, resources), the streaming chat
/// transcript with the input row, and a status bar showing the working folder, token usage
/// and busy state. Each working folder gets its own <see cref="AgentHost"/>; sessions can be
/// switched at any time — their transcripts are kept per session.
/// Agent turns run on a background task; every UI mutation is marshalled back through
/// <see cref="ConsoleWindowSystem.EnqueueOnUIThread(Action)"/>.
/// </summary>
internal sealed class MainWindow
{
    private const string DefaultSessionTitle = "Новая сессия";
    private const int SidebarWidth = 30;

    // Readable JSON for tool-call arguments in the chat log: without the relaxed
    // encoder the default serializer escapes quotes and non-ASCII as \uXXXX sequences.
    private static readonly JsonSerializerOptions ToolArgsJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ConsoleWindowSystem ws;
    private readonly Dictionary<string, AgentHost> hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> folderOrder = [];
    private readonly List<SessionEntry> sessions = [];

    // Streaming chunks are buffered here and flushed into the transcript in one batch
    // (per-chunk redraws made the log flicker and scroll constantly).
    private readonly List<TranscriptChunk> pendingOutput = [];
    private readonly Lock pendingLock = new();

    private readonly Window window;
    private readonly ListControl sidebar;
    private readonly TranscriptLog transcript;
    private readonly PromptControl input;
    private readonly StatusBarItem folderStatus;
    private readonly StatusBarItem usageStatus;
    private readonly StatusBarItem stateStatus;

    private string? workingFolder;
    private SessionEntry? active;
    private AgentModeProvider? modeProvider;
    private volatile bool busy;
    private string? busyState;
    private bool atLineStart = true;
    private bool modelsLoaded;
    private List<string> knownModels = [];
    private string currentMode = "execute";

    // "Auto permissions": every tool-approval request is granted automatically,
    // without showing the approval dialog. Toggled from the "Режим" menu.
    private bool autoPermissions;

    /// <param name="initialFolder">
    /// Working folder to open the first session in, or <see langword="null"/> to ask for one
    /// once the main loop is up (the folder picker is a window and needs a running system).
    /// </param>
    public MainWindow(ConsoleWindowSystem ws, string? initialFolder)
    {
        this.ws = ws;
        this.workingFolder = initialFolder;

        this.window = new WindowBuilder(ws)
            .WithTitle("HarnessCli")
            // Restore bounds for when the user un-maximizes; the window starts maximized.
            .WithSize(120, 30)
            .Maximized()
            .WithAsyncWindowThread(this.WindowThreadAsync)
            .Build();

        var menu = this.BuildMenu();
        menu.StickyPosition = StickyPosition.Top;

        this.sidebar = Controls.List("Сессии")
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();
        this.sidebar.ItemActivated += this.OnSidebarItemActivated;

        var chat = Controls.ChatTranscript().Build();
        this.transcript = new TranscriptLog(chat);

        this.input = Controls.Prompt("› ")
            .UnfocusOnEnter(false)
            .WithHistory()
            .WithAlignment(HorizontalAlignment.Stretch)
            .OnEntered((_, text) => this.Send(text))
            .Build();

        var root = Controls.Grid()
            .Columns(GridLength.Cells(SidebarWidth), GridLength.Star(1))
            .Rows(GridLength.Star(1), GridLength.Auto())
            .ColumnSplitterAfter(0)
            .Place(this.sidebar, 0, 0)
            .Place(chat, 0, 1)
            .Place(this.input, 1, 0, colSpan: 2)
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        var statusBar = Controls.StatusBar().Build();
        this.folderStatus = statusBar.AddLeftText(Escape(initialFolder ?? "—"));
        statusBar.AddCenter("Enter", "Отправить", () => this.Send(this.input.Input));
        statusBar.AddCenter("Ctrl+P", "Auto permissions", this.ToggleAutoPermissions);
        this.usageStatus = statusBar.AddRightText("📊 TOKENS —");
        this.stateStatus = statusBar.AddRightText("Готово");

        this.window.AddControl(menu);
        this.window.AddControl(root);
        this.window.AddControl(statusBar);

        this.window.KeyPressed += (_, e) =>
        {
            if (e.AlreadyHandled)
            {
                return;
            }

            if (!e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                return;
            }

            // MenuControl's shortcut column is display-only — the real bindings live here.
            switch (e.KeyInfo.Key)
            {
                case ConsoleKey.P:
                    this.ToggleAutoPermissions();
                    break;

                case ConsoleKey.N:
                    this.RunBackground(() => this.CreateSessionAsync(this.active?.Folder ?? this.workingFolder));
                    break;

                case ConsoleKey.O:
                    this.RunBackground(this.PickFolderAsync);
                    break;

                case ConsoleKey.Q:
                    this.ws.Shutdown();
                    break;

                default:
                    return;
            }

            e.Handled = true;
        };

        ws.AddWindow(this.window);
    }

    // ---- Menu ----

    private MenuControl BuildMenu() => Controls.Menu()
        .Horizontal()
        .Sticky()
        .AddItem("Сессия", m => m
            .AddItem("Новая сессия", "Ctrl+N", () =>
                this.RunBackground(() => this.CreateSessionAsync(this.active?.Folder ?? this.workingFolder)))
            .AddItem("Рабочая папка…", "Ctrl+O", () => this.RunBackground(this.PickFolderAsync))
            .AddItem("Очистить вывод", this.ClearTranscript)
            .AddSeparator()
            .AddItem("Выход", "Ctrl+Q", () => this.ws.Shutdown()))
        .AddItem("Модель", m => m
            .AddItem("Выбрать из списка…", () => this.RunBackground(this.ChooseModelAsync))
            .AddItem("Ввести имя модели…", () => this.RunBackground(this.EnterModelNameAsync)))
        .AddItem("Режим", m => m
            .AddItem("execute", () => this.RunBackground(() => this.SetModeAsync("execute")))
            .AddItem("plan", () => this.RunBackground(() => this.SetModeAsync("plan")))
            .AddSeparator()
            .AddItem("Auto permissions", "Ctrl+P", this.ToggleAutoPermissions))
        .AddItem("Ресурсы", m => m
            .AddItem("Агенты…", () => this.RunBackground(() => this.ShowResourcesAsync("Агенты", ResourceKind.Agents)))
            .AddItem("Навыки…", () => this.RunBackground(() => this.ShowResourcesAsync("Навыки (skills)", ResourceKind.Skills)))
            .AddItem("Плагины…", () => this.RunBackground(() => this.ShowResourcesAsync("Плагины", ResourceKind.Plugins)))
            .AddItem("Скрипты…", () => this.RunBackground(() => this.ShowResourcesAsync("Скрипты (dotnet)", ResourceKind.Scripts))))
        .Build();

    // ---- Window thread ----

    /// <summary>
    /// Runs for the lifetime of the window: resolves the working folder (asking for one when
    /// the command line and the saved settings did not supply it), opens the first session,
    /// then flushes buffered output four times a second. The interval used to be a second
    /// because a re-render cost hundreds of milliseconds; the transcript now appends to the
    /// open message instead of re-rendering, so the answer can appear as it arrives.
    /// </summary>
    private async Task WindowThreadAsync(Window window, CancellationToken ct)
    {
        // The folder picker is a window of its own — it cannot open before this one is live.
        while (!window.GetIsActive() && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        this.workingFolder ??= await this.OnUiAsync(
            () => AppDialogs.PickFolderAsync(this.ws, initial: null)).ConfigureAwait(false);

        if (this.workingFolder is null)
        {
            // The working folder is mandatory — without it the agent has nothing to operate on.
            this.ws.Shutdown(1);
            return;
        }

        await this.CreateSessionAsync(this.workingFolder).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            this.ws.EnqueueOnUIThread(this.FlushOutput);
            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ---- Sidebar ----

    private void OnSidebarItemActivated(object? sender, ListItem item)
    {
        if (this.busy || item.Tag is null)
        {
            return;
        }

        switch (item.Tag)
        {
            // Enter on a folder header starts a new session in that folder (the console
            // equivalent of the "+" hot zone of the WinForms sidebar).
            case FolderTag folder:
                this.RunBackground(() => this.CreateSessionAsync(folder.Path));
                break;

            case SessionEntry entry when !ReferenceEquals(entry, this.active):
                this.RunBackground(() => this.ActivateSessionAsync(entry));
                break;
        }
    }

    private void RebuildSidebar()
    {
        int previous = this.sidebar.SelectedIndex;

        this.sidebar.Items.Clear();

        foreach (var folder in this.folderOrder)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrEmpty(name))
            {
                name = folder;
            }

            // The folder header and each of its sessions carry themselves in ListItem.Tag,
            // so activating a row needs no parallel index table.
            this.AddSidebarRow($"📂 {name}", new FolderTag(folder));

            foreach (var session in this.sessions)
            {
                if (!string.Equals(session.Folder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isActive = ReferenceEquals(session, this.active);
                this.AddSidebarRow($" {(isActive ? "▸" : " ")} {session.Title}", session);
            }
        }

        if (this.sidebar.Items.Count > 0)
        {
            this.sidebar.SelectedIndex = Math.Clamp(previous, 0, this.sidebar.Items.Count - 1);
        }

        this.sidebar.Invalidate();
    }

    private void AddSidebarRow(string text, object tag) =>
        this.sidebar.Items.Add(new ListItem(Escape(text)) { Tag = tag });

    // ---- Hosts, folders and sessions ----

    private async Task PickFolderAsync()
    {
        if (this.busy)
        {
            return;
        }

        string? folder = await this.OnUiAsync(() => AppDialogs.PickFolderAsync(
            this.ws,
            this.active?.Folder ?? this.workingFolder)).ConfigureAwait(false);

        if (folder is not null)
        {
            await this.CreateSessionAsync(folder).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the agent host for <paramref name="folder"/>, creating it on first use.
    /// Every working folder owns its own host (sandbox, shell, HTTP client).
    /// </summary>
    private async Task<AgentHost?> EnsureHostAsync(string folder)
    {
        if (this.hosts.TryGetValue(folder, out var existing))
        {
            return existing;
        }

        this.SetBusy(true, "Инициализация агента…");
        try
        {
            var host = await AgentHost.CreateAsync(folder, "HarnessCli").ConfigureAwait(false);
            this.hosts[folder] = host;

            // Mirror plugin logs into the chat output (they arrive from background threads).
            host.Plugins.PluginLog += (plugin, message) =>
            {
                this.AppendLine($"🔌 [{plugin}] {message}", TranscriptKind.Info);
                this.ws.EnqueueOnUIThread(this.FlushOutput);
            };

            if (!this.modelsLoaded)
            {
                await this.LoadModelListAsync(host).ConfigureAwait(false);
            }

            return host;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Agent host creation failed for folder {Folder}", folder);
            this.ws.EnqueueOnUIThread(() => _ = AppDialogs.ErrorAsync(this.ws, "Ошибка запуска агента", ex.Message));
            return null;
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    /// <summary>Creates a new session in <paramref name="folder"/> and makes it active.</summary>
    private async Task CreateSessionAsync(string? folder)
    {
        if (folder is null)
        {
            return;
        }

        var host = await this.EnsureHostAsync(folder).ConfigureAwait(false);
        if (host is null)
        {
            return;
        }

        this.SetBusy(true, "Создание сессии…");
        try
        {
            var session = await host.Agent.CreateSessionAsync().ConfigureAwait(false);
            var entry = new SessionEntry { Folder = folder, Host = host, Session = session };

            // The session/folder lists belong to the UI thread (the sidebar enumerates them
            // while redrawing) — mutate them there, never from this background task.
            await this.ws.InvokeAsync(() =>
            {
                this.sessions.Add(entry);
                if (!this.folderOrder.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    this.folderOrder.Add(folder);
                }
            }).ConfigureAwait(false);

            await this.ActivateSessionAsync(entry, greet: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Session creation failed in folder {Folder}", folder);
            this.AppendLine($"❌ Не удалось создать сессию: {ex.Message}", TranscriptKind.Error);
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    /// <summary>
    /// Switches the UI to <paramref name="entry"/>: stores the current transcript,
    /// restores the target one, and repoints the mode/folder state at its host.
    /// </summary>
    private async Task ActivateSessionAsync(SessionEntry entry, bool greet = false)
    {
        await this.ws.InvokeAsync(() =>
        {
            this.SaveActiveTranscript();
            this.active = entry;
            this.atLineStart = entry.AtLineStart;
            this.transcript.Load(entry.Transcript);
            this.usageStatus.Label = Escape(entry.UsageText);
            this.folderStatus.Label = Escape(entry.Folder);
            this.window.Title = $"HarnessCli — {entry.Host.ModelName}";
            this.RebuildSidebar();
        }).ConfigureAwait(false);

        this.modeProvider = entry.Host.Agent.GetService<AgentModeProvider>();
        AppSettings.SaveLastWorkingFolder(entry.Folder);

        if (greet)
        {
            this.AppendLine(
                "Попросите меня прочитать или отредактировать файл в рабочей папке, " +
                "что-нибудь найти, выполнить команду оболочки или запустить код на Python. " +
                "Enter — отправить, F10 — меню, Tab — переход между панелями.",
                TranscriptKind.Info);
        }

        await this.RefreshModeAsync().ConfigureAwait(false);
        this.ws.EnqueueOnUIThread(() =>
        {
            this.FlushOutput();
            this.input.RequestFocus();
        });
    }

    private void SaveActiveTranscript()
    {
        this.FlushOutput(); // the snapshot must include everything still buffered

        if (this.active is not null)
        {
            this.active.Transcript = this.transcript.Snapshot();
            this.active.AtLineStart = this.atLineStart;
            this.active.UsageText = this.usageStatus.Label;
        }
    }

    private void ClearTranscript()
    {
        lock (this.pendingLock)
        {
            this.pendingOutput.Clear();
        }

        this.transcript.Clear();
        this.atLineStart = true;
    }

    // ---- "Ресурсы": everything the agent can currently reach ----

    private enum ResourceKind
    {
        Agents,
        Skills,
        Plugins,
        Scripts,
    }

    /// <summary>
    /// Lists the resources of one kind and fills the input box with a ready prompt for the
    /// picked entry. Agents, skills and plugins live next to the exe (each in its own
    /// folder); dotnet scripts live in the active session's working folder. The list is read
    /// from disk on every open — the agent can create a plugin or a script mid-session.
    /// </summary>
    private async Task ShowResourcesAsync(string title, ResourceKind kind)
    {
        var baseDir = AppContext.BaseDirectory;
        var workingDir = this.active?.Host.WorkingDirectory;

        var items = kind switch
        {
            ResourceKind.Agents => DiscoverFolders(Path.Combine(baseDir, "agents"), "AGENTS.md"),
            ResourceKind.Skills => DiscoverFolders(Path.Combine(baseDir, "skills"), "SKILL.md"),
            ResourceKind.Plugins => DiscoverFolders(Path.Combine(baseDir, "plugins"), "plugin.cs"),
            _ => workingDir is null ? [] : DiscoverFiles(Path.Combine(workingDir, "scripts"), "*.cs"),
        };

        int? picked = await this.OnUiAsync(() => AppDialogs.SelectAsync(
            this.ws,
            title,
            items.Select(i => i.Name).ToList(),
            details: index => items[index].Summary.Length > 0 ? items[index].Summary : items[index].Path))
            .ConfigureAwait(false);

        if (picked is not int index || index < 0 || index >= items.Count)
        {
            return;
        }

        var item = items[index];
        string prompt = kind switch
        {
            ResourceKind.Agents => $"Прочитай \"{item.Path}\" и прими эту роль.",
            ResourceKind.Skills => $"Применяй навык \"{item.Name}\" (\"{item.Path}\") для задачи: ",
            ResourceKind.Plugins => $"Покажи, что умеет плагин \"{item.Name}\", и пример вызова его инструментов.",
            _ => $"Запусти скрипт: dotnet run scripts\\{Path.GetFileName(item.Path)}",
        };

        // A pick fills the input box instead of sending straight away — most entries need
        // the user to append the actual task before the turn starts.
        this.ws.EnqueueOnUIThread(() =>
        {
            this.input.SetInput(prompt);
            this.input.RequestFocus();
        });
    }

    // Layout of agents/, skills/ and plugins/: one folder per item, holding a marker file.
    private static IReadOnlyList<ResourceItem> DiscoverFolders(string root, string markerFile)
    {
        return Discover(root, () => Directory
            .EnumerateDirectories(root)
            .Select(dir => Path.Combine(dir, markerFile))
            .Where(File.Exists));
    }

    private static IReadOnlyList<ResourceItem> DiscoverFiles(string root, string pattern)
    {
        return Discover(root, () => Directory.EnumerateFiles(root, pattern));
    }

    private static IReadOnlyList<ResourceItem> Discover(string root, Func<IEnumerable<string>> enumerate)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return enumerate()
                .Select(file => new ResourceItem(
                    Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Equals("plugin.cs", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileName(Path.GetDirectoryName(file))!  // folder name identifies agents/skills/plugins
                        : Path.GetFileName(file),
                    file,
                    ReadSummary(file)))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Resource discovery failed for {Root}", root);
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Resource discovery failed for {Root}", root);
            return [];
        }
    }

    // Best-effort one-line summary for the hint line: the "description:" field of a SKILL.md
    // front matter, the first prose line of a Markdown document, or the leading //-comment
    // of a plugin or script source file.
    private static string ReadSummary(string file)
    {
        const string DescriptionKey = "description:";

        try
        {
            foreach (var raw in File.ReadLines(file).Take(30))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line == "---" || line.StartsWith('#') ||
                    line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith(DescriptionKey, StringComparison.OrdinalIgnoreCase))
                {
                    return Shorten(line[DescriptionKey.Length..].Trim());
                }

                if (line.StartsWith("//"))
                {
                    return Shorten(line.TrimStart('/').Trim());
                }

                if (!line.StartsWith("using ") && !line.StartsWith('<') && !line.StartsWith('['))
                {
                    return Shorten(line);
                }
            }
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "Resource summary unreadable: {File}", file);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(ex, "Resource summary unreadable: {File}", file);
        }

        return string.Empty;

        static string Shorten(string text) => text.Length > 160 ? text[..160] + "…" : text;
    }

    private sealed record ResourceItem(string Name, string Path, string Summary);

    // ---- Chat turn ----

    private void Send(string raw)
    {
        var entry = this.active;
        string text = (raw ?? string.Empty).Trim();
        if (this.busy || entry is null || text.Length == 0)
        {
            return;
        }

        this.input.SetInput(string.Empty);
        this.AppendLine(text, TranscriptKind.User);

        // The first message names the session in the sidebar.
        if (entry.Title == DefaultSessionTitle)
        {
            entry.Title = text.Length > 24 ? text[..24] + "…" : text;
            this.RebuildSidebar();
        }

        this.FlushOutput();
        this.RunBackground(() => this.SendAsync(entry, text));
    }

    private async Task SendAsync(SessionEntry entry, string text)
    {
        this.SetBusy(true, "Агент работает…");
        try
        {
            await this.RunTurnAsync(entry, [new ChatMessage(ChatRole.User, text)]).ConfigureAwait(false);
        }
        finally
        {
            this.SetBusy(false);
            await this.RefreshModeAsync().ConfigureAwait(false);
            this.ws.EnqueueOnUIThread(() =>
            {
                this.FlushOutput();
                this.input.RequestFocus();
            });
        }
    }

    /// <summary>
    /// Runs one agent turn, re-invoking the agent with approval responses until no
    /// tool-approval requests remain (the console equivalent of the runner loop).
    /// </summary>
    private async Task RunTurnAsync(SessionEntry entry, List<ChatMessage> messages)
    {
        IList<ChatMessage>? next = messages;
        var approvals = new List<ToolApprovalRequestContent>();

        while (next is not null)
        {
            approvals.Clear();

            try
            {
                await foreach (var update in entry.Host.Agent.RunStreamingAsync(next, entry.Session).ConfigureAwait(false))
                {
                    foreach (var content in update.Contents)
                    {
                        this.HandleContent(content, approvals);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Agent turn stream failed");
                this.AppendLine($"❌ Ошибка потока: {ex.GetType().Name}: {ex.Message}", TranscriptKind.Error);
            }

            this.EnsureLineBreak();

            // The full context must be visible before the modal approval dialogs open.
            await this.ws.InvokeAsync(this.FlushOutput).ConfigureAwait(false);

            next = approvals.Count > 0
                ? await this.CollectApprovalResponsesAsync(approvals).ConfigureAwait(false)
                : null;
        }

        // A resident plugin hot-loaded during this turn (plugin_create/plugin_load) requires
        // an agent rebuild so its tools become available from the next message.
        await this.RefreshPluginToolsAsync(entry.Host).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds the host's agent after a resident plugin hot-load and re-attaches every
    /// session of that host to the new agent (same migration as a model switch).
    /// </summary>
    private async Task RefreshPluginToolsAsync(AgentHost host)
    {
        if (!host.HasPendingPluginTools)
        {
            return;
        }

        try
        {
            var oldAgent = host.Agent;
            var affected = await this.ws.InvokeAsync(
                () => this.sessions.Where(s => ReferenceEquals(s.Host, host)).ToList()).ConfigureAwait(false);
            var snapshots = new Dictionary<SessionEntry, JsonElement>();
            foreach (var session in affected)
            {
                snapshots[session] = await oldAgent.SerializeSessionAsync(session.Session).ConfigureAwait(false);
            }

            if (!host.RefreshPluginToolsIfNeeded())
            {
                return;
            }

            foreach (var session in affected)
            {
                session.Session = await host.Agent.DeserializeSessionAsync(snapshots[session]).ConfigureAwait(false);
            }

            this.modeProvider = host.Agent.GetService<AgentModeProvider>();
            this.AppendLine(
                "🔌 Резидентный плагин загружен: его инструменты доступны со следующего сообщения.",
                TranscriptKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Plugin tools refresh failed");
            this.AppendLine($"❌ Не удалось обновить инструменты плагинов: {ex.Message}", TranscriptKind.Error);
        }
    }

    private void HandleContent(AIContent content, List<ToolApprovalRequestContent> approvals)
    {
        switch (content)
        {
            case ToolApprovalRequestContent approvalRequest:
                approvals.Add(approvalRequest);
                this.AppendLine($"⚠️ Требуется подтверждение: {FormatToolCall(approvalRequest.ToolCall, maxArgsLength: 160)}", TranscriptKind.Tool);
                break;

            case FunctionCallContent functionCall:
                this.AppendLine($"🔧 Вызов инструмента: {FormatToolCall(functionCall, maxArgsLength: 160)}…", TranscriptKind.Tool);
                break;

            case ErrorContent error:
                string errorText = $"❌ Ошибка: {error.Message}";
                if (!string.IsNullOrWhiteSpace(error.ErrorCode))
                {
                    errorText += $" (код: {error.ErrorCode})";
                }

                this.AppendLine(errorText, TranscriptKind.Error);
                break;

            case UsageContent usage:
                string usageText = usage.Details is not null ? this.FormatUsage(usage.Details) : "📊 TOKENS —";
                this.ws.EnqueueOnUIThread(() => this.usageStatus.Label = Escape(usageText));
                break;

            case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                this.AppendText(reasoning.Text, TranscriptKind.Reasoning);
                break;

            case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                this.AppendText(textContent.Text, TranscriptKind.Markdown);
                break;
        }
    }

    /// <summary>
    /// Shows the approval dialog for every pending request and turns each decision into
    /// the corresponding approval-response message for the next agent invocation.
    /// The dialogs run on the UI thread while this background turn awaits them.
    /// </summary>
    private async Task<List<ChatMessage>> CollectApprovalResponsesAsync(List<ToolApprovalRequestContent> approvals)
    {
        var responses = new List<ChatMessage>(approvals.Count);

        // "Auto permissions" mode: grant every request without showing a dialog.
        if (this.autoPermissions)
        {
            foreach (var request in approvals)
            {
                this.AppendLine(
                    $"🔓 Авто-разрешено: {FormatToolCall(request.ToolCall, maxArgsLength: 120)}",
                    TranscriptKind.Info);
                responses.Add(new ChatMessage(ChatRole.User,
                    [request.CreateResponse(approved: true, reason: "Auto permissions mode")]));
            }

            return responses;
        }

        foreach (var request in approvals)
        {
            var decision = await this.OnUiAsync(() => AppDialogs.AskToolApprovalAsync(
                this.ws, FormatToolCall(request.ToolCall, maxArgsLength: 2000))).ConfigureAwait(false);

            AIContent response = decision switch
            {
                ToolApprovalDecision.AlwaysApproveTool => request.CreateAlwaysApproveToolResponse("User chose to always approve this tool"),
                ToolApprovalDecision.AlwaysApproveToolWithArguments => request.CreateAlwaysApproveToolWithArgumentsResponse("User chose to always approve this tool with these arguments"),
                ToolApprovalDecision.Deny => request.CreateResponse(approved: false, reason: "User denied"),
                _ => request.CreateResponse(approved: true, reason: "User approved"),
            };

            string outcome = decision switch
            {
                ToolApprovalDecision.AlwaysApproveTool => "✅ Всегда разрешено (любые аргументы)",
                ToolApprovalDecision.AlwaysApproveToolWithArguments => "✅ Всегда разрешено (эти аргументы)",
                ToolApprovalDecision.Deny => "❌ Отклонено",
                _ => "✅ Разрешено",
            };

            this.AppendLine(
                $"🔹 {outcome}: {FormatToolCall(request.ToolCall, maxArgsLength: 120)}",
                decision == ToolApprovalDecision.Deny ? TranscriptKind.Error : TranscriptKind.Info);

            responses.Add(new ChatMessage(ChatRole.User, [response]));
        }

        await this.ws.InvokeAsync(this.FlushOutput).ConfigureAwait(false);
        return responses;
    }

    // ---- Model selection ----

    /// <summary>
    /// Caches the model names available on the endpoint: the configured model first, then
    /// whatever /api/tags reports.
    /// </summary>
    private async Task LoadModelListAsync(AgentHost host)
    {
        this.modelsLoaded = true;

        var models = await host.ListAvailableModelsAsync().ConfigureAwait(false);
        var names = new List<string> { host.ModelName };
        foreach (var model in models)
        {
            if (!names.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(model);
            }
        }

        this.knownModels = names;

        if (models.Count == 0)
        {
            this.AppendLine(
                "Список моделей с сервера получить не удалось — используйте «Модель → Ввести имя модели…».",
                TranscriptKind.Info);
        }
    }

    private async Task ChooseModelAsync()
    {
        var entry = this.active;
        if (this.busy || entry is null)
        {
            return;
        }

        if (this.knownModels.Count <= 1)
        {
            await this.LoadModelListAsync(entry.Host).ConfigureAwait(false);
        }

        var models = this.knownModels;
        int current = models.FindIndex(m => string.Equals(m, entry.Host.ModelName, StringComparison.OrdinalIgnoreCase));
        int? picked = await this.OnUiAsync(
            () => AppDialogs.SelectAsync(this.ws, "Модель", models, Math.Max(current, 0))).ConfigureAwait(false);

        if (picked is int index && index >= 0 && index < models.Count)
        {
            await this.ApplyModelAsync(models[index]).ConfigureAwait(false);
        }
    }

    private async Task EnterModelNameAsync()
    {
        if (this.busy)
        {
            return;
        }

        string? name = await this.OnUiAsync(() => AppDialogs.PromptTextAsync(
            this.ws, "Модель", "Имя модели Ollama:", this.active?.Host.ModelName ?? string.Empty))
            .ConfigureAwait(false);

        if (name is not null)
        {
            await this.ApplyModelAsync(name).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches the backend model of the active session's host (takes effect from the next
    /// message; sessions are kept). The context window is taken from the model itself, so
    /// the host may rebuild its agent — every session of that host is then serialized with
    /// the old agent and re-attached to the new one.
    /// </summary>
    private async Task ApplyModelAsync(string? modelName)
    {
        var entry = this.active;
        if (entry is null)
        {
            return;
        }

        modelName = modelName?.Trim();
        if (string.IsNullOrEmpty(modelName) || string.Equals(modelName, entry.Host.ModelName, StringComparison.Ordinal))
        {
            return;
        }

        this.SetBusy(true, "Переключение модели…");
        try
        {
            var host = entry.Host;
            var oldAgent = host.Agent;

            // Snapshot every session on this host up front: a context-window change
            // replaces the agent, and sessions only run against the agent that owns them.
            var affected = await this.ws.InvokeAsync(
                () => this.sessions.Where(s => ReferenceEquals(s.Host, host)).ToList()).ConfigureAwait(false);
            var snapshots = new Dictionary<SessionEntry, JsonElement>();
            foreach (var session in affected)
            {
                snapshots[session] = await oldAgent.SerializeSessionAsync(session.Session).ConfigureAwait(false);
            }

            bool rebuilt = await host.SetModelAsync(modelName).ConfigureAwait(false);
            if (rebuilt)
            {
                foreach (var session in affected)
                {
                    session.Session = await host.Agent.DeserializeSessionAsync(snapshots[session]).ConfigureAwait(false);
                }

                this.modeProvider = host.Agent.GetService<AgentModeProvider>();
            }

            this.ws.EnqueueOnUIThread(() => this.window.Title = $"HarnessCli — {host.ModelName}");
            this.AppendLine(
                $"Модель переключена: {modelName} (контекстное окно: {host.ContextWindowTokens:N0} токенов; " +
                "действует со следующего сообщения, сессия сохранена)",
                TranscriptKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Model switch to {Model} failed", modelName);
            this.AppendLine($"❌ Не удалось переключить модель: {ex.Message}", TranscriptKind.Error);
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    // ---- Mode selection ----

    private void ToggleAutoPermissions()
    {
        this.autoPermissions = !this.autoPermissions;
        this.AppendLine(
            this.autoPermissions
                ? "🔓 Auto permissions включён: инструменты выполняются без подтверждения."
                : "🔐 Auto permissions выключен: подтверждение инструментов снова требуется.",
            TranscriptKind.Info);
        this.UpdateStateLabel();
        this.FlushOutput();
    }

    private async Task SetModeAsync(string mode)
    {
        if (this.busy || this.modeProvider is null || this.active is null)
        {
            return;
        }

        try
        {
            await this.modeProvider.SetModeAsync(this.active.Session, mode).ConfigureAwait(false);
            this.currentMode = mode;
            this.AppendLine($"Режим переключён: {mode}", TranscriptKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mode switch to {Mode} failed", mode);
            this.AppendLine($"❌ Не удалось переключить режим: {ex.Message}", TranscriptKind.Error);
            await this.RefreshModeAsync().ConfigureAwait(false);
        }

        this.ws.EnqueueOnUIThread(() =>
        {
            this.UpdateStateLabel();
            this.FlushOutput();
        });
    }

    private async Task RefreshModeAsync()
    {
        if (this.modeProvider is null || this.active is null)
        {
            return;
        }

        try
        {
            this.currentMode = await this.modeProvider.GetModeAsync(this.active.Session).ConfigureAwait(false);
            this.ws.EnqueueOnUIThread(this.UpdateStateLabel);
        }
        catch (Exception ex)
        {
            // Mode display is cosmetic; ignore transient provider errors.
            Log.Debug(ex, "Mode refresh failed");
        }
    }

    // ---- State / formatting helpers ----

    private void SetBusy(bool busy, string? state = null)
    {
        this.busy = busy;
        this.busyState = state;

        this.ws.EnqueueOnUIThread(() =>
        {
            this.input.IsEnabled = !busy;
            this.UpdateStateLabel();
            if (!busy)
            {
                this.FlushOutput(); // show the tail of the answer as soon as the turn ends
            }
        });
    }

    private void UpdateStateLabel()
    {
        string state = this.busy ? this.busyState ?? "Агент работает…" : "Готово";
        this.stateStatus.Label = Escape(
            $"{state} │ режим: {this.currentMode}{(this.autoPermissions ? " │ 🔓 auto" : string.Empty)}");
    }

    private static string FormatToolCall(AIContent? toolCall, int maxArgsLength)
    {
        if (toolCall is not FunctionCallContent fc)
        {
            return toolCall?.ToString() ?? "unknown";
        }

        string args = string.Empty;
        if (fc.Arguments is { Count: > 0 })
        {
            try
            {
                args = JsonSerializer.Serialize(fc.Arguments, ToolArgsJsonOptions);
            }
            catch (NotSupportedException ex)
            {
                Log.Debug(ex, "Tool args serialization failed for {Tool}, falling back to plain join", fc.Name);
                args = string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
            }

            if (args.Length > maxArgsLength)
            {
                args = args[..maxArgsLength] + "…";
            }
        }

        return args.Length > 0 ? $"{fc.Name} {args}" : fc.Name;
    }

    // Token budgets come from the active host: the context window is per-model,
    // queried from the endpoint when the model is selected.
    private string FormatUsage(UsageDetails details)
    {
        int contextWindow = this.active?.Host.ContextWindowTokens ?? AgentHost.DefaultContextWindowTokens;
        int outputBudget = this.active?.Host.OutputTokens ?? AgentHost.MaxOutputTokens;
        int inputBudget = contextWindow - outputBudget;
        return $"📊 in {FormatTokenCount(details.InputTokenCount, inputBudget)}"
            + $" │ out {FormatTokenCount(details.OutputTokenCount, outputBudget)}"
            + $" │ всего {FormatTokenCount(details.TotalTokenCount, contextWindow)}";
    }

    private static string FormatTokenCount(long? count, int budget)
    {
        if (count is null)
        {
            return "—";
        }

        if (budget > 0)
        {
            double pct = (double)count.Value / budget * 100;
            return $"{count.Value:N0}/{budget:N0} ({pct:F1}%)";
        }

        return $"{count.Value:N0}";
    }

    // Status-bar labels are markup; user-supplied text (paths, model names, tool output)
    // must not be parsed as tags.
    private static string Escape(string text) => MarkupParser.Escape(text);

    // ---- Chat log helpers ----

    private void AppendLine(string text, TranscriptKind kind)
    {
        this.EnsureLineBreak();
        this.AppendText(text + "\n", kind);
    }

    private void AppendText(string text, TranscriptKind kind)
    {
        lock (this.pendingLock)
        {
            this.pendingOutput.Add(new TranscriptChunk(text, kind));
        }

        this.atLineStart = text.EndsWith('\n');
    }

    /// <summary>
    /// Hands all buffered chunks to the transcript as one batch, so a streamed answer grows
    /// four times a second instead of once per token. Must run on the UI thread.
    /// </summary>
    private void FlushOutput()
    {
        List<TranscriptChunk> batch;
        lock (this.pendingLock)
        {
            if (this.pendingOutput.Count == 0)
            {
                return;
            }

            batch = [.. this.pendingOutput];
            this.pendingOutput.Clear();
        }

        this.transcript.Append(batch);
    }

    private void EnsureLineBreak()
    {
        if (!this.atLineStart)
        {
            this.AppendText("\n", TranscriptKind.Markdown);
        }
    }

    // ---- Threading helpers ----

    /// <summary>
    /// Runs an agent operation off the UI thread. SharpConsoleUI's main loop is single
    /// threaded: everything the operation shows must go back through
    /// <see cref="ConsoleWindowSystem.EnqueueOnUIThread(Action)"/>.
    /// </summary>
    private void RunBackground(Func<Task> operation) => _ = Task.Run(async () =>
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background operation failed");
            this.AppendLine($"❌ {ex.GetType().Name}: {ex.Message}", TranscriptKind.Error);
            this.ws.EnqueueOnUIThread(this.FlushOutput);
        }
    });

    /// <summary>
    /// Starts an async UI operation (a modal dialog) on the UI thread and awaits its result
    /// from this background caller. <see cref="ConsoleWindowSystem.InvokeAsync{T}(Func{T})"/>
    /// only covers synchronous work — a dialog completes frames later, so its task has to be
    /// bridged back here.
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.ws.EnqueueOnUIThread(() => _ = BridgeAsync());
        return completion.Task;

        async Task BridgeAsync()
        {
            try
            {
                completion.SetResult(await operation().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }
    }

    /// <summary>Marks a sidebar row as a working-folder header (Enter starts a session in it).</summary>
    private sealed record FolderTag(string Path);

    /// <summary>One chat session: its working folder, host, agent session and saved transcript.</summary>
    private sealed class SessionEntry
    {
        public required string Folder { get; init; }

        public required AgentHost Host { get; init; }

        // Settable: a model switch that changes the context window rebuilds the agent,
        // and the session is re-created from a snapshot against the new agent.
        public required AgentSession Session { get; set; }

        public string Title { get; set; } = DefaultSessionTitle;

        public List<TranscriptChunk> Transcript { get; set; } = [];

        public string UsageText { get; set; } = "📊 TOKENS —";

        public bool AtLineStart { get; set; } = true;
    }

    /// <summary>Disposes every agent host created for this window.</summary>
    public async ValueTask DisposeHostsAsync()
    {
        foreach (var host in this.hosts.Values)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup on exit.
                Log.Warning(ex, "AgentHost dispose failed on exit");
            }
        }

        this.hosts.Clear();
    }
}
