namespace KillSwitch;

/// <summary>A ListView that doesn't flicker when rebuilt every second.</summary>
public sealed class FastListView : ListView
{
    public FastListView()
    {
        View = View.Details;
        FullRowSelect = true;
        GridLines = false;
        DoubleBuffered = true;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Surface2;
        ForeColor = Theme.Text;
        OwnerDraw = true;
    }

    /// <summary>Stretch the last column to fill any empty space so there's no light gap on the right.</summary>
    public bool AutoFillLastColumn { get; set; } = true;

    private void FillLastColumn()
    {
        if (!AutoFillLastColumn || Columns.Count == 0 || !IsHandleCreated) return;
        int used = 0;
        for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
        int avail = ClientSize.Width - used - 2;
        if (avail > 48) Columns[^1].Width = avail;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); FillLastColumn(); }
    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); FillLastColumn(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); FillLastColumn(); }
    protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        if (AutoFillLastColumn && e.ColumnIndex != Columns.Count - 1) FillLastColumn(); // guard prevents recursion
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var bg = new SolidBrush(Theme.HeaderBg)) e.Graphics.FillRectangle(bg, e.Bounds);
        using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding;
        if (e.Header != null)
        {
            if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
            else if (e.Header.TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, e.Bounds, Theme.TextDim, flags);
        }
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e) => e.DrawDefault = false; // cells drawn in OnDrawSubItem

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null) { e.DrawDefault = true; return; }
        bool selected = e.Item.Selected;
        using (var bg = new SolidBrush(selected ? Theme.Selection : Theme.Surface2))
            e.Graphics.FillRectangle(bg, e.Bounds);

        // respect per-row colors (e.g. red "locked"); otherwise use the theme text color
        var color = (e.Item.ForeColor.ToArgb() == Theme.Text.ToArgb() || e.Item.ForeColor.ToArgb() == SystemColors.WindowText.ToArgb())
            ? Theme.Text : e.Item.ForeColor;

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding;
        var align = e.Header?.TextAlign ?? HorizontalAlignment.Left;
        if (align == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
        else if (align == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;

        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, e.Bounds, color, flags);
    }
}

/// <summary>The "Traffic" tab: live per-app usage (top) and a live packet feed (bottom).</summary>
public sealed class TrafficTab : UserControl
{
    private readonly KillSwitchContext _ctx;
    private readonly TrafficMonitor _monitor = new();

