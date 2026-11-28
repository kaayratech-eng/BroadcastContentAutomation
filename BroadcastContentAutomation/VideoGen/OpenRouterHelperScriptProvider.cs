// OpenRouter script provider via the vault-backed helper (set up
// 2026-11-23, script-pool expansion).
using GiggleGarden.Shared;

// Same shape as GroqHelperScriptProvider - the prompt travels as one argv
// element (no shell) to the openrouter_chat.py `chat` command, which
// posts to OpenRouter's OpenAI-compatible endpoint (openrouter.ai/api/v1)
// with model openrouter/free (the router picks a free model that fits the
// request, so a rotated-out model does not break the leg) through the
// custom.openrouter connection's authd surrogate exchange. No key value
// is handled here. --max-tokens 8192: a 20-24 scene act needs up to
// ~4,000 output tokens, and the headroom covers reasoning-heavy replies.
// KEY-BLOCKED until Rishi adds an OpenRouter API key (see
// custom.openrouter/CONNECTOR.md): with no connection registered the
// helper fails fast (~0.2s surrogate error) and the pool falls through
// to the next leg. Selected via GenConfig.ScriptProvider =
// "openrouter-helper", or as the fifth leg of "pool-helper" (see
// ScriptProviderFactory / FallbackScriptProvider).
class OpenRouterHelperScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt, PrepIdentity? context = null)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.OpenRouterHelperPath, "chat",
            "--prompt", prompt,
            "--model", "openrouter/free",
            "--max-tokens", "8192",
        ], timeoutMinutes: 10, what: $"OpenRouter helper ({cfg.OpenRouterHelperPath})", context: context);
        if (result.ExitCode != 0)
        {
            // L63: one logged non-retry — a transient helper crash is one
            // failure, not a retried one; the pool falls through to the
            // next leg.
            Console.WriteLine($"  {PrepContext.PrefixFor(context)}[openrouter] helper failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 300)} — no retry at this leg, falling through to next leg");
            throw new Exception($"OpenRouter helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");
        }

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        // L3: an exit-0 helper with empty/garbage stdout used to return ""
        // (or a refusal) as a script, and the error only surfaced later as
        // a generic parse failure with the leg name lost. Fail here, with
        // the leg named, so the pool falls through to the next leg.
        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("OpenRouter helper returned empty stdout (0 chars) — falling through to next leg");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new Exception($"OpenRouter helper returned no JSON object (output had no {{...}} block, {text.Length} chars) — falling through to next leg");
        return text[start..(end + 1)];
    }
}
