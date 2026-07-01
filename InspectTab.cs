namespace KillSwitch;

/// <summary>HTTPS inspector tab — opt-in MITM decryption with a clear warning + cert controls.</summary>
public sealed class InspectTab : UserControl
{
    private readonly KillSwitchContext _ctx;
    private readonly CheckBox _enable = new();
    private readonly Label _status = new();
    private readonly TextBox _filter = new();
    private readonly FastListView _list = new();
    private readonly TextBox _detail = new();
    private readonly System.Windows.Forms.Timer _t = new() { Interval = 1000 };
    private bool _loading;

    public InspectTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        _t.Tick += (_, _) => Reload();
        VisibleChanged += (_, _) =>
        {
            if (Visible) { _loading = true; _enable.Checked = _ctx.MitmRunning; _loading = false; UpdateStatus(); _t.Start(); Reload(); }
            else _t.Stop();
        };
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        var top = new Panel { Dock = DockStyle.Top, Height = 80 };
        _enable.Text = "Enable HTTPS inspection (decrypts traffic — installs a root certificate)";
        _enable.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _enable.SetBounds(2, 4, 600, 22);
        _enable.CheckedChanged += (_, _) => { if (!_loading) ToggleMitm(); };

        var warn = new Label
        {
            Text = "⚠ Invasive: installs a KillSwitch root cert and routes traffic through a local proxy so it can be read. "
                 + "May break cert-pinned apps (banking/Teams) while on. Off by default; turning it off or exiting restores normal traffic.",
            ForeColor = Color.FromArgb(170, 80, 0), AutoSize = false,
        };
        warn.SetBounds(2, 26, 900, 30);

        var removeCert = new Button { Text = "Remove certificate", Width = 140, Height = 26, Location = new Point(2, 50) };
        removeCert.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Remove the KillSwitch root certificate from your trust store?", "KillSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            { _ctx.Mitm.RemoveCertificate(); MessageBox.Show(this, "Certificate removed.", "KillSwitch"); }
        };
        var clear = new Button { Text = "Clear", Width = 70, Height = 26, Location = new Point(148, 50) };
        clear.Click += (_, _) => { _ctx.Mitm.Clear(); Reload(); };
        var flbl = new Label { Text = "Filter:", AutoSize = true, Location = new Point(232, 54) };
        _filter.SetBounds(276, 51, 200, 24);
        _filter.PlaceholderText = "host / url / method";
        _filter.TextChanged += (_, _) => Reload();
        _status.SetBounds(488, 54, 420, 18);
        _status.ForeColor = Color.Gray;

        top.Controls.AddRange(new Control[] { _enable, warn, removeCert, clear, flbl, _filter, _status });

        _list.Columns.Add("Time", 90);
        _list.Columns.Add("Method", 60);
        _list.Columns.Add("Status", 60, HorizontalAlignment.Right);
        _list.Columns.Add("Host", 180);
        _list.Columns.Add("URL", 340);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Type", 140);
        _list.Dock = DockStyle.Fill;
        _list.SelectedIndexChanged += (_, _) => ShowDetail();

        _detail.Multiline = true; _detail.ReadOnly = true; _detail.ScrollBars = ScrollBars.Both;
        _detail.WordWrap = false; _detail.Dock = DockStyle.Bottom; _detail.Height = 180;
        _detail.BackColor = Color.White; _detail.Font = new Font("Consolas", 9f);

        Controls.Add(_list);
        Controls.Add(_detail);
        Controls.Add(top);
    }

    private void ToggleMitm()
    {
        if (_enable.Checked && !_ctx.MitmRunning)
        {
            var ans = MessageBox.Show(this,
                "Enable HTTPS inspection?\n\nThis installs a KillSwitch root certificate into your trust store and routes traffic " +
                "through a local proxy so it can be decrypted and shown here.\n\n" +
                "• It can break apps that pin certificates (some banking apps, Teams) while active.\n" +
                "• Turn it off (or exit KillSwitch) to stop and restore normal traffic.\n\nProceed?",
                "KillSwitch — HTTPS inspection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ans != DialogResult.Yes) { _loading = true; _enable.Checked = false; _loading = false; return; }
            var err = _ctx.EnableMitm(true);
            if (err != null) { MessageBox.Show(this, "Could not start HTTPS inspection:\n" + err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning); _loading = true; _enable.Checked = false; _loading = false; }
        }
        else if (!_enable.Checked && _ctx.MitmRunning)
        {
            _ctx.EnableMitm(false);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _status.Text = _ctx.MitmRunning ? "● Decrypting via local proxy (127.0.0.1)" : "Off — traffic not inspected.";
        _status.ForeColor = _ctx.MitmRunning ? IconFactory.Blocked : Color.Gray;
    }

    private void Reload()
    {
        if (!Visible) return;
        UpdateStatus();
        var f = _filter.Text.Trim();
        var txns = _ctx.Mitm.Snapshot();
        IEnumerable<HttpTxn> q = txns;
        if (f.Length > 0)
            q = q.Where(t => t.Host.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || t.Url.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || t.Method.Contains(f, StringComparison.OrdinalIgnoreCase));
        var list = q.TakeLast(300).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in list)
        {
            var it = new ListViewItem(t.Time.ToString("HH:mm:ss")) { Tag = t };
            it.SubItems.Add(t.Method);
            it.SubItems.Add(t.Status > 0 ? t.Status.ToString() : "");
            it.SubItems.Add(t.Host);
            it.SubItems.Add(t.Url);
            it.SubItems.Add(t.RespLength > 0 ? Fmt.Bytes(t.RespLength) : "");
            it.SubItems.Add(t.RespContentType);
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.EnsureVisible(_list.Items.Count - 1);
    }

    private void ShowDetail()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not HttpTxn t) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{t.Method} {t.Url}");
        sb.AppendLine($"Host: {t.Host}");
        if (t.ReqContentType.Length > 0) sb.AppendLine($"Request-Content-Type: {t.ReqContentType}");
        if (t.ReqBody.Length > 0) { sb.AppendLine(); sb.AppendLine("--- REQUEST BODY ---"); sb.AppendLine(t.ReqBody); }
        sb.AppendLine();
        sb.AppendLine($"Status: {t.Status}   {t.RespContentType}   {(t.RespLength > 0 ? Fmt.Bytes(t.RespLength) : "")}");
        if (t.RespBody.Length > 0) { sb.AppendLine(); sb.AppendLine("--- RESPONSE BODY ---"); sb.AppendLine(t.RespBody); }
        _detail.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
