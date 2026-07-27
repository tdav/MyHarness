using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;

// Terminal.Gui's Attribute (a foreground/background/style triple) collides with
// System.Attribute pulled in by ImplicitUsings; in the UI layer the drawing one wins.
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace HarnessCli.UI;

/// <summary>
/// Terminal port of the MyHarness dark palette (dark navy + teal accent) and of the chat
/// output colors. The 24-bit RGB values are the exact ones the WinForms app uses; on a
/// terminal without true-color support Terminal.Gui maps them to the closest 16-color
/// approximation on its own, so the same code works everywhere.
/// The named <see cref="Scheme"/>s are registered once in <see cref="Register"/> and then
/// referenced by views through <see cref="Terminal.Gui.ViewBase.View.SchemeName"/>.
/// </summary>
internal static class Theme
{
    // ---- Palette (mirrors UI/Theme.cs of the WinForms app) ----

    public static readonly Color Background = new(26, 33, 43);
    public static readonly Color Sidebar = new(21, 28, 38);
    public static readonly Color Surface = new(35, 45, 58);
    public static readonly Color SurfaceHover = new(44, 56, 71);
    public static readonly Color Border = new(52, 64, 80);
    public static readonly Color TextPrimary = new(232, 237, 242);
    public static readonly Color TextMuted = new(133, 147, 165);
    public static readonly Color Accent = new(63, 193, 176);
    public static readonly Color AccentText = new(15, 27, 34);

    // ---- Chat output colors (mirrors MarkdownViewer's CSS classes) ----

    public static readonly Color ChatUser = new(111, 183, 232);
    public static readonly Color ChatReasoning = new(177, 140, 217);
    public static readonly Color ChatTool = new(224, 179, 106);
    public static readonly Color ChatError = new(247, 109, 109);
    public static readonly Color ChatInfo = new(133, 147, 165);

    // ---- Scheme names used by the views ----

    public const string SchemeApp = "HarnessApp";
    public const string SchemeSidebar = "HarnessSidebar";
    public const string SchemeBar = "HarnessBar";
    public const string SchemeInput = "HarnessInput";
    public const string SchemeAccentButton = "HarnessAccent";
    public const string SchemeTranscript = "HarnessTranscript";

    // ---- Attributes for the transcript renderer ----

    /// <summary>Assistant answer text (Markdown body) on the chat background.</summary>
    public static Attribute Markdown { get; } = new(TextPrimary, Background);

    public static Attribute User { get; } = new(ChatUser, Background, TextStyle.Bold);

    public static Attribute Reasoning { get; } = new(ChatReasoning, Background, TextStyle.Italic);

    public static Attribute Tool { get; } = new(ChatTool, Background);

    public static Attribute Error { get; } = new(ChatError, Background);

    public static Attribute Info { get; } = new(ChatInfo, Background);

    // Markdown element styles — the terminal equivalents of the WebView2 stylesheet.
    public static Attribute Heading { get; } = new(TextPrimary, Background, TextStyle.Bold | TextStyle.Underline);

    public static Attribute Strong { get; } = new(TextPrimary, Background, TextStyle.Bold);

    public static Attribute Emphasis { get; } = new(TextPrimary, Background, TextStyle.Italic);

    public static Attribute Strikethrough { get; } = new(TextMuted, Background, TextStyle.Strikethrough);

    /// <summary>Inline code and fenced code blocks: raised surface, same as the .md code CSS.</summary>
    public static Attribute Code { get; } = new(new Color(160, 220, 200), Surface);

    public static Attribute Link { get; } = new(Accent, Background, TextStyle.Underline);

    public static Attribute Quote { get; } = new(TextMuted, Background, TextStyle.Italic);

    public static Attribute Rule { get; } = new(Border, Background);

    public static Attribute TableHeader { get; } = new(TextPrimary, Surface, TextStyle.Bold);

    public static Attribute TableBorder { get; } = new(Border, Background);

    /// <summary>Bullet/number markers of list items.</summary>
    public static Attribute ListMarker { get; } = new(Accent, Background);

    /// <summary>
    /// Registers the named schemes with Terminal.Gui. Must be called after
    /// <c>Application.Init</c> (the scheme manager needs the driver's color support).
    /// </summary>
    public static void Register()
    {
        AddScheme(SchemeApp, TextPrimary, Background, focusFg: TextPrimary, focusBg: SurfaceHover);
        AddScheme(SchemeSidebar, TextMuted, Sidebar, focusFg: TextPrimary, focusBg: Surface);
        AddScheme(SchemeBar, TextMuted, Sidebar, focusFg: AccentText, focusBg: Accent);
        AddScheme(SchemeInput, TextPrimary, Surface, focusFg: TextPrimary, focusBg: Surface);
        AddScheme(SchemeAccentButton, AccentText, Accent, focusFg: AccentText, focusBg: new Color(94, 208, 193));
        AddScheme(SchemeTranscript, TextPrimary, Background, focusFg: TextPrimary, focusBg: Background);
    }

    private static void AddScheme(string name, Color fg, Color bg, Color focusFg, Color focusBg)
    {
        var scheme = new Scheme
        {
            Normal = new Attribute(fg, bg),
            Focus = new Attribute(focusFg, focusBg),
            HotNormal = new Attribute(Accent, bg),
            HotFocus = new Attribute(Accent, focusBg),
            Active = new Attribute(TextPrimary, SurfaceHover),
            HotActive = new Attribute(Accent, SurfaceHover),
            Highlight = new Attribute(AccentText, Accent),
            Editable = new Attribute(TextPrimary, Surface),
            ReadOnly = new Attribute(TextMuted, bg),
            Disabled = new Attribute(TextMuted, bg),
        };

        SchemeManager.AddScheme(name, scheme);
    }
}
