namespace KillSwitch;

/// <summary>
/// The "Apps" tab: the allowlist (default-deny) master switch, plus managed allowed/blocked
/// lists with Add (pick a running app or browse) / Remove.
/// </summary>
public sealed class AppsTab : UserControl
{
    private readonly KillSwitchContext _ctx;
    private AppSettings S => _ctx.Settings;
    private bool _loading;

    private readonly CheckBox _allowlist = new();
    private readonly FastListView _allowed = new();
    private readonly FastListView _blocked = new();
    private readonly FastListView _safe = new();

    public AppsTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        _ctx.StateChanged += () => { if (IsHandleCreated) BeginInvoke((Action)RefreshAll); };
        VisibleChanged += (_, _) => { if (Visible) RefreshAll(); };
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(12);

        var top = new Panel { Dock = DockStyle.Top, Height = 70 };
        _allowlist.Text = "Allowlist mode — block everything except allowed apps (default-deny)";
        _allowlist.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _allowlist.SetBounds(2, 6, 700, 24);
        _allowlist.CheckedChanged += OnAllowlistToggled;

        var desc = new Label
        {
            Text = "When on, nothing reaches the internet until you approve it. New apps that try to connect will pop a prompt. "
                 + "DNS is allowed automatically so approved apps can resolve names.",
            ForeColor = Color.Gray,
            AutoSize = false,
        };
        desc.SetBounds(22, 32, 760, 34);
        var lockedBtn = new Button { Text = "Locked apps…", Width = 120, Height = 28, Location = new Point(760, 6), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        lockedBtn.Click += (_, _) => { using var f = new LockedAppsForm(_ctx); f.ShowDialog(this); };

        top.Controls.Add(_allowlist);
        top.Controls.Add(desc);
        top.Controls.Add(lockedBtn);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        table.Controls.Add(BuildListGroup("🛡 Safe apps (stay online during a cut)", _safe,
            onAdd: AddSafe, onRemove: RemoveSafe), 0, 0);
        table.Controls.Add(BuildListGroup("Blocked apps (cannot connect)", _blocked,
            onAdd: AddBlocked, onRemove: RemoveBlocked), 1, 0);
        table.Controls.Add(BuildListGroup("Allowed apps (allowlist mode)", _allowed,
            onAdd: AddAllowed, onRemove: RemoveAllowed), 2, 0);

        Controls.Add(table);
        Controls.Add(top);
    }

    private static GroupBox BuildListGroup(string title, FastListView list, Action onAdd, Action onRemove)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Margin = new Padding(4) };

        list.Columns.Add("Application", 150);
        list.Columns.Add("Path", 320);
        list.Dock = DockStyle.Fill;

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var add = new Button { Text = "Add…", Width = 100, Height = 30, Location = new Point(6, 5) };
        add.Click += (_, _) => onAdd();
        var remove = new Button { Text = "Remove", Width = 100, Height = 30, Location = new Point(112, 5) };
        remove.Click += (_, _) => onRemove();
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 18, 6, 0) };
        pad.Controls.Add(list);
        g.Controls.Add(pad);
        g.Controls.Add(buttons);
        return g;
    }

    private void OnAllowlistToggled(object? sender, EventArgs e)
    {
        if (_loading) return;
        var err = _ctx.SetAllowlistMode(_allowlist.Checked);
        if (err != null)
        {
            MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshAll();
        }
    }

    private (string path, string name)? PickApp()
    {
        using var dlg = new AddAppDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPath != null)
            return (dlg.SelectedPath, dlg.SelectedName ?? dlg.SelectedPath);
        return null;
    }

    private void AddAllowed()
    {
        if (PickApp() is { } a) { _ctx.AllowApp(a.path, a.name); RefreshAll(); }
    }

    private void RemoveAllowed()
    {
        if (_allowed.SelectedItems.Count > 0 && _allowed.SelectedItems[0].Tag is AppRule r)
        { _ctx.RemoveAllowedApp(r.Path); RefreshAll(); }
    }

    private void AddBlocked()
    {
        if (PickApp() is { } a)
        {
            var err = _ctx.BlockApp(a.path, a.name);
            if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshAll();
        }
    }

    private void RemoveBlocked()
    {
        if (_blocked.SelectedItems.Count > 0 && _blocked.SelectedItems[0].Tag is AppRule r)
        { _ctx.UnblockApp(r.Path); RefreshAll(); }
    }

    private void AddSafe()
    {
        if (PickApp() is { } a) { _ctx.AddSafeApp(a.path, a.name); RefreshAll(); }
    }

    private void RemoveSafe()
    {
        if (_safe.SelectedItems.Count > 0 && _safe.SelectedItems[0].Tag is AppRule r)
        { _ctx.RemoveSafeApp(r.Path); RefreshAll(); }
    }

    private void RefreshAll()
    {
        _loading = true;
        _allowlist.Checked = S.AllowlistMode;

        Fill(_allowed, S.AllowedApps);
        Fill(_blocked, S.AppRules);
        Fill(_safe, S.SafeApps);
        _loading = false;
    }

    private static void Fill(FastListView list, IEnumerable<AppRule> rules)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var r in rules)
        {
            var it = new ListViewItem(r.Name) { Tag = r };
            it.SubItems.Add(r.Path);
            list.Items.Add(it);
        }
        list.EndUpdate();
    }
}
