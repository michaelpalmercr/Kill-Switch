namespace KillSwitch;

/// <summary>
/// The application's lifetime object: owns the tray icon, settings, network controller,
/// scheduler and global hotkey, and brokers all state changes.
/// </summary>
public sealed class KillSwitchContext : ApplicationContext
{
    public AppSettings Settings { get; }
    public NetworkController Net { get; }

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Scheduler _scheduler;
    private readonly GlobalHotkey _hotkey;
    private readonly GlobalHotkey _saveLoginHotkey;

    /// <summary>Raised when the "save login" hotkey (Ctrl+Alt+L) is pressed; carries the foreground window title captured before we steal focus.</summary>
    public event Action<string>? SaveLoginRequested;

    private MainForm? _form;
    private Icon? _currentIcon;

    private readonly EventWaitHandle _showEvent;
    private readonly Control _marshal = new();
    private volatile bool _disposed;

    /// <summary>Raised whenever the blocked state changes so open UI can refresh.</summary>
    public event Action? StateChanged;

    public bool IsBlocked => Net.IsBlocked();

    public KillSwitchContext(bool forceShow = false)
    {
        Settings = AppSettings.Load();
        Net = new NetworkController(Settings);

        // Marshaling target + cross-process "show window" signal (a 2nd launch surfaces this window).
        _ = _marshal.Handle; // realize the handle on the UI thread
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        new Thread(ShowListener) { IsBackground = true, Name = "show-listener" }.Start();

        _menu = new ContextMenuStrip();
        _toggleItem = new ToolStripMenuItem("Cut internet now", null, (_, _) => ToggleBlock());
        var openItem = new ToolStripMenuItem("Open KillSwitch", null, (_, _) => ShowMainWindow()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { CheckOnClick = false };
        _startupItem.Checked = Startup.IsEnabled();

        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(openItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };

        _scheduler = new Scheduler(Settings);
        _scheduler.TransitionRequested += blocked => ApplyState(blocked, "Scheduled");

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += ToggleBlock;
        ApplyHotkey();

        // Second, fixed hotkey: Ctrl+Alt+L quick-saves the current login into the encrypted vault.
        // We capture the foreground window's title FIRST (before showing our window), then hand it to the UI.
        _saveLoginHotkey = new GlobalHotkey(0xB00C);
        _saveLoginHotkey.Pressed += OnSaveLoginHotkey;
        _saveLoginHotkey.Register(Keys.Control | Keys.Alt, Keys.L);

        // Keep the saved StartWithWindows flag honest with the actual scheduled task.
        Settings.StartWithWindows = _startupItem.Checked;

        RefreshIcon();
        _scheduler.Start();
        StartAppScheduler();
        if (Settings.AllowlistMode) StartAllowWatcher();
        if (Settings.MitmEnabled) { try { Mitm.Start(Settings.MitmPort); } catch { } }

        if (forceShow || !Settings.StartMinimized)
            ShowMainWindow();
    }

    private void ShowListener()
    {
        while (!_disposed)
        {
            try { _showEvent.WaitOne(); } catch { break; }
            if (_disposed) break;
            try { _marshal.BeginInvoke((Action)ShowMainWindow); } catch { }
        }
    }

    // ---------------- state ----------------

    public void ToggleBlock() => ApplyState(!IsBlocked, "Manual");

    /// <summary>Apply the desired blocked state and surface the result.</summary>
    public void ApplyState(bool blocked, string source)
    {
        if (blocked == IsBlocked) { RefreshIcon(); return; }

        string? err = blocked ? Net.Block() : Net.Restore();
        RefreshIcon();
        StateChanged?.Invoke();

        if (err != null)
        {
            Notify("KillSwitch — error", err, warning: true);
            return;
        }

        if (blocked)
        {
            string extra = "";
            if (Settings.Mechanism == KillMechanism.Firewall && !NetworkController.IsFirewallOn())
                extra = "\n⚠ Windows Firewall is OFF — block may not be enforced.";
            Notify("Internet cut", $"{source}: all apps are now offline.{extra}");
        }
        else
        {
            Notify("Internet restored", $"{source}: you're back online.");
        }
    }

    public void ApplyMechanismChanged()
    {
        Settings.Save();
        RefreshIcon();
        StateChanged?.Invoke();
    }

    public void ApplyScheduleChanged()
    {
        Settings.Save();
        _scheduler.Reset();
    }

    public void ApplyHotkey()
    {
        if (Settings.HotkeyEnabled)
            _hotkey.Register((Keys)Settings.HotkeyModifiers, (Keys)Settings.HotkeyKey);
        else
            _hotkey.Unregister();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private void OnSaveLoginHotkey()
    {
        // Capture the active window's title BEFORE we surface our own window and steal focus.
        string title = "";
        try
        {
            var h = GetForegroundWindow();
            if (h != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(512);
                if (GetWindowText(h, sb, sb.Capacity) > 0) title = sb.ToString();
            }
        }
        catch { }
        ShowMainWindow();
        try { SaveLoginRequested?.Invoke(title); } catch { }
    }

    public string? ApplyStartup(bool enabled)
    {
        var err = Startup.SetEnabled(enabled);
        if (err == null)
        {
            Settings.StartWithWindows = enabled;
            _startupItem.Checked = enabled;
            Settings.Save();
        }
        return err;
    }

    private void ToggleStartup()
    {
        var err = ApplyStartup(!_startupItem.Checked);
        if (err != null) Notify("KillSwitch — startup", err, warning: true);
        StateChanged?.Invoke();
    }

    // ---------------- per-app rules ----------------

    public ISet<string> BlockedAppPaths() =>
        Settings.AppRules.Select(r => r.Path)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsAppBlocked(string path) => NetworkController.IsAppBlocked(path);

    public string? BlockApp(string path, string name)
    {
        var err = NetworkController.BlockApp(path);
        if (err != null) return err;
        var rule = Settings.AppRules.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (rule == null) { rule = new AppRule { Path = path, Name = name }; Settings.AppRules.Add(rule); }
        rule.BlockedNow = true;
        rule.Name = name;
        Settings.Save();
        Notify("App blocked", name + " can no longer reach the internet.");
        return null;
    }

    public string? UnblockApp(string path)
    {
        var err = NetworkController.UnblockApp(path);
        Settings.AppRules.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        StateChanged?.Invoke();
        return err;
    }

    // ---------------- allowlist (default-deny) ----------------

    private System.Windows.Forms.Timer? _allowWatch;
    private readonly HashSet<string> _sessionDenied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Path, string Name)> _promptQueue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private bool _promptShowing;

    public bool AllowlistMode => Settings.AllowlistMode;

    public ISet<string> AllowedAppPaths() =>
        Settings.AllowedApps.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? SetAllowlistMode(bool on)
    {
        if (on == Settings.AllowlistMode) return null;
        var err = on ? Net.EnterAllowlist() : Net.ExitAllowlist();
        if (err != null) return err;
        Settings.AllowlistMode = on;
        Settings.Save();
        RefreshIcon();
        StateChanged?.Invoke();
        if (on) StartAllowWatcher(); else StopAllowWatcher();
        Notify(on ? "Allowlist mode ON" : "Allowlist mode OFF",
               on ? "Only approved apps can reach the internet. You'll be asked about new ones."
                  : "All apps may connect again (except any you've blocked).");
        return null;
    }

    public string? AllowApp(string path, string name)
    {
        var rule = Settings.AllowedApps.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        if (rule == null) { rule = new AppRule { Path = path, Name = name }; Settings.AllowedApps.Add(rule); }
        rule.Name = name;
        if (Settings.AllowlistMode) NetworkController.AllowApp(path);
        Settings.Save();
        StateChanged?.Invoke();
        return null;
    }

    public void RemoveAllowedApp(string path)
    {
        Settings.AllowedApps.RemoveAll(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        NetworkController.DisallowApp(path);
        Settings.Save();
        StateChanged?.Invoke();
    }

    // ---------------- safe apps (exempt from the kill switch) ----------------

    public ISet<string> SafeAppPaths() =>
        Settings.SafeApps.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void AddSafeApp(string path, string name)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!Settings.SafeApps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            Settings.SafeApps.Add(new AppRule { Path = path, Name = name });
        Settings.Save();
        if (Settings.AllowlistMode) NetworkController.AllowApp(path);
        ReapplyIfGloballyBlocked();
        Notify("Marked safe", name + " will stay online when you cut the internet.");
        StateChanged?.Invoke();
    }

    public void RemoveSafeApp(string path)
    {
        Settings.SafeApps.RemoveAll(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        if (Settings.AllowlistMode && !Settings.AllowedApps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            NetworkController.DisallowApp(path);
        ReapplyIfGloballyBlocked();
        StateChanged?.Invoke();
    }

    // ---------------- block by destination IP ----------------

    public ISet<string> BlockedIpSet() =>
        Settings.BlockedIps.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? BlockIp(string ip)
    {
        var err = NetworkController.BlockIp(ip);
        if (err != null) return err;
        if (!Settings.BlockedIps.Contains(ip)) Settings.BlockedIps.Add(ip);
        Settings.Save();
        StateChanged?.Invoke();
        return null;
    }

    public int BlockIps(IEnumerable<string> ips)
    {
        int n = 0;
        foreach (var ip in ips.Distinct())
            if (NetworkController.BlockIp(ip) == null)
            {
                if (!Settings.BlockedIps.Contains(ip)) Settings.BlockedIps.Add(ip);
                n++;
            }
        Settings.Save();
        StateChanged?.Invoke();
        return n;
    }

    public void UnblockIp(string ip)
    {
        NetworkController.UnblockIp(ip);
        Settings.BlockedIps.RemoveAll(x => string.Equals(x, ip, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        StateChanged?.Invoke();
    }

    // ---------------- execution lock (force-close + block from running) ----------------

    public ISet<string> LockedAppPaths() =>
        Settings.LockedApps.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? LockApp(string path, string name)
    {
        var err = ProcessLock.Lock(path);
        if (err != null) return err;
        if (!Settings.LockedApps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            Settings.LockedApps.Add(new AppRule { Path = path, Name = name });
        Settings.Save();
        Notify("App locked", name + " was force-closed and can't run until you unlock it.");
        StateChanged?.Invoke();
        return null;
    }

    public void UnlockApp(string path)
    {
        ProcessLock.Unlock(path);
        Settings.LockedApps.RemoveAll(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        StateChanged?.Invoke();
    }

    // ---------------- per-app schedules ----------------

    private System.Windows.Forms.Timer? _appSched;
    private readonly Dictionary<string, bool> _appSchedLast = new(StringComparer.OrdinalIgnoreCase);

    public AppRule? GetScheduledApp(string path) =>
        Settings.ScheduledApps.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));

    public bool HasActiveSchedule(string path)
    {
        var r = GetScheduledApp(path);
        return r is { ScheduleEnabled: true } && r.Schedule.Count > 0;
    }

    public void SetAppSchedule(string path, string name, bool enabled, List<ScheduleWindow> windows)
    {
        var rule = GetScheduledApp(path);
        if (rule == null) { rule = new AppRule { Path = path, Name = name }; Settings.ScheduledApps.Add(rule); }
        rule.Name = name;
        rule.ScheduleEnabled = enabled;
        rule.Schedule = windows;
        if (!enabled && windows.Count == 0)
        {
            Settings.ScheduledApps.Remove(rule);
            NetworkController.UnblockApp(path);
        }
        Settings.Save();
        _appSchedLast.Remove(path);   // force re-evaluation on the next tick
        AppSchedTick();
        StartAppScheduler();
        StateChanged?.Invoke();
    }

    public void RemoveScheduledApp(string path)
    {
        Settings.ScheduledApps.RemoveAll(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        NetworkController.UnblockApp(path);
        _appSchedLast.Remove(path);
        Settings.Save();
        StateChanged?.Invoke();
    }

    private void StartAppScheduler()
    {
        if (_appSched == null)
        {
            _appSched = new System.Windows.Forms.Timer { Interval = 15000 };
            _appSched.Tick += (_, _) => AppSchedTick();
        }
        _appSched.Start();
    }

    private void AppSchedTick()
    {
        var now = DateTime.Now;
        foreach (var app in Settings.ScheduledApps.ToList())
        {
            bool active = app.ScheduleEnabled && app.Schedule.Any(w => w.IsActiveAt(now));
            bool known = _appSchedLast.TryGetValue(app.Path, out var last);
            if (known && active == last) continue;

            if (active)
            {
                NetworkController.BlockApp(app.Path);
            }
            else
            {
                // Don't lift a manual block that exists for the same app.
                bool manuallyBlocked = Settings.AppRules.Any(r => string.Equals(r.Path, app.Path, StringComparison.OrdinalIgnoreCase));
                if (!manuallyBlocked) NetworkController.UnblockApp(app.Path);
            }
            _appSchedLast[app.Path] = active;
        }
    }

    // ---------------- ledger (persistent per-app traffic history) ----------------

    public Ledger Ledger { get; } = Ledger.Load();
    public UsageHistory Usage { get; } = UsageHistory.Load();
    public PasswordVault Vault { get; } = new();
    private readonly Dictionary<string, (long In, long Out)> _ledgerBase = new(StringComparer.OrdinalIgnoreCase);
    private long _ledgerLastSave;

    public List<HourPoint> UsageSeries(string key, int hours) => Usage.Series(key, hours);

    /// <summary>Fold a live snapshot into the persistent ledger (adds positive deltas; tolerant of monitor restarts).</summary>
    public void RecordUsage(IEnumerable<AppUsage> apps)
    {
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        bool changed = false;

        foreach (var a in apps)
        {
            string key = !string.IsNullOrEmpty(a.ExePath) ? a.ExePath! : a.Name;
            if (string.IsNullOrEmpty(key)) continue;

            long curIn = a.BytesIn, curOut = a.BytesOut;
            bool hadBase = _ledgerBase.TryGetValue(key, out var b);
            long dIn = !hadBase ? curIn : (curIn >= b.In ? curIn - b.In : curIn);
            long dOut = !hadBase ? curOut : (curOut >= b.Out ? curOut - b.Out : curOut);
            _ledgerBase[key] = (curIn, curOut);
            if (dIn <= 0 && dOut <= 0) continue;

            if (!Ledger.Entries.TryGetValue(key, out var e))
            {
                e = new LedgerEntry { Key = key, Name = a.Name, FirstSeen = now };
                Ledger.Entries[key] = e;
            }
            e.Name = a.Name;
            e.BytesIn += dIn;
            e.BytesOut += dOut;
            e.LastSeen = now;
            if (a.Domains != null)
                foreach (var d in a.Domains)
                    if (e.Domains.Count < 50 && !e.Domains.Contains(d)) e.Domains.Add(d);
            Usage.Add(key, dIn, dOut);   // per-hour history for the activity graph
            changed = true;
        }

        if (changed)
        {
            long tick = Environment.TickCount64;
            if (tick - _ledgerLastSave > 20000) { Ledger.Save(); Usage.Save(); _ledgerLastSave = tick; }
        }
    }

    public void ClearLedger()
    {
        Ledger.Entries.Clear();
        _ledgerBase.Clear();
        Ledger.Save();
    }

    // ---------------- HTTPS inspector (MITM) ----------------

    public MitmProxy Mitm { get; } = new();
    public bool MitmRunning => Mitm.Running;

    public string? EnableMitm(bool on)
    {
        if (on)
        {
            var err = Mitm.Start(Settings.MitmPort);
            Settings.MitmEnabled = err == null;
            Settings.Save();
            if (err == null) Notify("HTTPS inspection ON", "Traffic is being decrypted via a local proxy. Disable when done.");
            return err;
        }
        Mitm.Stop();
        Settings.MitmEnabled = false;
        Settings.Save();
        return null;
    }

    /// <summary>If a global firewall cut is active, re-apply it so the safe list takes effect immediately.</summary>
    private void ReapplyIfGloballyBlocked()
    {
        if (Settings.Mechanism == KillMechanism.Firewall && Net.IsBlocked())
        {
            Net.Restore();
            Net.Block();
            RefreshIcon();
        }
    }

    private void StartAllowWatcher()
    {
        if (_allowWatch == null)
        {
            _allowWatch = new System.Windows.Forms.Timer { Interval = 2500 };
            _allowWatch.Tick += (_, _) => AllowWatchTick();
        }
        _allowWatch.Start();
    }

    private void StopAllowWatcher()
    {
        _allowWatch?.Stop();
        _promptQueue.Clear();
        _queued.Clear();
    }

    private void AllowWatchTick()
    {
        if (!Settings.AllowlistMode) return;
        List<ConnectionInfo> conns;
        try { conns = IpHelper.GetConnections(); } catch { return; }

        var allowed = AllowedAppPaths();
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (var pid in conns.Select(c => c.Pid).Distinct())
        {
            if (pid <= 4) continue;
            var (name, path) = ProcessResolver.Resolve(pid);
            if (string.IsNullOrEmpty(path)) continue;
            if (allowed.Contains(path) || _sessionDenied.Contains(path) || _queued.Contains(path)) continue;
            // Skip Windows components to avoid a flood of svchost prompts (they stay blocked; allow via Apps tab if needed).
            if (path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) continue;
            _promptQueue.Enqueue((path, name));
            _queued.Add(path);
        }
        ShowNextPrompt();
    }

    private void ShowNextPrompt()
    {
        if (_promptShowing || _promptQueue.Count == 0) return;
        var (path, name) = _promptQueue.Dequeue();
        if (AllowedAppPaths().Contains(path) || _sessionDenied.Contains(path))
        {
            _queued.Remove(path);
            ShowNextPrompt();
            return;
        }

        _promptShowing = true;
        var dlg = new AllowPromptForm(name, path);
        dlg.FormClosed += (_, _) =>
        {
            _queued.Remove(path);
            switch (dlg.Result)
            {
                case AllowPromptResult.Allow: AllowApp(path, name); break;
                case AllowPromptResult.AlwaysBlock: _sessionDenied.Add(path); BlockApp(path, name); break;
                default: _sessionDenied.Add(path); break;
            }
            _promptShowing = false;
            ShowNextPrompt();
        };
        dlg.Show();
        dlg.Activate();
    }

    // ---------------- tray / ui ----------------

    private void RefreshIcon()
    {
        bool blocked = IsBlocked;
        var newIcon = IconFactory.Make(blocked);
        _tray.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;

        _tray.Text = blocked ? "KillSwitch — INTERNET CUT" : "KillSwitch — online";
        _toggleItem.Text = blocked ? "Restore internet" : "Cut internet now";
    }

    public void Notify(string title, string text, bool warning = false)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    public void ShowMainWindow()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(this);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    public void ExitApp()
    {
        // Safety: stop the MITM proxy FIRST so the system proxy is always restored.
        try { Mitm.Stop(); } catch { /* ignore */ }
        // Safety: always restore connectivity on exit so we never leave the user stranded.
        try { Net.RestoreAll(); } catch { /* ignore */ }
        try { Ledger.Save(); } catch { /* ignore */ }
        try { Usage.Save(); } catch { /* ignore */ }
        try { Vault.Lock(); } catch { /* clear decrypted entries from memory */ }

        _disposed = true;
        try { _showEvent.Set(); } catch { }
        try { _showEvent.Dispose(); } catch { }
        try { _marshal.Dispose(); } catch { }

        StopAllowWatcher();
        _allowWatch?.Dispose();
        _appSched?.Stop();
        _appSched?.Dispose();
        _scheduler.Stop();
        _scheduler.Dispose();
        _hotkey.Dispose();
        _saveLoginHotkey.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        ExitThread();
    }
}
