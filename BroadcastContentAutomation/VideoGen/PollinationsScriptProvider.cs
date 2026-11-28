// Pollinations text provider - anonymous, free, no key, no config.
// POSTs the prompt to text.pollinations.ai/openai (the documented
// OpenAI-compatible chat endpoint) with a browser User-Agent, which the
// endpoint requires - a bare/default agent gets blocked. Verified live
// 2026-11-22: /openai returns a chat-completion wrapper
// (choices[0].message.content) in ~4-8s; the undocumented root endpoint
// (/) returns the model's raw text instead, so the unwrap below accepts
// both shapes, preferring the wrapper. POST is used instead of GET
// because the script prompts run 4,000-6,000 chars and would push a GET
// URL past safe lengths. Same fence-strip + {...} extraction as the other
// script providers. Selected via GenConfig.ScriptProvider = "pollinations",
// or as the last leg of "pool-helper" (see ScriptProviderFactory /
// FallbackScriptProvider).
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

class PollinationsScriptProvider : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt, PrepIdentity? context = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "openai",
            // Constrain the model to a single JSON object: gpt-oss-20b
            // otherwise emits malformed JSON on the long act prompts
            // (seen 2026-11-22: act 2 failed 3x at $.scenes[8]).
            // Verified live: the /openai endpoint honours this field.
            ["response_format"] = new { type = "json_object" },
            ["messages"] = new[]
            {
                new { role = "system", content = "Reply with JSON only, no markdown fences." },
                new { role = "user", content = prompt },
            },
        };
        var body = JsonSerializer.Serialize(payload);

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  {PrepContext.PrefixFor(context)}[pollinations] {m}")))
        {
            Timeout = TimeSpan.FromSeconds(180),
        };
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

        var resp = await http.PostAsync("https://text.pollinations.ai/openai",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Pollinations script generation failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 600)}");

        // /openai returns an OpenAI chat-completion wrapper; the root
        // endpoint used to return the model's raw text. Accept both,
        // preferring the wrapper, so a shape change on either side does
        // not silently break the leg.
        var content = await resp.Content.ReadAsStringAsync();
        string text;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var msgContent)
                && msgContent.ValueKind == JsonValueKind.String)
            {
                text = msgContent.GetString()!;
            }
            else
            {
                text = content;
            }
        }
        catch (JsonException)
        {
            text = content;
        }

        text = text.Replace("```json", "").Replace("```", "").Trim();
        // L3: like Kilo/OVH, an empty response is a hard failure, not an
        // empty script — the pool must fall through to the next leg.
        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("Pollinations returned no content (empty response) - try the next provider");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
