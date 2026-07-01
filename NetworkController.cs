using System.Security.Cryptography;
using System.Text;

namespace KillSwitch;

/// <summary>
/// Performs the actual "cut the internet" / "restore" operations using two mechanisms:
///   • Firewall  – adds high-priority block-all rules (block rules beat allow rules in Windows Firewall).
///   • Adapter   – disables every connected network adapter (hard kill).
/// Both are fully reversible. Localhost/loopback traffic is never affected by the firewall rules.
/// </summary>
public sealed class NetworkController
{
    private const string RuleOut = "KillSwitch_Block_Outbound";
    private const string RuleIn = "KillSwitch_Block_Inbound";

    private readonly AppSettings _settings;

    public NetworkController(AppSettings settings) => _settings = settings;

    /// <summary>Is the internet currently cut, for the active mechanism?</summary>
    public bool IsBlocked() => _settings.Mechanism switch
    {
        KillMechanism.Firewall => _settings.GlobalBlockMode == "allowlist" || FirewallRuleExists(),
        KillMechanism.Adapter => _settings.DisabledAdapters.Count > 0,
        _ => false,
    };

    /// <summary>Cut the internet using the active mechanism. Returns null on success, or an error message.</summary>
    public string? Block() => _settings.Mechanism switch
    {
        KillMechanism.Firewall => FirewallBlock(),
        KillMechanism.Adapter => AdapterDisable(),
        _ => "Unknown mechanism",
    };

    /// <summary>Restore the internet using the active mechanism. Returns null on success, or an error message.</summary>
    public string? Restore() => _settings.Mechanism switch
    {
        KillMechanism.Firewall => FirewallRestore(),
        KillMechanism.Adapter => AdapterEnable(),
        _ => "Unknown mechanism",
    };

    /// <summary>Restore whatever might be blocked regardless of the active mechanism (used on exit / cleanup).</summary>
    public void RestoreAll()
    {
        if (FirewallRuleExists() || _settings.GlobalBlockMode == "allowlist") FirewallRestore();
        if (_settings.DisabledAdapters.Count > 0) AdapterEnable();
        if (_settings.AllowlistMode)
        {
            ExitAllowlist();
            _settings.AllowlistMode = false;
            _settings.Save();
        }
    }

    // ---------------- Firewall mechanism ----------------

    public static bool FirewallRuleExists()
    {
        var r = ProcessRunner.Netsh($"advfirewall firewall show rule name=\"{RuleOut}\"");
        // netsh exits non-zero and prints "No rules match" when the rule is absent.
        return r.Ok && r.StdOut.IndexOf(RuleOut, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Is the Windows Firewall actually turned on? (Block rules only bite when it is.)</summary>
    public static bool IsFirewallOn()
    {
        var r = ProcessRunner.Netsh("advfirewall show allprofiles state");
        // Any profile showing OFF means we can't fully rely on firewall mode.
        return r.Ok && r.StdOut.IndexOf("OFF", StringComparison.OrdinalIgnoreCase) < 0;
    }

    public static string? EnableFirewall()
    {
        var r = ProcessRunner.Netsh("advfirewall set allprofiles state on");
        return r.Ok ? null : r.Output;
    }

    private string? FirewallBlock()
    {
        if (_settings.SafeApps.Count == 0)
        {
            // Hard block-all: outbound stops apps reaching servers; inbound for completeness.
            var a = ProcessRunner.Netsh(
                $"advfirewall firewall add rule name=\"{RuleOut}\" dir=out action=block enable=yes profile=any");
            var b = ProcessRunner.Netsh(
                $"advfirewall firewall add rule name=\"{RuleIn}\" dir=in action=block enable=yes profile=any");
            if (!a.Ok) return "Failed to add outbound block rule:\n" + a.Output;
            if (!b.Ok) return "Failed to add inbound block rule:\n" + b.Output;
            _settings.GlobalBlockMode = "blockall";
            _settings.Save();
            return null;
        }

        // Safe apps present → default-deny, then allow ONLY the safe apps. Nothing else (incl. OS/DNS) passes.
        var r = ProcessRunner.Netsh("advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound");
        if (!r.Ok) return "Failed to set default-deny policy:\n" + r.Output;
        if (_settings.AllowDnsDuringCut) AddBaselineAllows();  // strict by default — no global DNS leak
        foreach (var s in _settings.SafeApps) AllowApp(s.Path);
        _settings.GlobalBlockMode = "allowlist";
        _settings.Save();
        return null;
    }

    private string? FirewallRestore()
    {
        if (_settings.GlobalBlockMode == "allowlist")
        {
            ProcessRunner.Netsh("advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound");
            RemoveBaselineAllows();
            foreach (var s in _settings.SafeApps) DisallowApp(s.Path);
            _settings.GlobalBlockMode = "";
            _settings.Save();
            return null;
        }

        ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{RuleOut}\"");
        ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{RuleIn}\"");
        _settings.GlobalBlockMode = "";
        _settings.Save();
        return FirewallRuleExists() ? "Block rule still present after delete." : null;
    }

