using System.Diagnostics;
using System.Text;

namespace KillSwitch;

/// <summary>Result of running an external command.</summary>
public readonly record struct CmdResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Output => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + "\n" + StdErr;
}

/// <summary>Runs short-lived console tools (netsh, powershell, schtasks) with no visible window.</summary>
public static class ProcessRunner
{
    public static CmdResult Run(string fileName, string arguments, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new CmdResult(-1, "", "Failed to start " + fileName);

            string outStr = p.StandardOutput.ReadToEnd();
            string errStr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return new CmdResult(-2, outStr, "Timed out after " + timeoutMs + "ms");
            }
            return new CmdResult(p.ExitCode, outStr, errStr);
        }
        catch (Exception ex)
        {
            return new CmdResult(-3, "", ex.Message);
        }
    }

    /// <summary>Runs a PowerShell snippet (Windows PowerShell 5.1, always present).</summary>
    public static CmdResult PowerShell(string script, int timeoutMs = 30000)
        => Run("powershell.exe",
               "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
               timeoutMs);

    /// <summary>Runs a netsh command.</summary>
    public static CmdResult Netsh(string arguments, int timeoutMs = 30000)
        => Run("netsh.exe", arguments, timeoutMs);
}
