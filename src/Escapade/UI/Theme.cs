using Escapade.Core;
using static Escapade.Interop.NativeMethods;

namespace Escapade.UI;

/// <summary>
/// Escapade's colours, by role. The light theme is Windows' own: each role maps to the system
/// colour it has always used. The dark theme is Escapade's own scheme: near-black backgrounds,
/// grey chrome and edges, and orange for the accent (active pane, active tab, selection, hover).
/// </summary>
internal static class Theme
{
    // The dark scheme.
    private static readonly Color DarkGutter = Color.FromArgb(10, 10, 10);
    private static readonly Color DarkSurface = Color.FromArgb(30, 30, 30);
    private static readonly Color DarkContent = Color.FromArgb(18, 18, 18);
    private static readonly Color DarkTabRow = Color.FromArgb(22, 22, 22);
    private static readonly Color DarkStatusBar = Color.FromArgb(26, 26, 26);
    private static readonly Color DarkFrame = Color.FromArgb(60, 60, 60);
    private static readonly Color DarkDivider = Color.FromArgb(44, 44, 44);
    private static readonly Color DarkText = Color.FromArgb(232, 232, 232);
    private static readonly Color DarkDimText = Color.FromArgb(140, 140, 140);
    private static readonly Color Orange = Color.FromArgb(232, 163, 61); // warm amber, as in VS Code's warning tabs

    /// <summary>
    /// Sets the colour mode for the whole app. Call once, before any window is created: WinForms
    /// can't switch an app between light and dark once it has windows, which is why changing the
    /// theme needs a restart.
    /// </summary>
    public static void Apply(ThemeChoice choice)
    {
        Application.SetColorMode(choice switch
        {
            ThemeChoice.Light => SystemColorMode.Classic,
            ThemeChoice.Dark => SystemColorMode.Dark,
            _ => SystemColorMode.System,
        });
        if (IsDark)
            ToolStripManager.Renderer = new DarkToolStripRenderer();
    }

    /// <summary>Whether the app is actually showing dark (chosen outright, or following a dark Windows).</summary>
    public static bool IsDark =>
        Application.ColorMode == SystemColorMode.Dark
        || (Application.ColorMode == SystemColorMode.System && Application.SystemColorMode == SystemColorMode.Dark);

    /// <summary>How toolbars should render: the native look in light mode, Escapade's dark renderer otherwise.</summary>
    public static ToolStripRenderMode ToolStripMode => IsDark ? ToolStripRenderMode.ManagerRenderMode : ToolStripRenderMode.System;

    // ------------------------------------------------------------------
    // Roles
    // ------------------------------------------------------------------

    /// <summary>Chrome: toolbars, pane backgrounds, dialogs.</summary>
    public static Color Surface => IsDark ? DarkSurface : SystemColors.Control;

    /// <summary>Where content lives: file lists, the path bar, the active tab, text boxes.</summary>
    public static Color Content => IsDark ? DarkContent : SystemColors.Window;

    public static Color Text => IsDark ? DarkText : SystemColors.WindowText;

    /// <summary>Text on chrome: inactive tabs, the status bar, toolbar glyphs.</summary>
    public static Color ChromeText => IsDark ? Color.FromArgb(200, 200, 200) : SystemColors.ControlText;

    /// <summary>Secondary text: hidden files, paths in the palette, inactive tabs' close buttons.</summary>
    public static Color DimText => IsDark ? DarkDimText : SystemColors.GrayText;

    /// <summary>The accent: the active pane's frame and tab, matched letters in the palette.</summary>
    public static Color Accent => IsDark ? Orange : SystemColors.Highlight;

    /// <summary>A selected row (palette, and the file list in dark mode).</summary>
    public static Color SelectionBack => IsDark ? Blend(DarkContent, Orange, 0.38) : SystemColors.Highlight;

    /// <summary>A selected row in a list that doesn't have focus: the same, quieter.</summary>
    public static Color InactiveSelectionBack => IsDark ? Blend(DarkContent, Orange, 0.18) : SystemColors.ControlLight;

    public static Color SelectionText => IsDark ? Color.White : SystemColors.HighlightText;

    /// <summary>Under the mouse: toolbar buttons, tabs, the nav buttons.</summary>
    public static Color Hover => IsDark ? Blend(DarkSurface, Orange, 0.22) : Blend(TabRow, SystemColors.Window, 0.5);

    /// <summary>The window background between the panes: darker, so each pane reads as a card on it.</summary>
    public static Color Gutter => IsDark ? DarkGutter : Blend(SystemColors.Control, SystemColors.ControlDark, 0.4);

    /// <summary>A pane's 1px frame.</summary>
    public static Color Frame => IsDark ? DarkFrame : SystemColors.ControlDark;

    /// <summary>The active pane's frame.</summary>
    public static Color ActiveFrame => Accent;

    /// <summary>Thin lines between stacked parts: toolbar and panes, path bar and list, list and status bar.</summary>
    public static Color Divider => IsDark ? DarkDivider : Blend(SystemColors.Control, SystemColors.ControlDark, 0.7);

    /// <summary>Behind the tabs: the active tab, in the content colour, stands out against it.</summary>
    public static Color TabRow => IsDark ? DarkTabRow : Blend(SystemColors.Control, SystemColors.ControlDark, 0.12);

