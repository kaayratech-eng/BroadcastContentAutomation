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
    public async Task<string> CompleteAsync(string prompt)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.OpenRouterHelperPath, "chat",
            "--prompt", prompt,
            "--model", "openrouter/free",
            "--max-tokens", "8192",
        ], timeoutMinutes: 10);
        if (result.ExitCode != 0)
            throw new Exception($"OpenRouter helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