    private readonly ComboBox _engine = new();
    private readonly CheckBox _capture = new();
    private readonly CheckBox _freeze = new();
    private bool _frozen;
    private readonly TextBox _filter = new();
    private int _sortCol = 2;   // Down/s
    private bool _sortAsc;
    private readonly Label _status = new();
    private readonly FastListView _apps = new();
    private readonly FastListView _packets = new();
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 1000 };
    private readonly ContextMenuStrip _appMenu = new();
    private readonly ContextMenuStrip _pktMenu = new();
    private ISet<string> _blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private ISet<string> _allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private ISet<string> _safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private ISet<string> _locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private ISet<string> _blockedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public TrafficTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        _ui.Tick += (_, _) =>
        {
            if (_monitor.Running) _ctx.RecordUsage(_monitor.SnapshotApps()); // ledger keeps logging even when frozen
            if (!_frozen) Refresh2();
        };
        VisibleChanged += (_, _) => OnVisibilityChanged();
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        // ---- top bar ----
        var bar = new Panel { Dock = DockStyle.Top, Height = 36 };
        _engine.DropDownStyle = ComboBoxStyle.DropDownList;
        _engine.Items.AddRange(new object[] { "Raw sockets (no driver)", "Npcap (Wireshark driver)" });
        _engine.SelectedIndex = 0;
        _engine.SetBounds(0, 6, 200, 24);
        _engine.SelectedIndexChanged += (_, _) => Restart();

        _capture.Text = "Capture";
        _capture.Checked = true;
        _capture.SetBounds(212, 8, 80, 22);
        _capture.CheckedChanged += (_, _) => Restart();

        _freeze.Text = "Freeze view";
        _freeze.SetBounds(296, 8, 100, 22);
        _freeze.CheckedChanged += (_, _) => { _frozen = _freeze.Checked; UpdateStatus(); if (!_frozen) Refresh2(); };

        var flbl = new Label { Text = "Filter:", AutoSize = true };
        flbl.SetBounds(402, 11, 40, 18);
        _filter.SetBounds(444, 8, 150, 24);
        _filter.PlaceholderText = "name / path / site";
        _filter.TextChanged += (_, _) => { if (!_frozen) Refresh2(); };

        _status.SetBounds(604, 9, 300, 20);
        _status.ForeColor = Color.Gray;

        bar.Controls.AddRange(new Control[] { _engine, _capture, _freeze, flbl, _filter, _status });

        // ---- apps grid (top half) ----
        _apps.Columns.Add("Application", 180);
        _apps.Columns.Add("PID", 60, HorizontalAlignment.Right);
        _apps.Columns.Add("Down/s", 90, HorizontalAlignment.Right);
        _apps.Columns.Add("Up/s", 90, HorizontalAlignment.Right);
        _apps.Columns.Add("Down", 90, HorizontalAlignment.Right);
        _apps.Columns.Add("Up", 90, HorizontalAlignment.Right);
        _apps.Columns.Add("Conns", 60, HorizontalAlignment.Right);
        _apps.Columns.Add("Sites", 200);
        _apps.Columns.Add("Status", 90);
        _apps.Dock = DockStyle.Fill;
        _apps.HideSelection = false;
        _apps.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                var hit = _apps.HitTest(e.Location);
                if (hit.Item != null) hit.Item.Selected = true;
            }
        };
        _apps.ContextMenuStrip = _appMenu;
        _apps.DoubleClick += (_, _) => ShowDestinations();
        _appMenu.Opening += BuildAppMenu;
        _apps.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortCol) _sortAsc = !_sortAsc; else { _sortCol = e.Column; _sortAsc = false; }
            Refresh2();
        };

        // ---- packet feed (bottom half) ----
        _packets.Columns.Add("Time", 90);
        _packets.Columns.Add("", 30); // direction arrow
        _packets.Columns.Add("Proto", 60);
        _packets.Columns.Add("Process", 140);
        _packets.Columns.Add("Local", 170);
        _packets.Columns.Add("Remote", 190);
        _packets.Columns.Add("Bytes", 70, HorizontalAlignment.Right);
        _packets.Dock = DockStyle.Fill;

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 250,
        };

        // Panel1: label / apps grid / action buttons (deterministic layout)
        var p1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        p1.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        p1.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        p1.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        p1.Controls.Add(new Label { Text = "Apps using the network — select one, then use the buttons below (or right-click)", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 0);
        p1.Controls.Add(_apps, 0, 1);

        var appBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var btnBlock = new Button { Text = "Block selected", Width = 120, Height = 30, BackColor = IconFactory.Blocked, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        btnBlock.Click += (_, _) => BlockSelected();
        var btnUnblock = new Button { Text = "Unblock", Width = 90, Height = 30 };
        btnUnblock.Click += (_, _) => UnblockSelected();
        var btnAllow = new Button { Text = "Allow selected", Width = 120, Height = 30 };
        btnAllow.Click += (_, _) => AllowSelected();
        var btnSafe = new Button { Text = "Mark safe", Width = 100, Height = 30 };
        btnSafe.Click += (_, _) => MarkSafeSelected();
        var btnInfo = new Button { Text = "More info ⧉", Width = 110, Height = 30 };
        btnInfo.Click += (_, _) => WebSearchSelected();
        var btnAi = new Button { Text = "Analyze (AI)", Width = 100, Height = 30 };
        btnAi.Click += (_, _) => AnalyzeSelected();
        var btnBlockedIps = new Button { Text = "Blocked IPs…", Width = 110, Height = 30 };
        btnBlockedIps.Click += (_, _) => OpenBlockedIps();
        var btnLock = new Button { Text = "Lock app", Width = 90, Height = 30 };
        btnLock.Click += (_, _) => LockSelected();
        var btnDests = new Button { Text = "Destinations…", Width = 120, Height = 30 };
        btnDests.Click += (_, _) => ShowDestinations();
        appBtns.Controls.AddRange(new Control[] { btnBlock, btnUnblock, btnAllow, btnSafe, btnDests, btnInfo, btnAi, btnLock, btnBlockedIps });
        p1.Controls.Add(appBtns, 0, 2);

        _packets.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                var h = _packets.HitTest(e.Location);
                if (h.Item != null) h.Item.Selected = true;
            }
        };
        _packets.ContextMenuStrip = _pktMenu;
        _pktMenu.Opening += BuildPacketMenu;
        split.Panel1.Controls.Add(p1);

        split.Panel2.Controls.Add(_packets);
        split.Panel2.Controls.Add(new Label { Text = "Live packets (newest at bottom)", Dock = DockStyle.Top, Height = 18, ForeColor = Color.DimGray });

        Controls.Add(split);
        Controls.Add(bar);
    }

    private void OnVisibilityChanged()
    {
        if (Visible) { Restart(); _ui.Start(); }
        else { _ui.Stop(); _monitor.Stop(); }
    }

    private void Restart()
    {
        _monitor.Stop();
        if (!Visible || !_capture.Checked) { UpdateStatus(); return; }

        var engine = _engine.SelectedIndex == 1 ? CaptureEngine.Npcap : CaptureEngine.RawSocket;
        if (engine == CaptureEngine.Npcap && !NpcapCapture.IsInstalled())
        {
            var ans = MessageBox.Show(this,
                "Npcap isn't installed. It's the free third-party driver Wireshark uses, for full link-layer capture.\n\n" +
                "Open the Npcap download page now? After installing, re-select Npcap here.\n\n" +
                "For now KillSwitch will keep using raw sockets (no driver needed).",
                "KillSwitch — Npcap", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ans == DialogResult.Yes)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true }); } catch { }
            _engine.SelectedIndex = 0; // revert to raw sockets (re-triggers Restart)
            return;
        }
        _monitor.Start(engine);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_capture.Checked) { _status.Text = "Paused."; _status.ForeColor = Color.Gray; return; }
        if (_frozen) { _status.Text = "❄ View frozen — still capturing & logging. Uncheck to resume."; _status.ForeColor = Color.FromArgb(40, 110, 200); return; }
        if (_monitor.LastError != null)
        {
            _status.Text = "Capture error: " + _monitor.LastError;
            _status.ForeColor = IconFactory.Blocked;
            return;
        }
        _status.Text = _monitor.Running ? "Capturing via " + _monitor.EngineName : "Stopped.";
        _status.ForeColor = Color.Gray;
    }

    private List<AppUsage> FilterApps(List<AppUsage> apps)
    {
        var f = _filter.Text.Trim();
        if (f.Length == 0) return apps;
        return apps.Where(a =>
            (a.Name?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (a.ExePath?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (a.Domains?.Any(d => d.Contains(f, StringComparison.OrdinalIgnoreCase)) ?? false)
        ).ToList();
    }

    private static List<AppUsage> SortApps(List<AppUsage> apps, int col, bool asc)
    {
        Func<AppUsage, IComparable> key = col switch
        {
            0 => a => a.Name ?? "",
            1 => a => a.Pid,
            2 => a => a.RateIn,
            3 => a => a.RateOut,
            4 => a => a.BytesIn,
            5 => a => a.BytesOut,
            6 => a => a.Connections,
            7 => a => a.Domains?.Count ?? 0,
            _ => a => a.Name ?? "",
        };
        return (asc ? apps.OrderBy(key) : apps.OrderByDescending(key)).ToList();
    }

    private static string SitesLabel(AppUsage a)
    {
        if (a.Domains == null || a.Domains.Count == 0) return "";
        var first = a.Domains.First();
        return a.Domains.Count == 1 ? first : $"{first}  (+{a.Domains.Count - 1})";
    }

    private AppUsage? SelectedApp()
        => _apps.SelectedItems.Count > 0 ? _apps.SelectedItems[0].Tag as AppUsage : null;

    private bool RequireSelectedWithPath(out AppUsage app)
    {
        app = SelectedApp()!;
        if (app == null)
        {
            MessageBox.Show(this, "Select an app in the list first.", "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (string.IsNullOrEmpty(app.ExePath))
        {
            MessageBox.Show(this, $"{app.Name} is a system/protected process with no exe path, so it can't be blocked by path.",
                "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return true;
    }

    private void BlockSelected()
    {
        var a = SelectedApp();
        if (a == null) { Info("Select an app in the list first."); return; }
        if (!string.IsNullOrEmpty(a.ExePath)) { DoBlock(a); return; }
        BlockByDestinations(a); // ownerless/unattributed → block where it's going
    }

    private void UnblockSelected()
    {
        var a = SelectedApp();
        if (a == null) { Info("Select an app in the list first."); return; }
        if (!string.IsNullOrEmpty(a.ExePath)) { DoUnblock(a); return; }
        foreach (var ip in Routable(a).Where(_blockedIps.Contains).ToList()) _ctx.UnblockIp(ip);
        _blockedIps = _ctx.BlockedIpSet();
        Refresh2();
    }

    private void AllowSelected() { if (RequireSelectedWithPath(out var a)) DoAllow(a); }
    private void MarkSafeSelected() { if (RequireSelectedWithPath(out var a)) DoToggleSafe(a); }

    private void LockSelected()
    {
        var a = SelectedApp();
        if (a == null) { Info("Select an app first."); return; }
        if (string.IsNullOrEmpty(a.ExePath)) { Info($"{a.Name} has no exe path to lock."); return; }
        if (_locked.Contains(a.ExePath)) { _ctx.UnlockApp(a.ExePath!); _locked = _ctx.LockedAppPaths(); Refresh2(); return; }
        if (MessageBox.Show(this,
                $"Force-close \"{a.Name}\" and block it from running until you unlock it?\n\n{a.ExePath}\n\n" +
                "Unlock later from Apps → Locked apps… (a locked app vanishes from this list because it's closed).",
                "KillSwitch — lock app", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var err = _ctx.LockApp(a.ExePath!, a.Name);
        if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _locked = _ctx.LockedAppPaths();
        Refresh2();
    }

    private void BlockByDestinations(AppUsage app)
    {
        var ips = Routable(app);
        if (ips.Count == 0)
        {
            Info($"{app.Name} has no program path and no recorded destinations yet.\n\nLet its traffic flow for a moment and retry, right-click a packet below to block its destination IP, or use Cut Internet to block everything.");
            return;
        }
        var preview = string.Join("\n", ips.Take(15));
        if (ips.Count > 15) preview += $"\n…and {ips.Count - 15} more";
        var ans = MessageBox.Show(this,
            $"{app.Name} can't be blocked by program (no exe path), so KillSwitch will block the {ips.Count} destination IP(s) it's talking to:\n\n{preview}\n\nProceed?",
            "KillSwitch — block by destination", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ans != DialogResult.Yes) return;
        int n = _ctx.BlockIps(ips);
        _blockedIps = _ctx.BlockedIpSet();
        Refresh2();
        Info($"Blocked {n} destination IP(s). Manage them via \"Blocked IPs…\".");
    }

    private static List<string> Routable(AppUsage app)
        => (app.Remotes ?? new HashSet<string>()).Where(IsRoutable).Distinct().ToList();

    private static bool IsRoutable(string ip)
    {
        if (string.IsNullOrEmpty(ip) || ip is "0.0.0.0" or "::" or "::1" or "255.255.255.255") return false;
        if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) return false;
        if (int.TryParse(ip.Split('.')[0], out int o) && o >= 224 && o <= 239) return false; // multicast
        return true;
    }

    private void Info(string msg) => MessageBox.Show(this, msg, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void WebSearchSelected()
    {
        var a = SelectedApp();
        if (a == null) { Info("Select an app in the list first."); return; }
        string file = "";
        try { if (!string.IsNullOrEmpty(a.ExePath) && a.ExePath != "System") file = System.IO.Path.GetFileName(a.ExePath); } catch { }
        string q = $"{a.Name} {file} Windows process — what is it, who makes it, is it safe to block from the internet";
        OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(q));
    }

    private void OpenBlockedIps()
    {
        using var f = new BlockedIpsForm(_ctx);
        f.ShowDialog(this);
        _blockedIps = _ctx.BlockedIpSet();
        Refresh2();
    }

    private void ShowDestinations()
    {
        var a = SelectedApp();
        if (a == null) { Info("Select an app in the list first."); return; }
        using var f = new DestinationsForm(_ctx, _monitor, a.Pid, a.Name);
        f.ShowDialog(this);
        _blockedIps = _ctx.BlockedIpSet();
        Refresh2();
    }

    private void AnalyzeSelected()
    {
        var a = SelectedApp();
        if (a == null) { Info("Select an app first."); return; }
        using var f = new AiAnalysisForm(_ctx, a);
        f.ShowDialog(this);
        _blocked = _ctx.BlockedAppPaths();
        _safe = _ctx.SafeAppPaths();
        _blockedIps = _ctx.BlockedIpSet();
        Refresh2();
    }

    private void ScheduleSelected()
    {
        if (!RequireSelectedWithPath(out var a)) return;
        using var f = new AppScheduleForm(_ctx, a);
        f.ShowDialog(this);
        Refresh2();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void BuildPacketMenu(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _pktMenu.Items.Clear();
        if (_packets.SelectedItems.Count == 0 || _packets.SelectedItems[0].Tag is not string ip || string.IsNullOrEmpty(ip))
        { e.Cancel = true; return; }

        if (_blockedIps.Contains(ip))
            _pktMenu.Items.Add(new ToolStripMenuItem($"Unblock destination {ip}", null, (_, _) => { _ctx.UnblockIp(ip); _blockedIps = _ctx.BlockedIpSet(); }));
        else
            _pktMenu.Items.Add(new ToolStripMenuItem($"Block destination {ip}", null, (_, _) =>
            {
                var err = _ctx.BlockIp(ip);
                if (err != null) Info(err);
                _blockedIps = _ctx.BlockedIpSet();
            }));
        _pktMenu.Items.Add(new ToolStripSeparator());
        string? host = _monitor.ResolveHost(ip);
        _pktMenu.Items.Add(new ToolStripMenuItem("Analyze this destination (AI report)…", null, (_, _) =>
        {
            string capturedIp = ip;
            using var f = new AiReportForm(_ctx, host ?? ip, AppInspector.BuildIpPrompt(ip, host),
                block: () => { _ctx.BlockIp(capturedIp); _blockedIps = _ctx.BlockedIpSet(); }, blockLabel: "Block this IP");
            f.ShowDialog(this);
        }));
        if (!string.IsNullOrEmpty(host))
            _pktMenu.Items.Add(new ToolStripMenuItem($"Analyze site \"{host}\" (AI report)…", null, (_, _) =>
            {
                using var f = new AiReportForm(_ctx, host!, AppInspector.BuildDomainPrompt(host!));
                f.ShowDialog(this);
            }));
        _pktMenu.Items.Add(new ToolStripMenuItem("Search the web for this destination", null, (_, _) =>
            OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString("who owns IP address " + ip))));
        _pktMenu.Items.Add(new ToolStripMenuItem("Copy IP", null, (_, _) => { try { Clipboard.SetText(ip); } catch { } }));
    }

    private void DoToggleSafe(AppUsage app)
    {
        if (_safe.Contains(app.ExePath!)) _ctx.RemoveSafeApp(app.ExePath!);
        else _ctx.AddSafeApp(app.ExePath!, app.Name);
        _safe = _ctx.SafeAppPaths();
        Refresh2();
    }

    private void BuildAppMenu(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _appMenu.Items.Clear();
        var app = SelectedApp();
        if (app == null) { e.Cancel = true; return; }

        _appMenu.Items.Add(new ToolStripMenuItem("Show destinations (who's receiving data)…", null, (_, _) => ShowDestinations()));
        _appMenu.Items.Add(new ToolStripSeparator());

        if (string.IsNullOrEmpty(app.ExePath))
        {
            _appMenu.Items.Add(new ToolStripMenuItem($"{app.Name}: system/protected — can't block by path") { Enabled = false });
            return;
        }

        bool blocked = _blocked.Contains(app.ExePath);
        if (blocked)
            _appMenu.Items.Add(new ToolStripMenuItem($"Unblock \"{app.Name}\"", null, (_, _) => DoUnblock(app)));
        else
            _appMenu.Items.Add(new ToolStripMenuItem($"Block \"{app.Name}\" — cut its internet", null, (_, _) => DoBlock(app)));

        if (_ctx.AllowlistMode)
        {
            _appMenu.Items.Add(new ToolStripSeparator());
            bool allowed = _allowed.Contains(app.ExePath);
            if (allowed)
                _appMenu.Items.Add(new ToolStripMenuItem($"Remove \"{app.Name}\" from allowlist", null, (_, _) => DoDisallow(app)));
            else
                _appMenu.Items.Add(new ToolStripMenuItem($"Allow \"{app.Name}\" (allowlist mode is on)", null, (_, _) => DoAllow(app)));
        }

        _appMenu.Items.Add(new ToolStripSeparator());
        if (_safe.Contains(app.ExePath))
            _appMenu.Items.Add(new ToolStripMenuItem($"Remove \"{app.Name}\" from safe list", null, (_, _) => DoToggleSafe(app)));
        else
            _appMenu.Items.Add(new ToolStripMenuItem($"Mark \"{app.Name}\" safe — bypass kill switch", null, (_, _) => DoToggleSafe(app)));

        _appMenu.Items.Add(new ToolStripSeparator());
        if (_locked.Contains(app.ExePath))
            _appMenu.Items.Add(new ToolStripMenuItem($"Unlock \"{app.Name}\" (allow running)", null, (_, _) => { _ctx.UnlockApp(app.ExePath!); _locked = _ctx.LockedAppPaths(); Refresh2(); }));
        else
            _appMenu.Items.Add(new ToolStripMenuItem($"Force-close & lock \"{app.Name}\" (block from running)", null, (_, _) => LockSelected()));

        _appMenu.Items.Add(new ToolStripSeparator());
        _appMenu.Items.Add(new ToolStripMenuItem("Analyze with Claude (AI)…", null, (_, _) => AnalyzeSelected()));
        _appMenu.Items.Add(new ToolStripMenuItem("Search the web for this app ⧉", null, (_, _) => WebSearchSelected()));
        _appMenu.Items.Add(new ToolStripMenuItem("Schedule for this app…", null, (_, _) => ScheduleSelected()));
        _appMenu.Items.Add(new ToolStripMenuItem("Copy exe path", null, (_, _) => { try { Clipboard.SetText(app.ExePath!); } catch { } }));
    }

    private void DoBlock(AppUsage app)
    {
        var err = _ctx.BlockApp(app.ExePath!, app.Name);
        if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _blocked = _ctx.BlockedAppPaths();
        Refresh2();
    }

    private void DoUnblock(AppUsage app)
    {
        var err = _ctx.UnblockApp(app.ExePath!);
        if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _blocked = _ctx.BlockedAppPaths();
        Refresh2();
    }

    private void DoAllow(AppUsage app)
    {
        _ctx.AllowApp(app.ExePath!, app.Name);
        _allowed = _ctx.AllowedAppPaths();
        Refresh2();
    }

    private void DoDisallow(AppUsage app)
    {
        _ctx.RemoveAllowedApp(app.ExePath!);
        _allowed = _ctx.AllowedAppPaths();
        Refresh2();
    }

    private void Refresh2()
    {
        if (!Visible || !_monitor.Running) return;

        // apps
        _blocked = _ctx.BlockedAppPaths();
        _allowed = _ctx.AllowedAppPaths();
        _safe = _ctx.SafeAppPaths();
        _locked = _ctx.LockedAppPaths();
        _blockedIps = _ctx.BlockedIpSet();
        bool allowlist = _ctx.AllowlistMode;
        var apps = SortApps(FilterApps(_monitor.SnapshotApps()), _sortCol, _sortAsc);
        _apps.BeginUpdate();
        _apps.Items.Clear();
        foreach (var a in apps)
        {
            bool blocked = a.ExePath != null && _blocked.Contains(a.ExePath);
            bool allowed = a.ExePath != null && _allowed.Contains(a.ExePath);
            bool safe = a.ExePath != null && _safe.Contains(a.ExePath);

            bool scheduled = a.ExePath != null && _ctx.HasActiveSchedule(a.ExePath);
            bool locked = a.ExePath != null && _locked.Contains(a.ExePath);

            string status; Color color = _apps.ForeColor;
            if (locked) { status = "⛔ locked"; color = IconFactory.Blocked; }
            else if (blocked) { status = "🔒 blocked"; color = IconFactory.Blocked; }
            else if (safe) { status = "🛡 safe"; color = IconFactory.Online; }
            else if (scheduled) { status = "⏰ scheduled"; color = Color.FromArgb(90, 90, 200); }
            else if (allowlist && allowed) { status = "✓ allowed"; color = IconFactory.Online; }
            else if (allowlist) { status = "⛔ not allowed"; color = Color.FromArgb(180, 90, 0); }
            else status = "";

            var it = new ListViewItem(a.Name);
            it.SubItems.Add(a.Pid.ToString());
            it.SubItems.Add(Fmt.Rate(a.RateIn));
            it.SubItems.Add(Fmt.Rate(a.RateOut));
            it.SubItems.Add(Fmt.Bytes(a.BytesIn));
            it.SubItems.Add(Fmt.Bytes(a.BytesOut));
            it.SubItems.Add(a.Connections.ToString());
            it.SubItems.Add(SitesLabel(a));
            it.SubItems.Add(status);
            it.ForeColor = color;
            it.Tag = a;
            _apps.Items.Add(it);
        }
        _apps.EndUpdate();

        // packets
        var pkts = _monitor.SnapshotRecent(200);
        _packets.BeginUpdate();
        _packets.Items.Clear();
        foreach (var p in pkts)
        {
            string rip = p.Remote.ToString();
            var it = new ListViewItem(p.Time.ToString("HH:mm:ss")) { Tag = rip };
            it.SubItems.Add(p.Outbound ? "▲" : "▼");
            it.SubItems.Add(p.Proto);
            it.SubItems.Add(p.Process);
            it.SubItems.Add($"{p.Local}:{p.LocalPort}");
            string host = _monitor.ResolveHost(rip) ?? rip;   // show hostname when DNS/SNI revealed it
            it.SubItems.Add(p.RemotePort > 0 ? $"{host}:{p.RemotePort}" : host);
            it.SubItems.Add(Fmt.Bytes(p.Length));
            if (_blockedIps.Contains(rip)) it.ForeColor = IconFactory.Blocked;
            _packets.Items.Add(it);
        }
        _packets.EndUpdate();
        if (_packets.Items.Count > 0) _packets.EnsureVisible(_packets.Items.Count - 1);

        UpdateStatus();
    }
}

/// <summary>Human-friendly byte/rate formatting.</summary>
public static class Fmt
{
    public static string Bytes(long b)
    {
        double v = b;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.0} {u[i]}";
    }

    public static string Rate(double bytesPerSec)
        => bytesPerSec < 1 ? "—" : Bytes((long)bytesPerSec) + "/s";
}
