using System.IO;
using System.Text;

namespace KillSwitch;

/// <summary>Persistent history of which apps sent/received data, how much, and when.</summary>
public sealed class LedgerTab : UserControl
{
    private readonly KillSwitchContext _ctx;
    private readonly FastListView _list = new();
    private readonly Label _summary = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly TextBox _filter = new();
    private int _sortCol = 3;   // Total
    private bool _sortAsc;
    private readonly System.Windows.Forms.Timer _t = new() { Interval = 2000 };

    // AI side panel
    private readonly TextBox _aiBox = new();
    private readonly ComboBox _aiProvider = new();
    private readonly CheckBox _aiAuto = new();
    private readonly Dictionary<string, string> _aiCache = new(StringComparer.OrdinalIgnoreCase);

    // usage graph
    private readonly UsageGraph _graph = new();
    private readonly ComboBox _frame = new();

    public LedgerTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        _t.Tick += (_, _) => Reload();
        VisibleChanged += (_, _) => { if (Visible) { Reload(); _t.Start(); } else _t.Stop(); };
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(10);

        var top = new Panel { Dock = DockStyle.Top, Height = 56 };
        var title = new Label
        {
            Text = "Traffic ledger — accumulates while the Traffic tab is capturing; persists across restarts.",
            ForeColor = Color.DimGray, AutoSize = false,
        };
        title.SetBounds(2, 2, 860, 18);
        _summary.SetBounds(2, 22, 540, 18);
        _summary.ForeColor = Color.Gray;

        var flbl = new Label { Text = "Filter:", AutoSize = true };
        flbl.SetBounds(556, 24, 40, 18);
        _filter.SetBounds(598, 21, 230, 24);
        _filter.PlaceholderText = "app / path / site";
        _filter.TextChanged += (_, _) => Reload();

