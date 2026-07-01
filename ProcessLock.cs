using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KillSwitch;

/// <summary>
/// Force-closes an app and blocks it from running via Image File Execution Options (IFEO).
/// Setting the IFEO "Debugger" to a no-op stub makes Windows refuse to launch the exe until we remove it.
/// We set the lock BEFORE killing so anything that tries to respawn the app is denied at launch.
/// </summary>
public static class ProcessLock
{
    private const string IfeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Marker = "KillSwitchLock";

    public static string ExeName(string path) => Path.GetFileName(path);

    public static bool IsLockable(string path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path)) { reason = "No executable path for this app."; return false; }
        string name = ExeName(path);
        if (string.IsNullOrEmpty(name)) { reason = "No exe name."; return false; }
        if (name.Equals("KillSwitch.exe", StringComparison.OrdinalIgnoreCase)) { reason = "Can't lock KillSwitch itself."; return false; }
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) { reason = "Refusing to lock a Windows/OS component (could destabilize Windows)."; return false; }
        return true;
    }

    public static bool IsLocked(string path)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{IfeoBase}\{ExeName(path)}");
            return k?.GetValue(Marker) != null;
        }
        catch { return false; }
    }

    private static string BlockerCommand()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string systray = Path.Combine(sys, "systray.exe");          // legacy no-op stub: swallows the launch silently
        if (File.Exists(systray)) return systray;
        return Path.Combine(sys, "cmd.exe") + " /c exit";           // fallback: exits immediately
    }

    /// <summary>Lock first (so respawns are denied), then force-close all instances.</summary>
    public static string? Lock(string path)
    {
        if (!IsLockable(path, out var reason)) return reason;
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey($@"{IfeoBase}\{ExeName(path)}"))
            {
                k.SetValue("Debugger", BlockerCommand(), RegistryValueKind.String);
                k.SetValue(Marker, 1, RegistryValueKind.DWord);
            }
            KillAll(path);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string? Unlock(string path)
    {
        try
        {
            string name = ExeName(path);
            using (var k = Registry.LocalMachine.OpenSubKey($@"{IfeoBase}\{name}"))
                if (k == null || k.GetValue(Marker) == null) return null; // not ours / not locked
            Registry.LocalMachine.DeleteSubKey($@"{IfeoBase}\{name}", throwOnMissingSubKey: false);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Kill every instance by image name, a few passes to catch stubborn/respawning ones.</summary>
    public static void KillAll(string path)
    {
        string baseName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(baseName)) return;
        for (int pass = 0; pass < 3; pass++)
        {
            var procs = Process.GetProcessesByName(baseName);
            if (procs.Length == 0 && pass > 0) break;
            foreach (var p in procs)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { p.Dispose(); } catch { }
            }
            System.Threading.Thread.Sleep(150);
        }
    }
}
