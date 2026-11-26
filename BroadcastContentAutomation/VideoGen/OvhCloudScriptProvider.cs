// OVHcloud AI Endpoints script provider - anonymous, free, no key, no
// config. POSTs the prompt to oai.endpoints.kepler.ai.cloud.ovh.net/v1/
// chat/completions (OpenAI-compatible) with a browser User-Agent. Model
// is gpt-oss-120b (131K context, ~32K max output; OVHcloud documents
// JSON Mode + Structured Outputs for this endpoint). Verified live
// 2026-11-23: the models list returns HTTP 200 with gpt-oss-120b present.
// The anonymous bucket is shared and often already empty - the same day's
// probes got HTTP 429 ("API rate limit exceeded") on all three attempts,
// each failing in ~1s. That is the expected behaviour for this trickle
// leg: it costs about a second when the bucket is empty and may answer
// when it has room, so it sits above Pollinations in the pool but is not
// counted in daily capacity. Same fence-strip + {...} extraction as the
// other script providers, and the same choices[0].message.content unwrap
// as PollinationsScriptProvider. Selected via GenConfig.ScriptProvider =
// "ovhcloud", or as the seventh leg of "pool-helper" (see
// ScriptProviderFactory / FallbackScriptProvider).
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

class OvhCloudScriptProvider : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "gpt-oss-120b",
            // Constrain the model to a single JSON object, same as the
            // Pollinations leg: without it the model can emit malformed
            // JSON on the long act prompts. OVHcloud documents JSON Mode
            // support for this endpoint.
            ["response_format"] = new { type = "json_object" },
            ["max_tokens"] = 8192,
            ["messages"] = new[]
            {
                new { role = "system", content = "Reply with JSON only, no markdown fences." },
                new { role = "user", content = prompt },
            },
        };
        var body = JsonSerializer.Serialize(payload);

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [ovhcloud] {m}")))
        {
            Timeout = TimeSpan.FromSeconds(180),
        };
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

        var resp = await http.PostAsync("https://oai.endpoints.kepler.ai.cloud.ovh.net/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OVHcloud script generation failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 600)}");

        // The endpoint returns an OpenAI chat-completion wrapper; accept
        // the raw body too, preferring the wrapper, so a shape change on
        // either side does not silently break the leg. BUT a wrapper that
        // parsed with null/empty content is a HARD failure, not a fall
        // back to the raw body: gpt-oss-120b is a reasoning model, and in
        // that case the raw body can be a reasoning trace, which the
        // fence-strip + {...} extraction can false-parse into a 0-scene
        // script (the exact failure Kilo hit 2026-11-23). Fail fast so
        // the pool moves to the next leg.
        var content = await resp.Content.ReadAsStringAsync();
        string text;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var msgContent)
                && msgContent.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(msgContent.GetString()))
            {
                text = msgContent.GetString()!;
            }
            else if (doc.RootElement.TryGetProperty("choices", out _))
            {
                throw new Exception("OVHcloud returned no content (the model spent its token budget on reasoning) - try the next provider");
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
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
