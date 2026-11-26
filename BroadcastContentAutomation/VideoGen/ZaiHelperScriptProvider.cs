// Z.ai script provider via the vault-backed helper (set up 2026-11-23,
// script-pool expansion).
using GiggleGarden.Shared;

// Same shape as GroqHelperScriptProvider - the prompt travels as one argv
// element (no shell) to the zai_chat.py `chat` command, which posts to
// Z.ai's OpenAI-compatible endpoint (api.z.ai/api/paas/v4) with model
// glm-4.7-flash ($0 in/out, 200K context, ~128K max output) through the
// custom.zai connection's authd surrogate exchange. No key value is
// handled here. --max-tokens 8192: a 20-24 scene act needs up to ~4,000
// output tokens, and the headroom covers reasoning-heavy replies.
// KEY-BLOCKED until Rishi adds a Z.ai Standard API key (see
// custom.zai/CONNECTOR.md): with no connection registered the helper
// fails fast (~0.2s surrogate error) and the pool falls through to the
// next leg. Selected via GenConfig.ScriptProvider = "zai-helper", or as
// the third leg of "pool-helper" (see ScriptProviderFactory /
// FallbackScriptProvider).
class ZaiHelperScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.ZaiHelperPath, "chat",
            "--prompt", prompt,
            "--model", "glm-4.7-flash",
            "--max-tokens", "8192",
        ], timeoutMinutes: 10);
        if (result.ExitCode != 0)
            throw new Exception($"Z.ai helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
