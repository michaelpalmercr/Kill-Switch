using System.Diagnostics;

namespace KillSwitch;

/// <summary>
/// Task-manager-style view of running programs, with Force-close and Force-close &amp; lock
/// (block from running). Locked-but-not-running apps are shown too so they can be unlocked.
/// </summary>
public sealed class ProgramsTab : UserControl
{
    private sealed class Row
    {
        public string Name = "";
        public string? Path;
        public readonly List<int> Pids = new();
        public bool Locked;
        public string Key => string.IsNullOrEmpty(Path) ? "name:" + Name : Path!;
    }

    private readonly KillSwitchContext _ctx;
    private readonly FastListView _list = new();
    private readonly TextBox _filter = new();
    private readonly Label _summary = new();
    private readonly System.Windows.Forms.Timer _t = new() { Interval = 2000 };

    public ProgramsTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        _t.Tick += (_, _) => Reload();
        VisibleChanged += (_, _) => { if (Visible) { Reload(); _t.Start(); } else _t.Stop(); };
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        var top = new Panel { Dock = DockStyle.Top, Height = 30 };
        var lbl = new Label { Text = "Running programs — select one, then Force-close or Lock (blocks it from reopening).", AutoSize = true, ForeColor = Color.DimGray };
        lbl.SetBounds(2, 6, 620, 18);
        var flbl = new Label { Text = "Filter:", AutoSize = true, Location = new Point(640, 6) };
        _filter.SetBounds(684, 3, 180, 24);
        _filter.PlaceholderText = "name / path";
        _filter.TextChanged += (_, _) => Reload();
        top.Controls.AddRange(new Control[] { lbl, flbl, _filter });

        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Program", 220);
        _list.Columns.Add("Running", 70, HorizontalAlignment.Right);
        _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Path", 470);
        _list.Columns.Add("Status", 110);
        _list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { var h = _list.HitTest(e.Location); if (h.Item != null) h.Item.Selected = true; } };

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var close = new Button { Text = "Force-close", Width = 110, Height = 30, Location = new Point(2, 7), BackColor = Color.FromArgb(200, 90, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        close.Click += (_, _) => DoForceClose();
        var lockBtn = new Button { Text = "Force-close && lock", Width = 150, Height = 30, Location = new Point(118, 7), BackColor = IconFactory.Blocked, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        lockBtn.Click += (_, _) => DoLock();
        var unlock = new Button { Text = "Unlock", Width = 90, Height = 30, Location = new Point(274, 7) };
        unlock.Click += (_, _) => DoUnlock();
        var analyze = new Button { Text = "Analyze (AI)", Width = 100, Height = 30, Location = new Point(370, 7) };
        analyze.Click += (_, _) => DoAnalyze();
        var refresh = new Button { Text = "Refresh", Width = 80, Height = 30, Location = new Point(476, 7) };
        refresh.Click += (_, _) => Reload();
        bar.Controls.AddRange(new Control[] { close, lockBtn, unlock, analyze, refresh });

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bar);
    }

    private void Reload()
    {
        if (!Visible) return;
        var locked = _ctx.LockedAppPaths();
        var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            int pid = p.Id;
            try { p.Dispose(); } catch { }
            if (pid <= 0) continue;
            var (name, path) = ProcessResolver.Resolve(pid);
            string key = string.IsNullOrEmpty(path) ? "name:" + name : path!;
            if (!rows.TryGetValue(key, out var r)) { r = new Row { Name = name, Path = path }; rows[key] = r; }
            r.Pids.Add(pid);
        }
        // Include locked apps that aren't currently running, so they can be unlocked here.
        foreach (var la in _ctx.Settings.LockedApps)
        {
            if (!rows.ContainsKey(la.Path)) rows[la.Path] = new Row { Name = la.Name, Path = la.Path };
        }
        foreach (var r in rows.Values) r.Locked = r.Path != null && locked.Contains(r.Path);

        var f = _filter.Text.Trim();
        var list = rows.Values
            .Where(r => f.Length == 0 || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || (r.Path?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(r => r.Locked)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? selKey = (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is Row sr) ? sr.Key : null;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in list)
        {
            var it = new ListViewItem(r.Name) { Tag = r };
            it.SubItems.Add(r.Pids.Count.ToString());
            it.SubItems.Add(r.Pids.Count > 0 ? r.Pids[0].ToString() : "");
            it.SubItems.Add(r.Path ?? "(system/no path)");
            it.SubItems.Add(r.Locked ? "⛔ locked" : (r.Pids.Count > 0 ? "running" : ""));
            if (r.Locked) it.ForeColor = IconFactory.Blocked;
            if (r.Key == selKey) it.Selected = true;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _summary.Text = $"{list.Count} programs";
    }

    private Row? Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Row : null;

    private void DoForceClose()
    {
        var r = Selected();
        if (r == null) { Info("Select a program first."); return; }
        if (r.Pids.Count == 0) { Info($"{r.Name} isn't running."); return; }
        if (MessageBox.Show(this, $"Force-close {r.Pids.Count} instance(s) of \"{r.Name}\"?", "KillSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var pid in r.Pids)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
        }
        Reload();
    }

    private void DoLock()
    {
        var r = Selected();
        if (r == null) { Info("Select a program first."); return; }
        if (string.IsNullOrEmpty(r.Path)) { Info($"{r.Name} is a system/protected process with no exe path, so it can't be locked."); return; }
        if (MessageBox.Show(this, $"Force-close \"{r.Name}\" and block it from running until you unlock it?\n\n{r.Path}", "KillSwitch — lock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var err = _ctx.LockApp(r.Path!, r.Name);
        if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Reload();
    }

    private void DoUnlock()
    {
        var r = Selected();
        if (r == null || string.IsNullOrEmpty(r.Path)) { Info("Select a locked program."); return; }
        _ctx.UnlockApp(r.Path!);
        Reload();
    }

    private void DoAnalyze()
    {
        var r = Selected();
        if (r == null) { Info("Select a program first."); return; }
        var app = new AppUsage { Name = r.Name, ExePath = r.Path, Pid = r.Pids.Count > 0 ? r.Pids[0] : 0 };
        using var f = new AiAnalysisForm(_ctx, app);
        f.ShowDialog(this);
    }

    private void Info(string m) => MessageBox.Show(this, m, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
