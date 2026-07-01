using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace KillSwitch;

/// <summary>
/// Central dark theme. Call <see cref="Apply(Form)"/> once per form (after its controls are built) to paint
/// the whole tree, wire flat/hover buttons, and darken the title bar. Palette is exposed for custom-drawn
/// controls (ModernTabControl, FastListView).
/// </summary>
public static class Theme
{
    // ---- palette ----
    public static readonly Color Bg         = Color.FromArgb(0x0F, 0x12, 0x16); // window
    public static readonly Color Surface    = Color.FromArgb(0x17, 0x1B, 0x21); // panels / cards / pages
    public static readonly Color Surface2   = Color.FromArgb(0x1E, 0x24, 0x2C); // inputs / lists
    public static readonly Color Hover      = Color.FromArgb(0x26, 0x2E, 0x38); // button hover
    public static readonly Color Selection  = Color.FromArgb(0x2B, 0x34, 0x40); // list selection
    public static readonly Color Border     = Color.FromArgb(0x2E, 0x37, 0x46);
    public static readonly Color HeaderBg   = Color.FromArgb(0x22, 0x2A, 0x35); // list column headers
    public static readonly Color Text       = Color.FromArgb(0xE7, 0xEC, 0xF2);
    public static readonly Color TextDim    = Color.FromArgb(0x97, 0xA2, 0xB0);
    public static readonly Color Accent     = Color.FromArgb(0x2E, 0xC2, 0x6B); // green (matches "online")
    public static readonly Color Danger     = Color.FromArgb(0xE5, 0x48, 0x4D);

    // ---- entry point ----
    public static void Apply(Form f)
    {
        f.BackColor = Bg;
        f.ForeColor = Text;
        Style(f);
        if (f.AcceptButton is Button ab) StylePrimary(ab); // the default action gets the accent
        DarkTitleBar(f);
        RoundWindowCorners(f);
        f.HandleCreated += (_, _) => EnableNativeDark(f);
        f.Shown += (_, _) => EnableNativeDark(f); // children have handles by now → dark scrollbars
        if (f.IsHandleCreated) EnableNativeDark(f);
    }

    /// <summary>Style a button as the primary/accent action.</summary>
    public static void StylePrimary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Accent, 0.15f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(Accent, 0.02f);
        Round(b, 8);
    }

    /// <summary>Style a control and everything under it. Safe to call on a UserControl too.</summary>
    public static void Style(Control root)
    {
        foreach (Control c in root.Controls) StyleOne(c);
    }

    private static void StyleOne(Control c)
    {
        switch (c)
        {
            case Button b: StyleButton(b); break;
            case CheckBox or RadioButton:
                c.BackColor = Color.Transparent;
                if (IsDefaultText(c.ForeColor)) c.ForeColor = Text;
                break;
            case Label l:
                l.BackColor = Color.Transparent;
                if (l is LinkLabel ll) ll.LinkColor = Accent;
                else if (IsDefaultText(l.ForeColor)) l.ForeColor = Text;
                else if (IsGrayText(l.ForeColor)) l.ForeColor = TextDim;
                break;
            case TextBoxBase tb:
                tb.BackColor = Surface2; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Surface2; cb.ForeColor = Text;
                break;
            case ListView lv:
                lv.BackColor = Surface2; lv.ForeColor = Text;
                if (lv is not FastListView) lv.BorderStyle = BorderStyle.None;
                break;
            case NumericUpDown nud:
                nud.BackColor = Surface2; nud.ForeColor = Text; nud.BorderStyle = BorderStyle.FixedSingle;
                break;
            case GroupBox gb:
                gb.BackColor = Surface; gb.ForeColor = Text;
                break;
            case TabControl:
                break; // ModernTabControl paints itself
            case Panel or FlowLayoutPanel or TableLayoutPanel or TabPage or UserControl:
                if (!IsCustomSurface(c.BackColor)) c.BackColor = Surface;
                if (IsDefaultText(c.ForeColor)) c.ForeColor = Text;
                break;
        }

        if (c.HasChildren)
            foreach (Control ch in c.Controls) StyleOne(ch);
    }

    private static void StyleButton(Button b)
    {
        bool neutral = b.BackColor.ToArgb() == SystemColors.Control.ToArgb() || b.BackColor.ToArgb() == SystemColors.ButtonFace.ToArgb();
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        if (neutral)
        {
            b.BackColor = Surface2;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Hover;
            b.FlatAppearance.MouseDownBackColor = Selection;
        }
        else
        {
            // an already-colored action button (accent / danger / orange): keep its color, add hover, drop border
            var baseColor = b.BackColor;
            if (b.ForeColor.ToArgb() == SystemColors.ControlText.ToArgb()) b.ForeColor = Color.White;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(baseColor, 0.18f);
            b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(baseColor, 0.02f);
        }
        Round(b, 8);
    }

    /// <summary>Give a control soft rounded corners (re-applied on resize).</summary>
    public static void Round(Control c, int radius)
    {
        void apply()
        {
            if (c.Width <= 1 || c.Height <= 1) return;
            int d = Math.Min(radius * 2, Math.Min(c.Width, c.Height));
            var r = c.ClientRectangle;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure();
            c.Region = new Region(path);
        }
        apply();
        c.Resize += (_, _) => apply();
    }

    // A panel that was deliberately given a strong color (e.g. a status banner) is left alone.
    private static bool IsCustomSurface(Color c)
    {
        if (c.ToArgb() == SystemColors.Control.ToArgb()) return false;
        if (c == Color.Transparent) return true;
        // treat near-white / near-gray defaults as "not custom" so we darken them
        return !(c.R > 200 && c.G > 200 && c.B > 200) && !(Math.Abs(c.R - c.G) < 10 && Math.Abs(c.G - c.B) < 10 && c.R > 180);
    }

    private static bool IsDefaultText(Color c) =>
        c.ToArgb() == SystemColors.ControlText.ToArgb() || c.ToArgb() == Color.Black.ToArgb() || c.ToArgb() == SystemColors.WindowText.ToArgb();

    private static bool IsGrayText(Color c) =>
        c == Color.DimGray || c == Color.Gray || c == Color.Silver || c.ToArgb() == SystemColors.GrayText.ToArgb();

    // ---- dark title bar (Win10 1809+/Win11) ----
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static void DarkTitleBar(Form f)
    {
        void set()
        {
            int on = 1;
            try { DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)); } catch { }
            try { DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int)); } catch { } // older build attr id
        }
        if (f.IsHandleCreated) set(); else f.HandleCreated += (_, _) => set();
    }

    // ---- rounded window corners (Win11: DWMWA_WINDOW_CORNER_PREFERENCE = 33, round = 2) ----
    private static void RoundWindowCorners(Form f)
    {
        void set() { int round = 2; try { DwmSetWindowAttribute(f.Handle, 33, ref round, sizeof(int)); } catch { } }
        if (f.IsHandleCreated) set(); else f.HandleCreated += (_, _) => set();
    }

    // ---- native dark mode for scrollbars / native controls (uxtheme) ----
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int mode); // 1 = AllowDark, 2 = ForceDark

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    private static bool _appModeSet;

    private static void EnableNativeDark(Control root)
    {
        try { if (!_appModeSet) { SetPreferredAppMode(2); _appModeSet = true; } } catch { }
        ThemeTree(root);
    }

    private static void ThemeTree(Control c)
    {
        try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        foreach (Control ch in c.Controls) ThemeTree(ch);
    }
}
