using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Dialogs;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Flows;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace HarnessCli.UI;

/// <summary>
/// The user's decision for a single tool-approval request. Mirrors the four choices of the
/// WinForms approval dialog (approve once, always for the tool, always for these arguments, deny).
/// </summary>
internal enum ToolApprovalDecision
{
    Approve,
    AlwaysApproveTool,
    AlwaysApproveToolWithArguments,
    Deny,
}

/// <summary>
/// Modal dialogs of the console UI, on top of the SharpConsoleUI dialog primitives. Every
/// dialog follows the project convention: action buttons on the right, the primary one last.
/// Escape always cancels. All of these must be started on the UI thread — see
/// <c>MainWindow.OnUiAsync</c>, which is how the background agent turns reach them.
/// </summary>
internal static class AppDialogs
{
    /// <summary>
    /// Shows the pending tool call (name + arguments) and returns the user's decision.
    /// Dismissing the dialog counts as <see cref="ToolApprovalDecision.Deny"/>.
    /// </summary>
    public static async Task<ToolApprovalDecision> AskToolApprovalAsync(ConsoleWindowSystem ws, string toolDisplay)
    {
        var verdict = await Dialogs.ShowAsync(
            ws,
            "🔐 Подтверждение инструмента",
            $"Агент запрашивает разрешение на вызов инструмента:\n\n{toolDisplay}",
            [
                new FlowButton("❌ Отклонить", FlowVerdict.No),
                new FlowButton("✅ Всегда (эти аргументы)", FlowVerdict.Ignore),
                new FlowButton("✅ Всегда (инструмент)", FlowVerdict.Ok),
                new FlowButton("✅ Разрешить", FlowVerdict.Yes),
            ],
            NotificationSeverityEnum.Warning,
            literal: true).ConfigureAwait(false);

        return verdict switch
        {
            FlowVerdict.Yes => ToolApprovalDecision.Approve,
            FlowVerdict.Ok => ToolApprovalDecision.AlwaysApproveTool,
            FlowVerdict.Ignore => ToolApprovalDecision.AlwaysApproveToolWithArguments,
            _ => ToolApprovalDecision.Deny,
        };
    }

    /// <summary>
    /// Shows a scrollable list and returns the picked index, or <see langword="null"/> when
    /// the user cancelled. <paramref name="details"/> supplies the hint line shown under the
    /// list for the highlighted entry.
    /// </summary>
    public static Task<int?> SelectAsync(
        ConsoleWindowSystem ws,
        string title,
        IReadOnlyList<string> items,
        int selected = 0,
        Func<int, string>? details = null)
    {
        if (items.Count == 0)
        {
            return Dialogs.MessageAsync(ws, title, "Список пуст.").ContinueWith(
                static _ => (int?)null,
                TaskScheduler.Default);
        }

        var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var modal = new WindowBuilder(ws)
            .WithTitle(title)
            .Centered()
            .WithSize(70, 20)
            .AsModal()
            .Build();

        var list = Controls.List()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithDoubleClickActivation(true)
            .Build();

        foreach (var item in items)
        {
            list.Items.Add(new ListItem(MarkupParser.Escape(item)));
        }

        list.SelectedIndex = Math.Clamp(selected, 0, items.Count - 1);
        modal.AddControl(list);

        if (details is not null)
        {
            var hint = Controls.Markup()
                .AddLine(MarkupParser.Escape(details(list.SelectedIndex)))
                .StickyBottom()
                .Build();

            list.SelectedIndexChanged += (_, index) => hint.SetContent(
                [index >= 0 && index < items.Count ? MarkupParser.Escape(details(index)) : string.Empty]);

            modal.AddControl(hint);
        }

        // Enter (or a double click) picks the highlighted entry; Escape and the window's own
        // close button both count as a cancel, so the awaiting turn is never left hanging.
        void Finish(int? result)
        {
            completion.TrySetResult(result);
            modal.Close();
        }

        list.ItemActivated += (_, _) => Finish(list.SelectedIndex >= 0 ? list.SelectedIndex : null);
        modal.KeyPressed += (_, e) =>
        {
            if (e.AlreadyHandled)
            {
                return;
            }

            if (e.KeyInfo.Key == ConsoleKey.Escape)
            {
                Finish(null);
                e.Handled = true;
            }
        };

        modal.OnClosed += (_, _) => completion.TrySetResult(null);

        ws.AddWindow(modal);
        ws.SetActiveWindow(modal);
        list.RequestFocus();

        return completion.Task;
    }

    /// <summary>Single-line text prompt; returns <see langword="null"/> when cancelled or empty.</summary>
    public static async Task<string?> PromptTextAsync(ConsoleWindowSystem ws, string title, string label, string initial)
    {
        string? text = await Dialogs.PromptAsync(ws, title, label, initial).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Directory picker (the console counterpart of FolderBrowserDialog). Returns
    /// <see langword="null"/> when the user cancelled.
    /// </summary>
    public static async Task<string?> PickFolderAsync(ConsoleWindowSystem ws, string? initial)
    {
        string? picked = await FileDialogs.ShowFolderPickerAsync(
            ws,
            startPath: !string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial) ? initial : null)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked) ? null : picked;
    }

    /// <summary>Shows an error message box.</summary>
    public static Task ErrorAsync(ConsoleWindowSystem ws, string title, string message) =>
        Dialogs.MessageAsync(ws, title, message, severity: NotificationSeverityEnum.Danger, literal: true);
}
