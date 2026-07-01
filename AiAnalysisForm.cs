namespace KillSwitch;

/// <summary>Opt-in Claude analysis of a selected app, with one-click Block / Mark safe.</summary>
public sealed class AiAnalysisForm : Form
{
    private readonly KillSwitchContext _ctx;
    private readonly AppUsage _app;
    private readonly TextBox _output = new();
    private readonly ComboBox _provider = new();
    private bool _running;

    public AiAnalysisForm(KillSwitchContext ctx, AppUsage app)
    {
        _ctx = ctx;
        _app = app;
        BuildUi();
        Shown += (_, _) => MaybeAnalyze();
    }

    private void BuildUi()
    {
        Text = "Analyze app — " + _app.Name;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(620, 470);
        MinimumSize = new Size(520, 360);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(false);

        var header = new Label
        {
            Text = $"{_app.Name}  (PID {_app.Pid})\n{_app.ExePath ?? "(no exe path)"}",
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(10, 8, 10, 0),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoEllipsis = true,
        };

        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;
        _output.BorderStyle = BorderStyle.None;
        _output.Font = new Font("Segoe UI", 9.5f);
        _output.BackColor = Color.White;
        _output.Dock = DockStyle.Fill;
        var outPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4), BackColor = Color.White };
        outPanel.Controls.Add(_output);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        _provider.Items.AddRange(new object[] { "Claude", "Gemini" });
        _provider.SelectedIndex = AiService.IsGemini(_ctx.Settings) ? 1 : 0;
        _provider.SetBounds(8, 9, 96, 26);
        _provider.SelectedIndexChanged += (_, _) =>
        {
            _ctx.Settings.AiProvider = _provider.SelectedIndex == 1 ? "gemini" : "claude";
            _ctx.Settings.Save();
            MaybeAnalyze(true);
        };
        var analyze = new Button { Text = "Re-analyze" }; analyze.SetBounds(110, 7, 84, 30);
        analyze.Click += (_, _) => MaybeAnalyze(true);
        var block = new Button { Text = "Block app", BackColor = IconFactory.Blocked, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        block.SetBounds(198, 7, 96, 30);
        block.Click += (_, _) => DoBlock();
        var safe = new Button { Text = "Mark safe" }; safe.SetBounds(298, 7, 84, 30);
        safe.Click += (_, _) => { if (!string.IsNullOrEmpty(_app.ExePath)) _ctx.AddSafeApp(_app.ExePath!, _app.Name); };
        var key = new Button { Text = "Set API key" }; key.SetBounds(386, 7, 96, 30);
        key.Click += (_, _) => SetKey();
        var copy = new Button { Text = "Copy" }; copy.SetBounds(486, 7, 56, 30);
        copy.Click += (_, _) => { try { Clipboard.SetText(_output.Text); } catch { } };
        bar.Controls.AddRange(new Control[] { _provider, analyze, block, safe, key, copy });

        Controls.Add(outPanel);
        Controls.Add(header);
        Controls.Add(bar);
    }

    private async void MaybeAnalyze(bool force = false)
    {
        if (_running) return;
        var s = _ctx.Settings;
        string prov = AiService.ProviderName(s);
        if (!AiService.HasKey(s))
        {
            string where = AiService.IsGemini(s) ? "Google AI Studio (aistudio.google.com/apikey)" : "console.anthropic.com";
            _output.Text =
                $"AI analysis is OFF — no {prov} API key set.\r\n\r\n" +
                "This is the only feature that uses the internet: it sends this app's name, publisher, and the IPs/domains " +
                $"it talks to, to {prov}, and shows a verdict (with a web lookup of the vendor).\r\n\r\n" +
                $"Pick a provider on the left, then click \"Set API key\" and paste your key from {where}. " +
                "It's stored locally; nothing is sent until you click Analyze. Everything else works fully offline.\r\n\r\n" +
                "Prefer no key? Use \"More info\" instead — it just opens a web search.";
            return;
        }

        _running = true;
        _output.Text = $"Analyzing with {prov} ({AiService.ModelLabel(s)})…\r\nThis can take 10–30 seconds.";
        try
        {
            var result = await AiService.AnalyzeAsync(s, AppInspector.BuildPrompt(_app));
            _output.Text = result.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _output.Select(0, 0);
        }
        finally { _running = false; }
    }

    private void DoBlock()
    {
        string? path = _app.ExePath;
        if (!string.IsNullOrEmpty(path))
        {
            var err = _ctx.BlockApp(path!, _app.Name);
            MessageBox.Show(this, err ?? (_app.Name + " blocked."), "KillSwitch",
                MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return;
        }
        var ips = (_app.Remotes ?? new()).Where(ip => ip is not ("0.0.0.0" or "::" or "::1")).Distinct().ToList();
        if (ips.Count == 0) { MessageBox.Show(this, "No program path or destinations to block — use Cut Internet.", "KillSwitch"); return; }
        int n = _ctx.BlockIps(ips);
        MessageBox.Show(this, $"Blocked {n} destination IP(s).", "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetKey()
    {
        var s = _ctx.Settings;
        bool gem = AiService.IsGemini(s);
        string label = gem
            ? "Paste your Google AI Studio (Gemini) API key. Stored locally in config.json."
            : "Paste your Anthropic (Claude) API key (sk-ant-…). Stored locally in config.json.";
        var key = PromptText(label, gem ? s.GeminiApiKey : s.AiApiKey, password: true);
        if (key == null) return;
        if (gem) s.GeminiApiKey = key.Trim(); else s.AiApiKey = key.Trim();
        s.Save();
        MaybeAnalyze(true);
    }

    private string? PromptText(string label, string initial, bool password = false)
    {
        using var f = new Form
        {
            Text = "KillSwitch — API key",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(470, 140),
            Font = new Font("Segoe UI", 9f),
            Icon = IconFactory.Make(false),
        };
        var lbl = new Label { AutoSize = false };
        lbl.SetBounds(12, 12, 446, 40);
        lbl.Text = label;
        var tb = new TextBox { UseSystemPasswordChar = password, Text = initial };
        tb.SetBounds(12, 58, 446, 24);
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
        ok.SetBounds(282, 96, 80, 30);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(378, 96, 80, 30);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }
}
