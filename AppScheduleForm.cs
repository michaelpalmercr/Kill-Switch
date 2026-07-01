namespace KillSwitch;

/// <summary>Edit the block schedule for a single app (its own timetable).</summary>
public sealed class AppScheduleForm : Form
{
    private readonly KillSwitchContext _ctx;
    private readonly string _path;
    private readonly string _name;
    private readonly List<ScheduleWindow> _windows;

    private readonly CheckBox _enabled = new();
    private readonly FastListView _list = new();
    private readonly CheckBox[] _days = new CheckBox[7];
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();

    public AppScheduleForm(KillSwitchContext ctx, AppUsage app)
    {
        _ctx = ctx;
        _path = app.ExePath!;
        _name = app.Name;

        var existing = ctx.GetScheduledApp(_path);
        _windows = existing?.Schedule.Select(Clone).ToList() ?? new List<ScheduleWindow>();
        BuildUi();
        _enabled.Checked = existing?.ScheduleEnabled ?? true;
        PopulateWindows();
    }

    private static ScheduleWindow Clone(ScheduleWindow w) => new()
    {
        Days = (bool[])w.Days.Clone(),
        Start = w.Start,
        End = w.End,
        Enabled = w.Enabled,
    };

    private void BuildUi()
    {
        Text = "Schedule — " + _name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(470, 430);
        Font = new Font("Segoe UI", 9f);
        Icon = IconFactory.Make(true);

        _enabled.Text = "Block this app on the schedule below";
        _enabled.SetBounds(12, 10, 440, 22);
        _enabled.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        var hint = new Label
        {
            Text = "When a window is active, the app is blocked; outside it, allowed. Days = the night the window starts.",
            ForeColor = Color.Gray, AutoSize = false,
        };
        hint.SetBounds(12, 34, 446, 32);

        _list.SetBounds(12, 70, 446, 150);
        _list.Columns.Add("Days", 230);
        _list.Columns.Add("From", 100);
        _list.Columns.Add("To", 100);
        _list.FullRowSelect = true;

        string[] names = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        int x = 12;
        for (int i = 0; i < 7; i++)
        {
            var cb = new CheckBox
            {
                Text = names[i], Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter,
                Width = 40, Height = 26, Location = new Point(x, 234),
                Checked = i >= 1 && i <= 5,
            };
            _days[i] = cb;
            Controls.Add(cb);
            x += 44;
        }

        _from.Format = DateTimePickerFormat.Time; _from.ShowUpDown = true;
        _from.Value = DateTime.Today.AddHours(23); _from.SetBounds(12, 268, 90, 24);
        var dash = new Label { Text = "to", AutoSize = true, Location = new Point(110, 272) };
        _to.Format = DateTimePickerFormat.Time; _to.ShowUpDown = true;
        _to.Value = DateTime.Today.AddHours(7); _to.SetBounds(132, 268, 90, 24);

        var add = new Button { Text = "Add window", Location = new Point(232, 267), Width = 110, Height = 26 };
        add.Click += (_, _) => AddWindow();
        var remove = new Button { Text = "Remove selected", Location = new Point(12, 302), Width = 130, Height = 26 };
        remove.Click += (_, _) => { foreach (ListViewItem it in _list.SelectedItems) if (it.Tag is ScheduleWindow w) _windows.Remove(w); PopulateWindows(); };

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(266, 386), Width = 90, Height = 32 };
        save.Click += (_, _) => _ctx.SetAppSchedule(_path, _name, _enabled.Checked, _windows);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(364, 386), Width = 90, Height = 32 };
        var removeAll = new Button { Text = "Clear schedule", Location = new Point(12, 386), Width = 120, Height = 32 };
        removeAll.Click += (_, _) => { _ctx.RemoveScheduledApp(_path); DialogResult = DialogResult.OK; Close(); };

        Controls.AddRange(new Control[] { _enabled, hint, _list, _from, dash, _to, add, remove, save, cancel, removeAll });
        AcceptButton = save;
        CancelButton = cancel;
        Theme.Apply(this);
    }

    private void AddWindow()
    {
        var w = new ScheduleWindow
        {
            Days = Enumerable.Range(0, 7).Select(i => _days[i].Checked).ToArray(),
            Start = _from.Value.ToString("HH:mm"),
            End = _to.Value.ToString("HH:mm"),
            Enabled = true,
        };
        if (!w.Days.Any(d => d)) { MessageBox.Show(this, "Pick at least one day.", "KillSwitch"); return; }
        _windows.Add(w);
        PopulateWindows();
    }

    private void PopulateWindows()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var w in _windows)
        {
            var it = new ListViewItem(w.DaysLabel()) { Tag = w };
            it.SubItems.Add(w.Start);
            it.SubItems.Add(w.End);
            _list.Items.Add(it);
        }
        _list.EndUpdate();
    }
}
