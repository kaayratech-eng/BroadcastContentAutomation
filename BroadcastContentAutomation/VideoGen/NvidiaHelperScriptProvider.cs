// NVIDIA NIM script provider via the vault-backed helper (set up
// 2026-11-25, script-pool expansion).
using GiggleGarden.Shared;

// Same shape as GroqHelperScriptProvider - the prompt travels as one argv
// element (no shell) to the nvidia_chat.py `chat` command, which posts
// to NVIDIA NIM's OpenAI-compatible endpoint
// (integrate.api.nvidia.com/v1) with model nvidia/nemotron-3-ultra-550b-a55b
// (the 550B flagship, also the best :free model on OpenRouter) through
// the custom.nvidia connection's authd surrogate exchange. No key value
// is handled here. --max-tokens 8192: a 20-24 scene act needs up to
// ~4,000 output tokens, and the headroom covers reasoning-heavy replies.
// Trial credits (about 1,000 on signup, 90-day evaluation window,
// prototyping terms only) at ~40 requests/min - a burst leg, not an
// endless one. KEY-BLOCKED until Rishi adds an NVIDIA API key (see
// custom.nvidia/CONNECTOR.md): with no connection registered the helper
// fails fast (~0.2s surrogate error) and the pool falls through to the
// next leg. Selected via GenConfig.ScriptProvider = "nvidia-helper", or
// as the sixth leg of "pool-helper" (see ScriptProviderFactory /
// FallbackScriptProvider).
class NvidiaHelperScriptProvider(GenConfig cfg) : IScriptProvider
{
    public async Task<string> CompleteAsync(string prompt)
    {
        var result = await ProcessHelper.RunAsync("python3",
        [
            cfg.NvidiaHelperPath, "chat",
            "--prompt", prompt,
            "--model", "nvidia/nemotron-3-ultra-550b-a55b",
            "--max-tokens", "8192",
        ], timeoutMinutes: 10);
        if (result.ExitCode != 0)
            throw new Exception($"NVIDIA NIM helper script generation failed (exit {result.ExitCode}): {Text.Tail(result.Stderr + result.Stdout, 600)}");

        var text = result.Stdout.Replace("```json", "").Replace("```", "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
