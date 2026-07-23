using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace MyHarnessWin.UI;

/// <summary>
/// Dark-mode chat window for the harness agent, laid out like the Claude desktop app:
/// a session sidebar on the left (working folders with a "+" to start a session in them,
/// session titles), a menu strip on top (New, model selector, mode selector, folder
/// picker), the streaming chat log with the input row, and a status bar showing the
/// current working folder, token usage and busy state.
/// Each working folder gets its own <see cref="AgentHost"/>; sessions can be switched
/// at any time — their transcripts are kept per session.
/// The visual layout lives in MainForm.Designer.cs (designer-editable); this file holds
/// the runtime logic and event wiring only.
/// </summary>
public sealed partial class MainForm : Form
{
    private const int SidebarPlusZone = 30;
    private const string DefaultSessionTitle = "Новая сессия";

    // Readable JSON for tool-call arguments in the chat log: without the relaxed
    // encoder the default serializer escapes quotes and non-ASCII as \uXXXX sequences.
    private static readonly JsonSerializerOptions ToolArgsJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, AgentHost> hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> folderOrder = [];
    private readonly List<SessionEntry> sessions = [];
    private readonly string initialFolder;
    private readonly Font folderFont = new("Segoe UI Semibold", 9f);
    private readonly Font sessionFont = new("Segoe UI", 9.5f);

    private SessionEntry? active;
    private AgentModeProvider? modeProvider;
    private bool busy;
    private bool atLineStart = true;
    private bool modelsLoaded;

    // "Auto permissions": every tool-approval request is granted automatically,
    // without showing the approval dialog. Toggled from the "Режим" menu.
    private bool autoPermissions;

    // Streaming chunks are buffered here and flushed into the Markdown viewer in one
    // batch (per-chunk appends made the log flicker and scroll constantly).
    private readonly List<MarkdownViewer.Segment> pendingOutput = [];
    private readonly System.Windows.Forms.Timer flushTimer;

    public MainForm(string initialFolder)
    {
        this.initialFolder = initialFolder;

        this.InitializeComponent();

        // Dark-theme touches the designer cannot express: menu/status renderers,
        // button hover states. Colors themselves are already set in the designer file.
        Theme.StyleMenu(this.menu);
        Theme.StyleStatusStrip(this.statusStrip1);
        Theme.StyleAccentButton(this.sendButton);

        this.folderLabel.Text = initialFolder;

        // While the agent is streaming, buffered chunks are flushed every 2 seconds.
        this.flushTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        this.flushTimer.Tick += (_, _) => this.FlushOutput();

        // ---- Event wiring (kept out of the designer file) ----

         

        foreach (ToolStripItem item in this.modeMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem modeItem && modeItem.Tag is string mode)
            {
                modeItem.Click += async (_, _) => await this.SetModeAsync(mode);
            }
        }

        this.modeAutoPermItem.CheckedChanged += (_, _) =>
        {
            this.autoPermissions = this.modeAutoPermItem.Checked;
            this.AppendLine(
                this.autoPermissions
                    ? "🔓 Auto permissions включён: инструменты выполняются без подтверждения."
                    : "🔐 Auto permissions выключен: подтверждение инструментов снова требуется.",
                MarkdownViewer.SegmentKind.Info);
        };

        this.folderMenuItem.Click += async (_, _) => await this.PickFolderAsync();

        this.sessionList.ClientSizeChanged += (_, _) =>
            this.sessionColumn.Width = Math.Max(40, this.sessionList.ClientSize.Width);
        this.sessionList.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        this.sessionList.DrawItem += this.OnDrawSessionItem;
        this.sessionList.ItemSelectionChanged += (_, e) =>
        {
            if (e.Item?.Tag is FolderTag && e.IsSelected)
            {
                e.Item.Selected = false; // folder headers are labels, not selectable rows
            }
        };
        this.sessionList.MouseUp += this.OnSessionListMouseUp;

