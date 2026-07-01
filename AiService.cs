namespace KillSwitch;

/// <summary>Routes AI analysis to the selected provider (Claude or Gemini). Key stored locally per provider.</summary>
public static class AiService
{
    public static bool IsGemini(AppSettings s) => s.AiProvider == "gemini";

    public static string ProviderName(AppSettings s) => IsGemini(s) ? "Gemini" : "Claude";

    public static string ModelLabel(AppSettings s) => IsGemini(s) ? s.GeminiModel : s.AiModel;

    public static bool HasKey(AppSettings s) =>
        IsGemini(s) ? !string.IsNullOrWhiteSpace(s.GeminiApiKey) : !string.IsNullOrWhiteSpace(s.AiApiKey);

    public static Task<string> AnalyzeAsync(AppSettings s, string prompt) =>
        IsGemini(s)
            ? GeminiClient.AnalyzeAsync(s.GeminiApiKey, s.GeminiModel, prompt)
            : new AiClient(s.AiApiKey, s.AiModel).AnalyzeAsync(prompt);
}