    private static void AddBaselineAllows()
    {
        ProcessRunner.Netsh("advfirewall firewall add rule name=\"KillSwitch_Base_DNS_UDP\" dir=out action=allow protocol=UDP remoteport=53 enable=yes profile=any");
        ProcessRunner.Netsh("advfirewall firewall add rule name=\"KillSwitch_Base_DNS_TCP\" dir=out action=allow protocol=TCP remoteport=53 enable=yes profile=any");
        ProcessRunner.Netsh("advfirewall firewall add rule name=\"KillSwitch_Base_DHCP\" dir=out action=allow protocol=UDP remoteport=67-68 enable=yes profile=any");
    }

    private static void RemoveBaselineAllows()
    {
        ProcessRunner.Netsh("advfirewall firewall delete rule name=\"KillSwitch_Base_DNS_UDP\"");
        ProcessRunner.Netsh("advfirewall firewall delete rule name=\"KillSwitch_Base_DNS_TCP\"");
        ProcessRunner.Netsh("advfirewall firewall delete rule name=\"KillSwitch_Base_DHCP\"");
    }

    // ---------------- Adapter mechanism ----------------

    private static IEnumerable<string> AdaptersUp()
    {
        var r = ProcessRunner.PowerShell(
            "Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -ExpandProperty Name");
        if (!r.Ok) return Array.Empty<string>();
        return r.StdOut.Split('\n', '\r')
                       .Select(s => s.Trim())
                       .Where(s => s.Length > 0)
                       .ToArray();
    }

    private string? AdapterDisable()
    {
        var up = AdaptersUp().ToList();
        if (up.Count == 0)
        {
            // Nothing currently up — maybe already disabled by us, or genuinely offline.
            return _settings.DisabledAdapters.Count > 0 ? null : "No active network adapters found to disable.";
        }

        var errors = new List<string>();
        foreach (var name in up)
        {
            var safe = name.Replace("'", "''");
            var r = ProcessRunner.PowerShell($"Disable-NetAdapter -Name '{safe}' -Confirm:$false");
            if (r.Ok)
            {
                if (!_settings.DisabledAdapters.Contains(name))
                    _settings.DisabledAdapters.Add(name);
            }
            else
            {
                errors.Add(name + ": " + r.Output.Trim());
            }
        }
        _settings.Save();
        return errors.Count == 0 ? null : "Some adapters could not be disabled:\n" + string.Join("\n", errors);
    }

    private string? AdapterEnable()
    {
        // Re-enable exactly the adapters we disabled.
        var toEnable = _settings.DisabledAdapters.Count > 0
            ? _settings.DisabledAdapters.ToList()
            : DisabledAdapterNames().ToList();

        var errors = new List<string>();
        foreach (var name in toEnable)
        {
            var safe = name.Replace("'", "''");
            var r = ProcessRunner.PowerShell($"Enable-NetAdapter -Name '{safe}' -Confirm:$false");
            if (!r.Ok) errors.Add(name + ": " + r.Output.Trim());
        }
        _settings.DisabledAdapters.Clear();
        _settings.Save();
        return errors.Count == 0 ? null : "Some adapters could not be re-enabled:\n" + string.Join("\n", errors);
    }

    private static IEnumerable<string> DisabledAdapterNames()
    {
        var r = ProcessRunner.PowerShell(
            "Get-NetAdapter | Where-Object { $_.Status -eq 'Disabled' } | Select-Object -ExpandProperty Name");
        if (!r.Ok) return Array.Empty<string>();
        return r.StdOut.Split('\n', '\r').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    }