        top.Controls.Add(title);
        top.Controls.Add(_summary);
        top.Controls.Add(flbl);
        top.Controls.Add(_filter);

        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Application", 200);
        _list.Columns.Add("Downloaded", 110, HorizontalAlignment.Right);
        _list.Columns.Add("Uploaded", 110, HorizontalAlignment.Right);
        _list.Columns.Add("Total", 110, HorizontalAlignment.Right);
        _list.Columns.Add("Sites", 230);
        _list.Columns.Add("First seen", 130);
        _list.Columns.Add("Last seen", 130);
        _list.Columns.Add("Path / key", 320);
        _list.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                var h = _list.HitTest(e.Location);
                if (h.Item != null) h.Item.Selected = true;
            }
        };
        _list.ContextMenuStrip = _menu;
        _menu.Opening += BuildMenu;
        _list.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortCol) _sortAsc = !_sortAsc; else { _sortCol = e.Column; _sortAsc = false; }
            Reload();
        };
        _list.SelectedIndexChanged += (_, _) => OnSelect();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var block = new Button { Text = "Block app", Width = 100, Height = 30, Location = new Point(2, 7), BackColor = IconFactory.Blocked, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        block.Click += (_, _) => DoBlock();
        var info = new Button { Text = "More info ⧉", Width = 100, Height = 30, Location = new Point(108, 7) };
        info.Click += (_, _) => DoWebSearch();
        var ai = new Button { Text = "Analyze (AI)", Width = 100, Height = 30, Location = new Point(214, 7) };
        ai.Click += (_, _) => DoAnalyze();
        var exportBtn = new Button { Text = "Export CSV…", Width = 110, Height = 30, Location = new Point(340, 7) };
        exportBtn.Click += (_, _) => ExportCsv();
        var refresh = new Button { Text = "Refresh", Width = 80, Height = 30, Location = new Point(454, 7) };
        refresh.Click += (_, _) => Reload();
        var clear = new Button { Text = "Clear ledger", Width = 110, Height = 30, Location = new Point(538, 7) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Erase all recorded history?", "KillSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            { _ctx.ClearLedger(); Reload(); }
        };
        bottom.Controls.AddRange(new Control[] { block, info, ai, exportBtn, refresh, clear });

        // ---- AI + graph side panel (right) ----
        var aiPanel = new Panel { Dock = DockStyle.Right, Width = 390, Padding = new Padding(8, 0, 2, 0) };

        // activity graph (top of the side panel)
        var graphPanel = new Panel { Dock = DockStyle.Top, Height = 200 };
        var gTop = new Panel { Dock = DockStyle.Top, Height = 28 };
        var gLbl = new Label { Text = "Activity", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        gLbl.SetBounds(2, 6, 60, 18);
        _frame.DropDownStyle = ComboBoxStyle.DropDownList;
        _frame.Items.AddRange(new object[] { "Last 24 hours", "Last 3 days", "Last 7 days" });
        _frame.SelectedIndex = 0;
        _frame.SetBounds(66, 3, 130, 24);
        _frame.SelectedIndexChanged += (_, _) => { var e = Selected(); if (e != null) UpdateGraph(e); };
        gTop.Controls.AddRange(new Control[] { gLbl, _frame });
        _graph.Dock = DockStyle.Fill;
        graphPanel.Controls.Add(_graph);
        graphPanel.Controls.Add(gTop);

        var aiTop = new Panel { Dock = DockStyle.Top, Height = 32 };
        var aiHdr = new Label { Text = "AI insight", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        aiHdr.SetBounds(2, 7, 80, 18);
        _aiProvider.DropDownStyle = ComboBoxStyle.DropDownList;
        _aiProvider.Items.AddRange(new object[] { "Claude", "Gemini" });
        _aiProvider.SelectedIndex = AiService.IsGemini(_ctx.Settings) ? 1 : 0;
        _aiProvider.SetBounds(84, 4, 96, 24);
        _aiProvider.SelectedIndexChanged += (_, _) =>
        {
            _ctx.Settings.AiProvider = _aiProvider.SelectedIndex == 1 ? "gemini" : "claude";
            _ctx.Settings.Save();
            var e = Selected(); if (e != null) { _aiCache.Remove(e.Key); RunAi(e); }
        };
        var keyBtn = new Button { Text = "Key", Width = 50, Height = 24, Location = new Point(186, 4) };
        keyBtn.Click += (_, _) => SetAiKey();
        aiTop.Controls.AddRange(new Control[] { aiHdr, _aiProvider, keyBtn });

        var aiBottom = new Panel { Dock = DockStyle.Bottom, Height = 32 };
        _aiAuto.Text = "Auto on click"; _aiAuto.Checked = true; _aiAuto.SetBounds(2, 7, 110, 22);
        var goBtn = new Button { Text = "Analyze", Width = 90, Height = 24, Location = new Point(118, 5) };
        goBtn.Click += (_, _) => { var e = Selected(); if (e != null) { _aiCache.Remove(e.Key); RunAi(e); } };
        aiBottom.Controls.AddRange(new Control[] { _aiAuto, goBtn });

        _aiBox.Multiline = true;
        _aiBox.ReadOnly = true;
        _aiBox.ScrollBars = ScrollBars.Vertical;
        _aiBox.Dock = DockStyle.Fill;
        _aiBox.BorderStyle = BorderStyle.FixedSingle;
        _aiBox.BackColor = Color.White;
        _aiBox.Text = "Select an app to get an AI verdict (what it is, who makes it, safe to block).";

        var aiSection = new Panel { Dock = DockStyle.Fill };
        aiSection.Controls.Add(_aiBox);
        aiSection.Controls.Add(aiBottom);
        aiSection.Controls.Add(aiTop);

        aiPanel.Controls.Add(aiSection);    // fill (add first)
        aiPanel.Controls.Add(graphPanel);   // graph pinned to top

        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(_list);
        center.Controls.Add(aiPanel);

        Controls.Add(center);
        Controls.Add(top);
        Controls.Add(bottom);
    }

    private void OnSelect()
    {
        var e = Selected();
        if (e == null) return;
        UpdateGraph(e);
        if (_aiAuto.Checked) RunAi(e);
    }

    private void UpdateGraph(LedgerEntry e)
    {
        int hours = _frame.SelectedIndex switch { 1 => 72, 2 => 168, _ => 24 };
        _graph.SetData(_ctx.UsageSeries(e.Key, hours), e.Name);
    }

    private async void RunAi(LedgerEntry entry)
    {
        if (_aiCache.TryGetValue(entry.Key, out var cached)) { _aiBox.Text = cached; return; }
        var s = _ctx.Settings;
        if (!AiService.HasKey(s))
        {
            _aiBox.Text = $"No {AiService.ProviderName(s)} API key set — click \"Key\" above " +
                          (AiService.IsGemini(s) ? "(get one free at aistudio.google.com/apikey)." : "(console.anthropic.com).") +
                          "\r\n\r\nOr use \"More info\" for a plain web search (no key needed).";
            return;
        }
        _aiBox.Text = $"Analyzing “{entry.Name}” with {AiService.ProviderName(s)}…";
        var app = new AppUsage
        {
            Name = entry.Name,
            ExePath = IsPathKey(entry.Key) ? entry.Key : null,
            Pid = 0,
            BytesIn = entry.BytesIn,
            BytesOut = entry.BytesOut,
            Domains = new HashSet<string>(entry.Domains),
        };
        string result;
        try { result = await AiService.AnalyzeAsync(s, AppInspector.BuildPrompt(app)); }
        catch (Exception ex) { result = "AI failed: " + ex.Message; }
        _aiCache[entry.Key] = result;
        if (Selected()?.Key == entry.Key) _aiBox.Text = result.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private void SetAiKey()
    {
        var s = _ctx.Settings;
        bool gem = AiService.IsGemini(s);
        var key = PromptText(
            gem ? "Paste your Google AI Studio (Gemini) API key:" : "Paste your Anthropic (Claude) API key (sk-ant-…):",
            gem ? s.GeminiApiKey : s.AiApiKey);
        if (key == null) return;
        if (gem) s.GeminiApiKey = key.Trim(); else s.AiApiKey = key.Trim();
        s.Save();
        var e = Selected(); if (e != null) { _aiCache.Remove(e.Key); RunAi(e); }
    }

    private string? PromptText(string label, string initial)
    {
        using var f = new Form
        {
            Text = "KillSwitch — API key",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(470, 132),
            Font = new Font("Segoe UI", 9f),
            Icon = IconFactory.Make(false),
        };
        var lbl = new Label { AutoSize = false }; lbl.SetBounds(12, 12, 446, 38); lbl.Text = label;
        var tb = new TextBox { UseSystemPasswordChar = true, Text = initial }; tb.SetBounds(12, 54, 446, 24);
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK }; ok.SetBounds(282, 90, 80, 30);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; cancel.SetBounds(378, 90, 80, 30);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }

    private void Reload()
    {
        var rows = SortRows(FilterRows(_ctx.Ledger.Entries.Values));

        var blockedSet = _ctx.BlockedAppPaths();
        _list.BeginUpdate();
        _list.Items.Clear();
        long totIn = 0, totOut = 0;
        foreach (var e in rows)
        {
            totIn += e.BytesIn; totOut += e.BytesOut;
            var it = new ListViewItem(e.Name) { Tag = e };
            it.SubItems.Add(Fmt.Bytes(e.BytesIn));
            it.SubItems.Add(Fmt.Bytes(e.BytesOut));
            it.SubItems.Add(Fmt.Bytes(e.TotalBytes));
            it.SubItems.Add(SitesText(e));
            it.SubItems.Add(e.FirstSeen);
            it.SubItems.Add(e.LastSeen);
            it.SubItems.Add(e.Key);
            if (blockedSet.Contains(e.Key)) it.ForeColor = IconFactory.Blocked;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _summary.Text = $"{rows.Count} apps recorded · total down {Fmt.Bytes(totIn)}, up {Fmt.Bytes(totOut)}";
    }

    private void ExportCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export ledger",
            Filter = "CSV (*.csv)|*.csv",
            FileName = "killswitch-ledger.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("Application,BytesDown,BytesUp,BytesTotal,FirstSeen,LastSeen,Key,Domains");
        foreach (var e in _ctx.Ledger.Entries.Values.OrderByDescending(x => x.TotalBytes))
            sb.AppendLine($"{Csv(e.Name)},{e.BytesIn},{e.BytesOut},{e.TotalBytes},{Csv(e.FirstSeen)},{Csv(e.LastSeen)},{Csv(e.Key)},{Csv(string.Join("; ", e.Domains))}");

        try { File.WriteAllText(dlg.FileName, sb.ToString()); MessageBox.Show(this, "Saved to:\n" + dlg.FileName, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    // ---- row actions ----

    private LedgerEntry? Selected()
        => _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is LedgerEntry e ? e : null;

    private static bool IsPathKey(string key)
        => key.Contains('\\') || key.Equals("System", StringComparison.OrdinalIgnoreCase);

    private List<LedgerEntry> FilterRows(IEnumerable<LedgerEntry> src)
    {
        var f = _filter.Text.Trim();
        if (f.Length == 0) return src.ToList();
        return src.Where(e =>
            e.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Domains.Any(d => d.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private List<LedgerEntry> SortRows(List<LedgerEntry> rows)
    {
        Func<LedgerEntry, IComparable> key = _sortCol switch
        {
            0 => e => e.Name ?? "",
            1 => e => e.BytesIn,
            2 => e => e.BytesOut,
            3 => e => e.TotalBytes,
            4 => e => e.Domains?.Count ?? 0,
            5 => e => e.FirstSeen ?? "",
            6 => e => e.LastSeen ?? "",
            7 => e => e.Key ?? "",
            _ => e => e.TotalBytes,
        };
        return (_sortAsc ? rows.OrderBy(key) : rows.OrderByDescending(key)).ToList();
    }

    private static string SitesText(LedgerEntry e)
    {
        if (e.Domains == null || e.Domains.Count == 0) return "";
        var shown = string.Join(", ", e.Domains.Take(2));
        return e.Domains.Count > 2 ? $"{shown}  (+{e.Domains.Count - 2})" : shown;
    }

    private void BuildMenu(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _menu.Items.Clear();
        var entry = Selected();
        if (entry == null) { e.Cancel = true; return; }

        if (IsPathKey(entry.Key))
        {
            if (_ctx.BlockedAppPaths().Contains(entry.Key))
                _menu.Items.Add(new ToolStripMenuItem($"Unblock \"{entry.Name}\"", null, (_, _) => { _ctx.UnblockApp(entry.Key); Reload(); }));
            else
                _menu.Items.Add(new ToolStripMenuItem($"Block \"{entry.Name}\"", null, (_, _) => DoBlock()));
        }
        _menu.Items.Add(new ToolStripMenuItem("Analyze with Claude (AI)…", null, (_, _) => DoAnalyze()));
        _menu.Items.Add(new ToolStripMenuItem("Search the web for this app ⧉", null, (_, _) => DoWebSearch()));
        _menu.Items.Add(new ToolStripMenuItem("Copy path / key", null, (_, _) => { try { Clipboard.SetText(entry.Key); } catch { } }));
    }

    private void DoBlock()
    {
        var e = Selected();
        if (e == null) { Info("Select an app in the ledger first."); return; }
        if (!IsPathKey(e.Key)) { Info($"\"{e.Name}\" has no program path recorded, so it can't be blocked by program.\nBlock it by destination from the Traffic tab, or use Cut Internet."); return; }
        var err = _ctx.BlockApp(e.Key, e.Name);
        Info(err ?? (e.Name + " blocked."));
        Reload();
    }

    private void DoWebSearch()
    {
        var e = Selected();
        if (e == null) { Info("Select an app first."); return; }
        string file = "";
        try { if (IsPathKey(e.Key) && e.Key != "System") file = Path.GetFileName(e.Key); } catch { }
        OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString($"{e.Name} {file} Windows process — what is it, who makes it, is it safe to block"));
    }

    private void DoAnalyze()
    {
        var e = Selected();
        if (e == null) { Info("Select an app first."); return; }
        var app = new AppUsage { Name = e.Name, ExePath = IsPathKey(e.Key) ? e.Key : null, Pid = 0, BytesIn = e.BytesIn, BytesOut = e.BytesOut };
        using var f = new AiAnalysisForm(_ctx, app);
        f.ShowDialog(this);
        Reload();
    }

    private void Info(string m) => MessageBox.Show(this, m, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
