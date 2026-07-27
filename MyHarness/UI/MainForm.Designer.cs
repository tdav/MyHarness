namespace MyHarnessWin.UI;

partial class MainForm
{
    /// <summary>Required designer variable.</summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>Clean up any resources being used.</summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// Colors mirror the palette in <see cref="Theme"/> (the designer cannot keep
    /// live references); renderers and hover styles are applied in the constructor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
        menu = new MenuStrip();
        folderMenuItem = new ToolStripMenuItem();
        resourcesMenu = new ToolStripMenuItem();
        agentsMenuItem = new ToolStripMenuItem();
        skillsMenuItem = new ToolStripMenuItem();
        pluginsMenuItem = new ToolStripMenuItem();
        scriptsMenuItem = new ToolStripMenuItem();
        modelMenu = new ToolStripMenuItem();
        modeMenu = new ToolStripMenuItem();
        modeExecuteItem = new ToolStripMenuItem();
        modePlanItem = new ToolStripMenuItem();
        modeAutoPermItem = new ToolStripMenuItem();
        sidebar = new Panel();
        sessionList = new ListView();
        sessionColumn = new ColumnHeader();
        rowHeightImages = new ImageList(components);
        content = new Panel();
        outputPanel = new Panel();
        output = new MarkdownViewer();
        inputPanel = new Panel();
        inputHost = new Panel();
        input = new TextBox();
        sendButton = new Button();
        statusStrip1 = new StatusStrip();
        toolStripStatusLabel2 = new ToolStripStatusLabel();
        folderLabel = new ToolStripStatusLabel();
        toolStripStatusLabel1 = new ToolStripStatusLabel();
        usageLabel = new ToolStripStatusLabel();
        stateLabel = new ToolStripStatusLabel();
        menu.SuspendLayout();
        sidebar.SuspendLayout();
        content.SuspendLayout();
        outputPanel.SuspendLayout();
        inputPanel.SuspendLayout();
        inputHost.SuspendLayout();
        statusStrip1.SuspendLayout();
        SuspendLayout();
        // 
        // menu
        // 
        menu.BackColor = Color.FromArgb(21, 28, 38);
        menu.ForeColor = Color.FromArgb(232, 237, 242);
        menu.Items.AddRange(new ToolStripItem[] { folderMenuItem, resourcesMenu, modelMenu, modeMenu });
        menu.Location = new Point(0, 0);
        menu.Name = "menu";
        menu.Padding = new Padding(6, 4, 6, 4);
        menu.ShowItemToolTips = true;
        menu.Size = new Size(1080, 27);
        menu.TabIndex = 0;
        // 
        // folderMenuItem
        // 
        folderMenuItem.ForeColor = Color.FromArgb(232, 237, 242);
        folderMenuItem.Name = "folderMenuItem";
        folderMenuItem.Size = new Size(96, 19);
        folderMenuItem.Text = "NEW PROJECT";
        // 
        // resourcesMenu
        // 
        resourcesMenu.DropDownItems.AddRange(new ToolStripItem[] { agentsMenuItem, skillsMenuItem, pluginsMenuItem, scriptsMenuItem });
        resourcesMenu.ForeColor = Color.FromArgb(232, 237, 242);
        resourcesMenu.Name = "resourcesMenu";
        resourcesMenu.Size = new Size(100, 19);
        resourcesMenu.Text = "РАСШИРЕНИЯ";
        // 
        // agentsMenuItem
        // 
        agentsMenuItem.ForeColor = Color.FromArgb(232, 237, 242);
        agentsMenuItem.Name = "agentsMenuItem";
        agentsMenuItem.Size = new Size(180, 22);
        agentsMenuItem.Text = "Агенты";
        // 
        // skillsMenuItem
        // 
        skillsMenuItem.ForeColor = Color.FromArgb(232, 237, 242);
        skillsMenuItem.Name = "skillsMenuItem";
        skillsMenuItem.Size = new Size(180, 22);
        skillsMenuItem.Text = "Навыки (skills)";
        // 
        // pluginsMenuItem
        // 
        pluginsMenuItem.ForeColor = Color.FromArgb(232, 237, 242);
        pluginsMenuItem.Name = "pluginsMenuItem";
        pluginsMenuItem.Size = new Size(180, 22);
        pluginsMenuItem.Text = "Плагины";
        // 
        // scriptsMenuItem
        // 
        scriptsMenuItem.ForeColor = Color.FromArgb(232, 237, 242);
        scriptsMenuItem.Name = "scriptsMenuItem";
        scriptsMenuItem.Size = new Size(180, 22);
        scriptsMenuItem.Text = "Скрипты (dotnet)";
        // 
        // modelMenu
        // 
        modelMenu.ForeColor = Color.FromArgb(232, 237, 242);
        modelMenu.Name = "modelMenu";
        modelMenu.Size = new Size(68, 19);
        modelMenu.Text = "МОДЕЛЬ";
        // 
        // modeMenu
        // 
        modeMenu.DropDownItems.AddRange(new ToolStripItem[] { modeExecuteItem, modePlanItem, modeAutoPermItem });
        modeMenu.ForeColor = Color.FromArgb(232, 237, 242);
        modeMenu.Name = "modeMenu";
        modeMenu.Size = new Size(63, 19);
        modeMenu.Text = "РЕЖИМ";
        // 
        // modeExecuteItem
        // 
        modeExecuteItem.ForeColor = Color.FromArgb(232, 237, 242);
        modeExecuteItem.Name = "modeExecuteItem";
        modeExecuteItem.Size = new Size(181, 22);
        modeExecuteItem.Tag = "execute";
        modeExecuteItem.Text = "EXECUTE";
        // 
        // modePlanItem
        // 
        modePlanItem.ForeColor = Color.FromArgb(232, 237, 242);
        modePlanItem.Name = "modePlanItem";
        modePlanItem.Size = new Size(181, 22);
        modePlanItem.Tag = "plan";
        modePlanItem.Text = "PLAN";
        // 
        // modeAutoPermItem
        // 
        modeAutoPermItem.CheckOnClick = true;
        modeAutoPermItem.ForeColor = Color.FromArgb(232, 237, 242);
        modeAutoPermItem.Name = "modeAutoPermItem";
        modeAutoPermItem.Size = new Size(181, 22);
        modeAutoPermItem.Text = "AUTO PERMISSIONS";
        // 
        // sidebar
        // 
        sidebar.BackColor = Color.FromArgb(21, 28, 38);
        sidebar.Controls.Add(sessionList);
        sidebar.Dock = DockStyle.Left;
        sidebar.Location = new Point(0, 27);
        sidebar.Name = "sidebar";
        sidebar.Padding = new Padding(8);
        sidebar.Size = new Size(230, 631);
        sidebar.TabIndex = 1;
        // 
        // sessionList
        // 
        sessionList.BackColor = Color.FromArgb(21, 28, 38);
        sessionList.BorderStyle = BorderStyle.None;
        sessionList.Columns.AddRange(new ColumnHeader[] { sessionColumn });
        sessionList.Dock = DockStyle.Fill;
        sessionList.ForeColor = Color.FromArgb(232, 237, 242);
        sessionList.FullRowSelect = true;
        sessionList.HeaderStyle = ColumnHeaderStyle.None;
        sessionList.Location = new Point(8, 8);
        sessionList.MultiSelect = false;
        sessionList.Name = "sessionList";
        sessionList.OwnerDraw = true;
        sessionList.ShowGroups = false;
        sessionList.Size = new Size(214, 615);
        sessionList.SmallImageList = rowHeightImages;
        sessionList.TabIndex = 0;
        sessionList.UseCompatibleStateImageBehavior = false;
        sessionList.View = View.Details;
        // 
        // sessionColumn
        // 
        sessionColumn.Text = "";
        sessionColumn.Width = 190;
        // 
        // rowHeightImages
        // 
        rowHeightImages.ColorDepth = ColorDepth.Depth8Bit;
        rowHeightImages.ImageSize = new Size(1, 26);
        rowHeightImages.TransparentColor = Color.Transparent;
        // 
        // content
        // 
        content.BackColor = Color.FromArgb(26, 33, 43);
        content.Controls.Add(outputPanel);
        content.Controls.Add(inputPanel);
        content.Dock = DockStyle.Fill;
        content.Location = new Point(230, 27);
        content.Name = "content";
        content.Size = new Size(850, 631);
        content.TabIndex = 2;
        // 
        // outputPanel
        // 
        outputPanel.BackColor = Color.FromArgb(26, 33, 43);
        outputPanel.Controls.Add(output);
        outputPanel.Dock = DockStyle.Fill;
        outputPanel.Location = new Point(0, 0);
        outputPanel.Name = "outputPanel";
        outputPanel.Padding = new Padding(12, 8, 12, 4);
        outputPanel.Size = new Size(850, 587);
        outputPanel.TabIndex = 0;
        // 
        // output
        // 
        output.BackColor = Color.FromArgb(26, 33, 43);
        output.Dock = DockStyle.Fill;
        output.Location = new Point(12, 8);
        output.Name = "output";
        output.Size = new Size(826, 575);
        output.TabIndex = 0;
        // 
        // inputPanel
        // 
        inputPanel.BackColor = Color.FromArgb(26, 33, 43);
        inputPanel.Controls.Add(inputHost);
        inputPanel.Controls.Add(sendButton);
        inputPanel.Dock = DockStyle.Bottom;
        inputPanel.Location = new Point(0, 587);
        inputPanel.Name = "inputPanel";
        inputPanel.Padding = new Padding(10, 6, 10, 8);
        inputPanel.Size = new Size(850, 44);
        inputPanel.TabIndex = 1;
        // 
        // inputHost
        // 
        inputHost.BackColor = Color.FromArgb(26, 33, 43);
        inputHost.Controls.Add(input);
        inputHost.Dock = DockStyle.Fill;
        inputHost.Location = new Point(10, 6);
        inputHost.Name = "inputHost";
        inputHost.Padding = new Padding(0, 0, 8, 0);
        inputHost.Size = new Size(720, 30);
        inputHost.TabIndex = 0;
        // 
        // input
        // 
        input.BackColor = Color.FromArgb(35, 45, 58);
        input.BorderStyle = BorderStyle.FixedSingle;
        input.Dock = DockStyle.Fill;
        input.Font = new Font("Segoe UI", 10F);
        input.ForeColor = Color.FromArgb(232, 237, 242);
        input.Location = new Point(0, 0);
        input.Name = "input";
        input.Size = new Size(712, 25);
        input.TabIndex = 0;
        // 
        // sendButton
        // 
        sendButton.BackColor = Color.FromArgb(63, 193, 176);
        sendButton.Dock = DockStyle.Right;
        sendButton.FlatStyle = FlatStyle.Flat;
        sendButton.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold, GraphicsUnit.Point, 204);
        sendButton.ForeColor = Color.FromArgb(15, 27, 34);
        sendButton.Location = new Point(730, 6);
        sendButton.Name = "sendButton";
        sendButton.Size = new Size(110, 30);
        sendButton.TabIndex = 1;
        sendButton.Text = "Отправить";
        sendButton.UseVisualStyleBackColor = false;
        // 
        // statusStrip1
        // 
        statusStrip1.BackColor = Color.FromArgb(21, 28, 38);
        statusStrip1.ForeColor = Color.FromArgb(133, 147, 165);
        statusStrip1.Items.AddRange(new ToolStripItem[] { toolStripStatusLabel2, folderLabel, toolStripStatusLabel1, usageLabel, stateLabel });
        statusStrip1.Location = new Point(0, 658);
        statusStrip1.Name = "statusStrip1";
        statusStrip1.Size = new Size(1080, 22);
        statusStrip1.SizingGrip = false;
        statusStrip1.TabIndex = 3;
        // 
        // toolStripStatusLabel2
        // 
        toolStripStatusLabel2.AutoSize = false;
        toolStripStatusLabel2.Name = "toolStripStatusLabel2";
        toolStripStatusLabel2.Size = new Size(16, 17);
        toolStripStatusLabel2.Text = "   ";
        // 
        // folderLabel
        // 
        folderLabel.ForeColor = Color.FromArgb(224, 224, 224);
        folderLabel.Name = "folderLabel";
        folderLabel.Size = new Size(0, 17);
        // 
        // toolStripStatusLabel1
        // 
        toolStripStatusLabel1.Name = "toolStripStatusLabel1";
        toolStripStatusLabel1.Size = new Size(16, 17);
        toolStripStatusLabel1.Text = "   ";
        // 
        // usageLabel
        // 
        usageLabel.ForeColor = Color.FromArgb(224, 224, 224);
        usageLabel.Name = "usageLabel";
        usageLabel.Size = new Size(988, 17);
        usageLabel.Spring = true;
        usageLabel.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // stateLabel
        // 
        stateLabel.ForeColor = Color.FromArgb(224, 224, 224);
        stateLabel.Name = "stateLabel";
        stateLabel.Size = new Size(45, 17);
        stateLabel.Text = "Готово";
        stateLabel.TextAlign = ContentAlignment.MiddleRight;
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(7F, 17F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.FromArgb(26, 33, 43);
        ClientSize = new Size(1080, 680);
        Controls.Add(content);
        Controls.Add(sidebar);
        Controls.Add(statusStrip1);
        Controls.Add(menu);
        Font = new Font("Segoe UI", 9.5F);
        ForeColor = Color.FromArgb(232, 237, 242);
        Icon = (Icon)resources.GetObject("$this.Icon");
        MainMenuStrip = menu;
        MinimumSize = new Size(720, 440);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "MyHarness";
        menu.ResumeLayout(false);
        menu.PerformLayout();
        sidebar.ResumeLayout(false);
        content.ResumeLayout(false);
        outputPanel.ResumeLayout(false);
        inputPanel.ResumeLayout(false);
        inputHost.ResumeLayout(false);
        inputHost.PerformLayout();
        statusStrip1.ResumeLayout(false);
        statusStrip1.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private System.Windows.Forms.MenuStrip menu;
    private System.Windows.Forms.ToolStripMenuItem modelMenu;
    private System.Windows.Forms.ToolStripMenuItem modeMenu;
    private System.Windows.Forms.ToolStripMenuItem modeExecuteItem;
    private System.Windows.Forms.ToolStripMenuItem modePlanItem;
    private System.Windows.Forms.ToolStripMenuItem modeAutoPermItem;
    private System.Windows.Forms.ToolStripMenuItem folderMenuItem;
    private System.Windows.Forms.ToolStripMenuItem resourcesMenu;
    private System.Windows.Forms.ToolStripMenuItem agentsMenuItem;
    private System.Windows.Forms.ToolStripMenuItem skillsMenuItem;
    private System.Windows.Forms.ToolStripMenuItem pluginsMenuItem;
    private System.Windows.Forms.ToolStripMenuItem scriptsMenuItem;
    private System.Windows.Forms.Panel sidebar;
    private System.Windows.Forms.ListView sessionList;
    private System.Windows.Forms.ColumnHeader sessionColumn;
    private System.Windows.Forms.ImageList rowHeightImages;
    private System.Windows.Forms.Panel content;
    private System.Windows.Forms.Panel outputPanel;
    private MarkdownViewer output;
    private System.Windows.Forms.Panel inputPanel;
    private System.Windows.Forms.Panel inputHost;
    private System.Windows.Forms.TextBox input;
    private System.Windows.Forms.Button sendButton;
    private System.Windows.Forms.StatusStrip statusStrip1;
    private System.Windows.Forms.ToolStripStatusLabel folderLabel;
    private System.Windows.Forms.ToolStripStatusLabel usageLabel;
    private System.Windows.Forms.ToolStripStatusLabel stateLabel;
    private ToolStripStatusLabel toolStripStatusLabel2;
    private ToolStripStatusLabel toolStripStatusLabel1;
}
