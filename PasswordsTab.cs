namespace KillSwitch;

/// <summary>Encrypted password vault UI — unlock with a master password, then manage your own entries.</summary>
public sealed class PasswordsTab : UserControl
{
    private readonly KillSwitchContext _ctx;
    private PasswordVault V => _ctx.Vault;

    private readonly Panel _lockPanel = new() { Dock = DockStyle.Fill };
    private readonly Panel _box = new();
    private readonly Label _lockTitle = new();
    private readonly Label _lockInfo = new();
    private readonly TextBox _master = new();
    private readonly TextBox _confirm = new();
    private readonly Label _confirmLbl = new();
    private readonly Button _unlockBtn = new();
    private readonly Label _lockErr = new();

    private readonly Panel _mainPanel = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly FastListView _list = new();
    private readonly TextBox _filter = new();
    private bool _showPw;

    public PasswordsTab(KillSwitchContext ctx)
    {
        _ctx = ctx;
        BuildUi();
        VisibleChanged += (_, _) => { if (Visible) RefreshState(); };
    }

    private void BuildUi()
    {
        Dock = DockStyle.Fill;
        BuildLockPanel();
        BuildMainPanel();
        Controls.Add(_mainPanel);
        Controls.Add(_lockPanel);
        RefreshState();
    }

