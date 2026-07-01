using System.Collections.Concurrent;
using System.Diagnostics;

namespace KillSwitch;

/// <summary>Maps a PID to a process name + executable path, cached (PID reuse is tolerated).</summary>
public static class ProcessResolver
{
    private static readonly ConcurrentDictionary<int, (string Name, string? Path)> Cache = new();

    public static (string Name, string? Path) Resolve(int pid)
    {
        // PID 0 = packets we couldn't map to an owning process; not blockable by program.
        if (pid <= 0) return ("Unattributed", null);
        // PID 4 = the Windows kernel ("System"). Blockable via the special program=System firewall token.
        if (pid == 4) return ("System", "System");
        if (Cache.TryGetValue(pid, out var hit)) return hit;

        (string, string?) result;
        try
        {
            using var p = Process.GetProcessById(pid);
            string name = p.ProcessName;
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* protected/system process */ }
            result = (string.IsNullOrEmpty(name) ? $"pid {pid}" : name, path);
        }
        catch
        {
            result = ($"pid {pid}", null);
        }

        Cache[pid] = result;
        return result;
    }

    /// <summary>Drop cache entries for processes that have exited, so names stay accurate.</summary>
    public static void Prune(IReadOnlySet<int> alivePids)
    {
        foreach (var pid in Cache.Keys)
            if (!alivePids.Contains(pid)) Cache.TryRemove(pid, out _);
    }
}
