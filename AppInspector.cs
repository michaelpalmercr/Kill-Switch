using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KillSwitch;

/// <summary>Gathers local facts about an app (publisher, file metadata, destinations) for AI analysis. No network.</summary>
public static class AppInspector
{
    public static string BuildPrompt(AppUsage app)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a privacy & security analyst. A user is auditing which Windows apps should be allowed internet access, to protect their data. Help them decide about ONE app.");
        sb.AppendLine();
        sb.AppendLine("Facts gathered locally on the user's machine:");
        sb.AppendLine($"- Process name: {app.Name}");
        sb.AppendLine($"- PID: {app.Pid}");

        string path = app.ExePath ?? "";
        if (path == "System")
        {
            sb.AppendLine("- This is the Windows kernel 'System' process (PID 4).");
        }
        else if (!string.IsNullOrEmpty(path))
        {
            sb.AppendLine($"- Executable path: {path}");
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(fvi.CompanyName)) sb.AppendLine($"- Company (file metadata): {fvi.CompanyName}");
                if (!string.IsNullOrWhiteSpace(fvi.ProductName)) sb.AppendLine($"- Product: {fvi.ProductName}");
                if (!string.IsNullOrWhiteSpace(fvi.FileDescription)) sb.AppendLine($"- File description: {fvi.FileDescription}");
                if (!string.IsNullOrWhiteSpace(fvi.FileVersion)) sb.AppendLine($"- Version: {fvi.FileVersion}");
            }
            catch { }
            try
            {
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                sb.AppendLine($"- Digital signature (publisher): {cert.Subject}");
            }
            catch { sb.AppendLine("- Digital signature: NONE / unsigned (treat with extra caution)."); }
        }

        sb.AppendLine($"- Live traffic: down {Fmt.Rate(app.RateIn)}, up {Fmt.Rate(app.RateOut)}; totals down {Fmt.Bytes(app.BytesIn)}, up {Fmt.Bytes(app.BytesOut)}; {app.Connections} active connection(s).");
        if (app.Domains is { Count: > 0 })
            sb.AppendLine($"- Domains/hosts seen (DNS / TLS SNI / HTTP): {string.Join(", ", app.Domains.Take(15))}");
        if (app.Remotes is { Count: > 0 })
            sb.AppendLine($"- Talking to {app.Remotes.Count} remote IP(s), e.g.: {string.Join(", ", app.Remotes.Take(12))}");

        sb.AppendLine();
        sb.AppendLine("Use web search to verify the vendor and what this executable is if you're not certain (look up the company, product name, and IP owners if useful). Don't guess.");
        sb.AppendLine();
        sb.AppendLine("Then answer concisely (≈200 words) in these sections:");
        sb.AppendLine("• What it is & who makes it");
        sb.AppendLine("• Why it talks to the internet");
        sb.AppendLine("• Does it NEED internet to work, or is it safe to block?");
        sb.AppendLine("• Privacy / telemetry concerns");
        sb.AppendLine("• RECOMMENDATION: one of ALLOW / SAFE-TO-BLOCK / BLOCK-FOR-PRIVACY, with a one-line reason.");
        return sb.ToString();
    }

    /// <summary>Full report on a remote IP endpoint the machine is talking to.</summary>
    public static string BuildIpPrompt(string ip, string? host)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a network security & privacy analyst. The user's machine was seen communicating with this remote endpoint and wants a full report so they can decide whether to block it.");
        sb.AppendLine();
        sb.AppendLine($"- Remote IP: {ip}");
        if (!string.IsNullOrEmpty(host)) sb.AppendLine($"- Hostname (from DNS/TLS SNI): {host}");
        sb.AppendLine();
        sb.AppendLine("Use web search to identify the owner and purpose. Look up IP ownership (org / ASN / hosting provider), reverse DNS, and whether the IP/host is a known CDN, ad network, analytics/telemetry endpoint, cloud API, or a flagged/suspicious host. Don't guess — search.");
        sb.AppendLine();
        sb.AppendLine("Answer concisely (≈200 words) in these sections:");
        sb.AppendLine("• What it is & who owns it (company / hosting / ASN)");
        sb.AppendLine("• What service it provides / why an app would contact it");
        sb.AppendLine("• What data is likely sent here (tracking, telemetry, content, sync, ads)");
        sb.AppendLine("• Reputation: known-good / CDN / ad-tracker / analytics / suspicious / unknown");
        sb.AppendLine("• RECOMMENDATION: SAFE / BLOCK-FOR-PRIVACY / BLOCK-SUSPICIOUS, one-line reason.");
        return sb.ToString();
    }

    /// <summary>Full report on an installed program (from the uninstall registry).</summary>
    public static string BuildInstalledPrompt(InstalledProgram p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Windows software & security analyst. A user is auditing installed programs and considering removing this one. Help them understand it and whether it's safe to remove.");
        sb.AppendLine();
        sb.AppendLine("Facts read locally from the Windows uninstall registry:");
        sb.AppendLine($"- Name: {p.Name}");
        if (!string.IsNullOrWhiteSpace(p.Publisher)) sb.AppendLine($"- Publisher: {p.Publisher}");
        if (!string.IsNullOrWhiteSpace(p.Version)) sb.AppendLine($"- Version: {p.Version}");
        if (!string.IsNullOrWhiteSpace(p.InstallLocation)) sb.AppendLine($"- Install location: {p.InstallLocation}");
        if (p.SizeKB > 0) sb.AppendLine($"- Approx. size: {Fmt.Bytes(p.SizeKB * 1024L)}");
        if (p.SystemComponent) sb.AppendLine("- Flagged as a SYSTEM COMPONENT (often a runtime/redistributable other software depends on).");
        sb.AppendLine();
        sb.AppendLine("Use web search to verify the vendor and product if unsure. Don't guess.");
        sb.AppendLine();
        sb.AppendLine("Answer concisely (≈200 words) in these sections:");
        sb.AppendLine("• What it is & who makes it");
        sb.AppendLine("• What it's for / do users typically need it");
        sb.AppendLine("• Is it a dependency other software relies on (e.g. a VC++ runtime, .NET, driver)?");
        sb.AppendLine("• Risks of removing it");
        sb.AppendLine("• RECOMMENDATION: SAFE-TO-REMOVE / KEEP / REMOVE-WITH-CAUTION, one-line reason.");
        return sb.ToString();
    }

    /// <summary>Report on the impact of removing a specific file/folder, given a local dependency scan.</summary>
    public static string BuildImpactPrompt(string target, ImpactScanner.Result scan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Windows systems expert. The user wants to force-remove a file/folder and needs to know what could break BEFORE doing it. Assess the blast radius.");
        sb.AppendLine();
        sb.AppendLine($"- Target to remove: {target}");
        sb.AppendLine($"- Files affected: {scan.FileCount}{(scan.Truncated ? "+ (truncated)" : "")}, total {Fmt.Bytes(scan.TotalBytes)}");
        if (scan.Processes.Count > 0)
            sb.AppendLine($"- Currently loaded by running programs: {string.Join(", ", scan.Processes.Select(p => p.Name).Distinct().Take(15))}");
        if (scan.Services.Count > 0)
            sb.AppendLine($"- Windows services whose binary lives here: {string.Join(", ", scan.Services.Take(15))}");
        if (scan.SampleFiles.Count > 0)
            sb.AppendLine($"- Sample files: {string.Join("; ", scan.SampleFiles.Take(10).Select(Path.GetFileName))}");
        bool system = target.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) || target.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase);
        if (system) sb.AppendLine("- WARNING: this path is inside the Windows/System32 area — removal may destabilize the OS.");
        sb.AppendLine();
        sb.AppendLine("Use web search to identify the target and whether it's an OS component if unsure. Don't guess.");
        sb.AppendLine();
        sb.AppendLine("Answer concisely (≈200 words) in these sections:");
        sb.AppendLine("• What this file/folder is");
        sb.AppendLine("• What programs / OS features depend on it");
        sb.AppendLine("• What will likely break if it's removed");
        sb.AppendLine("• How to recover if it goes wrong");
        sb.AppendLine("• RECOMMENDATION: SAFE-TO-REMOVE / RISKY / DO-NOT-REMOVE, one-line reason.");
        return sb.ToString();
    }

    /// <summary>Full report on a website / domain.</summary>
    public static string BuildDomainPrompt(string domain)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a privacy analyst. The user's machine contacted this domain and wants a full report on the site/service behind it.");
        sb.AppendLine();
        sb.AppendLine($"- Domain / host: {domain}");
        sb.AppendLine();
        sb.AppendLine("Use web search to identify the service and the company behind it. Don't guess — search.");
        sb.AppendLine();
        sb.AppendLine("Answer concisely (≈200 words) in these sections:");
        sb.AppendLine("• What it is & who owns/operates it");
        sb.AppendLine("• What the service does / why an app or browser contacts it");
        sb.AppendLine("• What data it collects or 'reads' from you (accounts, telemetry, tracking, fingerprinting, ads)");
        sb.AppendLine("• Privacy reputation & any known concerns");
        sb.AppendLine("• RECOMMENDATION: SAFE / LIMIT / BLOCK-FOR-PRIVACY, one-line reason.");
        return sb.ToString();
    }
}
