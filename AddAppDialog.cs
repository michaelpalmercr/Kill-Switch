using System.Diagnostics;
using System.IO;

namespace KillSwitch;

/// <summary>Pick an application to manage — from running processes or by browsing to an .exe.</summary>
public sealed class AddAppDialog : Form
{
    public string? SelectedPath { get; private set; }
    public string? SelectedName { get; private set; }

    private readonly FastListView _list = new();
    private readonly TextBox _filter = new();

    public AddAppDialog()
    {
        Text = "Add application";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 460);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(false);

        var hint = new Label { Text = "Pick a running app, or Browse to any program (.exe):", AutoSize = true };
        hint.SetBounds(12, 10, 500, 18);

        _filter.SetBounds(12, 32, 420, 24);
        _filter.PlaceholderText = "Filter…";
        _filter.TextChanged += (_, _) => Populate();

        var refresh = new Button { Text = "Refresh", };
        refresh.SetBounds(440, 31, 108, 26);
        refresh.Click += (_, _) => Populate();

        _list.SetBounds(12, 64, 536, 320);
        _list.Columns.Add("Application", 180);
        _list.Columns.Add("PID", 60, HorizontalAlignment.Right);
        _list.Columns.Add("Path", 280);
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.DoubleClick += (_, _) => Accept();

        var browse = new Button { Text = "Browse…" };
        browse.SetBounds(12, 396, 120, 32);
        browse.Click += (_, _) => Browse();

        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK };
        ok.SetBounds(336, 396, 100, 32);
        ok.Click += (_, _) => Accept();

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(448, 396, 100, 32);

        Controls.AddRange(new Control[] { hint, _filter, refresh, _list, browse, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        Populate();
    }

    private void Populate()
    {
        string f = _filter.Text.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string name, int pid, string path)>();

        foreach (var p in Process.GetProcesses())
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (string.IsNullOrEmpty(path)) continue;
            if (!seen.Add(path)) continue; // distinct by exe
            string name = p.ProcessName;
            if (f.Length > 0 && !name.Contains(f, StringComparison.OrdinalIgnoreCase)
                             && !path.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add((name, p.Id, path));
        }

        rows.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in rows)
        {
            var it = new ListViewItem(r.name);
            it.SubItems.Add(r.pid.ToString());
            it.SubItems.Add(r.path);
            it.Tag = (r.path, r.name);
            _list.Items.Add(it);
        }
        _list.EndUpdate();
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select an application",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            SelectedPath = dlg.FileName;
            SelectedName = Path.GetFileNameWithoutExtension(dlg.FileName);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is ValueTuple<string, string> t)
        {
            SelectedPath = t.Item1;
            SelectedName = t.Item2;
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            MessageBox.Show(this, "Select an app or use Browse.", "Add application",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
