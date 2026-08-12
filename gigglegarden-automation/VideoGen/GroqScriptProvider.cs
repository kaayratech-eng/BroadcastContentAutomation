using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

// Groq's OpenAI-compatible endpoint - free tier (30 RPM / 6,000 TPM / 14,400 req/day,
// no card required), and unlike NVIDIA NIM's free tier, Groq's own terms allow real
// low-volume production use rather than prototyping-only. Selected via
// GenConfig.ScriptProvider = "groq" (see ScriptProviderFactory).
class GroqScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = cfg.GroqModel,
            messages = new[] { new { role = "user", content = prompt } },
        });

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [groq] {m}")))
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.GroqApiKey);

        var resp = await http.PostAsync("https://api.groq.com/openai/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Groq script generation failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 600)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

        // Llama-family models don't always honor "respond only with JSON, no fences"
        // as reliably as Claude, so strip fences the same way ClaudeScriptProvider does.
        var text = content.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
