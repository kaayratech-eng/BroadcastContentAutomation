using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

// Gemini's generateContent REST endpoint - free tier reported (community, not a
// live official table - Google now gates exact figures behind an AI-Studio login)
// at roughly 10 RPM/250,000 TPM/250 RPD for gemini-2.5-flash and 15 RPM/250,000
// TPM/1000 RPD for gemini-2.5-flash-lite - both well above Groq's confirmed 8,000
// TPM/model and 200,000 TPD ceilings (see GroqScriptProvider). Selected via
// GenConfig.ScriptProvider = "gemini" (see ScriptProviderFactory).
class GeminiScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            // responseMimeType "application/json" makes Gemini emit raw JSON without
            // markdown fences, unlike Claude/Groq which need fence-stripping.
            // Gemini 3 models think by default and hidden reasoning tokens count
            // against maxOutputTokens (same failure mode as Groq's gpt-oss models) -
            // thinking can't be fully disabled, only minimized via thinkingLevel, so
            // pair "low" with extra output headroom rather than relying on either alone.
            generationConfig = new
            {
                maxOutputTokens = 12000,
                responseMimeType = "application/json",
                thinkingConfig = new { thinkingLevel = "low" },
            },
        };

        var body = JsonSerializer.Serialize(payload);

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [gemini] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.Add("x-goog-api-key", cfg.GeminiApiKey);

        var resp = await http.PostAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.GeminiModel}:generateContent",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Gemini script generation failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 600)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // A safety block or an exhausted maxOutputTokens on the first token both come
        // back as HTTP 200 with an empty/missing candidates array - reading
        // candidates[0] first would throw an unhelpful index error.
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            var reason = root.TryGetProperty("promptFeedback", out var fb) &&
                fb.TryGetProperty("blockReason", out var br) ? br.GetString() : "unknown";
            throw new Exception($"Gemini returned no candidates (blockReason: {reason}). Try a different topic.");
        }

        var candidate = candidates[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;
        if (finishReason is "SAFETY" or "PROHIBITED_CONTENT" or "RECITATION")
            throw new Exception($"Gemini declined this script request (finishReason: {finishReason}). Try a different topic.");

        var text = candidate.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

        // responseMimeType=application/json should mean no fences, but strip them
        // defensively the same way the other providers do.
        text = text.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