    /// <summary>The status bar, a touch darker than the toolbar so the list's bottom edge is clear.</summary>
    public static Color StatusBar => IsDark ? DarkStatusBar : Blend(SystemColors.Control, SystemColors.ControlDark, 0.18);

    /// <summary>Error text: the light theme's dark red is unreadable on a dark background.</summary>
    public static Color ErrorText => IsDark ? Color.FromArgb(255, 112, 112) : Color.Firebrick;

    /// <summary>The "Filter: …" badge in the status bar.</summary>
    public static Color FilterBack => IsDark ? Blend(DarkContent, Orange, 0.3) : Color.FromArgb(255, 241, 178);

    public static Color FilterText => IsDark ? Color.FromArgb(255, 222, 186) : Color.Black;

    public static int GutterWidth => 6;

    public static Color Blend(Color a, Color b, double amount) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * amount),
        (int)Math.Round(a.G + (b.G - a.G) * amount),
        (int)Math.Round(a.B + (b.B - a.B) * amount));

    /// <summary>Gives a window a dark title bar in dark mode (Windows draws the title bar, not WinForms).</summary>
    public static void ApplyTitleBar(Form form)
    {
        if (!IsDark || !form.IsHandleCreated)
            return;
        int on = 1;
        DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    // ------------------------------------------------------------------
    // Toolbars and menus in the dark scheme
    // ------------------------------------------------------------------

    /// <summary>The professional renderer, recoloured: grey bars and menus, orange hover and press.</summary>
    private sealed class DarkToolStripRenderer() : ToolStripProfessionalRenderer(new DarkColorTable())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? DarkText : DarkDimText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled != false ? DarkText : DarkDimText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A plain orange tick, rather than the stock check image drawn for light menus.
            var r = e.ImageRectangle;
            using var pen = new Pen(Orange, Math.Max(2, r.Height / 8f));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, [
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f),
            ]);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Hot = Blend(DarkSurface, Orange, 0.25);
        private static readonly Color Pressed = Blend(DarkSurface, Orange, 0.45);

        public DarkColorTable() => UseSystemColors = false;

        public override Color ToolStripGradientBegin => DarkSurface;
        public override Color ToolStripGradientMiddle => DarkSurface;
        public override Color ToolStripGradientEnd => DarkSurface;
        public override Color ToolStripBorder => DarkSurface;
        public override Color ToolStripContentPanelGradientBegin => DarkSurface;
        public override Color ToolStripContentPanelGradientEnd => DarkSurface;
        public override Color ToolStripDropDownBackground => DarkSurface;
        public override Color MenuStripGradientBegin => DarkSurface;
        public override Color MenuStripGradientEnd => DarkSurface;
        public override Color MenuBorder => DarkFrame;
        public override Color ImageMarginGradientBegin => DarkSurface;
        public override Color ImageMarginGradientMiddle => DarkSurface;
        public override Color ImageMarginGradientEnd => DarkSurface;
        public override Color SeparatorDark => DarkFrame;
        public override Color SeparatorLight => DarkSurface;
        public override Color GripDark => DarkFrame;
        public override Color GripLight => DarkSurface;

        public override Color MenuItemSelected => Hot;
        public override Color MenuItemSelectedGradientBegin => Hot;
        public override Color MenuItemSelectedGradientEnd => Hot;
        public override Color MenuItemBorder => Orange;
        public override Color MenuItemPressedGradientBegin => Pressed;
        public override Color MenuItemPressedGradientMiddle => Pressed;
        public override Color MenuItemPressedGradientEnd => Pressed;

        public override Color ButtonSelectedHighlight => Hot;
        public override Color ButtonSelectedHighlightBorder => Orange;
        public override Color ButtonSelectedGradientBegin => Hot;
        public override Color ButtonSelectedGradientMiddle => Hot;
        public override Color ButtonSelectedGradientEnd => Hot;
        public override Color ButtonSelectedBorder => Orange;
        public override Color ButtonPressedHighlight => Pressed;
        public override Color ButtonPressedHighlightBorder => Orange;
        public override Color ButtonPressedGradientBegin => Pressed;
        public override Color ButtonPressedGradientMiddle => Pressed;
        public override Color ButtonPressedGradientEnd => Pressed;
        public override Color ButtonPressedBorder => Orange;
        public override Color ButtonCheckedHighlight => Pressed;
        public override Color ButtonCheckedHighlightBorder => Orange;
        public override Color ButtonCheckedGradientBegin => Pressed;
        public override Color ButtonCheckedGradientMiddle => Pressed;
        public override Color ButtonCheckedGradientEnd => Pressed;

        public override Color CheckBackground => DarkSurface;
        public override Color CheckSelectedBackground => Hot;
        public override Color CheckPressedBackground => Pressed;

        public override Color OverflowButtonGradientBegin => DarkSurface;
        public override Color OverflowButtonGradientMiddle => DarkSurface;
        public override Color OverflowButtonGradientEnd => DarkSurface;
        public override Color StatusStripGradientBegin => DarkSurface;
        public override Color StatusStripGradientEnd => DarkSurface;
    }
}
