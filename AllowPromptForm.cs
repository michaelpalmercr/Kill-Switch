namespace KillSwitch;

public enum AllowPromptResult { None, Allow, BlockOnce, AlwaysBlock }

/// <summary>"App X wants to connect" prompt shown in allowlist (default-deny) mode.</summary>
public sealed class AllowPromptForm : Form
{
    public AllowPromptResult Result { get; private set; } = AllowPromptResult.BlockOnce;

    public AllowPromptForm(string appName, string path)
    {
        Text = "KillSwitch — allow connection?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(440, 190);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(true);

        var title = new Label
        {
            Text = $"“{appName}” is trying to reach the internet.",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = false,
        };
        title.SetBounds(16, 16, 408, 26);

        var pathLbl = new Label
        {
            Text = path,
            ForeColor = Color.DimGray,
            AutoEllipsis = true,
        };
        pathLbl.SetBounds(16, 44, 408, 34);

        var ask = new Label { Text = "Allowlist mode is on — only apps you approve can connect.", ForeColor = Color.Gray };
        ask.SetBounds(16, 82, 408, 20);

        var allow = new Button { Text = "Allow", DialogResult = DialogResult.OK };
        allow.SetBounds(16, 120, 120, 40);
        allow.BackColor = IconFactory.Online;
        allow.ForeColor = Color.White;
        allow.FlatStyle = FlatStyle.Flat;
        allow.Click += (_, _) => Close(AllowPromptResult.Allow);

        var once = new Button { Text = "Keep blocked" };
        once.SetBounds(150, 120, 130, 40);
        once.Click += (_, _) => Close(AllowPromptResult.BlockOnce);

        var always = new Button { Text = "Always block" };
        always.SetBounds(294, 120, 130, 40);
        always.Click += (_, _) => Close(AllowPromptResult.AlwaysBlock);

        Controls.AddRange(new Control[] { title, pathLbl, ask, allow, once, always });
        AcceptButton = allow;
        Theme.Apply(this);
    }

    private void Close(AllowPromptResult r) { Result = r; Close(); }
}
