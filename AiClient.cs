using System.Text;
using Anthropic;
using Anthropic.Models.Messages;

namespace KillSwitch;

/// <summary>
/// Thin wrapper over the Anthropic SDK for the opt-in "Analyze app" feature.
/// Only ever called when the user has set an API key and explicitly clicks Analyze.
/// </summary>
public sealed class AiClient
{
    private readonly AnthropicClient _client;
    private readonly string _model;

    public AiClient(string apiKey, string model)
    {
        // Bypass the MITM/system proxy for our own request (see Net.CreateDirect).
        _client = new AnthropicClient { ApiKey = apiKey, HttpClient = Net.CreateDirect() };
        _model = string.IsNullOrWhiteSpace(model) ? "claude-opus-4-8" : model;
    }

    public async Task<string> AnalyzeAsync(string prompt, CancellationToken ct = default)
    {
        try
        {
            var resp = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 2000,
                // Web search lets Claude verify unfamiliar vendors / executables / IP owners.
                Tools = [new ToolUnion(new WebSearchTool20260209())],
                Messages = [new() { Role = Role.User, Content = prompt }],
            });

            var sb = new StringBuilder();
            foreach (var t in resp.Content.Select(b => b.Value).OfType<TextBlock>())
                sb.Append(t.Text);

            var text = sb.ToString().Trim();
            return text.Length > 0 ? text : "(Claude returned no text — try again.)";
        }
        catch (Exception ex)
        {
            return "AI analysis failed: " + ex.Message
                + "\n\n(If the internet is cut/blocked, restore it or mark KillSwitch.exe safe first. "
                + "If it's an auth error, check your API key under \"Set API key\".)";
        }
    }
}
