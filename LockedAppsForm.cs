namespace KillSwitch;

/// <summary>Manage execution-locked apps (force-closed + blocked from running). Unlock here since they leave the Traffic list.</summary>
public sealed class LockedAppsForm : Form
{
    private readonly KillSwitchContext _ctx;
    private readonly FastListView _list = new();

    public LockedAppsForm(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        Reload();
    }

    private void BuildUi()
    {
        Text = "Locked apps — blocked from running";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 360);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(true);

        var hint = new Label
        {
            Text = "These apps are force-closed and Windows refuses to launch them until unlocked (persists across reboots).",
            Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(4, 4, 0, 0),
        };

        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Application", 170);
        _list.Columns.Add("Path", 420);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var add = new Button { Text = "Lock an app…", Width = 110, Height = 30, Location = new Point(6, 7) };
        add.Click += (_, _) => AddLock();
        var unlock = new Button { Text = "Unlock", Width = 90, Height = 30, Location = new Point(122, 7) };
        unlock.Click += (_, _) => UnlockSelected();
        var unlockAll = new Button { Text = "Unlock all", Width = 100, Height = 30, Location = new Point(218, 7) };
        unlockAll.Click += (_, _) =>
        {
            if (_ctx.Settings.LockedApps.Count == 0) return;
            if (MessageBox.Show(this, "Unlock all locked apps?", "KillSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var r in _ctx.Settings.LockedApps.ToList()) _ctx.UnlockApp(r.Path);
            Reload();
        };
        var close = new Button { Text = "Close", Width = 80, Height = 30, Location = new Point(330, 7), DialogResult = DialogResult.OK };
        bar.Controls.AddRange(new Control[] { add, unlock, unlockAll, close });

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(bar);
        CancelButton = close;
        Theme.Apply(this);
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _ctx.Settings.LockedApps)
        {
            var it = new ListViewItem(r.Name) { Tag = r };
            it.SubItems.Add(r.Path);
            _list.Items.Add(it);
        }
        _list.EndUpdate();
    }

    private void UnlockSelected()
    {
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is AppRule r)
        { _ctx.UnlockApp(r.Path); Reload(); }
    }

    private void AddLock()
    {
        using var dlg = new AddAppDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedPath == null) return;
        var err = _ctx.LockApp(dlg.SelectedPath, dlg.SelectedName ?? dlg.SelectedPath);
        if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Reload();
    }
}
