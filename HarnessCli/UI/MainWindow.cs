using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Serilog;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HarnessCli.UI;

/// <summary>
/// Console chat window for the harness agent — the Terminal.Gui port of the WinForms
/// MainForm, with the same structure: a session sidebar on the left (working folders and
/// their sessions), a menu bar on top (session, model, mode, resources), the streaming chat
/// transcript with the input row, and a status bar showing the working folder, token usage
/// and busy state. Each working folder gets its own <see cref="AgentHost"/>; sessions can be
/// switched at any time — their transcripts are kept per session.
/// Agent turns run on a background task; every UI mutation is marshalled back through
/// <see cref="IApplication.Invoke(Action)"/>.
/// </summary>
internal sealed class MainWindow : Window
{
    private const string DefaultSessionTitle = "Новая сессия";
    private const int SidebarWidth = 30;

    // Readable JSON for tool-call arguments in the chat log: without the relaxed
    // encoder the default serializer escapes quotes and non-ASCII as \uXXXX sequences.
    private static readonly JsonSerializerOptions ToolArgsJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IApplication app;
    private readonly Dictionary<string, AgentHost> hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> folderOrder = [];
    private readonly List<SessionEntry> sessions = [];
    private readonly string initialFolder;

    // Sidebar rows: the observable collection feeds the ListView, the tag list maps a row
    // index back to the folder header or the session it stands for.
    private readonly ObservableCollection<string> sidebarRows = [];
    private readonly List<object> sidebarTags = [];

    // Streaming chunks are buffered here and flushed into the transcript in one batch
    // (per-chunk redraws made the log flicker and scroll constantly).
    private readonly List<TranscriptView.Segment> pendingOutput = [];
    private readonly Lock pendingLock = new();

    private readonly ListView sidebar;
    private readonly FrameView chatFrame;
    private readonly TranscriptView transcript;
    private readonly TextField input;
    private readonly Button sendButton;
    private readonly Shortcut folderStatus;
    private readonly Shortcut usageStatus;
    private readonly Shortcut stateStatus;

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

    public MainWindow(IApplication app, string initialFolder)
    {
        this.app = app;
        this.initialFolder = initialFolder;

        this.BorderStyle = LineStyle.None;
        this.SchemeName = Theme.SchemeApp;
        this.Title = "HarnessCli";

        var menu = this.BuildMenu();

        var sidebarFrame = new FrameView
        {
            Title = "Сессии",
            X = 0,
            Y = 1,
            Width = SidebarWidth,
            Height = Dim.Fill(1),
            SchemeName = Theme.SchemeSidebar,
        };

        this.sidebar = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = Theme.SchemeSidebar,
        };
        this.sidebar.SetSource(this.sidebarRows);
        this.sidebar.Accepting += this.OnSidebarAccepting;
        sidebarFrame.Add(this.sidebar);

        this.chatFrame = new FrameView
        {
            Title = "Чат",
            X = SidebarWidth,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            SchemeName = Theme.SchemeApp,
        };

