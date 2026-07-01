namespace KillSwitch;

internal static class Program
{
    public const string ShowEventName = "KillSwitch_Show_2f6c";

    [STAThread]
    private static void Main(string[] args)
    {
        // Headless escape hatches — these work even if no tray instance is running.
        // "Restore Internet" shortcut → guaranteed way to get back online.
        if (args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            new NetworkController(AppSettings.Load()).RestoreAll();
            ClearSystemProxy();
            MessageBox.Show("Internet restored.\n\nFirewall blocks removed, adapters re-enabled, and the system proxy cleared.",
                "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (args.Any(a => a.Equals("--cut", StringComparison.OrdinalIgnoreCase)))
        {
            var err = new NetworkController(AppSettings.Load()).Block();
            MessageBox.Show(err ?? "Internet cut — apps are now offline.",
                "KillSwitch", MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return;
        }

        // Single instance only — a kill switch fighting itself would be bad.
        using var mutex = new Mutex(true, "KillSwitch_SingleInstance_2f6c", out bool isNew);
        if (!isNew)
        {
            // Already running: signal that instance to surface its window, then exit quietly.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch
            {
                MessageBox.Show("KillSwitch is already running (check the system tray).",
                    "KillSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        bool forceShow = args.Any(a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        ApplicationConfiguration.Initialize();
        Application.Run(new KillSwitchContext(forceShow));
    }

    private static void ClearSystemProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            key?.SetValue("ProxyEnable", 0);
        }
        catch { }
    }
}
