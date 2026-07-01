using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KillSwitch;

/// <summary>
/// Best-effort "what will this removal affect" scan: files to remove, running programs that currently
/// have the target loaded, and services whose binary lives there. Cannot see boot-time / delay-loaded
/// dependencies or (from a 64-bit host) modules of 32-bit processes — so it reduces risk, not to zero.
/// </summary>
public static class ImpactScanner
{
    public sealed class Proc { public int Pid; public string Name = ""; public string Module = ""; }

    public sealed class Result
    {
        public List<Proc> Processes = new();
        public List<string> Services = new();
        public int FileCount;
        public long TotalBytes;
        public List<string> SampleFiles = new();
        public bool Truncated;
        public IEnumerable<int> Pids => Processes.Select(p => p.Pid).Distinct();
    }

    public static Result Scan(string path)
    {
        var r = new Result();
        bool isDir = Directory.Exists(path);

        // files that would be removed
        try
        {
            if (isDir)
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    r.FileCount++;
                    try { r.TotalBytes += new FileInfo(f).Length; } catch { }
                    if (r.SampleFiles.Count < 100) r.SampleFiles.Add(f);
                    if (r.FileCount >= 50000) { r.Truncated = true; break; }
                }
            }
            else if (File.Exists(path))
            {
                r.FileCount = 1;
                try { r.TotalBytes = new FileInfo(path).Length; } catch { }
                r.SampleFiles.Add(path);
            }
        }
        catch { }

        // running programs that have the target loaded
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                foreach (ProcessModule m in p.Modules)
                {
                    string mf = m.FileName ?? "";
                    if (mf.Length > 0 && Under(mf, path, isDir))
                    {
                        r.Processes.Add(new Proc { Pid = p.Id, Name = p.ProcessName, Module = mf });
                        break;
                    }
                }
            }
            catch { /* protected / bitness mismatch — skip */ }
            finally { try { p.Dispose(); } catch { } }
        }

        // services whose ImagePath references the target
        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (svc != null)
                foreach (var name in svc.GetSubKeyNames())
                {
                    try
                    {
                        using var k = svc.OpenSubKey(name);
                        if (k?.GetValue("ImagePath") is string ip && ip.Length > 0 && ImagePathHits(ip, path, isDir))
                            r.Services.Add(name);
                    }
                    catch { }
                }
        }
        catch { }

        return r;
    }

    private static bool Under(string file, string target, bool targetIsDir)
    {
        try
        {
            string f = Path.GetFullPath(file).TrimEnd('\\');
            string t = Path.GetFullPath(target).TrimEnd('\\');
            return targetIsDir
                ? f.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(f, t, StringComparison.OrdinalIgnoreCase)
                : string.Equals(f, t, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool ImagePathHits(string imagePath, string target, bool targetIsDir)
    {
        try
        {
            // ImagePath can be quoted and carry args; take the first token.
            string exe = imagePath.Trim();
            if (exe.StartsWith("\"")) exe = exe[1..(exe.IndexOf('"', 1) is int q and > 0 ? q : exe.Length)];
            else { int sp = exe.IndexOf(' '); if (sp > 0) exe = exe[..sp]; }
            return Under(exe, target, targetIsDir);
        }
        catch { return false; }
    }
}
