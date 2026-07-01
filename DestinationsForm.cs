namespace KillSwitch;

/// <summary>
/// Shows every remote destination a single app is talking to, with how much we've
/// SENT to each (uploads) vs received. Lets the user block a specific IP — or every
/// IP currently behind a hostname ("block this site") — so data stops flowing to it.
/// Live: refreshes once a second from the traffic monitor.
/// </summary>
public sealed class DestinationsForm : Form
{
    private readonly KillSwitchContext _ctx;
    private readonly TrafficMonitor _monitor;
    private readonly int _pid;
    private readonly FastListView _list = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private ISet<string> _blockedIps;
    private int _sortCol = 2;   // Up (sent)
    private bool _sortAsc;

    public DestinationsForm(KillSwitchContext ctx, TrafficMonitor monitor, int pid, string appName)
    {
        _ctx = ctx; _monitor = monitor; _pid = pid;
        _blockedIps = _ctx.BlockedIpSet();

        Text = $"Destinations — {appName} (PID {pid})";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(780, 470);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(false);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            ForeColor = Color.DimGray,
            Padding = new Padding(6, 6, 6, 0),
            Text = "Sorted by data SENT (Up) — the top rows are receiving the most from you.\n" +
                   "Pick a row, then \"Block this IP\", or \"Block whole site\" (every IP seen for that hostname).",
        };

        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Site / host", 250);
        _list.Columns.Add("IP", 150);
        _list.Columns.Add("Up (sent)", 95, HorizontalAlignment.Right);
        _list.Columns.Add("Down", 95, HorizontalAlignment.Right);
        _list.Columns.Add("Pkts", 60, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 90);
        _list.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortCol) _sortAsc = !_sortAsc; else { _sortCol = e.Column; _sortAsc = false; }
            Reload();
        };

        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(4) };
        var bIp = new Button { Text = "Block this IP", Width = 110, Height = 30, BackColor = IconFactory.Blocked, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        bIp.Click += (_, _) => BlockIp();
        var bSite = new Button { Text = "Block whole site", Width = 130, Height = 30, BackColor = IconFactory.Blocked, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        bSite.Click += (_, _) => BlockSite();
        var bUn = new Button { Text = "Unblock", Width = 90, Height = 30 };
        bUn.Click += (_, _) => Unblock();
        var bAi = new Button { Text = "Analyze (AI)…", Width = 110, Height = 30 };
        bAi.Click += (_, _) => AnalyzeDest();
        var bWeb = new Button { Text = "Look up ⧉", Width = 100, Height = 30 };
        bWeb.Click += (_, _) => LookUp();
        var bClose = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.OK };
        btns.Controls.AddRange(new Control[] { bIp, bSite, bUn, bAi, bWeb, bClose });

        Controls.Add(_list);
        Controls.Add(btns);
        Controls.Add(hint);
        AcceptButton = bClose;
        Theme.Apply(this);

        Reload();
        _timer.Tick += (_, _) => Reload();
        _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
    }

    private DestUsage? Selected() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as DestUsage : null;

    private void Reload()
    {
        _blockedIps = _ctx.BlockedIpSet();
        var dests = _monitor.SnapshotDests(_pid);

        Func<DestUsage, IComparable> key = _sortCol switch
        {
            0 => d => d.Label.ToLowerInvariant(),
            1 => d => d.Ip,
            2 => d => d.BytesOut,
            3 => d => d.BytesIn,
            4 => d => d.Packets,
            _ => d => d.BytesOut,
        };
        dests = (_sortAsc ? dests.OrderBy(key) : dests.OrderByDescending(key)).ToList();

        string? selIp = Selected()?.Ip;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var d in dests)
        {
            bool blocked = _blockedIps.Contains(d.Ip);
            var it = new ListViewItem(d.Label) { Tag = d };
            it.SubItems.Add(d.Ip);
            it.SubItems.Add(Fmt.Bytes(d.BytesOut));
            it.SubItems.Add(Fmt.Bytes(d.BytesIn));
            it.SubItems.Add(d.Packets.ToString());
            it.SubItems.Add(blocked ? "🔒 blocked" : "");
            if (blocked) it.ForeColor = IconFactory.Blocked;
            if (d.Ip == selIp) it.Selected = true;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
    }

    private void BlockIp()
    {
        var d = Selected();
        if (d == null) { Info("Select a destination first."); return; }
        var err = _ctx.BlockIp(d.Ip);
        if (err != null) Info(err);
        Reload();
    }

    private void BlockSite()
    {
        var d = Selected();
        if (d == null) { Info("Select a destination first."); return; }
        if (string.IsNullOrEmpty(d.Host)) { BlockIp(); return; } // no hostname → just the IP

        var ips = _monitor.SnapshotDests(_pid)
            .Where(x => string.Equals(x.Host, d.Host, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Ip).Distinct().ToList();

        var ans = MessageBox.Show(this,
            $"Block all {ips.Count} IP(s) currently seen for \"{d.Host}\"?\n\n" +
            "Sites can rotate to new IPs over time — re-block from here if it reappears.",
            "KillSwitch — block site", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ans != DialogResult.Yes) return;

        int n = _ctx.BlockIps(ips);
        Reload();
        Info($"Blocked {n} IP(s) for {d.Host}.");
    }

    private void Unblock()
    {
        var d = Selected();
        if (d == null) { Info("Select a destination first."); return; }

        var ips = new List<string> { d.Ip };
        if (!string.IsNullOrEmpty(d.Host))
            ips.AddRange(_monitor.SnapshotDests(_pid)
                .Where(x => string.Equals(x.Host, d.Host, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Ip));
        foreach (var ip in ips.Distinct().Where(_blockedIps.Contains)) _ctx.UnblockIp(ip);
        Reload();
    }

    private void AnalyzeDest()
    {
        var d = Selected();
        if (d == null) { Info("Select a destination first."); return; }
        string capturedIp = d.Ip;
        string title = string.IsNullOrEmpty(d.Host) ? d.Ip : d.Host!;
        string prompt = string.IsNullOrEmpty(d.Host)
            ? AppInspector.BuildIpPrompt(d.Ip, null)
            : AppInspector.BuildIpPrompt(d.Ip, d.Host);
        using var f = new AiReportForm(_ctx, title, prompt,
            block: () => { _ctx.BlockIp(capturedIp); Reload(); }, blockLabel: "Block this IP");
        f.ShowDialog(this);
    }

    private void LookUp()
    {
        var d = Selected();
        if (d == null) { Info("Select a destination first."); return; }
        string q = string.IsNullOrEmpty(d.Host) ? "who owns IP address " + d.Ip : d.Host + " what website is this";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true });
        }
        catch { }
    }

    private void Info(string m) =>
        MessageBox.Show(this, m, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
