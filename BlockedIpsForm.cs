namespace KillSwitch;

/// <summary>Manage destination IPs blocked via block-by-address.</summary>
public sealed class BlockedIpsForm : Form
{
    private readonly KillSwitchContext _ctx;
    private readonly FastListView _list = new();
    private readonly TextBox _ip = new();

    public BlockedIpsForm(KillSwitchContext ctx)
    {
        _ctx = ctx;
        Text = "Blocked destination IPs";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 380);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(true);

        _list.SetBounds(12, 12, 396, 278);
        _list.Columns.Add("Blocked IP (no app can reach these)", 360);
        _list.FullRowSelect = true;

        _ip.SetBounds(12, 298, 250, 24);
        _ip.PlaceholderText = "Add an IP / subnet to block…";

        var add = new Button { Text = "Block" };
        add.SetBounds(270, 297, 70, 26);
        add.Click += (_, _) =>
        {
            var t = _ip.Text.Trim();
            if (t.Length == 0) return;
            var err = _ctx.BlockIp(t);
            if (err != null) MessageBox.Show(this, err, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _ip.Clear();
            Reload();
        };

        var remove = new Button { Text = "Unblock selected" };
        remove.SetBounds(12, 334, 150, 30);
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0) { _ctx.UnblockIp(_list.SelectedItems[0].Text); Reload(); }
        };

        var close = new Button { Text = "Close", DialogResult = DialogResult.OK };
        close.SetBounds(308, 334, 100, 30);

        Controls.AddRange(new Control[] { _list, _ip, add, remove, close });
        AcceptButton = close;
        Reload();
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var ip in _ctx.Settings.BlockedIps) _list.Items.Add(new ListViewItem(ip));
        _list.EndUpdate();
    }
}
