using System.Runtime.InteropServices;

namespace MyHarnessWin.UI;

/// <summary>
/// Dark-mode palette and styling helpers shared by all Test05-Win windows.
/// </summary>
internal static class Theme
{
    // Dark-navy palette with a teal accent (dashboard-style mockup).
    public static readonly Color Background = Color.FromArgb(26, 33, 43);
    public static readonly Color Sidebar = Color.FromArgb(21, 28, 38);
    public static readonly Color Surface = Color.FromArgb(35, 45, 58);
    public static readonly Color SurfaceHover = Color.FromArgb(44, 56, 71);
    public static readonly Color Border = Color.FromArgb(52, 64, 80);
    public static readonly Color TextPrimary = Color.FromArgb(232, 237, 242);
    public static readonly Color TextMuted = Color.FromArgb(133, 147, 165);
    public static readonly Color Accent = Color.FromArgb(63, 193, 176);
    public static readonly Color AccentText = Color.FromArgb(15, 27, 34);

    public static readonly Color ChatUser = Color.FromArgb(111, 183, 232);
    public static readonly Color ChatReasoning = Color.FromArgb(177, 140, 217);
    public static readonly Color ChatTool = Color.FromArgb(224, 179, 106);
    public static readonly Color ChatError = Color.FromArgb(247, 109, 109);
    public static readonly Color ChatInfo = Color.FromArgb(133, 147, 165);

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>Switches the native title bar of the form to dark mode (Windows 10 1809+).</summary>
    public static void ApplyDarkTitleBar(Form form)
    {
        int enabled = 1;
        _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    public static void StyleFlatButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = SurfaceHover;
        button.FlatAppearance.MouseDownBackColor = Border;
        button.BackColor = Surface;
        button.ForeColor = TextPrimary;
        button.UseVisualStyleBackColor = false;
    }

    public static void StyleAccentButton(Button button)
    {
        StyleFlatButton(button);
        button.BackColor = Accent;
        button.ForeColor = AccentText;
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(94, 208, 193);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(47, 166, 150);
    }

    public static void StyleCombo(ComboBox combo)
    {
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Surface;
        combo.ForeColor = TextPrimary;
    }

    public static void StyleMenu(MenuStrip menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        menu.BackColor = Sidebar;
        menu.ForeColor = TextPrimary;
    }

    public static void StyleStatusStrip(StatusStrip strip)
    {
        strip.Renderer = new DarkMenuRenderer();
        strip.BackColor = Sidebar;
        strip.ForeColor = TextMuted;
        strip.SizingGrip = false;
    }

    /// <summary>
    /// Professional renderer with a dark palette; keeps item text readable both in the
    /// enabled and disabled state (default rendering falls back to light system colors).
    /// </summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkMenuColors())
        {
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Enabled items keep their own ForeColor (menu items are bright, status
            // labels muted); the disabled state is forced to muted instead of the
            // light system gray.
            e.TextColor = e.Item.Enabled ? e.Item.ForeColor : TextMuted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TextPrimary;
            base.OnRenderArrow(e);
        }
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Sidebar;

        public override Color MenuStripGradientEnd => Sidebar;

        public override Color StatusStripGradientBegin => Sidebar;

        public override Color StatusStripGradientEnd => Sidebar;

        public override Color MenuItemSelected => SurfaceHover;

        public override Color MenuItemSelectedGradientBegin => SurfaceHover;

        public override Color MenuItemSelectedGradientEnd => SurfaceHover;

        public override Color MenuItemPressedGradientBegin => Surface;

        public override Color MenuItemPressedGradientEnd => Surface;

        public override Color MenuItemBorder => Border;

        public override Color MenuBorder => Border;

        public override Color ToolStripDropDownBackground => Surface;

        public override Color ImageMarginGradientBegin => Surface;

        public override Color ImageMarginGradientMiddle => Surface;

        public override Color ImageMarginGradientEnd => Surface;

        public override Color SeparatorDark => Border;

        public override Color SeparatorLight => Border;

        public override Color CheckBackground => Accent;

        public override Color CheckSelectedBackground => Accent;

        public override Color CheckPressedBackground => Accent;
    }
}