    // ---------------- locked ----------------
    private void BuildLockPanel()
    {
        _box.Size = new Size(420, 230);
        _box.BackColor = Color.White;
        _box.BorderStyle = BorderStyle.FixedSingle;

        _lockTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _lockTitle.SetBounds(16, 14, 388, 26);
        _lockInfo.ForeColor = Color.DimGray; _lockInfo.AutoSize = false;
        _lockInfo.SetBounds(16, 44, 388, 48);
        _lockInfo.Text = "AES-256 encrypted; the master password is never stored. Entries are added by you — nothing is captured from network traffic.";

        var mlbl = new Label { Text = "Master password", AutoSize = true }; mlbl.SetBounds(16, 96, 120, 18);
        _master.UseSystemPasswordChar = true; _master.SetBounds(16, 116, 388, 26);
        _master.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) DoUnlockOrCreate(); };

        _confirmLbl.Text = "Confirm master password"; _confirmLbl.AutoSize = true; _confirmLbl.SetBounds(16, 148, 200, 18);
        _confirm.UseSystemPasswordChar = true; _confirm.SetBounds(16, 168, 388, 26);

        _unlockBtn.SetBounds(16, 200, 130, 30);
        _unlockBtn.Click += (_, _) => DoUnlockOrCreate();
        _lockErr.ForeColor = IconFactory.Blocked; _lockErr.AutoSize = true; _lockErr.SetBounds(156, 206, 248, 18);

        _box.Controls.AddRange(new Control[] { _lockTitle, _lockInfo, mlbl, _master, _confirmLbl, _confirm, _unlockBtn, _lockErr });
        _lockPanel.Controls.Add(_box);
        _lockPanel.Resize += (_, _) => CenterBox();
        CenterBox();
    }

    private void CenterBox() => _box.Location = new Point(Math.Max(8, (_lockPanel.ClientSize.Width - _box.Width) / 2), Math.Max(8, (_lockPanel.ClientSize.Height - _box.Height) / 2 - 20));

    private void DoUnlockOrCreate()
    {
        _lockErr.Text = "";
        if (!PasswordVault.Exists())
        {
            if (_master.Text.Length < 4) { _lockErr.Text = "Pick a master password (4+ chars)."; return; }
            if (_master.Text != _confirm.Text) { _lockErr.Text = "Passwords don't match."; return; }
            var err = V.Create(_master.Text);
            if (err != null) { _lockErr.Text = err; return; }
        }
        else
        {
            var err = V.Unlock(_master.Text);
            if (err != null) { _lockErr.Text = err; return; }
        }
        _master.Clear(); _confirm.Clear();
        RefreshState();
    }

    // ---------------- unlocked ----------------
    private void BuildMainPanel()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 36 };
        var add = new Button { Text = "Add login", Width = 90, Height = 28, Location = new Point(2, 4) };
        add.Click += (_, _) => { var e = EditEntry(new VaultEntry()); if (e != null) { V.Entries.Add(e); V.Save(); ReloadEntries(); } };
        var edit = new Button { Text = "Edit", Width = 70, Height = 28, Location = new Point(96, 4) };
        edit.Click += (_, _) => DoEdit();
        var del = new Button { Text = "Delete", Width = 80, Height = 28, Location = new Point(170, 4) };
        del.Click += (_, _) => DoDelete();
        var cu = new Button { Text = "Copy user", Width = 90, Height = 28, Location = new Point(254, 4) };
        cu.Click += (_, _) => { if (Sel() is { } e) Copy(e.Username); };
        var cp = new Button { Text = "Copy password", Width = 110, Height = 28, Location = new Point(348, 4) };
        cp.Click += (_, _) => { if (Sel() is { } e) Copy(e.Password); };
        var show = new CheckBox { Text = "Show passwords", Width = 130, Height = 28, Location = new Point(462, 6) };
        show.CheckedChanged += (_, _) => { _showPw = show.Checked; ReloadEntries(); };
        var flbl = new Label { Text = "Filter:", AutoSize = true, Location = new Point(600, 10) };
        _filter.SetBounds(642, 6, 160, 24);
        _filter.TextChanged += (_, _) => ReloadEntries();
        var lockv = new Button { Text = "Lock vault", Width = 90, Height = 28, Location = new Point(812, 4) };
        lockv.Click += (_, _) => { V.Lock(); RefreshState(); };
        top.Controls.AddRange(new Control[] { add, edit, del, cu, cp, show, flbl, _filter, lockv });

        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Title", 170);
        _list.Columns.Add("Username", 200);
        _list.Columns.Add("Password", 180);
        _list.Columns.Add("URL", 260);
        _list.Columns.Add("Updated", 120);
        _list.DoubleClick += (_, _) => DoEdit();

        var tip = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), Text = "Tip: on any login page press Ctrl+Alt+L to quick-save it here (you type the password — nothing is read from traffic)." };

        _mainPanel.Controls.Add(_list);
        _mainPanel.Controls.Add(tip);
        _mainPanel.Controls.Add(top);
    }

    private VaultEntry? Sel() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as VaultEntry : null;

    private void RefreshState()
    {
        bool unlocked = V.Unlocked;
        _mainPanel.Visible = unlocked;
        _lockPanel.Visible = !unlocked;
        if (unlocked) ReloadEntries();
        else
        {
            bool creating = !PasswordVault.Exists();
            _lockTitle.Text = creating ? "Create your password vault" : "Unlock your password vault";
            _unlockBtn.Text = creating ? "Create vault" : "Unlock";
            _confirm.Visible = _confirmLbl.Visible = creating;
        }
    }

    private void ReloadEntries()
    {
        if (!V.Unlocked) return;
        var f = _filter.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in V.Entries.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Length > 0 && !(e.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Url.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Username.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
            var it = new ListViewItem(e.Title) { Tag = e };
            it.SubItems.Add(e.Username);
            it.SubItems.Add(_showPw ? e.Password : new string('•', Math.Min(10, Math.Max(4, e.Password.Length))));
            it.SubItems.Add(e.Url);
            it.SubItems.Add(e.Updated);
            _list.Items.Add(it);
        }
        _list.EndUpdate();
    }

    private void DoEdit()
    {
        if (Sel() is not { } e) { Info("Select an entry."); return; }
        var updated = EditEntry(e);
        if (updated != null) { V.Save(); ReloadEntries(); }
    }

    private void DoDelete()
    {
        if (Sel() is not { } e) { Info("Select an entry."); return; }
        if (MessageBox.Show(this, $"Delete the saved login \"{e.Title}\"?", "KillSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        V.Entries.Remove(e); V.Save(); ReloadEntries();
    }

    /// <summary>Called by the global Ctrl+Alt+L hotkey: pre-fill an Add-login form from the active window's title.
    /// The user still types the password — nothing is read from network traffic or the keyboard.</summary>
    public void QuickAdd(string windowTitle)
    {
        if (!V.Unlocked)
        {
            RefreshState();
            _master.Focus();
            Info("Unlock your vault first, then press Ctrl+Alt+L again on the login page to quick-save it.");
            return;
        }
        var seed = new VaultEntry { Title = CleanTitle(windowTitle) };
        var made = EditEntry(seed);
        if (made != null) { V.Entries.Add(made); V.Save(); ReloadEntries(); }
    }

    /// <summary>Strip common browser/app suffixes so "Log in — Example - Google Chrome" becomes "Log in — Example".</summary>
    private static string CleanTitle(string title)
    {
        var t = (title ?? "").Trim();
        foreach (var suffix in new[] { " - Google Chrome", " - Microsoft​ Edge", " - Microsoft Edge", " — Mozilla Firefox", " - Mozilla Firefox", " - Brave", " - Opera", " - Vivaldi", " - Personal - Microsoft Edge" })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { t = t[..^suffix.Length].Trim(); break; }
        return t.Length > 80 ? t[..80] : t;
    }

    /// <summary>Add/edit dialog. For an existing entry it's mutated in place; returns it (or a new one) on OK, null on cancel.</summary>
    private VaultEntry? EditEntry(VaultEntry? existing)
    {
        var e = existing ?? new VaultEntry();
        using var f = new Form { Text = V.Entries.Contains(e) ? "Edit login" : "Add login", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(440, 320), Font = new Font("Segoe UI", 9f), Icon = IconFactory.Make(false) };
        TextBox Field(string label, string val, int y, bool pw = false)
        {
            var l = new Label { Text = label, AutoSize = true }; l.SetBounds(14, y, 120, 18);
            var t = new TextBox { Text = val, UseSystemPasswordChar = pw }; t.SetBounds(14, y + 20, 410, 24);
            f.Controls.Add(l); f.Controls.Add(t); return t;
        }
        var tTitle = Field("Title", e.Title, 10);
        var tUrl = Field("Website / URL", e.Url, 58);
        var tUser = Field("Username", e.Username, 106);
        var tPass = Field("Password", e.Password, 154, pw: true);
        var showCb = new CheckBox { Text = "Show", AutoSize = true, Location = new Point(360, 156) };
        showCb.CheckedChanged += (_, _) => tPass.UseSystemPasswordChar = !showCb.Checked;
        var nLbl = new Label { Text = "Notes", AutoSize = true }; nLbl.SetBounds(14, 202, 80, 18);
        var tNotes = new TextBox { Text = e.Notes, Multiline = true }; tNotes.SetBounds(14, 222, 410, 50);
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK }; ok.SetBounds(250, 282, 80, 30);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; cancel.SetBounds(344, 282, 80, 30);
        f.Controls.AddRange(new Control[] { showCb, nLbl, tNotes, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        Theme.Apply(f);

        if (f.ShowDialog(this) != DialogResult.OK) return null;
        e.Title = tTitle.Text.Trim();
        e.Url = tUrl.Text.Trim();
        e.Username = tUser.Text;
        e.Password = tPass.Text;
        e.Notes = tNotes.Text;
        e.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        return e;
    }

    private void Copy(string s) { try { Clipboard.SetText(string.IsNullOrEmpty(s) ? " " : s); } catch { } }
    private void Info(string m) => MessageBox.Show(this, m, "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
