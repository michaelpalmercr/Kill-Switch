using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace KillSwitch;

/// <summary>Builds the tray icons at runtime so the app needs no .ico asset files.</summary>
public static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static readonly Color Online = Color.FromArgb(46, 184, 92);   // green
    public static readonly Color Blocked = Color.FromArgb(220, 60, 55);  // red

    public static Icon Make(bool blocked) => MakeDot(blocked ? Blocked : Online);

    private static Icon MakeDot(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Filled status dot.
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 3, 3, 26, 26);

            // Subtle rim for contrast on light/dark taskbars.
            using var rim = new Pen(Color.FromArgb(70, 0, 0, 0), 2f);
            g.DrawEllipse(rim, 3, 3, 26, 26);

            // Tiny highlight.
            using var hi = new SolidBrush(Color.FromArgb(80, 255, 255, 255));
            g.FillEllipse(hi, 9, 8, 9, 7);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone(); // detach from the native handle so we can free it
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}