    // ---------------- Per-application rules ----------------

    private static string Hash8(string path)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())), 0, 4);

    /// <summary>Deterministic firewall rule name for an exe path (stable across runs).</summary>
    public static string AppRuleName(string path) => "KillSwitch_App_" + Hash8(path);

    public static string AllowRuleName(string path) => "KillSwitch_Allow_" + Hash8(path);

    public static bool IsAppBlocked(string path)
    {
        string rn = AppRuleName(path);
        var r = ProcessRunner.Netsh($"advfirewall firewall show rule name=\"{rn}\"");
        return r.Ok && r.StdOut.IndexOf(rn, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Block a specific program's outbound traffic. Returns null on success or an error.</summary>
    public static string? BlockApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No executable path for this app.";
        string rn = AppRuleName(path);
        if (IsAppBlocked(path)) return null;
        var r = ProcessRunner.Netsh(
            $"advfirewall firewall add rule name=\"{rn}\" dir=out action=block program=\"{path}\" enable=yes profile=any");
        return r.Ok ? null : "Failed to block app:\n" + r.Output;
    }

    public static string? UnblockApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string rn = AppRuleName(path);
        ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{rn}\"");
        return IsAppBlocked(path) ? "Block rule still present after delete." : null;
    }

    // ---------------- Allowlist (default-deny) mode ----------------

    public static bool IsAppAllowed(string path)
    {
        string rn = AllowRuleName(path);
        var r = ProcessRunner.Netsh($"advfirewall firewall show rule name=\"{rn}\"");
        return r.Ok && r.StdOut.IndexOf(rn, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string? AllowApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No executable path.";
        if (IsAppAllowed(path)) return null;
        string rn = AllowRuleName(path);
        var r = ProcessRunner.Netsh(
            $"advfirewall firewall add rule name=\"{rn}\" dir=out action=allow program=\"{path}\" enable=yes profile=any");
        return r.Ok ? null : r.Output;
    }

    public static void DisallowApp(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{AllowRuleName(path)}\"");
    }

    // ---------------- Block by destination IP ----------------

    private static string IpRuleName(string ip) => "KillSwitch_IP_" + Hash8(ip);

    public static bool IsIpBlocked(string ip)
    {
        string rn = IpRuleName(ip);
        var r = ProcessRunner.Netsh($"advfirewall firewall show rule name=\"{rn}\"");
        return r.Ok && r.StdOut.IndexOf(rn, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Block all traffic to/from a remote IP address (works for ownerless/unattributed traffic).</summary>
    public static string? BlockIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "No IP.";
        if (IsIpBlocked(ip)) return null;
        string rn = IpRuleName(ip);
        var a = ProcessRunner.Netsh($"advfirewall firewall add rule name=\"{rn}\" dir=out action=block remoteip={ip} enable=yes profile=any");
        var b = ProcessRunner.Netsh($"advfirewall firewall add rule name=\"{rn}_in\" dir=in action=block remoteip={ip} enable=yes profile=any");
        return a.Ok ? null : "Failed to block IP:\n" + a.Output;
    }

    public static void UnblockIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        string rn = IpRuleName(ip);
        ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{rn}\"");
        ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{rn}_in\"");
    }

    /// <summary>Switch the firewall to default-deny outbound + add baseline allows + per-app allows.</summary>
    public string? EnterAllowlist()
    {
        var r = ProcessRunner.Netsh("advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound");
        if (!r.Ok) return "Failed to set default-deny policy:\n" + r.Output;

        // Baseline allows so approved apps can actually function: DNS + DHCP.
        AddBaselineAllows();
        foreach (var a in _settings.AllowedApps) AllowApp(a.Path);
        // Safe apps stay reachable in allowlist mode too.
        foreach (var s in _settings.SafeApps) AllowApp(s.Path);
        return null;
    }

    /// <summary>Restore default outbound = allow and remove allowlist rules.</summary>
    public string? ExitAllowlist()
    {
        ProcessRunner.Netsh("advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound");
        RemoveBaselineAllows();
        foreach (var a in _settings.AllowedApps) DisallowApp(a.Path);
        return null;
    }
}
