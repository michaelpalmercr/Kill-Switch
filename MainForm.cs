namespace KillSwitch;

/// <summary>The "proper UI" shown when the tray icon is double-clicked.</summary>
public sealed class MainForm : Form
{
    private readonly KillSwitchContext _ctx;
    private AppSettings S => _ctx.Settings;
    private bool _loading;

    // tabs (kept so the "save login" hotkey can jump to Passwords)
    private TabControl _tabs = null!;
    private TabPage _passwordsPage = null!;
    private PasswordsTab _passwordsTab = null!;

    // status
    private readonly Label _statusDot = new();
    private readonly Label _statusText = new();
    private readonly Button _toggle = new();

    // mechanism
    private readonly RadioButton _rbFirewall = new();
    private readonly RadioButton _rbAdapter = new();
    private readonly Label _fwWarning = new();
    private readonly Button _fwEnable = new();

    // schedule
    private readonly CheckBox _schedEnabled = new();
    private readonly ListView _windows = new();
    private readonly CheckBox[] _dayChecks = new CheckBox[7];
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();

    // options
    private readonly CheckBox _startup = new();
    private readonly CheckBox _startMin = new();
    private readonly CheckBox _hotkeyEnabled = new();
    private readonly TextBox _hotkeyBox = new();

    public MainForm(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        _ctx.StateChanged += OnStateChanged;
        _ctx.SaveLoginRequested += OnSaveLoginRequested;
        FormClosed += (_, _) => _ctx.SaveLoginRequested -= OnSaveLoginRequested;
        RefreshFromState();
        PopulateWindows();
    }

    /// <summary>Ctrl+Alt+L pressed: jump to the Passwords tab and quick-save the current login.</summary>
    private void OnSaveLoginRequested(string windowTitle)
    {
        try
        {
            _tabs.SelectedTab = _passwordsPage;
            _passwordsTab.QuickAdd(windowTitle);
        }
        catch { }
    }

