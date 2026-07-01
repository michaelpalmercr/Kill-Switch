using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KillSwitch;

/// <summary>
/// Google Gemini provider via the Generative Language API (Google AI Studio key).
/// Uses Google Search grounding; auto-discovers a working model if the configured one is retired.
/// </summary>
public static class GeminiClient
{
    // Direct client that bypasses the MITM/system proxy (see Net.CreateDirect).
    private static readonly HttpClient Http = Net.CreateDirect();
    private static string? _resolved;   // cached working model for this session

    public static async Task<string> AnalyzeAsync(string apiKey, string model, string prompt)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "No Gemini API key set.";
        model = _resolved ?? (string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model);

        var (ok, text) = await AnalyzeOnce(apiKey, model, prompt);
        if (ok) { _resolved = model; return text; }

        // Model retired / not found → discover an available one and retry once.
        if (LooksLikeBadModel(text))
        {
            var discovered = await DiscoverModelAsync(apiKey);
            if (discovered != null && !discovered.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                var (ok2, text2) = await AnalyzeOnce(apiKey, discovered, prompt);
                if (ok2) { _resolved = discovered; return text2; }
                return text2;
            }
        }
        return text;
    }

    private static bool LooksLikeBadModel(string err) =>
        err.Contains("404") || err.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase)
        || err.Contains("no longer available", StringComparison.OrdinalIgnoreCase)
        || err.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || err.Contains("is not supported", StringComparison.OrdinalIgnoreCase);

    private static async Task<(bool ok, string text)> AnalyzeOnce(string apiKey, string model, string prompt)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var (ok, text) = await PostAsync(url, BuildBody(prompt, grounding: true));
        if (!ok && text.Contains("google_search", StringComparison.OrdinalIgnoreCase))
            (ok, text) = await PostAsync(url, BuildBody(prompt, grounding: false)); // some models reject the tool
        return (ok, text);
    }

    /// <summary>List the key's models and pick the best generateContent-capable one (prefer flash).</summary>
    private static async Task<string?> DiscoverModelAsync(string apiKey)
    {
        try
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(apiKey)}&pageSize=200";
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("models", out var models)) return null;

            var usable = new List<string>();
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var nameEl)) continue;
                string name = nameEl.GetString() ?? "";
                if (!name.StartsWith("models/gemini", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("embedding") || name.Contains("aqa")) continue;
                bool genContent = m.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                if (genContent) usable.Add(name["models/".Length..]);
            }
            // Prefer a stable flash model, then any flash, then anything.
            return usable.FirstOrDefault(n => n.Contains("flash") && !n.Contains("preview") && !n.Contains("exp"))
                ?? usable.FirstOrDefault(n => n.Contains("flash"))
                ?? usable.FirstOrDefault();
        }
        catch { return null; }
    }

    private static string BuildBody(string prompt, bool grounding)
    {
        object body = grounding
            ? new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                tools = new[] { new { google_search = new { } } },
            }
            : new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
        return JsonSerializer.Serialize(body);
    }

    private static async Task<(bool ok, string text)> PostAsync(string url, string json)
    {
        try
        {
            using var resp = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            string raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"Gemini error {(int)resp.StatusCode}: {Trim(raw)}");

            using var doc = JsonDocument.Parse(raw);
            var sb = new StringBuilder();
            if (doc.RootElement.TryGetProperty("candidates", out var cands))
                foreach (var c in cands.EnumerateArray())
                    if (c.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                        foreach (var p in parts.EnumerateArray())
                            if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());

            var outp = sb.ToString().Trim();
            return (true, outp.Length > 0 ? outp : "(Gemini returned no text.)");
        }
        catch (Exception ex)
        {
            return (false, "Gemini request failed: " + ex.Message);
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
