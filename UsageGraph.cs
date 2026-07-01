namespace KillSwitch;

/// <summary>Simple per-hour stacked bar chart (down + up) for an app's activity over time.</summary>
public sealed class UsageGraph : Control
{
    private List<HourPoint> _data = new();
    private string _title = "";

    public UsageGraph()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    public void SetData(List<HourPoint> data, string title)
    {
        _data = data ?? new List<HourPoint>();
        _title = title ?? "";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int w = Width, h = Height;
        int left = 6, right = 6, top = 22, bottom = 28;
        var plot = new Rectangle(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));

        using var titleFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var small = new Font("Segoe UI", 7.5f);
        var gray = Color.Gray;

        g.DrawString(_title, titleFont, Brushes.DimGray, 4, 3);

        using var inBrush = new SolidBrush(Color.FromArgb(70, 130, 200));   // download
        using var outBrush = new SolidBrush(Color.FromArgb(230, 140, 60));  // upload
        // legend
        g.FillRectangle(inBrush, w - 120, 5, 10, 10); g.DrawString("down", small, Brushes.Gray, w - 106, 3);
        g.FillRectangle(outBrush, w - 60, 5, 10, 10); g.DrawString("up", small, Brushes.Gray, w - 46, 3);

        if (_data.Count == 0) { g.DrawString("No data yet — select an app", small, Brushes.Gray, plot.Left, plot.Top); return; }

        long max = 1;
        foreach (var p in _data) if (p.Total > max) max = p.Total;

        using var axis = new Pen(Color.FromArgb(225, 225, 225));
        g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        g.DrawString(Fmt.Bytes(max) + "/h", small, Brushes.Gray, plot.Left, top - 16);

        int n = _data.Count;
        float bw = (float)plot.Width / n;
        for (int i = 0; i < n; i++)
        {
            var p = _data[i];
            float x = plot.Left + i * bw;
            float bwidth = Math.Max(1f, bw - (bw > 4 ? 1f : 0f));
            float inH = (float)((double)p.In / max * plot.Height);
            float outH = (float)((double)p.Out / max * plot.Height);
            float y = plot.Bottom;
            if (inH > 0) { g.FillRectangle(inBrush, x, y - inH, bwidth, inH); y -= inH; }
            if (outH > 0) { g.FillRectangle(outBrush, x, y - outH, bwidth, outH); }
        }

        g.DrawString(_data[0].Hour.ToString("M/d HH:mm"), small, Brushes.Gray, plot.Left, plot.Bottom + 5);
        var endStr = _data[n - 1].Hour.ToString("M/d HH:mm");
        var sz = g.MeasureString(endStr, small);
        g.DrawString(endStr, small, Brushes.Gray, plot.Right - sz.Width, plot.Bottom + 5);
    }
}
