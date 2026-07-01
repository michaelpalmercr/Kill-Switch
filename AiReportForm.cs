namespace KillSwitch;

/// <summary>Generic AI report dialog (program / IP / website). Provider switch + optional Block action.</summary>
public sealed class AiReportForm : Form
{
    private readonly KillSwitchContext _ctx;
    private readonly string _prompt;
    private readonly Action? _block;
    private readonly TextBox _output = new();
    private readonly ComboBox _provider = new();
    private bool _running;

    public AiReportForm(KillSwitchContext ctx, string title, string prompt, Action? block = null, string? blockLabel = null)
    {
        _ctx = ctx;
        _prompt = prompt;
        _block = block;

        Text = "AI report — " + title;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(620, 460);
        MinimumSize = new Size(520, 360);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(false);

        var header = new Label { Text = title, Dock = DockStyle.Top, Height = 26, Padding = new Padding(10, 6, 8, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoEllipsis = true };

        _output.Multiline = true; _output.ReadOnly = true; _output.ScrollBars = ScrollBars.Vertical;
        _output.BorderStyle = BorderStyle.None; _output.Font = new Font("Segoe UI", 9.5f); _output.BackColor = Color.White; _output.Dock = DockStyle.Fill;
        var outPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4), BackColor = Color.White };
        outPanel.Controls.Add(_output);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        _provider.Items.AddRange(new object[] { "Claude", "Gemini" });
        _provider.SelectedIndex = AiService.IsGemini(_ctx.Settings) ? 1 : 0;
        _provider.SetBounds(8, 9, 96, 26);
        _provider.SelectedIndexChanged += (_, _) => { _ctx.Settings.AiProvider = _provider.SelectedIndex == 1 ? "gemini" : "claude"; _ctx.Settings.Save(); Run(); };
        var rerun = new Button { Text = "Re-run", Width = 80, Height = 30, Location = new Point(110, 7) };
        rerun.Click += (_, _) => Run();
        var key = new Button { Text = "Set API key", Width = 96, Height = 30, Location = new Point(194, 7) };
        key.Click += (_, _) => SetKey();
        int x = 296;
        if (_block != null)
        {
            var blk = new Button { Text = blockLabel ?? "Block", Width = 130, Height = 30, Location = new Point(x, 7), BackColor = IconFactory.Blocked, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            blk.Click += (_, _) => { _block(); MessageBox.Show(this, "Blocked.", "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            bar.Controls.Add(blk);
            x += 136;
        }
        var copy = new Button { Text = "Copy", Width = 56, Height = 30, Location = new Point(x, 7) };
        copy.Click += (_, _) => { try { Clipboard.SetText(_output.Text); } catch { } };
        bar.Controls.AddRange(new Control[] { _provider, rerun, key, copy });

        Controls.Add(outPanel);
        Controls.Add(header);
        Controls.Add(bar);
        Shown += (_, _) => Run();
    }

    private async void Run()
    {
        if (_running) return;
        var s = _ctx.Settings;
        string prov = AiService.ProviderName(s);
        if (!AiService.HasKey(s))
        {
            string where = AiService.IsGemini(s) ? "aistudio.google.com/apikey" : "console.anthropic.com";
            _output.Text = $"No {prov} API key set.\r\n\r\nPick a provider (left), click \"Set API key\", and paste your key from {where}. Stored locally; used only when you ask.";
            return;
        }
        _running = true;
        _output.Text = $"Researching with {prov} ({AiService.ModelLabel(s)}) + web search…\r\nThis can take 10–30 seconds.";
        try
        {
            var result = await AiService.AnalyzeAsync(s, _prompt);
            _output.Text = result.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _output.Select(0, 0);
        }
        finally { _running = false; }
    }

    private void SetKey()
    {
        var s = _ctx.Settings;
        bool gem = AiService.IsGemini(s);
        var key = PromptText(gem ? "Paste your Google AI Studio (Gemini) API key:" : "Paste your Anthropic (Claude) API key (sk-ant-…):", gem ? s.GeminiApiKey : s.AiApiKey);
        if (key == null) return;
        if (gem) s.GeminiApiKey = key.Trim(); else s.AiApiKey = key.Trim();
        s.Save();
        Run();
    }

    private string? PromptText(string label, string initial)
    {
        using var f = new Form
        {
            Text = "KillSwitch — API key", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(470, 132), Font = new Font("Segoe UI", 9f), Icon = IconFactory.Make(false),
        };
        var lbl = new Label { AutoSize = false }; lbl.SetBounds(12, 12, 446, 38); lbl.Text = label;
        var tb = new TextBox { UseSystemPasswordChar = true, Text = initial }; tb.SetBounds(12, 54, 446, 24);
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK }; ok.SetBounds(282, 90, 80, 30);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; cancel.SetBounds(378, 90, 80, 30);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }
}
