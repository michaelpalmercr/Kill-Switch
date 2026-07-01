using System.Net.Http;
using System.Text.Json;

namespace KillSwitch;

/// <summary>
/// Resolves the owning org/company of an IP via public RDAP (the modern WHOIS). Used on demand — it sends
/// only the IP to a public registry (rdap.org), never your data. Results are cached for the session.
/// </summary>
public static class WhoisLookup
{
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient _http = MakeClient();

    private static HttpClient MakeClient()
    {
        HttpClient c;
        try { c = Net.CreateDirect(); } catch { c = new HttpClient(); }
        c.Timeout = TimeSpan.FromSeconds(8);
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("KillSwitch/1.0"); } catch { }
        return c;
    }

    public static bool TryGetCached(string ip, out string owner) => _cache.TryGetValue(ip, out owner!);

    public static async Task<string> ResolveAsync(string ip)
    {
        if (_cache.TryGetValue(ip, out var cached)) return cached;
        string owner = "";
        try
        {
            using var resp = await _http.GetAsync("https://rdap.org/ip/" + Uri.EscapeDataString(ip));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                owner = ExtractOwner(doc.RootElement);
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(owner)) owner = "(unknown)";
        _cache[ip] = owner;
        return owner;
    }

    private static string ExtractOwner(JsonElement root)
    {
        var fromEntities = FindVcardFn(root);
        if (!string.IsNullOrWhiteSpace(fromEntities)) return Trim(fromEntities!);
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) return Trim(n.GetString() ?? "");
        return "";
    }

    private static string Trim(string s) => s.Length > 48 ? s[..48] : s;

    private static string? FindVcardFn(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("vcardArray", out var vc)) { var fn = ReadVcardFn(vc); if (fn != null) return fn; }
        if (el.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
            foreach (var e in ents.EnumerateArray()) { var r = FindVcardFn(e); if (r != null) return r; }
        return null;
    }

    private static string? ReadVcardFn(JsonElement vcardArray)
    {
        // vcardArray = [ "vcard", [ ["fn", {}, "text", "ACME, Inc."], ... ] ]
        try
        {
            if (vcardArray.ValueKind != JsonValueKind.Array || vcardArray.GetArrayLength() < 2) return null;
            foreach (var p in vcardArray[1].EnumerateArray())
                if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 4 &&
                    p[0].ValueKind == JsonValueKind.String && p[0].GetString() == "fn" &&
                    p[3].ValueKind == JsonValueKind.String)
                    return p[3].GetString();
        }
        catch { }
        return null;
    }
}