    private void BuildUi()
    {
        Text = "KillSwitch";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 740);
        MinimumSize = new Size(700, 600);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(false);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs = tabs;
        var pageControl = new TabPage("Control");
        var pageTraffic = new TabPage("Traffic");
        var pagePrograms = new TabPage("Programs");
        var pageApps = new TabPage("Apps");
        var pageLedger = new TabPage("Ledger");
        var pageInspect = new TabPage("Inspect");
        var pagePasswords = new TabPage("Passwords");
        _passwordsPage = pagePasswords;
        var pageInstalled = new TabPage("Installed");
        tabs.TabPages.Add(pageControl);
        tabs.TabPages.Add(pageTraffic);
        tabs.TabPages.Add(pagePrograms);
        tabs.TabPages.Add(pageApps);
        tabs.TabPages.Add(pageLedger);
        tabs.TabPages.Add(pageInspect);
        tabs.TabPages.Add(pagePasswords);
        tabs.TabPages.Add(pageInstalled);
        Controls.Add(tabs);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
            Width = 452,
        };
        pageControl.Controls.Add(root);

        root.Controls.Add(BuildStatusPanel());
        root.Controls.Add(BuildMechanismGroup());
        root.Controls.Add(BuildScheduleGroup());
        root.Controls.Add(BuildOptionsGroup());
        root.Controls.Add(BuildFooter());

        // Keep the control column centered so the wide window (sized for Traffic) doesn't look lopsided.
        void CenterRoot()
        {
            root.Top = 0;
            root.Height = pageControl.ClientSize.Height;
            root.Left = Math.Max(0, (pageControl.ClientSize.Width - root.Width) / 2);
        }
        pageControl.Resize += (_, _) => CenterRoot();
        Shown += (_, _) => CenterRoot();
        CenterRoot();

        pageTraffic.Controls.Add(new TrafficTab(_ctx));
        pagePrograms.Controls.Add(new ProgramsTab(_ctx));
        pageApps.Controls.Add(new AppsTab(_ctx));
        pageLedger.Controls.Add(new LedgerTab(_ctx));
        pageInspect.Controls.Add(new InspectTab(_ctx));
        _passwordsTab = new PasswordsTab(_ctx);
        pagePasswords.Controls.Add(_passwordsTab);
        pageInstalled.Controls.Add(new InstalledTab(_ctx));

        FormClosing += (_, e) =>
        {
            // X just hides to the tray; real exit is via the tray menu.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private const int W = 416;

    private Control BuildStatusPanel()
    {
        var p = new Panel { Width = W, Height = 110, Margin = new Padding(0, 0, 0, 8) };

        _statusDot.Text = "●";
        _statusDot.Font = new Font("Segoe UI", 22f);
        _statusDot.AutoSize = true;
        _statusDot.Location = new Point(2, 6);

        _statusText.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        _statusText.AutoSize = true;
        _statusText.Location = new Point(40, 12);

        _toggle.Width = W;
        _toggle.Height = 54;
        _toggle.Location = new Point(0, 52);
        _toggle.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        _toggle.FlatStyle = FlatStyle.Flat;
        _toggle.ForeColor = Color.White;
        _toggle.FlatAppearance.BorderSize = 0;
        _toggle.Click += (_, _) => _ctx.ToggleBlock();

        p.Controls.Add(_statusDot);
        p.Controls.Add(_statusText);
        p.Controls.Add(_toggle);
        return p;
    }

    private Control BuildMechanismGroup()
    {
        var g = new GroupBox { Text = "Kill method", Width = W, Height = 150, Margin = new Padding(0, 0, 0, 8) };

        _rbFirewall.Text = "Firewall — block all traffic (recommended)";
        _rbFirewall.SetBounds(14, 24, W - 28, 22);
        _rbFirewall.CheckedChanged += (_, _) => { if (!_loading && _rbFirewall.Checked) SetMechanism(KillMechanism.Firewall); };

        var fwHelp = new Label { Text = "Instant on/off. Adapter stays up; localhost still works.", ForeColor = Color.Gray };
        fwHelp.AutoSize = false;
        fwHelp.SetBounds(34, 46, W - 48, 16);

        _rbAdapter.Text = "Adapter — disable network cards (hard kill)";
        _rbAdapter.SetBounds(14, 68, W - 28, 22);
        _rbAdapter.CheckedChanged += (_, _) => { if (!_loading && _rbAdapter.Checked) SetMechanism(KillMechanism.Adapter); };

        var adHelp = new Label
        {
            Text = "Absolute cut. Slower to recover (Wi-Fi must reconnect).",
            ForeColor = Color.Gray,
        };
        adHelp.AutoSize = false;
        adHelp.SetBounds(34, 90, W - 48, 16);

        _fwWarning.Text = "⚠ Windows Firewall is OFF — firewall mode won't be enforced.";
        _fwWarning.ForeColor = Color.FromArgb(180, 90, 0);
        _fwWarning.AutoSize = false;
        _fwWarning.SetBounds(14, 114, 270, 30);
        _fwWarning.Visible = false;

        _fwEnable.Text = "Turn on";
        _fwEnable.SetBounds(W - 100, 116, 86, 26);
        _fwEnable.Visible = false;
        _fwEnable.Click += (_, _) =>
        {
            var err = NetworkController.EnableFirewall();
            if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshFromState();
        };

        g.Controls.AddRange(new Control[] { _rbFirewall, fwHelp, _rbAdapter, adHelp, _fwWarning, _fwEnable });
        return g;
    }

    private Control BuildScheduleGroup()
    {
        var g = new GroupBox { Text = "Auto schedule", Width = W, Height = 250, Margin = new Padding(0, 0, 0, 8) };

        _schedEnabled.Text = "Enable automatic schedule";
        _schedEnabled.SetBounds(14, 22, 250, 22);
        _schedEnabled.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            S.ScheduleEnabled = _schedEnabled.Checked;
            _ctx.ApplyScheduleChanged();
        };

        _windows.SetBounds(14, 48, W - 28, 96);
        _windows.View = View.Details;
        _windows.CheckBoxes = true;
        _windows.FullRowSelect = true;
        _windows.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _windows.Columns.Add("Days", 170);
        _windows.Columns.Add("From", 100);
        _windows.Columns.Add("To", 100);
        _windows.ItemChecked += (_, e) =>
        {
            if (_loading) return;
            if (e.Item.Tag is ScheduleWindow w)
            {
                w.Enabled = e.Item.Checked;
                _ctx.ApplyScheduleChanged();
            }
        };

        // Editor row: day toggles
        string[] dayNames = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        int x = 14;
        for (int i = 0; i < 7; i++)
        {
            var cb = new CheckBox
            {
                Text = dayNames[i],
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Width = 38,
                Height = 26,
                Location = new Point(x, 150),
            };
            // default Mon–Fri ticked in the editor
            cb.Checked = i >= 1 && i <= 5;
            _dayChecks[i] = cb;
            g.Controls.Add(cb);
            x += 42;
        }

        _from.Format = DateTimePickerFormat.Time;
        _from.ShowUpDown = true;
        _from.Value = DateTime.Today.AddHours(23);
        _from.SetBounds(14, 182, 90, 24);

        var dash = new Label { Text = "to", AutoSize = true, Location = new Point(112, 186) };

        _to.Format = DateTimePickerFormat.Time;
        _to.ShowUpDown = true;
        _to.Value = DateTime.Today.AddHours(7);
        _to.SetBounds(134, 182, 90, 24);

        var add = new Button { Text = "Add window", Location = new Point(234, 181), Width = 96, Height = 26 };
        add.Click += (_, _) => AddWindow();

        var remove = new Button { Text = "Remove selected", Location = new Point(14, 214), Width = 130, Height = 26 };
        remove.Click += (_, _) => RemoveSelectedWindows();

        var hint = new Label
        {
            Text = "Days = the night the window starts.",
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(156, 219),
        };

        g.Controls.AddRange(new Control[] { _schedEnabled, _windows, _from, dash, _to, add, remove, hint });
        return g;
    }

    private Control BuildOptionsGroup()
    {
        var g = new GroupBox { Text = "Options", Width = W, Height = 124, Margin = new Padding(0, 0, 0, 8) };

        _startup.Text = "Start with Windows (runs elevated at logon)";
        _startup.SetBounds(14, 24, W - 28, 22);
        _startup.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var err = _ctx.ApplyStartup(_startup.Checked);
            if (err != null)
            {
                MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshFromState();
            }
        };

        _startMin.Text = "Start minimized to the tray";
        _startMin.SetBounds(14, 50, W - 28, 22);
        _startMin.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            S.StartMinimized = _startMin.Checked;
            S.Save();
        };

        _hotkeyEnabled.Text = "Global hotkey:";
        _hotkeyEnabled.SetBounds(14, 80, 110, 22);
        _hotkeyEnabled.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            S.HotkeyEnabled = _hotkeyEnabled.Checked;
            _hotkeyBox.Enabled = _hotkeyEnabled.Checked;
            _ctx.ApplyHotkey();
            S.Save();
        };

        _hotkeyBox.SetBounds(130, 78, 160, 24);
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.Cursor = Cursors.Hand;
        _hotkeyBox.ShortcutsEnabled = false;
        _hotkeyBox.GotFocus += (_, _) => _hotkeyBox.Text = "Press a key combo…";
        _hotkeyBox.LostFocus += (_, _) => RefreshHotkeyText();
        _hotkeyBox.KeyDown += OnHotkeyCapture;

        var hint = new Label
        {
            Text = "Click the box, then press your combo (e.g. Ctrl+Alt+K).",
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(14, 104),
        };

        g.Controls.AddRange(new Control[] { _startup, _startMin, _hotkeyEnabled, _hotkeyBox, hint });
        return g;
    }

    private Control BuildFooter()
    {
        var p = new Panel { Width = W, Height = 40 };
        var hide = new Button { Text = "Hide to tray", Width = 110, Height = 28, Location = new Point(0, 6) };
        hide.Click += (_, _) => Hide();

        var ver = new Label
        {
            Text = "KillSwitch v1.0 — runs as admin",
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(170, 12),
        };
        p.Controls.Add(hide);
        p.Controls.Add(ver);
        return p;
    }

    // ---------------- behaviour ----------------

    private void SetMechanism(KillMechanism m)
    {
        S.Mechanism = m;
        _ctx.ApplyMechanismChanged();
        RefreshFromState();
    }

    private void AddWindow()
    {
        var w = new ScheduleWindow
        {
            Days = Enumerable.Range(0, 7).Select(i => _dayChecks[i].Checked).ToArray(),
            Start = _from.Value.ToString("HH:mm"),
            End = _to.Value.ToString("HH:mm"),
            Enabled = true,
        };
        if (!w.Days.Any(d => d))
        {
            MessageBox.Show(this, "Pick at least one day.", "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        S.Schedule.Add(w);
        _ctx.ApplyScheduleChanged();
        PopulateWindows();
    }

    private void RemoveSelectedWindows()
    {
        foreach (ListViewItem item in _windows.SelectedItems)
            if (item.Tag is ScheduleWindow w) S.Schedule.Remove(w);
        _ctx.ApplyScheduleChanged();
        PopulateWindows();
    }

    private void PopulateWindows()
    {
        _loading = true;
        _windows.Items.Clear();
        foreach (var w in S.Schedule)
        {
            var item = new ListViewItem(w.DaysLabel()) { Tag = w, Checked = w.Enabled };
            item.SubItems.Add(w.Start);
            item.SubItems.Add(w.End);
            _windows.Items.Add(item);
        }
        _loading = false;
    }

    private void OnHotkeyCapture(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
            return; // wait for a real key

        S.HotkeyModifiers = (int)e.Modifiers;
        S.HotkeyKey = (int)key;
        S.HotkeyEnabled = true;
        _hotkeyEnabled.Checked = true;
        _ctx.ApplyHotkey();
        S.Save();
        RefreshHotkeyText();
        ActiveControl = null; // drop focus so the "press a combo" prompt clears
    }

    private void RefreshHotkeyText()
        => _hotkeyBox.Text = GlobalHotkey.Describe((Keys)S.HotkeyModifiers, (Keys)S.HotkeyKey);

    private void OnStateChanged() => RefreshFromState();

    private void RefreshFromState()
    {
        _loading = true;
        bool blocked = _ctx.IsBlocked;

        _statusDot.ForeColor = blocked ? IconFactory.Blocked : IconFactory.Online;
        _statusText.Text = blocked ? "INTERNET CUT" : "ONLINE";
        _statusText.ForeColor = blocked ? IconFactory.Blocked : IconFactory.Online;

        _toggle.Text = blocked ? "Restore Internet" : "Cut Internet";
        _toggle.BackColor = blocked ? IconFactory.Online : IconFactory.Blocked;

        _rbFirewall.Checked = S.Mechanism == KillMechanism.Firewall;
        _rbAdapter.Checked = S.Mechanism == KillMechanism.Adapter;

        bool showFwWarn = S.Mechanism == KillMechanism.Firewall && !NetworkController.IsFirewallOn();
        _fwWarning.Visible = showFwWarn;
        _fwEnable.Visible = showFwWarn;

        _schedEnabled.Checked = S.ScheduleEnabled;
        _startup.Checked = Startup.IsEnabled();
        _startMin.Checked = S.StartMinimized;
        _hotkeyEnabled.Checked = S.HotkeyEnabled;
        _hotkeyBox.Enabled = S.HotkeyEnabled;
        RefreshHotkeyText();

        _loading = false;
    }
}
