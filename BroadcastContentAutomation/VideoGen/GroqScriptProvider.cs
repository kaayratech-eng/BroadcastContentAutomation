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
        var payload = new Dictionary<string, object?>
        {
            ["model"] = cfg.GroqModel,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            // Groq's free tier evaluates its 8000 TPM cap against the REQUESTED total (prompt
            // + this), not actual usage, and the prompt itself varies a lot in size (Act 1's
            // full rules/schema prompt runs ~3-4x bigger than a continuation act's). Size the
            // completion budget off the actual prompt rather than a flat number, so a big
            // prompt doesn't push the requested total over the ceiling. ~4 chars/token is a
            // rough but standard English estimate; the 1500-token floor and 500-token safety
            // margin leave room for that estimate to run a little low.
            ["max_completion_tokens"] = Math.Max(1500, 7500 - prompt.Length / 4),
        };

        // gpt-oss models are reasoning models: hidden chain-of-thought tokens count against
        // max_completion_tokens too, so on "high" effort a long response can exhaust the
        // budget on reasoning before it ever writes the JSON. Force low effort and keep the
        // reasoning trace out of the content field entirely.
        if (cfg.GroqModel.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_effort"] = "low";
            payload["include_reasoning"] = false;
        }

        var body = JsonSerializer.Serialize(payload);

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

        // Open-weight models don't always honor "respond only with JSON, no fences" as
        // reliably as Claude, so strip fences the same way ClaudeScriptProvider does.
        var text = content.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
