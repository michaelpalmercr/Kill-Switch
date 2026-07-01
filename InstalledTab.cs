using System.IO;

namespace KillSwitch;

/// <summary>
/// Installed-programs manager: Tier-1 official Uninstall, Tier-2 Force-remove (with impact preview +
/// quarantine), arbitrary Force-delete of a file/folder, Restore-last-removal, and AI analysis.
/// </summary>
public sealed class InstalledTab : UserControl
{
    private readonly KillSwitchContext _ctx;
    private readonly FastListView _list = new();
    private readonly TextBox _filter = new();
    private readonly CheckBox _showSystem = new();
    private readonly Label _summary = new();
    private List<InstalledProgram> _all = new();

    public InstalledTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        VisibleChanged += (_, _) => { if (Visible && _all.Count == 0) Reload(); };
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        var top = new Panel { Dock = DockStyle.Top, Height = 30 };
        var lbl = new Label { Text = "Installed programs — Uninstall (clean) or Force-remove (with impact preview + quarantine).", AutoSize = true, ForeColor = Color.DimGray };
        lbl.SetBounds(2, 6, 640, 18);
        _showSystem.Text = "Show system components"; _showSystem.AutoSize = true; _showSystem.Location = new Point(650, 5);
        _showSystem.CheckedChanged += (_, _) => Render();
        var flbl = new Label { Text = "Filter:", AutoSize = true, Location = new Point(820, 6) };
        _filter.SetBounds(864, 3, 160, 24);
        _filter.PlaceholderText = "name / publisher";
        _filter.TextChanged += (_, _) => Render();
        top.Controls.AddRange(new Control[] { lbl, _showSystem, flbl, _filter });

        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Program", 240);
        _list.Columns.Add("Publisher", 190);
        _list.Columns.Add("Version", 100);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Location", 360);
        _list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { var h = _list.HitTest(e.Location); if (h.Item != null) h.Item.Selected = true; } };
        _list.DoubleClick += (_, _) => DoAnalyze();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 78, WrapContents = true, Padding = new Padding(2) };
        Button B(string text, EventHandler onClick, Color? back = null)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(2, 4, 2, 4) };
            if (back is { } c) { b.BackColor = c; b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; }
            b.Click += onClick;
            bar.Controls.Add(b);
            return b;
        }
        B("Uninstall (clean)", (_, _) => DoUninstall());
        B("Force remove…", (_, _) => DoForceRemove(), IconFactory.Blocked);
        B("Force-delete file…", (_, _) => DoForceDeleteFile(), Color.FromArgb(200, 90, 40));
        B("Force-delete folder…", (_, _) => DoForceDeleteFolder(), Color.FromArgb(200, 90, 40));
        B("Analyze (AI)", (_, _) => DoAnalyze());
        B("Restore last removal", (_, _) => DoRestore());
        B("Refresh", (_, _) => Reload());
        _summary.AutoSize = true; _summary.Margin = new Padding(10, 10, 2, 2); _summary.ForeColor = Color.DimGray;
        bar.Controls.Add(_summary);

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bar);
    }

    private void Reload()
    {
        Cursor = Cursors.WaitCursor;
        try { _all = InstalledPrograms.Enumerate(); } finally { Cursor = Cursors.Default; }
        Render();
    }

    private void Render()
    {
        var f = _filter.Text.Trim();
        var list = _all
            .Where(p => _showSystem.Checked || !p.SystemComponent)
            .Where(p => f.Length == 0 || p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Publisher.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? selName = Selected()?.Name;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in list)
        {
            var it = new ListViewItem(p.Name) { Tag = p };
            it.SubItems.Add(p.Publisher);
            it.SubItems.Add(p.Version);
            it.SubItems.Add(p.SizeKB > 0 ? Fmt.Bytes(p.SizeKB * 1024L) : "");
            it.SubItems.Add(p.InstallLocation);
            if (p.SystemComponent) it.ForeColor = Color.DimGray;
            if (p.Name == selName) it.Selected = true;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _summary.Text = $"{list.Count} of {_all.Count} programs";
    }

    private InstalledProgram? Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as InstalledProgram : null;

    private void DoUninstall()
    {
        var p = Selected();
        if (p == null) { Info("Select a program first."); return; }
        if (string.IsNullOrWhiteSpace(p.UninstallString)) { Info($"\"{p.Name}\" has no registered uninstaller. Use Force remove."); return; }
        if (MessageBox.Show(this, $"Run the official uninstaller for \"{p.Name}\"?", "KillSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Cursor = Cursors.WaitCursor;
        string msg;
        try { msg = RemovalEngine.Uninstall(p, preferSilent: false); } catch (Exception ex) { msg = "Uninstall failed: " + ex.Message; }
        Cursor = Cursors.Default;
        MessageBox.Show(this, msg, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Reload();
    }

    private void DoForceRemove()
    {
        var p = Selected();
        if (p == null) { Info("Select a program first."); return; }
        string target = p.InstallLocation;
        using var f = new ImpactPreviewForm(_ctx, target, "Force-remove: " + p.Name,
            (batch, kill) => RemovalEngine.ForceRemove(p, batch, kill));
        if (f.ShowDialog(this) == DialogResult.OK) Reload();
    }

    private void DoForceDeleteFile()
    {
        using var ofd = new OpenFileDialog { Title = "Pick a file to force-delete", CheckFileExists = true };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        ForceDeletePath(ofd.FileName);
    }

    private void DoForceDeleteFolder()
    {
        using var fbd = new FolderBrowserDialog { Description = "Pick a folder to force-delete" };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;
        ForceDeletePath(fbd.SelectedPath);
    }

    private void ForceDeletePath(string path)
    {
        using var f = new ImpactPreviewForm(_ctx, path, "Force-delete: " + Path.GetFileName(path.TrimEnd('\\')),
            (batch, kill) => RemovalEngine.ForceDelete(path, batch, kill));
        f.ShowDialog(this);
    }

    private void DoRestore()
    {
        var last = Quarantine.Latest();
        if (last == null) { Info("Nothing to restore — no quarantined removals yet."); return; }
        if (MessageBox.Show(this, $"Restore the most recent removal?\n\n{last.Note}\n{last.When}\n{last.Items.Count} item(s)", "KillSwitch — restore", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Cursor = Cursors.WaitCursor;
        var (ok, fail) = Quarantine.Restore(last);
        Cursor = Cursors.Default;
        MessageBox.Show(this, $"Restored {ok} item(s)" + (fail > 0 ? $", {fail} could not be restored (the original location may be in use)." : "."), "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Reload();
    }

    private void DoAnalyze()
    {
        var p = Selected();
        if (p == null) { Info("Select a program first."); return; }
        using var f = new AiReportForm(_ctx, p.Name, AppInspector.BuildInstalledPrompt(p));
        f.ShowDialog(this);
    }

    private void Info(string m) => MessageBox.Show(this, m, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
