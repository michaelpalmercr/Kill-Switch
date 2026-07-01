using System.Diagnostics;
using System.IO;

namespace KillSwitch;

/// <summary>
/// Tier 1: run a program's official uninstaller. Tier 2: force-remove (kill → take ownership →
/// clear attributes → quarantine → reboot-delete fallback). Also force-delete an arbitrary file/folder.
/// Quarantine makes removals reversible; locked items fall back to delete-on-reboot (flagged).
/// </summary>
public static class RemovalEngine
{
    // ---- Tier 1: official uninstall ----
    public static string Uninstall(InstalledProgram p, bool preferSilent)
    {
        string cmd = preferSilent && !string.IsNullOrWhiteSpace(p.QuietUninstallString)
            ? p.QuietUninstallString : p.UninstallString;
        if (string.IsNullOrWhiteSpace(cmd)) return "No uninstaller is registered for this program — use Force remove.";
        var r = ProcessRunner.Run("cmd.exe", "/c \"" + cmd + "\"", 600000);
        return r.Ok ? "Uninstaller finished." : "Uninstaller returned an error:\n" + r.Output;
    }

    // ---- Tier 2: force remove a program ----
    public static string ForceRemove(InstalledProgram p, QBatch batch, bool killLockers)
    {
        var log = new List<string>();
        // try the silent official uninstaller first (best-effort)
        if (!string.IsNullOrWhiteSpace(p.UninstallString))
        {
            try { Uninstall(p, preferSilent: true); log.Add("ran uninstaller"); } catch { }
        }
        // quarantine the install folder
        if (!string.IsNullOrWhiteSpace(p.InstallLocation) && Directory.Exists(p.InstallLocation))
            log.Add(ForceDelete(p.InstallLocation, batch, killLockers));
        // export + remove the Add/Remove-Programs registry key
        if (!string.IsNullOrWhiteSpace(p.RegKeyPath))
        {
            Quarantine.ExportRegKey(batch, p.RegKeyPath);
            ProcessRunner.Run("reg.exe", $"delete \"{p.RegKeyPath}\" /f");
            log.Add("removed registry entry");
        }
        Quarantine.Save(batch);
        return string.Join("; ", log);
    }

    // ---- Force-delete a file or folder, with escalation ----
    public static string ForceDelete(string path, QBatch batch, bool killLockers, IEnumerable<int>? lockerPids = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path + ": not found";

        ClearAttributes(path);

        if (TryStash(batch, path, out var why)) return Quarantined(path);

        if (why == Fail.Acl)
        {
            TakeOwnership(path);
            if (TryStash(batch, path, out _)) return Quarantined(path) + " (after taking ownership)";
        }

        if (killLockers)
        {
            var pids = lockerPids ?? ImpactScanner.Scan(path).Pids;
            foreach (var pid in pids.Distinct())
                try { using var pr = Process.GetProcessById(pid); pr.Kill(entireProcessTree: true); } catch { }
            System.Threading.Thread.Sleep(500);
            TakeOwnership(path);
            if (TryStash(batch, path, out _)) return Quarantined(path) + " (after closing the programs using it)";
        }

        // last resort: schedule deletion on next reboot (NOT recoverable)
        if (File.Exists(path) && Quarantine.ScheduleDeleteOnReboot(path))
            return path + ": locked — scheduled for deletion on next reboot (NOT recoverable).";

        return path + ": could not remove (still locked/protected).";
    }

    private enum Fail { None, Acl, Locked, Other }

    private static bool TryStash(QBatch batch, string path, out Fail why)
    {
        why = Fail.None;
        try { Quarantine.Stash(batch, path); return true; }
        catch (UnauthorizedAccessException) { why = Fail.Acl; }
        catch (IOException) { why = Fail.Locked; }
        catch { why = Fail.Other; }
        return false;
    }

    private static string Quarantined(string path) => path + ": quarantined (recoverable).";

    private static void ClearAttributes(string path)
    {
        try { ProcessRunner.Run("attrib.exe", $"-r -s -h \"{path}\" /s /d"); } catch { }
    }

    private static void TakeOwnership(string path)
    {
        bool dir = Directory.Exists(path);
        // S-1-5-32-544 = Administrators
        ProcessRunner.Run("takeown.exe", dir ? $"/f \"{path}\" /r /d y" : $"/f \"{path}\"", 120000);
        ProcessRunner.Run("icacls.exe", dir ? $"\"{path}\" /grant *S-1-5-32-544:F /t /c /q" : $"\"{path}\" /grant *S-1-5-32-544:F /c /q", 120000);
    }
}