        this.transcript = new TranscriptView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };

        this.input = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(14),
            SchemeName = Theme.SchemeInput,
        };
        this.input.Accepting += (_, e) =>
        {
            e.Handled = true;
            this.Send();
        };

        this.sendButton = new Button
        {
            Text = "Отправить",
            X = Pos.AnchorEnd(13),
            Y = Pos.AnchorEnd(1),
            Width = 13,
            IsDefault = true,
            SchemeName = Theme.SchemeAccentButton,
        };
        this.sendButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            this.Send();
        };

        this.chatFrame.Add(this.transcript, this.input, this.sendButton);

        this.folderStatus = new Shortcut { Title = initialFolder, CanFocus = false };
        this.usageStatus = new Shortcut { Title = "📊 TOKENS —", CanFocus = false };
        this.stateStatus = new Shortcut { Title = "Готово", CanFocus = false };
        var statusBar = new StatusBar([this.folderStatus, this.usageStatus, this.stateStatus])
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            SchemeName = Theme.SchemeBar,
        };

        this.Add(menu, sidebarFrame, this.chatFrame, statusBar);

        // While the agent is streaming, buffered chunks are flushed once a second.
        this.app.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            this.FlushOutput();
            return true;
        });

        // The first session is created once the main loop is up (host creation is slow and
        // must not block the constructor).
        this.app.Invoke(() => _ = Task.Run(() => this.CreateSessionAsync(this.initialFolder)));
    }

    // ---- Menu ----

    private MenuBar BuildMenu()
    {
        var menu = new MenuBar([
            new MenuBarItem("_Сессия", [
                new MenuItem("_Новая сессия", "в текущей папке", () => this.RunBackground(() => this.CreateSessionAsync(this.active?.Folder ?? this.initialFolder)), Key.N.WithCtrl),
                new MenuItem("_Рабочая папка…", "новая сессия в другой папке", this.PickFolder, Key.O.WithCtrl),
                new MenuItem("_Очистить вывод", "стереть транскрипт сессии", this.ClearTranscript),
                new Line(),
                new MenuItem("_Выход", "", () => this.app.RequestStop(this), Key.Q.WithCtrl),
            ]),
            new MenuBarItem("_Модель", [
                new MenuItem("_Выбрать из списка…", "модели с сервера Ollama", () => this.RunBackground(this.ChooseModelAsync)),
                new MenuItem("_Ввести имя модели…", "для эндпоинтов без /api/tags", this.EnterModelName),
            ]),
            new MenuBarItem("_Режим", [
                new MenuItem("_execute", "выполнять действия сразу", () => this.RunBackground(() => this.SetModeAsync("execute"))),
                new MenuItem("_plan", "сначала составить план", () => this.RunBackground(() => this.SetModeAsync("plan"))),
                new Line(),
                new MenuItem("_Auto permissions", "разрешать инструменты без запроса", this.ToggleAutoPermissions, Key.P.WithCtrl),
            ]),
            new MenuBarItem("_Ресурсы", [
                new MenuItem("_Агенты…", "роли из agents\\", () => this.ShowResources("Агенты", ResourceKind.Agents)),
                new MenuItem("_Навыки…", "SKILL.md из skills\\", () => this.ShowResources("Навыки (skills)", ResourceKind.Skills)),
                new MenuItem("_Плагины…", "плагины из plugins\\", () => this.ShowResources("Плагины", ResourceKind.Plugins)),
                new MenuItem("_Скрипты…", "dotnet-скрипты рабочей папки", () => this.ShowResources("Скрипты (dotnet)", ResourceKind.Scripts)),
            ]),
        ])
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            SchemeName = Theme.SchemeBar,
        };

        return menu;
    }

    // ---- Sidebar ----

    private void OnSidebarAccepting(object? sender, CommandEventArgs e)
    {
        e.Handled = true;
        if (this.busy)
        {
            return;
        }

        int index = this.sidebar.SelectedItem ?? -1;
        if (index < 0 || index >= this.sidebarTags.Count)
        {
            return;
        }

        switch (this.sidebarTags[index])
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
        int previous = this.sidebar.SelectedItem ?? 0;

        this.sidebarRows.Clear();
        this.sidebarTags.Clear();

        foreach (var folder in this.folderOrder)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrEmpty(name))
            {
                name = folder;
            }

            this.sidebarRows.Add($"📂 {name}");
            this.sidebarTags.Add(new FolderTag(folder));

            foreach (var session in this.sessions)
            {
                if (!string.Equals(session.Folder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isActive = ReferenceEquals(session, this.active);
                this.sidebarRows.Add($" {(isActive ? "▸" : " ")} {session.Title}");
                this.sidebarTags.Add(session);
            }
        }

        if (this.sidebarRows.Count > 0)
        {
            this.sidebar.SelectedItem = Math.Clamp(previous, 0, this.sidebarRows.Count - 1);
        }

        this.sidebar.SetNeedsDraw();
    }

    // ---- Hosts, folders and sessions ----

    private void PickFolder()
    {
        if (this.busy)
        {
            return;
        }

        string? folder = Dialogs.PickFolder(this.app, 
            "Рабочая папка агента (file_access, поиск и команды работают в ней)",
            this.active?.Folder ?? this.initialFolder);

        if (folder is not null)
        {
            this.RunBackground(() => this.CreateSessionAsync(folder));
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
            var host = await AgentHost.CreateAsync(folder).ConfigureAwait(false);
            this.hosts[folder] = host;

            // Mirror plugin logs into the chat output (they arrive from background threads).
            host.Plugins.PluginLog += (plugin, message) =>
            {
                this.AppendLine($"🔌 [{plugin}] {message}", TranscriptView.SegmentKind.Info);
                this.app.Invoke(this.FlushOutput);
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
            this.app.Invoke(() => Dialogs.Error(this.app, "Ошибка запуска агента", ex.Message));
            return null;
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    /// <summary>Creates a new session in <paramref name="folder"/> and makes it active.</summary>
    private async Task CreateSessionAsync(string folder)
    {
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
            await this.OnUiAsync(() =>
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
            this.AppendLine($"❌ Не удалось создать сессию: {ex.Message}", TranscriptView.SegmentKind.Error);
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
        await this.OnUiAsync(() =>
        {
            this.SaveActiveTranscript();
            this.active = entry;
            this.atLineStart = entry.AtLineStart;
            this.transcript.LoadSegments(entry.Transcript);
            this.usageStatus.Title = entry.UsageText;
            this.folderStatus.Title = entry.Folder;
            this.chatFrame.Title = $"Чат — {entry.Host.ModelName}";
            this.RebuildSidebar();
        }).ConfigureAwait(false);

        this.modeProvider = entry.Host.Agent.GetService<AgentModeProvider>();
        AppSettings.SaveLastWorkingFolder(entry.Folder);

        if (greet)
        {
            this.AppendLine(
                "Попросите меня прочитать или отредактировать файл в рабочей папке, " +
                "что-нибудь найти, выполнить команду оболочки или запустить код на Python. " +
                "Enter — отправить, F9 — меню, Tab — переход между панелями.",
                TranscriptView.SegmentKind.Info);
        }

        await this.RefreshModeAsync().ConfigureAwait(false);
        this.app.Invoke(() =>
        {
            this.FlushOutput();
            this.input.SetFocus();
        });
    }

    private void SaveActiveTranscript()
    {
        this.FlushOutput(); // the snapshot must include everything still buffered

        if (this.active is not null)
        {
            this.active.Transcript = this.transcript.Snapshot();
            this.active.AtLineStart = this.atLineStart;
            this.active.UsageText = this.usageStatus.Title ?? string.Empty;
        }
    }

    private void ClearTranscript()
    {
        lock (this.pendingLock)
        {
            this.pendingOutput.Clear();
        }

        this.transcript.ClearTranscript();
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
    private void ShowResources(string title, ResourceKind kind)
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

        int? picked = Dialogs.Select(
            this.app,
            title,
            items.Select(i => i.Name).ToList(),
            details: index => items[index].Summary.Length > 0 ? items[index].Summary : items[index].Path);

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
        this.input.Text = prompt;
        this.input.InsertionPoint = prompt.Length;
        this.input.SetFocus();
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

    private void Send()
    {
        var entry = this.active;
        string text = (this.input.Text ?? string.Empty).Trim();
        if (this.busy || entry is null || text.Length == 0)
        {
            return;
        }

        this.input.Text = string.Empty;
        this.AppendLine($"👤 Вы: {text}", TranscriptView.SegmentKind.User, bold: true);

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
            this.app.Invoke(() =>
            {
                this.FlushOutput();
                this.input.SetFocus();
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
                this.AppendLine($"❌ Ошибка потока: {ex.GetType().Name}: {ex.Message}", TranscriptView.SegmentKind.Error);
            }

            this.EnsureLineBreak();

            // The full context must be visible before the modal approval dialogs open.
            await this.OnUiAsync(() =>
            {
                this.FlushOutput();
                return true;
            }).ConfigureAwait(false);

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
            var affected = await this.OnUiAsync(
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
                TranscriptView.SegmentKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Plugin tools refresh failed");
            this.AppendLine($"❌ Не удалось обновить инструменты плагинов: {ex.Message}", TranscriptView.SegmentKind.Error);
        }
    }

    private void HandleContent(AIContent content, List<ToolApprovalRequestContent> approvals)
    {
        switch (content)
        {
            case ToolApprovalRequestContent approvalRequest:
                approvals.Add(approvalRequest);
                this.AppendLine($"⚠️ Требуется подтверждение: {FormatToolCall(approvalRequest.ToolCall, maxArgsLength: 160)}", TranscriptView.SegmentKind.Tool);
                break;

            case FunctionCallContent functionCall:
                this.AppendLine($"🔧 Вызов инструмента: {FormatToolCall(functionCall, maxArgsLength: 160)}…", TranscriptView.SegmentKind.Tool);
                break;

            case ErrorContent error:
                string errorText = $"❌ Ошибка: {error.Message}";
                if (!string.IsNullOrWhiteSpace(error.ErrorCode))
                {
                    errorText += $" (код: {error.ErrorCode})";
                }

                this.AppendLine(errorText, TranscriptView.SegmentKind.Error);
                break;

            case UsageContent usage:
                string usageText = usage.Details is not null ? this.FormatUsage(usage.Details) : "📊 TOKENS —";
                this.app.Invoke(() => this.usageStatus.Title = usageText);
                break;

            case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                this.AppendText(reasoning.Text, TranscriptView.SegmentKind.Reasoning);
                break;

            case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                this.AppendText(textContent.Text, TranscriptView.SegmentKind.Markdown);
                break;
        }
    }

    /// <summary>
    /// Shows the approval dialog for every pending request and turns each decision into
    /// the corresponding approval-response message for the next agent invocation.
    /// The dialogs run on the UI thread while this background turn awaits them.
    /// </summary>
    private Task<List<ChatMessage>> CollectApprovalResponsesAsync(List<ToolApprovalRequestContent> approvals)
    {
        // "Auto permissions" mode: grant every request without showing a dialog.
        if (this.autoPermissions)
        {
            var granted = new List<ChatMessage>(approvals.Count);
            foreach (var request in approvals)
            {
                this.AppendLine(
                    $"🔓 Авто-разрешено: {FormatToolCall(request.ToolCall, maxArgsLength: 120)}",
                    TranscriptView.SegmentKind.Info);
                granted.Add(new ChatMessage(ChatRole.User,
                    [request.CreateResponse(approved: true, reason: "Auto permissions mode")]));
            }

            return Task.FromResult(granted);
        }

        return this.OnUiAsync(() =>
        {
            var responses = new List<ChatMessage>(approvals.Count);

            foreach (var request in approvals)
            {
                var decision = Dialogs.AskToolApproval(this.app, FormatToolCall(request.ToolCall, maxArgsLength: 2000));

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
                    decision == ToolApprovalDecision.Deny ? TranscriptView.SegmentKind.Error : TranscriptView.SegmentKind.Info);

                responses.Add(new ChatMessage(ChatRole.User, [response]));
            }

            this.FlushOutput();
            return responses;
        });
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
                TranscriptView.SegmentKind.Info);
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
        int? picked = await this.OnUiAsync(() => Dialogs.Select(this.app, "Модель", models, Math.Max(current, 0))).ConfigureAwait(false);

        if (picked is int index && index >= 0 && index < models.Count)
        {
            await this.ApplyModelAsync(models[index]).ConfigureAwait(false);
        }
    }

    private void EnterModelName()
    {
        if (this.busy)
        {
            return;
        }

        string? name = Dialogs.PromptText(this.app, "Модель", "Имя модели Ollama:", this.active?.Host.ModelName ?? string.Empty);
        if (name is not null)
        {
            this.RunBackground(() => this.ApplyModelAsync(name));
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
            var affected = await this.OnUiAsync(
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

            this.app.Invoke(() => this.chatFrame.Title = $"Чат — {host.ModelName}");
            this.AppendLine(
                $"Модель переключена: {modelName} (контекстное окно: {host.ContextWindowTokens:N0} токенов; " +
                "действует со следующего сообщения, сессия сохранена)",
                TranscriptView.SegmentKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Model switch to {Model} failed", modelName);
            this.AppendLine($"❌ Не удалось переключить модель: {ex.Message}", TranscriptView.SegmentKind.Error);
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
            TranscriptView.SegmentKind.Info);
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
            this.AppendLine($"Режим переключён: {mode}", TranscriptView.SegmentKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mode switch to {Mode} failed", mode);
            this.AppendLine($"❌ Не удалось переключить режим: {ex.Message}", TranscriptView.SegmentKind.Error);
            await this.RefreshModeAsync().ConfigureAwait(false);
        }

        this.app.Invoke(() =>
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
            this.app.Invoke(this.UpdateStateLabel);
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

        this.app.Invoke(() =>
        {
            this.sendButton.Enabled = !busy;
            this.input.ReadOnly = busy;
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
        this.stateStatus.Title =
            $"{state} │ режим: {this.currentMode}{(this.autoPermissions ? " │ 🔓 auto" : string.Empty)}";
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

    // ---- Chat log helpers ----

    private void AppendLine(string text, TranscriptView.SegmentKind kind, bool bold = false)
    {
        this.EnsureLineBreak();
        this.AppendText(text + "\n", kind, bold);
    }

    private void AppendText(string text, TranscriptView.SegmentKind kind, bool bold = false)
    {
        lock (this.pendingLock)
        {
            this.pendingOutput.Add(new TranscriptView.Segment(text, kind, bold));
        }

        this.atLineStart = text.EndsWith('\n');
    }

    /// <summary>
    /// Hands all buffered chunks to the transcript as one batch — it re-renders and scrolls
    /// once per flush (once a second while streaming), so nothing flickers.
    /// Must run on the UI thread.
    /// </summary>
    private void FlushOutput()
    {
        List<TranscriptView.Segment> batch;
        lock (this.pendingLock)
        {
            if (this.pendingOutput.Count == 0)
            {
                return;
            }

            batch = [.. this.pendingOutput];
            this.pendingOutput.Clear();
        }

        this.transcript.AppendSegments(batch);
    }

    private void EnsureLineBreak()
    {
        if (!this.atLineStart)
        {
            this.AppendText("\n", TranscriptView.SegmentKind.Markdown);
        }
    }

    // ---- Threading helpers ----

    /// <summary>
    /// Runs an agent operation off the UI thread. Terminal.Gui's main loop is single
    /// threaded: everything the operation shows must go back through
    /// <see cref="this.app.Invoke(Action)"/>.
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
            this.AppendLine($"❌ {ex.GetType().Name}: {ex.Message}", TranscriptView.SegmentKind.Error);
            this.app.Invoke(this.FlushOutput);
        }
    });

    /// <summary>Runs <paramref name="action"/> on the UI thread and awaits its completion.</summary>
    private Task OnUiAsync(Action action) => this.OnUiAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>Runs <paramref name="func"/> on the UI thread and awaits its result.</summary>
    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.app.Invoke(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task;
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

        public List<TranscriptView.Segment> Transcript { get; set; } = [];

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
