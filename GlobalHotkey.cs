using System.Runtime.InteropServices;

namespace KillSwitch;

/// <summary>Registers a system-wide hotkey via a hidden message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    // fsModifiers flags
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly int _hotkeyId;
    private readonly MessageWindow _window;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkey(int id = 0xB00B)
    {
        _hotkeyId = id;
        _window = new MessageWindow(id);
        _window.HotkeyPressed += () => Pressed?.Invoke();
    }

    /// <summary>(Re)register the hotkey. <paramref name="modifiers"/> and <paramref name="key"/> are WinForms Keys values.</summary>
    public bool Register(Keys modifiers, Keys key)
    {
        Unregister();
        if (key == Keys.None) return false;

        uint mod = MOD_NOREPEAT;
        if (modifiers.HasFlag(Keys.Control)) mod |= MOD_CONTROL;
        if (modifiers.HasFlag(Keys.Alt)) mod |= MOD_ALT;
        if (modifiers.HasFlag(Keys.Shift)) mod |= MOD_SHIFT;
        if (modifiers.HasFlag(Keys.LWin) || modifiers.HasFlag(Keys.RWin)) mod |= MOD_WIN;

        _registered = RegisterHotKey(_window.Handle, _hotkeyId, mod, (uint)(key & Keys.KeyCode));
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_window.Handle, _hotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        _window.DestroyHandle();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly int _id;
        public event Action? HotkeyPressed;

        public MessageWindow(int id) { _id = id; CreateHandle(new CreateParams()); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == _id)
                HotkeyPressed?.Invoke();
            base.WndProc(ref m);
        }
    }

    /// <summary>Human-readable label, e.g. "Ctrl+Alt+K".</summary>
    public static string Describe(Keys modifiers, Keys key)
    {
        if (key == Keys.None) return "(none)";
        var parts = new List<string>();
        if (modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(Keys.LWin) || modifiers.HasFlag(Keys.RWin)) parts.Add("Win");
        parts.Add((key & Keys.KeyCode).ToString());
        return string.Join("+", parts);
    }
}
