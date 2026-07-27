using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HarnessCli.UI;

// TextView (used read-only for the tool-call details) is obsolete in favour of the separate
// Terminal.Gui.Editor package; see the note in TranscriptView.cs.
#pragma warning disable CS0618 // Type or member is obsolete

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
/// Modal dialogs of the console UI. Every dialog follows the project convention: action
/// buttons on the right, the primary one last (Terminal.Gui makes the last added button
/// the default). Escape always cancels.
/// </summary>
internal static class Dialogs
{
    /// <summary>
    /// Shows the pending tool call (name + arguments) and returns the user's decision.
    /// Dismissing the dialog counts as <see cref="ToolApprovalDecision.Deny"/>.
    /// </summary>
    public static ToolApprovalDecision AskToolApproval(IApplication app, string toolDisplay)
    {
        using var dialog = new Dialog
        {
            Title = "🔐 Подтверждение инструмента",
            Width = Dim.Percent(80),
            Height = Dim.Percent(70),
            SchemeName = Theme.SchemeApp,
        };

        var header = new Label
        {
            Text = "Агент запрашивает разрешение на вызов инструмента:",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            SchemeName = Theme.SchemeApp,
        };

        var details = new TextView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = true,
            Text = toolDisplay,
            SchemeName = Theme.SchemeInput,
        };

        dialog.Add(header, details);

        // Deny first (leftmost), the plain approval last so it is the default.
        dialog.AddButton(NewButton("❌ Отклонить"));
        dialog.AddButton(NewButton("✅ Всегда (эти аргументы)"));
        dialog.AddButton(NewButton("✅ Всегда (инструмент)"));
        dialog.AddButton(NewButton("✅ Разрешить", isDefault: true));

        app.Run(dialog);

        return dialog.Result switch
        {
            1 => ToolApprovalDecision.AlwaysApproveToolWithArguments,
            2 => ToolApprovalDecision.AlwaysApproveTool,
            3 => ToolApprovalDecision.Approve,
            _ => ToolApprovalDecision.Deny,
        };
    }

    /// <summary>
    /// Shows a scrollable list and returns the picked index, or <see langword="null"/> when
    /// the user cancelled. <paramref name="details"/> supplies the hint line shown under the
    /// list for the highlighted entry.
    /// </summary>
    public static int? Select(
        IApplication app,
        string title,
        IReadOnlyList<string> items,
        int selected = 0,
        Func<int, string>? details = null)
    {
        if (items.Count == 0)
        {
            MessageBox.Query(app, title, "Список пуст.", "OK");
            return null;
        }

        using var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(70),
            Height = Dim.Percent(70),
            SchemeName = Theme.SchemeApp,
        };

        var list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = details is null ? Dim.Fill(1) : Dim.Fill(3),
            SchemeName = Theme.SchemeApp,
        };
        list.SetSource(new ObservableCollection<string>(items));
        list.SelectedItem = Math.Clamp(selected, 0, items.Count - 1);
        dialog.Add(list);

        if (details is not null)
        {
            var hint = new Label
            {
                X = 0,
                Y = Pos.Bottom(list),
                Width = Dim.Fill(),
                Height = 2,
                SchemeName = Theme.SchemeBar,
                Text = details(list.SelectedItem ?? 0),
            };
            list.ValueChanged += (_, _) => hint.Text = details(list.SelectedItem ?? 0);
            dialog.Add(hint);
        }

        // Enter (or a double click) on the list is the same as pressing OK.
        list.Accepting += (_, e) =>
        {
            e.Handled = true;
            dialog.Result = 1;
            app.RequestStop(dialog);
        };

        dialog.AddButton(NewButton("Отмена"));
        dialog.AddButton(NewButton("OK", isDefault: true));

        app.Run(dialog);

        return dialog.Result == 1 ? list.SelectedItem : null;
    }

    /// <summary>Single-line text prompt; returns <see langword="null"/> when cancelled or empty.</summary>
    public static string? PromptText(IApplication app, string title, string label, string initial)
    {
        using var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(70),
            Height = 8,
            SchemeName = Theme.SchemeApp,
        };

        var caption = new Label { Text = label, X = 0, Y = 0, Width = Dim.Fill(), SchemeName = Theme.SchemeApp };
        var field = new TextField
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Text = initial,
            SchemeName = Theme.SchemeInput,
        };

        field.Accepting += (_, e) =>
        {
            e.Handled = true;
            dialog.Result = 1;
            app.RequestStop(dialog);
        };

        dialog.Add(caption, field);
        dialog.AddButton(NewButton("Отмена"));
        dialog.AddButton(NewButton("OK", isDefault: true));

        field.SetFocus();
        app.Run(dialog);

        return dialog.Result == 1 && !string.IsNullOrWhiteSpace(field.Text) ? field.Text.Trim() : null;
    }

    /// <summary>
    /// Directory picker (the console counterpart of FolderBrowserDialog). Returns
    /// <see langword="null"/> when the user cancelled.
    /// </summary>
    public static string? PickFolder(IApplication app, string title, string? initial)
    {
        using var dialog = new OpenDialog
        {
            Title = title,
            OpenMode = OpenMode.Directory,
            AllowsMultipleSelection = false,
            MustExist = true,
            SchemeName = Theme.SchemeApp,
        };

        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            dialog.Path = initial;
        }

        app.Run(dialog);

        if (dialog.Canceled)
        {
            return null;
        }

        var picked = dialog.FilePaths.Count > 0 ? dialog.FilePaths[0] : dialog.Path;
        return string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked) ? null : picked;
    }

    /// <summary>Shows an error message box.</summary>
    public static void Error(IApplication app, string title, string message) =>
        MessageBox.ErrorQuery(app, title, message, "OK");

    private static Button NewButton(string text, bool isDefault = false) => new()
    {
        Text = text,
        IsDefault = isDefault,
        SchemeName = isDefault ? Theme.SchemeAccentButton : Theme.SchemeApp,
    };
}
