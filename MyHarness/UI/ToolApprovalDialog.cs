namespace MyHarnessWin.UI;

/// <summary>
/// The user's decision for a single tool-approval request. Mirrors the four choices the
/// Test04 console offered (approve once, always for the tool, always for these arguments, deny).
/// </summary>
public enum ToolApprovalDecision
{
    Approve,
    AlwaysApproveTool,
    AlwaysApproveToolWithArguments,
    Deny,
}

/// <summary>
/// Modal dialog that shows a pending tool call (name + arguments) and lets the user
/// pick one of the four approval decisions. Replaces the console ChoiceFollowUpQuestion.
/// </summary>
public sealed class ToolApprovalDialog : Form
{
    private ToolApprovalDecision decision = ToolApprovalDecision.Deny;

    private ToolApprovalDialog(string toolDisplay)
    {
        this.Text = "Подтверждение инструмента";
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MinimizeBox = false;
        this.MaximizeBox = false;
        this.ShowInTaskbar = false;
        this.ClientSize = new Size(560, 330);
        this.BackColor = Theme.Background;
        this.ForeColor = Theme.TextPrimary;

        var header = new Label
        {
            Text = "🔐 Агент запрашивает разрешение на вызов инструмента:",
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(10, 8, 10, 0),
            ForeColor = Theme.TextPrimary,
        };

        var details = new TextBox
        {
            Text = toolDisplay,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        var detailsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
        detailsPanel.Controls.Add(details);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.TopDown,
            Height = 160,
            Padding = new Padding(10, 0, 10, 6),
            WrapContents = false,
        };

        buttons.Controls.Add(this.MakeButton("✅ Разрешить этот вызов", ToolApprovalDecision.Approve, isDefault: true));
        buttons.Controls.Add(this.MakeButton("✅ Всегда разрешать этот инструмент (любые аргументы)", ToolApprovalDecision.AlwaysApproveTool));
        buttons.Controls.Add(this.MakeButton("✅ Всегда разрешать этот инструмент с этими аргументами", ToolApprovalDecision.AlwaysApproveToolWithArguments));
        buttons.Controls.Add(this.MakeButton("❌ Отклонить", ToolApprovalDecision.Deny));

        this.Controls.Add(detailsPanel);
        this.Controls.Add(buttons);
        this.Controls.Add(header);
    }

    /// <inheritdoc/>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    private Button MakeButton(string text, ToolApprovalDecision decision, bool isDefault = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Width = 530,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Theme.StyleFlatButton(button);

        button.Click += (_, _) =>
        {
            this.decision = decision;
            this.DialogResult = DialogResult.OK;
            this.Close();
        };

        if (isDefault)
        {
            this.AcceptButton = button;
        }

        return button;
    }

    private void InitializeComponent()
    {

    }

    /// <summary>
    /// Shows the dialog modally and returns the user's decision.
    /// Closing the dialog without choosing counts as <see cref="ToolApprovalDecision.Deny"/>.
    /// </summary>
    public static ToolApprovalDecision Ask(IWin32Window owner, string toolDisplay)
    {
        using var dialog = new ToolApprovalDialog(toolDisplay);
        dialog.ShowDialog(owner);
        return dialog.decision;
    }
}
