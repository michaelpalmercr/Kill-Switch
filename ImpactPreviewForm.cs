using System.IO;

namespace KillSwitch;

/// <summary>
/// Shows the blast radius of a force-removal (files/size, running programs using it, services) and,
/// for protected/System32 paths, requires typing REMOVE to confirm. Removals are quarantined
/// (reversible) unless a locked file falls back to delete-on-reboot. Also offers an AI impact report.
/// </summary>
public sealed class ImpactPreviewForm : Form
{
    private readonly KillSwitchContext _ctx;
    private readonly string _target;
    private readonly string _title;
    private readonly Func<QBatch, bool, string> _removeAction;

    private readonly TextBox _details = new();
    private readonly CheckBox _killLockers = new();
    private readonly TextBox _confirmBox = new();
    private readonly Label _confirmLbl = new();
    private readonly Button _remove = new();
    private readonly Button _ai = new();
    private ImpactScanner.Result? _scan;
    private readonly bool _protected;

    public ImpactPreviewForm(KillSwitchContext ctx, string target, string title, Func<QBatch, bool, string> removeAction)
    {
        _ctx = ctx;
        _target = target ?? "";
        _title = title;
        _removeAction = removeAction;
        _protected = _target.Length > 0 && (
            _target.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) ||
            _target.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase) ||
            _target.TrimEnd('\\').EndsWith(@":\", StringComparison.OrdinalIgnoreCase));

        BuildUi();
        Shown += async (_, _) => await RunScanAsync();
    }

    private void BuildUi()
    {
        Text = "Remove — impact preview";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(640, 500);
        MinimumSize = new Size(560, 420);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(true);

        var header = new Label
        {
            Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 8, 0),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoEllipsis = true,
            Text = _title + "\n" + _target,
        };

        _details.Multiline = true; _details.ReadOnly = true; _details.ScrollBars = ScrollBars.Both; _details.WordWrap = false;
        _details.Dock = DockStyle.Fill;
        _details.BorderStyle = BorderStyle.None; _details.BackColor = Color.White; _details.Font = new Font("Consolas", 9f);
        _details.Text = "Scanning what this removal would affect…";
        var outPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4), BackColor = Color.White };
        outPanel.Controls.Add(_details);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 92 };
        _killLockers.Text = "Close any programs currently using it first (force-close before removing)";
        _killLockers.AutoSize = true; _killLockers.Location = new Point(12, 6);

        _confirmLbl.Text = "This is a protected/system path. Type REMOVE to enable removal:";
        _confirmLbl.AutoSize = true; _confirmLbl.ForeColor = IconFactory.Blocked; _confirmLbl.Location = new Point(12, 30);
        _confirmBox.SetBounds(12, 50, 160, 24);
        _confirmBox.TextChanged += (_, _) => UpdateRemoveEnabled();
        _confirmLbl.Visible = _confirmBox.Visible = _protected;

        _ai.Text = "AI impact report"; _ai.SetBounds(190, 52, 130, 30);
        _ai.Click += (_, _) => ShowAiReport();

        _remove.Text = "Quarantine && remove"; _remove.SetBounds(330, 52, 160, 34);
        _remove.BackColor = IconFactory.Blocked; _remove.ForeColor = Color.White; _remove.FlatStyle = FlatStyle.Flat;
        _remove.Click += (_, _) => DoRemove();

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(498, 52, 120, 34);
        CancelButton = cancel;

        bar.Controls.AddRange(new Control[] { _killLockers, _confirmLbl, _confirmBox, _ai, _remove, cancel });

        Controls.Add(outPanel);
        Controls.Add(header);
        Controls.Add(bar);
        UpdateRemoveEnabled();
    }

    private void UpdateRemoveEnabled()
    {
        _remove.Enabled = !_protected || string.Equals(_confirmBox.Text.Trim(), "REMOVE", StringComparison.Ordinal);
    }

    private async System.Threading.Tasks.Task RunScanAsync()
    {
        if (_target.Length == 0)
        {
            _details.Text = "No install folder is recorded for this item.\r\nOnly its registry entry and official uninstaller (if any) will be used.";
            return;
        }
        _remove.Enabled = false;
        var target = _target;
        var scan = await System.Threading.Tasks.Task.Run(() => ImpactScanner.Scan(target));
        _scan = scan;
        _details.Text = Describe(scan);
        _details.Select(0, 0);
        UpdateRemoveEnabled();
    }

    private string Describe(ImpactScanner.Result s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("FILES TO REMOVE");
        sb.AppendLine($"  {s.FileCount}{(s.Truncated ? "+ (scan truncated)" : "")} file(s), total {Fmt.Bytes(s.TotalBytes)}");
        sb.AppendLine();

        sb.AppendLine("RUNNING PROGRAMS CURRENTLY USING IT");
        if (s.Processes.Count == 0) sb.AppendLine("  (none detected — but boot/delay-loaded uses can't always be seen)");
        else foreach (var g in s.Processes.GroupBy(p => p.Name).OrderBy(g => g.Key))
                sb.AppendLine($"  • {g.Key}  (PID {string.Join(", ", g.Select(p => p.Pid).Distinct().Take(6))})");
        sb.AppendLine();

        sb.AppendLine("WINDOWS SERVICES WITH A BINARY HERE");
        if (s.Services.Count == 0) sb.AppendLine("  (none detected)");
        else foreach (var svc in s.Services.Take(30)) sb.AppendLine($"  • {svc}");
        sb.AppendLine();

        if (s.SampleFiles.Count > 0)
        {
            sb.AppendLine($"SAMPLE FILES ({Math.Min(s.SampleFiles.Count, 100)} shown)");
            foreach (var f in s.SampleFiles.Take(100)) sb.AppendLine("  " + f);
            sb.AppendLine();
        }

        sb.AppendLine("NOTE");
        sb.AppendLine("  Removed items are moved to quarantine and can be restored from the Installed tab.");
        sb.AppendLine("  Locked files that can't be moved are scheduled for deletion on next reboot (NOT reversible).");
        if (_protected) sb.AppendLine("  ⚠ This is a protected/system path — removing it can break Windows or installed software.");
        return sb.ToString().Replace("\n", "\r\n");
    }

    private void ShowAiReport()
    {
        var scan = _scan ?? new ImpactScanner.Result();
        using var f = new AiReportForm(_ctx, "Removal impact", AppInspector.BuildImpactPrompt(_target.Length > 0 ? _target : _title, scan));
        f.ShowDialog(this);
    }

    private void DoRemove()
    {
        if (MessageBox.Show(this,
                $"Remove:\n{_target}\n\nItems are quarantined so you can restore them. Continue?",
                "KillSwitch — confirm removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        Enabled = false;
        Cursor = Cursors.WaitCursor;
        string msg;
        try
        {
            var batch = Quarantine.NewBatch(_title + " — " + _target);
            msg = _removeAction(batch, _killLockers.Checked);
            Quarantine.Save(batch);
        }
        catch (Exception ex) { msg = "Removal failed: " + ex.Message; }
        finally { Cursor = Cursors.Default; Enabled = true; }

        MessageBox.Show(this, msg, "KillSwitch — removal result", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }
}
