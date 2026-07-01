namespace KillSwitch;

/// <summary>
/// A dark, flat TabControl: no 3D borders, tabs painted with the app palette, an accent underline on the
/// selected tab. Paints its own surface so the light default strip never shows through.
/// </summary>
public sealed class ModernTabControl : TabControl
{
    public ModernTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(116, 36);
        Alignment = TabAlignment.Top;
        Font = new Font("Segoe UI", 9.5f);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);

        // Fill the body area (behind the selected page) so edges read as one surface.
        var body = new Rectangle(0, ItemSize.Height, Width, Height - ItemSize.Height);
        using (var bb = new SolidBrush(Theme.Surface)) g.FillRectangle(bb, body);

        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r = GetTabRect(i);
            bool sel = i == SelectedIndex;

            using (var b = new SolidBrush(sel ? Theme.Surface : Theme.Bg)) g.FillRectangle(b, r);

            TextRenderer.DrawText(g, TabPages[i].Text, Font, r, sel ? Theme.Text : Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (sel)
                using (var a = new SolidBrush(Theme.Accent))
                    g.FillRectangle(a, r.X + 10, r.Bottom - 3, r.Width - 20, 3);
        }
    }
}
