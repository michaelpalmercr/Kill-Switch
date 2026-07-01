using Microsoft.Win32;

namespace KillSwitch;

public sealed class InstalledProgram
{
    public string Name = "";
    public string Publisher = "";
    public string Version = "";
    public string InstallLocation = "";
    public string UninstallString = "";
    public string QuietUninstallString = "";
    public long SizeKB;
    public string RegKeyPath = "";       // for export/delete via reg.exe (e.g. HKLM\SOFTWARE\...\<key>)
    public bool SystemComponent;
}

/// <summary>Enumerates installed programs from the Windows uninstall registry (machine 64/32-bit + per-user).</summary>
public static class InstalledPrograms
{
    public static List<InstalledProgram> Enumerate()
    {
        var list = new List<InstalledProgram>();
        Read(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
             @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list);
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
             @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", list);
        Read(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
             @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list);

        return list
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name + "|" + p.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Read(RegistryKey hive, string subPath, string regPrefix, List<InstalledProgram> list)
    {
        try
        {
            using var root = hive.OpenSubKey(subPath);
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var s = root.OpenSubKey(name);
                    if (s?.GetValue("DisplayName") is not string dn || string.IsNullOrWhiteSpace(dn)) continue;

                    list.Add(new InstalledProgram
                    {
                        Name = dn,
                        Publisher = s.GetValue("Publisher") as string ?? "",
                        Version = s.GetValue("DisplayVersion") as string ?? "",
                        InstallLocation = s.GetValue("InstallLocation") as string ?? "",
                        UninstallString = s.GetValue("UninstallString") as string ?? "",
                        QuietUninstallString = s.GetValue("QuietUninstallString") as string ?? "",
                        SizeKB = s.GetValue("EstimatedSize") is int kb ? kb : 0L,
                        RegKeyPath = regPrefix + "\\" + name,
                        SystemComponent = s.GetValue("SystemComponent") is int sc && sc == 1,
                    });
                }
                catch { }
            }
        }
        catch { }
    }
}