        this.input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = this.SendAsync();
            }
        };
        this.sendButton.Click += async (_, _) => await this.SendAsync();

        this.Load += async (_, _) => await this.CreateSessionAsync(this.initialFolder);
    }

    /// <inheritdoc/>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    /// <inheritdoc/>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        this.flushTimer.Dispose();
        foreach (var host in this.hosts.Values)
        {
            try
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort cleanup on exit.
            }
        }

        this.hosts.Clear();
    }

    // ---- Sidebar: rendering and interaction ----

    private void OnDrawSessionItem(object? sender, DrawListViewItemEventArgs e)
    {
        var bounds = e.Bounds;
        bool isFolder = e.Item.Tag is FolderTag;
        bool isActive = e.Item.Tag is SessionEntry entry && ReferenceEquals(entry, this.active);

        using (var back = new SolidBrush(isActive ? Theme.Surface : Theme.Sidebar))
        {
            e.Graphics.FillRectangle(back, bounds);
        }

        if (isFolder)
        {
            var textRect = new Rectangle(bounds.X + 6, bounds.Y, bounds.Width - SidebarPlusZone - 8, bounds.Height);
            TextRenderer.DrawText(e.Graphics, "📂 " + e.Item.Text, this.folderFont, textRect, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            var plusRect = new Rectangle(bounds.Right - SidebarPlusZone, bounds.Y, SidebarPlusZone - 4, bounds.Height);
            TextRenderer.DrawText(e.Graphics, "＋", this.folderFont, plusRect, Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            if (isActive)
            {
                using var accent = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(accent, new Rectangle(bounds.X, bounds.Y + 4, 3, bounds.Height - 8));
            }

            var textRect = new Rectangle(bounds.X + 20, bounds.Y, bounds.Width - 24, bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Item.Text, this.sessionFont, textRect,
                isActive ? Theme.TextPrimary : Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private async void OnSessionListMouseUp(object? sender, MouseEventArgs e)
    {
        if (this.busy || e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit = this.sessionList.HitTest(e.Location);
        switch (hit.Item?.Tag)
        {
            case FolderTag folder when e.X >= hit.Item.Bounds.Right - SidebarPlusZone:
                await this.CreateSessionAsync(folder.Path);
                break;

            case SessionEntry entry when !ReferenceEquals(entry, this.active):
                await this.ActivateSessionAsync(entry);
                break;
        }
    }

    private void RebuildSidebar()
    {
        this.sessionList.BeginUpdate();
        this.sessionList.Items.Clear();

        foreach (var folder in this.folderOrder)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrEmpty(name))
            {
                name = folder;
            }

            this.sessionList.Items.Add(new ListViewItem(name) { Tag = new FolderTag(folder) });

            foreach (var session in this.sessions)
            {
                if (string.Equals(session.Folder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    var item = new ListViewItem(session.Title) { Tag = session };
                    this.sessionList.Items.Add(item);
                    if (ReferenceEquals(session, this.active))
                    {
                        item.Selected = true;
                    }
                }
            }
        }

        this.sessionList.EndUpdate();
        this.sessionList.Invalidate();
    }

    // ---- Hosts, folders and sessions ----

    private async Task PickFolderAsync()
    {
        if (this.busy)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите рабочую папку агента (file_access, поиск и команды будут работать в ней)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = this.active?.Folder ?? this.initialFolder,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            await this.CreateSessionAsync(dialog.SelectedPath);
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
            var host = await Task.Run(() => AgentHost.CreateAsync(folder));
            this.hosts[folder] = host;
            if (!this.modelsLoaded)
            {
                await this.LoadModelListAsync(host);
            }

            return host;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Test05-Win — ошибка запуска агента", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        var host = await this.EnsureHostAsync(folder);
        if (host is null)
        {
            return;
        }

        this.SetBusy(true, "Создание сессии…");
        try
        {
            var session = await host.Agent.CreateSessionAsync();
            var entry = new SessionEntry { Folder = folder, Host = host, Session = session };
            this.sessions.Add(entry);
            if (!this.folderOrder.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                this.folderOrder.Add(folder);
            }

            await this.ActivateSessionAsync(entry, greet: true);
        }
        catch (Exception ex)
        {
            this.AppendLine($"❌ Не удалось создать сессию: {ex.Message}", MarkdownViewer.SegmentKind.Error);
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    /// <summary>
    /// Switches the UI to <paramref name="entry"/>: stores the current transcript,
    /// restores the target one, and repoints the model/mode/folder controls at its host.
    /// </summary>
    private async Task ActivateSessionAsync(SessionEntry entry, bool greet = false)
    {
        this.SaveActiveTranscript();
        this.active = entry;
        this.modeProvider = entry.Host.Agent.GetService<AgentModeProvider>();
        AppSettings.SaveLastWorkingFolder(entry.Folder);

        this.output.LoadSegments(entry.Transcript);
        this.atLineStart = entry.AtLineStart;

        this.usageLabel.Text = entry.UsageText;
        this.folderLabel.Text = entry.Folder;
        this.UpdateModelMenu(entry.Host.ModelName);

        if (greet)
        {
            this.AppendLine(
                "Попросите меня прочитать или отредактировать файл в рабочей папке, " +
                "что-нибудь найти, выполнить команду оболочки или запустить код на Python.",
                MarkdownViewer.SegmentKind.Info);
        }

        this.RebuildSidebar();
        await this.RefreshModeAsync();
        this.input.Focus();
    }

    private void SaveActiveTranscript()
    {
        this.FlushOutput(); // the snapshot must include everything still buffered

        if (this.active is not null)
        {
            this.active.Transcript = this.output.Snapshot();
            this.active.AtLineStart = this.atLineStart;
            this.active.UsageText = this.usageLabel.Text ?? string.Empty;
        }
    }

    // ---- Chat turn ----

    private async Task SendAsync()
    {
        var entry = this.active;
        string text = this.input.Text.Trim();
        if (this.busy || entry is null || text.Length == 0)
        {
            return;
        }

        this.input.Clear();
        this.AppendLine($"👤 Вы: {text}", MarkdownViewer.SegmentKind.User, bold: true);

        // The first message names the session in the sidebar.
        if (entry.Title == DefaultSessionTitle)
        {
            entry.Title = text.Length > 36 ? text[..36] + "…" : text;
            this.RebuildSidebar();
        }

        this.SetBusy(true, "Агент работает…");
        try
        {
            await this.RunTurnAsync(entry, [new ChatMessage(ChatRole.User, text)]);
        }
        finally
        {
            this.SetBusy(false);
            await this.RefreshModeAsync();
            this.input.Focus();
        }
    }

    /// <summary>
    /// Runs one agent turn, re-invoking the agent with approval responses until no
    /// tool-approval requests remain (the WinForms equivalent of the console runner loop).
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
                await foreach (var update in entry.Host.Agent.RunStreamingAsync(next, entry.Session))
                {
                    foreach (var content in update.Contents)
                    {
                        this.HandleContent(content, approvals);
                    }
                }
            }
            catch (Exception ex)
            {
                this.AppendLine($"❌ Ошибка потока: {ex.GetType().Name}: {ex.Message}", MarkdownViewer.SegmentKind.Error);
            }

            this.EnsureLineBreak();
            this.FlushOutput(); // the full context must be visible before modal approval dialogs
            next = approvals.Count > 0 ? this.CollectApprovalResponses(approvals) : null;
        }
    }

    private void HandleContent(AIContent content, List<ToolApprovalRequestContent> approvals)
    {
        switch (content)
        {
            case ToolApprovalRequestContent approvalRequest:
                approvals.Add(approvalRequest);
                this.AppendLine($"⚠️ Требуется подтверждение: {FormatToolCall(approvalRequest.ToolCall, maxArgsLength: 160)}", MarkdownViewer.SegmentKind.Tool);
                break;

            case FunctionCallContent functionCall:
                this.AppendLine($"🔧 Вызов инструмента: {FormatToolCall(functionCall, maxArgsLength: 160)}…", MarkdownViewer.SegmentKind.Tool);
                break;

            case ErrorContent error:
                string errorText = $"❌ Ошибка: {error.Message}";
                if (!string.IsNullOrWhiteSpace(error.ErrorCode))
                {
                    errorText += $" (код: {error.ErrorCode})";
                }

                this.AppendLine(errorText, MarkdownViewer.SegmentKind.Error);
                break;

            case UsageContent usage:
                this.usageLabel.Text = usage.Details is not null
                    ? FormatUsage(usage.Details)
                    : "📊 Tokens —";
                break;

            case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                this.AppendText(reasoning.Text, MarkdownViewer.SegmentKind.Reasoning);
                break;

            case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                this.AppendText(textContent.Text, MarkdownViewer.SegmentKind.Markdown);
                break;
        }
    }

    /// <summary>
    /// Shows the approval dialog for every pending request and turns each decision into
    /// the corresponding approval-response message for the next agent invocation.
    /// </summary>
    private List<ChatMessage> CollectApprovalResponses(List<ToolApprovalRequestContent> approvals)
    {
        var responses = new List<ChatMessage>(approvals.Count);

        foreach (var request in approvals)
        {
            // "Auto permissions" mode: grant every request without showing the dialog.
            if (this.autoPermissions)
            {
                this.AppendLine($"🔓 Авто-разрешено: {FormatToolCall(request.ToolCall, maxArgsLength: 120)}",
                    MarkdownViewer.SegmentKind.Info);
                responses.Add(new ChatMessage(ChatRole.User,
                    [request.CreateResponse(approved: true, reason: "Auto permissions mode")]));
                continue;
            }

            var decision = ToolApprovalDialog.Ask(this, FormatToolCall(request.ToolCall, maxArgsLength: 2000));

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

            this.AppendLine($"🔹 {outcome}: {FormatToolCall(request.ToolCall, maxArgsLength: 120)}",
                decision == ToolApprovalDecision.Deny ? MarkdownViewer.SegmentKind.Error : MarkdownViewer.SegmentKind.Info);

            responses.Add(new ChatMessage(ChatRole.User, [response]));
        }

        return responses;
    }

    // ---- Model selection (menu) ----

    /// <summary>
    /// Fills the model menu: the currently configured model first, then whatever the
    /// Ollama endpoint reports (/api/tags), plus a free-text entry item for endpoints
    /// that do not support listing.
    /// </summary>
    private async Task LoadModelListAsync(AgentHost host)
    {
        this.modelsLoaded = true;

        var models = await host.ListAvailableModelsAsync();
        var names = new List<string> { host.ModelName };
        foreach (var model in models)
        {
            if (!names.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(model);
            }
        }

        this.modelMenu.DropDownItems.Clear();
        foreach (var name in names)
        {
            var item = new ToolStripMenuItem(name) { Tag = name, ForeColor = Theme.TextPrimary };
            item.Click += async (_, _) => await this.ApplyModelAsync(name);
            this.modelMenu.DropDownItems.Add(item);
        }

        this.modelMenu.DropDownItems.Add(new ToolStripSeparator());
        var custom = new ToolStripMenuItem("Ввести имя модели…") { ForeColor = Theme.TextPrimary };
        custom.Click += async (_, _) =>
        {
            string? name = PromptText(this, "Модель", "Имя модели Ollama:", this.active?.Host.ModelName ?? string.Empty);
            if (name is not null)
            {
                await this.ApplyModelAsync(name);
            }
        };
        this.modelMenu.DropDownItems.Add(custom);

        this.UpdateModelMenu(host.ModelName);

        if (models.Count == 0)
        {
            this.AppendLine(
                "Список моделей с сервера получить не удалось — используйте «Модель → Ввести имя модели…».",
                MarkdownViewer.SegmentKind.Info);
        }
    }

    /// <summary>Shows the current model in the menu title and checks it in the dropdown.</summary>
    private void UpdateModelMenu(string modelName)
    {
        this.modelMenu.Text = $"Модель: {modelName}";
        foreach (ToolStripItem item in this.modelMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is string name)
            {
                menuItem.Checked = string.Equals(name, modelName, StringComparison.OrdinalIgnoreCase);
            }
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
        if (this.busy || entry is null)
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
            var affected = this.sessions.Where(s => ReferenceEquals(s.Host, host)).ToList();
            var snapshots = new Dictionary<SessionEntry, JsonElement>();
            foreach (var session in affected)
            {
                snapshots[session] = await oldAgent.SerializeSessionAsync(session.Session);
            }

            bool rebuilt = await host.SetModelAsync(modelName);
            if (rebuilt)
            {
                foreach (var session in affected)
                {
                    session.Session = await host.Agent.DeserializeSessionAsync(snapshots[session]);
                }

                this.modeProvider = host.Agent.GetService<AgentModeProvider>();
            }

            this.Text = $"Test05-Win — {modelName} — {entry.Folder}";
            this.UpdateModelMenu(modelName);
            this.AppendLine(
                $"Модель переключена: {modelName} (контекстное окно: {host.ContextWindowTokens:N0} токенов; " +
                "действует со следующего сообщения, сессия сохранена)",
                MarkdownViewer.SegmentKind.Info);
        }
        catch (Exception ex)
        {
            this.AppendLine($"❌ Не удалось переключить модель: {ex.Message}", MarkdownViewer.SegmentKind.Error);
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    // ---- Mode selection (menu) ----

    private async Task SetModeAsync(string mode)
    {
        if (this.busy || this.modeProvider is null || this.active is null)
        {
            return;
        }

        try
        {
            await this.modeProvider.SetModeAsync(this.active.Session, mode);
            this.UpdateModeMenu(mode);
            this.AppendLine($"Режим переключён: {mode}", MarkdownViewer.SegmentKind.Info);
        }
        catch (Exception ex)
        {
            this.AppendLine($"❌ Не удалось переключить режим: {ex.Message}", MarkdownViewer.SegmentKind.Error);
            await this.RefreshModeAsync();
        }
    }

    /// <summary>Shows the current mode in the menu title and checks it in the dropdown.</summary>
    private void UpdateModeMenu(string mode)
    {
        this.modeMenu.Text = $"Режим: {mode}";
        foreach (ToolStripItem item in this.modeMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is string name)
            {
                menuItem.Checked = string.Equals(name, mode, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private async Task RefreshModeAsync()
    {
        if (this.modeProvider is null || this.active is null)
        {
            return;
        }

        try
        {
            string mode = await this.modeProvider.GetModeAsync(this.active.Session);
            this.UpdateModeMenu(mode);
        }
        catch
        {
            // Mode display is cosmetic; ignore transient provider errors.
        }
    }

    // ---- State / formatting helpers ----

    private void SetBusy(bool busy, string? state = null)
    {
        this.busy = busy;
        this.sendButton.Enabled = !busy;
        // ReadOnly (not Enabled=false) keeps the dark colors — a disabled TextBox
        // repaints with the light system palette.
        this.input.ReadOnly = busy;
        this.modelMenu.Enabled = !busy;
        this.modeMenu.Enabled = !busy;
        this.folderMenuItem.Enabled = !busy;
        this.stateLabel.Text = state ?? (busy ? "Агент работает…" : "Готово");

        if (!busy)
        {
            this.FlushOutput(); // show the tail of the answer as soon as the turn ends
        }
    }

    /// <summary>Minimal dark-mode text prompt (WinForms has no built-in input box).</summary>
    private static string? PromptText(IWin32Window owner, string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 108),
            BackColor = Theme.Background,
            ForeColor = Theme.TextPrimary,
        };
        form.HandleCreated += (_, _) => Theme.ApplyDarkTitleBar(form);

        var caption = new Label { Text = label, Location = new Point(12, 10), AutoSize = true, ForeColor = Theme.TextPrimary };
        var box = new TextBox
        {
            Location = new Point(12, 34),
            Width = 396,
            Text = initial,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(252, 70), Width = 75 };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(333, 70), Width = 75 };
        Theme.StyleFlatButton(ok);
        Theme.StyleFlatButton(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Controls.AddRange([caption, box, ok, cancel]);

        return form.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim()
            : null;
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
            catch (NotSupportedException)
            {
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
        return $"📊 Tokens — input: {FormatTokenCount(details.InputTokenCount, inputBudget)}"
            + $" | output: {FormatTokenCount(details.OutputTokenCount, outputBudget)}"
            + $" | total: {FormatTokenCount(details.TotalTokenCount, contextWindow)}";
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

    // ---- Chat log helpers (Markdown viewer) ----

    private void AppendLine(string text, MarkdownViewer.SegmentKind kind, bool bold = false)
    {
        this.EnsureLineBreak();
        this.AppendText(text + "\n", kind, bold);
    }

    private void AppendText(string text, MarkdownViewer.SegmentKind kind, bool bold = false)
    {
        this.pendingOutput.Add(new MarkdownViewer.Segment(text, kind, bold));
        this.atLineStart = text.EndsWith('\n');

        if (this.busy)
        {
            // Streaming turn: let the timer flush the batch every 2 seconds.
            this.flushTimer.Start();
        }
        else
        {
            // One-off messages (greetings, mode/model switches) show up immediately.
            this.FlushOutput();
        }
    }

    /// <summary>
    /// Hands all buffered chunks to the Markdown viewer as one batch — the page is
    /// re-rendered and scrolled once per flush (every 2 seconds while streaming),
    /// so nothing flickers.
    /// </summary>
    private void FlushOutput()
    {
        if (this.pendingOutput.Count == 0)
        {
            this.flushTimer.Stop();
            return;
        }

        this.output.AppendSegments(this.pendingOutput);
        this.pendingOutput.Clear();

        if (!this.busy)
        {
            this.flushTimer.Stop();
        }
    }

    private void EnsureLineBreak()
    {
        if (!this.atLineStart)
        {
            this.AppendText("\n", MarkdownViewer.SegmentKind.Markdown);
        }
    }

    /// <summary>Marks a sidebar row as a working-folder header (its "+" starts a session).</summary>
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

        public List<MarkdownViewer.Segment> Transcript { get; set; } = [];

        public string UsageText { get; set; } = "📊 Tokens —";

        public bool AtLineStart { get; set; } = true;
    }
}
