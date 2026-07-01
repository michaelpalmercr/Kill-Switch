using System.Net;

namespace KillSwitch;

/// <summary>
/// Machine-wide connections cockpit: every remote IP the PC is talking to, both directions.
/// "Outbound" = your data going out (who's receiving it); "Inbound" = who's reaching into the PC.
/// Per row: Block, Unblock, resolve Owner (WHOIS/RDAP), AI "who owns it / what they collect", web look-up.
/// Fed by the shared always-on monitor, so it records even when this tab is closed.
/// </summary>
public sealed class ConnectionsTab : UserControl
{
    private readonly KillSwitchContext _ctx;
    private readonly TrafficMonitor _monitor;
    private readonly FastListView _list = new();
    private readonly TextBox _filter = new();
    private readonly ComboBox _dir = new();
    private readonly CheckBox _showLocal = new();
    private readonly Label _summary = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };
    private ISet<string> _blockedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _resolving;

    private static readonly string[] ColNames = { "Dir", "Site / host", "IP", "Owner", "App(s)", "Out ↑", "In ↓", "Status" };
    private int _sortCol = 5;   // Out (data going out)
    private bool _sortAsc = false;

    public ConnectionsTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        _monitor = ctx.Monitor;
        BuildUi();
        _timer.Tick += (_, _) => Reload();
        VisibleChanged += (_, _) => { if (Visible) { Reload(); _timer.Start(); } else _timer.Stop(); };
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        var top = new Panel { Dock = DockStyle.Top, Height = 32 };
        var lbl = new Label { Text = "Every IP your PC is talking to. Outbound = your data going out; Inbound = who's reaching in.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(2, 7) };
        _dir.DropDownStyle = ComboBoxStyle.DropDownList;
        _dir.Items.AddRange(new object[] { "All", "Outbound (my data)", "Inbound (to my PC)", "Unsolicited inbound" });
        _dir.SelectedIndex = 0;
        _dir.SetBounds(560, 4, 160, 24);
        _dir.SelectedIndexChanged += (_, _) => Reload();
        _showLocal.Text = "Show local/LAN"; _showLocal.AutoSize = true; _showLocal.Location = new Point(732, 6);
        _showLocal.CheckedChanged += (_, _) => Reload();
        var flbl = new Label { Text = "Filter:", AutoSize = true, Location = new Point(858, 7) };
        _filter.SetBounds(902, 4, 150, 24);
        _filter.PlaceholderText = "host / ip / owner";
        _filter.TextChanged += (_, _) => Reload();
        top.Controls.AddRange(new Control[] { lbl, _dir, _showLocal, flbl, _filter });

        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Dir", 44, HorizontalAlignment.Center);
        _list.Columns.Add("Site / host", 240);
        _list.Columns.Add("IP", 140);
        _list.Columns.Add("Owner", 190);
        _list.Columns.Add("App(s)", 150);
        _list.Columns.Add("Out ↑", 90, HorizontalAlignment.Right);
        _list.Columns.Add("In ↓", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Status", 80);
        _list.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortCol) _sortAsc = !_sortAsc;
            else { _sortCol = e.Column; _sortAsc = e.Column is 1 or 2 or 3 or 4; } // text asc, numeric desc
            Reload();
        };
        _list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { var h = _list.HitTest(e.Location); if (h.Item != null) h.Item.Selected = true; } };
        _list.DoubleClick += (_, _) => AnalyzeAi();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(2) };
        Button B(string text, EventHandler onClick, Color? back = null)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(2, 4, 2, 4) };
            if (back is { } c) { b.BackColor = c; b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; }
            b.Click += onClick;
            bar.Controls.Add(b);
            return b;
        }
        B("Block IP", (_, _) => BlockIp(), IconFactory.Blocked);
        B("Block whole site", (_, _) => BlockSite(), IconFactory.Blocked);
        B("Unblock", (_, _) => Unblock());
        B("Who owns it (WHOIS)", (_, _) => ResolveSelected());
        B("AI report", (_, _) => AnalyzeAi());
        B("Look up ⧉", (_, _) => LookUp());
        B("Copy IP", (_, _) => { if (Sel() is { } e) { try { Clipboard.SetText(e.Ip); } catch { } } });
        B("Refresh", (_, _) => Reload());
        _summary.AutoSize = true; _summary.Margin = new Padding(10, 10, 2, 2); _summary.ForeColor = Color.DimGray;
        bar.Controls.Add(_summary);

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bar);
    }

    private RemoteEndpoint? Sel() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as RemoteEndpoint : null;

    private void Reload()
    {
        if (!Visible) return;
        _blockedIps = _ctx.BlockedIpSet();
        var f = _filter.Text.Trim();

        var list = _monitor.SnapshotAllDests()
            .Where(e => _showLocal.Checked || !IsLocal(e.Ip))
            .Where(e => _dir.SelectedIndex switch
            {
                1 => e.HasOutbound,
                2 => e.HasInbound,
                3 => e.InboundOnly,
                _ => true,
            })
            .Select(e => { if (WhoisLookup.TryGetCached(e.Ip, out var o)) e.Owner = o; return e; })
            .Where(e => f.Length == 0
                || e.Label.Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.Ip.Contains(f, StringComparison.OrdinalIgnoreCase)
                || (e.Owner ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.AppsLabel.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();

        list.Sort(Compare);
        for (int i = 0; i < _list.Columns.Count; i++)
            _list.Columns[i].Text = ColNames[i] + (i == _sortCol ? (_sortAsc ? "  ▲" : "  ▼") : "");

        string? selIp = Sel()?.Ip;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in list)
        {
            bool blocked = _blockedIps.Contains(e.Ip);
            var it = new ListViewItem(DirGlyph(e)) { Tag = e };
            it.SubItems.Add(e.Label);
            it.SubItems.Add(e.Ip);
            it.SubItems.Add(e.Owner ?? "");
            it.SubItems.Add(e.AppsLabel);
            it.SubItems.Add(e.BytesOut > 0 ? Fmt.Bytes(e.BytesOut) : "");
            it.SubItems.Add(e.BytesIn > 0 ? Fmt.Bytes(e.BytesIn) : "");
            it.SubItems.Add(blocked ? "🔒 blocked" : (e.InboundOnly ? "inbound" : ""));
            if (blocked) it.ForeColor = IconFactory.Blocked;
            else if (e.InboundOnly) it.ForeColor = Color.FromArgb(230, 165, 60); // highlight who's reaching in
            if (e.Ip == selIp) it.Selected = true;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _summary.Text = $"{list.Count} endpoints";
    }

    private static string DirGlyph(RemoteEndpoint e) => e.HasOutbound && e.HasInbound ? "↕" : e.HasOutbound ? "↑" : "↓";

    private int Compare(RemoteEndpoint a, RemoteEndpoint b)
    {
        int c = _sortCol switch
        {
            0 => a.BytesOut.CompareTo(b.BytesOut),
            1 => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase),
            2 => string.Compare(a.Ip, b.Ip, StringComparison.OrdinalIgnoreCase),
            3 => string.Compare(a.Owner ?? "", b.Owner ?? "", StringComparison.OrdinalIgnoreCase),
            4 => string.Compare(a.AppsLabel, b.AppsLabel, StringComparison.OrdinalIgnoreCase),
            5 => a.BytesOut.CompareTo(b.BytesOut),
            6 => a.BytesIn.CompareTo(b.BytesIn),
            7 => _blockedIps.Contains(a.Ip).CompareTo(_blockedIps.Contains(b.Ip)),
            _ => a.BytesOut.CompareTo(b.BytesOut),
        };
        if (c == 0) c = b.TotalBytes.CompareTo(a.TotalBytes);
        return _sortAsc ? c : -c;
    }

    private void BlockIp()
    {
        if (Sel() is not { } e) { Info("Select a connection first."); return; }
        var err = _ctx.BlockIp(e.Ip);
        if (err != null) Info(err);
        Reload();
    }

    private void BlockSite()
    {
        if (Sel() is not { } e) { Info("Select a connection first."); return; }
        if (string.IsNullOrEmpty(e.Host)) { BlockIp(); return; }
        var ips = _monitor.SnapshotAllDests()
            .Where(x => string.Equals(x.Host, e.Host, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Ip).Distinct().ToList();
        if (MessageBox.Show(this, $"Block all {ips.Count} IP(s) currently seen for \"{e.Host}\"?\n\nSites can rotate IPs — re-block from here if it reappears.",
                "KillSwitch — block site", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int n = _ctx.BlockIps(ips);
        Reload();
        Info($"Blocked {n} IP(s) for {e.Host}.");
    }

    private void Unblock()
    {
        if (Sel() is not { } e) { Info("Select a connection first."); return; }
        var ips = new List<string> { e.Ip };
        if (!string.IsNullOrEmpty(e.Host))
            ips.AddRange(_monitor.SnapshotAllDests().Where(x => string.Equals(x.Host, e.Host, StringComparison.OrdinalIgnoreCase)).Select(x => x.Ip));
        foreach (var ip in ips.Distinct().Where(_blockedIps.Contains)) _ctx.UnblockIp(ip);
        Reload();
    }

    private void AnalyzeAi()
    {
        if (Sel() is not { } e) { Info("Select a connection first."); return; }
        string ip = e.Ip;
        string title = string.IsNullOrEmpty(e.Host) ? e.Ip : e.Host!;
        using var f = new AiReportForm(_ctx, title, AppInspector.BuildIpPrompt(e.Ip, e.Host),
            block: () => { _ctx.BlockIp(ip); Reload(); }, blockLabel: "Block this IP");
        f.ShowDialog(this);
    }

    /// <summary>Resolve owners via RDAP for the current view (selected first, then the rest), then refresh.</summary>
    private async void ResolveSelected()
    {
        if (_resolving) return;
        var ips = new List<string>();
        if (Sel() is { } sel) ips.Add(sel.Ip);
        foreach (ListViewItem it in _list.Items)
            if (it.Tag is RemoteEndpoint e && !WhoisLookup.TryGetCached(e.Ip, out _)) ips.Add(e.Ip);
        ips = ips.Distinct().Take(40).ToList();
        if (ips.Count == 0) { Info("Owners already resolved for the visible rows."); return; }

        _resolving = true;
        _summary.Text = $"Resolving {ips.Count} owner(s) via WHOIS…";
        try
        {
            foreach (var ip in ips)
            {
                await WhoisLookup.ResolveAsync(ip);
                if (IsDisposed) return;
            }
        }
        finally { _resolving = false; }
        if (!IsDisposed) Reload();
    }

    private void LookUp()
    {
        if (Sel() is not { } e) { Info("Select a connection first."); return; }
        string q = string.IsNullOrEmpty(e.Host) ? "who owns IP address " + e.Ip : e.Host + " what website is this";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true }); } catch { }
    }

    private static bool IsLocal(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return false;
        if (IPAddress.IsLoopback(a)) return true;
        var b = a.GetAddressBytes();
        if (b.Length == 4)
        {
            if (b[0] == 10) return true;                         // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;         // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;         // link-local
            if (b[0] >= 224) return true;                        // multicast / broadcast
            if (b[0] == 0) return true;
        }
        else
        {
            if (a.IsIPv6LinkLocal || a.IsIPv6Multicast) return true;
            if (b.Length == 16 && (b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique-local
        }
        return false;
    }

    private void Info(string m) => MessageBox.Show(this, m, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
