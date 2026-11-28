// Gemini script provider via the vault-backed helper (set up 2026-11-11).
using GiggleGarden.Shared;
//
// Same generateContent request as GeminiScriptProvider (JSON mode, low
// thinking, 12000 output tokens), but the API key comes from Muse's secure
// vault (custom.gemini) through the gemini_image.py helper's authd
// surrogate exchange instead of from GenConfig.GeminiApiKey. No key value
// is handled here; the prompt travels as one argv element (no shell), and
// the model name comes from cfg.GeminiModel. Selected via
// GenConfig.ScriptProvider = "gemini-helper" (see ScriptProviderFactory).
// Use this whenever the Gemini key lives in the vault and not in config.
class GeminiHelperScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt, PrepIdentity? context = null)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.GeminiHelperPath, "text",
            "--prompt", prompt,
            "--model", cfg.GeminiModel,
            "--max-tokens", "12000",
        ], timeoutMinutes: 10, what: $"Gemini helper ({cfg.GeminiHelperPath})", context: context);
        if (result.ExitCode != 0)
        {
            // L63: one logged non-retry — a transient helper crash is one
            // failure, not a retried one; the pool falls through to the
            // next leg.
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[gemini] helper failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 300)} — no retry at this leg, falling through to next leg");
            throw new Exception($"Gemini helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");
        }

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        // L3: an exit-0 helper with empty/garbage stdout used to return ""
        // (or a refusal) as a script, and the error only surfaced later as
        // a generic parse failure with the leg name lost. Fail here, with
        // the leg named, so the pool falls through to the next leg.
        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("Gemini helper returned empty stdout (0 chars) — falling through to next leg");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new Exception($"Gemini helper returned no JSON object (output had no {{...}} block, {text.Length} chars) — falling through to next leg");
        return text[start..(end + 1)];
    }
}
