// Kilo Gateway script provider - anonymous, free, no key, no config.
// POSTs the prompt to api.kilo.ai/api/gateway/chat/completions (the
// OpenAI-compatible chat endpoint - note the /api/gateway path, missing
// it gives 404 on every call) with a browser User-Agent. Model is
// openrouter/free: the router picks a free model that fits the request.
// Verified live 2026-11-23: the router returned valid JSON with 22
// scenes in 38.6s on a SHORT act-shaped prompt (3,384 chars). (Pinning
// nvidia/nemotron-3-ultra-550b-a55b:free directly timed out at 180s the
// same day - the 550B model is slow/saturated on the free tier, so the
// router is the right call, not a pinned model.)
// REASONING TRAP (found 2026-11-23, real-length act-1 prompt, 13,111
// chars): the router picked nvidia/nemotron-3.5-lightning:free, which
// spent ALL 8,192 completion tokens on reasoning (reasoning_tokens =
// 8192) and returned message.content = null with finish_reason "length"
// - the 62K-char raw body was the thinking trace. The provider then
// fell back to the raw body and false-parsed a 65K span into a 0-scene
// script (dry-run FATAL). Two guards, both verified live the same day:
// (1) the payload disables reasoning ("reasoning": {"enabled": false})
// - the router then picked nemotron-3.5-super-vl:free, reasoning_tokens
// = 0, valid JSON with 12 scenes in 15.0s; (2) a null/empty content is
// a hard failure, NEVER a fall back to the raw body - the raw body in
// that case is a reasoning dump, not a script. Same fence-strip +
// {...} extraction as the other script providers, and the same
// choices[0].message.content unwrap as PollinationsScriptProvider.
// Selected via GenConfig.ScriptProvider = "kilo", or as the sixth leg of
// "pool-helper" (see ScriptProviderFactory / FallbackScriptProvider).
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;

class KiloGatewayScriptProvider : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "openrouter/free",
            // Constrain the model to a single JSON object, same as the
            // Pollinations leg: without it the model can emit malformed
            // JSON on the long act prompts.
            ["response_format"] = new { type = "json_object" },
            // Disable reasoning (see the header comment): the router's
            // free pool includes reasoning models that can burn the whole
            // max_tokens budget on thinking and return null content.
            // Verified 2026-11-23: with this set, reasoning_tokens = 0
            // and the leg returned 12 valid scenes in 15s on a real
            // 13,111-char act-1 prompt.
            ["reasoning"] = new { enabled = false },
            ["max_tokens"] = 8192,
            ["messages"] = new[]
            {
                new { role = "system", content = "Reply with JSON only, no markdown fences." },
                new { role = "user", content = prompt },
            },
        };
        var body = JsonSerializer.Serialize(payload);

        using var http = new HttpClient(new RetryHandler(log: m => Console.WriteLine($"  [kilo] {m}")))
        {
            Timeout = TimeSpan.FromSeconds(180),
        };
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

        var resp = await http.PostAsync("https://api.kilo.ai/api/gateway/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Kilo Gateway script generation failed ({(int)resp.StatusCode}): {Text.Tail(await resp.Content.ReadAsStringAsync(), 600)}");

        // The gateway returns an OpenAI chat-completion wrapper; accept
        // the raw body too, preferring the wrapper, so a shape change on
        // either side does not silently break the leg. BUT a wrapper that
        // parsed with null/empty content is a HARD failure, not a fall
        // back to the raw body: in that case the raw body is the model's
        // reasoning trace (verified 2026-11-23: 62K chars of thinking,
        // content null), and falling back false-parses it into a 0-scene
        // script. Fail fast so the pool moves to the next leg.
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
                throw new Exception("Kilo Gateway returned no content (the routed model spent its token budget on reasoning) - try the next provider");
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
